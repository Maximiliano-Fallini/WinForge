using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

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
        services.AddSingleton<IAutoClickerService, AutoClickerService>();

        return services;
    }
}
