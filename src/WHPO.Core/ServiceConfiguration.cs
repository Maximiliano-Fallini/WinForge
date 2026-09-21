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
        // Paquetes de idioma (catálogo + descarga de packs). Se registra acá y la UI
        // lo inicializa al arrancar: sin eso el motor de traducciones solo conocía los
        // idiomas embebidos y el selector no veía los packs descargados.
        services.AddSingleton<LanguagePackService>();
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
        services.AddSingleton<BusCadenceService>();
        services.AddSingleton<IInstalledGamesService, InstalledGamesService>();
        services.AddSingleton<IGameBoostService, GameBoostService>();
        services.AddSingleton<IAppUpdateService, AppUpdateService>();
        services.AddSingleton<IPostUpdateRestartService, PostUpdateRestartService>();
        services.AddSingleton<ICleanupService, CleanupService>();
        services.AddSingleton<IDuplicateFinderService, DuplicateFinderService>();
        services.AddSingleton<IDriveWatcherService, DriveWatcherService>();
        services.AddSingleton<IStartupManagerService, StartupManagerService>();
        services.AddSingleton<IUsbOverclockService, UsbOverclockService>();
        // Medidor de latencia de entrada (Raw Input): intervalo real entre informes HID
        // de mouse/teclado/mandos — el "polling tester" nativo de la pestaña Overclock USB.
        services.AddSingleton<InputLatencyMonitorService>();
        // Test de latencia: ventana acotada sobre el monitor y veredicto de regularidad.
        services.AddSingleton<InputLatencyTestService>();
        // Test de latencia de AUDIO (WASAPI): motor + ida y vuelta por endpoint.
        services.AddSingleton<AudioLatencyService>();
        // Limpiador de registro (Limpieza personalizada): escaneo, backup .reg y limpieza HKCU.
        services.AddSingleton<IRegistryCleanerService, RegistryCleanerService>();
        // Salud del Modo Juego de Windows: chequeo de que se vaya a activar, reparación
        // del caso "solo deshabilitado" y activación por partida con snapshot.
        services.AddSingleton<IWindowsGameModeHealthService, WindowsGameModeHealthService>();
        // Control de ventiladores (SuperIO vía driver PawnIO + LibreHardwareMonitor).
        services.AddSingleton<IFanControlService, FanControlService>();

        // Overlay de métricas de juegos (FPS por ETW + muestreo de hardware)
        services.AddSingleton<IFpsMonitor, FpsMonitor>();
        services.AddSingleton<IOverlayMetricsService, OverlayMetricsService>();

        return services;
    }
}
