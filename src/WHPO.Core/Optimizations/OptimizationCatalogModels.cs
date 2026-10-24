using System.Text.Json;
using System.Text.Json.Serialization;

namespace WHPO.Core.Optimizations;

/// <summary>
/// Catálogo de optimizaciones: la lista completa de ajustes de Windows que WinForge
/// sabe leer, aplicar y revertir. Se carga del JSON embebido
/// (optimization-catalog.json) y es la única fuente de verdad de la pantalla
/// "Optimizaciones".
/// </summary>
public sealed class OptimizationCatalog
{
    /// <summary>Versión del formato del catálogo (para migraciones futuras).</summary>
    public int Version { get; set; } = 1;

    /// <summary>Categorías del catálogo, ya ordenadas.</summary>
    public List<OptimizationFeature> Features { get; set; } = new();
}

/// <summary>
/// Una categoría del hub (Privacidad, Energía, Juegos, Explorador, etc.).
/// </summary>
public sealed class OptimizationFeature
{
    /// <summary>Id estable de la categoría ("privacy", "power", "gaming"...).</summary>
    public string Id { get; set; } = "";

    /// <summary>Id de la feature en el catálogo original (solo informativo).</summary>
    public string FeatureId { get; set; } = "";

    /// <summary>Glifo del icono (Segoe Fluent Icons / Material).</summary>
    public string Icon { get; set; } = "";

    /// <summary>Orden de aparición en el hub.</summary>
    public int Order { get; set; }

    /// <summary>Nombre de la categoría por idioma ("es", "en", "pt", "de", "fr").</summary>
    public Dictionary<string, string> Name { get; set; } = new();

    /// <summary>Ajustes de la categoría.</summary>
    public List<OptimizationSetting> Settings { get; set; } = new();
}

/// <summary>
/// Un ajuste individual dentro de una categoría.
/// </summary>
public sealed class OptimizationSetting
{
    /// <summary>Id estable del ajuste.</summary>
    public string Id { get; set; } = "";

    /// <summary>Grupo al que pertenece (clave cruda, en inglés).</summary>
    public string Group { get; set; } = "";

    /// <summary>Nombre del grupo por idioma.</summary>
    public Dictionary<string, string> GroupText { get; set; } = new();

    /// <summary>Glifo del icono del ajuste (opcional).</summary>
    public string? Icon { get; set; }

    /// <summary>Tipo de control: "Toggle", "Selection", "NumericRange" o "Action".</summary>
    public string InputType { get; set; } = "Toggle";

    /// <summary>True si el valor correcto depende del gusto del usuario.</summary>
    public bool IsSubjectivePreference { get; set; }

    /// <summary>True si hay que pedir confirmación antes de aplicar.</summary>
    public bool RequiresConfirmation { get; set; }

    /// <summary>True si el ajuste pide reiniciar el equipo.</summary>
    public bool RequiresRestart { get; set; }

    /// <summary>Proceso a reiniciar para que el ajuste surta efecto.</summary>
    public string? RestartProcess { get; set; }

    /// <summary>Servicio a reiniciar para que el ajuste surta efecto.</summary>
    public string? RestartService { get; set; }

    /// <summary>Solo aplica en Windows 11.</summary>
    public bool IsWindows11Only { get; set; }

    /// <summary>Solo aplica en Windows 10.</summary>
    public bool IsWindows10Only { get; set; }

    /// <summary>Build mínima de Windows necesaria.</summary>
    public int? MinimumBuildNumber { get; set; }

    /// <summary>Build máxima de Windows admitida.</summary>
    public int? MaximumBuildNumber { get; set; }

    /// <summary>Versión de Winhance en la que se agregó el ajuste (badge "NUEVO").</summary>
    public string? AddedInVersion { get; set; }

    /// <summary>Estado recomendado a nivel de ajuste (gana sobre el valor por clave).</summary>
    public bool? RecommendedToggleState { get; set; }

    /// <summary>Estado por defecto de Windows a nivel de ajuste.</summary>
    public bool? DefaultToggleState { get; set; }

    /// <summary>Si el estado no conocido se resuelve a la opción por defecto.</summary>
    public bool ResolveUnmatchedToDefault { get; set; }

    /// <summary>Tipo de detección especial (si lo hay).</summary>
    public string? DetectionType { get; set; }

    /// <summary>Nombre y descripción por idioma.</summary>
    public Dictionary<string, OptimizationText> Texts { get; set; } = new();

    /// <summary>Claves de registro que definen el estado del ajuste.</summary>
    public List<OptimizationRegistryEntry> Registry { get; set; } = new();

    /// <summary>Opciones (para ajustes de tipo Selection).</summary>
    public List<OptimizationOption> Options { get; set; } = new();

