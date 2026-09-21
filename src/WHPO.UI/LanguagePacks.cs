using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Puente entre el servicio de paquetes de idioma (Core) y el motor de
/// traducciones (UI): carga los packs instalados al arrancar, activa un idioma
/// descargado en caliente y expone el catálogo para el selector del navbar y el
/// paso de idioma del onboarding.
///
/// Flujo completo de un idioma descargable:
///   1. Al arrancar, <see cref="Initialize"/> mergea los packs instalados en
///      Translations y registra sus códigos en I18n (selector y banderas).
///   2. El catálogo (languages.json del repo, cacheado local) dice qué idiomas hay,
///      su versión, peso y SHA-256.
///   3. <see cref="DownloadAsync"/> baja el pack, lo verifica y lo activa.
///   4. Los packs no se actualizan a mano: si cambian los textos fuente, el pack queda
///      viejo y se refresca solo. Al usuario no se le pide nada por idiomas, a propósito.
/// </summary>
public static class LanguagePacks
{
    private static readonly object Sync = new();
    private static readonly List<InstalledLanguageRecord> Installed = new();
    private static LanguagePackService? _service;
    private static LanguageCatalog? _catalog;

    /// <summary>True si el servicio ya está inicializado (los packs ya se mergearon).</summary>
    public static bool Initialized => _service != null;

