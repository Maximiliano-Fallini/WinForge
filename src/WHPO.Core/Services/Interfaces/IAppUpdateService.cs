using WHPO.Core.Services;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Actualizador integrado de la app: chequea releases de GitHub, descarga el MSI
/// y lanza el instalador silencioso que reemplaza la versión instalada.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>Versión de la app en ejecución (del ensamblado, "0.1.4").</summary>
    static string CurrentVersion() => AppUpdateService.CurrentVersion();

    /// <summary>Consulta la última release del repositorio y compara la versión.</summary>
    Task<AppUpdateInfo> CheckForUpdatesAsync();

    /// <summary>
    /// Descarga el MSI a <paramref name="localMsiPath"/> (puede incluir %TEMP%) y
    /// lanza la instalación silenciosa pasándole <paramref name="launchArgs"/> como
    /// property de msiexec (la que el instalador usa para reabrir la app al terminar).
    /// Devuelve true si el proceso se lanzó (la app debe cerrarse enseguida).
    /// </summary>
    bool DownloadAndLaunchInstaller(string downloadUrl, string localMsiPath, string launchArgs);
}