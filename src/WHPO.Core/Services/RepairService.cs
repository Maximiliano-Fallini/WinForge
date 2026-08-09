using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// Indica si la aplicación se está ejecutando con privilegios de administrador.
    /// </summary>
    public bool IsRunningElevated() => IsElevated();

    /// <summary>
    /// Ejecuta un comando de forma asíncrona transmitiendo su salida en tiempo real
    /// a través de <paramref name="progress"/> y permitiendo cancelarlo con <paramref name="cancellationToken"/>.
    /// </summary>
    private async Task<RepairResult> RunStreamingProcessAsync(
        string command,
        string arguments,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool requiresAdmin = true)
    {
        try
        {
            if (requiresAdmin && !IsElevated())
            {
                return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
            }

            var executablePath = ResolveExecutablePath(command);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return new RepairResult(false, $"No se pudo resolver la herramienta '{command}'.", "La herramienta solicitada no está disponible en este sistema.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
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
                return new RepairResult(false, "No se pudo iniciar el proceso.", "El proceso de sistema no pudo crearse.");

            var output = new StringBuilder();

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
                    output.AppendLine(e.Data);
                    progress?.Report(e.Data);
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Monitor de actividad: indica si el proceso sigue usando CPU (avance real)
            var previousCpu = TimeSpan.Zero;
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeat = Task.Run(async () =>
            {
                try
                {
                    while (!heartbeatCts.IsCancellationRequested && !process.HasExited)
                    {
                        TimeSpan cpu = TimeSpan.Zero;
                        try { cpu = process.TotalProcessorTime; } catch { }
                        var delta = (cpu - previousCpu).TotalSeconds;
                        previousCpu = cpu;
                        progress?.Report(delta > 0.25 ? "[HB] ACTIVE" : "[HB] IDLE");
                        await Task.Delay(2000, heartbeatCts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            });

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                heartbeatCts.Cancel();
                try { process.Kill(entireProcessTree: true); } catch { }
                return new RepairResult(false, "Operación cancelada por el usuario.", "La herramienta fue cancelada manualmente.");
            }
            heartbeatCts.Cancel();
            try { await heartbeat; } catch { }

            var combinedOutput = output.ToString().Trim();
            var exitCodeSuccess = process.ExitCode == 0;
            var outputNormalized = NormalizeForComparison(combinedOutput);

            var knownFailurePatterns = new[]
            {
                "proteccion de recursos de windows no pudo iniciar el servicio de reparacion",
                "windows resource protection could not start the repair service",
                "proteccion de recursos de windows no pudo realizar la operacion solicitada",
                "windows resource protection could not perform the requested operation",
                "el servicio de reparacion no se pudo iniciar",
                "the repair service could not be started"
            };

            var hasKnownFailure = knownFailurePatterns.Any(p => outputNormalized.Contains(p));
            var success = exitCodeSuccess && !hasKnownFailure;

            var message = success
                ? "Comando ejecutado correctamente."
                : hasKnownFailure
                    ? "La herramienta reportó un error en su salida (aunque el código de salida sea 0)."
                    : $"La herramienta terminó con el código {process.ExitCode}.";

            _loggingService.LogInfo($"{command} {arguments}: {(success ? "Éxito" : "Fallo")} - ExitCode={process.ExitCode} - KnownFailure={hasKnownFailure}");

            progress?.Report($"[PASO] {command} finalizó con código {process.ExitCode}.");
            return new RepairResult(success, message, combinedOutput);
        }
        catch (OperationCanceledException)
        {
            return new RepairResult(false, "Operación cancelada por el usuario.", "");
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error ejecutando {command} {arguments}", ex);
            return new RepairResult(false, $"Error: {ex.Message}", ex.ToString());
        }
    }

    /// <summary>
    /// Ejecuta un comando de forma asíncrona y captura la salida.
    /// </summary>
    private async Task<RepairResult> RunCommandAsync(string command, string arguments, bool requiresAdmin = true)
    {
        try
        {
            if (requiresAdmin && !IsElevated())
            {
                return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
            }

            var executablePath = ResolveExecutablePath(command);
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return new RepairResult(false, $"No se pudo resolver la herramienta '{command}'.", "La herramienta solicitada no está disponible en este sistema.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
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
                return new RepairResult(false, "No se pudo iniciar el proceso.", "El proceso de sistema no pudo crearse.");

            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    output.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    error.AppendLine(e.Data);
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            var combinedOutput = (output.Length > 0 && error.Length > 0)
                ? string.Join(Environment.NewLine, output.ToString().Trim(), error.ToString().Trim())
                : output.ToString().Trim() + error.ToString().Trim();
            
            // Determinar éxito real: exit code 0 Y no hay errores conocidos en la salida
            var exitCodeSuccess = process.ExitCode == 0;
            var outputNormalized = NormalizeForComparison(combinedOutput);
            
            // Errores conocidos de SFC que devuelven exit code 0 pero son fallos reales
            var knownFailurePatterns = new[]
            {
                "proteccion de recursos de windows no pudo iniciar el servicio de reparacion",
                "windows resource protection could not start the repair service",
                "proteccion de recursos de windows no pudo realizar la operacion solicitada",
                "windows resource protection could not perform the requested operation",
                "el servicio de reparacion no se pudo iniciar",
                "the repair service could not be started"
            };
            
            var hasKnownFailure = knownFailurePatterns.Any(p => outputNormalized.Contains(p));
            var success = exitCodeSuccess && !hasKnownFailure;
            
            var message = success
                ? "Comando ejecutado correctamente."
                : hasKnownFailure
                    ? "La herramienta reportó un error en su salida (aunque el código de salida sea 0)."
                    : $"La herramienta terminó con el código {process.ExitCode}.";

            _loggingService.LogInfo($"{command} {arguments}: {(success ? "Éxito" : "Fallo")} - ExitCode={process.ExitCode} - KnownFailure={hasKnownFailure}");

            return new RepairResult(success, message, combinedOutput, false);
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error ejecutando {command} {arguments}", ex);
            return new RepairResult(false, $"Error: {ex.Message}", ex.ToString());
        }
    }

    private async Task<RepairResult> RunShellCommandAsync(string command, bool requiresAdmin = true)
    {
        try
        {
            if (requiresAdmin && !IsElevated())
            {
                return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
            }

            var cmdPath = ResolveExecutablePath("cmd");
            if (string.IsNullOrWhiteSpace(cmdPath))
            {
                return new RepairResult(false, "No se pudo encontrar cmd.exe.", "El shell de Windows no está disponible.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = cmdPath,
                Arguments = $"/d /c \"{command}\"",
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
                return new RepairResult(false, "No se pudo iniciar el shell de Windows.", "El proceso de shell no pudo crearse.");

            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    output.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    error.AppendLine(e.Data);
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
                "acceso denegado"
            };
            var hasKnownFailure = knownFailurePatterns.Any(p => outputNormalized.Contains(p));
            var success = exitCodeSuccess && !hasKnownFailure;
            
            var message = success
                ? "Comando de shell ejecutado correctamente."
                : hasKnownFailure
                    ? "El comando reportó un error en su salida."
                    : $"El comando terminó con el código {process.ExitCode}.";

            return new RepairResult(success, message, combinedOutput);
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error ejecutando shell command {command}", ex);
            return new RepairResult(false, $"Error: {ex.Message}", ex.ToString());
        }
    }

    private async Task<RepairResult> RunPowerShellCommandAsync(string script, bool requiresAdmin = true)
    {
        try
        {
            if (requiresAdmin && !IsElevated())
            {
                return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
            }

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
                    output.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    error.AppendLine(e.Data);
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

    /// <summary>
    /// Verifica e inicia los servicios requeridos para SFC/DISM (TrustedInstaller / Windows Modules Installer).
    /// </summary>
    private async Task<bool> EnsureRequiredServicesRunningAsync()
    {
        try
        {
            // Nombres posibles del servicio (inglés/español)
            var serviceNames = new[] { "trustedinstaller", "TrustedInstaller" };
            
            foreach (var serviceName in serviceNames)
            {
                if (await IsServiceRunningAsync(serviceName))
                {
                    _loggingService.LogInfo($"Servicio {serviceName} ya está corriendo.");
                    return true;
                }
            }

            _loggingService.LogWarning("TrustedInstaller no está corriendo. Intentando iniciarlo...");

            // Intentar iniciar el servicio usando PowerShell con elevación (funciona mejor que sc.exe con runas)
            foreach (var serviceName in serviceNames)
            {
                if (await StartServiceElevatedAsync(serviceName))
                {
                    // Esperar a que inicie
                    await Task.Delay(5000);
                    
                    if (await IsServiceRunningAsync(serviceName))
                    {
                        _loggingService.LogInfo($"Servicio {serviceName} iniciado correctamente.");
                        return true;
                    }
                }
            }

            _loggingService.LogError("No se pudo iniciar TrustedInstaller. SFC/DISM fallarán.");
            return false;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error verificando/iniciando servicios requeridos", ex);
            return false;
        }
    }

    private async Task<bool> IsServiceRunningAsync(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(NativeSystemDirectory, "sc.exe"),
                Arguments = $"query {serviceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync();
            var output = await process.StandardOutput.ReadToEndAsync();

            // Detección robusta del estado "corriendo" en inglés y español (localización de sc.exe)
            var outputUpper = output.ToUpperInvariant();
            return outputUpper.Contains("RUNNING")
                || outputUpper.Contains("EN EJECUCI")
                || outputUpper.Contains("EJECUTANDO")
                || outputUpper.Contains("EJECUTANDOSE");
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> StartServiceElevatedAsync(string serviceName)
    {
        try
        {
            // Usar PowerShell con -Verb RunAs para elevación real
            var script = $"Start-Service -Name '{serviceName}' -ErrorAction Stop";
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellExePath,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -EncodedCommand {encodedScript}",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error iniciando servicio {serviceName} con elevación", ex);
            return false;
        }
    }

    private bool LaunchVisibleElevatedConsole(string commandLine, string description)
    {
        try
        {
            if (!IsElevated())
            {
                _loggingService.LogWarning($"{description} requiere elevación. Se necesita ejecutar WHPO como administrador.");
                return false;
            }

            var cmdPath = ResolveExecutablePath("cmd");
            if (string.IsNullOrWhiteSpace(cmdPath))
            {
                _loggingService.LogWarning("No se pudo encontrar cmd.exe para abrir una consola visible.");
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = cmdPath,
                Arguments = $"/k {commandLine}",
                UseShellExecute = true,
                CreateNoWindow = false,
                WorkingDirectory = SystemWorkingDirectory,
                Verb = "runas"
            };

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error abriendo consola visible para {description}", ex);
            return false;
        }
    }

    public async Task<RepairResult> RunSFCAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        _loggingService.LogInfo("Iniciando SFC /scannow...");
        progress?.Report("[PASO] Iniciando SFC /scannow...");

        if (!IsElevated())
        {
            return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
        }

        // Verificar e iniciar servicios requeridos (TrustedInstaller)
        var servicesOk = await EnsureRequiredServicesRunningAsync();
        if (!servicesOk)
        {
            return new RepairResult(false, "No se pudo iniciar el servicio 'Windows Modules Installer' (TrustedInstaller) requerido para SFC.",
                "Posibles soluciones:\n" +
                "1. Abre services.msc → busca 'Windows Modules Installer' → ponlo en 'Manual' o 'Automático' e inícialo\n" +
                "2. Ejecuta en PowerShell como admin: Set-Service trustedinstaller -StartupType Manual; Start-Service trustedinstaller\n" +
                "3. Verifica que no esté deshabilitado en el registro: HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\TrustedInstaller → Start = 2 (Automático) o 3 (Manual)", true);
        }

        progress?.Report("[PASO] Escaneando archivos de sistema protegidos (puede tardar 10-20 min)...");
        // Ejecutar SFC capturando la salida directamente
        var result = await RunStreamingProcessAsync("sfc", "/scannow", progress, cancellationToken);
        
        // Si SFC falla con el error específico, dar más detalles (comparación insensible a acentos)
        var detailsNormalized = NormalizeForComparison(result.Details ?? "");
        if (!result.Success && (detailsNormalized.Contains("proteccion de recursos") || detailsNormalized.Contains("resource protection")))
        {
            return new RepairResult(false, "SFC falló: Protección de recursos de Windows no pudo iniciar el servicio de reparación.",
                result.Details + "\n\n" +
                "Esto suele pasar porque el servicio 'Windows Modules Installer' (TrustedInstaller) no está corriendo o está deshabilitado.\n" +
                "Soluciones:\n" +
                "1. services.msc → 'Windows Modules Installer' → Iniciar (tipo: Manual/Automático)\n" +
                "2. PowerShell admin: Set-Service trustedinstaller -StartupType Manual; Start-Service trustedinstaller\n" +
                "3. Registro: HKLM\\SYSTEM\\CurrentControlSet\\Services\\TrustedInstaller → Start = 2 o 3\n" +
                "4. Reinicia Windows y vuelve a intentarlo", true);
        }

        return result;
    }

    public async Task<RepairResult> RunDISMAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        _loggingService.LogInfo("Iniciando DISM /RestoreHealth...");
        progress?.Report("[PASO] Iniciando DISM /RestoreHealth...");

        if (!IsElevated())
        {
            return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
        }

        // Verificar e iniciar servicios requeridos (TrustedInstaller)
        var servicesOk = await EnsureRequiredServicesRunningAsync();
        if (!servicesOk)
        {
            return new RepairResult(false, "No se pudo iniciar el servicio 'Windows Modules Installer' (TrustedInstaller) requerido para DISM.", "Intenta iniciar el servicio manualmente desde services.msc y vuelve a intentarlo.", true);
        }

        progress?.Report("[PASO] Reparando la imagen de Windows (puede tardar 10-30 min, descargando desde Windows Update)...");
        // Ejecutar DISM capturando la salida directamente
        return await RunStreamingProcessAsync("dism", "/Online /Cleanup-Image /RestoreHealth", progress, cancellationToken);
    }

    public async Task<RepairResult> RunCHKDSKAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        _loggingService.LogInfo("Iniciando CHKDSK C: /scan...");
        progress?.Report("[PASO] Iniciando CHKDSK C: /scan...");

        if (!IsElevated())
        {
            return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
        }

        progress?.Report("[PASO] Verificando el sistema de archivos del disco C:...");
        // CHKDSK /scan se puede ejecutar en línea, pero /f o /r requieren reinicio
        // Usamos /scan para verificación en línea sin reinicio
        return await RunStreamingProcessAsync("chkdsk", "C: /scan", progress, cancellationToken);
    }

    public async Task<RepairResult> RepairComponentStoreAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        _loggingService.LogInfo("Iniciando reparación del almacén de componentes...");
        progress?.Report("[PASO] Iniciando reparación del almacén de componentes...");

        if (!IsElevated())
        {
            return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
        }

        // Verificar e iniciar servicios requeridos (TrustedInstaller)
        var servicesOk = await EnsureRequiredServicesRunningAsync();
        if (!servicesOk)
        {
            return new RepairResult(false, "No se pudo iniciar el servicio 'Windows Modules Installer' (TrustedInstaller) requerido para DISM.", "Intenta iniciar el servicio manualmente desde services.msc y vuelve a intentarlo.", true);
        }

        progress?.Report("[PASO] Limpiando el almacén de componentes WinSxS (puede tardar 10-30 min)...");
        // Ejecutar DISM capturando la salida directamente
        return await RunStreamingProcessAsync("dism", "/Online /Cleanup-Image /StartComponentCleanup /ResetBase", progress, cancellationToken);
    }

    public async Task<RepairResult> ResetNetworkAsync()
    {
        _loggingService.LogInfo("Restableciendo configuración de red...");

        if (!IsElevated())
        {
            return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
        }

        // Ejecutar comandos de red capturando la salida
        var result1 = await RunShellCommandAsync("netsh winsock reset", true);
        var result2 = await RunShellCommandAsync("netsh int ip reset", true);

        var combinedOutput = string.Join(Environment.NewLine, result1.Details ?? "", result2.Details ?? "");
        var success = result1.Success && result2.Success;

        return new RepairResult(success, success ? "Configuración de red restablecida correctamente." : "Hubo errores al restablecer la red.", combinedOutput);
    }

    public async Task<RepairResult> FlushDNSAsync()
    {
        _loggingService.LogInfo("Vaciando caché de DNS...");

        if (!IsElevated())
        {
            return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
        }

        // Ejecutar ipconfig /flushdns capturando la salida
        return await RunShellCommandAsync("ipconfig /flushdns", true);
    }

    public async Task<RepairResult> RepairStoreAsync()
    {
        _loggingService.LogInfo("Reparando Windows Store...");

        if (!IsElevated())
        {
            return new RepairResult(false, "Esta herramienta requiere privilegios de administrador. Reinicia WHPO como administrador y vuelve a intentarlo.", "La ejecución se detuvo porque la app no está corriendo con elevación.", true);
        }

        // Ejecutar PowerShell para reparar la Store capturando la salida
        var script = "$packages = Get-AppxPackage -AllUsers; foreach ($pkg in $packages) { try { Add-AppxPackage -DisableDevelopmentMode -Register \"$($pkg.InstallLocation)\\AppXManifest.xml\" -ErrorAction SilentlyContinue } catch {} }";
        return await RunPowerShellCommandAsync(script, true);
    }

    public async Task<RepairResult> RepairUserProfileAsync()
    {
        _loggingService.LogInfo("Verificando perfil de usuario...");

        var profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ntuserPath = Path.Combine(profilePath, "NTUSER.DAT");
        var details = new List<string>();

        if (File.Exists(ntuserPath))
            details.Add($"Perfil encontrado en {ntuserPath}");
        else
            details.Add("No se encontró NTUSER.DAT en el perfil actual.");

        details.Add($"Ruta del perfil: {profilePath}");

        var success = File.Exists(ntuserPath);
        return success
            ? new RepairResult(true, "El perfil de usuario parece estar disponible y se ha validado correctamente. No se han aplicado cambios automáticos.", string.Join(Environment.NewLine, details))
            : new RepairResult(false, "No se pudo validar el perfil de usuario actual.", string.Join(Environment.NewLine, details));
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
                "Re-registra todas las aplicaciones de la Store y repara el funcionamiento de la Microsoft Store y sus aplicaciones.",
                "Compatible con Windows 10/11",
                true,
                false
            ),
            new RepairToolInfo(
                "repair_profile",
                "Reparar perfil de usuario",
                "Verifica la integridad del perfil de usuario actual. Útil para detectar corrupción en el registro de usuario.",
                "Compatible con Windows 10/11",
                false,
                false
            )
        };
    }
}