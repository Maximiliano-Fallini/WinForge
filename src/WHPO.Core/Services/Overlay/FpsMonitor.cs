using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services.Overlay;

/// <summary>
/// Contador de FPS por proceso leyendo eventos de presentación directamente desde
/// ETW. La ruta DXGI (DXGI_Present_Start) alimenta la métrica pública actual; la
/// ruta interna DxgKrnl correlaciona flips/completions para preparar Displayed FPS
/// sin depender de una aplicación externa. Es un mecanismo de Windows para medir
/// framerate: escuchar los presents no requiere inyectar código en el juego, así
/// que no choca con anti-cheats (EAC, BattlEye).
///
/// Cómo se calcula el FPS: por cada evento de presentación de un proceso se mide
/// el delta desde la presentación anterior (tiempo de frame). El FPS reportado es
/// la mediana de los últimos ~30 frames (robusto a outliers). El 1% low / 0.1% low
/// se calcula promediando los frames más lentos (el peor 1% / 0.1%) del buffer.
///
/// La sesión ETW corre en un hilo de fondo (TraceEventSession.Source.Process()
/// bloquea hasta detener la sesión). Todo el estado es thread-safe (concurrent
/// dictionary + volátiles); los consumidores leen desde su propio hilo.
///
/// Cada muestra guarda además el timestamp de LLEGADA (DateTime.UtcNow, reloj de
/// pared) junto al delta: es lo que permite al gráfico de latencia del overlay
/// anclar cada frame a tiempo real (eje X = hace cuánto se presentó) y drenar
/// solas las muestras viejas cuando el juego deja de presentar.
/// </summary>
public sealed class FpsMonitor : IFpsMonitor, IDisposable
{
    private readonly EtwFrameCapture _capture = new();
    private readonly DxgKrnlFrameCorrelator _kernelCorrelator = new();

    // Microsoft-Windows-DxgKrnl (provider kernel) es la base de PresentMon y
    // permite cubrir DX9, OpenGL y Vulkan. TraceEvent 3.2.5 no incluye un parser
    // DxgKrnl manifestado; los eventos gráficos deben correlacionarse por
    // PresentHistoryToken/SubmitSequence/VSync antes de usarlos para FPS.
    private static readonly Guid DxgKrnlProviderGuid = new("802ec45a-1e99-4b83-9920-87c98277ba9d");

    // Keyword "Events" del provider DXGI.
    private const ulong DxgiEventsKeyword = 0x2;
    // Keyword Present del provider DxgKrnl (0x08000000).

    private const string SessionName = "WinForgeFps";

    private readonly ILoggingService _log;
    private readonly object _startLock = new();
    private TraceEventSession? _session;
    private Thread? _consumerThread;
    private System.Threading.Timer? _flushTimer;
    private volatile bool _running;

    // Estado por proceso: anillo de frames (timestamp de llegada + ms) y último
    // present para los deltas.
    private sealed class ProcessStats
    {
        public long LastPresentTicks;   // e.TimeStamp del último present (para los deltas de frame)
        public long LastEventAtTicks;   // DateTime.UtcNow de CUANDO se recibió el evento (para el prune)
        public readonly FrameRing Frames = new();
    }

    private readonly ConcurrentDictionary<int, ProcessStats> _processes = new();

    // Anillo circular de muestras de frame: guarda (timestamp ETW del evento,
    // timestamp de llegada, ms del frame) sin desplazamientos O(n) por evento (la
    // List vieja movía hasta 900 doubles por frame a fps altos). Capacidad: 900
    // frames cubren 3.5 s hasta ~257 fps (la ventana que usa el gráfico de
    // latencia del overlay).
    private sealed class FrameRing
    {
        private const int Capacity = 900;
        private readonly long[] _etwTicks = new long[Capacity];
        private readonly long[] _wallTicks = new long[Capacity];
        private readonly double[] _ms = new double[Capacity];
        private int _head; // índice del sample más viejo
        public int Count { get; private set; }

