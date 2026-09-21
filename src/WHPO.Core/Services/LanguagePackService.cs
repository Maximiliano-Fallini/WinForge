using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

// =========================================================================
// Modelos (mismos nombres de campo que los JSON que genera
// build-language-packs.ps1; el parseo es case-insensitive)
// =========================================================================

/// <summary>Un idioma del catálogo (languages.json): embebido o descargable.</summary>
public sealed class LanguageCatalogEntry
{
    public string Code { get; set; } = "";
    /// <summary>Nombre del idioma EN SU PROPIO idioma (Deutsch, Português (Brasil)…).</summary>
    public string Endonym { get; set; } = "";
    public string EnglishName { get; set; } = "";
    /// <summary>True si el idioma viene dentro de la app (no se descarga nunca).</summary>
    public bool Builtin { get; set; }
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public int KeyCount { get; set; }
    /// <summary>Porcentaje de claves traducidas (100 = completo).</summary>
    public int Completion { get; set; }
    public string MinAppVersion { get; set; } = "";

    /// <summary>Nombre para mostrar: el endónimo si lo hay, si no el nombre en inglés.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Endonym) ? EnglishName : Endonym;
}

/// <summary>Manifest de paquetes de idioma (languages.json del repo).</summary>
public sealed class LanguageCatalog
{
    public int CatalogVersion { get; set; }
    /// <summary>Hash de las claves fuente de la app que corresponde a este catálogo.</summary>
    public string SourceHash { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    public List<LanguageCatalogEntry> Languages { get; set; } = new();
}

/// <summary>Paquete de idioma descargado: mapa "texto fuente en español" → traducción.</summary>
public sealed class LanguagePack
{
    public int Schema { get; set; }
    public string Code { get; set; } = "";
    public string Version { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public int KeyCount { get; set; }
    public int Completion { get; set; }
    public Dictionary<string, string> Strings { get; set; } = new();
}

/// <summary>Paquete instalado (language-packs.json): de dónde salió y dónde está.</summary>
public sealed class InstalledLanguageRecord
{
    public string Code { get; set; } = "";
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public string InstalledAt { get; set; } = "";
    public int KeyCount { get; set; }
    public int Completion { get; set; }
    public string SourceHash { get; set; } = "";
    public string Endonym { get; set; } = "";
}

public enum LanguageStage { Downloading, Verifying, Installing, Done }

/// <summary>Progreso de una descarga/instalación de pack.</summary>
public sealed record LanguageProgress(LanguageStage Stage, int Percent);

/// <summary>Resultado de traer el catálogo de idiomas.</summary>
public sealed class LanguageCatalogFetchResult
{
    public LanguageCatalog? Catalog { get; init; }
    /// <summary>True si salió de la copia local por no poder llegar al repo.</summary>
    public bool FromCache { get; init; }
    public DateTime? CachedAt { get; init; }
    public string? Error { get; init; }
}

/// <summary>Resultado de instalar (descargar) un paquete de idioma.</summary>
public sealed class LanguagePackOutcome
{
    public bool Success { get; init; }
    /// <summary>Mensaje de error (texto fuente en español, pasable por I18n.T).</summary>
    public string? Error { get; init; }
    public InstalledLanguageRecord? Record { get; init; }
}

/// <summary>
/// Paquetes de idioma: catálogo + descarga/instalación de los idiomas que no
/// vienen dentro de la app.
///
/// Cómo funciona (espejo del Workshop de componentes, mismo pipeline probado):
///  1. El catálogo vive en el repo (ProjectEndpoints.LanguagesCatalog, servido por
///     raw.githubusercontent para evitar el límite de 60 req/h de la API) y se
///     cachea localmente para modo offline.
///  2. "Descargar idioma": baja el JSON de la release de idiomas, verifica SHA-256
///     contra el manifest, lo guarda en %LOCALAPPDATA%\WHPO\Languages\&lt;code&gt;-&lt;ver&gt;.json
///     (atómico: temp → move) y lo registra en language-packs.json.
///  3. El idioma se activa en caliente: la UI mergea el pack en su tabla de
///     traducciones (Translations.AddPack) sin reiniciar la app.
///
/// Modo offline: los idiomas embebidos (es-AR/en-US) y los packs ya instalados
/// funcionan sin red; el catálogo cae a la última copia buena.
/// </summary>
public sealed class LanguagePackService
{
    private readonly ILoggingService _logging;
    private readonly ISettingsService _settings;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// URL del catálogo. Se puede sobrescribir con el setting "languages.catalogUrl"
    /// (útil para probar el flujo contra un servidor local antes de publicar al repo).
    /// Un valor vacío cuenta como "sin override": si no, un settings.json editado a
    /// mano dejaría a la app sin catálogo para siempre.
    /// </summary>
    public string CatalogUrl
    {
        get
        {
            var configured = _settings.Get("languages.catalogUrl", "");
            return string.IsNullOrWhiteSpace(configured) ? ProjectEndpoints.LanguagesCatalog : configured;
        }
    }

