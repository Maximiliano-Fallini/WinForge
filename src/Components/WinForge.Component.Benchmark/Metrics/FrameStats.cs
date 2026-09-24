using System;
using System.Collections.Generic;

namespace WinForge.Component.Benchmark.Metrics;

/// <summary>
/// Una muestra de un frame de la escena. Campo por campo, lo que se puede medir de
/// verdad y sin adornos:
/// <list type="bullet">
/// <item><see cref="FrameMs"/>: intervalo entre dos presentaciones consecutivas
/// (reloj QPC del present). Es la fuente del FPS: FPS = 1000 / mediana.</item>
/// <item><see cref="GpuMs"/>: tiempo de GPU del frame, medido con los timestamps de
/// la propia API (0 si la API no expone timestamps o el dato no está listo todavía).</item>
/// <item><see cref="CpuMs"/>: trabajo del hilo de render para preparar y enviar el frame,
/// SIN contar la presentación.</item>
/// <item><see cref="PresentMs"/>: lo que tardó la entrega del frame (Present). Incluye la
/// espera por la cola de la GPU cuando el frame ya está listo: es "entrega", no CPU.</item>
/// </list>
/// Los cuatro juntos son los que permiten decir DÓNDE está el límite: un frame de 8 ms con
/// 7,5 ms de GPU está limitado por la placa; el mismo frame con 1,2 ms de GPU y 5 ms de
/// entrega está limitado por la presentación; y con 5 ms de CPU, por el CPU.
/// </summary>
public readonly record struct FrameSample(double FrameMs, double GpuMs, double CpuMs, double PresentMs, double QueueWaitMs = 0);

/// <summary>
/// Estadística de una corrida. Es una función PURA sobre las muestras (ver
/// <see cref="Compute"/>): el veredicto de una medición no puede depender de la
/// pantalla ni de la placa — se puede verificar con series sintéticas, igual que
/// InputLatencyAnalysis y AudioLatencyAnalysis de la app.
///
/// Los umbrales son juicios explícitos y viven acá arriba para poder discutirlos sin
/// tocar la medición.
/// </summary>
public sealed record FrameStats
{
    /// <summary>Un frame se cuenta como "hitch" si tarda al menos esto respecto de la mediana.</summary>
    public const double HitchFactor = 2.0;

    /// <summary>Y además si tarda más que este piso: un hitch en un juego a 500 FPS necesitaría 4 ms.</summary>
    public const double HitchFloorMs = 16.7;

    /// <summary>Porción inicial/final que se compara para el decaimiento sostenido.</summary>
    public const double DecayWindowFraction = 0.1;

    public int Frames { get; init; }
    public double DurationSeconds { get; init; }

    // ---- Fluidez (sobre el interval entre presents) ----
    public double AverageFps { get; init; }
    public double MedianFps { get; init; }
    public double MaxFps { get; init; }          // del frame más rápido
    public double MinFps { get; init; }          // del frame más lento
    public double Low1Fps { get; init; }         // promedio del peor 1 % de los frames
    public double Low01Fps { get; init; }        // promedio del peor 0,1 %
    public double MedianFrameMs { get; init; }
    public double P99FrameMs { get; init; }
    public double P999FrameMs { get; init; }
    public double WorstFrameMs { get; init; }
    public double HitchesPerMinute { get; init; }
    public int Hitches { get; init; }
    public double HitchThresholdMs { get; init; }
    public double OutOfBudgetPercent { get; init; }
    public double BudgetMs { get; init; }

    // ---- Decaimiento (¿aguanta el ritmo o se cae con el calor?) ----
    public double StartFps { get; init; }        // primer 10 % de la corrida
    public double EndFps { get; init; }          // último 10 %
    public double DecayPercent { get; init; }    // negativo = se cayó; 0 = sostuvo

    // ---- GPU / CPU por frame ----
    public int GpuSamples { get; init; }
    public double GpuAverageMs { get; init; }
    public double GpuMedianMs { get; init; }
    public double GpuP99Ms { get; init; }
    public double GpuBusyPercent { get; init; }  // ms de GPU por segundo de corrida
    public double CpuAverageMs { get; init; }
    public double CpuMedianMs { get; init; }
    public double CpuP99Ms { get; init; }

    public double PresentAverageMs { get; init; }
    public double PresentMedianMs { get; init; }
    public double PresentP99Ms { get; init; }

    /// <summary>
    /// Espera por la cola de la GPU antes de poder armar cada frame (valla de Direct3D 12). No es
    /// trabajo del CPU: es la placa viniendo atrasada. Se informa aparte justamente para que el
    /// número de CPU no la absorba.
    /// </summary>
    public double QueueWaitAverageMs { get; init; }
    public double QueueWaitMedianMs { get; init; }

