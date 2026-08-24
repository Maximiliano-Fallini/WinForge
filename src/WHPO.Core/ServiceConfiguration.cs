using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using WHPO.Core.Services.Overlay;

namespace WHPO.Core;

/// <summary>
/// Configuración de servicios para inyección de dependencias.
/// </summary>
public static class ServiceConfiguration
{
    /// <summary>
    /// Registra todos los servicios de la aplicación en el contenedor DI.
    /// </summary>
    /// <param name="services">Colección de servicios DI.</param>
    /// <param name="settingsDirectory">Directorio para archivos de configuración.</param>
    public static IServiceCollection AddWHPOServices(this IServiceCollection services, string settingsDirectory)
    {
        // Logging
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Servicios de aplicación
        services.AddSingleton<ILoggingService, LoggingService>();
        services.AddSingleton<ISettingsService>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggingService>();
            return new SettingsService(logger, settingsDirectory);
        });
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISystemInfoService, SystemInfoService>();
        services.AddSingleton<ISensorService, SensorService>();
        services.AddSingleton<ICpuPowerService, CpuPowerService>();
        services.AddSingleton<INetworkService, NetworkService>();
        services.AddSingleton<IMemoryService, MemoryService>();
        services.AddSingleton<ITweakService, TweakService>();
        services.AddSingleton<IRepairService, RepairService>();
        services.AddSingleton<IWindowsUpdateService, WindowsUpdateService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<IWinUtilService, WinUtilService>();
        services.AddSingleton<IStabilityService, StabilityService>();
        services.AddSingleton<IKeyboardService, KeyboardService>();
        services.AddSingleton<IMacroService, MacroService>();
        services.AddSingleton<IAutoClickerService, AutoClickerService>();
        services.AddSingleton<IProcessService, ProcessService>();
        services.AddSingleton<IInstalledGamesService, InstalledGamesService>();
        services.AddSingleton<IGameBoostService, GameBoostService>();
        services.AddSingleton<IAppUpdateService, AppUpdateService>();
        services.AddSingleton<IPostUpdateRestartService, PostUpdateRestartService>();
        services.AddSingleton<ICleanupService, CleanupService>();
        services.AddSingleton<IDuplicateFinderService, DuplicateFinderService>();
        services.AddSingleton<IDriveWatcherService, DriveWatcherService>();
        services.AddSingleton<IStartupManagerService, StartupManagerService>();

        // Overlay de métricas de juegos (FPS por ETW + muestreo de hardware)
        services.AddSingleton<IFpsMonitor, FpsMonitor>();
        services.AddSingleton<IOverlayMetricsService, OverlayMetricsService>();

        return services;
    }
}