    private static string RootDir => AppPaths.RootDir;
    private static string PacksRoot => Path.Combine(RootDir, "Languages");
    private static string StorePath => Path.Combine(RootDir, "language-packs.json");
    private static string CachePath => Path.Combine(RootDir, "languages.cache.json");

    public LanguagePackService(ISettingsService settings, ILoggingService logging)
    {
        _settings = settings;
        _logging = logging;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinForge-Languages/1.0");
    }

    /// <summary>Nombre de archivo de un pack, igual que el que publica el script de build.</summary>
    public static string PackFileName(string code, string version) => $"{code}-{version}.json";

    // =====================================================================
    // Catálogo
    // =====================================================================

    /// <summary>Trae el catálogo de idiomas; si falla, devuelve la copia cacheada.</summary>
    public async Task<LanguageCatalogFetchResult> FetchCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await GetStringAsync(CatalogUrl, ct);
            var catalog = JsonSerializer.Deserialize<LanguageCatalog>(json, _json);
            if (catalog == null || catalog.Languages.Count == 0)
                throw new InvalidDataException("languages.json vacío o inválido.");
            try
            {
                Directory.CreateDirectory(RootDir);
                await File.WriteAllTextAsync(CachePath, json, ct);
            }
            catch (Exception ex) { _logging.LogWarning($"Idiomas: no se pudo cachear el catálogo: {ex.Message}"); }
            return new LanguageCatalogFetchResult { Catalog = catalog };
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Idiomas: no se pudo bajar el catálogo: {ex.Message}");
            try
            {
                if (File.Exists(CachePath))
                {
                    var cached = JsonSerializer.Deserialize<LanguageCatalog>(await File.ReadAllTextAsync(CachePath, ct), _json);
                    var cachedAt = File.GetLastWriteTime(CachePath);
                    if (cached != null && cached.Languages.Count > 0)
                        return new LanguageCatalogFetchResult { Catalog = cached, FromCache = true, CachedAt = cachedAt };
                }
            }
            catch (Exception ex2) { _logging.LogWarning($"Idiomas: la copia cacheada tampoco se pudo leer: {ex2.Message}"); }
            return new LanguageCatalogFetchResult { Error = ex.Message };
        }
    }

    // =====================================================================
    // Instalados (persistencia en language-packs.json)
    // =====================================================================

    public List<InstalledLanguageRecord> GetInstalled()
    {
        try
        {
            if (!File.Exists(StorePath)) return new List<InstalledLanguageRecord>();
            var store = JsonSerializer.Deserialize<InstalledStore>(File.ReadAllText(StorePath), _json);
            return store?.Installed ?? new List<InstalledLanguageRecord>();
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Idiomas: no se pudo leer language-packs.json: {ex.Message}");
            return new List<InstalledLanguageRecord>();
        }
    }