    /// <summary>Ajustes de powercfg (planes de energía).</summary>
    public List<OptimizationPowerEntry> Power { get; set; } = new();

    /// <summary>Tareas programadas que definen el estado del ajuste.</summary>
    public List<OptimizationTaskEntry> Tasks { get; set; } = new();

    /// <summary>Scripts de PowerShell que se ejecutan al activar/desactivar.</summary>
    public List<OptimizationScriptEntry> Scripts { get; set; } = new();

    /// <summary>Rango numérico (para sliders).</summary>
    public OptimizationNumericRange? Numeric { get; set; }
}

/// <summary>Nombre y descripción traducibles de un ajuste.</summary>
public sealed class OptimizationText
{
    /// <summary>Nombre visible.</summary>
    public string Name { get; set; } = "";

    /// <summary>Descripción visible.</summary>
    public string Description { get; set; } = "";
}

/// <summary>
/// Una clave de registro que participa en el estado de un ajuste. Los valores se
/// guardan como JSON crudo (<see cref="JsonElement"/>) para no perder el tipo
/// original (DWord, QWord, String, Binary, MultiString...).
/// </summary>
public sealed class OptimizationRegistryEntry
{
    /// <summary>Ruta de la clave sin el nombre del hive.</summary>
    public string KeyPath { get; set; } = "";

    /// <summary>Nombre del valor (null = la existencia de la clave).</summary>
    public string? ValueName { get; set; }

    /// <summary>Valor recomendado.</summary>
    public JsonElement? RecommendedValue { get; set; }

    /// <summary>Valor por defecto de Windows.</summary>
    public JsonElement? DefaultValue { get; set; }

    /// <summary>Valores que representan el ajuste ACTIVADO.</summary>
    public List<JsonElement?>? EnabledValue { get; set; }

    /// <summary>Valores que representan el ajuste DESACTIVADO.</summary>
    public List<JsonElement?>? DisabledValue { get; set; }

    /// <summary>Tipo de valor de registro ("DWord", "String", "Binary"...).</summary>
    public string ValueType { get; set; } = "DWord";

    /// <summary>True si es la clave principal (la que define el estado).</summary>
    public bool IsPrimary { get; set; }

    /// <summary>Máscara de bits cuando el ajuste modifica un byte suelto.</summary>
    public byte? BitMask { get; set; }

    /// <summary>True si solo se modifica un byte del valor binario.</summary>
    public bool ModifyByteOnly { get; set; }

    /// <summary>Sub-clave dentro de un valor de texto compuesto.</summary>
    public string? CompositeStringKey { get; set; }

    /// <summary>True si hay que aplicarlo por interfaz de red.</summary>
    public bool ApplyPerNetworkInterface { get; set; }

    /// <summary>True si hay que aplicarlo por monitor.</summary>
    public bool ApplyPerMonitor { get; set; }

    /// <summary>True si la clave es una GPO (se salta el bloqueo de ACL).</summary>
    public bool IsGroupPolicy { get; set; }

    /// <summary>Índice del byte a modificar dentro de un valor binario.</summary>
    public int? BinaryByteIndex { get; set; }

    /// <summary>True si hay que bloquear el acceso a la clave.</summary>
    public bool LockKeyAccess { get; set; }
}

/// <summary>Una opción de un ajuste de tipo Selection.</summary>
public sealed class OptimizationOption
{
    /// <summary>Nombre de la opción en el catálogo original (sirve de clave de traducción).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>True si es la opción por defecto de Windows.</summary>
    public bool IsDefault { get; set; }

    /// <summary>True si es la opción recomendada.</summary>
    public bool IsRecommended { get; set; }

    /// <summary>Valor simple (para ajustes de un solo valor de registro).</summary>
    public int? SimpleValue { get; set; }

    /// <summary>Mapa valor-de-registro por nombre de clave.</summary>
    public Dictionary<string, JsonElement?> ValueMappings { get; set; } = new();

    /// <summary>Nombre visible de la opción por idioma.</summary>
    public Dictionary<string, string> Text { get; set; } = new();
}

/// <summary>Un ajuste del plan de energía (powercfg).</summary>
public sealed class OptimizationPowerEntry
{
    /// <summary>Alias del subgrupo.</summary>
    public string SubgroupAlias { get; set; } = "";

    /// <summary>Alias del ajuste dentro del subgrupo.</summary>
    public string SettingAlias { get; set; } = "";

    /// <summary>GUID del subgrupo.</summary>
    public string SubgroupGuid { get; set; } = "";

    /// <summary>GUID del ajuste.</summary>
    public string SettingGuid { get; set; } = "";

    /// <summary>"Both", "Separate", "ACOnly" o "DCOnly".</summary>
    public string PowerModeSupport { get; set; } = "Separate";

    /// <summary>Valor recomendado con corriente.</summary>
    public int? RecommendedAc { get; set; }

