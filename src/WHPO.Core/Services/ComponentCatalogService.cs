using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WHPO.Core.Components;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

public enum ComponentStage { Downloading, Verifying, Extracting, Loading, Done }

/// <summary>Progreso de una instalación de componente (para la card del Workshop).</summary>
public sealed record ComponentProgress(ComponentStage Stage, int Percent);

/// <summary>Resultado de traer el catálogo (components.json) desde GitHub.</summary>
public sealed class CatalogFetchResult
{
    public ComponentCatalog? Catalog { get; init; }
    /// <summary>True si el catálogo salió de la copia local por no poder llegar a GitHub.</summary>
    public bool FromCache { get; init; }
    /// <summary>Fecha de la copia local (si FromCache).</summary>
    public DateTime? CachedAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>Resultado de una instalación.</summary>
public sealed class InstallOutcome
{
    public bool Success { get; init; }
    /// <summary>Mensaje de error (texto fuente en español, pasable por I18n.T).</summary>
    public string? Error { get; init; }
    public IWinForgeComponent? Component { get; init; }
}

/// <summary>
/// Workshop de WinForge: catálogo + instalación/desinstalación de componentes.
///
/// Cómo se descarga un componente (paso a paso):
///  1. El catálogo vive en el repo (raw.githubusercontent.com/&lt;repo&gt;/main/components.json),
///     cacheado localmente para modo offline. raw (no la API) evita el límite de 60 req/h.
///  2. "Instalar": descarga el asset zip de la Release indicado por la entrada,
///     verifica SHA-256 contra el manifest, extrae a
///     %LOCALAPPDATA%\WHPO\Modules\&lt;id&gt;\&lt;versión&gt; (atómico: temp → rename),
///     carga la DLL con un AssemblyLoadContext aislado y valida MinAppVersion.
///  3. "Desinstalar": quita el registro y renombra la carpeta a *.uninstalled-*;
///     el assembly queda en memoria hasta reiniciar la app (descarga final del ALC),
///     y esas carpetas se borran en el arranque siguiente.
/// Un update de la app JAMÁS crashea por un módulo viejo: si MinAppVersion no se
/// cumple, el componente no se carga (la UI muestra "requiere WinForge X.Y").
/// </summary>
public sealed class ComponentCatalogService
{
    private const string RepoOwner = "Maximiliano-Fallini";
    private const string RepoName = "WinForge";

    private static string Repo => $"{RepoOwner}/{RepoName}";

    private readonly ILoggingService _logging;
    private readonly ISettingsService _settings;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// URL del catálogo. Se puede sobrescribir con el setting "workshop.catalogUrl"
    /// (útil para probar el flujo contra un servidor local antes de publicar al repo).
    /// </summary>
    public string CatalogUrl =>
        _settings.Get("workshop.catalogUrl", $"https://raw.githubusercontent.com/{Repo}/main/components.json");

    private static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WHPO");
    private static string ModulesRoot => Path.Combine(RootDir, "Modules");
    private static string StorePath => Path.Combine(RootDir, "modules.json");
    private static string CachePath => Path.Combine(RootDir, "components.cache.json");

    public ComponentCatalogService(ISettingsService settings, ILoggingService logging)
    {
        _settings = settings;
        _logging = logging;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinForge-Workshop/1.0");
    }

    // =====================================================================
    // Catálogo
    // =====================================================================

