using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Test de estabilidad: pone todos los núcleos al 100% con la carga elegida
/// (FP, enteros, SSE, AVX, AVX-512) mientras un hilo de monitoreo lee uso,
/// temperatura, potencia y frecuencia cada segundo. Los hilos de carga corren
/// con prioridad elevada y se cancelan al terminar la duración o al detener.
///
/// El proceso es x86 (32 bits): AVX/AVX2/AVX-512 se ofrecen solo si las
/// instrucciones están DETECTADAS en el procesador, y cada carga usa el SIMD
/// más ancho que el runtime permita, cayendo a SSE/FP si no está disponible.
/// </summary>
public class StabilityService : IStabilityService
{
    private readonly ISystemInfoService _systemInfoService;
    private readonly ILoggingService _loggingService;

    private CancellationTokenSource? _cts;
    private Thread[]? _workers;
    private Thread? _monitorThread;
    private System.Diagnostics.PerformanceCounter? _cpuCounter;
    private DateTime _startTime;
    private TimeSpan _duration;
    private StabilityTestType _activeType;
    private volatile bool _isRunning;
    private double _maxUsage, _maxTemp, _maxPower;

    // Checksum por worker: cada hilo escribe su acumulador en SU casilla para que
    // el JIT no elimine los bucles de carga por "resultado sin usar".
    // OJO: NO usar un solo campo compartido: cada escritura invalida la línea de
    // caché en todos los núcleos (cache-line ping-pong) y eso frenaba el sistema
    // entero con el lagazo al iniciar el test.
    private static readonly double[] Checksums = new double[128];

    public StabilityService(ISystemInfoService systemInfoService, ILoggingService loggingService)
    {
        _systemInfoService = systemInfoService;
        _loggingService = loggingService;
    }

    public bool IsRunning => _isRunning;
    public StabilityTestType ActiveType => _activeType;
    public TimeSpan Duration => _duration;
    public DateTime StartTime => _startTime;
    public StabilitySample? LastSample { get; private set; }

    public event Action<StabilitySample>? SampleUpdated;
    public event Action<StabilityTestResult>? TestCompleted;

    /// <summary>
    /// Tipos de test según la detección de instrucciones del procesador. La carga
    /// SIMD se ofrece solo si el CPU la declara; si el runtime no la soporta (app
    /// de 32 bits), la carga cae internamente a SSE/FP.
    /// </summary>
    public IReadOnlyList<(StabilityTestType Type, string Label)> GetAvailableTestTypes()
    {
        string set = "";
        try { set = _systemInfoService.GetCpuInstructionSet(); } catch { }
        var tokens = set.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Has(string t) => tokens.Contains(t, StringComparer.OrdinalIgnoreCase);

        var list = new List<(StabilityTestType, string)>
        {
            (StabilityTestType.Balanced, "Equilibrado (mezcla)"),
            (StabilityTestType.Fpu, "FPU · punto flotante"),
            (StabilityTestType.Integer, "ALU · enteros")
        };

        if (Has("SSE2"))
            list.Add((StabilityTestType.Sse, "SSE / SSE2"));
        if (Has("AVX2"))
            list.Add((StabilityTestType.Avx, Has("FMA") ? "AVX2 + FMA" : "AVX2"));
        else if (Has("AVX"))
            list.Add((StabilityTestType.Avx, "AVX"));
        if (Has("AVX-512F"))
            list.Add((StabilityTestType.Avx512, "AVX-512"));

        return list;
    }

