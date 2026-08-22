namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Snapshot de métricas para el overlay de juegos. Los valores son 0 cuando el
/// hardware no expone el sensor o no hay datos (la UI muestra "--").
/// </summary>
public sealed record OverlayMetrics(
    double CpuUsagePercent,
    double CpuTempCelsius,
    double CpuMhz,
    double CpuWatts,
    double GpuUsagePercent,
    double GpuTempCelsius,
    double GpuMhz,
    double GpuWatts,
    double RamPercent,
    double RamUsedMb,
    string RamConfig,
    double RamMhz,
    string CpuName,
    string GpuName,
    double Fps,
    double FpsLow1,
    double FpsLow01,
    int GamePid,
    string GameName,
    string GfxApi,
    bool FpsMonitorActive);

/// <summary>
/// Muestreador de métricas en vivo para el overlay: CPU/GPU (uso, temp, MHz, watts),
/// RAM y FPS del juego en primer plano. Corre su propio timer de fondo; la última
/// lectura se expone vía <see cref="Latest"/> (thread-safe).
/// </summary>
public interface IOverlayMetricsService
{
    /// <summary>¿El muestreo está corriendo?</summary>
    bool IsRunning { get; }

    /// <summary>Inicia el muestreo y la sesión ETW de FPS (idempotente).</summary>
    void Start();

    /// <summary>Detiene el muestreo y la sesión ETW.</summary>
    void Stop();

    /// <summary>Última lectura de métricas (null si el muestreo no arrancó).</summary>
    OverlayMetrics? Latest { get; }
}
