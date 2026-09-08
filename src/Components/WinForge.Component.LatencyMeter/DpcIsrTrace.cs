using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinForge.Component.LatencyMeter;

/// <summary>
/// Motor de medición: sesión ETW del kernel en tiempo real que procesa los pares
/// de eventos DPC/ISR (inicio/fin) y calcula duraciones por rutina.
///
/// Los eventos llegan al callback en un hilo interno de ETW; el callback parsea y
/// acumula con un lock corto. La UI muestrea ese estado con un timer.
///
/// Duración de cada rutina = TimeStamp(del evento de fin) - InitialTime(del payload),
/// ambos en QPC ticks (ClientContext=1 + PROCESS_TRACE_MODE_RAW_TIMESTAMP: enfoque
/// documentado para que ambas escalas coincidan, ver LatencyMon/OSR).
/// </summary>
public sealed class DpcIsrTrace : IDisposable
{
    // Sesión del kernel con nombre propio (no "NT Kernel Logger") + SYSTEM_LOGGER_MODE:
    // permite recibir eventos del kernel en Win8+ sin ocupar el slot único.
    private const string SessionName = "WinForge-LatencyMeter";
    private static readonly ulong InvalidHandle = unchecked((ulong)-1);

    private readonly object _statsLock = new();
    private readonly Dictionary<string, DriverAgg> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<double> _hist = new(4096);   // duraciones DPC (µs) de la ventana

    private long _dpcTotal;
    private long _isrTotal;
    private long _dpcTotalPrev;
    private long _dpcPerSec;
    private double _maxDpcUs;
    private double _maxIsrUs;
    private double _maxTotalUs;
    private string _worstModule = "";
    private double _worstUs;
    private double _qpcToUs = 0.1;   // fallback: ticks 100ns -> µs
    private Thread? _consumerThread;
    private ulong _sessionHandle = InvalidHandle;
    private ulong _traceHandle = InvalidHandle;
    private Native.TraceProperties? _props;
    private Native.TraceLogfile? _logfile;
    private GCHandle _selfHandle;
    private string? _lastError;
    private bool _disposed;

    /// <summary>Fila del ranking de drivers.</summary>
    public sealed record DriverRow(string Module, long DpcCount, long IsrCount, double MaxDpcUs, double MaxIsrUs, double MaxTotalUs, double TotalDpcUs);

    /// <summary>Snapshot de estado para la UI.</summary>
    public sealed record Snapshot(double MaxDpcUs, double MaxIsrUs, double MaxTotalUs, long DpcCount, long IsrCount,
        string WorstModule, double AvgDpcUs, double P99DpcUs, long DpcPerSec, double WindowMaxUs, IReadOnlyList<DriverRow> TopDrivers);

    public string? LastError => Volatile.Read(ref _lastError);
    public bool IsRunning => _traceHandle != InvalidHandle;

    /// <summary>Callback de ETW (hilo interno de ProcessTrace).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void EventCallbackStatic(Native.EVENT_RECORD* record)
    {
        // El contexto de la instancia llega en EVENT_RECORD.UserContext (copiado del
        // campo Context del EVENT_TRACE_LOGFILEW).
        var inst = (DpcIsrTrace?)GCHandle.FromIntPtr(record->UserContext).Target;
        inst?.OnEvent(record);
    }

