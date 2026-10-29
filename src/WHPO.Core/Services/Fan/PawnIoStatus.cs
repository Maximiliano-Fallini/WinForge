using Microsoft.Win32;

namespace WHPO.Core.Services.Fan;

/// <summary>
/// Estado real de PawnIO leído del registro en cada consulta.
///
/// No se usa <c>LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled</c> porque su
/// constructor estático cachea la versión UNA sola vez por proceso: si WinForge
/// arrancó sin el driver, después de instalarlo esa propiedad seguiría diciendo
/// que no está hasta reiniciar la app. Para el chequeo de la instalación (y para
/// el aviso del monitor de sensores) hace falta leer el estado actual.
///
/// LHM sí sigue usando su caché para cargar los módulos, así que después de
/// instalar PawnIO hay que reiniciar WinForge para que las lecturas del CPU
/// empiecen a funcionar. La UI lo avisa.
/// </summary>
internal static class PawnIoStatus
{
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    /// <summary>True si PawnIO está registrado en el sistema en este momento.</summary>
    public static bool IsInstalled => TryGetVersion(out _);

    /// <summary>
    /// Lee la versión instalada del registro. Prueba la vista de 64 y de 32 bits
    /// porque el instalador puede registrar la entrada en cualquiera de las dos.
    /// </summary>
    public static bool TryGetVersion(out string? version)
    {
        if (!OperatingSystem.IsWindows())
        {
            version = null;
            return false;
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(UninstallKey);
                if (key?.GetValue("DisplayVersion") is string text && Version.TryParse(text, out _))
                {
                    version = text;
                    return true;
                }
            }
            catch
            {
                // Sin acceso a esa vista del registro: se prueba la otra.
            }
        }

        version = null;
        return false;
    }
}
