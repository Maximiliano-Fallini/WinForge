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
    private const string RepoOwner = "Maximiliano-Fallini";
    private const string RepoName = "WinForge";
    private static string Repo => $"{RepoOwner}/{RepoName}";

    /// <summary>Nombre del repo (owner/name) para URLs públicas (p. ej. el link a releases).</summary>
    public const string RepositoryFullName = "Maximiliano-Fallini/WinForge";

    private static readonly string[] PrereleaseSuffixes = { "-beta", "-alpha", "-rc", "-preview", "-prerelease" };

    private readonly ILoggingService _logging;
    private readonly HttpClient _http;

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

            var json = await GetStringAsync($"https://api.github.com/repos/{Repo}/releases");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Sin releases publicadas: no hay nada que comparar.
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                _logging.LogWarning("AppUpdateService: el repositorio no tiene releases publicadas.");
                return new AppUpdateInfo { Status = AppUpdateStatus.Error, CurrentVersion = current };
            }

            // GitHub las ordena por fecha de publicación (más nueva primero).
            // Preferir la última estable; si todas son prerelease, usar la más reciente.
            var rootList = root.EnumerateArray().ToList();
            var release = rootList[0];
            foreach (var r in rootList)
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
                    // Solo MSIs instalables. La app publica el instalador x64 pero el
                    // nombre puede ser "WinForge.msi" o "WinForge-x64.msi": se toman
                    // ambos y se descartan explícitamente otras arquitecturas.
                    if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Contains("arm64", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Contains("x86", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("x64", StringComparison.OrdinalIgnoreCase)) continue;
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
                ReleaseNotesUrl = $"https://github.com/{Repo}/releases/tag/{tag}",
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
    /// Descarga el MSI de la actualización a %TEMP% y lo lanza con msiexec en modo
    /// silencioso y "requiere reinicio" planificado (ARPNOREMOVE + el CustomAction
    /// RunWinForgeAfterUpgrade del instalador la vuelve a abrir al terminar).
    /// </summary>
    public bool DownloadAndLaunchInstaller(string downloadUrl, string localMsiPath, string launchArgs)
    {
        try
        {
            var path = Environment.ExpandEnvironmentVariables(localMsiPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = _http.GetByteArrayAsync(downloadUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(path, bytes);
            _logging.LogInfo($"AppUpdateService: MSI descargado ({bytes.Length} bytes) a {path}");

            var psi = new ProcessStartInfo("msiexec.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("/qn");
            psi.ArgumentList.Add("REBOOT=ReallySuppress");
            psi.ArgumentList.Add("PROPERTY_PATH=" + launchArgs);
            var p = Process.Start(psi);
            if (p == null)
            {
                _logging.LogError("AppUpdateService: no se pudo lanzar msiexec.");
                return false;
            }
            _logging.LogInfo("AppUpdateService: instalador lanzado; la app se cerrará sola.");
            return true;
        }
        catch (Exception ex)
        {
            _logging.LogError("AppUpdateService: fallo al descargar/instalar la actualización.", ex);
            return false;
        }
    }

    private async Task<string> GetStringAsync(string url)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }
}