    public void Start(StabilityTestType type, TimeSpan duration)
    {
        if (_isRunning) return;
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromMinutes(10);
        if (duration > TimeSpan.FromHours(24)) duration = TimeSpan.FromHours(24);

        _activeType = type;
        _duration = duration;
        _startTime = DateTime.Now;
        _maxUsage = _maxTemp = _maxPower = 0;
        LastSample = null;
        _cts = new CancellationTokenSource();

        try
        {
            _cpuCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuCounter.NextValue(); // primera lectura descartada (necesita delta)
        }
        catch { _cpuCounter = null; }

        int cores = Math.Max(1, Environment.ProcessorCount);
        _workers = new Thread[cores];
        for (int i = 0; i < cores; i++)
        {
            int wi = i;
            var t = new Thread(() => WorkerLoop(type, wi, _cts!.Token))
            {
                IsBackground = true,
                // Prioridad NORMAL a propósito: un hilo por núcleo lógico ya satura
                // el CPU al ~100%. AboveNormal robaba tiempo al hilo de la UI y la
                // app entera (y el sistema) se congelaban al iniciar el test.
                Priority = ThreadPriority.Normal,
                Name = $"Stability-{wi}"
            };
            _workers[i] = t;
            t.Start();
        }

        _isRunning = true;
        _monitorThread = new Thread(MonitorLoop) { IsBackground = true, Name = "Stability-Monitor" };
        _monitorThread.Start();

        _loggingService.LogInfo($"Test de estabilidad iniciado: {type}, {duration.TotalMinutes:F0} min, {cores} hilos");
    }

    public void Stop() => Finish(completed: false);

    // ====== Hilo de monitoreo ======

    private void MonitorLoop()
    {
        while (_isRunning)
        {
            Thread.Sleep(1000);
            if (!_isRunning) break;

            double usage = 0, temp = 0, power = 0, freq = 0;
            try { if (_cpuCounter != null) usage = Math.Max(0, _cpuCounter.NextValue()); } catch { }
            try { temp = _systemInfoService.GetCpuTemperatureFresh(); } catch { }
            try { power = _systemInfoService.GetCpuPower(); } catch { }
            try { freq = _systemInfoService.GetCpuFrequency(); } catch { }

            var elapsed = DateTime.Now - _startTime;
            var remaining = _duration - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            if (usage > _maxUsage) _maxUsage = usage;
            if (temp > _maxTemp) _maxTemp = temp;
            if (power > _maxPower) _maxPower = power;

            var sample = new StabilitySample(usage, temp, power, freq, elapsed, remaining);
            LastSample = sample;
            try { SampleUpdated?.Invoke(sample); } catch { }

            if (remaining <= TimeSpan.Zero)
            {
                Finish(completed: true);
                return;
            }
        }
    }

    private void Finish(bool completed)
    {
        if (!_isRunning) return;
        _isRunning = false;
        try { _cts?.Cancel(); } catch { }

        var workers = _workers;
        _workers = null;
        if (workers != null)
        {
            foreach (var w in workers)
            {
                try { w.Join(300); } catch { }
            }
        }
        _monitorThread = null;
        try { _cpuCounter?.Dispose(); } catch { }
        _cpuCounter = null;

        var result = new StabilityTestResult(completed, _duration, _maxUsage, _maxTemp, _maxPower);
        _loggingService.LogInfo($"Test de estabilidad finalizado: completado={completed}, uso máx {_maxUsage:F0}%, temp máx {_maxTemp:F0}°C, potencia máx {_maxPower:F0}W");
        try { TestCompleted?.Invoke(result); } catch { }
    }

    // ====== Cargas de trabajo (1 hilo por núcleo lógico) ======

