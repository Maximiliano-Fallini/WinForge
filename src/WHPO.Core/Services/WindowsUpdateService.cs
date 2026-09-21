using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementa perfiles de Windows Update equivalentes a los de herramientas de
/// mantenimiento de Windows, usando politicas locales, servicios y tareas del sistema.
/// </summary>
public sealed class WindowsUpdateService : IWindowsUpdateService
{
    private const string WindowsUpdatePolicyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string AutomaticUpdatesPolicyPath = WindowsUpdatePolicyPath + @"\AU";
    private const string DeviceMetadataPath = @"SOFTWARE\Policies\Microsoft\Windows\Device Metadata";
    private const string DriverSearchingPath = @"SOFTWARE\Policies\Microsoft\Windows\DriverSearching";
    private const string DeliveryOptimizationPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config";

    private static readonly string[] UpdateTaskPaths =
    [
        @"\Microsoft\Windows\InstallService\",
        @"\Microsoft\Windows\UpdateOrchestrator\",
        @"\Microsoft\Windows\UpdateAssistant\",
        @"\Microsoft\Windows\WaaSMedic\",
        @"\Microsoft\Windows\WindowsUpdate\",
        @"\Microsoft\WindowsUpdate\"
    ];

    private readonly ILoggingService _loggingService;

    public WindowsUpdateService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public WindowsUpdatePolicyState GetCurrentPolicy()
    {
        try
        {
            var updatesDisabled = GetDword(AutomaticUpdatesPolicyPath, "NoAutoUpdate") == 1;
            if (updatesDisabled)
            {
                return new WindowsUpdatePolicyState(
                    WindowsUpdateMode.Disabled,
                    "Actualizaciones desactivadas",
                    "Windows Update, sus servicios y tareas relacionadas están desactivados.");
            }

            var isRecommended =
                GetDword(WindowsUpdatePolicyPath, "ExcludeWUDriversInQualityUpdate") == 1 &&
                GetDword(WindowsUpdatePolicyPath, "DeferFeatureUpdatesPeriodInDays") == 365 &&
                GetDword(WindowsUpdatePolicyPath, "DeferQualityUpdatesPeriodInDays") == 4 &&
                GetDword(AutomaticUpdatesPolicyPath, "NoAutoRebootWithLoggedOnUsers") == 1;

            if (isRecommended)
            {
                return new WindowsUpdatePolicyState(
                    WindowsUpdateMode.Recommended,
                    "Modo recomendado",
                    "Actualizaciones de seguridad activas, control de reinicios y aplazamiento de versiones nuevas.");
            }

            var hasWhpoPolicy = GetDword(AutomaticUpdatesPolicyPath, "NoAutoUpdate") is not null ||
                                GetDword(WindowsUpdatePolicyPath, "DeferFeatureUpdatesPeriodInDays") is not null ||
                                GetDword(WindowsUpdatePolicyPath, "ExcludeWUDriversInQualityUpdate") is not null;

            return hasWhpoPolicy
                ? new WindowsUpdatePolicyState(WindowsUpdateMode.Custom, "Configuración personalizada", "Se detectaron políticas de Windows Update distintas de los tres perfiles de WHPO.")
                : new WindowsUpdatePolicyState(WindowsUpdateMode.Default, "Predeterminado de Windows", "Windows administra las actualizaciones con su configuración habitual.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("No se pudo detectar la politica actual de Windows Update.", ex);
            return new WindowsUpdatePolicyState(WindowsUpdateMode.Custom, "Estado no disponible", "No fue posible leer las políticas locales de Windows Update.");
        }
    }

    public Task<WindowsUpdatePolicyResult> ApplyPolicyAsync(WindowsUpdateMode mode)
    {
        if (mode == WindowsUpdateMode.Custom)
        {
            return Task.FromResult(new WindowsUpdatePolicyResult(false, "El modo personalizado no se puede aplicar."));
        }

        return Task.Run(() => ApplyPolicy(mode));
    }

    private WindowsUpdatePolicyResult ApplyPolicy(WindowsUpdateMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsUpdatePolicyResult(false, "Esta función solo está disponible en Windows.", RestartRecommended: false);
        }

        var warnings = new List<string>();
        try
        {
            switch (mode)
            {
                case WindowsUpdateMode.Default:
                    ApplyDefaultPolicy(warnings);
                    break;
                case WindowsUpdateMode.Recommended:
                    ApplyRecommendedPolicy(warnings);
                    break;
                case WindowsUpdateMode.Disabled:
                    ApplyDisabledPolicy(warnings);
                    break;
            }

            var title = mode switch
            {
                WindowsUpdateMode.Default => "Se restauraron los valores predeterminados de Windows Update.",
                WindowsUpdateMode.Recommended => "Se aplicó el perfil recomendado de Windows Update.",
                WindowsUpdateMode.Disabled => "Windows Update fue desactivado.",
                _ => ""
            };

            _loggingService.LogInfo($"Politica de Windows Update aplicada: {mode}.");
            var details = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
            return new WindowsUpdatePolicyResult(true, title, details);
        }
        catch (UnauthorizedAccessException ex)
        {
            _loggingService.LogError($"Permiso denegado al aplicar la politica de Windows Update {mode}.", ex);
            return new WindowsUpdatePolicyResult(false, "Se requieren permisos de administrador para cambiar Windows Update.", ex.Message, false);
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error al aplicar la politica de Windows Update {mode}.", ex);
            return new WindowsUpdatePolicyResult(false, "No se pudo completar el cambio de Windows Update.", ex.ToString(), false);
        }
    }