    private unsafe void OnEvent(Native.EVENT_RECORD* r)
    {        if (r->ProviderId != Native.PerfInfoGuid) return;
        byte opcode = r->DescOpcode;
        if (opcode < 256) _opcodeCounts[opcode]++;
        RecordPayloadSample(opcode, r);

        // Escala del TimeStamp: los system loggers pueden ignorar ClientContext y
        // sellar en 100ns absolutos (FILETIME ~1.3e17) en vez de QPC boot-relative
        // (~1e13). FILETIME de cualquier fecha actual > 1e16; uptime QPC raramente
        // pasa 1e15 (3 años a 10 MHz): umbral limpio.
        if (!_tsScaleResolved)
        {
            _tsScaleResolved = true;
            if (r->TimeStamp > 1_000_000_000_000_000_0L) _tsIsFiletime = true;
            _qpcToUs = _tsIsFiletime ? 0.1 : 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        // 66 = DPC start (clásico), 67 = threaded DPC start (el que realmente se ve
        // en Win10/11, miles/s), 68 = DPC end (payload InitialTime+Routine).
        // 73 = ISR start, 74 = ISR end. SIN 67 en el set, la auto-calibración nunca
        // recibe muestras (bug detectado con el harness: +1.5k/−28k).
        bool isDpcStart = opcode is Native.EVENT_TRACE_TYPE_DPC_START or 67;
        bool isDpcEnd = opcode is Native.EVENT_TRACE_TYPE_DPC_END;
        bool isIsrStart = opcode is Native.EVENT_TRACE_TYPE_ISR_START;
        bool isIsrEnd = opcode is Native.EVENT_TRACE_TYPE_ISR_END;
        if (!isDpcStart && !isDpcEnd && !isIsrStart && !isIsrEnd) return;
        if (r->UserDataLength < 16 || r->UserData == IntPtr.Zero) return;
        long initialTime = Unsafe.Read<long>((void*)r->UserData);
        ulong routine = Unsafe.Read<ulong>((byte*)r->UserData + 8);
        if (routine == 0) return;

        bool isEnd = isDpcEnd || isIsrEnd;
        bool isDpc = isDpcStart || isDpcEnd;
        ushort cpu = r->ProcessorIndex;
        var key = (cpu, routine);
        long recTs = r->TimeStamp;

        // Eventos envejecidos: si el reloj del logger corre ~156 s por detrás del
        // nuestro, un end con QPC previo al ancla produce duración negativa de
        // escala de segundos. Descartar en vez de contaminar las estadísticas.
        if (recTs < _filetimeAtStart)
        {
            Interlocked.Increment(ref _staleCount);
            return;
        }

        // InitialTime (QPC) → dominio del record (FILETIME 100ns) con el ancla de Start().
        long initConv = initialTime;
        if (_tsIsFiletime)
        {
            long qpcDelta = initialTime - _qpcAtStart;              // ticks QPC
            initConv = _filetimeAtStart
                + (long)(qpcDelta * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        if (!isEnd)
        {
            // En un START, InitialTime refiere a la ENTRADA de la rutina y el ts del
            // record a cuándo se logueó: initConv - recTs mide directamente el error
            // del ancla (A). Es la fuente de auto-calibración (los threaded DPC
            // starts ocurren miles de veces por segundo).
            AddCalibrationSample(initConv - recTs);
            lock (_statsLock) { _openStarts[key] = recTs; }
            return;
        }

        long durationTicks;
        lock (_statsLock)
        {
            if (_openStarts.Remove(key, out long t0) && recTs - t0 >= 0)
            {
                // Par inicio/fin del MISMO CPU+rutina: duración EXACTA (mismo dominio).
                durationTicks = recTs - t0;
                Interlocked.Increment(ref _pairedCount);
            }
            else
            {
                // Camino principal en Win10/11 (DPC ends sin start): duración =
                // recEnd - kernelFT(t_entry), con initConv = kernelFT(t_entry) + A.
                // A (sesgo del ancla) viene de la auto-calibración con starts.
                durationTicks = recTs - initConv + (long)_calTicks;
                Interlocked.Increment(ref _unpairedCount);
            }
        }

        if (durationTicks < 0)
        {
            Interlocked.Increment(ref _negCount);
            lock (_statsLock)
            {
                if (_firstSamples.Count < 5)
                    _firstSamples.Add(new RawSample(initialTime, recTs, durationTicks));
            }
            return;
        }
        Interlocked.Increment(ref _posCount);
        double us = durationTicks * _qpcToUs;

        string module = ResolveModule(routine);
        RecordStat(module, isDpc, us);
    }

    // ===== Instrumentación (el harness la lee; la UI no) =====
    private readonly long[] _opcodeCounts = new long[256];
    private long _pairedCount;
    private long _unpairedCount;
    private long _staleCount;
    private bool _tsScaleResolved;
    private bool _tsIsFiletime;   // TimeStamp del record en FILETIME absoluto (100ns)
    private long _qpcAtStart;     // ancla QPC capturada en Start()
    private long _filetimeAtStart; // ancla FILETIME (100ns) capturada en Start()

    // Auto-calibración del ancla: cada par exacto mide el error del ancla (en
    // ticks de 100ns); se aplica la mediana como corrección para el resto.
    private readonly List<long> _calBuf = new(64);
    private double _calTicks;

    private void AddCalibrationSample(long anchorErrorTicks)
    {
        if (_calBuf.Count >= 64) return;
        _calBuf.Add(anchorErrorTicks);
        _calTicks = _calBuf.Count >= 8
            ? _calBuf.OrderBy(v => v).ElementAt(_calBuf.Count / 2)
            : anchorErrorTicks; // provisional hasta tener mediana estable
    }

    private readonly Dictionary<(ushort cpu, ulong routine), long> _openStarts = new();

    public long[] GetOpcodeCounts() { lock (_statsLock) { return (long[])_opcodeCounts.Clone(); } }
    public (long Paired, long Unpaired) GetPairStats() { lock (_statsLock) { return (_pairedCount, _unpairedCount); } }

    // ===== Diagnóstico crudo (solo harness) =====
    public sealed record RawSample(long InitialTime, long RecordTs, long DurationTicks);
    public sealed record RawDiagnostics(long PositiveCount, long NegativeCount, long QpcAtStart, long FiletimeAtStart, IReadOnlyList<RawSample> FirstSamples);
    private long _posCount, _negCount;
    private readonly List<RawSample> _firstSamples = new();
    private readonly Dictionary<byte, List<string>> _payloadSamples = new();

    /// <summary>Guarda los primeros payloads crudos de cada opcode (hex, 16 bytes).</summary>
    private unsafe void RecordPayloadSample(byte opcode, Native.EVENT_RECORD* r)
    {
        if (!_payloadSamples.TryGetValue(opcode, out var list))
        {
            list = new List<string>();
            _payloadSamples[opcode] = list;
        }
        if (list.Count >= 3) return;
        int len = Math.Min((int)r->UserDataLength, 24);
        var sb = new System.Text.StringBuilder(len * 3);
        for (int i = 0; i < len; i++) sb.Append(((byte*)r->UserData)[i].ToString("X2")).Append(' ');
        list.Add($"op={opcode} cpu={r->ProcessorIndex} len={r->UserDataLength} props=0x{r->EventProperty:X} ts={r->TimeStamp} payload=[{sb}] initialQpc={Unsafe.Read<long>((void*)r->UserData)} initAsUs={Unsafe.Read<long>((void*)r->UserData) * 0.1:F1}");
    }

    public IReadOnlyList<string> GetPayloadSamples()
    {
        lock (_statsLock)
            return _payloadSamples.SelectMany(kv => kv.Value).ToList();
    }

    public RawDiagnostics GetRawDiagnostics()
    {
        lock (_statsLock)
        {
            return new RawDiagnostics(Interlocked.Read(ref _posCount), Interlocked.Read(ref _negCount),
                _qpcAtStart, _filetimeAtStart, _firstSamples.ToList());
        }
    }

    private (ulong Start, ulong End, string Name)[]? _moduleRanges;
    private long _rangesTick;

    private string ResolveModule(ulong routine)
    {
        var snap = _moduleRanges;
        if (snap is null || snap.Length == 0 || Environment.TickCount64 - _rangesTick > 5000)
        {
            snap = RefreshRanges();
            _moduleRanges = snap;
            _rangesTick = Environment.TickCount64;
        }
        foreach (var (start, end, name) in snap)
            if (routine >= start && routine < end)
                return name;
        return "0x" + routine.ToString("X");
    }

    private (ulong, ulong, string)[] RefreshRanges()
    {
        try
        {
            Native.EnumDeviceDrivers(null, 0, out uint needed);
            if (needed == 0) return Array.Empty<(ulong, ulong, string)>();
            var bases = new ulong[needed / sizeof(ulong) + 4];
            if (!Native.EnumDeviceDrivers(bases, (uint)(bases.Length * sizeof(ulong)), out _))
                return Array.Empty<(ulong, ulong, string)>();
            int count = (int)(needed / sizeof(ulong));
            var list = new List<(ulong, ulong, string)>(count);
            var nameBuf = new char[520];
            for (int i = 0; i < count; i++)
            {
                if (bases[i] == 0) continue;
                uint len = Native.GetDeviceDriverFileNameW((IntPtr)bases[i], nameBuf, (uint)nameBuf.Length);
                if (len == 0 || len >= nameBuf.Length) continue;
                string path = new(nameBuf, 0, (int)len);
                // \SystemRoot\system32\... → C:\Windows\system32\... ; \??\C:\... → C:\...
                if (path.StartsWith("\\SystemRoot\\", StringComparison.OrdinalIgnoreCase))
                    path = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + path[11..];
                else if (path.StartsWith("\\??\\", StringComparison.Ordinal))
                    path = path[4..];
                string name = System.IO.Path.GetFileName(path);
                if (name.Length == 0) continue;
                // El tamaño exacto no viene en EnumDeviceDrivers: se usa el hueco hasta
                // la siguiente base (suficiente para atribuir la rutina al módulo).
                ulong size = i + 1 < count && bases[i + 1] > bases[i] ? bases[i + 1] - bases[i] : 0x800000;
                list.Add((bases[i], bases[i] + size, name));
            }
            list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return list.ToArray();
        }
        catch { return Array.Empty<(ulong, ulong, string)>(); }
    }

    private void RecordStat(string module, bool isDpc, double dpcUs)
    {
        lock (_statsLock)
        {
            if (!_stats.TryGetValue(module, out var agg))
            {
                agg = new DriverAgg(module);
                _stats[module] = agg;
            }
            if (isDpc)
            {
                agg.DpcCount++;
                agg.TotalDpcUs += dpcUs;
                if (dpcUs > agg.MaxDpcUs) agg.MaxDpcUs = dpcUs;
                if (dpcUs > _maxDpcUs) _maxDpcUs = dpcUs;
                _dpcTotal++;
                _hist.Add(dpcUs);
                if (_hist.Count > 40000) _hist.RemoveRange(0, _hist.Count - 40000);
            }
            else
            {
                agg.IsrCount++;
                if (dpcUs > agg.MaxIsrUs) agg.MaxIsrUs = dpcUs;
                if (dpcUs > _maxIsrUs) _maxIsrUs = dpcUs;
                _isrTotal++;
            }
            double worst = Math.Max(_maxDpcUs, _maxIsrUs);
            if (worst > _worstUs) { _worstUs = worst; _worstModule = module; }
        }
    }

    /// <summary>Snapshot para la UI (llamar desde el hilo de UI).</summary>
    public Snapshot GetSnapshot()
    {
        lock (_statsLock)
        {
            var hist = _hist.ToArray();
            Array.Sort(hist);
            double avg = hist.Length > 0 ? hist.Average() : 0;
            double p99 = hist.Length > 0 ? hist[(int)Math.Min(hist.Length - 1, (long)(hist.Length * 0.99))] : 0;
            double windowMax = hist.Length > 0 ? hist[^1] : 0;
            var top = _stats.Values
                .OrderByDescending(a => a.TotalDpcUs)
                .Take(10)
                .Select(a => new DriverRow(a.Module, a.DpcCount, a.IsrCount, a.MaxDpcUs, a.MaxIsrUs, a.MaxTotalUs, a.TotalDpcUs))
                .ToList();
            return new Snapshot(_maxDpcUs, _maxIsrUs, _maxTotalUs, _dpcTotal, _isrTotal,
                _worstModule, avg, p99, _dpcPerSec, windowMax, top);
        }
    }

    /// <summary>Recalcula DPC/s (lo llama el timer de la UI).</summary>
    public void TickRate()
    {
        lock (_statsLock)
        {
            _dpcPerSec = _dpcTotal - _dpcTotalPrev;
            _dpcTotalPrev = _dpcTotal;
        }
    }

    public bool Start()
    {
        if (IsRunning) return true;
        _lastError = null;

        // Ancla QPC<->FILETIME para el camino "InitialTime del payload": el system
        // logger sella los records en FILETIME absoluto (100ns) pero InitialTime
        // viene en QPC boot-relative. Capturado justo antes de StartTrace.
        _qpcAtStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Native.GetSystemTimePreciseAsFileTime(out _filetimeAtStart);

        // ClientContext=1 (QPC): TimeStamp del record y InitialTime del payload
        // quedan en la misma escala. El consumer abre con RAW_TIMESTAMP.
        _props = new Native.TraceProperties(SessionName,
            Native.EVENT_TRACE_FLAG_DPC | Native.EVENT_TRACE_FLAG_INTERRUPT,
            Native.EVENT_TRACE_REAL_TIME_MODE | Native.EVENT_TRACE_SYSTEM_LOGGER_MODE, 1);

        uint status = Native.StartTraceW(out _sessionHandle, SessionName, _props.Ptr);
        if (status != 0)
        {
            _lastError = status == Native.ERROR_ALREADY_EXISTS
                ? "Ya existe una sesión con ese nombre (¿otra instancia abierta?)."
                : $"StartTrace falló (0x{status:X8}).";
            return false;
        }

        _selfHandle = GCHandle.Alloc(this);
        _consumerThread = new Thread(ConsumeLoop)
        {
            Name = "WinForge-LatencyMeter-ETW",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _consumerThread.Start();
        return true;
    }

    private unsafe void ConsumeLoop()
    {
        try
        {
            _logfile = new Native.TraceLogfile(SessionName, &EventCallbackStatic);
            _logfile.SetContext((IntPtr)_selfHandle);

            _traceHandle = Native.OpenTraceW(_logfile.Ptr);
            if (_traceHandle == InvalidHandle)
            {
                Volatile.Write(ref _lastError, $"OpenTrace falló (0x{Marshal.GetLastWin32Error():X8}).");
                return;
            }

            _qpcToUs = 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
            var handles = new[] { _traceHandle };
            uint status = Native.ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            if (status != 0 && status != 0xC0000102) // STATUS_DELETE_PENDING al detener: esperado
                Volatile.Write(ref _lastError, $"ProcessTrace falló (0x{status:X8}).");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastError, ex.Message);
        }
    }

    public void Stop()
    {
        try
        {
            if (_sessionHandle != InvalidHandle)
            {
                using var stopProps = new Native.TraceProperties(SessionName, 0, 0, 0);
                Native.ControlTraceW(0, SessionName, stopProps.Ptr, Native.EVENT_TRACE_CONTROL_STOP);
                _sessionHandle = InvalidHandle;
            }
            if (_traceHandle != InvalidHandle)
            {
                Native.CloseTrace(_traceHandle);
                _traceHandle = InvalidHandle;
            }
            if (_selfHandle.IsAllocated) { _selfHandle.Free(); }
        }
        catch { /* Stop es best-effort: nunca debe lanzar desde la UI. */ }
    }

    public void Reset()
    {
        lock (_statsLock)
        {
            _stats.Clear();
            _hist.Clear();
            _dpcTotal = _isrTotal = _dpcTotalPrev = _dpcPerSec = 0;
            _maxDpcUs = _maxIsrUs = _maxTotalUs = _worstUs = 0;
            _worstModule = "";
        }
        _openStarts.Clear();
        Array.Clear(_opcodeCounts);
        Interlocked.Exchange(ref _pairedCount, 0);
        Interlocked.Exchange(ref _unpairedCount, 0);
        lock (_statsLock)
        {
            _calBuf.Clear();
            _calTicks = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _consumerThread?.Join(1500); } catch { }
        _props?.Dispose();
        _logfile?.Dispose();
    }

    private sealed class DriverAgg(string module)
    {
        public readonly string Module = module;
        public long DpcCount;
        public long IsrCount;
        public double TotalDpcUs;
        public double MaxDpcUs;
        public double MaxIsrUs;
        public double MaxTotalUs;
    }
}
