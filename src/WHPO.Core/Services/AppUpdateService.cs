using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>Estado del chequeo de actualizaciones frente a la última release del repo.</summary>
public enum AppUpdateStatus
{
    /// <summary>La versión instalada es la última publicada (o no se pudo determinar).</summary>
    UpToDate,

    /// <summary>Hay una versión más nueva en el repositorio.</summary>
    UpdateAvailable,

    /// <summary>La build instalada es MÁS NUEVA que la última release publicada (build en desarrollo).</summary>
    DevelopmentBuild,

    /// <summary>El chequeo falló por un motivo no relacionado con la red (respuesta inesperada, sin releases, etc.).</summary>
    Error,

    /// <summary>No se pudo contactar al repositorio: falta de conexión a internet (o GitHub inaccesible/timeout).</summary>
    NoConnection
}

/// <summary>
/// Resultado de un chequeo de actualizaciones.
/// </summary>
public sealed class AppUpdateInfo
{
    public AppUpdateStatus Status { get; init; } = AppUpdateStatus.UpToDate;

    /// <summary>True si hay una versión más nueva instalable (Status == UpdateAvailable).</summary>
    public bool Available => Status == AppUpdateStatus.UpdateAvailable;
    public string? LatestVersion { get; init; }
    public string? CurrentVersion { get; init; }
    public string? ReleaseNotesUrl { get; init; }
    public string? DownloadUrl { get; init; }
}

/// <summary>
/// Una release de VERSIÓN de la app, para el historial de Configuración → Actualizaciones:
/// versión, fecha, notas y si el instalador se puede bajar desde la app.
/// </summary>
public sealed class AppReleaseInfo
{
    /// <summary>Versión sin la "v" del tag ("0.6.1").</summary>
    public string Version { get; init; } = "";
    public string Tag { get; init; } = "";
    /// <summary>Título de la release ("WinForge 0.6.1").</summary>
    public string Name { get; init; } = "";
    /// <summary>Notas de la release, en Markdown (lo que GitHub muestra como body).</summary>
    public string Notes { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }
    public bool Prerelease { get; init; }
    public string WebUrl { get; init; } = "";
    /// <summary>MSI instalable de esa release (null si la release no lo trae).</summary>
    public string? DownloadUrl { get; init; }
    public long SizeBytes { get; init; }
}

/// <summary>
/// Resultado de lanzar una actualización: si el instalador arrancó y, cuando no,
/// si el motivo fue no poder descargarlo. La UI usa <see cref="NoConnection"/> para
/// no culpar a la red cuando falló el disco o msiexec.
/// </summary>
public sealed record UpdateInstallOutcome(bool Launched, bool NoConnection = false);

/// <summary>
/// Actualizador integrado de WinForge: consulta las releases del repositorio
/// (GitHub API), compara la versión con la app instalada y descarga el MSI de la
/// última release para instalarlo sobre la versión anterior (MajorUpgrade).
///
/// El MSI se descarga a %TEMP% con un nombre fijo (WinForge-update.msi) y se lanza
/// con msiexec en modo silencioso. El instalador ya cierra WinForge (CustomAction
/// KillWinForge con taskkill) antes de reemplazar los archivos y luego lo relanza
/// con la ruta guardada en el registro RUN_WINFORGE_AFTER_UPDATE — esta se pasa
/// como PROPERTY de msiexec (PROPERTY="valor"), porque los argumentos de msiexec
/// con espacios rompen la línea de comandos de ProcessStartInfo de otra forma.
///
/// La versión actual se lee del InformationalVersion del ensamblado (0.1.5-beta,
/// definido por MSBuild a partir de &lt;Version&gt;). GitHub permite semver sin
/// la etiqueta de pre-release como tag (v0.1.5): la etiqueta -beta se ignora en
/// la comparación para que la app sepa que un v0.1.5 instalado ya incluye todo.
/// </summary>
public sealed class AppUpdateService : IAppUpdateService
{
    // Las URLs del repositorio (API de releases, notas de versión, link público)
    // viven en ProjectEndpoints: mudar el proyecto a otro repositorio se hace
    // SOLO ahí, sin tocar este servicio.