    /// <summary>En qué segundo de la corrida cayó el peor frame (para saber si fue el arranque o ruido del sistema).</summary>
    public double WorstFrameAtSeconds { get; init; }

    /// <summary>
    /// Dónde está el límite, con las tres mediciones por frame: GPU, CPU de envío y entrega.
    /// Es un juicio explícito (con un margen del 20 %), no un teorema: sirve para que el informe
    /// no deje al usuario adivinando por qué el FPS no sube.
    /// </summary>
    public string LimitingFactor { get; init; } = "";

    public bool HasData => Frames > 0;

    /// <summary>
    /// Calcula la estadística de la corrida. <paramref name="warmupFrames"/> descarta los
    /// primeros frames (compilado de shaders, primer uso de las texturas, caches de driver):
    /// contarlos sería medir el arranque, no el rendimiento sostenido.
    /// </summary>
    public static FrameStats Compute(IReadOnlyList<FrameSample> samples, double budgetMs, int warmupFrames)
    {
        var stats = new FrameStats { BudgetMs = budgetMs };
        if (samples == null || samples.Count == 0) return stats;

        int start = Math.Clamp(warmupFrames, 0, Math.Max(0, samples.Count - 1));
        int count = samples.Count - start;
        if (count <= 0) return stats;            // ---- Copias para ordenar: la serie queda intacta (el decaimiento la necesita en orden) ----
            var frameMs = new double[count];
            var gpuMs = new List<double>(count);
            var cpuMs = new List<double>(count);
        var presentMs = new List<double>(count);
        var queueWaitMs = new List<double>(count);
        double frameSum = 0, cpuSum = 0, worstMs = -1, worstAtSeconds = 0, elapsedMs = 0;
            for (int i = 0; i < count; i++)
            {
                var s = samples[start + i];
                frameMs[i] = s.FrameMs;
                frameSum += s.FrameMs;
                // El acumulado está en milisegundos (suma de frametimes) y el informe lo
                // publica en SEGUNDOS: sin esta división, un hitch en el segundo 1,09 se
                // informaba como 1090 (se veía como si hubiera caído al minuto 18).
                elapsedMs += s.FrameMs;
                if (s.FrameMs > worstMs) { worstMs = s.FrameMs; worstAtSeconds = elapsedMs / 1000.0; }
            if (s.GpuMs > 0) gpuMs.Add(s.GpuMs);
            if (s.CpuMs > 0) cpuMs.Add(s.CpuMs);
            if (s.PresentMs > 0) presentMs.Add(s.PresentMs);
            if (s.QueueWaitMs > 0) queueWaitMs.Add(s.QueueWaitMs);
            cpuSum += s.CpuMs;
        }

        double durationSeconds = frameSum / 1000.0;
        var sorted = (double[])frameMs.Clone();
        Array.Sort(sorted);
        gpuMs.Sort();
        cpuMs.Sort();
        queueWaitMs.Sort();

        double median = Percentile(sorted, 0.5);
        double threshold = Math.Max(HitchFloorMs, median * HitchFactor);

        int hitches = 0;
        int outOfBudget = 0;
        foreach (double ms in frameMs)
        {
            if (ms > threshold) hitches++;
            if (ms > budgetMs) outOfBudget++;
        }

        double FpsOf(double ms) => ms > 0 ? 1000.0 / ms : 0;

        var result = new FrameStats
        {
            Frames = count,
            DurationSeconds = durationSeconds,
            AverageFps = durationSeconds > 0 ? count / durationSeconds : 0,
            MedianFps = FpsOf(median),
            // Los "mínimos" se informan como el FPS del peor frame y del mejor: son los
            // extremos reales de la serie, no un promedio disfrazado (mismo criterio que
            // el overlay de la app, que distingue FpsMin de los 1 % low).
            MinFps = FpsOf(sorted[^1]),
            MaxFps = FpsOf(sorted[0]),
            MedianFrameMs = median,
            P99FrameMs = Percentile(sorted, 0.99),
            P999FrameMs = Percentile(sorted, 0.999),
            WorstFrameMs = sorted[^1],
            Hitches = hitches,
            HitchThresholdMs = threshold,
            HitchesPerMinute = durationSeconds > 0 ? hitches * 60.0 / durationSeconds : 0,
            OutOfBudgetPercent = outOfBudget * 100.0 / count,
            BudgetMs = budgetMs,
            Low1Fps = FpsOf(WorstAverage(sorted, 0.01)),
            Low01Fps = FpsOf(WorstAverage(sorted, 0.001)),
            StartFps = WindowFps(frameMs, count, DecayWindowFraction, fromStart: true),
            EndFps = WindowFps(frameMs, count, DecayWindowFraction, fromStart: false),

            GpuSamples = gpuMs.Count,
            GpuAverageMs = gpuMs.Count > 0 ? Average(gpuMs) : 0,
            GpuMedianMs = gpuMs.Count > 0 ? Percentile(gpuMs, 0.5) : 0,
            GpuP99Ms = gpuMs.Count > 0 ? Percentile(gpuMs, 0.99) : 0,
            // Clamp a 100: la lectura del timestamp va un frame detrás del frametime (ver
            // D3D11Backend.CollectGpuTiming), así que en los bordes de la corrida pueden
            // contarse ms de GPU que el reloj de la corrida todavía no cubre — 114 % es
            // aritmética desalineada, no placa trabajando de más.
            GpuBusyPercent = durationSeconds > 0 ? Math.Min(100.0, Sum(gpuMs) / (durationSeconds * 1000.0) * 100.0) : 0,
            CpuAverageMs = cpuMs.Count > 0 ? cpuSum / count : 0,
            CpuMedianMs = cpuMs.Count > 0 ? Percentile(cpuMs, 0.5) : 0,
            CpuP99Ms = cpuMs.Count > 0 ? Percentile(cpuMs, 0.99) : 0,
            PresentAverageMs = presentMs.Count > 0 ? Average(presentMs) : 0,
            PresentMedianMs = presentMs.Count > 0 ? Percentile(presentMs, 0.5) : 0,
            PresentP99Ms = presentMs.Count > 0 ? Percentile(presentMs, 0.99) : 0,
            QueueWaitAverageMs = queueWaitMs.Count > 0 ? Average(queueWaitMs) : 0,
            QueueWaitMedianMs = queueWaitMs.Count > 0 ? Percentile(queueWaitMs, 0.5) : 0,
            WorstFrameAtSeconds = worstAtSeconds
        };

        double gpuMedian = result.GpuMedianMs;
        double cpuMedian = result.CpuMedianMs;
        double presentMedian = result.PresentMedianMs;
        const double Margin = 1.2;
        string factor =
            gpuMedian >= cpuMedian * Margin && gpuMedian >= presentMedian * Margin ? "GPU"
            : cpuMedian >= presentMedian * Margin ? "CPU (envío de frames)"
            : presentMedian >= gpuMedian * Margin ? "entrega de frames (present)"
            : "repartido entre GPU, CPU y entrega";
        result = result with { LimitingFactor = factor };

        double startFps = result.StartFps;
        if (startFps > 0)
        {
            // Negativo = la corrida se cayó (calor, potencia o fondo); 0 = sostuvo el ritmo.
            result = result with { DecayPercent = (result.EndFps - startFps) * 100.0 / startFps };
        }
        return result;
    }