    private static void WorkerLoop(StabilityTestType type, int workerIndex, CancellationToken ct)
    {
        var rng = new Random(workerIndex * 7919 + 17);
        double[] buf = new double[4096];
        for (int i = 0; i < buf.Length; i++) buf[i] = rng.NextDouble() * 100.0;

        double acc = 0;
        int idx = workerIndex;
        long iters = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                switch (type)
                {
                    case StabilityTestType.Fpu:
                        FpuBlock(buf, ref acc, ref idx);
                        break;
                    case StabilityTestType.Integer:
                        IntegerBlock(buf, ref acc, ref idx);
                        break;
                    case StabilityTestType.Sse:
                        if (Sse2.IsSupported) SseBlock(buf, ref acc, ref idx);
                        else FpuBlock(buf, ref acc, ref idx);
                        break;
                    case StabilityTestType.Avx:
                        if (Avx2.IsSupported) AvxBlock(buf, ref acc, ref idx);
                        else if (Sse2.IsSupported) SseBlock(buf, ref acc, ref idx);
                        else FpuBlock(buf, ref acc, ref idx);
                        break;
                    case StabilityTestType.Avx512:
                        if (Avx512F.IsSupported) Avx512Block(buf, ref acc, ref idx);
                        else if (Avx2.IsSupported) AvxBlock(buf, ref acc, ref idx);
                        else if (Sse2.IsSupported) SseBlock(buf, ref acc, ref idx);
                        else FpuBlock(buf, ref acc, ref idx);
                        break;
                    default: // Balanced
                        FpuBlock(buf, ref acc, ref idx);
                        IntegerBlock(buf, ref acc, ref idx);
                        if (Sse2.IsSupported) SseBlock(buf, ref acc, ref idx);
                        break;
                }

                iters++;
                if ((iters & 0x1FFF) == 0)
                {
                    // Escribir el acumulador evita que el JIT elimine los bucles;
                    // cada worker escribe solo su casilla (sin contención de caché)
                    // y poco seguido (cada 8192 iteraciones).
                    Volatile.Write(ref Checksums[workerIndex & 127], acc);
                    if (ct.IsCancellationRequested) break;
                }
            }
        }
        catch { /* un hilo nunca debe tumbar el test */ }
    }

    private static void FpuBlock(double[] buf, ref double acc, ref int idx)
    {
        for (int i = 0; i < 512; i++)
        {
            idx = (idx + 1) & 4095;
            double v = buf[idx];
            v = Math.Sqrt(Math.Abs(v * 1.0000001 + 0.5));
            v = Math.Sin(v * 1.7) * Math.Cos(v * 0.9) + Math.Exp(v * 0.001);
            v = Math.Pow(v + 1.0, 1.00001);
            buf[idx] = v;
            acc += v;
        }
    }

    private static void IntegerBlock(double[] buf, ref double acc, ref int idx)
    {
        for (int i = 0; i < 512; i++)
        {
            idx = (idx + 1) & 4095;
            ulong u = (ulong)BitConverter.DoubleToInt64Bits(buf[idx]);
            u ^= u << 13; u ^= u >> 7; u ^= u << 17;
            u *= 0x9E3779B97F4A7C15UL;
            buf[idx] = BitConverter.Int64BitsToDouble((long)u);
            acc += u & 0xFFFF;
        }
    }

    private static void SseBlock(double[] buf, ref double acc, ref int idx)
    {
        var mul = Vector128.Create(1.0000001f);
        var add = Vector128.Create(0.5f);
        var v = Vector128<float>.Zero;
        for (int i = 0; i < 512; i++)
        {
            idx = (idx + 1) & 4095;
            var x = Vector128.Create((float)buf[idx]);
            v = Sse2.Add(Sse2.Multiply(v, mul), add);
            v = Sse2.Add(v, x);
        }
        acc += v.GetElement(0) + v.GetElement(3);
    }

    private static void AvxBlock(double[] buf, ref double acc, ref int idx)
    {
        var mul = Vector256.Create(1.0000001f);
        var add = Vector256.Create(0.5f);
        var v = Vector256<float>.Zero;
        for (int i = 0; i < 512; i++)
        {
            idx = (idx + 1) & 4095;
            var x = Vector256.Create((float)buf[idx]);
            v = Fma.IsSupported
                ? Fma.MultiplyAdd(v, mul, add)
                : Avx.Add(Avx.Multiply(v, mul), add);
            v = Avx.Add(v, x);
        }
        acc += v.GetElement(0) + v.GetElement(7);
    }

    private static void Avx512Block(double[] buf, ref double acc, ref int idx)
    {
        var mul = Vector512.Create(1.0000001f);
        var add = Vector512.Create(0.5f);
        var v = Vector512<float>.Zero;
        for (int i = 0; i < 512; i++)
        {
            idx = (idx + 1) & 4095;
            var x = Vector512.Create((float)buf[idx]);
            v = Avx512F.Add(Avx512F.Multiply(v, mul), add);
            v = Avx512F.Add(v, x);
        }
        acc += v.GetElement(0) + v.GetElement(15);
    }
}
