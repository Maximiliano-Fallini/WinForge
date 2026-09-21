namespace WHPO.Core.Services.Interfaces;

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
    // FPS máximo de la sesión del juego actual (se reinicia cuando cambia el juego
    // detectado). FpsMin = peor promedio de ~3 s del buffer de frametimes,
    // RECALCULADO en cada muestreo (no es un mínimo acumulado de sesión: cambia
    // con las condiciones actuales y captura los lagazos). 0 = sin datos todavía.
    double FpsMax,
    double FpsMin,
    // VRAM de la GPU principal: usada (contadores "GPU Adapter Memory") y total
    // (DXGI DedicatedVideoMemory, resuelto una vez). 0 = no disponible.
    double GpuMemUsedMb,
    double GpuVramTotalMb,
    // RAM física total (GlobalMemoryStatusEx).
    double RamTotalMb);

public interface IOverlayMetricsService
{
    bool IsRunning { get; }
    void Start();
    void Stop();
    OverlayMetrics? Latest { get; }
    string LaunchedTargetExecutable { get; }
    void RegisterLaunchedGame(string executable, string? installPath = null);
    string TargetMode { get; }
    string TargetExecutable { get; }
    FrametimeSample[] GetLiveFrametimes(int maxSamples);
}