    /// <summary>Valor recomendado con batería.</summary>
    public int? RecommendedDc { get; set; }

    /// <summary>Valor por defecto con corriente.</summary>
    public int? DefaultAc { get; set; }

    /// <summary>Valor por defecto con batería.</summary>
    public int? DefaultDc { get; set; }

    /// <summary>Unidades de la medida ("Minutes", "Seconds"...).</summary>
    public string? Units { get; set; }
}

/// <summary>Una tarea programada que define el estado de un ajuste.</summary>
public sealed class OptimizationTaskEntry
{
    /// <summary>Id de la tarea.</summary>
    public string Id { get; set; } = "";

    /// <summary>Ruta de la tarea.</summary>
    public string TaskPath { get; set; } = "";

    /// <summary>Estado recomendado (true = habilitada).</summary>
    public bool? RecommendedState { get; set; }

    /// <summary>Estado por defecto de Windows (true = habilitada).</summary>
    public bool? DefaultState { get; set; }
}

/// <summary>Un script de PowerShell que se ejecuta al activar o desactivar el ajuste.</summary>
public sealed class OptimizationScriptEntry
{
    /// <summary>Script que se ejecuta al activar.</summary>
    public string? EnabledScript { get; set; }

    /// <summary>Script que se ejecuta al desactivar.</summary>
    public string? DisabledScript { get; set; }

    /// <summary>True si el script necesita elevación (la app ya corre elevada).</summary>
    public bool RequiresElevation { get; set; } = true;
}

/// <summary>Rango numérico de un ajuste con slider.</summary>
public sealed class OptimizationNumericRange
{
    /// <summary>Valor mínimo.</summary>
    public long Min { get; set; }

    /// <summary>Valor máximo.</summary>
    public long Max { get; set; }

    /// <summary>Paso del slider.</summary>
    public long Step { get; set; } = 1;

    /// <summary>Unidades ("Minutes", "Seconds"...).</summary>
    public string? Units { get; set; }
}

/// <summary>
/// Resolvedor de traducciones del catálogo: traduce por idioma con cadena de
/// respaldo (idioma pedido → inglés → español, que es la fuente).
/// </summary>
public sealed class OptimizationCatalogText
{
    private readonly OptimizationCatalog _catalog;

    /// <summary>Crea el resolvedor sobre un catálogo cargado.</summary>
    public OptimizationCatalogText(OptimizationCatalog catalog) => _catalog = catalog;

    /// <summary>Nombre de una categoría en el idioma pedido.</summary>
    public string FeatureName(OptimizationFeature feature, string language)
        => Resolve(feature.Name, language) ?? feature.Id;

    /// <summary>Nombre de un ajuste en el idioma pedido.</summary>
    public string SettingName(OptimizationSetting setting, string language)
        => Resolve(setting.Texts, language)?.Name ?? setting.Id;

    /// <summary>Descripción de un ajuste en el idioma pedido.</summary>
    public string SettingDescription(OptimizationSetting setting, string language)
        => Resolve(setting.Texts, language)?.Description ?? "";

    /// <summary>Nombre del grupo de un ajuste en el idioma pedido.</summary>
    public string GroupName(OptimizationSetting setting, string language)
        => Resolve(setting.GroupText, language) ?? setting.Group;

    /// <summary>Nombre de una opción en el idioma pedido.</summary>
    public string OptionName(OptimizationOption option, string language)
        => Resolve(option.Text, language) ?? option.DisplayName;

    private static string? Resolve(Dictionary<string, string> map, string language)
    {
        if (map.Count == 0) return null;
        if (map.TryGetValue(language, out var exact) && !string.IsNullOrEmpty(exact)) return exact;

        // Los códigos de la app son "es-AR", "en-US"...: el catálogo usa "es", "en".
        var prefix = language.Split('-')[0];
        if (map.TryGetValue(prefix, out var shortForm) && !string.IsNullOrEmpty(shortForm)) return shortForm;

        if (map.TryGetValue("en", out var en) && !string.IsNullOrEmpty(en)) return en;
        if (map.TryGetValue("es", out var es) && !string.IsNullOrEmpty(es)) return es;
        return map.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
    }

    private static OptimizationText? Resolve(Dictionary<string, OptimizationText> map, string language)
    {
        if (map.Count == 0) return null;
        if (map.TryGetValue(language, out var exact) && exact is not null) return exact;

        var prefix = language.Split('-')[0];
        if (map.TryGetValue(prefix, out var shortForm) && shortForm is not null) return shortForm;

        if (map.TryGetValue("en", out var en) && en is not null) return en;
        if (map.TryGetValue("es", out var es) && es is not null) return es;
        return map.Values.FirstOrDefault(v => v is not null);
    }
}
