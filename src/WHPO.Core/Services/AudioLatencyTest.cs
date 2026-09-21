using System;

namespace WHPO.Core.Services;

/// <summary>
/// Veredicto de la latencia del MOTOR de audio (WASAPI): cuántos milisegundos pasan entre
/// que la app entrega muestras y el controlador las tiene. No juzga el DAC ni el parlante.
/// </summary>
public enum AudioEngineVerdict
{
    /// <summary>Ni una muestra: no se pudo abrir el endpoint o la ventana fue demasiado corta.</summary>
    InsufficientData,

    /// <summary>Motor de baja latencia (modo exclusivo o driver con período chico).</summary>
    LowLatency,

    /// <summary>Lo normal en Windows compartido: es el período del motor, no un defecto.</summary>
    Normal,

    /// <summary>Muy por encima de lo normal: otro proceso con el endpoint en exclusivo, un
    /// driver que pide búferes grandes, o el endpoint cambiando de período durante la prueba.</summary>
    High
}

/// <summary>Cómo se midió la ida y vuelta.</summary>
public enum AudioRoundTripMode
{
    /// <summary>No se pudo medir (endpoint sin salida o sin entrada disponible).</summary>
    None,

    /// <summary>
    /// Loopback INTERNO: se captura el mismo endpoint de salida, así que mide el camino de
    /// software (motor + búferes) y NO el DAC, el cable ni el ADC. Es el número que se puede
    /// dar sin conectar nada, y por eso se informa como software y no como latencia real.
    /// </summary>
    Software,

    /// <summary>Físico: el sonido salió por la salida y volvió por una entrada (cable o
    /// micrófono). Incluye DAC + amplificador + ADC, y en el caso del micrófono también el
    /// tiempo de vuelo del aire.</summary>
    Hardware
}

/// <summary>Veredicto de la ida y vuelta. Los umbrales dependen del modo (ver abajo).</summary>
public enum AudioRoundTripVerdict
{
    /// <summary>No se detectó señal de retorno: no hay loopback conectado.</summary>
    NotDetected,

    /// <summary>La primera intentona no alcanzó: se detectó señal en pocos intentos.</summary>
    InsufficientData,

    Excellent,

    Good,

    High
}

/// <summary>
/// Un endpoint de audio activo del sistema (salida o entrada), con lo que hace falta para
/// mostrarlo en la UI y para identificarlo entre corridas.
///
/// <paramref name="Id"/> es el id de endpoint de Windows (la cadena que empieza con "{0.0.0.00000000}"),
/// que sobrevive a la corrida: es la clave de comparación con la prueba anterior, igual que
/// el VID/PID en el test de periféricos.
/// </summary>
public sealed record AudioEndpointInfo(
    string Id,
    string Name,
    bool IsInput,
    bool IsDefault,
    int SampleRate,
    int Bits,
    int Channels)
{
    /// <summary>Etiqueta para el desplegable: "Salida · Nombre (predeterminado)".</summary>
    public override string ToString() => Name;
}

/// <summary>
/// Latencia del motor de audio medida sobre un endpoint durante una ventana: la serie de
/// muestras de <c>GetStreamLatency</c> (en ms) más los datos fijos del flujo que la explican.
///
/// OJO, límite honesto del instrumento: <c>GetStreamLatency</c> es un dato FIJO del stream ya
/// inicializado (búfer + período), así que su serie es plana por construcción y el veredicto
/// sale de su mediana. Lo que varía —y lo que se mide en vivo— es la
/// <paramref name="Cadence"/> del motor (eventos de búfer), que va adjunta acá porque se toma
/// en la misma ventana.
///
/// LÍMITE QUE HAY QUE DECIR EN VOZ ALTA: en modo compartido el motor es el de WINDOWS, con su
/// período por defecto de 10 ms, así que <paramref name="PeriodMs"/> sale casi idéntico en
/// cualquier dispositivo (un parlante de monitor y un auricular inalámbrico dan lo mismo). No
/// es un error de la medición: es que ese número describe al motor, no al aparato. Lo que SÍ
/// distingue a un endpoint es <paramref name="MinPeriodMs"/> (el período más chico que acepta:
/// los que soportan baja latencia bajan de esos 10 ms) y, sobre todo, la ida y vuelta, que es
/// la única medición que incluye los convertidores del dispositivo.
/// </summary>
public sealed record AudioEngineLatencyResult(
    int Samples,
    double AvgMs,
    double MinMs,
    double MaxMs,
    double MedianMs,
    double P99Ms,
    double PeriodMs,          // período del motor en modo compartido (GetDevicePeriod, default)
    double MinPeriodMs,       // período MÍNIMO que ESTE endpoint acepta en modo compartido (IAudioClient3)
    double BufferMs,          // búfer en ms (frames / frecuencia)
    double StreamLatencyMs,   // lo que reporta el propio motor para este flujo (GetStreamLatency; 0 = no lo reporta)
    bool EstimatedFromBuffer, // true cuando el driver no reporta latencia y el número sale del búfer
    int SampleRate,
    int Bits,
    int Channels,
    AudioEngineVerdict Verdict,
    AudioEngineCadenceResult? Cadence); // null = el driver no expuso los eventos de búfer

