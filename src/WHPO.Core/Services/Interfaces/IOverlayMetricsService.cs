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
    string GfxApi);

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
