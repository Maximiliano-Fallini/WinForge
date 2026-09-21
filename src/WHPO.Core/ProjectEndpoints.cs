namespace WHPO.Core;

/// <summary>
/// ÚNICO lugar donde se define el repositorio del proyecto y todo link que dependa
/// de él: chequeo/descarga de actualizaciones, catálogo del Workshop, paquetes de
/// idioma, y links públicos (releases, issues, estrellas).
///
/// Si el proyecto se muda a otro repositorio u organización, se cambian SOLO
/// <see cref="Owner"/> y <see cref="Name"/> (y <see cref="Branch"/> si la rama de
/// los catálogos cambia): el resto de las URLs se recalculan solas. Al mudarse hay
/// además que re-subir los assets de las releases de contenido y regenerar los
/// manifests con sus URLs nuevas (ver <see cref="ContentReleaseTag"/>).
///
/// Todos los valores son <c>const</c>, así que el compilador los pliega en un
/// literal único dentro del ensamblado: no hay costo en runtime y la URL final
/// queda verificable en el binario.
///
/// Regla: ningún servicio ni página vuelve a escribir a mano "github.com/..." ni
/// "raw.githubusercontent.com/..." con datos del repo propio; se usa esta clase.
/// </summary>
public static class ProjectEndpoints
{
    // ---------------------------------------------------------------------
    // Identidad del repositorio (lo único que se toca al mudar el proyecto)
    // ---------------------------------------------------------------------

    public const string Owner = "Maximiliano-Fallini";
    public const string Name = "WinForge";

    /// <summary>Rama desde la que se sirven los archivos sueltos (catálogos JSON).</summary>
    public const string Branch = "main";

    /// <summary>owner/name, tal como lo espera la API de GitHub.</summary>
    public const string FullName = Owner + "/" + Name;

    // ---------------------------------------------------------------------
    // Web pública
    // ---------------------------------------------------------------------

    public const string Web = "https://github.com/" + FullName;
    public const string Releases = Web + "/releases";
    public const string Issues = Web + "/issues";
    public const string Stargazers = Web + "/stargazers";

    /// <summary>Página de las notas de una versión publicada (v0.4.0, etc.).</summary>
    public static string ReleaseTag(string tag) => Releases + "/tag/" + tag;

    // ---------------------------------------------------------------------
    // API de GitHub
    // ---------------------------------------------------------------------

    /// <summary>Base de la API del repo (releases, tags, etc.).</summary>
    public const string Api = "https://api.github.com/repos/" + FullName;

    /// <summary>Listado de releases: lo usa el actualizador integrado.</summary>
    public const string ApiReleases = Api + "/releases";

    // ---------------------------------------------------------------------
    // Archivos sueltos y assets de release
    // ---------------------------------------------------------------------

    /// <summary>
    /// Base de raw.githubusercontent: sirve archivos del repo sin el límite de
    /// 60 peticiones/hora de la API (por eso los catálogos viven acá y no en la API).
    /// </summary>
    public const string RawBase = "https://raw.githubusercontent.com/" + FullName + "/" + Branch + "/";

    /// <summary>Un archivo suelto del repo (p. ej. "components.json").</summary>
    public static string Raw(string path) => RawBase + path;

    /// <summary>Base de descarga de assets de release.</summary>
    public const string ReleaseDownloadBase = Web + "/releases/download/";

    /// <summary>Asset de una release (descarga directa del archivo subido).</summary>
    public static string ReleaseAsset(string tag, string fileName) => ReleaseDownloadBase + tag + "/" + fileName;

    /// <summary>
    /// Release que funciona como bucket de contenido (no es una versión de la app):
    /// los assets que la app descarga en runtime se suben ahí, versionados en el
    /// nombre del archivo. Los catálogos apuntan a la URL completa de cada asset.
    /// </summary>
    public const string ContentReleaseTag = "components";

    /// <summary>
    /// Release donde se publican los paquetes de idioma (Fase 1 de los packs).
    /// Separada de la de componentes para poder actualizarla sin tocar la otra.
    /// </summary>
    public const string LanguagesReleaseTag = "languages";

    // ---------------------------------------------------------------------
    // Endpoints concretos de la app
    // ---------------------------------------------------------------------

    /// <summary>
    /// Catálogo de componentes del Workshop. Se puede sobrescribir en runtime con
    /// el setting "workshop.catalogUrl" (probar el flujo contra un servidor local).
    /// </summary>
    public const string ComponentCatalog = RawBase + "components.json";

    /// <summary>Catálogo de paquetes de idioma disponibles (Fase 1 de los packs).</summary>
    public const string LanguagesCatalog = RawBase + "languages.json";

    /// <summary>Asset de un paquete de idioma dentro de la release de idiomas.</summary>
    public static string LanguagePackAsset(string fileName) => ReleaseAsset(LanguagesReleaseTag, fileName);
}