/// <summary>
/// Veredicto de la CADENCIA del motor: cada cuánto pide datos (eventos de búfer). No juzga
/// la latencia (eso lo dice la del motor): juzga si el motor entrega a su período o si se
/// saltea alguno, que es lo que se oye como un corte.
/// </summary>
public enum AudioEngineCadenceVerdict
{
    /// <summary>Menos eventos que el mínimo: la ventana no alcanza para juzgar.</summary>
    InsufficientData,

    /// <summary>Eventos parejos: el motor sostiene su período.</summary>
    Regular,

    /// <summary>Picos aislados (un porcentaje chico de eventos llega tarde): jitter audible en el borde.</summary>
    WithSpikes,

    /// <summary>
    /// Huecos marcados: el motor se saltea períodos enteros. Es el caso que explica los
    /// "crackles" y los tirones de audio, y el que la latencia del flujo NO muestra.
    /// </summary>
    Unstable
}

/// <summary>
/// Cadencia REAL del motor de audio durante la ventana: el intervalo entre eventos de búfer
/// (AUDCLNT_STREAMFLAGS_EVENTCALLBACK), en ms. Es una serie VIVA — a diferencia de la latencia
/// del flujo, que es un dato fijo del driver — y por eso es lo que se puede mirar en vivo.
///
/// <paramref name="MedianMs"/> es el período efectivo del motor y <paramref name="P99Ms"/> /
/// <paramref name="MaxMs"/> son la cola: un pico acá es un período que el motor se tomó de más.
/// </summary>
public sealed record AudioEngineCadenceResult(
    int Samples,              // intervalos entre eventos (sin los 2 primeros: el arranque no es representativo)
    double MedianMs,          // período efectivo del motor
    double MinMs,
    double MaxMs,
    double P99Ms,             // 1% de los eventos llegó más tarde que esto (jitter)
    double PeriodMs,          // período declarado por el motor, para comparar
    AudioEngineCadenceVerdict Verdict);

/// <summary>Ida y vuelta del sonido: cuánto tarda en volver lo que se reprodujo.</summary>
public sealed record AudioRoundTripResult(
    bool Detected,
    AudioRoundTripMode Mode,
    int Attempts,             // intentos disparados
    int DetectedAttempts,     // intentos con señal de retorno
    double MedianMs,          // mediana de los intentos válidos (el primer impulso se descarta)
    double MinMs,
    double MaxMs,
    double LevelDb,           // nivel del pico capturado, en dBFS (para saber si fue al límite del ruido)
    AudioRoundTripVerdict Verdict);

/// <summary>
/// Análisis puro del test de audio: percentiles, detección del impulso de retorno y
/// veredictos. Es una función sobre números (series de ms, muestras capturadas) para poder
/// verificarla con series sintéticas: ninguna conclusión puede depender de tener la placa de
/// sonido delante.
///
/// Los umbrales son juicios explícitos, no constantes físicas, y están acá arriba para
/// discutirlos sin tocar la medición.
/// </summary>
public static class AudioLatencyAnalysis
{
    /// <summary>Muestras mínimas (de latencia del motor) para que un veredicto signifique algo.</summary>
    public const int MinEngineSamples = 4;

    /// <summary>Motor "de baja latencia" hasta este valor. Un driver en modo exclusivo o con
    /// período corto queda acá; el modo compartido de Windows arranca en ~10 ms.</summary>
    public const double LowLatencyMs = 12.0;

    /// <summary>Arriba de esto el motor no es "normal": se informa como alto.</summary>
    public const double HighLatencyMs = 30.0;

    /// <summary>Eventos de búfer mínimos para que la cadencia signifique algo. Cada uno es un
    /// período del motor, así que a 10 ms son 6 décimas de segundo: el mínimo razonable.</summary>
    public const int MinCadenceSamples = 6;

