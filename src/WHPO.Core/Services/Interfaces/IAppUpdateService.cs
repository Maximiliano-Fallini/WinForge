using System.Collections.Generic;
using WHPO.Core.Services;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Actualizador integrado de la app: chequea releases de GitHub, descarga el MSI
/// y lanza el instalador silencioso que reemplaza la versión instalada.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>Versión de la app en ejecución (del ensamblado, "0.1.5").</summary>
    static string CurrentVersion() => AppUpdateService.CurrentVersion();

    /// <summary>Consulta la última release del repositorio y compara la versión.</summary>
    Task<AppUpdateInfo> CheckForUpdatesAsync();

    /// <summary>
    /// Historial de releases de versión de la app (la más nueva primero), para la pestaña
    /// Actualizaciones de Configuración. <paramref name="forceRefresh"/> saltea la caché
    /// en memoria (la usa el botón "Buscar actualizaciones").
    /// </summary>
    Task<IReadOnlyList<AppReleaseInfo>> GetReleasesAsync(int max = 10, bool forceRefresh = false);

    /// <summary>
    /// Descarga el MSI a <paramref name="localMsiPath"/> (puede incluir %TEMP%) y
    /// lanza la instalación silenciosa pasándole <paramref name="launchArgs"/> como
    /// property de msiexec (la que el instalador usa para reabrir la app al terminar).
    /// Devuelve si el proceso se lanzó (la app debe cerrarse enseguida) y, cuando no,
    /// si el motivo fue la conexión: la UI avisa distinto un corte de red que un
    /// fallo de disco o de msiexec.
    /// </summary>
    UpdateInstallOutcome DownloadAndLaunchInstaller(string downloadUrl, string localMsiPath, string launchArgs);
}
