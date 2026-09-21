using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Progreso de una corrida de audio. La UI dibuja con esto: en la fase del motor llegan
/// muestras cada <see cref="AudioLatencyService.EngineSampleMs"/>, y en la de ida y vuelta
/// llega un aviso por intento.
/// </summary>
public sealed record AudioTestProgress(
    bool RoundTripPhase,
    int EngineSamples,
    double EngineMs,
    double EngineP99Ms,
    int Attempt,
    int Attempts,
    double LastRoundTripMs,
    bool Detected,
    double EngineCadenceMs,       // último intervalo entre eventos de búfer (0 = todavía no hay)
    double EngineCadenceMedianMs, // mediana de la cadencia medida hasta ahora
    AudioRoundTripMode RoundTripMode); // camino que se está probando en la fase de ida y vuelta (None en la del motor)

/// <summary>
/// Una corrida completa del test de audio: el endpoint elegido, lo medido en el motor y lo
/// medido en la ida y vuelta. Sin corrida anterior: el test no guarda historial.
/// </summary>
public sealed record AudioLatencyRun(
    DateTimeOffset Timestamp,
    int WindowSeconds,
    AudioEndpointInfo Endpoint,
    AudioEngineLatencyResult? Engine,
    AudioRoundTripResult? RoundTrip,
    bool Stopped);

/// <summary>
/// Test de latencia de AUDIO, hermano del de periféricos: en vez del intervalo entre
/// informes HID, mide dos cosas de un endpoint de sonido. En la corrida van en este orden:
/// primero la ida y vuelta (corta, y es la que el usuario quiere ver) y después la ventana
/// del motor.
///
/// 1) Latencia del MOTOR: cuánto tarda WASAPI en mover las muestras hasta el controlador
///    (GetStreamLatency), muestreada durante la ventana. Se puede medir siempre, sin conectar
///    nada, y un pico acá explica los "crackles" y la sensación de audio atrasado.
/// 2) Ida y vuelta: se reproduce un impulso corto y se busca cuándo vuelve.
///    - FÍSICO (cable de salida a entrada, o micrófono): incluye DAC + amplificador + ADC.
///    - INTERNO (loopback del mismo endpoint de salida): solo el camino de software. Se
///      informa aparte y como tal, porque NO es la latencia del dispositivo.
///
/// Qué NO mide: la latencia del juego, la del driver de video ni la del teclado. Y el modo
/// software no incluye el DAC ni el parlante por definición.
/// </summary>
public sealed class AudioLatencyService
{
    /// <summary>Duración por defecto de la fase del motor.</summary>
    public const int DefaultDurationSeconds = 10;

    /// <summary>Cada cuánto se muestrea la latencia del motor.</summary>
    public const int EngineSampleMs = 200;

    /// <summary>Ventana en la que se busca el impulso de vuelta, por intento.</summary>
    public const int RoundTripWindowMs = 400;

    /// <summary>
    /// Intentos del camino FÍSICO que se tiran antes de darlo por ausente cuando no volvió nada.
    /// Dos, y no uno, porque el primer intento es el flojo: los dos clientes se están acomodando
    /// (el resumen descarta el primero por la misma razón). Sin señal, la fase física termina acá
    /// en vez de repetir el tono cinco veces con el usuario escuchando de gusto.
    /// </summary>
    private const int PhysicalProbeMissLimit = 2;

    private readonly ILoggingService _logging;

    public AudioLatencyService(ILoggingService logging)
    {
        _logging = logging;
    }

    // =====================================================================
    // Endpoints
    // =====================================================================

    /// <summary>
    /// Endpoints de audio ACTIVOS: salidas primero y después entradas, con el predeterminado
    /// de cada sentido marcado. Devuelve una lista vacía (no null) si no hay placa o si COM
    /// no está disponible: la UI muestra "no hay dispositivos" y listo.
    /// </summary>
    public IReadOnlyList<AudioEndpointInfo> EnumerateEndpoints()
    {
        var result = new List<AudioEndpointInfo>();
        bool com = WasapiAudio.TryInitCom();
        try
        {
            var enumerator = WasapiAudio.CreateEnumerator();
            if (enumerator == null) return result;

            string? defaultRender = WasapiAudio.GetDefaultEndpointId(enumerator, WasapiAudio.EDataFlowRender);
            string? defaultCapture = WasapiAudio.GetDefaultEndpointId(enumerator, WasapiAudio.EDataFlowCapture);

            AddEndpoints(result, enumerator, WasapiAudio.EDataFlowRender, isInput: false, defaultRender);
            AddEndpoints(result, enumerator, WasapiAudio.EDataFlowCapture, isInput: true, defaultCapture);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Test de audio: no se pudieron enumerar los dispositivos: {ex.Message}");
        }
        finally
        {
            if (com) WasapiAudio.CoUninitialize();
        }
        return result;
    }