    /// <summary>
    /// Cuántas veces la mediana puede valer el P99 de la cadencia antes de llamarla despareja.
    /// El jitter normal de un motor compartido es de unos pocos por ciento; un 35 % ya es un
    /// período que se estiró y se nota en el borde del sonido.
    /// </summary>
    public const double CadenceSpikeFactor = 1.35;

    /// <summary>
    /// Cuántas veces la mediana puede valer el PEOR intervalo antes de llamar inestable a la
    /// cadencia: es un período (o más) salteado, que es exactamente un hueco audible.
    /// </summary>
    public const double CadenceUnstableFactor = 2.5;

    /// <summary>
    /// Intervalos que se descartan al principio de la ventana: los primeros eventos del stream
    /// llegan con el motor acomodándose y no representan su cadencia.
    /// </summary>
    public const int CadenceWarmupSamples = 2;

    // Ida y vuelta: los umbrales son distintos según el modo porque miden cosas distintas.
    // Software (loopback interno) NO incluye el DAC ni el ADC, así que sus números son más
    // chicos: en la práctica son ~2 × el período del motor. Físico incluye toda la cadena
    // analógica, más el tiempo de vuelo si se usó un micrófono.
    public const double SoftwareExcellentMs = 25.0;
    public const double SoftwareGoodMs = 60.0;
    public const double HardwareExcellentMs = 40.0;
    public const double HardwareGoodMs = 100.0;

    /// <summary>Umbral absoluto del detector de impulso, en fracción de escala completa: por
    /// debajo de esto es ruido de fondo, no señal (evita detector disparado por silencio).</summary>
    public const double OnsetMinLevel = 0.01;

    /// <summary>Cuántas veces el piso de ruido tiene que superar la muestra para contar como
    /// comienzo del impulso.</summary>
    public const double OnsetNoiseMultiplier = 8.0;

    /// <summary>Muestras consecutivas por encima del umbral que confirman el comienzo (una
    /// sola muestra alta puede ser un click del sistema, no el impulso).</summary>
    public const int OnsetConfirmSamples = 3;

    /// <summary>Intentos de ida y vuelta de una corrida: el primero suele salir mal (los
    /// clientes todavía se están acomodando), así que se piden algunos más.</summary>
    public const int RoundTripAttempts = 5;

    /// <summary>
    /// Veredicto de la latencia del MOTOR. Se juzga con la MEDIANA de la serie y no con su
    /// P99: <c>GetStreamLatency</c> es un dato fijo del stream, así que la cola solo cambia si
    /// el endpoint cambió de período en medio de la ventana, y en ese caso la mediana es el
    /// valor que representa la corrida (el P99 queda informado aparte, como el peor visto).
    /// </summary>
    public static AudioEngineVerdict EngineVerdict(int samples, double medianMs)
    {
        if (samples < MinEngineSamples) return AudioEngineVerdict.InsufficientData;
        if (medianMs <= LowLatencyMs) return AudioEngineVerdict.LowLatency;
        if (medianMs <= HighLatencyMs) return AudioEngineVerdict.Normal;
        return AudioEngineVerdict.High;
    }

    /// <summary>
    /// Veredicto de la CADENCIA del motor (intervalos entre eventos de búfer). Compara la cola
    /// contra la propia mediana: no hay un número absoluto, porque el período lo elige el
    /// endpoint (puede ser 3 ms en modo de baja latencia o 10-30 ms en el compartido normal).
    /// </summary>
    public static AudioEngineCadenceResult? Cadence(double[] intervalsMs, double periodMs)
    {
        if (intervalsMs == null || intervalsMs.Length == 0) return null;

        // Los primeros eventos del stream no representan la cadencia: el motor se está acomodando.
        var usable = intervalsMs.Length > CadenceWarmupSamples
            ? intervalsMs[CadenceWarmupSamples..]
            : intervalsMs;

        var sorted = (double[])usable.Clone();
        Array.Sort(sorted);
        double median = InputLatencyAnalysis.Percentile(sorted, 0.5);
        double p99 = InputLatencyAnalysis.Percentile(sorted, 0.99);

        AudioEngineCadenceVerdict verdict;
        if (sorted.Length < MinCadenceSamples)
            verdict = AudioEngineCadenceVerdict.InsufficientData;
        else if (median > 0 && sorted[^1] > median * CadenceUnstableFactor)
            verdict = AudioEngineCadenceVerdict.Unstable;
        else if (median > 0 && p99 > median * CadenceSpikeFactor)
            verdict = AudioEngineCadenceVerdict.WithSpikes;
        else
            verdict = AudioEngineCadenceVerdict.Regular;

        return new AudioEngineCadenceResult(
            sorted.Length, median, sorted[0], sorted[^1], p99, periodMs, verdict);
    }

