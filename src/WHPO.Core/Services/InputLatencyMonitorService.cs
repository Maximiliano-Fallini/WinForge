using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>Tipo de dispositivo de entrada medido.</summary>
public enum InputDeviceKind
{
    Mouse,
    Keyboard,
    Gamepad
}

/// <summary>
/// Cómo está conectado el dispositivo, leído de su ruta de Raw Input: un HID por Bluetooth
/// cuelga de BTHENUM y no de un hub USB. La diferencia decide cómo se lee su cadencia — por
/// cable la fija el sondeo del endpoint (y ya está en el mínimo del bus), por Bluetooth la
/// fija el intervalo del enlace inalámbrico, que es lo único ajustable desde el software del
/// fabricante.
/// </summary>
public enum InputTransport
{
    Unknown,

    /// <summary>
    /// Cuelga del bus USB. OJO: NO significa "por cable". Un receptor inalámbrico de una sola
    /// función que no se identifica a sí mismo es indistinguible de un periférico por cable
    /// (Windows no publica ninguna diferencia), así que la UI lo muestra como "USB" —que es
    /// cierto— y solo dice "2,4 GHz" cuando el receptor se puede reconocer.
    /// </summary>
    Usb,

    Bluetooth,

    /// <summary>
    /// Inalámbrico por su propio receptor (dongle) en 2,4 GHz. Desde Raw Input se ve IGUAL
    /// que un USB por cable —los dos son "USB\VID_...&PID_..."—, así que se distingue con
    /// datos del propio dispositivo: se resuelve por instancia, con la evidencia que cada nodo
    /// declara (ver TransportForInstance).
    /// </summary>
    Wireless24
}