        public void Add(long etwTicks, long wallTicks, double ms)
        {
            // Cuando está lleno, el nuevo frame reemplaza exactamente al más viejo
            // (head) y luego head avanza. En el caso no lleno, tail es el siguiente
            // hueco libre. Ambas ramas mantienen el orden cronológico.
            int index;
            if (Count == Capacity)
            {
                index = _head;
                _head = (_head + 1) % Capacity;
            }
            else
            {
                index = (_head + Count) % Capacity;
                Count++;
            }
            _etwTicks[index] = etwTicks;
            _wallTicks[index] = wallTicks;
            _ms[index] = ms;
        }

        public long EtwTickAt(int i) => _etwTicks[(_head + i) % Capacity];
        public long WallTickAt(int i) => _wallTicks[(_head + i) % Capacity];
        public double MsAt(int i) => _ms[(_head + i) % Capacity];

        /// <summary>Copia los valores de ms (para ordenar en los percentiles).</summary>
        public double[] CopyMs()
        {
            var result = new double[Count];
            for (int i = 0; i < Count; i++) result[i] = _ms[(_head + i) % Capacity];
            return result;
        }
    }

    private const int FpsSmoothingWindow = 30;

    public FpsMonitor(ILoggingService log)
    {
        _log = log;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        lock (_startLock)
        {
            if (_running) return;

            try
            {
                // Si quedó una sesión huérfana de una corrida anterior (crash), detenerla
                // antes de crear la nuestra con el mismo nombre.
                try
                {
                    var orphan = TraceEventSession.GetActiveSession(SessionName);
                    orphan?.Stop();
                    orphan?.Dispose();
                }
                catch { }

                _session = new TraceEventSession(SessionName, null);
                // La fuente productiva actual es DXGI. Se habilita además la sesión
                // kernel estándar para disponer de contexto de procesos/hilos y dejar
                // lista la telemetría ETW; DxgKrnl gráfico no se cuenta aún porque
                // TraceEvent no decodifica su manifest y un filtro heurístico falsearía
                // FPS. El parser completo se integrará en una fase posterior.
                _session.EnableProvider(EtwFrameCapture.DxgiProviderGuid, TraceEventLevel.Informational, DxgiEventsKeyword);
                try
                {
                    _session.EnableKernelProvider(
                        KernelTraceEventParser.Keywords.Process |
                        KernelTraceEventParser.Keywords.Thread);
                    _session.EnableProvider(
                        DxgKrnlFrameCorrelator.ProviderGuid,
                        TraceEventLevel.Informational,
                        0x08000000);
                    _log.LogInfo("FpsMonitor: captura ETW kernel DxgKrnl habilitada");
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"FpsMonitor: contexto ETW kernel no disponible; se mantiene DXGI: {ex.Message}");
                }
                _session.Source.AllEvents += OnEvent;

                // Flush forzado cada 100 ms: ETW entrega los eventos en ráfagas
                // (por defecto ~1 s), lo que hacía que el gráfico de frametime se
                // cortara y el valor ms parpadeara entre ráfaga y ráfaga. Con este
                // flush los eventos llegan casi en tiempo real y el gráfico
                // scrollea fluido.
                _flushTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        if (_running) _session?.Flush();
                    }
                    catch { }
                }, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));

                _running = true;
                _consumerThread = new Thread(ConsumerLoop)
                {
                    IsBackground = true,
                    Name = "WinForgeFpsETW"
                };
                _consumerThread.Start();

                _log.LogInfo("FpsMonitor: sesión ETW DXGI + DxgKrnl iniciada");
            }
            catch (Exception ex)
            {
                _running = false;
                try { _session?.Dispose(); } catch { }
                _session = null;
                _log.LogWarning($"FpsMonitor: no se pudo iniciar la sesión ETW: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        lock (_startLock)
        {
            if (!_running) return;
            _running = false;
            _flushTimer?.Dispose();
            _flushTimer = null;
            try
            {
                _session?.Stop();
                _session?.Dispose();
            }
            catch (Exception ex)
            {
                _log.LogWarning($"FpsMonitor: error deteniendo la sesión ETW: {ex.Message}");
            }
            _session = null;
            _consumerThread = null;
            _processes.Clear();
        }
    }

    private void ConsumerLoop()
    {
        try
        {
            _session?.Source.Process(); // bloquea hasta Stop()
        }
        catch (Exception ex)
        {
            if (_running)
                _log.LogWarning($"FpsMonitor: la sesión ETW terminó con error: {ex.Message}");
        }
    }

    private void OnEvent(TraceEvent e)
    {
        if (_capture.IsFrameEvent(e))
        {
            TryRecordPresent(e, e.ProcessID, e.TimeStamp);
            return;
        }

        // La ruta kernel se procesa para validar/correlacionar presentaciones, pero
        // no se mezcla todavía con la serie DXGI: su completion mide Present→display,
        // mientras que esta API pública expone delta entre frames. Mezclarlas
        // falsearía FPS; el correlador queda listo para añadir Displayed FPS separado.
        _kernelCorrelator.TryProcess(e, out _);
    }

    private void TryRecordPresent(TraceEvent e, int pid, DateTime timestamp)
    {
        if (pid <= 0) return;
        var stats = GetOrAdd(pid);

        // IMPORTANTE: el prune se basa en el tiempo de LLEGADA del evento (reloj del
        // sistema), NO en e.TimeStamp: TraceEvent mapea los timestamps de los eventos
        // con un offset frente al reloj del sistema (verificado: 3 horas en esta
        // máquina) y si el prune usara e.TimeStamp, todos los procesos se evictaban
        // en cada muestreo → el FPS parpadeaba a "--". Los deltas de frame SÍ usan
        // e.TimeStamp (ambos eventos comparten el mismo reloj → deltas precisos).
        stats.LastEventAtTicks = DateTime.UtcNow.Ticks;

        long nowTicks = timestamp.Ticks;
        long prev = Interlocked.Exchange(ref stats.LastPresentTicks, nowTicks);
        if (prev == 0) return; // primera presentación del proceso: sin delta

        double dtMs = (nowTicks - prev) / 10000.0;
        // Descartar deltas espurios: < 0.1 ms (duplicados) o > 2 s (pausas/alt-tab).
        if (dtMs < 0.1 || dtMs > 2000) return;

        // Se guardan DOS relojes: el timestamp ETW del evento (reloj QPC, uniforme
        // entre presents — el eje X correcto del gráfico) y el de LLEGADA (reloj de
        // pared — el ancla con la que el gráfico drena las muestras viejas cuando
        // el juego deja de presentar). ETW entrega los eventos en ráfagas, así que
        // los timestamps de llegada de una ráfaga son casi idénticos: usarlos como
        // eje X apiñaba los puntos y la línea se veía como barras "|".
        long etwTicks = nowTicks;
        long arrivalTicks = stats.LastEventAtTicks;
        lock (stats.Frames)
        {
            stats.Frames.Add(etwTicks, arrivalTicks, dtMs);
        }
    }

    private ProcessStats GetOrAdd(int pid)
    {
        // Los objetos se quedan en el diccionario hasta Prune(): mantener la instancia
        // evita reseteos de FPS cuando un proceso alterna entre 2 hilos de present.
        return _processes.GetOrAdd(pid, _ => new ProcessStats());
    }

    public double GetFps(int pid)
    {
        if (!_running || pid <= 0 || !_processes.TryGetValue(pid, out var stats)) return 0;
        lock (stats.Frames)
        {
            if (stats.Frames.Count == 0) return 0;
            int n = Math.Min(stats.Frames.Count, FpsSmoothingWindow);
            var recent = new double[n];
            for (int i = 0; i < n; i++) recent[i] = stats.Frames.MsAt(stats.Frames.Count - n + i);
            Array.Sort(recent);
            double median = recent[recent.Length / 2];
            return median > 0 ? 1000.0 / median : 0;
        }
    }

    public double GetLow1(int pid) => GetLowPercentile(pid, 0.01, minSamples: 50);

    public double GetLow01(int pid) => GetLowPercentile(pid, 0.001, minSamples: 100);

    /// <summary>
    /// Serie reciente de frametimes, del más viejo al más nuevo (hasta maxSamples),
    /// cada una con su timestamp de llegada (reloj de pared). Es la materia prima
    /// del gráfico de latencia del overlay: cada punto es el delta entre dos
    /// Present() consecutivos del proceso, anclado a tiempo real.
    /// </summary>
    public FrametimeSample[] GetFrametimeSeries(int pid, int maxSamples)
    {
        if (maxSamples <= 0) return Array.Empty<FrametimeSample>();
        if (!_running || pid <= 0 || !_processes.TryGetValue(pid, out var stats))
            return Array.Empty<FrametimeSample>();
        lock (stats.Frames)
        {
            if (stats.Frames.Count == 0) return Array.Empty<FrametimeSample>();
            int take = Math.Min(stats.Frames.Count, maxSamples);
            var result = new FrametimeSample[take];
            for (int i = 0; i < take; i++)
            {
                int idx = stats.Frames.Count - take + i;
                result[i] = new FrametimeSample(
                    stats.Frames.EtwTickAt(idx),
                    stats.Frames.WallTickAt(idx),
                    stats.Frames.MsAt(idx));
            }
            return result;
        }
    }

    /// <summary>
    /// FPS del peor "p" de los frames: promedio del p% de frames más lentos.
    /// Requiere al menos minSamples frames en el buffer para ser representativo.
    /// </summary>
    private double GetLowPercentile(int pid, double worstFraction, int minSamples)
    {
        if (!_running || pid <= 0 || !_processes.TryGetValue(pid, out var stats)) return 0;
        lock (stats.Frames)
        {
            if (stats.Frames.Count < minSamples) return 0;
            var sorted = stats.Frames.CopyMs();
            Array.Sort(sorted);
            int worstCount = Math.Max(1, (int)(sorted.Length * worstFraction));
            double sum = 0;
            for (int i = sorted.Length - worstCount; i < sorted.Length; i++)
                sum += sorted[i];
            double avg = sum / worstCount;
            return avg > 0 ? 1000.0 / avg : 0;
        }
    }

    /// <summary>
    /// Elimina el estado de procesos sin presentaciones recientes (10 s). Solo por
    /// tiempo, a propósito: el sondeo con Process.GetProcessById puede lanzar
    /// "Acceso denegado" sobre procesos protegidos por anti-cheat (EAC/BattlEye) y
    /// el catch evictaba el juego → el FPS parpadeaba. El timeout es generoso para
    /// aguantar huecos de eventos ETW (entregas con retraso) sin perder el juego.
    /// </summary>
    public void Prune()
    {
        long cutoff = DateTime.UtcNow.Ticks - TimeSpan.FromSeconds(10).Ticks;
        foreach (var kvp in _processes)
        {
            if (kvp.Value.LastEventAtTicks < cutoff)
                _processes.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    /// El proceso con mayor tasa de presentación actual (el "juego más activo"),
    /// excluyendo el pid indicado (la propia app). Se usa como respaldo cuando ni
    /// el primer plano ni el último juego conocido presentan: cubre el caso de un
    /// juego corriendo en background (p. ej. en otro monitor) mientras el primer
    /// plano es un emulador o el escritorio.
    /// </summary>
    public (int Pid, double Fps) GetMostActiveProcess(int excludePid)
    {
        int bestPid = 0;
        double bestFps = 0;
        foreach (var kvp in _processes)
        {
            if (kvp.Key == excludePid) continue;
            double f = GetFps(kvp.Key);
            if (f > bestFps)
            {
                bestFps = f;
                bestPid = kvp.Key;
            }
        }
        return (bestPid, bestFps);
    }

    public void Dispose()
    {
        Stop();
    }
}
