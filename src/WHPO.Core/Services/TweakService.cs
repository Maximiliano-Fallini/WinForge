using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación del servicio de tweaks del sistema.
/// Aplica y revierte modificaciones al registro, servicios y configuraciones de Windows.
/// Basado en los tweaks de Christitus WinUtil (https://github.com/christitustech/winutil)
/// </summary>
public class TweakService : ITweakService
{
    private readonly ILoggingService _loggingService;
    private readonly Dictionary<string, TweakDefinition> _tweaks;

    // Progreso "ambient" para reportar los comandos reales al ejecutar un tweak
    // (estilo cmd/winutil). Se setea al inicio de Apply/Revert y se limpia al final;
    // los helpers de registro/comandos reportan a él si está activo.
    private IProgress<string>? _progress;

    public event Action<string, bool>? TweakStateChanged;

    public TweakService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
        _tweaks = BuildTweaksDictionary();
        _loggingService.LogInfo($"TweakService inicializado con {_tweaks.Count} tweaks de Christitus WinUtil");
    }

    public List<TweakDefinition> GetAllTweaks() => new(_tweaks.Values);

    public bool IsTweakApplied(string tweakId)
    {
        if (!_tweaks.TryGetValue(tweakId, out var tweak)) return false;
        try
        {
            return tweak.CheckApplied();
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error verificando estado de tweak {tweakId}", ex);
            return false;
        }
    }

    public async Task<TweakResult> ApplyTweakAsync(string tweakId, IProgress<string>? progress = null)
    {
        if (!_tweaks.TryGetValue(tweakId, out var tweak))
        {
            return new TweakResult(false, $"Tweak no encontrado: {tweakId}");
        }

        _progress = progress;
        try
        {
            _loggingService.LogInfo($"Aplicando tweak: {tweak.Name}");
            var result = await tweak.ApplyAction();

            if (result.Success)
            {
                _loggingService.LogInfo($"Tweak aplicado correctamente: {tweak.Name}");
                TweakStateChanged?.Invoke(tweakId, true);
            }
            else
            {
                _loggingService.LogWarning($"Tweak falló: {tweak.Name} - {result.Message}");
            }

            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error aplicando tweak {tweakId}", ex);
            return new TweakResult(false, ex.Message);
        }
        finally
        {
            _progress = null;
        }
    }

    public async Task<TweakResult> RevertTweakAsync(string tweakId, IProgress<string>? progress = null)
    {
        if (!_tweaks.TryGetValue(tweakId, out var tweak))
        {
            return new TweakResult(false, $"Tweak no encontrado: {tweakId}");
        }

        if (!tweak.IsReversible)
        {
            return new TweakResult(false, "Este tweak no es reversible.");
        }

        _progress = progress;
        try
        {
            _loggingService.LogInfo($"Revirtiendo tweak: {tweak.Name}");
            var result = await tweak.RevertAction();

            if (result.Success)
            {
                _loggingService.LogInfo($"Tweak revertido correctamente: {tweak.Name}");
                TweakStateChanged?.Invoke(tweakId, false);
            }
            else
            {
                _loggingService.LogWarning($"Reversión falló: {tweak.Name} - {result.Message}");
            }

            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error revirtiendo tweak {tweakId}", ex);
            return new TweakResult(false, ex.Message);
        }
        finally
        {
            _progress = null;
        }
    }

    // ====== Utilidades ======

    private void Report(string message) => _progress?.Report(message);

    private static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM:",
        RegistryHive.CurrentUser => "HKCU:",
        _ => hive.ToString()
    };

    private static string FormatValue(object value) => value switch
    {
        string s => $"\"{s}\"",
        byte[] b => $"0x{BitConverter.ToString(b).Replace("-", "")}",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };

    /// <summary>
    /// Reporta el comando en estilo cmd. Los scripts largos de PowerShell se recortan
    /// para que la línea de la consola siga siendo legible.
    /// </summary>
    private void ReportCommand(string command, string args)
    {
        if (_progress == null) return;
        const int maxLen = 160;
        var display = args.Length > maxLen ? args[..maxLen] + " …" : args;
        Report($"> {command} {display}".TrimEnd());
    }

    private async Task<TweakResult> RunCommandAsync(string command, string args = "")
    {
        try
        {
            // IMPORTANTE: NO usar Verb="runas" con UseShellExecute=false: Process.Start lanza
            // InvalidOperationException y TODOS los tweaks fallarían. La app ya corre elevada
            // (app.manifest: requireAdministrator), así que la elevación no es necesaria.
            // PowerShell se resuelve al nativo (Sysnative) para que la app x86 no use el stub de 32 bits.
            var executable = command.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(RepairService.NativeSystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                : command;

            ReportCommand(command, args);

            // Sin -NoProfile el perfil de PowerShell del usuario puede cambiar el exit code
            // (ej. $ErrorActionPreference='Stop' convierte errores no terminantes en fatales).
            var effectiveArgs = command.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                ? "-NoProfile -NonInteractive " + args
                : args;

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = effectiveArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new TweakResult(false, "No se pudo iniciar el proceso.");

            // Leer ambas salidas en paralelo para evitar deadlock si el buffer se llena
            // (ej: Get-AppxPackage -AllUsers devuelve mucho texto).
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
                return new TweakResult(false, $"Exit code {process.ExitCode}: {error}");

            return new TweakResult(true, string.IsNullOrEmpty(output) ? "OK" : output.Trim());
        }
        catch (Exception ex)
        {
            return new TweakResult(false, ex.Message);
        }
    }

    private static RegistryKey? GetBaseKey(RegistryHive hive)
    {
        // Usar la vista de 64 bits EXPLÍCITA: la app es x86 y, si no, Registry.LocalMachine
        // se abre en la vista 32 bits (Wow6432Node) y los tweaks escribirían en el hive equivocado.
        return hive switch
        {
            RegistryHive.LocalMachine => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64),
            RegistryHive.CurrentUser => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64),
            _ => null
        };
    }

    private static bool CheckRegistryValue(RegistryHive hive, string path, string name, object expectedValue)
    {
        try
        {
            var baseKey = GetBaseKey(hive);
            if (baseKey == null) return false;
            using var key = baseKey.OpenSubKey(path);
            if (key == null) return false;
            var value = key.GetValue(name);
            if (value == null) return false;
            return value.Equals(expectedValue);
        }
        catch { return false; }
    }

    private TweakResult SetRegistryValue(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        Report($"Set-ItemProperty -Path \"{HiveName(hive)}{path}\" -Name \"{name}\" -Value {FormatValue(value)} -Type {kind}");
        try
        {
            var baseKey = GetBaseKey(hive);
            if (baseKey == null)
                return new TweakResult(false, $"Hive no soportado: {hive}");
            using var key = baseKey.CreateSubKey(path);
            if (key == null)
                return new TweakResult(false, $"No se pudo abrir/crear la clave: {path}");
            key.SetValue(name, value, kind);
            return new TweakResult(true, "OK");
        }
        catch (Exception ex)
        {
            return new TweakResult(false, ex.Message);
        }
    }

    private TweakResult RemoveRegistryValue(RegistryHive hive, string path, string name)
    {
        Report($"Remove-ItemProperty -Path \"{HiveName(hive)}{path}\" -Name \"{name}\"");
        try
        {
            var baseKey = GetBaseKey(hive);
            if (baseKey == null)
                return new TweakResult(false, $"Hive no soportado: {hive}");
            using var key = baseKey.OpenSubKey(path, true);
            if (key == null)
                return new TweakResult(true, "La clave no existe, nada que revertir.");
            if (key.GetValue(name) == null)
                return new TweakResult(true, "El valor no existe, nada que revertir.");
            key.DeleteValue(name);
            return new TweakResult(true, "OK");
        }
        catch (Exception ex)
        {
            return new TweakResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Aplica múltiples valores de registro en una sola operación.
    /// </summary>
    private TweakResult SetMultipleRegistryValues(params (RegistryHive hive, string path, string name, object value, RegistryValueKind kind)[] entries)
    {
        foreach (var (hive, path, name, value, kind) in entries)
        {
            var result = SetRegistryValue(hive, path, name, value, kind);
            if (!result.Success) return result;
        }
        return new TweakResult(true, "OK");
    }

    /// <summary>
    /// Elimina múltiples valores de registro en una sola operación.
    /// </summary>
    private TweakResult RemoveMultipleRegistryValues(params (RegistryHive hive, string path, string name)[] entries)
    {
        foreach (var (hive, path, name) in entries)
        {
            RemoveRegistryValue(hive, path, name);
        }
        return new TweakResult(true, "OK");
    }

    /// <summary>
    /// Verifica si al menos uno de varios valores de registro coincide.
    /// </summary>
    private static bool CheckAnyRegistryValue(params (RegistryHive hive, string path, string name, object value)[] entries)
    {
        foreach (var (hive, path, name, value) in entries)
        {
            if (CheckRegistryValue(hive, path, name, value)) return true;
        }
        return false;
    }

    /// <summary>
    /// Detecta si store.db tiene el deny de Everyone (S-1-1-0) que aplica el tweak.
    /// Lee el ACL directo (sin spawn de procesos) y por SID (independiente del idioma).
    /// </summary>
    private static bool IsStoreSearchBlocked()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalState\store.db");
            if (!File.Exists(path)) return false;

            var acl = new FileInfo(path).GetAccessControl();
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Deny
                    && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl
                    && rule.IdentityReference.Value == "S-1-1-0")
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ====== Diccionario de Tweaks (Solo Christitus WinUtil) ======

    private Dictionary<string, TweakDefinition> BuildTweaksDictionary()
    {
        var dict = new Dictionary<string, TweakDefinition>();

        // ===== ESSENTIAL TWEAKS =====

        AddTweak(dict, "WPFTweaksActivity", "Historial de actividad - Desactivar",
            "Borra documentos recientes, portapapeles e historial de ejecución.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => CheckAnyRegistryValue(
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0)),
            () => Task.FromResult(SetMultipleRegistryValues(
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, RegistryValueKind.DWord))),
            () => Task.FromResult(RemoveMultipleRegistryValues(
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities"))));

        AddTweak(dict, "WPFTweaksHiber", "Hibernación - Desactivar",
            "La hibernación está pensada para portátiles. Realmente nunca debería usarse en escritorios.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"System\CurrentControlSet\Control\Session Manager\Power", "HibernateEnabled", 0),
            () => Task.Run(() =>
            {
                var regResult = SetMultipleRegistryValues(
                    (RegistryHive.LocalMachine, @"System\CurrentControlSet\Control\Session Manager\Power", "HibernateEnabled", 0, RegistryValueKind.DWord),
                    (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings", "ShowHibernateOption", 0, RegistryValueKind.DWord));
                if (!regResult.Success) return regResult;
                return RunCommandAsync("powercfg.exe", "/hibernate off").Result;
            }),
            () => Task.Run(() =>
            {
                SetMultipleRegistryValues(
                    (RegistryHive.LocalMachine, @"System\CurrentControlSet\Control\Session Manager\Power", "HibernateEnabled", 1, RegistryValueKind.DWord),
                    (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings", "ShowHibernateOption", 1, RegistryValueKind.DWord));
                return RunCommandAsync("powercfg.exe", "/hibernate on").Result;
            }));

        AddTweak(dict, "WPFTweaksWidget", "Widgets - Quitar",
            "Elimina los molestos widgets en la parte inferior izquierda de la barra de tareas.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => false,
            () => RunCommandAsync("powershell", "-Command \"Get-Process *Widget* | Stop-Process -Force; Get-AppxPackage Microsoft.WidgetsPlatformRuntime -AllUsers | Remove-AppxPackage -AllUsers; Get-AppxPackage MicrosoftWindows.Client.WebExperience -AllUsers | Remove-AppxPackage -AllUsers\""),
            () => RunCommandAsync("powershell", "-Command \"Get-AppxPackage -AllUsers Microsoft.WidgetsPlatformRuntime | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register $($_.InstallLocation)\\AppXManifest.xml}; Get-AppxPackage -AllUsers MicrosoftWindows.Client.WebExperience | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register $($_.InstallLocation)\\AppXManifest.xml}\""));

        AddTweak(dict, "WPFTweaksRevertStartMenu", "Diseño anterior del menú Inicio - Activar",
            "Restaura el diseño antiguo del menú Inicio anterior al despliegue gradual del nuevo en 25H2. En versiones nuevas de Windows no funcionará.",
            "Compatible con Windows 11 25H2", true, "Essential Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\ControlSet001\Control\FeatureManagement\Overrides\8\3036241548", "EnabledState", 1),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\ControlSet001\Control\FeatureManagement\Overrides\8\3036241548", "EnabledState", 1, RegistryValueKind.DWord)),
            () => Task.FromResult(RemoveRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\ControlSet001\Control\FeatureManagement\Overrides\8\3036241548", "EnabledState")));

        AddTweak(dict, "WPFTweaksDisableStoreSearch", "Resultados recomendados de Microsoft Store - Desactivar",
            "No mostrará apps recomendadas de Microsoft Store al buscar en el menú Inicio.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", false,
            () => IsStoreSearchBlocked(),
            () => RunCommandAsync("powershell", "-Command \"icacls \\\"$Env:LocalAppData\\Packages\\Microsoft.WindowsStore_8wekyb3d8bbwe\\LocalState\\store.db\\\" /deny *S-1-1-0:F\""),
            () => RunCommandAsync("powershell", "-Command \"icacls \\\"$Env:LocalAppData\\Packages\\Microsoft.WindowsStore_8wekyb3d8bbwe\\LocalState\\store.db\\\" /grant *S-1-1-0:F\""));

        AddTweak(dict, "WPFTweaksLocation", "Seguimiento de ubicación - Desactivar",
            "Desactiva el seguimiento de ubicación.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => CheckAnyRegistryValue(
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Overrides\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}", "SensorPermissionState", 0),
                (RegistryHive.LocalMachine, @"SYSTEM\Maps", "AutoUpdateEnabled", 0)),
            () => Task.FromResult(SetMultipleRegistryValues(
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny", RegistryValueKind.String),
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Overrides\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}", "SensorPermissionState", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SYSTEM\Maps", "AutoUpdateEnabled", 0, RegistryValueKind.DWord))),
            () => Task.FromResult(RemoveMultipleRegistryValues(
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Overrides\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}", "SensorPermissionState"),
                (RegistryHive.LocalMachine, @"SYSTEM\Maps", "AutoUpdateEnabled"))));

        AddTweak(dict, "WPFTweaksServices", "Servicios - Configurar en Manual",
            "Configura algunos servicios en Manual y ajusta SvcHostSplitThresholdInKB para reducir significativamente la cantidad de procesos svchost.exe.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => false,
            () => Task.Run(() =>
            {
                var regResult = SetRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "SvcHostSplitThresholdInKB", GetTotalMemoryKB(), RegistryValueKind.DWord);
                if (!regResult.Success) return regResult;
                return RunCommandAsync("powershell", "-Command \"Set-Service -Name CscService -StartupType Disabled; Set-Service -Name DiagTrack -StartupType Disabled; Set-Service -Name MapsBroker -StartupType Manual; Set-Service -Name StorSvc -StartupType Manual; Set-Service -Name SharedAccess -StartupType Disabled\"").Result;
            }),
            () => Task.Run(() =>
            {
                SetRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "SvcHostSplitThresholdInKB", 384000, RegistryValueKind.DWord);
                return RunCommandAsync("powershell", "-Command \"Set-Service -Name CscService -StartupType Manual; Set-Service -Name DiagTrack -StartupType Automatic; Set-Service -Name MapsBroker -StartupType Automatic; Set-Service -Name StorSvc -StartupType Automatic; Set-Service -Name SharedAccess -StartupType Automatic\"").Result;
            }));

        AddTweak(dict, "WPFTweaksBraveDebloat", "Brave Browser - Desbloat",
            "Desactiva varias molestias como Brave Rewards, Leo AI, Crypto Wallet y VPN.",
            "Requiere Brave Browser instalado", true, "Advanced Tweaks", false,
            () => false,
            () => RunCommandAsync("powershell", "-Command \"$regPath = 'HKLM:\\SOFTWARE\\Policies\\BraveSoftware\\Brave'; if (-not (Test-Path $regPath)) { New-Item -Path $regPath -Force }; Set-ItemProperty -Path $regPath -Name BraveRewardsDisabled -Value 1 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name BraveWalletDisabled -Value 1 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name BraveVPNDisabled -Value 1 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name BraveAIChatEnabled -Value 0 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name BraveStatsPingEnabled -Value 0 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name BraveNewsDisabled -Value 1 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name BraveTalkDisabled -Value 1 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name TorDisabled -Value 1 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name BraveP3AEnabled -Value 0 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name UrlKeyedAnonymizedDataCollectionEnabled -Value 0 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name SafeBrowsingExtendedReportingEnabled -Value 0 -Type DWord -Force; Set-ItemProperty -Path $regPath -Name MetricsReportingEnabled -Value 0 -Type DWord -Force; Write-Output 'Brave debloat aplicado'\""),
            () => RunCommandAsync("powershell", "-Command \"Remove-Item -Path 'HKLM:\\SOFTWARE\\Policies\\BraveSoftware\\Brave' -Recurse -Force -ErrorAction SilentlyContinue; Write-Output 'Brave debloat revertido'\""));

        AddTweak(dict, "WPFTweaksDisableWarningForUnsignedRdp", "Advertencias de archivos RDP sin firmar - Desactivar",
            "Desactiva las advertencias al lanzar archivos RDP sin firmar introducidas en las últimas actualizaciones.",
            "Compatible con Windows 10/11", true, "Advanced Tweaks", false,
            () => CheckAnyRegistryValue(
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\Client", "RedirectionWarningDialogVersion", 1),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Terminal Server Client", "RdpLaunchConsentAccepted", 1)),
            () => Task.FromResult(SetMultipleRegistryValues(
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\Client", "RedirectionWarningDialogVersion", 1, RegistryValueKind.DWord),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Terminal Server Client", "RdpLaunchConsentAccepted", 1, RegistryValueKind.DWord))),
            () => Task.FromResult(RemoveMultipleRegistryValues(
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\Client", "RedirectionWarningDialogVersion"),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Terminal Server Client", "RdpLaunchConsentAccepted"))));

        AddTweak(dict, "WPFTweaksEdgeDebloat", "Microsoft Edge - Desbloat",
            "Desactiva varias opciones de telemetría, popups y otras molestias en Edge.",
            "Requiere Microsoft Edge instalado", true, "Advanced Tweaks", false,
            () => CheckAnyRegistryValue(
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\EdgeUpdate", "CreateDesktopShortcutDefault", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "PersonalizationReportingEnabled", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "ShowRecommendationsEnabled", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "HideFirstRunExperience", 1),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "UserFeedbackAllowed", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "ConfigureDoNotTrack", 1),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "AlternateErrorPagesEnabled", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeCollectionsEnabled", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeShoppingAssistantEnabled", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "MicrosoftEdgeInsiderPromotionEnabled", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "ShowMicrosoftRewards", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "WebWidgetAllowed", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "DiagnosticData", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeAssetDeliveryServiceEnabled", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "WalletDonationEnabled", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "DefaultBrowserSettingsCampaignEnabled", 0)),
            () => Task.FromResult(SetMultipleRegistryValues(
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\EdgeUpdate", "CreateDesktopShortcutDefault", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "PersonalizationReportingEnabled", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "ShowRecommendationsEnabled", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "HideFirstRunExperience", 1, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "UserFeedbackAllowed", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "ConfigureDoNotTrack", 1, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "AlternateErrorPagesEnabled", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeCollectionsEnabled", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeShoppingAssistantEnabled", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "MicrosoftEdgeInsiderPromotionEnabled", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "ShowMicrosoftRewards", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "WebWidgetAllowed", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "DiagnosticData", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeAssetDeliveryServiceEnabled", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "WalletDonationEnabled", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "DefaultBrowserSettingsCampaignEnabled", 0, RegistryValueKind.DWord),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallBlocklist", "1", "ofefcgjbeghpigppfmkologfjadafddi", RegistryValueKind.String))),
            () => Task.FromResult(RemoveMultipleRegistryValues(
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\EdgeUpdate", "CreateDesktopShortcutDefault"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "PersonalizationReportingEnabled"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "ShowRecommendationsEnabled"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "HideFirstRunExperience"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "UserFeedbackAllowed"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "ConfigureDoNotTrack"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "AlternateErrorPagesEnabled"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeCollectionsEnabled"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeShoppingAssistantEnabled"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "MicrosoftEdgeInsiderPromotionEnabled"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "ShowMicrosoftRewards"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "WebWidgetAllowed"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "DiagnosticData"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeAssetDeliveryServiceEnabled"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "WalletDonationEnabled"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "DefaultBrowserSettingsCampaignEnabled"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "ExtensionInstallBlocklist"))));

        AddTweak(dict, "WPFTweaksConsumerFeatures", "ConsumerFeatures - Desactivar",
            "Detiene instalaciones promocionadas de apps y reduce sugerencias de Microsoft Store.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, RegistryValueKind.DWord)),
            () => Task.FromResult(RemoveRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures")));

        AddTweak(dict, "WPFTweaksTelemetry", "Telemetría - Desactivar",
            "Desactiva la telemetría de Microsoft.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => CheckAnyRegistryValue(
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Input\TIPC", "Enabled", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
                (RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
                (RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)),
            () => Task.Run(() =>
            {
                var regResult = SetMultipleRegistryValues(
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Input\TIPC", "Enabled", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0, RegistryValueKind.DWord),
                    (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", 0, RegistryValueKind.DWord),
                    (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, RegistryValueKind.DWord));
                if (!regResult.Success) return regResult;
                return RunCommandAsync("powershell", "-Command \"Set-MpPreference -SubmitSamplesConsent 2; Set-Service -Name diagtrack -StartupType Disabled; Set-Service -Name wermgr -StartupType Disabled; [Environment]::SetEnvironmentVariable('POWERSHELL_TELEMETRY_OPTOUT', '1', 'Machine'); Remove-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Siuf\\Rules' -Name PeriodInNanoSeconds -ErrorAction SilentlyContinue\"").Result;
            }),
            () => Task.Run(() =>
            {
                RemoveMultipleRegistryValues(
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Input\TIPC", "Enabled"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy"),
                    (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs"),
                    (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod"));
                return RunCommandAsync("powershell", "-Command \"Set-MpPreference -SubmitSamplesConsent 1; Set-Service -Name diagtrack -StartupType Automatic; Set-Service -Name wermgr -StartupType Automatic; [Environment]::SetEnvironmentVariable('POWERSHELL_TELEMETRY_OPTOUT', '', 'Machine')\"").Result;
            }));

        AddTweak(dict, "WPFTweaksDeliveryOptimization", "Optimización de entrega - Desactivar",
            "Evita que Windows use tu ancho de banda para subir actualizaciones a otros equipos en internet o red local.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, RegistryValueKind.DWord)),
            () => Task.FromResult(RemoveRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode")));

        AddTweak(dict, "WPFTweaksRemoveEdge", "Microsoft Edge - Eliminar",
            "Desinstala Microsoft Edge creando un archivo dummy MicrosoftEdge.exe que engaña al desinstalador oficial para una eliminación a nivel de sistema.",
            "Requiere precaución", true, "Advanced Tweaks", true,
            () => false,
            () => RunCommandAsync("powershell", "-Command \"$Path = Resolve-Path -Path \\\"$Env:ProgramFiles (x86)\\Microsoft\\Edge\\Application\\*\\Installer\\setup.exe\\\" | Select-Object -Last 1; if (Test-Path $Path) { New-Item -Path \\\"$Env:SystemRoot\\SystemApps\\Microsoft.MicrosoftEdge_8wekyb3d8bbwe\\MicrosoftEdge.exe\\\" -Force; Start-Process -FilePath $Path -ArgumentList '--uninstall --system-level --force-uninstall --delete-profile' -Wait; Write-Output 'Microsoft Edge fue eliminado' } else { Write-Output 'Microsoft Edge no está instalado' }\""),
            () => RunCommandAsync("powershell", "-Command \"winget install Microsoft.Edge --source winget --silent\""));

        AddTweak(dict, "WPFTweaksDisableBitLocker", "BitLocker - Desactivar",
            "Desactiva BitLocker.",
            "Solo si no usas cifrado de disco", true, "Essential Tweaks", true,
            () => false,
            () => RunCommandAsync("powershell", "-Command \"Disable-BitLocker -MountPoint $Env:SystemDrive\""),
            () => RunCommandAsync("powershell", "-Command \"Enable-BitLocker -MountPoint $Env:SystemDrive\""));

        AddTweak(dict, "WPFTweaksUTC", "Fecha y hora - Configurar en UTC",
            "Esencial para equipos con dual-boot. Corrige la sincronización horaria con sistemas Linux.",
            "Solo dual-boot con Linux", true, "Advanced Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "RealTimeIsUniversal", 1),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "RealTimeIsUniversal", 1, RegistryValueKind.QWord)),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "RealTimeIsUniversal", 0, RegistryValueKind.QWord)));

        AddTweak(dict, "WPFTweaksRemoveOneDrive", "Microsoft OneDrive - Eliminar",
            "Deniega permisos para eliminar archivos de usuario de OneDrive, usa su desinstalador para quitarlo y restaura los permisos.",
            "Requiere precaución", true, "Advanced Tweaks", true,
            // "Aplicado" = OneDrive ya no está instalado: el exe por usuario es el marcador
            // definitivo (el desinstalador OneDriveSetup.exe del System32 queda aunque se quite).
            () => !File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\OneDrive\OneDrive.exe")),
            () => RunCommandAsync("powershell", "-Command \"icacls $Env:OneDrive /deny '*S-1-5-32-544:(D,DC)'; Start-Process -FilePath (Join-Path $Env:SystemRoot 'System32\\OneDriveSetup.exe') -ArgumentList '/uninstall' -Wait; Stop-Process -Name FileCoAuth,Explorer -ErrorAction SilentlyContinue; Remove-Item \\\"$Env:LocalAppData\\Microsoft\\OneDrive\\\" -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item \\\"$Env:ProgramData\\Microsoft OneDrive\\\" -Recurse -Force -ErrorAction SilentlyContinue; icacls $Env:OneDrive /grant '*S-1-5-32-544:(D,DC)'; if (-not (Get-ChildItem -Path $Env:OneDrive)) { Remove-Item -Path $Env:OneDrive -Recurse -Force; [Environment]::SetEnvironmentVariable('OneDrive', $null, 'User') }; Set-Service -Name OneSyncSvc -StartupType Disabled\""),
            () => RunCommandAsync("powershell", "-Command \"winget install Microsoft.Onedrive --source winget --silent; Set-Service -Name OneSyncSvc -StartupType Automatic\""));

        AddTweak(dict, "WPFTweaksRemoveHomeAndGallery", "Inicio y Galería del Explorador - Desactivar",
            "Elimina Inicio y Galería del Explorador y establece Este PC como predeterminado.",
            "Compatible con Windows 11", true, "Advanced Tweaks", false,
            () => CheckAnyRegistryValue(
                (RegistryHive.CurrentUser, @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", "System.IsPinnedToNameSpaceTree", 0),
                (RegistryHive.CurrentUser, @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}", "System.IsPinnedToNameSpaceTree", 0),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 1)),
            () => Task.FromResult(SetMultipleRegistryValues(
                (RegistryHive.CurrentUser, @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", "System.IsPinnedToNameSpaceTree", 0, RegistryValueKind.DWord),
                (RegistryHive.CurrentUser, @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}", "System.IsPinnedToNameSpaceTree", 0, RegistryValueKind.DWord),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", 1, RegistryValueKind.DWord))),
            () => Task.FromResult(RemoveMultipleRegistryValues(
                (RegistryHive.CurrentUser, @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}", "System.IsPinnedToNameSpaceTree"),
                (RegistryHive.CurrentUser, @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}", "System.IsPinnedToNameSpaceTree"),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo"))));

        AddTweak(dict, "WPFTweaksDisplay", "Efectos visuales - Configurar en Máximo rendimiento",
            "Configura las preferencias del sistema a rendimiento. Puedes hacerlo manualmente con sysdm.cpl.",
            "Compatible con Windows 10/11", true, "Advanced Tweaks", false,
            () => CheckAnyRegistryValue(
                (RegistryHive.CurrentUser, @"Control Panel\Desktop", "DragFullWindows", "0"),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3)),
            () => Task.Run(() =>
            {
                var regResult = SetMultipleRegistryValues(
                    (RegistryHive.CurrentUser, @"Control Panel\Desktop", "DragFullWindows", "0", RegistryValueKind.String),
                    (RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "200", RegistryValueKind.String),
                    (RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String),
                    (RegistryHive.CurrentUser, @"Control Panel\Keyboard", "KeyboardDelay", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewAlphaSelect", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewShadow", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton", 0, RegistryValueKind.DWord),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0, RegistryValueKind.DWord));
                if (!regResult.Success) return regResult;
                return RunCommandAsync("powershell", "-Command \"Set-ItemProperty -Path 'HKCU:\\Control Panel\\Desktop' -Name 'UserPreferencesMask' -Type Binary -Value ([byte[]](144,18,3,128,16,0,0,0))\"").Result;
            }),
            () => Task.Run(() =>
            {
                RemoveMultipleRegistryValues(
                    (RegistryHive.CurrentUser, @"Control Panel\Desktop", "DragFullWindows"),
                    (RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay"),
                    (RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate"),
                    (RegistryHive.CurrentUser, @"Control Panel\Keyboard", "KeyboardDelay"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewAlphaSelect"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewShadow"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\DWM", "EnableAeroPeek"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton"),
                    (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode"));
                return RunCommandAsync("powershell", "-Command \"Remove-ItemProperty -Path 'HKCU:\\Control Panel\\Desktop' -Name 'UserPreferencesMask' -ErrorAction SilentlyContinue\"").Result;
            }));

        AddTweak(dict, "WPFTweaksReservedStorage", "Almacenamiento reservado - Desactivar",
            "Desactiva el almacenamiento reservado de Windows (7-10 GB para actualizaciones). Solo recomendado en discos pequeños. Re-activar antes de grandes actualizaciones.",
            "Solo en discos pequeños", true, "Advanced Tweaks", true,
            () => false,
            () => RunCommandAsync("powershell", "-Command \"DISM /Online /Set-ReservedStorageState /State:Disabled\""),
            () => RunCommandAsync("powershell", "-Command \"DISM /Online /Set-ReservedStorageState /State:Enabled\""));

        AddTweak(dict, "WPFTweaksRestorePoint", "Punto de restauración - Crear",
            "Crea un punto de restauración en tiempo de ejecución por si se necesita revertir modificaciones.",
            "Requiere permisos de administrador", true, "Essential Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency", 0),
            () => Task.Run(() =>
            {
                // WinUtil escribe SystemRestorePointCreationFrequency=0 para que
                // Checkpoint-Computer no falle por el límite de un punto por día.
                var regResult = SetRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
                if (!regResult.Success) return regResult;
                return RunCommandAsync("powershell", "-Command \"if (-not (Get-ComputerRestorePoint)) { Enable-ComputerRestore -Drive $Env:SystemDrive }; Checkpoint-Computer -Description 'System Restore Point created by WinUtil' -RestorePointType MODIFY_SETTINGS; Write-Output 'System Restore Point Created Successfully'\"").Result;
            }),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "SystemRestorePointCreationFrequency", 1440, RegistryValueKind.DWord)));

        AddTweak(dict, "WPFTweaksEndTaskOnTaskbar", "Finalizar tarea con clic derecho - Activar",
            "Habilita la opción de finalizar tarea al hacer clic derecho en un programa de la barra de tareas.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", false,
            () => CheckRegistryValue(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 1),
            () => Task.FromResult(SetRegistryValue(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 1, RegistryValueKind.DWord)),
            () => Task.FromResult(RemoveRegistryValue(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask")));

        AddTweak(dict, "WPFTweaksStorage", "Storage Sense - Desactivar",
            "Storage Sense elimina archivos temporales automáticamente.",
            "Compatible con Windows 10/11", true, "Advanced Tweaks", false,
            () => CheckRegistryValue(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0),
            () => Task.FromResult(SetRegistryValue(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0, RegistryValueKind.DWord)),
            () => Task.FromResult(SetRegistryValue(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1, RegistryValueKind.DWord)));

        AddTweak(dict, "WPFTweaksWindowsAI", "Windows AI - Desactivar y eliminar",
            "Elimina y desactiva todas las funciones y paquetes de IA.",
            "Compatible con Windows 11", true, "Advanced Tweaks", true,
            () => CheckAnyRegistryValue(
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "SettingsPageVisibility", "hide:aicomponents"),
                (RegistryHive.LocalMachine, @"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1)),
            () => RunCommandAsync("powershell", "-Command \"$Appx = (Get-AppxPackage MicrosoftWindows.Client.CoreAI).PackageFullName; $Sid = (Get-LocalUser $Env:UserName).Sid.Value; New-Item \\\"HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\AppxAllUserStore\\EndOfLife\\$Sid\\$Appx\\\" -Force; Get-AppxPackage -AllUsers '*Copilot*' | Remove-AppxPackage -AllUsers; winget uninstall -e --name 'Copilot' --silent --force --accept-source-agreements 2>$null; Get-AppxPackage -AllUsers Microsoft.MicrosoftOfficeHub | Remove-AppxPackage -AllUsers; if ($Appx) { Remove-AppxPackage $Appx }; Set-Service -Name WSAIFabricSvc -StartupType Disabled; Disable-WindowsOptionalFeature -FeatureName Recall -Online -NoRestart; Write-Output 'Windows AI Disabled'\""),
            () => Task.FromResult(new TweakResult(true, "Revertir Windows AI requiere reinstalar los paquetes eliminados.")));

        AddTweak(dict, "WPFTweaksWPBT", "Tabla binaria de plataforma Windows (WPBT) - Desactivar",
            "WPBT permite que el fabricante ejecute programas al iniciar, como software antirrobo o instalaciones forzadas sin consentimiento. Riesgo de seguridad.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "DisableWpbtExecution", 1),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "DisableWpbtExecution", 1, RegistryValueKind.DWord)),
            () => Task.FromResult(RemoveRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "DisableWpbtExecution")));

        AddTweak(dict, "WPFTweaksPreventDeviceMetadataFromNetwork", "Prevenir apps complementarias de dispositivos",
            "Evita que se instale software adicional al conectar dispositivos (ej. anuncios al conectar un monitor). Riesgo de seguridad.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Device Metadata", "PreventDeviceMetadataFromNetwork", 1),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Device Metadata", "PreventDeviceMetadataFromNetwork", 1, RegistryValueKind.DWord)),
            () => Task.FromResult(RemoveRegistryValue(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Device Metadata", "PreventDeviceMetadataFromNetwork")));

        AddTweak(dict, "WPFTweaksRazerBlock", "Instalación automática de software Razer - Desactivar",
            "Bloquea TODAS las instalaciones de software Razer. El hardware funciona bien sin software.",
            "Solo hardware Razer", true, "Advanced Tweaks", false,
            () => CheckAnyRegistryValue(
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", 0),
                (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Installer", "DisableCoInstallers", 1)),
            () => Task.Run(() =>
            {
                var regResult = SetMultipleRegistryValues(
                    (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig", 0, RegistryValueKind.DWord),
                    (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Installer", "DisableCoInstallers", 1, RegistryValueKind.DWord));
                if (!regResult.Success) return regResult;
                return RunCommandAsync("powershell", "-Command \"$RazerPath = \\\"$Env:SystemRoot\\Installer\\Razer\\\"; if (Test-Path $RazerPath) { Remove-Item $RazerPath\\* -Recurse -Force } else { New-Item -Path $RazerPath -ItemType Directory }; icacls $RazerPath /deny '*S-1-1-0:(W)'\"").Result;
            }),
            () => RunCommandAsync("powershell", "-Command \"icacls \\\"$Env:SystemRoot\\Installer\\Razer\\\" /remove:d *S-1-1-0\""));

        AddTweak(dict, "WPFTweaksDisableNotifications", "Notificaciones del sistema y calendario - Desactivar",
            "Desactiva todas las notificaciones INCLUYENDO el calendario.",
            "Compatible con Windows 10/11", true, "Advanced Tweaks", false,
            () => CheckAnyRegistryValue(
                (RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableNotificationCenter", 1),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0)),
            () => Task.FromResult(SetMultipleRegistryValues(
                (RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableNotificationCenter", 1, RegistryValueKind.DWord),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0, RegistryValueKind.DWord))),
            () => Task.FromResult(RemoveMultipleRegistryValues(
                (RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableNotificationCenter"),
                (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled"))));

        AddTweak(dict, "WPFTweaksBlockAdobeNet", "Lista de bloqueo de URL de Adobe - Activar",
            "Reduce interrupciones bloqueando selectivamente conexiones a servidores de activación y telemetría de Adobe.",
            "Requiere software Adobe", true, "Advanced Tweaks", true,
            () => false,
            () => RunCommandAsync("powershell", "-Command \"$hostsUrl = Invoke-RestMethod -Uri https://github.com/Ruddernation-Designs/Adobe-URL-Block-List/raw/refs/heads/master/hosts; Add-Content -Path \\\"$Env:SystemRoot\\System32\\drivers\\etc\\hosts\\\" -Value $hostsUrl; ipconfig /flushdns; Write-Output 'Added Adobe url block list from host file'\""),
            () => RunCommandAsync("powershell", "-Command \"Set-Content \\\"$Env:SystemRoot\\System32\\drivers\\etc\\hosts\\\" ((Get-Content \\\"$Env:SystemRoot\\System32\\drivers\\etc\\hosts\\\") -join \"`n\" -replace '(?s)#New Ver.*', ''); ipconfig /flushdns; Write-Output 'Removed Adobe url block list from host file'\""));

        AddTweak(dict, "WPFTweaksRightClickMenu", "Menú contextual anterior - Activar",
            "Restaura el menú contextual clásico del Explorador, reemplazando la versión simplificada de Windows 11.",
            "Compatible con Windows 11", true, "Advanced Tweaks", false,
            () => CheckRegistryValue(RegistryHive.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", ""),
            () => RunCommandAsync("powershell", "-Command \"New-Item -Path 'HKCU:\\Software\\Classes\\CLSID\\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}' -Name InprocServer32 -Value '' -Force; Stop-Process -Name explorer\""),
            () => RunCommandAsync("powershell", "-Command \"Remove-Item -Path 'HKCU:\\Software\\Classes\\CLSID\\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}' -Recurse -Force\""));

        AddTweak(dict, "WPFTweaksDiskCleanup", "Limpieza de disco - Ejecutar",
            "Ejecuta la limpieza del disco C: y elimina actualizaciones de Windows antiguas.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", true,
            () => false,
            () => RunCommandAsync("powershell", "-Command \"cleanmgr.exe /d C: /VERYLOWDISK; Dism.exe /online /Cleanup-Image /StartComponentCleanup /ResetBase\""),
            () => Task.FromResult(new TweakResult(true, "No es posible revertir la limpieza de disco.")));

        AddTweak(dict, "WPFTweaksDeleteTempFiles", "Archivos temporales - Eliminar",
            "Borra las carpetas TEMP.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", false,
            () => false,
            () => RunCommandAsync("powershell", "-Command \"Remove-Item -Path \\\"$Env:Temp\\*\\\" -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -Path \\\"$Env:SystemRoot\\Temp\\*\\\" -Recurse -Force -ErrorAction SilentlyContinue\""),
            () => Task.FromResult(new TweakResult(true, "No es necesario revertir la eliminación de temporales.")));

        AddTweak(dict, "WPFTweaksIPv46", "IPv6 - Configurar IPv4 como preferido",
            "Configurar la preferencia IPv4 puede tener beneficios de latencia y seguridad en redes privadas sin IPv6.",
            "Compatible con Windows 10/11", true, "Advanced Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 32),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 32, RegistryValueKind.DWord)),
            () => Task.FromResult(RemoveRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents")));

        AddTweak(dict, "WPFTweaksTeredo", "Teredo - Desactivar",
            "Teredo es un túnel IPv6 que puede causar latencia adicional, aunque puede causar problemas con algunos juegos.",
            "Compatible con Windows 10/11", true, "Advanced Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 1),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 1, RegistryValueKind.DWord)),
            () => Task.FromResult(RemoveRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents")));

        AddTweak(dict, "WPFTweaksDisableIPv6", "IPv6 - Desactivar",
            "Desactiva IPv6.",
            "Requiere precaución", true, "Advanced Tweaks", true,
            () => CheckRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 255),
            () => Task.FromResult(SetRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 255, RegistryValueKind.DWord)),
            () => Task.FromResult(RemoveRegistryValue(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents")));

        AddTweak(dict, "WPFTweaksDisableBGapps", "Apps en segundo plano - Desactivar",
            "Desactiva todas las apps de Microsoft Store en segundo plano, lo que debe hacerse individualmente desde Windows 11.",
            "Compatible con Windows 10/11", true, "Advanced Tweaks", true,
            () => CheckRegistryValue(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1),
            () => Task.FromResult(SetRegistryValue(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, RegistryValueKind.DWord)),
            () => Task.FromResult(SetRegistryValue(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 0, RegistryValueKind.DWord)));

        AddTweak(dict, "WPFTweaksDisableFSO", "Optimizaciones de pantalla completa - Desactivar",
            "Desactiva FSO en todas las aplicaciones. NOTA: Desactivará la gestión de color en pantalla completa exclusiva.",
            "Compatible con Windows 10/11", true, "Advanced Tweaks", false,
            () => CheckRegistryValue(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1),
            () => Task.FromResult(SetRegistryValue(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 1, RegistryValueKind.DWord)),
            () => Task.FromResult(SetRegistryValue(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", 0, RegistryValueKind.DWord)));

        AddTweak(dict, "WPFTweaksDisableExplorerAutoDiscovery", "Detección automática de carpetas en Explorador - Desactivar",
            "El Explorador intenta adivinar el tipo de carpeta según su contenido, ralentizando la navegación. ¡ADVERTENCIA! Desactivará la agrupación del Explorador.",
            "Compatible con Windows 10/11", true, "Essential Tweaks", false,
            () => false,
            () => RunCommandAsync("powershell", "-Command \"$bags = 'HKCU:\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\Shell\\Bags'; $bagMRU = 'HKCU:\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\Shell\\BagMRU'; Remove-Item -Path $bags -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -Path $bagMRU -Recurse -Force -ErrorAction SilentlyContinue; $allFolders = 'HKCU:\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\Shell\\Bags\\AllFolders\\Shell'; if (-not (Test-Path $allFolders)) { New-Item -Path $allFolders -Force }; New-ItemProperty -Path $allFolders -Name 'FolderType' -Value 'NotSpecified' -PropertyType String -Force; Write-Output 'Please sign out and back in, or restart your computer to apply the changes!' \""),
            () => RunCommandAsync("powershell", "-Command \"$bags = 'HKCU:\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\Shell\\Bags'; $bagMRU = 'HKCU:\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\Shell\\BagMRU'; Remove-Item -Path $bags -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -Path $bagMRU -Recurse -Force -ErrorAction SilentlyContinue; Write-Output 'Please sign out and back in, or restart your computer to apply the changes!' \""));

        // ===== BUTTONS AND COMBOBOX (from Christitus) =====

        AddTweak(dict, "WPFOOSUbutton", "O&O ShutUp10++ - Ejecutar",
            "Ejecuta O&O ShutUp10++ para aplicar su colección de tweaks de privacidad.",
            "Requiere descargar O&O ShutUp10++", true, "Advanced Tweaks", true,
            () => false,
            () => RunCommandAsync("powershell", "-Command \"if (Test-Path 'C:\\Program Files\\O&O ShutUp10\\OOSU10.exe') { Start-Process 'C:\\Program Files\\O&O ShutUp10\\OOSU10.exe' -ArgumentList '/quiet' } else { Write-Output 'O&O ShutUp10 no encontrado' }\""),
            () => Task.FromResult(new TweakResult(true, "No es posible revertir automáticamente los cambios de O&O ShutUp10.")));

        return dict;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    /// <summary>
    /// RAM física total en KB, igual que WinUtil (Get-CimInstance Win32_PhysicalMemory ... / 1KB).
    /// Antes estaba hardcodeado a 384000 KB y el tweak "Servicios - Configurar en Manual"
    /// escribía el valor por defecto en vez del umbral según la memoria real.
    /// </summary>
    private static long GetTotalMemoryKB()
    {
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(status) && status.ullTotalPhys > 0)
                return (long)(status.ullTotalPhys / 1024);
        }
        catch
        {
            // fall through al valor por defecto
        }
        return 384000; // Fallback: valor por defecto de Windows (384 MB en KB)
    }

    private void AddTweak(
        Dictionary<string, TweakDefinition> dict,
        string id, string name, string description, string compatibility,
        bool isReversible, string category, bool requiresAdmin,
        Func<bool> checkApplied,
        Func<Task<TweakResult>> applyAction,
        Func<Task<TweakResult>> revertAction)
    {
        dict[id] = new TweakDefinition(id, name, description, compatibility, isReversible, category, requiresAdmin, checkApplied, applyAction, revertAction);
    }
}