    private static readonly string[] PrereleaseSuffixes = { "-beta", "-alpha", "-rc", "-preview", "-prerelease" };

    private readonly ILoggingService _logging;
    private readonly HttpClient _http;

    // Historial de releases cacheado en memoria: abrir y cerrar la pestaña Actualizaciones
    // no puede gastar el límite de peticiones de la API de GitHub en cada visita (60/h
    // sin autenticar). El botón "Buscar actualizaciones" fuerza la recarga.
    private static readonly object ReleaseCacheLock = new();
    private static readonly TimeSpan ReleaseCacheTtl = TimeSpan.FromMinutes(5);
    private static IReadOnlyList<AppReleaseInfo>? _releaseCache;
    private static DateTime _releaseCacheAt = DateTime.MinValue;

    /// <summary>Ruta del MSI descargado (se conserva si el usuario cancela el reinicio).</summary>
    public const string MsiPath = "%TEMP%\\WinForge-update.msi";

    public AppUpdateService(ILoggingService logging)
    {
        _logging = logging;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinForge-Updater/1.0");
    }

    /// <summary>Versión de la app en ejecución ("0.1.5"): del ensamblado, sin pre-release.</summary>
    public static string CurrentVersion()
    {
        try
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (!string.IsNullOrWhiteSpace(info?.InformationalVersion))
                return StripPrerelease(info.InformationalVersion);
        }
        catch { }
        return "0.0.0";
    }

    /// <summary>Quita sufijos de pre-release ("0.1.5-beta" -> "0.1.5") y hashes de build.</summary>
    private static string StripPrerelease(string version)
    {
        var s = version.Trim();
        foreach (var suffix in PrereleaseSuffixes)
        {
            int i = s.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) s = s.Substring(0, i);
        }
        int plus = s.IndexOf('+');
        if (plus > 0) s = s.Substring(0, plus);
        return s;
    }

    /// <summary>
    /// Compara dos versiones "X.Y.Z" (partes opcionales) con reglas semver:
    /// mayor → menor → build. Devuelve > 0 si a > b.
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = ParseParts(a);
        var pb = ParseParts(b);
        for (int i = 0; i<3; i++)
        {
            int d = pa[i].CompareTo(pb[i]);
            if (d != 0) return d;
        }
        return 0;
    }

    private static int[] ParseParts(string v)
    {
        var parts = new[] { 0, 0, 0 };
        var tokens = v.Split('.');
        for (int i = 0; i < tokens.Length && i < 3; i++)
        {
            if (int.TryParse(tokens[i].Trim(), out var n)) parts[i] = n;
        }
        return parts;
    }

    /// <summary>Tag de una release ("" si no lo trae).</summary>
    private static string TagOf(JsonElement release)
        => release.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";

    /// <summary>
    /// True si el tag es una versión de la app: "v" + dígito (v0.1.2, v1.0.0). Deja afuera
    /// las releases de contenido, cuyos tags son nombres ("components", "languages").
    /// </summary>
    private static bool IsVersionTag(string tag)
        => tag.Length >= 2
           && (tag[0] == 'v' || tag[0] == 'V')
           && char.IsDigit(tag[1]);

    /// <summary>
    /// True si el asset es un instalador que la app puede bajar: MSI de la arquitectura
    /// de este build (se descartan arm64 y x86 suelto). La app publica el instalador x64
    /// pero el nombre puede ser "WinForge.msi" o "WinForge-x64.msi".
    /// </summary>
    private static bool IsInstallableMsi(string name)
        => name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
           && !name.Contains("arm64", StringComparison.OrdinalIgnoreCase)
           && (!name.Contains("x86", StringComparison.OrdinalIgnoreCase)
               || name.Contains("x64", StringComparison.OrdinalIgnoreCase));

    /// <summary>Chequea actualizaciones en el repositorio (GitHub Releases API).</summary>
    /// <remarks>
    /// Consulta la LISTA de releases (/releases) en vez de /releases/latest: ese
    /// endpoint devuelve 404 cuando todas las releases están marcadas como
    /// prerelease (que es el caso en releases preliminares del proyecto). La
    /// versión "objetivo" es la última release NO-prerelease; si todas lo son,
    /// se usa la más reciente.
    /// </remarks>
    public async Task<AppUpdateInfo> CheckForUpdatesAsync()
    {
        try
        {
            string current = CurrentVersion();
            if (current == "0.0.0")
            {
                _logging.LogWarning("AppUpdateService: no se pudo leer la versión de la app (0.0.0), se omite el chequeo.");
                return new AppUpdateInfo { Status = AppUpdateStatus.Error, CurrentVersion = current };
            }

            var json = await GetStringAsync(ProjectEndpoints.ApiReleases);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Sin releases publicadas: no hay nada que comparar.
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                _logging.LogWarning("AppUpdateService: el repositorio no tiene releases publicadas.");
                return new AppUpdateInfo { Status = AppUpdateStatus.Error, CurrentVersion = current };
            }

            // GitHub las ordena por fecha de publicación (más nueva primero).
            //
            // OJO: en la lista conviven las releases de CONTENIDO (los buckets "components"
            // y "languages", que solo llevan assets para la app) con las de la app. Sin
            // filtrarlas, un bucket publicado después de la última versión se tomaba como
            // "la última publicada", su tag ("languages") no parseaba como versión y TODAS
            // las máquinas veían "Versión X en desarrollo". Se acepta solo el formato de tag
            // de versión del proyecto (vX.Y.Z).
            var rootList = root.EnumerateArray().ToList();
            var releases = rootList.Where(r => IsVersionTag(TagOf(r))).ToList();
            if (releases.Count == 0)
            {
                _logging.LogWarning("AppUpdateService: el repositorio no tiene releases de versión (solo de contenido).");
                return new AppUpdateInfo { Status = AppUpdateStatus.UpToDate, CurrentVersion = current };
            }

            // Preferir la última estable; si todas son prerelease, usar la más reciente.
            var release = releases[0];
            foreach (var r in releases)
            {
                bool prerelease = r.TryGetProperty("prerelease", out var pr)
                    && pr.ValueKind == JsonValueKind.True && pr.GetBoolean();
                if (prerelease) continue;
                release = r;
                break;
            }

            string tag = release.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            string? version = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;

            int cmp = string.IsNullOrEmpty(version)
                ? 0
                : CompareVersions(StripPrerelease(version), StripPrerelease(current));

            var status = cmp switch
            {
 > 0 => AppUpdateStatus.UpdateAvailable,
                < 0 => AppUpdateStatus.DevelopmentBuild,
                _ => AppUpdateStatus.UpToDate
            };

            string? downloadUrl = null;
            if (status == AppUpdateStatus.UpdateAvailable && release.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (!IsInstallableMsi(name)) continue;
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (status == AppUpdateStatus.UpdateAvailable)
                _logging.LogInfo($"AppUpdateService: actualización disponible v{version} vs instalada v{current}.");
            else if (status == AppUpdateStatus.DevelopmentBuild)
                _logging.LogInfo($"AppUpdateService: la app v{current} va adelantada al repo (última publicada v{version}).");

            return new AppUpdateInfo
            {
                Status = status,
                LatestVersion = version,
                CurrentVersion = current,
                ReleaseNotesUrl = ProjectEndpoints.ReleaseTag(tag),
                DownloadUrl = downloadUrl
            };
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"AppUpdateService: no se pudo consultar actualizaciones: {ex.Message}");

            // Distinguir "sin conexión" de un error genuino: si no se pudo llegar a
            // GitHub (DNS/red caída, timeout del HttpClient, TLS), la UI muestra
            // "falta de conexión a internet" en vez de un error genérico.
            bool noConnection = ex is HttpRequestException
                or TaskCanceledException // timeout del HttpClient (25 s)
                or System.Net.Sockets.SocketException;
            return new AppUpdateInfo
            {
                Status = noConnection ? AppUpdateStatus.NoConnection : AppUpdateStatus.Error,
                CurrentVersion = CurrentVersion()
            };
        }
    }

    /// <summary>
    /// Historial de releases de VERSIÓN de la app (la más nueva primero), con fecha, notas
    /// e instalador. Deja afuera los buckets de contenido ("components", "languages"), que
    /// comparten el mismo endpoint pero no son versiones de la app.
    ///
    /// Lo usa la pestaña Actualizaciones de Configuración. La respuesta se cachea unos
    /// minutos en memoria: recorrer la pestaña no gasta el límite de la API de GitHub, y
    /// <paramref name="forceRefresh"/> lo saltea cuando el usuario pide buscar de nuevo.
    /// </summary>
    public async Task<IReadOnlyList<AppReleaseInfo>> GetReleasesAsync(int max = 10, bool forceRefresh = false)
    {
        int take = Math.Max(1, max);
        lock (ReleaseCacheLock)
        {
            if (!forceRefresh && _releaseCache != null
                && DateTime.UtcNow - _releaseCacheAt < ReleaseCacheTtl)
                return _releaseCache.Take(take).ToList();
        }

        var json = await GetStringAsync(ProjectEndpoints.ApiReleases);
        using var doc = JsonDocument.Parse(json);

        var releases = new List<AppReleaseInfo>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                string tag = TagOf(release);
                if (!IsVersionTag(tag)) continue;

                bool prerelease = release.TryGetProperty("prerelease", out var pr)
                    && pr.ValueKind == JsonValueKind.True && pr.GetBoolean();

                DateTimeOffset? publishedAt = null;
                if (release.TryGetProperty("published_at", out var pa)
                    && pa.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(
                        pa.GetString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var published))
                    publishedAt = published;

                string? downloadUrl = null;
                long sizeBytes = 0;
                if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string assetName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                        if (!IsInstallableMsi(assetName)) continue;
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var dl)
                            ? dl.GetString()
                            : null;
                        if (asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var bytes))
                            sizeBytes = bytes;
                        break;
                    }
                }

                releases.Add(new AppReleaseInfo
                {
                    Version = tag.Length > 1 ? tag.Substring(1) : tag,
                    Tag = tag,
                    Name = release.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                    PublishedAt = publishedAt,
                    Prerelease = prerelease,
                    WebUrl = release.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "",
                    DownloadUrl = downloadUrl,
                    SizeBytes = sizeBytes
                });
            }
        }

        releases = releases
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .ToList();

        lock (ReleaseCacheLock)
        {
            _releaseCache = releases;
            _releaseCacheAt = DateTime.UtcNow;
        }

        return releases.Take(take).ToList();
    }

    /// <summary>
    /// Descarga el MSI de la actualización a %TEMP% y lo lanza con msiexec en modo
    /// silencioso y "requiere reinicio" planificado (ARPNOREMOVE + el CustomAction
    /// RunWinForgeAfterUpgrade del instalador la vuelve a abrir al terminar).
    /// </summary>
    public UpdateInstallOutcome DownloadAndLaunchInstaller(string downloadUrl, string localMsiPath, string launchArgs)
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables(localMsiPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = _http.GetByteArrayAsync(downloadUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(path, bytes);
            _logging.LogInfo($"AppUpdateService: MSI descargado ({bytes.Length} bytes) a {path}");

            if (!File.Exists(path))
            {
                _logging.LogError($"AppUpdateService: el MSI no quedó guardado en {path}; no se puede instalar.");
                return new UpdateInstallOutcome(false);
            }

            var psi = new ProcessStartInfo("msiexec.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Argumentos como STRING (patrón del resto del proyecto): usar
                // ArgumentList.Add lanzaba msiexec SIN argumentos (el usuario veía
                // la ayuda de Windows Installer en vez de instalar, porque las
                // opciones /i /qn no llegaban a la línea de comandos).
                Arguments = "/i \"" + path + "\" /qn REBOOT=ReallySuppress PROPERTY_PATH=" + launchArgs
            };
            var p = Process.Start(psi);
            if (p == null)
            {
                _logging.LogError("AppUpdateService: no se pudo lanzar msiexec.");
                return new UpdateInstallOutcome(false);
            }
            _logging.LogInfo("AppUpdateService: instalador lanzado; la app se cerrará sola.");
            return new UpdateInstallOutcome(true);
        }
        catch (Exception ex)
        {
            _logging.LogError("AppUpdateService: fallo al descargar/instalar la actualización.", ex);
            return new UpdateInstallOutcome(false, NetworkError.IsConnectionFailure(ex));
        }
    }

    private async Task<string> GetStringAsync(string url)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }
}