    private void AddEndpoints(List<AudioEndpointInfo> result, WasapiAudio.IMMDeviceEnumerator enumerator,
        int dataFlow, bool isInput, string? defaultId)
    {
        if (enumerator.EnumAudioEndpoints(dataFlow, WasapiAudio.DeviceStateActive, out var collection) < 0 || collection == null)
            return;
        if (collection.GetCount(out int count) < 0) return;

        for (int i = 0; i < count; i++)
        {
            try
            {
                if (collection.Item(i, out var device) < 0 || device == null) continue;
                string? id = device.GetId(out var rawId) >= 0 ? rawId : null;
                if (string.IsNullOrWhiteSpace(id)) continue;

                string name = WasapiAudio.ReadFriendlyName(device) ?? (isInput ? "Entrada" : "Salida");

                int rate = 0, bits = 0, channels = 0;
                var client = WasapiAudio.ActivateClient(device);
                if (client != null && client.GetMixFormat(out var format) >= 0 && format != IntPtr.Zero)
                {
                    try
                    {
                        if (WasapiAudio.TryReadFormat(format, out var wf))
                        {
                            rate = (int)wf.nSamplesPerSec;
                            bits = wf.wBitsPerSample;
                            channels = wf.nChannels;
                        }
                    }
                    finally { Marshal.FreeCoTaskMem(format); }
                }

                result.Add(new AudioEndpointInfo(
                    id, name, isInput,
                    string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                    rate, bits, channels));
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"Test de audio: un dispositivo no se pudo leer: {ex.Message}");
            }
        }
    }

    // =====================================================================
    // Corrida completa
    // =====================================================================

    /// <summary>
    /// Corre el test sobre un endpoint: primero la ventana del motor, después la ida y
    /// vuelta. Cancelar no es un error (es "detener"): devuelve lo medido hasta ahí y la UI
    /// decide si lo acepta (modo hasta-detener) o lo descarta (abortar una duración fija).
    /// </summary>
    public async Task<AudioLatencyRun> RunAsync(
        AudioEndpointInfo endpoint,
        int durationSeconds,
        IProgress<AudioTestProgress>? progress,
        CancellationToken ct)
    {
        // Todo el trabajo de audio va en un hilo de fondo: abrir streams y esperar búferes
        // no puede bloquear el hilo de UI (y WASAPI se lleva bien con el apartment MTA).
        var result = await Task.Run(
            () => RunCore(endpoint, durationSeconds, progress, ct),
            ct).ConfigureAwait(false);
        return result;
    }

    private AudioLatencyRun RunCore(
        AudioEndpointInfo endpoint,
        int durationSeconds,
        IProgress<AudioTestProgress>? progress,
        CancellationToken ct)
    {
        bool com = WasapiAudio.TryInitCom();
        var sw = Stopwatch.StartNew();
        AudioEngineLatencyResult? engine = null;
        AudioRoundTripResult? roundTrip = null;
        bool stopped = false;

        try
        {
            var enumerator = WasapiAudio.CreateEnumerator();
            if (enumerator == null)
                return new AudioLatencyRun(DateTimeOffset.Now, 1, endpoint, null, null, false);

            string? deviceId = endpoint.Id;
            if (enumerator.GetDevice(deviceId, out var device) < 0 || device == null)
            {
                _logging.LogWarning($"Test de audio: el endpoint {endpoint.Name} no está disponible.");
                return new AudioLatencyRun(DateTimeOffset.Now, 1, endpoint, null, null, false);
            }

            // ---- Fase 1: ida y vuelta ----
            // Va PRIMERO por una razón de uso, no de física: es la medición corta (~1 s) y la
            // que el usuario quiere ver. Poniéndola después, en modo "hasta detener" no se
            // mediría nunca (el reloj del motor corre hasta que el usuario corta). Se intenta
            // primero el camino FÍSICO (salida → entrada): si hay un cable o un micrófono, ese
            // es el número que importa. Si no vuelve nada, se cae al loopback interno, que
            // siempre está disponible pero solo mide software.
            roundTrip = MeasureRoundTrip(enumerator, device, endpoint, progress, ct);

            // ---- Fase 2: latencia del motor ----
            if (!ct.IsCancellationRequested)
            {
                engine = MeasureEngine(enumerator, device, endpoint, durationSeconds, progress, ct, out stopped);
            }
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Test de audio: la prueba falló: {ex.Message}");
        }
        finally
        {
            if (com) WasapiAudio.CoUninitialize();
        }

        sw.Stop();
        if (ct.IsCancellationRequested) stopped = true;

        _logging.LogInfo($"Test de audio: {endpoint.Name} → motor " +
            (engine == null
                ? "sin datos"
                : $"{engine.MedianMs:0.#} ms (p99 {engine.P99Ms:0.#}), {engine.Samples} muestras" +
                  (engine.Cadence == null
                      ? " · cadencia no disponible (el driver no expone los eventos de bufer)"
                      : $" · cadencia {engine.Cadence.MedianMs:0.##} ms (p99 {engine.Cadence.P99Ms:0.##}, peor {engine.Cadence.MaxMs:0.##}), {engine.Cadence.Samples} eventos, {engine.Cadence.Verdict}")) +
            " · ida y vuelta " +
            (roundTrip == null || !roundTrip.Detected
                ? "sin señal de retorno"
                : $"{roundTrip.MedianMs:0.#} ms ({roundTrip.Mode}, {roundTrip.DetectedAttempts}/{roundTrip.Attempts})"));

        return new AudioLatencyRun(
            DateTimeOffset.Now,
            Math.Max(1, (int)Math.Round(sw.Elapsed.TotalSeconds)),
            endpoint,
            engine,
            roundTrip,
            stopped);
    }

    // =====================================================================
    // Fase 1: motor de audio
    // =====================================================================

    private AudioEngineLatencyResult? MeasureEngine(
        WasapiAudio.IMMDeviceEnumerator enumerator,
        WasapiAudio.IMMDevice device,
        AudioEndpointInfo endpoint,
        int durationSeconds,
        IProgress<AudioTestProgress>? progress,
        CancellationToken ct,
        out bool stopped)
    {
        stopped = false;
        // El stream de esta fase se abre con EVENTOS de búfer: la latencia del flujo es un dato
        // fijo del driver (búfer + período), así que la ventana no puede aportar una serie de
        // ella. Lo que sí varía es la CADENCIA del motor (cada cuánto pide datos), y para eso
        // hace falta el evento de AUDCLNT_STREAMFLAGS_EVENTCALLBACK.
        var stream = Open(device, loopback: false, eventCallback: true);
        if (stream == null)
        {
            _logging.LogWarning($"Test de audio: no se pudo abrir el endpoint {endpoint.Name} (¿está en uso en modo exclusivo?).");
            return null;
        }

        try
        {
            stream.Client.GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            stream.Client.GetBufferSize(out uint bufferFrames);
            stream.Client.GetStreamLatency(out long streamLatency);

            double sampleRate = stream.SampleRate;
            double bufferMs = sampleRate > 0 ? bufferFrames * 1000.0 / sampleRate : 0;
            if (bufferMs <= 0) bufferMs = 0.001; // nunca 0: el veredicto no puede salir de un número inexistente
            double periodMs = defaultPeriod / 10000.0;
            // El mínimo del dispositivo: es el dato que SÍ distingue a un endpoint de otro. En
            // modo compartido el período por defecto es el de Windows (10 ms) y sale igual en
            // casi cualquier aparato; el mínimo, en cambio, depende del driver y del hardware.
            double minimumPeriodMs = minimumPeriod / 10000.0;
            double minEnginePeriodMs = minimumPeriodMs;

            // Período REAL del motor en modo compartido (IAudioClient3). Es el dato que dice
            // si el endpoint está en el período chico de baja latencia o en el grande; si el
            // driver no expone IAudioClient3, queda el período del dispositivo y se informa.
            double enginePeriodMs = periodMs;
            var client3 = WasapiAudio.AsClient3(stream.Client);
            if (client3 != null &&
                client3.GetSharedModeEnginePeriod(stream.Format, out uint defaultFrames, out _, out uint minFrames, out _) >= 0 &&
                sampleRate > 0)
            {
                enginePeriodMs = defaultFrames * 1000.0 / sampleRate;
                if (minFrames > 0) minEnginePeriodMs = minFrames * 1000.0 / sampleRate;

                // El período vigente puede ser distinto del soportado (otra app puede haber
                // pedido baja latencia). Requiere el flujo inicializado: si falla, no pasa nada.
                if (client3.GetCurrentSharedModeEnginePeriod(out _, out uint currentFrames) >= 0 && currentFrames > 0)
                    enginePeriodMs = currentFrames * 1000.0 / sampleRate;
            }

            // Arranca el stream y lo deja lleno de silencio: la cadencia tiene que ser la de un
            // motor corriendo de verdad (un stream parado no le pide datos a nadie y no habría
            // nada que medir). El silencio no se oye.
            stream.Client.Start();
            stream.Started = true;
            FeedSilence(stream, bufferFrames);

            var samples = new List<double>();   // latencia del flujo (dato fijo del driver)
            var cadence = new List<double>();   // intervalos entre eventos de búfer (serie viva)
            long lastSignalTicks = 0;
            double lastCadenceMs = 0;
            bool timed = durationSeconds > 0;
            bool estimated = false;
            var sw = Stopwatch.StartNew();
            while (!timed || sw.Elapsed < TimeSpan.FromSeconds(durationSeconds))
            {
                if (ct.IsCancellationRequested) { stopped = true; break; }

                // La latencia del flujo (motor + driver) es un dato FIJO del stream: se muestrea
                // para ver si el endpoint cambia de período en medio de la ventana (otra app
                // pidiendo baja latencia lo hace), no para graficarla.
                double sampleMs = bufferMs;
                if (stream.Client.GetStreamLatency(out long latency) >= 0 && latency > 0)
                {
                    sampleMs = latency / 10000.0;
                }
                else
                {
                    // Hay drivers (sobre todo en placas USB y auriculares inalámbricos) que
                    // reportan 0 en GetStreamLatency porque no lo implementan. Tomar ese 0
                    // como real sería informar "baja latencia" cuando en realidad no hay
                    // dato: se usa el BÚFER, que es lo que WASAPI agrega antes del
                    // controlador, y se marca que el número es estimado.
                    estimated = true;
                }

                // Los eventos del búfer se esperan DENTRO del tick: cada vez que el motor libera
                // espacio se le entrega silencio y se anota el intervalo con el evento anterior.
                lastSignalTicks = CollectCadence(stream, bufferFrames, cadence, lastSignalTicks, ref lastCadenceMs);

                samples.Add(sampleMs);
                progress?.Report(new AudioTestProgress(
                    RoundTripPhase: false,
                    EngineSamples: samples.Count,
                    EngineMs: sampleMs,
                    EngineP99Ms: samples.Count >= 4 ? Percentile(samples, 0.99) : sampleMs,
                    Attempt: 0, Attempts: 0, LastRoundTripMs: 0, Detected: false,
                    EngineCadenceMs: lastCadenceMs,
                    EngineCadenceMedianMs: Median(cadence),
                    RoundTripMode: AudioRoundTripMode.None));

                // Con eventos, el ritmo del bucle lo marca el propio motor. Sin evento (driver que
                // no lo soporta) queda la espera fija, para no girar en vacío.
                if (stream.EventHandle == IntPtr.Zero)
                {
                    try { Task.Delay(EngineSampleMs, ct).Wait(ct); }
                    catch (OperationCanceledException) { stopped = true; break; }
                }
            }

            if (samples.Count == 0) return null;

            var sorted = samples.ToArray();
            Array.Sort(sorted);
            double avg = sorted.Average();
            double median = InputLatencyAnalysis.Percentile(sorted, 0.5);
            double p99 = InputLatencyAnalysis.Percentile(sorted, 0.99);

            return new AudioEngineLatencyResult(
                sorted.Length, avg, sorted[0], sorted[^1], median, p99,
                enginePeriodMs, minEnginePeriodMs, bufferMs, streamLatency / 10000.0,
                estimated,
                (int)sampleRate, stream.Fmt.wBitsPerSample, stream.Fmt.nChannels,
                AudioLatencyAnalysis.EngineVerdict(sorted.Length, median),
                AudioLatencyAnalysis.Cadence(cadence.ToArray(), enginePeriodMs));
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static double Percentile(List<double> values, double p)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return InputLatencyAnalysis.Percentile(sorted, p);
    }

    /// <summary>Mediana de la serie acumulada hasta ahora (0 si todavía no hay nada).</summary>
    private static double Median(List<double> values) =>
        values.Count == 0 ? 0 : Percentile(values, 0.5);

    /// <summary>
    /// Presupuesto de espera de eventos de búfer por tick. Es el mismo ritmo al que se muestrea
    /// la latencia, así que un tick no puede durar más que eso (salvo que el sistema se pause).
    /// </summary>
    private const int CadenceBudgetMs = EngineSampleMs;

    /// <summary>
    /// Un intervalo entre eventos de búfer más grande que esto no es cadencia del motor (el
    /// stream se frenó, la máquina se suspendió): se descarta en vez de ensuciar la cola.
    /// </summary>
    private const double MaxCadenceMs = 1000.0;

    /// <summary>
    /// Espera los eventos de búfer del tick y devuelve la marca del último: cada evento es el
    /// motor pidiendo más datos, así que el intervalo entre dos eventos es su cadencia REAL.
    /// En cada uno se le entrega silencio, porque el motor solo le pide datos a un cliente que
    /// está alimentando el stream (y el flag SILENT evita tocar el búfer).
    /// </summary>
    private static long CollectCadence(OpenStream stream, uint bufferFrames, List<double> cadence,
        long lastSignalTicks, ref double lastCadenceMs)
    {
        if (stream.EventHandle == IntPtr.Zero) return lastSignalTicks;

        long tickStart = Stopwatch.GetTimestamp();
        while (true)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - tickStart) * 1000.0 / Stopwatch.Frequency;
            int remainingMs = CadenceBudgetMs - (int)elapsedMs;
            if (remainingMs <= 0) break;

            if (WasapiAudio.WaitForSingleObject(stream.EventHandle, (uint)remainingMs) != WasapiAudio.WaitObject0)
                break;

            FeedSilence(stream, bufferFrames);

            long signalTicks = Stopwatch.GetTimestamp();
            if (lastSignalTicks != 0)
            {
                double intervalMs = (signalTicks - lastSignalTicks) * 1000.0 / Stopwatch.Frequency;
                if (intervalMs > 0 && intervalMs < MaxCadenceMs)
                {
                    cadence.Add(intervalMs);
                    lastCadenceMs = intervalMs;
                }
            }
            lastSignalTicks = signalTicks;
        }
        return lastSignalTicks;
    }

    /// <summary>
    /// Llena con SILENCIO el espacio que el motor liberó: el stream queda como un cliente que
    /// entrega datos (que es lo que el motor espera) sin que se oiga nada. GetCurrentPadding
    /// dice cuánto queda encolado, así que el espacio libre es el búfer menos eso.
    /// </summary>
    private static void FeedSilence(OpenStream stream, uint bufferFrames)
    {
        try
        {
            var render = stream.Render;
            if (render == null || bufferFrames == 0) return;
            if (stream.Client.GetCurrentPadding(out uint padding) < 0) return;

            uint available = padding >= bufferFrames ? 0 : bufferFrames - padding;
            if (available == 0) return;

            if (render.GetBuffer(available, out IntPtr buffer) < 0 || buffer == IntPtr.Zero) return;
            render.ReleaseBuffer(available, WasapiAudio.BufferFlagsSilent);
        }
        catch { }
    }

    // =====================================================================
    // Fase 2: ida y vuelta
    // =====================================================================

    private AudioRoundTripResult MeasureRoundTrip(
        WasapiAudio.IMMDeviceEnumerator enumerator,
        WasapiAudio.IMMDevice outputDeviceOrAny,
        AudioEndpointInfo endpoint,
        IProgress<AudioTestProgress>? progress,
        CancellationToken ct)
    {
        // El par se arma solo: si el usuario eligió una SALIDA, la vuelta se busca en la
        // entrada predeterminada; si eligió una ENTRADA, se reproduce por la salida
        // predeterminada. Así el test necesita una sola elección.
        var outputDevice = endpoint.IsInput
            ? ResolveDefault(enumerator, WasapiAudio.EDataFlowRender) ?? outputDeviceOrAny
            : outputDeviceOrAny;

        var hardware = MeasureRoundTripPhysical(enumerator, outputDevice, endpoint, progress, ct);
        if (hardware.Detected) return hardware;

        // Sin señal por la entrada: se mide por software (loopback del mismo endpoint de
        // salida). Se informa como software para no hacer pasar por latencia del dispositivo
        // algo que no incluye el DAC.
        var software = MeasureRoundTripSoftware(outputDevice, endpoint, progress, ct);
        return software.Detected ? software : hardware;
    }

    private WasapiAudio.IMMDevice? ResolveDefault(WasapiAudio.IMMDeviceEnumerator enumerator, int dataFlow)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(dataFlow, WasapiAudio.ERoleConsole, out var device) >= 0
                ? device
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Ida y vuelta FÍSICA: reproduce el impulso por la salida y lo busca en la entrada. El
    /// tiempo se calcula con los dos QPC que da WASAPI (posición del reloj de la salida y
    /// QPC del paquete capturado), que están en la misma base de 100 ns: por eso el número
    /// incluye DAC + amplificador + ADC (+ el aire, si es un micrófono).
    ///
    /// Si no vuelve nada en los primeros intentos se corta acá (ver PhysicalProbeMissLimit): un
    /// cable o un micrófono están o no están, y repetir el tono no los inventa.
    /// </summary>
    private AudioRoundTripResult MeasureRoundTripPhysical(
        WasapiAudio.IMMDeviceEnumerator enumerator,
        WasapiAudio.IMMDevice outputDevice,
        AudioEndpointInfo endpoint,
        IProgress<AudioTestProgress>? progress,
        CancellationToken ct)
    {
        var inputDevice = endpoint.IsInput ? null : ResolveDefault(enumerator, WasapiAudio.EDataFlowCapture);
        if (endpoint.IsInput)
        {
            // El endpoint elegido ES la entrada: se busca en él directamente.
            if (enumerator.GetDevice(endpoint.Id, out var chosen) < 0) chosen = null;
            inputDevice = chosen;
        }
        if (inputDevice == null) return NotDetected(AudioRoundTripMode.Hardware);

        var output = Open(outputDevice, loopback: false);
        var input = Open(inputDevice, loopback: false);
        if (output?.Render == null || input?.Capture == null)
        {
            output?.Dispose();
            input?.Dispose();
            return NotDetected(AudioRoundTripMode.Hardware);
        }

        try
        {
            output.Client.Start();
            input.Client.Start();
            Thread.Sleep(300); // que los dos flujos arranquen y se estabilicen antes de medir

            var impulse = BuildInterleavedImpulse(output);
            var attempts = new List<double>();
            int fired = 0;
            for (int i = 0; i < AudioLatencyAnalysis.RoundTripAttempts && !ct.IsCancellationRequested; i++)
            {
                fired++;
                Drain(input);
                ulong playQpc = WriteImpulse(output, impulse);
                ulong? onsetQpc = ReadOnsetQpc(input, RoundTripWindowMs, ct);

                if (onsetQpc != null)
                {
                    double ms = (onsetQpc.Value - playQpc) / 10000.0;
                    if (ms is > 0 and < RoundTripWindowMs) attempts.Add(ms);
                }

                progress?.Report(new AudioTestProgress(
                    RoundTripPhase: true,
                    EngineSamples: 0, EngineMs: 0, EngineP99Ms: 0,
                    Attempt: fired, Attempts: AudioLatencyAnalysis.RoundTripAttempts,
                    LastRoundTripMs: attempts.Count > 0 ? attempts[^1] : 0,
                    Detected: attempts.Count > 0,
                    EngineCadenceMs: 0, EngineCadenceMedianMs: 0,
                    RoundTripMode: AudioRoundTripMode.Hardware));

                // Sonda de PRESENCIA: si los primeros intentos no devuelven nada, no hay cable ni
                // micrófono escuchando, y seguir tocando tonos solo alarga la prueba y el ruido de
                // la sala (el loopback interno toma el relevo enseguida). Dos intentos y no uno
                // porque el PRIMERO es el flojo por definición: los clientes se están acomodando
                // y hasta el resumen lo descarta (ver SummarizeAttempts). Con señal detectada se
                // completan todos los intentos, que es lo que da una mediana en serio.
                if (attempts.Count == 0 && fired >= PhysicalProbeMissLimit) break;

                Thread.Sleep(150);
            }

            return Summarize(AudioRoundTripMode.Hardware, attempts, 0, fired);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Test de audio: ida y vuelta física: {ex.Message}");
            return NotDetected(AudioRoundTripMode.Hardware);
        }
        finally
        {
            output.Dispose();
            input.Dispose();
        }
    }

    /// <summary>
    /// Ida y vuelta por SOFTWARE: se captura por loopback el mismo endpoint de salida, así
    /// que mide el camino motor + búferes y nada más.
    ///
    /// Detalle que no es obvio: acá NO se pueden restar los QPC (el que reporta la captura de
    /// loopback es el del RENDER, o sea el mismo momento en que se reprodujo: la resta daría
    /// cero siempre). Por eso se mide el reloj del sistema: desde que se entrega el impulso
    /// hasta que el paquete que lo contiene se puede leer. Eso incluye el período del motor y
    /// el búfer que ya estaba encolado, que es exactamente la latencia de software, con una
    /// precisión limitada por cada cuánto se sondea la captura (~1 ms).
    ///
    /// Ojo con lo que significa el número: incluye el búfer del PROPIO cliente de loopback, así
    /// que es una cota superior del camino de software, no la latencia del dispositivo (por eso
    /// se informa como "software" y no compite con la del camino físico).
    /// </summary>
    private AudioRoundTripResult MeasureRoundTripSoftware(
        WasapiAudio.IMMDevice outputDevice,
        AudioEndpointInfo endpoint,
        IProgress<AudioTestProgress>? progress,
        CancellationToken ct)
    {
        if (endpoint.IsInput) return NotDetected(AudioRoundTripMode.Software);

        var output = Open(outputDevice, loopback: false);
        var loopback = Open(outputDevice, loopback: true);
        if (output?.Render == null || loopback?.Capture == null)
        {
            output?.Dispose();
            loopback?.Dispose();
            return NotDetected(AudioRoundTripMode.Software);
        }

        try
        {
            output.Client.Start();
            loopback.Client.Start();
            Thread.Sleep(200);

            var impulse = BuildInterleavedImpulse(output);
            var attempts = new List<double>();
            for (int i = 0; i < AudioLatencyAnalysis.RoundTripAttempts && !ct.IsCancellationRequested; i++)
            {
                Drain(loopback);

                long submittedTicks = Stopwatch.GetTimestamp();
                WriteImpulse(output, impulse);
                double? ms = ReadOnsetWallClock(loopback, RoundTripWindowMs, submittedTicks, ct);

                if (ms != null) attempts.Add(ms.Value);

                progress?.Report(new AudioTestProgress(
                    RoundTripPhase: true,
                    EngineSamples: 0, EngineMs: 0, EngineP99Ms: 0,
                    Attempt: i + 1, Attempts: AudioLatencyAnalysis.RoundTripAttempts,
                    LastRoundTripMs: attempts.Count > 0 ? attempts[^1] : 0,
                    Detected: attempts.Count > 0,
                    EngineCadenceMs: 0, EngineCadenceMedianMs: 0,
                    RoundTripMode: AudioRoundTripMode.Software));

                Thread.Sleep(150);
            }

            return Summarize(AudioRoundTripMode.Software, attempts, 0, AudioLatencyAnalysis.RoundTripAttempts);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Test de audio: ida y vuelta por software: {ex.Message}");
            return NotDetected(AudioRoundTripMode.Software);
        }
        finally
        {
            output.Dispose();
            loopback.Dispose();
        }
    }

    /// <summary>
    /// Cierra la fase de ida y vuelta. <paramref name="fired"/> es cuántos intentos se
    /// dispararon DE VERDAD: el camino físico puede cortar antes por la sonda de presencia, y
    /// informar 5 cuando se tiraron 2 sería mentir sobre la corrida.
    /// </summary>
    private static AudioRoundTripResult Summarize(AudioRoundTripMode mode, List<double> attempts, double levelDb, int fired)
    {
        if (attempts.Count == 0) return NotDetected(mode, fired);

        var (median, min, max) = AudioLatencyAnalysis.SummarizeAttempts(attempts.ToArray());
        return new AudioRoundTripResult(
            Detected: true,
            Mode: mode,
            Attempts: fired,
            DetectedAttempts: attempts.Count,
            MedianMs: median, MinMs: min, MaxMs: max,
            LevelDb: levelDb,
            Verdict: AudioLatencyAnalysis.RoundTripVerdict(mode, attempts.Count, median));
    }

    private static AudioRoundTripResult NotDetected(AudioRoundTripMode mode, int fired = 0)
        => new(false, mode, fired, 0, 0, 0, 0, -160,
            AudioRoundTripVerdict.NotDetected);

    // =====================================================================
    // Impulso y lectura
    // =====================================================================

    private static float[] BuildInterleavedImpulse(OpenStream stream)
    {
        int channels = Math.Max(1, (int)stream.Fmt.nChannels);
        var mono = WasapiAudio.BuildImpulse(stream.SampleRate);
        if (channels == 1) return mono;

        var interleaved = new float[mono.Length * channels];
        for (int i = 0; i < mono.Length; i++)
            for (int ch = 0; ch < channels; ch++)
                interleaved[i * channels + ch] = mono[i];
        return interleaved;
    }

    /// <summary>
    /// Escribe el impulso en la salida y devuelve el QPC en que su primera muestra sale del
    /// motor. Se calcula con el reloj del propio stream: la posición actual más lo que ya
    /// estaba encolado da el primer frame libre, y después se espera a que la posición
    /// alcance ese frame interpolando el QPC del reloj (precisión de microsegundos, no de
    /// milisegundos como si se midiera con el reloj del sistema).
    /// </summary>
    private static ulong WriteImpulse(OpenStream stream, float[] interleaved)
    {
        int channels = Math.Max(1, (int)stream.Fmt.nChannels);
        int frames = interleaved.Length / channels;
        var clock = stream.Clock;

        ulong position = 0, qpcAtWrite = 0;
        clock?.GetPosition(out position, out qpcAtWrite);
        stream.Client.GetCurrentPadding(out uint padding);
        ulong impulseFrame = position + padding;

        if (stream.Render.GetBuffer((uint)frames, out IntPtr buffer) < 0 || buffer == IntPtr.Zero)
            return qpcAtWrite;

        // Ceros primero: el búfer viene con lo último que haya quedado, y eso se oiría.
        // No hace falta limpiar el resto del búfer: solo se liberan las muestras del impulso,
        // así que lo que quede detrás nunca se reproduce.
        WasapiAudio.WriteSamples(buffer, interleaved, stream.Float32);
        stream.Render.ReleaseBuffer((uint)frames, 0);

        if (clock == null) return qpcAtWrite;

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 400)
        {
            if (clock.GetPosition(out ulong now, out ulong qpcNow) < 0) break;
            if (now >= impulseFrame)
            {
                // Interpolación hacia atrás desde la posición actual hasta el frame del impulso.
                ulong framesAhead = now - impulseFrame;
                ulong offset = framesAhead * 10_000_000UL / (ulong)Math.Max(1, stream.SampleRate);
                return qpcNow > offset ? qpcNow - offset : qpcNow;
            }
            Thread.Sleep(1);
        }
        return qpcAtWrite;
    }

    /// <summary>Descarta lo que haya pendiente en una captura: cada intento arranca limpio.</summary>
    private static void Drain(OpenStream input)
    {
        try
        {
            var capture = input.Capture;
            if (capture == null) return;
            for (int guard = 0; guard < 200; guard++)
            {
                if (capture.GetNextPacketSize(out uint pending) < 0 || pending == 0) return;
                if (capture.GetBuffer(out _, out uint frames, out _, out _, out _) < 0) return;
                capture.ReleaseBuffer(frames);
            }
        }
        catch { }
    }

    /// <summary>
    /// Lee la captura hasta encontrar el comienzo del impulso y devuelve el QPC de esa
    /// muestra (base 100 ns). Junta paquetes y los va mapeando para poder convertir el índice
    /// de la muestra detectada en el paquete al que pertenece: el QPC del paquete es el de su
    /// PRIMERA muestra, así que hay que sumarle la parte proporcional.
    /// </summary>
    private static ulong? ReadOnsetQpc(OpenStream input, int windowMs, CancellationToken ct)
    {
        int channels = Math.Max(1, (int)input.Fmt.nChannels);
        var capture = input.Capture!;
        var samples = new List<float>();
        var packets = new List<(ulong Qpc, int Index)>();
        var sw = Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < windowMs && !ct.IsCancellationRequested)
        {
            if (capture.GetNextPacketSize(out uint pending) < 0) return null;
            if (pending == 0) { Thread.Sleep(1); continue; }

            if (capture.GetBuffer(out IntPtr data, out uint frames, out _, out _, out ulong qpc) < 0) return null;
            packets.Add((qpc, samples.Count));
            samples.AddRange(AudioLatencyAnalysis.ToFloat(data, (int)frames * input.Fmt.nBlockAlign, input.Fmt.wBitsPerSample));
            capture.ReleaseBuffer(frames);
        }

        int onset = AudioLatencyAnalysis.DetectOnset(samples.ToArray());
        if (onset < 0) return null;

        for (int i = packets.Count - 1; i >= 0; i--)
        {
            if (packets[i].Index > onset) continue;
            double framesIn = (onset - packets[i].Index) / (double)channels;
            return packets[i].Qpc + (ulong)Math.Round(framesIn * 10_000_000 / Math.Max(1, input.SampleRate));
        }
        return null;
    }

    /// <summary>
    /// Igual que <see cref="ReadOnsetQpc"/> pero devolviendo MILISEGUNDOS medidos con el reloj
    /// del sistema desde la entrega del impulso: es lo único honesto en el loopback interno
    /// (ver MeasureRoundTripSoftware).
    /// </summary>
    private static double? ReadOnsetWallClock(OpenStream input, int windowMs, long submittedTicks, CancellationToken ct)
    {
        var capture = input.Capture!;
        var samples = new List<float>();
        var sw = Stopwatch.StartNew();
        double? arrivalMs = null;

        while (sw.ElapsedMilliseconds < windowMs && !ct.IsCancellationRequested)
        {
            if (capture.GetNextPacketSize(out uint pending) < 0) return null;
            if (pending == 0) { Thread.Sleep(1); continue; }

            if (capture.GetBuffer(out IntPtr data, out uint frames, out _, out _, out _) < 0) return null;
            samples.AddRange(AudioLatencyAnalysis.ToFloat(data, (int)frames * input.Fmt.nBlockAlign, input.Fmt.wBitsPerSample));
            capture.ReleaseBuffer(frames);

            // El reloj se toma en cuanto el paquete que contiene el comienzo del impulso se
            // puede leer: eso es "cuándo volvió".
            if (AudioLatencyAnalysis.DetectOnset(samples.ToArray()) >= 0)
            {
                arrivalMs = (Stopwatch.GetTimestamp() - submittedTicks) * 1000.0 / Stopwatch.Frequency;
                break;
            }
        }

        return arrivalMs;
    }

    // =====================================================================
    // Streams
    // =====================================================================

    private sealed class OpenStream : IDisposable
    {
        public WasapiAudio.IAudioClient Client = null!;
        public IntPtr Format;
        public WasapiAudio.WAVEFORMATEX Fmt;
        public bool Float32;
        public WasapiAudio.IAudioRenderClient? Render;
        public WasapiAudio.IAudioCaptureClient? Capture;
        public WasapiAudio.IAudioClock? Clock;
        public IntPtr EventHandle;   // evento de búfer (0 = el driver no lo dio: sin cadencia)
        public bool Started;

        public int SampleRate => (int)Fmt.nSamplesPerSec;

        public void Dispose()
        {
            try { if (Started) Client.Stop(); } catch { }
            try { if (Format != IntPtr.Zero) Marshal.FreeCoTaskMem(Format); } catch { }
            try { if (EventHandle != IntPtr.Zero) WasapiAudio.CloseHandle(EventHandle); } catch { }
            Format = IntPtr.Zero;
            EventHandle = IntPtr.Zero;
            Started = false;
        }
    }

    /// <summary>
    /// Abre un flujo compartido sobre un endpoint. <paramref name="loopback"/> activa la
    /// captura del propio endpoint de salida (lo que se reproduce, sin pasar por el hardware
    /// de entrada). <paramref name="eventCallback"/> pide el evento de búfer, que es lo que
    /// permite medir la cadencia del motor (si el driver no lo soporta, el stream queda igual
    /// de usable y la cadencia se informa como no disponible).
    /// </summary>
    private OpenStream? Open(WasapiAudio.IMMDevice device, bool loopback, bool eventCallback = false)
    {
        var client = WasapiAudio.ActivateClient(device);
        if (client == null) return null;

        if (client.GetMixFormat(out var format) < 0 || format == IntPtr.Zero) return null;
        if (!WasapiAudio.TryReadFormat(format, out var fmt))
        {
            Marshal.FreeCoTaskMem(format);
            return null;
        }

        uint flags = WasapiAudio.StreamFlagsNoPersist;
        if (loopback) flags |= WasapiAudio.StreamFlagsLoopback;
        if (eventCallback) flags |= WasapiAudio.StreamFlagsEventCallback;

        int hr = client.Initialize(WasapiAudio.ShareModeShared, flags, 0, 0, format, IntPtr.Zero);
        if (hr < 0)
        {
            Marshal.FreeCoTaskMem(format);
            return null;
        }

        var stream = new OpenStream
        {
            Client = client,
            Format = format,
            Fmt = fmt,
            Float32 = WasapiAudio.IsFloat32(format),
            // Un stream es de render o de captura: cuál de los dos interfaces se pide depende
            // del modo (en loopback se captura lo que se reproduce).
            Capture = loopback ? WasapiAudio.GetCaptureClient(client) : null,
            Render = loopback ? null : WasapiAudio.GetRenderClient(client),
            Clock = loopback ? null : WasapiAudio.GetClock(client)
        };

        if (eventCallback)
        {
            // Evento de búfer (auto-reset, sin nombre: privado del proceso). Si el driver no lo
            // acepta, el stream sigue sirviendo para la latencia y la cadencia queda en null.
            stream.EventHandle = WasapiAudio.CreateEventW(IntPtr.Zero, false, false, null);
            if (stream.EventHandle == IntPtr.Zero || client.SetEventHandle(stream.EventHandle) < 0)
            {
                if (stream.EventHandle != IntPtr.Zero) WasapiAudio.CloseHandle(stream.EventHandle);
                stream.EventHandle = IntPtr.Zero;
            }
        }
        return stream;
    }

}
