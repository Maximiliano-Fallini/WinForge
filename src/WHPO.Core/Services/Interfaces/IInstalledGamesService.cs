using System;
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
/// Avance de un escaneo de emuladores: porcentaje (0-100) y contexto del momento (la unidad en
/// curso, o una etiqueta como "Preparando unidades..."). El texto llega en español porque nace en
/// el servicio: la UI lo traduce con I18n antes de mostrarlo.
/// </summary>
public record EmulatorScanProgress(int Percent, string Current);

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

    /// <summary>
    /// Escaneo con emuladores profundos opcionales. Con <paramref name="deepEmulatorScan"/> en
    /// true se recorren además las unidades fijas/removibles buscando emuladores portables (el
    /// "Escaneo profundo" de la biblioteca): es la única parte que puede tardar minutos, así que
    /// solo corre cuando el usuario lo pide a mano. <paramref name="progress"/> informa el avance
    /// (porcentaje + contexto) para poder mostrarlo en la UI.
    /// </summary>
    Task<List<InstalledGame>> GetInstalledGamesAsync(bool refresh, bool deepEmulatorScan, IProgress<EmulatorScanProgress>? progress);

    /// <summary>
    /// Repara rutas de instalación que ya no existen en disco: vuelve a preguntarle a
    /// los launchers dónde está cada entrada rota y actualiza la biblioteca y su caché.
    /// Es la red de seguridad del enganche por ruta — los juegos de la Store, cuya
    /// carpeta lleva la versión en el nombre, cambian de carpeta con cada actualización,
    /// y cualquier juego o emulador que el usuario mueva también.
    /// No agrega ni quita juegos (respeta los borrados a propósito); si no hay ninguna
    /// ruta rota no escanea nada.
    /// </summary>
    /// <returns>Pares (entrada vieja, entrada reparada) para que el llamador actualice
    /// sus mapas; lista vacía si no hubo nada que reparar.</returns>
    Task<List<(InstalledGame Broken, InstalledGame Fresh)>> RepairMissingInstallPathsAsync(
        IReadOnlyList<InstalledGame> library);

    /// <summary>Carpetas elegidas por el usuario donde buscar emuladores portables (Capa 3).</summary>
    IReadOnlyList<string> GetEmulatorSearchFolders();

    /// <summary>Agrega una carpeta a la búsqueda de emuladores portables. False si ya estaba o no existe.</summary>
    bool AddEmulatorSearchFolder(string folder);

    /// <summary>¿Ya hay un resultado en caché (para no mostrar el skeleton de carga)?</summary>
    bool HasCachedResult { get; }

    /// <summary>Borra la caché de juegos instalados (memoria + archivo en disco): la próxima consulta re-escanea los launchers.</summary>
    void ClearCache();
}