    public static AudioRoundTripVerdict RoundTripVerdict(AudioRoundTripMode mode, int detectedAttempts, double medianMs)
    {
        if (mode == AudioRoundTripMode.None || detectedAttempts == 0) return AudioRoundTripVerdict.NotDetected;
        if (detectedAttempts < 2) return AudioRoundTripVerdict.InsufficientData;

        double excellent = mode == AudioRoundTripMode.Software ? SoftwareExcellentMs : HardwareExcellentMs;
        double good = mode == AudioRoundTripMode.Software ? SoftwareGoodMs : HardwareGoodMs;
        if (medianMs <= excellent) return AudioRoundTripVerdict.Excellent;
        if (medianMs <= good) return AudioRoundTripVerdict.Good;
        return AudioRoundTripVerdict.High;
    }

    /// <summary>
    /// Busca el comienzo del impulso en las muestras capturadas y devuelve su índice (o -1 si
    /// no hay señal).
    ///
    /// El piso de ruido se estima con el PERCENTIL 20 de la señal completa y no con una
    /// ventana inicial: el impulso ocupa menos del 1 % de la captura, así que ese percentil
    /// es ruido, y en cambio una ventana fija puede comerse el propio impulso cuando el
    /// retorno llega antes de lo que mide la ventana (que es justo el caso del loopback
    /// interno, donde vuelve en milisegundos). Comparar contra el ruido propio de la placa
    /// es lo que hace que esto funcione igual con un cable (silencio digital) que con un
    /// micrófono (ruido ambiente).
    /// </summary>
    public static int DetectOnset(float[] samples)
    {
        if (samples == null || samples.Length < OnsetConfirmSamples + 8) return -1;

        var magnitudes = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++) magnitudes[i] = Math.Abs(samples[i]);
        var sorted = (double[])magnitudes.Clone();
        Array.Sort(sorted);
        double noise = InputLatencyAnalysis.Percentile(sorted, 0.2);

        double threshold = Math.Max(OnsetMinLevel, noise * OnsetNoiseMultiplier);

        int run = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            if (Math.Abs(samples[i]) >= threshold)
            {
                // El comienzo del impulso es la PRIMERA muestra de la racha, no la que confirma.
                run++;
                if (run >= OnsetConfirmSamples) return i - (OnsetConfirmSamples - 1);
            }
            else
            {
                run = 0;
            }
        }
        return -1;
    }

    /// <summary>Pico de la serie, en dBFS (0 = fondo de escala). -160 cuando no hay señal.</summary>
    public static double PeakDb(float[] samples)
    {
        if (samples == null || samples.Length == 0) return -160;
        double peak = 0;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        return peak <= 0 ? -160 : 20 * Math.Log10(peak);
    }

    /// <summary>
    /// Mediana de los intentos VÁLIDOS descartando el primero (los clientes de audio todavía
    /// se están acomodando y suele dar un valor inflado). Con un solo intento válido se
    /// devuelve ese.
    /// </summary>
    public static (double Median, double Min, double Max) SummarizeAttempts(double[] attemptsMs)
    {
        var valid = attemptsMs?.Length > 1 ? attemptsMs[1..] : attemptsMs ?? Array.Empty<double>();
        if (valid.Length == 0) return (0, 0, 0);

        var sorted = (double[])valid.Clone();
        Array.Sort(sorted);
        return (
            InputLatencyAnalysis.Percentile(sorted, 0.5),
            sorted[0],
            sorted[^1]);
    }

    /// <summary>Convierte a float las muestras capturadas, según el formato del flujo.
    /// Se soportan los dos que devuelve WASAPI en la práctica: float de 32 bits y PCM de 16.</summary>
    public static float[] ToFloat(IntPtr buffer, int bytes, int bitsPerSample)
    {
        if (buffer == IntPtr.Zero || bytes <= 0) return Array.Empty<float>();

        if (bitsPerSample == 32)
        {
            var result = new float[bytes / 4];
            System.Runtime.InteropServices.Marshal.Copy(buffer, result, 0, result.Length);
            return result;
        }

        if (bitsPerSample == 16)
        {
            var result = new float[bytes / 2];
            var raw = new short[result.Length];
            System.Runtime.InteropServices.Marshal.Copy(buffer, raw, 0, raw.Length);
            for (int i = 0; i < result.Length; i++) result[i] = raw[i] / 32768f;
            return result;
        }

        return Array.Empty<float>();
    }
}
