using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Resultado de una corrida completa: lo medido durante la ventana. Sin corrida
/// anterior: el test no guarda historial.
/// </summary>
public sealed record InputLatencyTestRun(
    DateTimeOffset Timestamp,
    int WindowSeconds,
    List<InputLatencyTestResult> Results,
    bool Stopped);

/// <summary>
/// Test de latencia de entrada: una ventana acotada en la que se acumulan los
/// intervalos entre informes HID y se emite un VEREDICTO de regularidad.
///
/// Por qué existe, si el medidor en vivo ya muestra números: el medidor muestra, el
/// test juzga. Mide una ventana cerrada de UN dispositivo (por VID/PID, que sobrevive al
/// reinicio del dispositivo), así que se
/// puede correr en cualquier momento —antes de tocar nada, después de aplicar una
/// tasa, o días más tarde— sin depender de haber pasado por ningún flujo de
/// instalación.
///
/// Mide TODOS los dispositivos que emitan informes durante la ventana, sin selector:
/// un dispositivo que no se usa no aparece, y eso ya es una respuesta.
///
/// Qué NO mide: la latencia del juego, la del cable ni la del compositor de Windows.
/// Mide el intervalo entre informes (= 1/polling real). Y no puede distinguir un
/// informe perdido de uno que llegó tarde: por eso el veredicto habla de regularidad.
/// </summary>
public sealed class InputLatencyTestService
{
    /// <summary>Duración por defecto. 10 s a 125 Hz ya dan 1250 intervalos, y 80.000 a
    /// 8000 Hz (el anillo del monitor está dimensionado para eso). 0 = hasta detener.</summary>
    public const int DefaultDurationSeconds = 10;

    private const int ProgressTickMs = 200;

    private readonly InputLatencyMonitorService _monitor;
    private readonly ILoggingService _logging;

    public InputLatencyTestService(InputLatencyMonitorService monitor, ILoggingService logging)
    {
        _monitor = monitor;
        _logging = logging;
    }

    /// <summary>
    /// Corre la prueba sobre UN dispositivo: abre la captura, espera la ventana
    /// (reportando el segundo en curso) y cierra. Si se cancela, corta sin guardar nada y
    /// sin dejar la captura abierta.
    /// </summary>
    /// <param name="deviceKey">
    /// El VID/PID del dispositivo elegido, NO el handle crudo de Raw Input: el handle
    /// cambia cada vez que el periférico se reinicia (justo lo que pasa al aplicarle una
    /// tasa), así que la elección del usuario tiene que sobrevivir a eso.
    /// </param>
    /// <param name="durationSeconds">
    /// Cuánto dura la prueba. 0 o menos = HASTA DETENERLA a mano: el usuario decide cuándo
    /// termina (y en ese caso el resultado se calcula sobre lo medido hasta ahí).
    /// </param>
    public async Task<InputLatencyTestRun> RunAsync(string deviceKey, int durationSeconds, CancellationToken ct)
    {
        _monitor.Start();
        _monitor.BeginSampling();

        bool timed = durationSeconds > 0;
        bool stopped = false;
        List<(InputLatencyStats Stats, double[] Intervals)> captured;
        var sw = Stopwatch.StartNew();
        try
        {
            while (!timed || sw.Elapsed < TimeSpan.FromSeconds(durationSeconds))
                await Task.Delay(ProgressTickMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelar NO es un error: es "detener". Se devuelve lo medido y la UI decide
            // si lo acepta (modo hasta-detener) o lo descarta (abortar una duración fija).
            stopped = true;
        }
        finally
        {
            // Pase lo que pase la captura se cierra: cancelar no puede dejar al monitor
            // acumulando para siempre.
            captured = _monitor.EndSampling();
        }
        sw.Stop();

        var now = DateTimeOffset.Now;
        var results = new List<InputLatencyTestResult>();
        // Una misma clave (VID/PID) puede tener VARIAS entradas de Raw Input: un receptor expone
        // una colección de mouse y otra de teclado, y las dos dan el mismo Handle. Se mide la que
        // MÁS informes juntó en la ventana, que es la que el usuario estuvo usando: así los toques
        // de teclado no se cuelan como si fueran del mouse (y al revés).
        var candidates = captured
            .Where(c => string.Equals(c.Stats.Handle, deviceKey, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Intervals.Length)
            .ToList();
        if (candidates.Count > 1)
            _logging.LogDebug($"Test de latencia: {candidates.Count} entradas comparten {deviceKey}; " +
                $"se mide la de más informes ({candidates[0].Stats.Kind}, {candidates[0].Intervals.Length} intervalos).");
        if (candidates.Count > 0)
        {
            var (stats, intervals) = candidates[0];
            var result = InputLatencyAnalysis.Analyze(stats.Kind, stats.Name, stats.Handle, stats.DeviceId, intervals, now);
            if (result != null) results.Add(result);
        }

        // El test no guarda nada (no hay historial que comparar): se informa y listo.
        _logging.LogInfo($"Test de latencia: {sw.Elapsed.TotalSeconds:0.#} s sobre {deviceKey}" +
            (stopped ? " (detenida)" : "") + " → " +
            (results.Count == 0
                ? "sin informes durante la prueba."
                : $"{results[0].Hz:0} Hz, {results[0].GapPercent:0.##}% huecos, {results[0].Verdict}, {results[0].Samples} intervalos."));

        return new InputLatencyTestRun(
            now,
            Math.Max(1, (int)Math.Round(sw.Elapsed.TotalSeconds)),
            results,
            stopped);
    }
}