    /// <summary>Trae el catálogo desde el repo; si falla, devuelve la copia cacheada.</summary>
    public async Task<CatalogFetchResult> FetchCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await GetStringAsync(CatalogUrl, ct);
            var catalog = JsonSerializer.Deserialize<ComponentCatalog>(json, _json);
            if (catalog == null) throw new InvalidDataException("components.json vacío o inválido.");
            try
            {
                Directory.CreateDirectory(RootDir);
                await File.WriteAllTextAsync(CachePath, json, ct);
            }
            catch (Exception ex) { _logging.LogWarning($"Workshop: no se pudo cachear el catálogo: {ex.Message}"); }
            return new CatalogFetchResult { Catalog = catalog };
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Workshop: no se pudo bajar el catálogo: {ex.Message}");
            // Fallback: última copia buena guardada en disco (modo offline).
            try
            {
                if (File.Exists(CachePath))
                {
                    var cached = JsonSerializer.Deserialize<ComponentCatalog>(await File.ReadAllTextAsync(CachePath, ct), _json);
                    var cachedAt = File.GetLastWriteTime(CachePath);
                    if (cached != null)
                        return new CatalogFetchResult { Catalog = cached, FromCache = true, CachedAt = cachedAt };
                }
            }
            catch (Exception ex2) { _logging.LogWarning($"Workshop: la copia cacheada tampoco se pudo leer: {ex2.Message}"); }
            return new CatalogFetchResult { Error = ex.Message };
        }
    }

    // =====================================================================
    // Instalados (persistencia en modules.json)
    // =====================================================================

    /// <summary>Lista de componentes instalados (modules.json).</summary>
    public List<InstalledComponentRecord> GetInstalled()
    {
        try
        {
            if (!File.Exists(StorePath)) return new List<InstalledComponentRecord>();
            var store = JsonSerializer.Deserialize<InstalledStore>(File.ReadAllText(StorePath), _json);
            return store?.Installed ?? new List<InstalledComponentRecord>();
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Workshop: no se pudo leer modules.json: {ex.Message}");
            return new List<InstalledComponentRecord>();
        }
    }

    private void SaveInstalled(List<InstalledComponentRecord> list)
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(new InstalledStore { Installed = list }, _json));
    }

    private sealed class InstalledStore
    {
        public List<InstalledComponentRecord> Installed { get; set; } = new();
    }

    /// <summary>
    /// Al arrancar: borra carpetas de desinstalaciones pendientes (*.uninstalled-*).
    /// El DLL quedó cargado en la sesión anterior y no se podía borrar en caliente;
    /// ahora la app arrancó de nuevo y sí.
    /// </summary>
    public void CleanupPendingUninstalls()
    {
        try
        {
            if (!Directory.Exists(ModulesRoot)) return;
            foreach (var dir in Directory.GetDirectories(ModulesRoot, "*.uninstalled-*"))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception ex) { _logging.LogWarning($"Workshop: no se pudo borrar {dir}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { _logging.LogWarning($"Workshop: limpieza de desinstalaciones: {ex.Message}"); }
    }

    // =====================================================================
    // Carga de componentes (AssemblyLoadContext aislado)
    // =====================================================================

    /// <summary>
    /// Carga un componente instalado desde su carpeta. Devuelve null + error si el
    /// assembly no se puede cargar o si MinAppVersion no se cumple.
    /// </summary>
    public IWinForgeComponent? LoadInstalled(InstalledComponentRecord record, out string? error)
    {
        error = null;
        try
        {
            if (!Directory.Exists(record.Path))
            {
                error = "La carpeta del componente ya no existe.";
                return null;
            }
            var ctx = new ComponentLoadContext(record.Path);
            var component = FindComponentInContext(ctx, record.Path, out var loadError);
            if (component == null)
            {
                error = loadError;
                return null;
            }
            if (!IsVersionCompatible(component.MinAppVersion))
            {
                error = $"Incompatible: requiere WinForge {component.MinAppVersion} o superior.";
                return null;
            }
            return component;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logging.LogError($"Workshop: error cargando componente '{record.Id}': {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>True si la versión actual de la app cumple el mínimo del componente.</summary>
    public static bool IsVersionCompatible(string minAppVersion)
    {
        if (string.IsNullOrWhiteSpace(minAppVersion)) return true;
        return CompareVersions(AppUpdateService.CurrentVersion(), minAppVersion) >= 0;
    }

    /// <summary>Compara versiones semver "X.Y.Z" (&gt;0 si a es más nueva que b).</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = ParseParts(a);
        var pb = ParseParts(b);
        for (int i = 0; i < 3; i++)
        {
            int d = pa[i].CompareTo(pb[i]);
            if (d != 0) return d;
        }
        return 0;
    }

    private static int[] ParseParts(string v)
    {
        var parts = new[] { 0, 0, 0 };
        var tokens = v.Trim().Split('.');
        for (int i = 0; i < tokens.Length && i < 3; i++)
            if (int.TryParse(tokens[i].Trim(), out var n)) parts[i] = n;
        return parts;
    }

    /// <summary>Carga los componentes instalados para registrarlos en el registry al arrancar.</summary>
    public List<IWinForgeComponent> LoadInstalledComponents()
    {
        var result = new List<IWinForgeComponent>();
        foreach (var record in GetInstalled())
        {
            var component = LoadInstalled(record, out var error);
            if (component == null)
            {
                _logging.LogWarning($"Workshop: '{record.Id}' no se cargó ({error}).");
                continue;
            }
            result.Add(component);
            _logging.LogInfo($"Workshop: componente instalado cargado: {component.Id} v{component.Version}.");
        }
        return result;
    }

    // =====================================================================
    // Instalación / desinstalación
    // =====================================================================

    /// <summary>
    /// Descarga e instala el componente del catálogo. Reporta progreso (etapa +
    /// porcentaje). Al terminar, el componente queda registrado en modules.json.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(ComponentCatalogEntry entry, IProgress<ComponentProgress>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Url))
            return new InstallOutcome { Success = false, Error = "La entrada del catálogo no tiene URL de descarga." };
        if (!IsVersionCompatible(entry.MinAppVersion))
            return new InstallOutcome { Success = false, Error = $"Incompatible: requiere WinForge {entry.MinAppVersion} o superior." };

        string? tempZip = null;
        try
        {
            // 1) Descarga con progreso (a un temp antes de verificar).
            Directory.CreateDirectory(ModulesRoot);
            tempZip = Path.Combine(ModulesRoot, $"{entry.Id}-{entry.Version}.zip.tmp");
            progress?.Report(new ComponentProgress(ComponentStage.Downloading, 0));
            await DownloadToFileAsync(entry.Url, tempZip, entry.SizeBytes, progress, ct);

            // 2) Verificación de integridad: SHA-256 del archivo descargado vs manifest.
            progress?.Report(new ComponentProgress(ComponentStage.Verifying, 100));
            var actualHash = await ComputeSha256Async(tempZip, ct);
            var expected = (entry.Sha256 ?? "").Trim().ToLowerInvariant();
            if (expected.Length > 0 && !string.Equals(actualHash, expected, StringComparison.Ordinal))
            {
                _logging.LogWarning($"Workshop: fallo de integridad para '{entry.Id}': esperado {expected}, obtenido {actualHash}.");
                return new InstallOutcome { Success = false, Error = "Fallo de integridad: el archivo descargado no coincide con el manifest (SHA-256)." };
            }

            // 3) Extracción atómica: primero a una carpeta temp, luego rename.
            var destDir = Path.Combine(ModulesRoot, entry.Id, entry.Version);
            var tmpDir = destDir + ".tmp";
            progress?.Report(new ComponentProgress(ComponentStage.Extracting, 100));
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
            ZipFile.ExtractToDirectory(tempZip, tmpDir, overwriteFiles: true);
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
            Directory.Move(tmpDir, destDir);

            // 4) Carga y validación del componente.
            progress?.Report(new ComponentProgress(ComponentStage.Loading, 100));
            var record = new InstalledComponentRecord
            {
                Id = entry.Id,
                Version = entry.Version,
                Path = destDir,
                InstalledAt = DateTime.Now.ToString("O", CultureInfo.InvariantCulture)
            };
            var component = LoadInstalled(record, out var loadError);
            if (component == null)
            {
                try { Directory.Delete(destDir, recursive: true); } catch { }
                return new InstallOutcome { Success = false, Error = loadError ?? "El componente no se pudo cargar." };
            }

            // 5) Persistir en modules.json (reemplaza si ya estaba).
            var all = GetInstalled();
            all.RemoveAll(r => string.Equals(r.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            all.Add(record);
            SaveInstalled(all);

            _logging.LogInfo($"Workshop: componente instalado: {entry.Id} v{entry.Version} ({actualHash[..12]}…).");
            progress?.Report(new ComponentProgress(ComponentStage.Done, 100));
            return new InstallOutcome { Success = true, Component = component };
        }
        catch (OperationCanceledException)
        {
            return new InstallOutcome { Success = false, Error = "Instalación cancelada." };
        }
        catch (Exception ex)
        {
            _logging.LogError($"Workshop: error instalando '{entry.Id}': {ex.Message}", ex);
            return new InstallOutcome { Success = false, Error = $"No se pudo instalar el componente: {ex.Message}" };
        }
        finally
        {
            try { if (tempZip != null && File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// <summary>
    /// Desinstala un componente: quita el registro de modules.json y renombra su
    /// carpeta (el DLL cargado no se puede borrar en caliente; la carpeta renombrada
    /// se borra en el próximo arranque).
    /// </summary>
    public bool Uninstall(string id)
    {
        try
        {
            var all = GetInstalled();
            var record = all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            all.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            SaveInstalled(all);
            if (record != null && Directory.Exists(record.Path))
            {
                var graveyard = record.Path + ".uninstalled-" + DateTime.UtcNow.Ticks;
                try { Directory.Move(record.Path, graveyard); }
                catch (Exception ex) { _logging.LogWarning($"Workshop: no se pudo renombrar la carpeta de '{id}': {ex.Message}"); }
            }
            _logging.LogInfo($"Workshop: componente desinstalado: {id} (el DLL se libera por completo al reiniciar la app).");
            return true;
        }
        catch (Exception ex)
        {
            _logging.LogError($"Workshop: error desinstalando '{id}': {ex.Message}", ex);
            return false;
        }
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private async Task DownloadToFileAsync(string url, string destPath, long expectedSize, IProgress<ComponentProgress>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buffer = new byte[64 * 1024];
        long total = expectedSize > 0 ? expectedSize : resp.Content.Headers.ContentLength ?? 0;
        long read = 0;
        int lastReported = -1;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
            {
                int pct = (int)Math.Min(100, read * 100 / total);
                if (pct != lastReported)
                {
                    lastReported = pct;
                    progress?.Report(new ComponentProgress(ComponentStage.Downloading, pct));
                }
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Busca el primer tipo exportado que implemente IWinForgeComponent dentro de
    /// las DLLs de la carpeta, cargadas en el ALC dado. Los DLL que fallen (nativos,
    /// parciales) se saltan uno por uno.
    /// </summary>
    private static IWinForgeComponent? FindComponentInContext(ComponentLoadContext ctx, string folder, out string? error)
    {
        error = null;
        var dlls = Directory.GetFiles(folder, "*.dll")
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
        foreach (var dll in dlls)
        {
            if (Path.GetFileName(dll).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)) continue;
            Assembly asm;
            try { asm = ctx.LoadFromAssemblyPath(dll); }
            catch (BadImageFormatException) { continue; } // DLL nativa u otra arquitectura
            catch (FileLoadException) { continue; }
            IWinForgeComponent? component = null;
            try
            {
                var contract = typeof(IWinForgeComponent);
                foreach (var type in asm.GetExportedTypes())
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    if (!contract.IsAssignableFrom(type)) continue;
                    if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                    component = (IWinForgeComponent)Activator.CreateInstance(type)!;
                    break;
                }
            }
            catch (Exception ex)
            {
                error = $"El ensamblado {Path.GetFileName(dll)} no se pudo inspeccionar: {ex.Message}";
                continue;
            }
            if (component != null) return component;
        }
        return null;
    }

    /// <summary>
    /// ALC aislado y collectible por componente: resuelve los DLL propios desde la
    /// carpeta del componente y delega TODO lo demás (WHPO.Core, WinAppSDK,
    /// dependencias del runtime) al contexto por defecto de la app.
    /// </summary>
    private sealed class ComponentLoadContext : AssemblyLoadContext
    {
        private readonly string _folder;

        public ComponentLoadContext(string folder) : base($"wf-comp-{Path.GetFileName(folder)}", isCollectible: true)
        {
            _folder = folder;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            var candidate = Path.Combine(_folder, name.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