/// <summary>
/// Estadísticas de latencia (intervalo entre informes) de un dispositivo. "Latencia"
/// acá es el intervalo entre informes HID consecutivos (= 1/polling real), lo mismo
/// que miden gamepadla/controllerco: no incluye la latencia del juego ni del cable.
/// Hz = 1000/Avg (el polling real medido, no el del descriptor).
/// </summary>
public record InputLatencyStats(
    InputDeviceKind Kind,
    string Name,          // nombre visible ("VID_046D PID_C547 interfaz 00")
    string Handle,        // "VID_xxxx&PID_xxxx" para correlacionar con la grilla USB
    long DeviceId,        // handle crudo de Raw Input: clave única incluso con dos modelos iguales
    int SampleCount,      // intervalos en la ventana
    double AvgMs,
    double MinMs,
    double MaxMs,
    double P1Ms,          // 1% de los informes fue MÁS RÁPIDO que esto
    double P99Ms,         // 1% de los informes fue MÁS LENTO que esto (jitter)
    double Hz)
{
    public static InputLatencyStats Empty(InputDeviceKind kind, string name, string handle, long deviceId) =>
        new(kind, name, handle, deviceId, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Medidor de latencia de entrada por Raw Input (WM_INPUT). Registra mouse, teclado y
/// gamepads con RIDEV_INPUTSINK: recibe COPIAS de los informes aunque la ventana no
/// tenga foco y sin consumirlos (no hay modo exclusivo, el cursor y los juegos siguen
/// intactos). Para cada dispositivo (por handle de Raw Input) mide el intervalo entre
/// informes consecutivos con QueryPerformanceCounter: ese intervalo ES el período de
/// polling real — exactamente lo que mide un "polling tester" web, pero a resolución
/// de microsegundos y sin el jitter del compositor del navegador.
/// Medir por handle (no por tipo) permite dos mouses/mandos a la vez sin mezclar.
/// Hilo propio con GetMessage: cero CPU cuando no hay informes.
/// </summary>
public sealed class InputLatencyMonitorService : IDisposable
{
    private readonly ILoggingService _logging;
    private readonly ConcurrentDictionary<long, DeviceSamples> _byHandle = new();

    // Transporte ya resuelto POR DISPOSITIVO (instance id, no VID/PID): no cambia mientras el
    // dispositivo esté conectado, y la grilla lo consulta en cada refresco, así que se recuerda
    // para no repetir el recorrido del árbol PnP.
    private readonly ConcurrentDictionary<string, InputTransport> _instanceTransportCache = new();
    private readonly Thread _messageThread;
    private volatile IntPtr _hwnd;
    private volatile bool _running;
    private volatile bool _registered;

    // Últimas teclas apretadas (VKey + dispositivo), para que la UI pueda pintar la tecla en
    // pantalla. Vive SOLO en memoria y se consume en el acto: no se registra en el log, no se
    // guarda en disco y no se acumula (el texto que se escribe no se reconstruye ni se conserva).
    private readonly List<(int VKey, long DeviceId)> _recentKeys = new();
    private readonly object _keyLock = new();
    private long _keysCaptured;      // contador para diagnóstico (no guarda QUÉ tecla)
    private volatile bool _keyReadWarned;

    // Ventana de estadísticas: 6000 intervalos ≈ 6 s a 1000 Hz (48 s a 125 Hz).
    private const int WindowSize = 6000;

    public InputLatencyMonitorService(ILoggingService logging)
    {
        _logging = logging;
        _messageThread = new Thread(MessageLoop)
        {
            Name = "WinForge.InputLatencyMonitor",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal // perder informes = medir mal
        };
    }

    /// <summary>Arranca el hilo de mensajes y registra los dispositivos. Idempotente.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _messageThread.Start();
    }

    // Un dispositivo se olvida recién a los 10 minutos sin dar señales: la lista de la card
    // tiene que sobrevivir a un rato sin tocarlo (un teclado en reposo NO manda nada justamente
    // porque no lo estás usando, y hay que poder medirlo igual). DESCONECTADO es otra cosa y se
    // va al instante: eso lo decide PresentHandles(), no el reloj. Sin esa distinción, un mando
    // apagado seguía figurando en el desplegable y ofrecía un test que no puede dar nada.
    private const long ForgetAfterUs = 600_000_000;

    /// <summary>
    /// Handles de Raw Input que existen AHORA. Separa "no lo estás usando" de "lo desconectaste":
    /// un teclado en reposo sigue en la lista, un periférico apagado o desenchufado desaparece de
    /// inmediato. Si la consulta falla devuelve vacío y quien llama NO descarta nada (mejor una
    /// lista de más que una lista vacía por un error de API).
    /// </summary>
    private static HashSet<long> PresentHandles()
    {
        var present = new HashSet<long>();
        try
        {
            uint count = 0;
            uint itemSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            if (GetRawInputDeviceList(IntPtr.Zero, ref count, itemSize) == unchecked((uint)-1) || count == 0)
                return present;

            IntPtr buf = Marshal.AllocHGlobal((int)(count * itemSize));
            try
            {
                if (GetRawInputDeviceList(buf, ref count, itemSize) == unchecked((uint)-1)) return present;
                for (uint i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(IntPtr.Add(buf, (int)(i * itemSize)));
                    present.Add(item.hDevice.ToInt64());
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* sin datos de presencia se cae al criterio del reloj */ }
        return present;
    }

    /// <summary>
    /// Agrega a la lista los dispositivos de entrada PRESENTES que todavía no mandaron ningún
    /// informe, para poder elegirlos sin haberlos usado antes.
    ///
    /// Por qué hace falta: la lista se armaba solo con lo que ya había reportado (cada informe
    /// crea la entrada del dispositivo). En un periférico USB eso se compensa con el listado del
    /// bus, pero uno por BLUETOOTH no está en el bus USB —cuelga del adaptador Bluetooth—, así
    /// que su única vía es Raw Input y un mando conectado no aparecía hasta apretar un botón.
    ///
    /// Acá se enumeran los dispositivos presentes, se pregunta su colección de nivel superior y
    /// se agregan los que el medidor sabe medir (pointer, mouse, teclado, teclado numérico,
    /// joystick, game pad y multi-axis). Las colecciones de consumo o definidas por el fabricante
    /// quedan afuera: no son medibles y llenarían la lista de filas inútiles.
    ///
    /// Best effort: si alguna consulta falla queda el comportamiento anterior (el dispositivo
    /// aparece al reportar), nunca una excepción.
    /// </summary>
    private void DiscoverPresentDevices()
    {
        try
        {
            uint count = 0;
            uint itemSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            if (GetRawInputDeviceList(IntPtr.Zero, ref count, itemSize) == unchecked((uint)-1) || count == 0) return;

            IntPtr buf = Marshal.AllocHGlobal((int)(count * itemSize));
            try
            {
                if (GetRawInputDeviceList(buf, ref count, itemSize) == unchecked((uint)-1)) return;
                for (uint i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(IntPtr.Add(buf, (int)(i * itemSize)));
                    long handle = item.hDevice.ToInt64();
                    if (_byHandle.ContainsKey(handle)) continue; // ya conocido, con su actividad
                    if (item.dwType == RIM_TYPEHID)
                    {
                        if (!TryReadHidUsage(item.hDevice, out uint usagePage, out uint usage)) continue;
                        if (!IsMeasurableUsage(usagePage, usage)) continue;
                    }
                    var discovered = DescribeDevice(item.hDevice, (int)item.dwType);
                    // 0 = conectado y SIN informes: es lo que distingue "todavía no lo usaste" de
                    // "mandó algo hace diez minutos". GetKnownDevices lo conserva mientras el
                    // dispositivo exista, en lugar de olvidarlo por reloj.
                    discovered.LastArrivalUs = 0;
                    _byHandle.TryAdd(handle, discovered);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* sin enumeración queda el descubrimiento por informe */ }
    }

    /// <summary>Usages de Generic Desktop que el medidor sabe medir (los mismos que registra, más
    /// pointer). Sirve para decidir si un dispositivo PRESENTE merece una fila: los informes se
    /// siguen clasificando por tipo, esto no filtra datos.</summary>
    private static bool IsMeasurableUsage(uint usagePage, uint usage) =>
        usagePage == 0x01 && usage is 0x01 or 0x02 or 0x04 or 0x05 or 0x06 or 0x07 or 0x08;

    /// <summary>Colección de nivel superior de un HID presente (RID_DEVICE_INFO). Si no se puede
    /// leer devuelve false y el dispositivo no se agrega sin haber reportado.</summary>
    private static bool TryReadHidUsage(IntPtr hDevice, out uint usagePage, out uint usage)
    {
        usagePage = usage = 0;
        try
        {
            uint size = (uint)Marshal.SizeOf<RID_DEVICE_INFO>();
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.StructureToPtr(new RID_DEVICE_INFO { cbSize = size }, buf, false);
                if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, buf, ref size) == unchecked((uint)-1)) return false;
                var info = Marshal.PtrToStructure<RID_DEVICE_INFO>(buf);
                if (info.dwType != RIM_TYPEHID) return false;
                usagePage = info.hidUsagePage;
                usage = info.hidUsage;
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return false; }
    }

    /// <summary>
    /// Presencia de un dispositivo conocido: identidad + cuánto hace que no manda nada.
    /// Es lo que llena el desplegable de la card, y por eso incluye el "hace cuánto":
    /// un dispositivo puede estar en la lista y no dar datos, y la UI tiene que poder
    /// decir cuál es el caso.
    /// </summary>
    public record InputDevicePresence(string Handle, string Name, InputDeviceKind Kind, long DeviceId,
        double SecondsSinceLastReport, InputTransport Transport = InputTransport.Unknown);

    /// <summary>Resumen de la prueba EN CURSO: los intervalos del trazo (ms, en orden
    /// cronológico) y sus números. Todo sobre la ventana reciente, no sobre el total.</summary>
    public record InputLatencyLive(double[] Intervals, double AvgMs, double MinMs, double MaxMs, double Hz, int Samples);

    /// <summary>Dispositivos de entrada vistos por Raw Input, con o sin informes recientes,
    /// ordenados por nombre. Copia consistente para el hilo de UI.</summary>
    public List<InputDevicePresence> GetKnownDevices()
    {
        // Primero se completa la lista con los dispositivos PRESENTES que todavía no reportaron:
        // sin esto, un dispositivo aparece recién después de usarlo, y uno por Bluetooth —que no
        // tiene fila en el bus USB— podía no aparecer nunca. Ver DiscoverPresentDevices.
        DiscoverPresentDevices();

        long nowUs = NowUs();
        var present = PresentHandles();
        bool canCheckPresence = present.Count > 0;
        var result = new List<InputDevicePresence>();
        var forgotten = new List<long>();
        foreach (var kvp in _byHandle)
        {
            var dev = kvp.Value;
            // LastArrivalUs == 0: CONECTADO y sin un solo informe. El reloj mide actividad, y acá
            // no hay ninguna, así que no se olvida por tiempo: sale de la lista cuando el
            // dispositivo desaparece. Mientras tanto la UI lo ofrece para medirlo, que es lo que
            // un mando necesita (no manda nada hasta que lo tocás).
            bool neverReported = dev.LastArrivalUs == 0;
            long sinceUs = neverReported ? 0 : nowUs - dev.LastArrivalUs;
            // Desconectado (su handle ya no existe) se olvida YA, sin esperar los diez minutos:
            // no tiene sentido listarlo ni ofrecerle un test. La identidad de la card es el
            // VID/PID, así que al reconectarlo vuelve solo, con sus números.
            if ((canCheckPresence && !present.Contains(kvp.Key)) || sinceUs > ForgetAfterUs)
                forgotten.Add(kvp.Key);
            else result.Add(new InputDevicePresence(dev.Handle, dev.Name, dev.Kind, dev.DeviceId,
                neverReported ? double.PositiveInfinity : sinceUs / 1_000_000.0, dev.Transport));
        }
        foreach (var k in forgotten) _byHandle.TryRemove(k, out _);
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Estadísticas de la ventana móvil de cada dispositivo conocido, como lista propia de
    /// quien llama (puede filtrarla y ordenarla). Solo se incluyen los que ya juntaron
    /// informes: una fila sin muestras no es una medición.
    /// </summary>
    public List<InputLatencyStats> GetStats()
    {
        var list = new List<InputLatencyStats>();
        foreach (var kvp in _byHandle)
        {
            var stats = kvp.Value.Snapshot();
            if (stats.SampleCount > 0) list.Add(stats);
        }
        return list;
    }

    /// <summary>
    /// Muestra EN VIVO de una prueba en curso: el trazo de los últimos intervalos y su
    /// resumen, para dibujar mientras se mide. Null si el dispositivo todavía no mandó
    /// nada — que es distinto de "mandó cero informes": la UI tiene que poder decirlo.
    /// </summary>
    /// <param name="preferKind">
    /// Tipo del dispositivo que el usuario eligió en la lista. Una MISMA clave (VID/PID) puede
    /// tener varias entradas de Raw Input: un receptor expone una colección de mouse (MI_00) y otra
    /// de teclado (MI_01), y las dos producen el mismo Handle. Con el tipo se elige la correcta
    /// aunque la otra haya juntado más informes.
    /// </param>
    public InputLatencyLive? GetLiveSample(string deviceKey, int maxIntervals, double windowMs,
        InputDeviceKind? preferKind = null)
    {
        // Elegir la PRIMERA entrada que coincide con la clave era el bug del trazo en vivo: si la
        // lista interna ya tenía la colección de teclado del mismo receptor (que existe desde que
        // el monitoreo descubre los dispositivos presentes, sin necesidad de usarlos), esa entrada
        // coincidía primero y no tenía ni un intervalo → el trazo y los números en vivo quedaban
        // vacíos mientras la medición sí avanzaba. Se elige la mejor:
        //   1) la del tipo esperado que tenga datos (es el dispositivo que el usuario eligió),
        //   2) si ninguna del tipo esperado reportó, la que haya juntado más intervalos.
        double[] best = Array.Empty<double>();
        bool sawPreferred = false;
        foreach (var kvp in _byHandle)
        {
            if (!string.Equals(kvp.Value.Handle, deviceKey, StringComparison.OrdinalIgnoreCase)) continue;
            var trail = kvp.Value.TestTrail(maxIntervals, windowMs);
            if (trail.Length == 0) continue;
            bool preferred = preferKind is { } kind && kvp.Value.Kind == kind;
            if (preferred)
            {
                if (!sawPreferred || trail.Length > best.Length) best = trail;
                sawPreferred = true;
            }
            else if (!sawPreferred && trail.Length > best.Length)
            {
                best = trail;
            }
        }
        if (best.Length == 0) return null;

        double sum = 0, min = double.MaxValue, max = 0;
        foreach (var v in best)
        {
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        double avg = sum / best.Length;
        return new InputLatencyLive(best, avg, min, max, avg > 0 ? 1000.0 / avg : 0, best.Length);
    }

    /// <summary>Reinicia las muestras (no los registros) de todos los dispositivos.</summary>
    public void Reset()
    {
        foreach (var kvp in _byHandle)
            kvp.Value.Reset();
    }

    // ===== Captura para el TEST de latencia (ver InputLatencyTestService) =====
    // El medidor en vivo resume en un anillo de 6000 intervalos (≈6 s a 1000 Hz, pero
    // solo ~0,75 s a 8000 Hz): suficiente para mostrar números, insuficiente para
    // juzgar una ventana de 10 s a tasa alta. Durante una prueba se acumula aparte,
    // sin dar vueltas, y fuera de la prueba no cuesta nada (no se copia nada).

    private volatile bool _sampling;

    /// <summary>Arranca la captura del test (limpia lo acumulado antes).</summary>
    public void BeginSampling()
    {
        foreach (var kvp in _byHandle)
            kvp.Value.BeginSampling();
        _sampling = true; // los dispositivos que aparezcan durante la prueba también acumulan
    }

    /// <summary>
    /// Cierra la captura y devuelve, por dispositivo, su identidad y TODOS los
    /// intervalos acumulados en orden cronológico. Incluye dispositivos que dejaron de
    /// emitir durante la prueba (sus datos son parte de la respuesta).
    /// </summary>
    public List<(InputLatencyStats Stats, double[] Intervals)> EndSampling()
    {
        var result = new List<(InputLatencyStats Stats, double[] Intervals)>();
        try
        {
            foreach (var kvp in _byHandle)
            {
                var intervals = kvp.Value.EndSampling();
                if (intervals.Length > 0) result.Add((kvp.Value.Snapshot(), intervals));
            }
        }
        finally
        {
            _sampling = false;
        }
        return result;
    }

    /// <summary>
    /// Cuántos sellos lleva acumulados la captura para ese dispositivo. Permite cortar la
    /// ventana del bus en cuanto hay datos suficientes, en vez de esperar un tiempo fijo
    /// (el usuario sigue usando la PC y la medición termina sola).
    /// </summary>
    public int GetSamplingStampCount(string deviceKey)
    {
        // Varias entradas pueden compartir la clave (mouse y teclado del mismo receptor): se
        // devuelve la que MÁS sellos juntó, que es la que está reportando de verdad. Quedarse con
        // la primera podía devolver 0 y hacer que la ventana del bus creyera que no hay informes.
        int max = 0;
        foreach (var kvp in _byHandle)
        {
            if (!string.Equals(kvp.Value.Handle, deviceKey, StringComparison.OrdinalIgnoreCase)) continue;
            int count = kvp.Value.SampledStampCount;
            if (count > max) max = count;
        }
        return max;
    }

    /// <summary>
    /// Sellos QPC (µs) de llegada de cada informe del dispositivo con esa clave, cerrando la
    /// captura (par de EndSampling, para el cruce con la traza ETW del bus). Lista vacía si el
    /// dispositivo no mandó nada o no tiene captura activa.
    /// </summary>
    public long[] EndSamplingStamps(string deviceKey)
    {
        // Igual que GetSamplingStampCount: con varias entradas bajo la misma clave, la del cruce
        // con la traza del bus es la que trae sellos (las demás devuelven vacío).
        long[] best = Array.Empty<long>();
        foreach (var kvp in _byHandle)
        {
            if (!string.Equals(kvp.Value.Handle, deviceKey, StringComparison.OrdinalIgnoreCase)) continue;
            var stamps = kvp.Value.EndSamplingStamps();
            if (stamps.Length > best.Length) best = stamps;
        }
        return best;
    }

    /// <summary>Borra TODO (dispositivos y estadísticas). Se usa al cerrar el panel:
    /// al reabrir, el medidor arranca de cero en vez de arrastrar lo anterior.</summary>
    public void ClearAll() => _byHandle.Clear();

    private static long NowUs() => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

    // ===================== Muestras por dispositivo =====================

    /// <summary>Ring buffer de intervalos por handle. Escribe el hilo de mensajes,
    /// lee el de UI (Snapshot copia bajo lock: barato, máx 6000 doubles).</summary>
    private sealed class DeviceSamples
    {
        public InputDeviceKind Kind;
        public long DeviceId;
        public string Name = "";
        public string Handle = "";
        public InputTransport Transport = InputTransport.Unknown;
        private readonly double[] _buf = new double[WindowSize];
        private int _head;
        private int _count;
        private readonly object _lock = new();
        // Piso físico de un intervalo entre informes HID distintos (50 µs; 8000 Hz = 125 µs).
        // Por debajo es artefacto de lote (cola de mensajes despachando varios juntos), no dato.
        private const double MinPlausibleIntervalMs = 0.05;
        private long _lastArrivalUs; // 0 = todavía no hay informe previo (para el intervalo)
        public long LastArrivalUs;   // marca de actividad (NO se resetea con Reset(): define si el dispositivo sigue activo)

        // ===== Acumulador del TEST =====
        // Misma serie que el anillo, pero sin dar vueltas: el test necesita cubrir toda
        // su ventana. Techo de 100.000 intervalos = 12 s a 8000 Hz (800 KB por
        // dispositivo), de sobra para cualquier prueba y sin crecer sin control si el
        // usuario deja la prueba abierta.
        // ANILLO rodante (no una lista que crece): si la prueba dura más de lo que aguanta
        // el techo —12,5 s a 8000 Hz con 100.000 intervalos— se descartan los MÁS VIEJOS, así
        // una prueba "hasta detener" analiza la ventana más reciente en vez de quedarse con
        // los primeros segundos, que ya no representan nada. RemoveAt(0) sobre una lista
        // sería O(n) por informe (imposible a 8 kHz): de ahí el arreglo circular.
        private const int TestMaxIntervals = 100_000;
        private readonly double[] _testBuf = new double[TestMaxIntervals];
        private int _testHead;    // próxima posición a escribir
        private int _testCount;
        // Sellos QPC absolutos (µs) de cada llegada durante el test. Sirve para cruzar con la
        // traza ETW del bus (que trae el sello del KERNEL para cada transferencia): la
        // diferencia kernel → app es el tramo de host, la parte del camino que los tweaks
        // pueden tocar. Techo = TestMaxIntervals + 1 sellos.
        private readonly long[] _testStamps = new long[TestMaxIntervals + 1];
        private int _stampHead;
        private int _stampCount;
        public volatile bool Sampling;

        public void BeginSampling()
        {
            lock (_lock)
            {
                _testHead = 0;
                _testCount = 0;
                _stampHead = 0;
                _stampCount = 0;
                Sampling = true;
            }
        }

        private void AppendTest(double deltaMs)
        {
            if (!Sampling) return;
            _testBuf[_testHead] = deltaMs;
            _testHead = (_testHead + 1) % TestMaxIntervals;
            if (_testCount < TestMaxIntervals) _testCount++;
        }

        /// <summary>Guarda el sello QPC (µs) de la llegada, con el mismo descarte de huecos
        /// que la estadística: un informe tras una pausa no representa el camino del host.</summary>
        private void AppendStamp(long arrivalUs, double deltaMs)
        {
            if (!Sampling) return;
            if (deltaMs > 1000) return; // hueco (ventana congelada): el sello viejo no cruza bien
            _testStamps[_stampHead] = arrivalUs;
            _stampHead = (_stampHead + 1) % _testStamps.Length;
            if (_stampCount < _testStamps.Length) _stampCount++;
        }

        /// <summary>Cierra la captura y devuelve los intervalos acumulados en orden
        /// cronológico (copia).</summary>
        public double[] EndSampling()
        {
            lock (_lock)
            {
                Sampling = false;
                return TestSnapshot();
            }
        }

        /// <summary>Sellos acumulados en la captura en curso (para cortar la ventana).</summary>
        public int SampledStampCount { get { lock (_lock) return _stampCount; } }

        /// <summary>Cierra la captura y devuelve los sellos QPC (µs) de llegada de cada
        /// informe, en orden cronológico (copia). Par de EndSampling para el cruce con ETW.</summary>
        public long[] EndSamplingStamps()
        {
            lock (_lock)
            {
                Sampling = false;
                if (_stampCount == 0) return Array.Empty<long>();
                var arr = new long[_stampCount];
                if (_stampCount == _testStamps.Length)
                {
                    int tail = _stampHead;
                    Array.Copy(_testStamps, tail, arr, 0, _testStamps.Length - tail);
                    Array.Copy(_testStamps, 0, arr, _testStamps.Length - tail, tail);
                }
                else
                {
                    Array.Copy(_testStamps, arr, _stampCount);
                }
                return arr;
            }
        }

        /// <summary>Contenido del anillo en orden cronológico (llamar con el lock tomado).</summary>
        private double[] TestSnapshot()
        {
            if (_testCount == 0) return Array.Empty<double>();
            var arr = new double[_testCount];
            if (_testCount == TestMaxIntervals)
            {
                int tail = _testHead;
                Array.Copy(_testBuf, tail, arr, 0, TestMaxIntervals - tail);
                Array.Copy(_testBuf, 0, arr, TestMaxIntervals - tail, tail);
            }
            else
            {
                Array.Copy(_testBuf, arr, _testCount);
            }
            return arr;
        }

        /// <summary>
        /// Los últimos intervalos que sumen hasta <paramref name="windowMs"/>, acotado por
        /// <paramref name="maxIntervals"/>: es la ventana del trazo en vivo.
        /// </summary>
        public double[] TestTrail(int maxIntervals, double windowMs)
        {
            lock (_lock)
            {
                if (_testCount == 0) return Array.Empty<double>();

                int idx(int fromNewest) => ((_testHead - 1 - fromNewest) % TestMaxIntervals + TestMaxIntervals) % TestMaxIntervals;

                int take = 0;
                double sum = 0;
                while (take < _testCount && take < maxIntervals)
                {
                    double v = _testBuf[idx(take)];
                    if (take > 0 && sum + v > windowMs) break;
                    sum += v;
                    take++;
                }

                var arr = new double[take];
                for (int i = 0; i < take; i++) arr[i] = _testBuf[idx(take - 1 - i)]; // del más viejo al más nuevo
                return arr;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _head = 0;
                _count = 0;
                _lastArrivalUs = 0;
            }
        }

        public void AddArrival(long arrivalUs)
        {
            lock (_lock)
            {
                long last = _lastArrivalUs;
                _lastArrivalUs = arrivalUs;
                LastArrivalUs = arrivalUs;
                if (last == 0)
                {
                    // Primer informe: sin intervalo aún, pero el sello entra al buffer de
                    // cruce (siempre hay un kernel→app para medir) y el intervalo 0 no
                    // ensucia la estadística.
                    AppendStamp(arrivalUs, 0);
                    return;
                }
                double deltaMs = (arrivalUs - last) / 1000.0;
                // Hueco = ventana congelada/sleep del hilo: fuera de la estadística.
                if (deltaMs <= 0 || deltaMs > 1000) return;
                // ARTEFACTO DE LOTE: dos informes HID distintos no pueden llegar a menos de
                // ~50 µs (incluso a 8000 Hz son 125 µs). Cuando la cola de mensajes se atrasa,
                // varios informes se despachan juntos y sus marcas caen en el mismo µs: eso
                // NO es un intervalo del dispositivo, y si se cuela el "Mín" termina mostrando
                // 0,00 ms — un número que no significa nada. Se descarta el intervalo, no el
                // informe: la marca ya quedó guardada, así que el próximo delta se mide bien.
                if (deltaMs < MinPlausibleIntervalMs) return;
                _buf[_head] = deltaMs;
                _head = (_head + 1) % WindowSize;
                if (_count < WindowSize) _count++;
                // Captura del test: mismo intervalo, acumulador sin vueltas.
                AppendTest(deltaMs);
                AppendStamp(arrivalUs, deltaMs);
            }
        }

        public InputLatencyStats Snapshot()
        {
            lock (_lock)
            {
                if (_count == 0)
                    return InputLatencyStats.Empty(Kind, Name, Handle, DeviceId);
                var arr = new double[_count];
                if (_count == WindowSize)
                {
                    int tail = _head; // primera posición del bloque más viejo
                    Array.Copy(_buf, tail, arr, 0, WindowSize - tail);
                    Array.Copy(_buf, 0, arr, WindowSize - tail, tail);
                }
                else
                {
                    Array.Copy(_buf, arr, _count);
                }
                Array.Sort(arr);

                double sum = 0;
                for (int i = 0; i < arr.Length; i++) sum += arr[i];
                double avg = sum / arr.Length;
                return new InputLatencyStats(Kind, Name, Handle, DeviceId,
                    arr.Length, avg, arr[0], arr[^1],
                    // Mismo percentil que el test: una sola implementación (InputLatencyAnalysis).
                    InputLatencyAnalysis.Percentile(arr, 0.01), InputLatencyAnalysis.Percentile(arr, 0.99),
                    avg > 0 ? 1000.0 / avg : 0);
            }
        }
    }

    // ===================== Hilo de mensajes + Raw Input =====================

    private void MessageLoop()
    {
        _hwnd = CreateWindowForMessages();
        if (_hwnd == IntPtr.Zero)
        {
            _logging.LogWarning("InputLatency: no se pudo crear la ventana de mensajes; el medidor queda desactivado.");
            _running = false;
            return;
        }
        RegisterDevices();

        while (_running && GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_INPUT) HandleRawInput(msg.lParam);
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private void RegisterDevices()
    {
        if (_registered) return;
        _registered = true;
        // RIDEV_INPUTSINK: recibir sin foco y SIN consumir los informes (siguen su
        // curso normal).
        //
        // Usage page 0x01 (Generic Desktop) COMPLETA: pointer 0x01, mouse 0x02, joystick 0x04,
        // game pad 0x05, teclado 0x06, teclado numérico 0x07 y multi-axis 0x08. Windows entrega
        // WM_INPUT de un HID SOLO si la aplicación registró la colección de nivel superior con la
        // que el aparato se declara: un mando que se anuncia como "multi-axis con controller"
        // —habitual entre los que se conectan por BLUETOOTH, donde el aparato describe su propia
        // colección— no reportaba ni un informe por más que se lo usara, así que no aparecía en
        // la lista. Registrar de más no cuesta nada: los informes que no interesan se descartan
        // por tipo al despacharlos.
        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x01, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x07, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x08, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
        };
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            _logging.LogWarning($"InputLatency: RegisterRawInputDevices falló (error {Marshal.GetLastWin32Error()}).");
    }

    private void HandleRawInput(IntPtr lParam)
    {
        try
        {
            // 1) Encabezado (tipo + handle del dispositivo). Dos llamadas: tamaño, datos.
            uint size = 0;
            if (GetRawInputData(lParam, RID_HEADER, IntPtr.Zero, ref size, RawInputHeaderSize) == unchecked((uint)-1))
                return;
            var header = new RAWINPUTHEADER();
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RID_HEADER, buf, ref size, RawInputHeaderSize) == unchecked((uint)-1))
                    return;
                header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }

            long handle = header.hDevice.ToInt64();

            // 2) Si el informe es de un TECLADO, se guarda QUÉ tecla se apretó: es lo que enciende la
            // tecla correspondiente en el teclado en pantalla del onboarding visual. Va ANTES del
            // descarte de los informes sin dispositivo porque la entrada INYECTADA (y alguna
            // sintética) llega con handle 0: no sirve para MEDIR —no se puede atribuir a ningún
            // aparato—, pero el dato de la tecla sí es válido y la UI sabe descartarlo por
            // dispositivo. Con la captura antes del descarte, el camino completo queda verificable.
            if (header.dwType == 1) CaptureKeyPress(lParam, handle); // 1 = RIM_TYPEKEYBOARD

            if (handle == 0) return; // informe sin dispositivo asociado: no sirve para medir

            // 3) Marca de llegada en µs (QPC): el intervalo entre llegadas consecutivas
            // del MISMO dispositivo aproxima el período de polling real. Los informes
            // llegan al sink en orden y sin pasar por la cola de foco, así que el
            // jitter agregado es mínimo (µs).
            long arrivalUs = NowUs();

            var samples = _byHandle.GetOrAdd(handle, _ => DescribeDevice(header.hDevice, (int)header.dwType));
            samples.AddArrival(arrivalUs);
        }
        catch { /* un informe malo no mata al medidor */ }
    }

    /// <summary>
    /// Lee la tecla del informe (RAWINPUT.data.keyboard) y la encola para la UI. Solo se toman las
    /// PULSACIONES: el bit 0 de Flags (RI_KEY_BREAK) marca el soltado, y VKey 0xFF es un evento sin
    /// tecla real.
    ///
    /// Privacidad: de un teclado se guarda únicamente el código de tecla y el dispositivo, en
    /// memoria, para pintar la tecla del onboarding; se vacía en cada consulta de la UI (33 ms) y
    /// nunca se escribe en el log ni en disco. No hay captura de texto ni se conserva historial.
    /// </summary>
    private void CaptureKeyPress(IntPtr lParam, long deviceId)
    {
        try
        {
            uint size = 0;
            if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, RawInputHeaderSize) == unchecked((uint)-1))
            {
                WarnKeyReadOnce();
                return;
            }
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RID_INPUT, buf, ref size, RawInputHeaderSize) == unchecked((uint)-1))
                {
                    WarnKeyReadOnce();
                    return;
                }
                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(buf, (int)RawInputHeaderSize));
                if ((kb.Flags & 1) != 0) return;  // soltado
                if (kb.VKey == 0xFF) return;      // evento sin tecla
                lock (_keyLock)
                {
                    _recentKeys.Add((kb.VKey, deviceId));
                    // Tope: la UI consume cada 33 ms, así que una cola larga solo puede venir de un
                    // problema (UI congelada). Se descartan las más viejas en vez de crecer sin fin.
                    if (_recentKeys.Count > 64) _recentKeys.RemoveRange(0, _recentKeys.Count - 64);
                }
                Interlocked.Increment(ref _keysCaptured);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* un informe malo no mata al medidor */ }
    }

    /// <summary>Cantidad de pulsaciones capturadas desde que arrancó el medidor. Es el contador que
    /// permite diagnosticar sin guardar QUÉ tecla: 0 significa que no llega ningún informe de
    /// teclado, y un número que sube con ninguno encendido apunta a la UI.</summary>
    public long KeysCaptured => Interlocked.Read(ref _keysCaptured);

    /// <summary>Aviso ÚNICO (no por tecla) si el informe no se pudo leer: sin esto, un fallo de la
    /// API deja el teclado en pantalla muerto sin decir nada en ningún lado.</summary>
    private void WarnKeyReadOnce()
    {
        if (_keyReadWarned) return;
        _keyReadWarned = true;
        _logging.LogWarning("InputLatency: no se pudo leer el informe de una tecla (GetRawInputData): el teclado en pantalla del test no se va a encender.");
    }

    /// <summary>Teclas apretadas desde la última consulta (vacío si no hubo). Quien llama las usa
    /// para pintar la tecla en pantalla; el dato NO se registra en ningún log.</summary>
    public List<(int VKey, long DeviceId)> DrainKeyPresses()
    {
        lock (_keyLock)
        {
            if (_recentKeys.Count == 0) return new List<(int VKey, long DeviceId)>();
            var copy = new List<(int VKey, long DeviceId)>(_recentKeys);
            _recentKeys.Clear();
            return copy;
        }
    }

    /// <summary>Nombre legible de un handle Raw Input (una consulta por dispositivo nuevo).</summary>
    private DeviceSamples DescribeDevice(IntPtr hDevice, int dwType)
    {
        var ds = new DeviceSamples
        {
            DeviceId = hDevice.ToInt64(),
            LastArrivalUs = NowUs(),
            Sampling = _sampling, // un dispositivo que aparece en plena prueba también cuenta
            Kind = dwType switch
            {
                0 => InputDeviceKind.Mouse,     // RIM_TYPEMOUSE
                1 => InputDeviceKind.Keyboard,  // RIM_TYPEKEYBOARD
                _ => InputDeviceKind.Gamepad    // RIM_TYPEHID (gamepad/joystick)
            }
        };
        try
        {
            uint size = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size > 0)
            {
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buf, ref size) != unchecked((uint)-1))
                    {
                        // ANSI terminado en null: "\\??\HID#VID_046D&PID_C547&MI_00\8&..."
                        string path = Marshal.PtrToStringAnsi(buf)?.TrimEnd('\0') ?? "";
                        // El TRANSPORTE sale de la ruta de Raw Input y no del bus. OJO: la ruta de
                        // un HID por Bluetooth NO dice "BTHENUM" —eso está en el instance id de
                        // PnP, que es otro dato—; lo que la delata es el UUID del servicio HID de
                        // Bluetooth ("{00001124-...}") y el formato del VID/PID con "&":
                        // "VID&0002054c_PID&09cc" (los 4 hex del medio son el origen del id; el
                        // fabricante, los 4 últimos). Los USB escriben "VID_046D&PID_C547" con
                        // underscore, así que "VID&" es la marca de Bluetooth — y vale igual para
                        // el HID clásico y para BLE, que usan el mismo formato.
                        bool bluetooth = path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                            || path.Contains("{00001124-", StringComparison.OrdinalIgnoreCase)
                            || path.Contains("VID&", StringComparison.OrdinalIgnoreCase);
                        var m = Regex.Match(path, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})(?:&MI_([0-9A-Fa-f]{2}))?");
                        if (bluetooth)
                        {
                            ds.Transport = InputTransport.Bluetooth;
                            var bt = Regex.Match(path, @"VID&([0-9A-Fa-f]{2,8})_PID&([0-9A-Fa-f]{4})");
                            if (bt.Success)
                            {
                                // El VID de Bluetooth viene en 8 hex (los 4 primeros son el origen
                                // del id: 0002 = USB-IF) pero no se asume el largo: si viniera
                                // corto, cortarlo a ciegas tiraría excepción.
                                string vidHex = bt.Groups[1].Value;
                                string vid = (vidHex.Length > 4 ? vidHex[^4..] : vidHex).ToUpperInvariant();
                                string pid = bt.Groups[2].Value.ToUpperInvariant();
                                ds.Handle = $"VID_{vid}&PID_{pid}";
                                ds.Name = $"VID_{vid} PID_{pid}";
                            }
                        }
                        else if (m.Success)
                        {
                            // Un mouse por cable y uno inalámbrico con dongle de 2,4 GHz se ven
                            // idénticos en la ruta: el dato se saca del propio aparato, subiendo
                            // por su árbol PnP (su receptor, si lo tiene).
                            ds.Transport = TransportForInstance(path);
                            ds.Handle = $"VID_{m.Groups[1].Value}&PID_{m.Groups[2].Value}";
                            string mi = m.Groups[3].Success ? $" interfaz {m.Groups[3].Value}" : "";
                            ds.Name = $"VID_{m.Groups[1].Value} PID_{m.Groups[2].Value}{mi}";
                        }
                        if (ds.Handle.Length == 0)
                        {
                            // Ruta sin VID/PID reconocible (HID virtual o de otro enumerador):
                            // el Handle tiene que seguir siendo único, pero mostrarlo crudo era
                            // ilegible (media ruta: "9cc#b&82b2c29&0&0000#{4d1e..."). Se muestra
                            // un nombre legible y la identidad queda en el Handle.
                            ds.Handle = path.Length > 48 ? path[^48..] : path;
                            ds.Name = "Dispositivo HID";
                        }

                        // Nombre REAL del aparato, cuando Windows lo publica. Importa sobre todo
                        // en Bluetooth: esa ruta no trae un nombre de producto (el VID/PID que se
                        // ve ahí no es una identificación de marca) y la fila quedaba como
                        // "VID_054C PID_09CC", que no le dice nada a nadie. En USB no cambia
                        // nada: los nodos HID de un periférico cableado se llaman "HID-compliant
                        // mouse" y esas etiquetas genéricas se descartan (ver ReadableText).
                        string product = PnPFriendlyName(path);
                        if (product.Length > 0) ds.Name = product;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch { }
        if (string.IsNullOrEmpty(ds.Name)) ds.Name = ds.Handle;
        return ds;
    }

    // ============ Transporte por DISPOSITIVO: USB / Bluetooth / 2,4 GHz ============

    /// <summary>Palabras que delatan a un receptor inalámbrico en el nombre que el propio
    /// dispositivo reporta al bus. NO se incluyen "wireless" ni "lightspeed" sueltos: un
    /// mouse G502 X LIGHTSPEED enchufado por cable también se llama así, y ahí la etiqueta
    /// sería mentira. El 2,4 se exige con la "g" de GHz pegada ("2.4G", "2.4GHz", "2,4 GHz")
    /// para no marcar como receptor a un dispositivo que solo lleva "2.4" en el nombre por
    /// otro motivo (una versión, una medida).</summary>
    private static readonly string[] ReceiverTokens = { "receiver", "dongle", "unifying", "2,4g", "2.4g" };

    /// <summary>
    /// Transporte de UN dispositivo concreto, identificado por su instance id de PnP o por la
    /// ruta de Raw Input. Es el único camino: resolver por FAMILIA VID/PID era lo que permitía
    /// afirmar 2,4 GHz a partir del nombre de otro nodo con el mismo VID/PID (una instancia vieja
    /// del receptor, o el mismo PID conectado de las dos formas), y la evidencia tiene que venir
    /// del aparato que se está midiendo.
    ///
    /// La prueba positiva es siempre algo que el propio dispositivo declara: el nombre que
    /// reporta al bus ("LIGHTSPEED Receiver", "USB Receiver", "Unifying", "2.4G…") o que
    /// exponga slots de emparejamiento (ver HasPairingSlot). Bluetooth sale de la ruta de Raw
    /// Input, que lo dice de forma directa. Sin prueba la respuesta es USB: puede quedarse corta
    /// con un dongle que no se identifique —Windows no publica ninguna diferencia entre un dongle
    /// anónimo y un periférico por cable— pero nunca afirma un enlace inalámbrico sin respaldo.
    /// Best effort: si el árbol PnP o el registro no se pueden leer se responde USB, nunca una
    /// excepción.
    /// </summary>
    public InputTransport TransportForInstance(string instanceIdOrPath)
    {
        string instance = NormalizeInstance(instanceIdOrPath, out bool bluetooth);
        // La ruta es la vía rápida, pero no la única: un dispositivo por Bluetooth LE no escribe
        // ninguno de sus marcadores, así que también se pregunta al propio instance id.
        if (bluetooth || IsBluetoothEnumerator(instance)) return InputTransport.Bluetooth;
        if (instance.Length == 0) return InputTransport.Unknown;
        string self = VidPidToken(instance);
        if (self.Length == 0) return InputTransport.Unknown;
        return _instanceTransportCache.GetOrAdd(instance, _ => DetectInstanceTransport(instance, self));
    }

    /// <summary>Acepta las dos formas con las que llega un dispositivo: el instance id de PnP
    /// ("USB\VID_046D&PID_C547\6&185282C&0&1") o la ruta de Raw Input ("\\??\\HID#VID_046D&…
    /// #8&…#{4d1e55b2-…}"). En este segundo caso hay que sacar el prefijo, el GUID de la clase
    /// de interfaz del final y convertir los "#" en "\".
    ///
    /// Se corta en el ÚLTIMO "#{" y no en el primero: en Bluetooth el propio enumerador es
    /// "BTHENUM#{00001124-…}_VID&…_PID&…", así que la ruta tiene DOS y el primero pertenece al
    /// nombre del enumerador (cortar ahí devolvía "BTHENUM" a secas).
    ///
    /// El instance id se devuelve también cuando es Bluetooth: el TRANSPORTE se responde por la
    /// ruta, pero el NOMBRE hay que buscarlo en el nodo PnP de esa instancia.</summary>
    private static string NormalizeInstance(string raw, out bool bluetooth)
    {
        bluetooth = false;
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Trim();
        // Bluetooth: lo dice la ruta de Raw Input. {00001124-…} es el servicio HID clásico y
        // {00001812-…} el HID sobre GATT (Bluetooth LE, el que usan los mandos modernos).
        if (s.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
            || s.Contains("BTHLE", StringComparison.OrdinalIgnoreCase)
            || s.Contains("{00001124-", StringComparison.OrdinalIgnoreCase)
            || s.Contains("{00001812-", StringComparison.OrdinalIgnoreCase)
            || s.Contains("VID&", StringComparison.OrdinalIgnoreCase))
            bluetooth = true;
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        int guid = s.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid >= 0) s = s[..guid];
        return s.Replace('#', '\\');
    }

    /// <summary>
    /// ¿El instance id pertenece a un nodo enumerado por Bluetooth? Todos los enumeradores de
    /// Windows para Bluetooth empiezan con "BTH": BTHENUM (clásico), BTHLE (Bluetooth LE),
    /// BTHHFENUM (manos libres), BTHPAN, BTHA2DP…
    ///
    /// Es la señal más confiable de las dos, y por eso no alcanza con mirar la RUTA de Raw Input:
    /// un dispositivo por Bluetooth LE (HID sobre GATT) no escribe "VID&" ni el UUID del HID
    /// clásico, así que se clasificaba como USB —con su badge diciendo USB— estando inalámbrico.
    /// </summary>
    private static bool IsBluetoothEnumerator(string instanceOrId)
    {
        int sep = instanceOrId.IndexOf('\\');
        string enumerator = sep > 0 ? instanceOrId[..sep] : instanceOrId;
        return enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
    }

    private static string VidPidToken(string instance)
    {
        var m = Regex.Match(instance, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
        return m.Success
            ? $"VID_{m.Groups[1].Value.ToUpperInvariant()}&PID_{m.Groups[2].Value.ToUpperInvariant()}"
            : "";
    }

    /// <summary>
    /// Recorre el aparato hacia arriba mientras los nodos sigan siendo EL MISMO dispositivo
    /// (mismo VID/PID) y busca evidencia de receptor. El límite del VID/PID no es un detalle: más
    /// arriba está el hub o la controladora, y el nombre de un hub no dice nada del periférico
    /// (además de que un "hub 2.4G" contaría como falso receptor). Subir es imprescindible porque
    /// en un dispositivo compuesto el nombre del receptor vive en la RAÍZ (USB\VID_..&PID_..) y
    /// los HID cuelgan de sus interfaces (USB\..&MI_xx): así el mouse de un dongle se resuelve
    /// con el nombre de SU receptor, y uno por cable con el suyo (verificado en esta máquina: el
    /// receptor C547 se anuncia "LIGHTSPEED Receiver" y la identidad por cable del mismo mouse,
    /// C094, es un genérico "USB Composite Device").
    /// </summary>
    private static InputTransport DetectInstanceTransport(string instance, string self)
    {
        try
        {
            uint node = 0;
            if (CM_Locate_DevNodeW(ref node, instance, 0) != CrSuccess) return InputTransport.Usb;
            // Se recorre el camino COMPLETO buscando el enumerador de Bluetooth: un periférico USB
            // nunca cuelga de BTHENUM/BTHLE (el adaptador Bluetooth es un aparato aparte, no un
            // hub del que cuelguen periféricos). La evidencia de RECEPTOR, en cambio, se busca solo
            // mientras los nodos sigan siendo el mismo dispositivo: al cruzar ese límite está el
            // hub, y su nombre no dice nada del periférico.
            bool sameDevice = true;
            for (int level = 0; level < 8; level++)
            {
                string id = DevNodeId(node);
                if (id.Length == 0) break;
                if (IsBluetoothEnumerator(id)) return InputTransport.Bluetooth;
                if (sameDevice && id.Contains(self, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsReceiverName(RegistryTextOfInstance(id))) return InputTransport.Wireless24;
                    if (HasPairingSlot(node)) return InputTransport.Wireless24;
                }
                else sameDevice = false;
                if (CM_Get_Parent(out uint parent, node, 0) != CrSuccess) break;
                node = parent;
            }
            return InputTransport.Usb;
        }
        catch
        {
            // Sin árbol PnP legible: USB (una etiqueta menos precisa, nunca una falsa).
            return InputTransport.Usb;
        }
    }

    /// <summary>
    /// ¿El nodo del dispositivo expone hijos de SLOT de emparejamiento? Los receptores
    /// inalámbricos enumeran un nodo por slot (en esta máquina, el receptor Logitech expone
    /// "USB\VID_046D&PID_C547&LAMPARRAY\…_SLOT00…06"). Es una señal del PROPIO receptor y no
    /// depende de que un driver lo haya nombrado —justo el caso que el nombre no cubre, porque
    /// sin el software del fabricante el mismo dongle se anuncia "USB Composite Device"—. La
    /// evidencia contraria también se verificó acá: la identidad POR CABLE del mismo mouse no
    /// expone ningún slot, así que la señal no se confunde con un periférico cableado.
    /// </summary>
    private static bool HasPairingSlot(uint devInst)
    {
        if (CM_Get_Child(out uint child, devInst, 0) != CrSuccess) return false;
        uint current = child;
        for (int i = 0; i < 32; i++)
        {
            string id = DevNodeId(current);
            if (id.Contains("LAMPARRAY", StringComparison.OrdinalIgnoreCase)
                || id.Contains("SLOT", StringComparison.OrdinalIgnoreCase)
                || IsReceiverName(RegistryTextOfInstance(id))) return true;
            if (CM_Get_Sibling(out uint sibling, current, 0) != CrSuccess) break;
            current = sibling;
        }
        return false;
    }

    private static string DevNodeId(uint devInst)
    {
        if (CM_Get_Device_ID_Size(out uint len, devInst, 0) != CrSuccess || len == 0) return "";
        IntPtr buffer = Marshal.AllocHGlobal((int)(len + 1) * sizeof(char));
        try
        {
            return CM_Get_Device_IDW(devInst, buffer, len + 1, 0) == CrSuccess
                ? Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') ?? ""
                : "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Textos del nodo de ESA instancia concreta (no de toda la familia VID/PID).</summary>
    private static string RegistryTextOfInstance(string instanceId)
    {
        try
        {
            using var node = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            return node is null ? "" : DeviceNodeText(node);
        }
        catch { return ""; }
    }

    // ============ Nombre del aparato (para los que no están en el bus USB) ============

    /// <summary>Etiquetas que Windows da a clases enteras de dispositivo y no identifican a
    /// ninguno: "HID-compliant mouse" lo dice el mouse de cualquier marca. No se usan como
    /// nombre.</summary>
    private static readonly string[] GenericDeviceNames =
    {
        "hid-compliant", "hid keyboard", "hid mouse", "usb input device", "usb composite device",
        "bluetooth hid device", "dispositivo hid", "conforme a hid", "entrada usb"
    };

    /// <summary>
    /// Nombre del dispositivo según Windows, para los que NO tienen una fila en el bus USB (por
    /// ejemplo los conectados por Bluetooth). Se busca en el nodo PnP de la propia instancia —su
    /// FriendlyName, que es el nombre que Windows publica del aparato, y si no hay, el texto de
    /// DeviceDesc— y, si ahí no hay nada, un nivel arriba: en Bluetooth el nodo del enumerador
    /// suele llevar el nombre del aparato emparejado. Las etiquetas genéricas del sistema se
    /// descartan, salvo en Bluetooth: ahí cualquier nombre que Windows publique es mejor que un
    /// VID/PID sin significado.
    ///
    /// Best effort: sin registro legible devuelve vacío y quien llama deja el nombre que ya tenía.
    /// </summary>
    private static string PnPFriendlyName(string instanceOrPath)
    {
        string instance = NormalizeInstance(instanceOrPath, out bool bluetooth);
        if (instance.Length == 0) return "";

        string own = ReadableNameOfNode(instance, allowGeneric: bluetooth);
        if (own.Length > 0) return own;

        try
        {
            uint node = 0;
            if (CM_Locate_DevNodeW(ref node, instance, 0) == CrSuccess
                && CM_Get_Parent(out uint parent, node, 0) == CrSuccess)
            {
                string parentId = DevNodeId(parent);
                if (parentId.Length > 0) return ReadableNameOfNode(parentId, allowGeneric: bluetooth);
            }
        }
        catch { }
        return "";
    }

    /// <summary>Texto legible del nodo: FriendlyName primero, después lo que reporta el bus y por
    /// último DeviceDesc (ver ReadableText).</summary>
    private static string ReadableNameOfNode(string instanceId, bool allowGeneric)
    {
        try
        {
            using var node = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            if (node is null) return "";
            foreach (var valueName in new[] { "FriendlyName", "BusReportedDeviceDesc", "DeviceDesc" })
            {
                if (node.GetValue(valueName) is not string raw) continue;
                string text = ReadableText(raw);
                if (text.Length == 0) continue;
                if (!allowGeneric && IsGenericDeviceName(text)) continue;
                return text;
            }
            return "";
        }
        catch { return ""; }
    }

    /// <summary>El valor del registro llega como "@inf,%token%;Texto" (o como texto pelado): se
    /// toma lo que va después del último ';', que es lo que se le muestra a una persona.</summary>
    private static string ReadableText(string raw)
    {
        int sep = raw.LastIndexOf(';');
        string text = (sep >= 0 ? raw[(sep + 1)..] : raw).Trim();
        return text.StartsWith('@') ? "" : text;
    }

    private static bool IsGenericDeviceName(string text)
    {
        foreach (var generic in GenericDeviceNames)
            if (text.Contains(generic, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsReceiverName(string text)
    {
        if (text.Length == 0) return false;
        foreach (var token in ReceiverTokens)
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Textos legibles de un nodo del registro de dispositivos. La descripción llega
    /// como "@inf,%token%;Texto": se usa el valor completo, así sirven tanto el token del INF
    /// como el texto, sin importar el idioma del sistema.</summary>
    private static string DeviceNodeText(RegistryKey node)
    {
        var text = new StringBuilder();
        foreach (var valueName in new[] { "DeviceDesc", "BusReportedDeviceDesc", "FriendlyName" })
            if (node.GetValue(valueName) is string value && value.Length > 0)
                text.Append(value).Append(' ');
        return text.ToString();
    }

    // ===================== Interop =====================

    // --- Árbol PnP (cfgmgr32): resuelve el transporte del dispositivo CONCRETO, no de su familia.
    private const uint CrSuccess = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(ref uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint dnDevInst, IntPtr buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint pdnParent, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    private const uint WM_INPUT = 0x00FF;
    private const uint RID_HEADER = 0x10000005;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;

    // RIDI_DEVICEINFO llena RID_DEVICE_INFO: para un HID trae VID/PID y la colección de nivel
    // superior con la que el aparato se declara (usUsagePage/usUsage). RIM_TYPEHID es el dwType
    // con el que GetRawInputDeviceList marca a los HID.
    private const uint RIDI_DEVICEINFO = 0x2000000b;
    private const uint RIM_TYPEHID = 2;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    private const uint RawInputHeaderSize = 24; // sizeof(RAWINPUTHEADER): 4+4+8+8 en x64

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    /// <summary>RAWINPUT.data.keyboard: interesan VKey (qué tecla) y Flags (bit 0 = soltado). El
    /// campo ExtraInformation va alineado a 8, así que la estructura mide los 24 bytes que espera
    /// la API en x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public ulong ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    // Lista de dispositivos de entrada presentes (no confundir con RAWINPUTDEVICE, que es la
    // estructura para REGISTRARSE). Devuelve la cantidad o (uint)-1 si falla.
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    /// <summary>RID_DEVICE_INFO: cabecera (cbSize, dwType) + la unión de las tres variantes. Se
    /// declaran los campos de la variante HID (VID, PID, versión y la colección de nivel
    /// superior), que es la que interesa, y da el mismo tamaño que espera la API (24 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO
    {
        public uint cbSize;
        public uint dwType;
        public uint hidVendorId;
        public uint hidProductId;
        public uint hidVersionNumber;
        public ushort hidUsagePage;
        public ushort hidUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>Ventana invisible de solo mensajes (nunca se muestra ni recibe pintado).</summary>
    private IntPtr CreateWindowForMessages()
        => CreateWindowExW(0, "STATIC", "WinForgeInputLatency", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

    public void Dispose()
    {
        _running = false;
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}