    public InstalledLanguageRecord? GetInstalled(string code)
        => GetInstalled().FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));

    public bool IsInstalled(string code) => GetInstalled(code) != null;

    private void SaveInstalled(List<InstalledLanguageRecord> list)
    {
        Directory.CreateDirectory(RootDir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(new InstalledStore { Installed = list }, _json));
    }

    private sealed class InstalledStore
    {
        public List<InstalledLanguageRecord> Installed { get; set; } = new();
    }

    // =====================================================================
    // Lectura de los packs instalados
    // =====================================================================

    /// <summary>
    /// Lee todos los packs instalados, listos para mergear en el motor de
    /// traducciones. Un pack ilegible o borrado a mano se saltea (se informa) sin
    /// romper el arranque.
    /// </summary>
    public List<(InstalledLanguageRecord Record, LanguagePack Pack)> LoadInstalledPacks()
    {
        var result = new List<(InstalledLanguageRecord, LanguagePack)>();
        foreach (var record in GetInstalled())
        {
            var pack = ReadPack(record.Path);
            if (pack == null)
            {
                _logging.LogWarning($"Idiomas: el pack de '{record.Code}' no se pudo leer ({record.Path}).");
                continue;
            }
            result.Add((record, pack));
        }
        return result;
    }

    /// <summary>Lee y valida un archivo de pack; null si no se puede leer.</summary>
    public LanguagePack? ReadPack(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var pack = JsonSerializer.Deserialize<LanguagePack>(File.ReadAllText(path), _json);
            if (pack == null || pack.Strings == null || pack.Strings.Count == 0) return null;
            return pack;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Idiomas: error leyendo el pack '{path}': {ex.Message}");
            return null;
        }
    }

    // =====================================================================
    // Descarga / instalación / desinstalación
    // =====================================================================

    /// <summary>
    /// Descarga e instala un paquete de idioma del catálogo. Al terminar queda
    /// registrado en language-packs.json y disponible para activar.
    /// </summary>
    public async Task<LanguagePackOutcome> InstallAsync(LanguageCatalogEntry entry, IProgress<LanguageProgress>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Code) || string.IsNullOrWhiteSpace(entry.Version))
            return new LanguagePackOutcome { Success = false, Error = "El catálogo no tiene los datos del idioma (código y versión)." };
        if (entry.Builtin)
            return new LanguagePackOutcome { Success = false, Error = "Ese idioma ya viene incluido en la app." };
        if (!ComponentCatalogService.IsVersionCompatible(entry.MinAppVersion))
            return new LanguagePackOutcome { Success = false, Error = $"Incompatible: requiere WinForge {entry.MinAppVersion} o superior." };

        var fileName = PackFileName(entry.Code, entry.Version);
        // El manifest trae la URL completa de cada pack; si faltara (manifest viejo o
        // incompleto), se arma con la convención de la release de idiomas.
        var url = string.IsNullOrWhiteSpace(entry.Url)
            ? ProjectEndpoints.LanguagePackAsset(fileName)
            : entry.Url;
        string? tempFile = null;
        try
        {
            Directory.CreateDirectory(PacksRoot);
            tempFile = Path.Combine(PacksRoot, fileName + ".tmp");
            progress?.Report(new LanguageProgress(LanguageStage.Downloading, 0));
            await DownloadToFileAsync(url, tempFile, entry.SizeBytes, progress, ct);

            // Integridad: SHA-256 del archivo descargado vs manifest.
            progress?.Report(new LanguageProgress(LanguageStage.Verifying, 100));
            var actualHash = await ComputeSha256Async(tempFile, ct);
            var expected = (entry.Sha256 ?? "").Trim().ToLowerInvariant();
            if (expected.Length > 0 && !string.Equals(actualHash, expected, StringComparison.Ordinal))
            {
                _logging.LogWarning($"Idiomas: fallo de integridad para '{entry.Code}': esperado {expected}, obtenido {actualHash}.");
                return new LanguagePackOutcome { Success = false, Error = "Fallo de integridad: el archivo descargado no coincide con el catálogo (SHA-256)." };
            }

            // El pack tiene que ser válido y del idioma pedido ANTES de instalarlo.
            progress?.Report(new LanguageProgress(LanguageStage.Installing, 100));
            var pack = ReadPack(tempFile);
            if (pack == null)
                return new LanguagePackOutcome { Success = false, Error = "El paquete descargado está corrupto o vacío." };
            if (!string.Equals(pack.Code, entry.Code, StringComparison.OrdinalIgnoreCase))
                return new LanguagePackOutcome { Success = false, Error = $"El paquete es de '{pack.Code}' y se pidió '{entry.Code}'." };

            // Instalación atómica: temp → nombre final (y se borran versiones previas).
            var destFile = Path.Combine(PacksRoot, fileName);
            foreach (var old in Directory.GetFiles(PacksRoot, entry.Code + "-*.json"))
            {
                if (string.Equals(old, destFile, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(old); } catch { /* queda para la próxima */ }
            }
            if (File.Exists(destFile)) File.Delete(destFile);
            File.Move(tempFile, destFile);
            tempFile = null;

            var record = new InstalledLanguageRecord
            {
                Code = entry.Code,
                Version = entry.Version,
                Path = destFile,
                InstalledAt = DateTime.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                KeyCount = pack.KeyCount > 0 ? pack.KeyCount : pack.Strings.Count,
                Completion = pack.Completion,
                SourceHash = pack.SourceHash,
                Endonym = entry.DisplayName
            };
            var all = GetInstalled();
            all.RemoveAll(r => string.Equals(r.Code, entry.Code, StringComparison.OrdinalIgnoreCase));
            all.Add(record);
            SaveInstalled(all);

            _logging.LogInfo($"Idiomas: pack instalado: {entry.Code} v{entry.Version} ({pack.Strings.Count} textos, {actualHash[..12]}…).");
            progress?.Report(new LanguageProgress(LanguageStage.Done, 100));
            return new LanguagePackOutcome { Success = true, Record = record };
        }
        catch (OperationCanceledException)
        {
            return new LanguagePackOutcome { Success = false, Error = "Descarga cancelada." };
        }
        catch (Exception ex)
        {
            _logging.LogError($"Idiomas: error instalando '{entry.Code}': {ex.Message}", ex);
            return new LanguagePackOutcome { Success = false, Error = $"No se pudo descargar el idioma: {ex.Message}" };
        }
        finally
        {
            try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    /// <summary>
    /// Desinstala un pack: lo saca del registro y borra el archivo. El idioma deja
    /// de estar disponible (si era el activo, la UI debe volver a en-US/es-AR antes).
    /// </summary>
    public bool Uninstall(string code)
    {
        try
        {
            var all = GetInstalled();
            var record = all.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
            all.RemoveAll(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
            SaveInstalled(all);
            if (record != null)
            {
                try { if (File.Exists(record.Path)) File.Delete(record.Path); }
                catch (Exception ex) { _logging.LogWarning($"Idiomas: no se pudo borrar el pack '{code}': {ex.Message}"); }
            }
            _logging.LogInfo($"Idiomas: pack desinstalado: {code}.");
            return true;
        }
        catch (Exception ex)
        {
            _logging.LogError($"Idiomas: error desinstalando '{code}': {ex.Message}", ex);
            return false;
        }
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>
    /// Hash de las claves fuente de la app. Contrato (documentado también en
    /// build-language-packs.ps1): SHA-256 en UTF-8 de las claves en español
    /// ordenadas con StringComparer.Ordinal (NO por cultura) unidas con "\n", sin
    /// salto final. Si un pack no lo tiene igual, su traducción quedó vieja.
    /// </summary>
    public static string ComputeSourceHash(IEnumerable<string> keys)
    {
        var ordered = keys
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal);
        var payload = string.Join("\n", ordered);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    /// <summary>True si el pack quedó desactualizado respecto de las claves de la app.</summary>
    public static bool IsOutdated(string packSourceHash, string appSourceHash)
        => !string.IsNullOrEmpty(packSourceHash)
           && !string.IsNullOrEmpty(appSourceHash)
           && !string.Equals(packSourceHash, appSourceHash, StringComparison.Ordinal);

    /// <summary>
    /// True si dos códigos son el mismo idioma por su base ("pt-PT" ↔ "pt-BR").
    /// Se usa para que el idioma del sistema elija un pack instalado.
    /// </summary>
    public static bool MatchesLanguage(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(BaseOf(a), BaseOf(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parte del idioma sin la región ("pt-BR" → "pt").</summary>
    public static string BaseOf(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var i = code.IndexOf('-');
        return (i > 0 ? code[..i] : code).ToLowerInvariant();
    }

    private async Task DownloadToFileAsync(string url, string destPath, long expectedSize, IProgress<LanguageProgress>? progress, CancellationToken ct)
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
                    progress?.Report(new LanguageProgress(LanguageStage.Downloading, pct));
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
}
