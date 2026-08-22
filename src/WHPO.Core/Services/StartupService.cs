using System.Diagnostics;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Registra o elimina WHPO del inicio de sesion del usuario actual.
///
/// Usa una TAREA PROGRAMADA de Windows (schtasks) en lugar de la clave Run del
/// registro: la app exige administrador (manifest requireAdministrator) y las
/// entradas de Run se lanzan SIN elevacion al iniciar sesion, asi que el inicio
/// automatico fallaba en silencio (sobre todo con UAC en "elevar sin preguntar",
/// donde el exe ni siquiera llega a pedir consentimiento). Una tarea con
/// RunLevel=Highest arranca el exe ya elevado, sin prompt de UAC, tanto en modo
/// desarrollo como instalado.
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string TaskName = "WHPO";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WHPO";

    /// <summary>
    /// Flag que se agrega a la accion de la tarea cuando "Iniciar minimizado"
    /// está activo: la app solo se oculta en la bandeja al arrancar cuando Windows
    /// la lanza con este argumento (inicio de sesion), nunca en un arranque manual.
    /// </summary>
    public const string StartMinimizedArg = "--start-minimized";

    private readonly ILoggingService _loggingService;

    public StartupService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public bool IsEnabled()
    {
        try
        {
            // schtasks /Query devuelve 0 si la tarea existe, no-cero si no.
            return Run("schtasks.exe", $"/Query /TN \"{TaskName}\"") == 0;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("No se pudo comprobar el inicio automatico de WHPO.", ex);
            return false;
        }
    }

    /// <summary>
    /// Indica si la tarea de inicio trae el flag de minimizado. El XML de la tarea
    /// incluye <c>&lt;Arguments&gt;--start-minimized&lt;/Arguments&gt;</c> cuando está activo.
    /// </summary>
    public bool HasStartMinimizedFlag()
    {
        try
        {
            var (exitCode, stdout) = RunWithOutput("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML");
            return exitCode == 0 && stdout.Contains(StartMinimizedArg, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("No se pudo leer el valor de inicio automatico de WHPO.", ex);
            return false;
        }
    }

    public StartupOperationResult SetEnabled(bool enabled, bool startMinimized = false)
    {
        try
        {
            if (enabled)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    return new StartupOperationResult(false, "No se pudo determinar la ruta del ejecutable de WHPO.");
                }

                // /SC ONLOGON = al iniciar sesion; /RL HIGHEST = elevado (sin UAC).
                // Sin /RP queda "ejecutar solo cuando el usuario este conectado"
                // (token interactivo, no guarda contrasena). El /TR lleva el exe
                // entre comillas (la ruta puede tener espacios) + el flag si aplica.
                string argFlag = startMinimized ? " " + StartMinimizedArg : "";
                int exit = Run("schtasks.exe",
                    $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{executablePath}\\\"{argFlag}\"");
                if (exit != 0)
                {
                    return new StartupOperationResult(false, "No se pudo crear la tarea de inicio automatico (schtasks).");
                }

                // Migracion: si quedaba una entrada vieja en la clave Run (mecanismo
                // anterior), borrarla para no lanzar la app dos veces al iniciar sesion.
                DeleteRunValue();
                _loggingService.LogInfo(startMinimized
                    ? "Inicio automatico de WHPO activado (tarea programada, minimizado)."
                    : "Inicio automatico de WHPO activado (tarea programada).");
                return new StartupOperationResult(true, startMinimized
                    ? "WHPO se iniciara minimizado al iniciar sesion."
                    : "WHPO se iniciara al iniciar sesion.");
            }

            Run("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"");
            DeleteRunValue();
            _loggingService.LogInfo("Inicio automatico de WHPO desactivado.");
            return new StartupOperationResult(true, "WHPO ya no se iniciara automaticamente.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("No se pudo cambiar el inicio automatico de WHPO.", ex);
            return new StartupOperationResult(false, ex.Message);
        }
    }

    /// <summary>Borra la entrada vieja de la clave Run (solo migracion).</summary>
    private static void DeleteRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort: si falla, la tarea programada sigue funcionando.
        }
    }

    private static int Run(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(5000);
        return p.HasExited ? p.ExitCode : -1;
    }

    private static (int ExitCode, string StdOut) RunWithOutput(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        // Leer antes de esperar evita deadlock si la salida llena el pipe.
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return (p.HasExited ? p.ExitCode : -1, output ?? "");
    }
}
