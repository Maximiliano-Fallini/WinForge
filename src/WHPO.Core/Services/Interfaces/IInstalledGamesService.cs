using System.Collections.Generic;
using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>Juego instalado detectado desde un launcher (Steam/Epic/GOG…).</summary>
public record InstalledGame(
    string Name,
    string ExeFileName,   // nombre del ejecutable (ej. "csgo.exe"); puede estar vacío
    string Launcher,      // "Steam", "Epic", ...
    string InstallPath,   // carpeta de instalación (puede estar vacía)
    string AppId = "",   // id del juego en el launcher (banners/caché, o código de producto Battle.net)
    string BannerUrl = "", // URL directa del banner (CDN de Steam); vacía si no hay
    string ArtNamespace = "", // namespace del catálogo de Epic (para buscar el banner en la API pública)
    string EpicAppName = ""); // AppName del manifest de Epic (para lanzar por URI del launcher: com.epicgames.launcher://apps/{AppName})

/// <summary>
/// Escanea las bibliotecas de los launchers instalados (Steam: appmanifest_*.acf;
/// Epic: manifests/*.item) para armar la lista de juegos instalados con su
/// ejecutable. Es la forma "profesional" de saber qué es un juego sin hardcodear
/// nombres: se lee la base de datos de cada launcher.
///
/// El escaneo es caro (lee registros, manifiestos, SQLite y hasta PowerShell), así
/// que el resultado se cachea: la primera vez se escanea y se guarda (memoria +
/// disco); las siguientes consultas devuelven la caché sin re-escannear. Un escaneo
/// nuevo solo ocurre con refresh=true (botón "Re-detectar" de la biblioteca).
/// </summary>
public interface IInstalledGamesService
{
    /// <summary>Juegos instalados desde la caché (no re-escanea si ya hay resultado guardado).</summary>
    Task<List<InstalledGame>> GetInstalledGamesAsync();

    /// <summary>Escaneo forzado de los launchers; actualiza la caché (memoria + disco).</summary>
    Task<List<InstalledGame>> GetInstalledGamesAsync(bool refresh);

    /// <summary>¿Ya hay un resultado en caché (para no mostrar el skeleton de carga)?</summary>
    bool HasCachedResult { get; }

    /// <summary>Borra la caché de juegos instalados (memoria + archivo en disco): la próxima consulta re-escanea los launchers.</summary>
    void ClearCache();
}
