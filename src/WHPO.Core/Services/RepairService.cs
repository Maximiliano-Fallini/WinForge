using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación del servicio de reparación del sistema.
/// Ejecuta herramientas como SFC, DISM, CHKDSK, etc.
/// </summary>
public class RepairService : IRepairService
{
    private readonly ILoggingService _loggingService;

    public RepairService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    private async Task<RepairResult> RunPowerShellCommandAsync(string script, bool requiresAdmin = true, IProgress<string>? progress = null)
    {
        try
        {
            if (requiresAdmin && !IsElevated())
            {
                return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
            }

            progress?.Report("Iniciando PowerShell...");

            var powershellPath = ResolveExecutablePath("powershell");
            if (string.IsNullOrWhiteSpace(powershellPath))
            {
                return new RepairResult(false, "No se pudo encontrar PowerShell.", "No existe un ejecutable de PowerShell válido en este sistema.");
            }

            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -EncodedCommand {encodedScript}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = SystemWorkingDirectory,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new RepairResult(false, "No se pudo iniciar PowerShell.", "El proceso de PowerShell no pudo crearse.");

            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    output.AppendLine(e.Data);
                    progress?.Report(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    error.AppendLine(e.Data);
                    progress?.Report(e.Data);
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            var combinedOutput = (output.Length > 0 && error.Length > 0)
                ? string.Join(Environment.NewLine, output.ToString().Trim(), error.ToString().Trim())
                : output.ToString().Trim() + error.ToString().Trim();
            
            var exitCodeSuccess = process.ExitCode == 0;
            var outputNormalized = NormalizeForComparison(combinedOutput);
            var knownFailurePatterns = new[]
            {
                "error",
                "fallo",
                "failed",
                "no se pudo",
                "could not",
                "access denied",
                "acceso denegado",
                "exception",
                "excepcion"
            };
            var hasKnownFailure = knownFailurePatterns.Any(p => outputNormalized.Contains(p));
            var success = exitCodeSuccess && !hasKnownFailure;
            
            var message = success
                ? "PowerShell terminó correctamente."
                : hasKnownFailure
                    ? "PowerShell reportó un error en su salida."
                    : $"PowerShell terminó con el código {process.ExitCode}.";

            // Guardar la salida en el log cuando falla, para poder diagnosticar sin re-ejecutar.
            if (!success)
            {
                _loggingService.LogWarning($"PowerShell: {message}{Environment.NewLine}{combinedOutput}");
            }

            return new RepairResult(success, message, combinedOutput);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error ejecutando PowerShell", ex);
            return new RepairResult(false, $"Error: {ex.Message}", ex.ToString());
        }
    }

    /// <summary>
    /// Devuelve el directorio System32 nativo (de 64 bits) aunque la app se haya compilado como x86 (32 bits).
    /// <para>
    /// En Windows de 64 bits, un proceso de 32 bits ve "C:\Windows\System32" redirigido a SysWOW64,
    /// por lo que sfc.exe/dism.exe/cmd.exe resueltos desde ahí son los stubs de 32 bits que NO pueden
    /// iniciar el servicio de reparación (TrustedInstaller). "C:\Windows\Sysnative" expone el System32
    /// real de 64 bits y solo existe/está visible desde procesos de 32 bits.
    /// </para>
    /// </summary>
    public static string NativeSystemDirectory
    {
        get
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windowsDirectory))
                windowsDirectory = @"C:\Windows";

            if (!Environment.Is64BitProcess)
            {
                var sysnative = Path.Combine(windowsDirectory, "Sysnative");
                if (Directory.Exists(sysnative))
                    return sysnative;
            }

