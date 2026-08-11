using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación de las secciones "Features", "Fixes" y "Legacy Windows Panels" de
/// winutil (Chris Titus Tech): https://github.com/ChrisTitusTech/winutil
/// La app corre elevada, así que DISM / bcdedit / schtasks no requieren UAC extra.
/// </summary>
public class WinUtilService : IWinUtilService
{
    private readonly ILoggingService _loggingService;
    private readonly List<WinFeatureInfo> _features;
    private readonly List<WinFixInfo> _fixes;
    private readonly List<WindowsPanelInfo> _panels;

    public WinUtilService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
        _features = BuildFeatures();
        _fixes = BuildFixes();
        _panels = BuildPanels();
        _loggingService.LogInfo($"WinUtilService inicializado: {_features.Count} features, {_fixes.Count} fixes, {_panels.Count} paneles");
    }

    public List<WinFeatureInfo> GetFeatures() => new(_features);
    public List<WinFixInfo> GetFixes() => new(_fixes);
    public List<WindowsPanelInfo> GetPanels() => new(_panels);

    // ====== RUTAS NATIVAS (la app es x86: DISM/bcdedit/w32tm deben ser los de 64 bits) ======

    private string NativeToolPath(string exe) => Path.Combine(RepairService.NativeSystemDirectory, exe);

    // ====== FEATURES ======

    private static List<WinFeatureInfo> BuildFeatures() => new()
    {
        new WinFeatureInfo("dotnet", ".NET Framework (2, 3 y 4) - Activar",
            "Plataforma de desarrollo de Microsoft necesaria para ejecutar aplicaciones y juegos antiguos que dependen de .NET Framework.",
            new[] { "NetFx3", "NetFx4-AdvSrvs" }),
        new WinFeatureInfo("hyperv", "Hyper-V - Activar",
            "Plataforma de virtualización de Microsoft para crear y administrar máquinas virtuales. Requiere reinicio.",
            new[] { "Microsoft-Hyper-V-All" }, NeedsRestart: true),
        new WinFeatureInfo("legacymedia", "Componentes multimedia heredados (WMP, DirectPlay) - Activar",
            "Habilita programas antiguos de versiones anteriores de Windows (Windows Media Player, DirectPlay, componentes heredados).",
            new[] { "WindowsMediaPlayer", "MediaPlayback", "DirectPlay", "LegacyComponents" }),
        new WinFeatureInfo("wsl", "Subsistema de Windows para Linux (WSL) - Activar",
            "Permite ejecutar distribuciones de Linux de forma nativa en Windows sin máquina virtual ni dual boot. Requiere reinicio.",
            new[] { "VirtualMachinePlatform", "Microsoft-Windows-Subsystem-Linux" }, NeedsRestart: true),
        new WinFeatureInfo("nfs", "Sistema de archivos de red (NFS) - Activar",
            "Mecanismo para almacenar archivos en la red. Habilita el cliente NFS y la administración.",
            new[] { "ServicesForNFS-ClientOnly", "ClientForNFS-Infrastructure", "NFS-Administration" }),
        new WinFeatureInfo("regbackup", "Copia de seguridad del registro (tarea diaria 00:30) - Activar",
            "Habilita la copia de seguridad periódica del registro, desactivada por Microsoft desde Windows 10 1803. Crea una tarea programada diaria.",
            Array.Empty<string>()),
        new WinFeatureInfo("f8recovery", "Recuperación F8 heredada - Activar",
            "Habilita la pantalla de Opciones de arranque avanzadas (F8) para iniciar Windows en modos de solución de problemas.",
            Array.Empty<string>()),
        new WinFeatureInfo("sandbox", "Windows Sandbox - Activar",
            "Máquina virtual ligera que proporciona un escritorio temporal aislado para ejecutar aplicaciones de forma segura. Requiere reinicio.",
            new[] { "Containers-DisposableClientVM" }, NeedsRestart: true)
    };

    public async Task<FeatureState> GetFeatureStateAsync(string featureId)
    {
        var feature = _features.FirstOrDefault(f => f.Id == featureId);
        if (feature == null) return FeatureState.Unknown;

        try
        {
            // Características especiales (sin nombres DISM)
            if (feature.Id == "regbackup")
            {
                return IsRegistryBackupEnabled() ? FeatureState.Enabled : FeatureState.Disabled;
            }
            if (feature.Id == "f8recovery")
            {
                return await IsF8RecoveryEnabledAsync() ? FeatureState.Enabled : FeatureState.Disabled;
            }

            if (feature.FeatureNames.Length == 0) return FeatureState.Unknown;

            // DISM: la característica está activa solo si TODAS sus features lo están.
            var anyPending = false;
            foreach (var name in feature.FeatureNames)
            {
                var result = await RunProcessAsync(NativeToolPath("dism.exe"),
                    $"/Online /Get-FeatureInfo /FeatureName:{name}", null, CancellationToken.None);
                var state = ParseDismState(result.Output);
                if (state == FeatureState.Pending) anyPending = true;
                else if (state != FeatureState.Enabled) return FeatureState.Disabled;
            }
            return anyPending ? FeatureState.Pending : FeatureState.Enabled;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error verificando estado de feature {featureId}", ex);
            return FeatureState.Unknown;
        }
    }

    /// <summary>
    /// Parsea la línea "Estado / State" de DISM tolerando el idioma del sistema
    /// (español: Habilitado/Deshabilitado/Pendiente; inglés: Enabled/Disabled/Pending).
    /// </summary>
    private static FeatureState ParseDismState(string output)
    {
        var line = output.Split('\n').FirstOrDefault(l =>
            l.Contains("Estado", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("State", StringComparison.OrdinalIgnoreCase));
        if (line == null) return FeatureState.Unknown;

        var s = line.Substring(line.IndexOf(':') + 1).ToLowerInvariant();

        if (s.Contains("pendient") || s.Contains("pending") ||
            s.Contains("habilitando") || s.Contains("enabling") ||
            s.Contains("deshabilitando") || s.Contains("disabling"))
            return FeatureState.Pending;

        if (s.Contains("deshabilit") || s.Contains("disabled"))
            return FeatureState.Disabled;

        if (s.Contains("habilit") || s.Contains("enabled"))
            return FeatureState.Enabled;

        return FeatureState.Unknown;
    }

    public async Task<CommandResult> EnableFeatureAsync(string featureId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var feature = _features.FirstOrDefault(f => f.Id == featureId);
        if (feature == null) return new CommandResult(false, $"Feature no encontrada: {featureId}");

        try
        {
            progress?.Report($"Activando {feature.Name}...");
            _loggingService.LogInfo($"Activando feature: {feature.Name}");

            if (feature.Id == "regbackup")
            {
                return await EnableRegistryBackupAsync(progress, cancellationToken);
            }
            if (feature.Id == "f8recovery")
            {
                return await RunProcessAsync(NativeToolPath("bcdedit.exe"), "/set bootmenupolicy legacy", progress, cancellationToken);
            }

            foreach (var name in feature.FeatureNames)
            {
                progress?.Report($"DISM: {name}...");
                var result = await RunProcessAsync(NativeToolPath("dism.exe"),
                    $"/Online /Enable-Feature /FeatureName:{name} /All /NoRestart", progress, cancellationToken);
                if (!result.Success)
                {
                    _loggingService.LogWarning($"Falló activación de {name}: {result.Output}");
                    return result;
                }
            }

            progress?.Report($"✓ {feature.Name} activada{(feature.NeedsRestart ? " — reiniciá para completar" : "")}.");
            return new CommandResult(true, $"Feature activada: {feature.Name}");
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error activando feature {featureId}", ex);
            return new CommandResult(false, ex.Message);
        }
    }

    public async Task<CommandResult> DisableFeatureAsync(string featureId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var feature = _features.FirstOrDefault(f => f.Id == featureId);
        if (feature == null) return new CommandResult(false, $"Feature no encontrada: {featureId}");

        try
        {
            progress?.Report($"Desactivando {feature.Name}...");
            _loggingService.LogInfo($"Desactivando feature: {feature.Name}");

            if (feature.Id == "regbackup")
            {
                return await DisableRegistryBackupAsync(progress, cancellationToken);
            }
            if (feature.Id == "f8recovery")
            {
                return await RunProcessAsync(NativeToolPath("bcdedit.exe"), "/set bootmenupolicy standard", progress, cancellationToken);
            }

            foreach (var name in feature.FeatureNames)
            {
                progress?.Report($"DISM: {name}...");
                var result = await RunProcessAsync(NativeToolPath("dism.exe"),
                    $"/Online /Disable-Feature /FeatureName:{name} /NoRestart", progress, cancellationToken);
                if (!result.Success)
                {
                    _loggingService.LogWarning($"Falló desactivación de {name}: {result.Output}");
                    return result;
                }
            }

            progress?.Report($"✓ {feature.Name} desactivada.");
            return new CommandResult(true, $"Feature desactivada: {feature.Name}");
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error desactivando feature {featureId}", ex);
            return new CommandResult(false, ex.Message);
        }
    }

    // ====== FEATURES ESPECIALES ======

    private bool IsRegistryBackupEnabled()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Configuration Manager");
            return key?.GetValue("EnablePeriodicBackup") is int v && v == 1;
        }
        catch { return false; }
    }

    private Task<CommandResult> EnableRegistryBackupAsync(IProgress<string>? progress, CancellationToken ct)
    {
        // Script del winutil: habilita el backup periódico y crea la tarea diaria a las 00:30.
        const string script =
            "New-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Configuration Manager' -Name 'EnablePeriodicBackup' -Type DWord -Value 1 -Force;" +
            "New-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Configuration Manager' -Name 'BackupCount' -Type DWord -Value 2 -Force;" +
            "$action = New-ScheduledTaskAction -Execute 'schtasks' -Argument '/run /i /tn \"\\Microsoft\\Windows\\Registry\\RegIdleBackup\"';" +
            "$trigger = New-ScheduledTaskTrigger -Daily -At 00:30;" +
            "Register-ScheduledTask -Action $action -Trigger $trigger -TaskName 'AutoRegBackup' -Description 'Create System Registry Backups' -User 'System' -Force;" +
            "Write-Output 'Copia de seguridad del registro habilitada'";
        return RunPowerShellAsync(script, progress, ct);
    }

    private Task<CommandResult> DisableRegistryBackupAsync(IProgress<string>? progress, CancellationToken ct)
    {
        const string script =
            "Unregister-ScheduledTask -TaskName 'AutoRegBackup' -Confirm:$false -ErrorAction SilentlyContinue;" +
            "New-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Configuration Manager' -Name 'EnablePeriodicBackup' -Type DWord -Value 0 -Force;" +
            "Write-Output 'Copia de seguridad del registro deshabilitada'";
        return RunPowerShellAsync(script, progress, ct);
    }

    private async Task<bool> IsF8RecoveryEnabledAsync()
    {
        var result = await RunProcessAsync(NativeToolPath("bcdedit.exe"), "/enum {bootmgr}", null, CancellationToken.None);
        return result.Output.Contains("bootmenupolicy", StringComparison.OrdinalIgnoreCase)
               && result.Output.Contains("legacy", StringComparison.OrdinalIgnoreCase);
    }

    // ====== FIXES ======

    private static List<WinFixInfo> BuildFixes() => new()
    {
        new WinFixInfo("ntp", "Servidor NTP - Activar",
            "Reemplaza el servidor horario predeterminado de Windows (time.windows.com) por pool.ntp.org para una sincronización más precisa y confiable.",
            SupportsRevert: true),
        new WinFixInfo("autologon", "AutoLogon - Configurar",
            "Configura el inicio de sesión automático de Windows. Pide usuario y contraseña, y los guarda en el registro (Winlogon)."),
        new WinFixInfo("winget", "WinGet - Reinstalar",
            "Desinstala y reinstala el Instalador de aplicaciones (App Installer / WinGet) desde la última versión oficial del repositorio winget-cli.",
            IsLongRunning: true)
    };

    public async Task<CommandResult> RunFixAsync(string fixId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        switch (fixId)
        {
            case "ntp":
                return await RunNtpFixAsync(progress, cancellationToken);

            case "autologon":
                return new CommandResult(false, "AutoLogon se configura desde su formulario (usuario y contraseña).");

            case "winget":
                return await RunWingetReinstallAsync(progress, cancellationToken);

            default:
                return new CommandResult(false, $"Fix no encontrado: {fixId}");
        }
    }

    public async Task<CommandResult> RevertFixAsync(string fixId, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        switch (fixId)
        {
            case "ntp":
                return await RevertNtpFixAsync(progress, cancellationToken);

            default:
                return new CommandResult(false, $"El fix {fixId} no se puede revertir.");
        }
    }

    private async Task<CommandResult> RunNtpFixAsync(IProgress<string>? progress, CancellationToken ct)
        => await RunNtpScriptAsync("pool.ntp.org", reliable: true, "Configurando pool.ntp.org como servidor horario...", "Servidor horario configurado en pool.ntp.org", progress, ct);

    private async Task<CommandResult> RevertNtpFixAsync(IProgress<string>? progress, CancellationToken ct)
        => await RunNtpScriptAsync("time.windows.com", reliable: false, "Restaurando time.windows.com como servidor horario...", "Servidor horario restaurado a time.windows.com", progress, ct);

    /// <summary>
    /// Un solo script: w32tm no entiende "&&" (es del shell), así que configurar,
    /// reiniciar el servicio y resincronizar se hacen en PowerShell.
    /// </summary>
    private async Task<CommandResult> RunNtpScriptAsync(string server, bool reliable, string startMsg, string endMsg, IProgress<string>? progress, CancellationToken ct)
    {
        var reliableFlag = reliable ? "/reliable:YES" : "/reliable:NO";
        var script =
            $"Write-Output '{startMsg}';" +
            $"w32tm /config /manualpeerlist:\"{server},0x1\" /syncfromflags:manual {reliableFlag} /update;" +
            "Write-Output 'Reiniciando el servicio de hora (w32time)...';" +
            "Restart-Service w32time -Force -ErrorAction SilentlyContinue;" +
            "Write-Output 'Resincronizando hora...';" +
            "w32tm /resync;" +
            $"Write-Output '{endMsg}'";
        return await RunPowerShellAsync(script, progress, ct);
    }

    private async Task<CommandResult> RunWingetReinstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        const string script =
            "$ProgressPreference = 'SilentlyContinue';" +
            "Write-Output 'Desinstalando App Installer (WinGet)...';" +
            "Get-AppxPackage -AllUsers Microsoft.DesktopAppInstaller | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue;" +
            "Write-Output 'Descargando la última versión de winget-cli...';" +
            "$tag = (Invoke-RestMethod -Uri 'https://api.github.com/repos/microsoft/winget-cli/releases/latest' -Headers @{ 'User-Agent' = 'WinForge' }).tag_name;" +
            "$url = \"https://github.com/microsoft/winget-cli/releases/download/$tag/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle\";" +
            "Invoke-WebRequest -Uri $url -OutFile \"$env:TEMP\\WinForge-winget.msixbundle\";" +
            "Write-Output 'Instalando App Installer...';" +
            "Add-AppxPackage \"$env:TEMP\\WinForge-winget.msixbundle\";" +
            "Remove-Item \"$env:TEMP\\WinForge-winget.msixbundle\" -Force -ErrorAction SilentlyContinue;" +
            "Write-Output 'WinGet reinstalado correctamente'";
        return await RunPowerShellAsync(script, progress, ct);
    }

    // ====== AUTOLOGON ======

    public Task<CommandResult> SetAutoLogonAsync(string username, string password, string? domain = null)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            if (key == null) return Task.FromResult(new CommandResult(false, "No se pudo abrir Winlogon en el registro."));

            key.SetValue("AutoAdminLogon", "1", RegistryValueKind.String);
            key.SetValue("DefaultUserName", username, RegistryValueKind.String);
            key.SetValue("DefaultPassword", password, RegistryValueKind.String);
            key.SetValue("DefaultDomainName", string.IsNullOrWhiteSpace(domain) ? "." : domain, RegistryValueKind.String);

            _loggingService.LogInfo($"AutoLogon configurado para {username}");
            return Task.FromResult(new CommandResult(true, $"AutoLogon configurado para {username}. Reiniciá para probarlo."));
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error configurando AutoLogon", ex);
            return Task.FromResult(new CommandResult(false, ex.Message));
        }
    }

    public Task<CommandResult> RemoveAutoLogonAsync()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", true);
            if (key == null) return Task.FromResult(new CommandResult(true, "No había configuración de AutoLogon."));

            key.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
            key.DeleteValue("DefaultPassword", false);

            _loggingService.LogInfo("AutoLogon eliminado");
            return Task.FromResult(new CommandResult(true, "AutoLogon desactivado."));
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error quitando AutoLogon", ex);
            return Task.FromResult(new CommandResult(false, ex.Message));
        }
    }

    // ====== PANELES DE WINDOWS ======

    private static List<WindowsPanelInfo> BuildPanels() => new()
    {
        new WindowsPanelInfo("computer", "Administración de equipos", "Herramientas de sistema: visor de eventos, discos, servicios y más.", "compmgmt.msc"),
        new WindowsPanelInfo("control", "Panel de control", "El panel de control clásico de Windows.", "control"),
        new WindowsPanelInfo("mouse", "Propiedades del mouse", "Configuración de botones, puntero y velocidad del mouse.", "main.cpl"),
        new WindowsPanelInfo("network", "Conexiones de red", "Adaptadores de red y sus propiedades.", "ncpa.cpl"),
        new WindowsPanelInfo("power", "Panel de energía", "Planes de energía y configuración de ahorro.", "powercfg.cpl"),
        new WindowsPanelInfo("printer", "Impresoras", "Dispositivos e impresoras.", "shell:::{A8A91A66-3A7D-4424-8D24-04E180695C7A}"),
        new WindowsPanelInfo("programs", "Programas y características", "Desinstalar o cambiar programas instalados.", "appwiz.cpl"),
        new WindowsPanelInfo("region", "Región", "Formato de fecha, hora e idioma regional.", "intl.cpl"),
        new WindowsPanelInfo("security", "Seguridad y mantenimiento", "Centro de seguridad y estado del sistema.", "wscui.cpl"),
        new WindowsPanelInfo("sound", "Sonido", "Dispositivos de reproducción, grabación y sonidos del sistema.", "mmsys.cpl"),
        new WindowsPanelInfo("system", "Propiedades del sistema", "Rendimiento, nombre del equipo, protección y ajustes avanzados.", "sysdm.cpl"),
        new WindowsPanelInfo("timedate", "Fecha y hora", "Configuración de fecha, hora y zona horaria.", "timedate.cpl"),
        new WindowsPanelInfo("firewall", "Firewall de Windows Defender", "Estado y reglas del firewall de Windows.", "firewall.cpl"),
        new WindowsPanelInfo("restore", "Restaurar sistema", "Restaura el equipo a un punto anterior.", "rstrui.exe")
    };

    public Task<CommandResult> LaunchPanelAsync(string panelId)
    {
        var panel = _panels.FirstOrDefault(p => p.Id == panelId);
        if (panel == null) return Task.FromResult(new CommandResult(false, $"Panel no encontrado: {panelId}"));

        try
        {
            _loggingService.LogInfo($"Abriendo panel: {panel.Name} ({panel.LaunchCommand})");
            var command = panel.LaunchCommand;

            // URI shell: (impresoras) → explorer.exe con el URI.
            if (command.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = command,
                    UseShellExecute = true
                });
            }
            // .cpl → control.exe nativo (64 bits): resuelve el .cpl desde el System32 real.
            // Lanzar el nombre pelado desde la app x86 lo busca en SysWOW64, donde faltan
            // firewall.cpl y otros → Win32Exception. Mismo motivo para rstrui.exe abajo.
            else if (command.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase))
            {
                var controlExe = Path.Combine(RepairService.NativeSystemDirectory, "control.exe");
                if (!File.Exists(controlExe)) controlExe = "control.exe";
                Process.Start(new ProcessStartInfo
                {
                    FileName = controlExe,
                    Arguments = command,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            // .msc (Administración de equipos) → mmc.exe nativo de 64 bits con la ruta
            // REAL del .msc (System32). Dos trampas de 32/64 bits: abrir el .msc directo
            // lanza el mmc de 32 bits (SysWOW64), que no carga los snap-ins de 64 bits
            // (Administración de equipos, visor de eventos...); y al mmc de 64 bits hay
            // que pasarle la ruta real (C:\Windows\System32\...) porque "Sysnative" solo
            // existe para procesos de 32 bits y el mmc de 64 bits no la resuelve.
            else if (command.EndsWith(".msc", StringComparison.OrdinalIgnoreCase))
            {
                var mmcExe = Path.Combine(RepairService.NativeSystemDirectory, "mmc.exe");
                if (!File.Exists(mmcExe)) mmcExe = "mmc.exe";
                var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? @"C:\Windows";
                var mscPath = Path.Combine(Path.Combine(windowsDirectory, "System32"), command);
                Process.Start(new ProcessStartInfo
                {
                    FileName = mmcExe,
                    Arguments = $"\"{mscPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            // "control" (Panel de control) → control.exe nativo.
            else if (command.Equals("control", StringComparison.OrdinalIgnoreCase))
            {
                var controlExe = Path.Combine(RepairService.NativeSystemDirectory, "control.exe");
                if (!File.Exists(controlExe)) controlExe = "control.exe";
                Process.Start(new ProcessStartInfo
                {
                    FileName = controlExe,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            // .msc / .exe → ruta nativa completa (Sysnative desde la app x86).
            else
            {
                var fullPath = Path.Combine(RepairService.NativeSystemDirectory, command);
                Process.Start(new ProcessStartInfo
                {
                    FileName = File.Exists(fullPath) ? fullPath : command,
                    UseShellExecute = true
                });
            }

            return Task.FromResult(new CommandResult(true, $"Abriendo {panel.Name}..."));
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error abriendo panel {panelId}", ex);
            return Task.FromResult(new CommandResult(false, $"No se pudo abrir {panel.Name}: {ex.Message}"));
        }
    }

    // ====== PROCESOS ======

    /// <summary>
    /// Ejecuta un proceso nativo capturando salida y reportando progreso línea a línea.
    /// </summary>
    private static async Task<CommandResult> RunProcessAsync(string exe, string args, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RepairService.SystemWorkingDirectory
            };

            using var process = Process.Start(psi);
            if (process == null) return new CommandResult(false, "No se pudo iniciar el proceso.");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct);
            var output = await outputTask;
            var error = await errorTask;

            foreach (var line in (output + "\n" + error).Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) progress?.Report(trimmed);
            }

            if (process.ExitCode != 0)
                return new CommandResult(false, $"Exit code {process.ExitCode}: {Trim(error)}");

            return new CommandResult(true, Trim(output));
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "Operación cancelada.");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, ex.Message);
        }
    }

    private Task<CommandResult> RunPowerShellAsync(string script, IProgress<string>? progress, CancellationToken ct)
    {
        var psPath = Path.Combine(RepairService.NativeSystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(psPath)) psPath = "powershell.exe";

        var args = $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"";
        return RunProcessAsync(psPath, args, progress, ct);
    }

    private static string Trim(string s) => string.IsNullOrWhiteSpace(s) ? "OK" : s.Trim();
}