    public static InstalledLanguageRecord? Record(string code)
        => Installed.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));

    public static bool IsInstalled(string code) => Record(code) != null;

    /// <summary>
    /// Mergea los packs ya instalados en el motor de traducciones y los registra
    /// como idiomas disponibles. Se llama UNA vez al arrancar, antes de crear las
    /// ventanas (el idioma guardado tiene que poder resolverse ya).
    /// </summary>
    public static void Initialize(LanguagePackService service, ILoggingService logging)
    {
        _service = service;
        ApplyInstalled(logging);
    }

    private static void ApplyInstalled(ILoggingService? logging = null)
    {
        lock (Sync)
        {
            Installed.Clear();
            if (_service != null)
            {
                foreach (var (record, pack) in _service.LoadInstalledPacks())
                {
                    Translations.AddPack(record.Code, pack.Strings);
                    Installed.Add(record);
                }
                int count = Installed.Count;
                if (count > 0)
                    logging?.LogInfo($"Idiomas: {count} pack(s) instalado(s) cargado(s): {string.Join(", ", Installed.Select(r => $"{r.Code} v{r.Version}"))}.");
                // El hash de las claves de la app es contra lo que se compara el de cada
                // pack ("traducción desactualizada"): dejarlo en el log simplifica el
                // diagnóstico cuando un pack queda viejo.
                logging?.LogInfo($"Idiomas: {Translations.KeyCount} claves | sourceHash {Translations.SourceHash[..12]}…");
                foreach (var record in Installed)
                {
                    var state = LanguagePackService.IsOutdated(record.SourceHash, Translations.SourceHash)
                        ? "DESACTUALIZADO"
                        : "al día";
                    logging?.LogInfo($"Idiomas: pack {record.Code} v{record.Version}: {record.KeyCount} textos, {state}.");
                }
            }
            I18n.SetExtraLanguages(Installed.Select(r => r.Code));
        }
    }

    // =====================================================================
    // Catálogo
    // =====================================================================

    /// <summary>
    /// Catálogo de idiomas disponibles. Se cachea en memoria (y en disco el
    /// servicio) para que el selector y el onboarding no pidan la red dos veces.
    /// </summary>
    public static async Task<LanguageCatalog?> GetCatalogAsync(ILoggingService? logging = null, bool force = false, CancellationToken ct = default)
    {
        if (_catalog != null && !force) return _catalog;
        if (_service == null) return null;
        try
        {
            var result = await _service.FetchCatalogAsync(ct);
            if (result.Catalog != null)
            {
                _catalog = result.Catalog;
                if (result.FromCache)
                    logging?.LogInfo($"Idiomas: catálogo desde la copia local ({result.CachedAt:g}).");
            }
        }
        catch (Exception ex)
        {
            logging?.LogWarning($"Idiomas: no se pudo traer el catálogo: {ex.Message}");
        }
        return _catalog;
    }

    /// <summary>True si el catálogo ya se trajo (para no volver a pedirlo).</summary>
    public static bool CatalogLoaded => _catalog != null;

    /// <summary>
    /// Nombre para mostrar de un idioma: el endónimo del catálogo (Deutsch,
    /// Português (Brasil)…), el que se guardó al instalar el pack, o el código si
    /// todavía no se conoce el catálogo.
    /// </summary>
    public static string DisplayName(string code)
    {
        var entry = _catalog?.Languages
            .FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        if (entry != null && !string.IsNullOrWhiteSpace(entry.DisplayName)) return entry.DisplayName;
        var record = Record(code);
        if (record != null && !string.IsNullOrWhiteSpace(record.Endonym)) return record.Endonym;
        return code;
    }

    /// <summary>
    /// Idiomas que la app NO trae y que todavía no están instalados (los que se
    /// ofrecen para descargar, atenuados en el selector).
    ///
    /// La app solo trae es-AR (fuente) y en-US, así que acá cae todo el resto:
    /// pt-BR, de-DE, fr-FR y cualquier idioma nuevo que se publique como pack.
    /// Es un solo idioma por fila: apenas se descarga, IsSupported pasa a true, el
    /// ítem sale de esta lista y se suma a los disponibles (cambia de estancia).
    /// </summary>
    public static List<LanguageCatalogEntry> Downloadable()
    {
        var catalog = _catalog;
        if (catalog == null) return new List<LanguageCatalogEntry>();

        return catalog.Languages
            .Where(l => !l.Builtin && !I18n.IsSupported(l.Code))
            .ToList();
    }

    // =====================================================================
    // Descarga / activación / desinstalación
    // =====================================================================

    /// <summary>
    /// Descarga e instala el pack de un idioma, y lo deja listo para activar
    /// (queda registrado en I18n.Languages, así que el selector ya lo muestra).
    /// </summary>
    public static async Task<LanguagePackOutcome> DownloadAsync(LanguageCatalogEntry entry, IProgress<LanguageProgress>? progress = null, CancellationToken ct = default)
    {
        if (_service == null)
            return new LanguagePackOutcome { Success = false, Error = "El servicio de idiomas no está inicializado." };

        var outcome = await _service.InstallAsync(entry, progress, ct);
        if (outcome.Success && outcome.Record != null)
        {
            var pack = _service.ReadPack(outcome.Record.Path);
            if (pack != null) Translations.AddPack(outcome.Record.Code, pack.Strings);
            lock (Sync)
            {
                Installed.RemoveAll(r => string.Equals(r.Code, entry.Code, StringComparison.OrdinalIgnoreCase));
                Installed.Add(outcome.Record);
                I18n.SetExtraLanguages(Installed.Select(r => r.Code));
            }
        }
        return outcome;
    }

    /// <summary>
    /// Recupera el idioma guardado cuando su pack no está instalado (por ejemplo
    /// pt-BR/de-DE/fr-FR elegidos en una versión anterior, cuando esos idiomas venían
    /// embebidos en la app y ahora son descargables).
    ///
    /// Se llama UNA vez al arrancar, en segundo plano y sin bloquear: si hay catálogo
    /// y red, baja el pack del idioma que el usuario ya tenía elegido y lo activa (la
    /// UI se re-traduce sola al dispararse LanguageChanged). Si no hay red, no hace
    /// nada: el menú de idioma muestra la descarga atenuada y el usuario decide.
    /// </summary>
    /// <returns>True si descargó y activó el idioma guardado.</returns>
    public static async Task<bool> RestoreSavedLanguageAsync(
        ISettingsService settings, ILoggingService? logging = null, CancellationToken ct = default)
    {
        if (_service == null) return false;

        var saved = settings.Get("app.language", "");
        if (string.IsNullOrWhiteSpace(saved)) return false;
        // Ya está cubierto: embebido, o con un pack instalado (ResolveSupported también
        // matchea por idioma base, así que pt-PT/de-AT/fr-CA no disparan nada).
        if (I18n.ResolveSupported(saved) != null) return false;

        var catalog = await GetCatalogAsync(logging, force: false, ct);
        if (catalog == null) return false;

        var baseLang = saved.Split('-')[0];
        var entry = catalog.Languages.FirstOrDefault(l => !l.Builtin
            && (string.Equals(l.Code, saved, StringComparison.OrdinalIgnoreCase)
                || string.Equals(l.Code.Split('-')[0], baseLang, StringComparison.OrdinalIgnoreCase)));
        if (entry == null) return false;

        logging?.LogInfo($"Idiomas: el idioma guardado ({saved}) no está instalado: se descarga {entry.Code} en segundo plano.");
        var outcome = await DownloadAsync(entry, null, ct);
        if (!outcome.Success)
        {
            logging?.LogWarning($"Idiomas: no se pudo recuperar el idioma guardado ({entry.Code}): {outcome.Error}");
            return false;
        }

        I18n.SetLanguage(entry.Code, settings);
        logging?.LogInfo($"Idiomas: {entry.Code} descargado y activado (idioma guardado recuperado).");
        return true;
    }

    /// <summary>
    /// Quita un pack descargado. Si era el idioma activo, vuelve al predeterminado
    /// ANTES de desinstalarlo (si no, la UI quedaría en un idioma sin traducciones).
    /// </summary>
    public static bool Remove(string code, ISettingsService settings)
    {
        if (_service == null) return false;
        if (string.Equals(I18n.Current, code, StringComparison.OrdinalIgnoreCase))
            I18n.SetLanguage(I18n.DefaultLanguage, settings);
        if (!_service.Uninstall(code)) return false;
        Translations.RemovePack(code);
        lock (Sync)
        {
            Installed.RemoveAll(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
            I18n.SetExtraLanguages(Installed.Select(r => r.Code));
        }
        return true;
    }
}