            return Path.Combine(windowsDirectory, "System32");
        }
    }

    /// <summary>
    /// Directorio de trabajo válido para los procesos hijos: el System32 real ("C:\Windows\System32").
    /// <para>
    /// NO usar <see cref="NativeSystemDirectory"/> (Sysnative) como directorio de trabajo:
    /// Sysnative solo es visible para procesos de 32 bits, así que un cmd.exe de 64 bits lanzado con
    /// ese CWD falla con "El directorio actual no es válido" y aborta antes de ejecutar el comando.
    /// "C:\Windows\System32" es válido para procesos de 64 bits (sin redirección) y de 32 bits (vía SysWOW64).
    /// </para>
    /// </summary>
    public static string SystemWorkingDirectory
    {
        get
        {
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windowsDirectory))
                windowsDirectory = @"C:\Windows";
            return Path.Combine(windowsDirectory, "System32");
        }
    }

    /// <summary>
    /// Ruta del PowerShell nativo (evita que un proceso de 32 bits resuelva el stub de SysWOW64).
    /// </summary>
    private static string PowerShellExePath => Path.Combine(NativeSystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string ResolveExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return string.Empty;

        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(command) ? command : string.Empty;
        }

        var systemDirectory = NativeSystemDirectory;

        return command.ToLowerInvariant() switch
        {
            "sfc" => File.Exists(Path.Combine(systemDirectory, "sfc.exe")) ? Path.Combine(systemDirectory, "sfc.exe") : string.Empty,
            "dism" => File.Exists(Path.Combine(systemDirectory, "dism.exe")) ? Path.Combine(systemDirectory, "dism.exe") : string.Empty,
            "chkdsk" => File.Exists(Path.Combine(systemDirectory, "chkdsk.exe")) ? Path.Combine(systemDirectory, "chkdsk.exe") : string.Empty,
            "ipconfig" => File.Exists(Path.Combine(systemDirectory, "ipconfig.exe")) ? Path.Combine(systemDirectory, "ipconfig.exe") : string.Empty,
            "netsh" => File.Exists(Path.Combine(systemDirectory, "netsh.exe")) ? Path.Combine(systemDirectory, "netsh.exe") : string.Empty,
            "powershell" => File.Exists(PowerShellExePath)
                ? PowerShellExePath
                : string.Empty,
            "cmd" => File.Exists(Path.Combine(systemDirectory, "cmd.exe")) ? Path.Combine(systemDirectory, "cmd.exe") : string.Empty,
            _ => command
        };
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Normaliza texto para comparación insensible a mayúsculas, minúsculas y acentos.
    /// </summary>
    private static string NormalizeForComparison(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        
        // Convertir a minúsculas y remover acentos/diacríticos
        var normalized = input.ToLowerInvariant();
        var stringBuilder = new StringBuilder();
        
        foreach (var c in normalized.Normalize(System.Text.NormalizationForm.FormD))
        {
            var unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }
        
        return stringBuilder.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }



    public async Task<RepairResult> RepairStoreAsync(IProgress<string>? progress = null)
    {
        _loggingService.LogInfo("Reparando Windows Store...");

        if (!IsElevated())
        {
            return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
        }

        // Re-registra todas las apps con un ciclo protegido: las apps en uso (0x80073D02),
        // con versión más nueva (0x80073D06) o sin manifest se OMITEN sin romper el lote.
        // Antes, un solo paquete problemático hacía terminar a PowerShell con código 1.
        // Escribe progreso en vivo (1 línea cada 10 paquetes) para que la UI muestre avance.
        var script =
            "$ErrorActionPreference = 'Continue';" +
            "$ok = 0; $fail = 0; $failed = @();" +
            "Write-Output 'Enumerando paquetes instalados...';" +
            "try { $packages = @(Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue) } catch { Write-Output ('No se pudo enumerar los paquetes: ' + $_.Exception.Message); exit 1 };" +
            "if ($packages.Count -eq 0) { Write-Output 'No se encontraron paquetes para re-registrar.'; exit 1 };" +
            "Write-Output ('Total de paquetes: ' + $packages.Count);" +
            "$processed = 0;" +
            "foreach ($pkg in $packages) {" +
            "  $processed++;" +
            "  if (-not $pkg.InstallLocation) { $fail++; $failed += $pkg.Name; continue };" +
            "  $manifest = Join-Path $pkg.InstallLocation 'AppXManifest.xml';" +
            "  if (-not (Test-Path $manifest)) { $fail++; $failed += $pkg.Name; continue };" +
            "  try { Add-AppxPackage -DisableDevelopmentMode -Register $manifest -ErrorAction Stop; $ok++ } catch { $fail++; $failed += $pkg.Name };" +
            "  if (($processed % 10) -eq 0) { Write-Output ('Procesados ' + $processed + ' de ' + $packages.Count + '...') }" +
            "};" +
            "Write-Output ('Aplicaciones re-registradas correctamente: ' + $ok);" +
            "Write-Output ('Omitidas (en uso, sin manifest o con version mas nueva): ' + $fail);" +
            "if ($failed.Count -gt 0) { Write-Output ('Detalle de omitidas: ' + ($failed -join ', ')) };" +
            "if ($ok -gt 0) { exit 0 } else { exit 1 }";
        return await RunPowerShellCommandAsync(script, true, progress);
    }

    /// <summary>
    /// Verifica y repara los perfiles de usuario en el registro (ProfileList).
    /// Corrige el marcador de corrupción "State" != 0 (el fix documentado de Microsoft:
    /// https://support.microsoft.com/topic/fix-a-corrupted-user-profile-in-windows) cuando la
    /// carpeta del perfil y su NTUSER.DAT existen. Si la carpeta falta, lo reporta sin tocar nada.
    /// </summary>
    public async Task<RepairResult> RepairUserProfileAsync()
    {
        _loggingService.LogInfo("Verificando y reparando perfiles de usuario...");
        await Task.Yield();

        var profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var details = new List<string>
        {
            $"Perfil actual: {profilePath}",
            string.Empty
        };
        var repaired = new List<string>();
        var problems = new List<string>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var profileList = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profileList == null)
            {
                return new RepairResult(false, "No se pudo abrir la lista de perfiles del registro.",
                    "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList no existe.", true);
            }

            var sidNames = profileList.GetSubKeyNames();
            if (sidNames.Length == 0)
            {
                return new RepairResult(false, "No se encontraron perfiles de usuario en el registro.", "ProfileList está vacío.");
            }

            foreach (var sid in sidNames)
            {
                // Saltar cuentas del sistema (LocalSystem, LocalService, NetworkService).
                if (sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") continue;

                using var sidKey = profileList.OpenSubKey(sid, true); // escritura para corregir State
                if (sidKey == null) continue;

                var imagePath = sidKey.GetValue("ProfileImagePath") as string;
                if (string.IsNullOrWhiteSpace(imagePath)) continue;

                // Saltar carpetas del sistema que no son perfiles de usuario reales.
                if (imagePath.Contains(@"\SystemProfile", StringComparison.OrdinalIgnoreCase) ||
                    imagePath.Contains(@"\LocalService", StringComparison.OrdinalIgnoreCase) ||
                    imagePath.Contains(@"\NetworkService", StringComparison.OrdinalIgnoreCase) ||
                    imagePath.Contains(@"\Public", StringComparison.OrdinalIgnoreCase) ||
                    imagePath.Contains(@"\Default", StringComparison.OrdinalIgnoreCase))
                    continue;

                var state = sidKey.GetValue("State") is int s ? s : 0;
                var folderExists = Directory.Exists(imagePath);
                var ntuserOk = folderExists && File.Exists(Path.Combine(imagePath, "NTUSER.DAT"));

                details.Add($"{sid}  {imagePath}");
                details.Add($"   State={state} · {(folderExists ? "carpeta OK" : "carpeta NO existe")} · {(ntuserOk ? "NTUSER.DAT OK" : "NTUSER.DAT falta")}");

                if (state != 0)
                {
                    if (folderExists && ntuserOk)
                    {
                        // El perfil es real pero quedó marcado como corrupto (error clásico
                        // "No podemos iniciar sesión en tu cuenta"): se corrige el marcador.
                        sidKey.SetValue("State", 0, RegistryValueKind.DWord);
                        repaired.Add($"{sid} (State {state} → 0): {imagePath}");
                        _loggingService.LogWarning($"Perfil {sid} marcado como corrupto (State={state}); corregido a 0.");
                    }
                    else
                    {
                        problems.Add($"Perfil {sid}: State={state} pero la carpeta {imagePath} no existe o le falta NTUSER.DAT. No se tocó; revisión manual.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error reparando perfil de usuario", ex);
            return new RepairResult(false, "Error al reparar el perfil de usuario: " + ex.Message, string.Join(Environment.NewLine, details));
        }

        var summary = string.Join(Environment.NewLine, details);

        if (repaired.Count > 0)
        {
            return new RepairResult(true, "Perfil de usuario reparado: se corrigió el marcador de corrupción de " + repaired.Count + " perfil(es). Reiniciá la sesión para completar.",
                summary + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, repaired));
        }
        if (problems.Count > 0)
        {
            return new RepairResult(false, "Se encontraron perfiles con problemas que requieren revisión manual.",
                summary + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
        return new RepairResult(true, "Todos los perfiles de usuario están en buen estado. No se requirieron cambios.", summary);
    }

    public List<RepairToolInfo> GetAvailableTools()
    {
        return new List<RepairToolInfo>
        {
            new RepairToolInfo(
                "sfc",
                "SFC - Escaneo de archivos de sistema",
                "Repara archivos de sistema corruptos o dañados en Windows. Escanea todos los archivos protegidos del sistema y reemplaza las versiones incorrectas con las versiones correctas.",
                "Compatible con Windows 10/11",
                true,
                true
            ),
            new RepairToolInfo(
                "dism",
                "DISM - Reparación de imagen de Windows",
                "Repara la imagen de Windows cuando SFC no puede solucionar el problema. Descarga archivos correctos desde Windows Update si es necesario.",
                "Compatible con Windows 10/11",
                true,
                true
            ),
            new RepairToolInfo(
                "chkdsk",
                "CHKDSK - Verificación de disco",
                "Verifica y repara errores en el sistema de archivos y sectores dañados del disco duro o SSD. Puede requerir reinicio si el disco está en uso.",
                "Compatible con Windows 10/11",
                true,
                true
            ),
            new RepairToolInfo(
                "component_store",
                "Reparación del almacén de componentes",
                "Limpia el almacén de componentes de Windows (WinSxS) para liberar espacio y eliminar versiones obsoletas de archivos del sistema.",
                "Compatible con Windows 10/11",
                true,
                true
            ),
            new RepairToolInfo(
                "reset_network",
                "Restablecer configuración de red",
                "Restablece el stack de red de Windows a su estado predeterminado. Soluciona problemas de conectividad, DNS y adaptadores de red.",
                "Compatible con Windows 10/11",
                true,
                false
            ),
            new RepairToolInfo(
                "repair_store",
                "Reparar Windows Store",
                "Re-registra todas las aplicaciones de la Store. Las apps en uso o con una versión más nueva se omiten sin errores.",
                "Compatible con Windows 10/11",
                true,
                false
            ),
            new RepairToolInfo(
                "repair_profile",
                "Reparar perfil de usuario",
                "Escanea los perfiles de usuario del registro (ProfileList) y corrige el marcador de corrupción (State) cuando la carpeta del perfil está intacta. Útil para el error 'No podemos iniciar sesión en tu cuenta'.",
                "Compatible con Windows 10/11",
                false,
                false
            )
        };
    }
}