    private void ApplyDefaultPolicy(List<string> warnings)
    {
        RemoveValues(AutomaticUpdatesPolicyPath, "NoAutoUpdate", "AUOptions", "NoAutoRebootWithLoggedOnUsers", "AUPowerManagement");
        RemoveValues(WindowsUpdatePolicyPath, "ExcludeWUDriversInQualityUpdate", "DeferFeatureUpdates", "DeferFeatureUpdatesPeriodInDays", "DeferQualityUpdates", "DeferQualityUpdatesPeriodInDays");
        RemoveValues(DeviceMetadataPath, "PreventDeviceMetadataFromNetwork");
        RemoveValues(DriverSearchingPath, "DontPromptForWindowsUpdate", "DontSearchWindowsUpdate", "DriverUpdateWizardWuSearchEnabled");
        RemoveValues(DeliveryOptimizationPath, "DODownloadMode");

        ConfigureUpdateServices(disable: false, warnings);
        SetUpdateTasksEnabled(enabled: true, warnings);
    }

    private void ApplyRecommendedPolicy(List<string> warnings)
    {
        SetDword(DeviceMetadataPath, "PreventDeviceMetadataFromNetwork", 1);
        SetDword(DriverSearchingPath, "DontPromptForWindowsUpdate", 1);
        SetDword(DriverSearchingPath, "DontSearchWindowsUpdate", 1);
        SetDword(DriverSearchingPath, "DriverUpdateWizardWuSearchEnabled", 0);
        SetDword(WindowsUpdatePolicyPath, "ExcludeWUDriversInQualityUpdate", 1);
        SetDword(WindowsUpdatePolicyPath, "DeferFeatureUpdates", 1);
        SetDword(WindowsUpdatePolicyPath, "DeferFeatureUpdatesPeriodInDays", 365);
        SetDword(WindowsUpdatePolicyPath, "DeferQualityUpdates", 1);
        SetDword(WindowsUpdatePolicyPath, "DeferQualityUpdatesPeriodInDays", 4);
        SetDword(AutomaticUpdatesPolicyPath, "AUOptions", 4);
        SetDword(AutomaticUpdatesPolicyPath, "NoAutoRebootWithLoggedOnUsers", 1);
        SetDword(AutomaticUpdatesPolicyPath, "AUPowerManagement", 0);
        RemoveValues(AutomaticUpdatesPolicyPath, "NoAutoUpdate");
        RemoveValues(DeliveryOptimizationPath, "DODownloadMode");

        ConfigureUpdateServices(disable: false, warnings);
        SetUpdateTasksEnabled(enabled: true, warnings);
    }

    private void ApplyDisabledPolicy(List<string> warnings)
    {
        SetDword(AutomaticUpdatesPolicyPath, "NoAutoUpdate", 1);
        SetDword(AutomaticUpdatesPolicyPath, "AUOptions", 1);
        SetDword(DeliveryOptimizationPath, "DODownloadMode", 0);

        ConfigureUpdateServices(disable: true, warnings);
        ClearDownloadedUpdateCache(warnings);
        SetUpdateTasksEnabled(enabled: false, warnings);
    }

    private static int? GetDword(string path, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
        return key?.GetValue(valueName) is int value ? value : null;
    }

    private static void SetDword(string path, string valueName, int value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    private static void RemoveValues(string path, params string[] valueNames)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
        if (key is null) return;

        foreach (var valueName in valueNames)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private void ConfigureUpdateServices(bool disable, List<string> warnings)
    {
        if (disable)
        {
            foreach (var service in new[] { "BITS", "wuauserv", "UsoSvc" })
            {
                RunSystemCommand("sc.exe", $"stop {service}", warnings, allowFailure: true);
                RunSystemCommand("sc.exe", $"config {service} start= disabled", warnings, allowFailure: false);
            }
            return;
        }

        RunSystemCommand("sc.exe", "config BITS start= demand", warnings, allowFailure: false);
        RunSystemCommand("sc.exe", "config wuauserv start= demand", warnings, allowFailure: false);
        RunSystemCommand("sc.exe", "config UsoSvc start= auto", warnings, allowFailure: false);
        RunSystemCommand("sc.exe", "start UsoSvc", warnings, allowFailure: true);
    }

    private void SetUpdateTasksEnabled(bool enabled, List<string> warnings)
    {
        var action = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        var paths = string.Join(",", UpdateTaskPaths.Select(path => $"'{path}'"));
        var command = $"$paths=@({paths});foreach($path in $paths){{Get-ScheduledTask -TaskPath $path -ErrorAction SilentlyContinue | {action} -ErrorAction SilentlyContinue}}";
        RunSystemCommand("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command}\"", warnings, allowFailure: true);
    }

    private static void ClearDownloadedUpdateCache(List<string> warnings)
    {
        var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution");
        try
        {
            if (!Directory.Exists(cachePath)) return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(cachePath))
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"No se pudo limpiar la cache de actualizaciones: {ex.Message}");
        }
    }

    private static void RunSystemCommand(string fileName, string arguments, List<string> warnings, bool allowFailure)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });

        if (process is null)
        {
            warnings.Add($"No se pudo iniciar {fileName}.");
            return;
        }

        process.WaitForExit();
        if (process.ExitCode != 0 && !allowFailure)
        {
            var error = process.StandardError.ReadToEnd().Trim();
            warnings.Add($"{fileName} no completo correctamente: {error}");
        }
    }
}