    /// <summary>FPS promedio de la ventana inicial o final (para ver el decaimiento sostenido).</summary>
    private static double WindowFps(double[] frameMs, int count, double fraction, bool fromStart)
    {
        int window = Math.Max(1, (int)(count * fraction));
        int from = fromStart ? 0 : count - window;
        double sum = 0;
        for (int i = from; i < from + window; i++) sum += frameMs[i];
        return sum > 0 ? window * 1000.0 / sum : 0;
    }

    /// <summary>Percentil con interpolación lineal sobre una serie YA ordenada.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double pos = Math.Clamp(p, 0, 1) * (sorted.Count - 1);
        int lower = (int)Math.Floor(pos);
        int upper = (int)Math.Ceiling(pos);
        if (lower == upper) return sorted[lower];
        double t = pos - lower;
        return sorted[lower] * (1 - t) + sorted[upper] * t;
    }

    /// <summary>
    /// Promedio del peor <paramref name="worstFraction"/> de la serie ordenada. Se
    /// devuelve en milisegundos: el FPS bajo es 1000 / ese promedio.
    /// </summary>
    private static double WorstAverage(IReadOnlyList<double> sorted, double worstFraction)
    {
        int worstCount = Math.Max(1, (int)Math.Round(sorted.Count * worstFraction));
        double sum = 0;
        for (int i = sorted.Count - worstCount; i < sorted.Count; i++) sum += sorted[i];
        return sum / worstCount;
    }

    private static double Average(IReadOnlyList<double> values)
    {
        double sum = 0;
        foreach (double v in values) sum += v;
        return values.Count > 0 ? sum / values.Count : 0;
    }

    private static double Sum(IReadOnlyList<double> values)
    {
        double sum = 0;
        foreach (double v in values) sum += v;
        return sum;
    }
}
