using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services.Overlay;

/// <summary>
/// Contador de FPS por proceso leyendo los eventos de presentación que el runtime
/// DXGI ya emite por ETW (provider Microsoft-Windows-DXGI, evento
/// DXGI_Present_Start). Es el mismo mecanismo que usa PresentMon/OCAT para medir
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
/// </summary>
public sealed class FpsMonitor : IFpsMonitor, IDisposable
{
    // Microsoft-Windows-DXGI (el provider user-mode del runtime DXGI): emite un
    // evento DXGI_Present_Start por cada llamada Present() de cada aplicación, con
    // el PID del proceso que presenta y nombre decodificable. Es el mismo que usa
    // PresentMon/OCAT como punto de entrada de cada frame, sin el ruido del provider
    // de kernel (DxgKrnl) ni la necesidad de mapear IDs por versión de Windows.
    private static readonly Guid DxgiProviderGuid = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");

    // Keyword "Events" del provider (verificado con `logman query providers
    // Microsoft-Windows-DXGI`): cubre los eventos DXGI_Present_*.
    private const ulong DxgiEventsKeyword = 0x2;

    private const string SessionName = "WinForgeFps";

    private readonly ILoggingService _log;
    private readonly object _startLock = new();
    private TraceEventSession? _session;
    private Thread? _consumerThread;
    private volatile bool _running;

    // Estado por proceso: último timestamp de presentación + buffer de tiempos de frame (ms).
    private sealed class ProcessStats
    {
        public long LastPresentTicks;   // e.TimeStamp del último present (para los deltas de frame)
        public long LastEventAtTicks;   // DateTime.UtcNow de CUANDO se recibió el evento (para el prune)
        public readonly List<double> FrameTimesMs = new();
    }

    private readonly ConcurrentDictionary<int, ProcessStats> _processes = new();

    // Tamaño del buffer de tiempos de frame para FPS (mediana) y para los percentiles.
    private const int FrameTimeBufferSize = 300;
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
                // Keyword "Events" + nivel Informational: el runtime DXGI emite los
                // eventos DXGI_Present_Start/Stop de cada Present() de cada proceso.
                _session.EnableProvider(DxgiProviderGuid, TraceEventLevel.Informational, DxgiEventsKeyword);
                _session.Source.AllEvents += OnEvent;

                _running = true;
                _consumerThread = new Thread(ConsumerLoop)
                {
                    IsBackground = true,
                    Name = "WinForgeFpsETW"
                };
                _consumerThread.Start();

                _log.LogInfo("FpsMonitor: sesión ETW DXGI iniciada");
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
        if (e.ProviderGuid != DxgiProviderGuid) return;
        if (!IsPresentEvent(e)) return;
        TryRecordPresent(e, e.ProcessID, e.TimeStamp);
    }

    /// <summary>
    /// Determina si un evento del provider es el inicio de una presentación de
    /// frame. DXGI_Present_Start es el evento "el proceso llamó Present()": el
    /// delta entre Present_Start consecutivos del mismo proceso es el tiempo de
    /// frame (la métrica que muestran MSI Afterburner / RTSS como framerate).
    /// </summary>
    private static bool IsPresentEvent(TraceEvent e)
    {
        var name = e.EventName;
        if (!string.IsNullOrEmpty(name))
        {
            // Manifest decodificado: Microsoft-Windows-DXGI/DXGI_Present_Start.
            if (name.EndsWith("DXGI_Present_Start", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith("Present_Start", StringComparison.OrdinalIgnoreCase)) return true;
        }
        // Sin manifest decodificable (verificado empíricamente): DXGI_Present_Start
        // es el evento 42 con opcode Start en Windows 10/11; el 43 es Present_Stop.
        return (int)e.ID == 42 && e.Opcode == TraceEventOpcode.Start;
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

        lock (stats.FrameTimesMs)
        {
            stats.FrameTimesMs.Add(dtMs);
            if (stats.FrameTimesMs.Count > FrameTimeBufferSize)
                stats.FrameTimesMs.RemoveRange(0, stats.FrameTimesMs.Count - FrameTimeBufferSize);
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
        lock (stats.FrameTimesMs)
        {
            if (stats.FrameTimesMs.Count == 0) return 0;
            int n = Math.Min(stats.FrameTimesMs.Count, FpsSmoothingWindow);
            var recent = stats.FrameTimesMs.Skip(stats.FrameTimesMs.Count - n).ToArray();
            Array.Sort(recent);
            double median = recent[recent.Length / 2];
            return median > 0 ? 1000.0 / median : 0;
        }
    }

    public double GetLow1(int pid) => GetLowPercentile(pid, 0.01, minSamples: 50);

    public double GetLow01(int pid) => GetLowPercentile(pid, 0.001, minSamples: 100);

    /// <summary>
    /// FPS del peor "p" de los frames: promedio del p% de frames más lentos.
    /// Requiere al menos minSamples frames en el buffer para ser representativo.
    /// </summary>
    private double GetLowPercentile(int pid, double worstFraction, int minSamples)
    {
        if (!_running || pid <= 0 || !_processes.TryGetValue(pid, out var stats)) return 0;
        lock (stats.FrameTimesMs)
        {
            if (stats.FrameTimesMs.Count < minSamples) return 0;
            var sorted = stats.FrameTimesMs.ToArray();
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
