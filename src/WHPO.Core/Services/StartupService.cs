using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Registra o elimina WHPO del inicio de sesion del usuario actual.
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WHPO";
    private readonly ILoggingService _loggingService;

    public StartupService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("No se pudo comprobar el inicio automatico de WHPO.", ex);
            return false;
        }
    }

    public StartupOperationResult SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    return new StartupOperationResult(false, "No se pudo determinar la ruta del ejecutable de WHPO.");
                }

                key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
                _loggingService.LogInfo("Inicio automatico de WHPO activado.");
                return new StartupOperationResult(true, "WHPO se iniciara al iniciar sesion.");
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            _loggingService.LogInfo("Inicio automatico de WHPO desactivado.");
            return new StartupOperationResult(true, "WHPO ya no se iniciara automaticamente.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("No se pudo cambiar el inicio automatico de WHPO.", ex);
            return new StartupOperationResult(false, ex.Message);
        }
    }
}
