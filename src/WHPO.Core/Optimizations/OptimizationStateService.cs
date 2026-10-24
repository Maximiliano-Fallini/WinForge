using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace WHPO.Core.Optimizations;

/// <summary>
/// Estado en vivo de un ajuste: si está activo, qué opción está elegida y cómo
/// se compara con lo recomendado y lo que trae Windows de fábrica.
/// </summary>
public sealed class OptimizationSettingState
{
    /// <summary>Ajuste al que pertenece el estado.</summary>
    public OptimizationSetting Setting { get; init; } = new();

    /// <summary>Estado del toggle (null si el ajuste no es un toggle o no se pudo leer).</summary>
    public bool? IsOn { get; init; }

    /// <summary>Índice de la opción seleccionada (ajustes de tipo Selection).</summary>
    public int? SelectedOptionIndex { get; init; }

    /// <summary>Valor numérico actual (ajustes con slider).</summary>
    public long? NumericValue { get; init; }

    /// <summary>True si el ajuste tiene datos de recomendado/por defecto para mostrar badges.</summary>
    public bool HasBadgeData { get; init; }

    /// <summary>True si el estado actual coincide con lo recomendado.</summary>
    public bool MatchesRecommended { get; init; }

    /// <summary>True si el estado actual coincide con el valor de fábrica de Windows.</summary>
    public bool MatchesDefault { get; init; }

    /// <summary>True si el estado actual es un valor personalizado sin opción conocida.</summary>
    public bool IsCustom { get; init; }

    /// <summary>Motivo por el que el ajuste no se puede aplicar en este equipo.</summary>
    public string? UnsupportedReason { get; init; }
}

/// <summary>
/// Resumen de badges de una categoría: los contadores que muestra la card del hub.
/// </summary>
public sealed class OptimizationFeatureSummary
{
    /// <summary>Categoría resumida.</summary>
    public OptimizationFeature Feature { get; init; } = new();

    /// <summary>Cantidad de ajustes con datos de badge (el denominador).</summary>
    public int Total { get; init; }

    /// <summary>Cantidad de ajustes que coinciden con lo recomendado.</summary>
    public int Recommended { get; init; }

    /// <summary>Cantidad de ajustes que coinciden con el valor de fábrica.</summary>
    public int Default { get; init; }

    /// <summary>Cantidad de ajustes con valor personalizado.</summary>
    public int Custom { get; init; }
}

/// <summary>
/// Lee el estado real del sistema para los ajustes del catálogo y calcula los
/// badges (recomendado / por defecto / personalizado), replicando la misma
/// lógica que usa Winhance para que los contadores del hub signifiquen lo mismo.
/// </summary>
public sealed class OptimizationStateService
{
    // Las cachés son CONCURRENTES: el hub lee las categorías en paralelo y
    // varias hilos leen el estado al mismo tiempo (un schtasks por ajuste de
    // tarea hace que leerlas en serie tardara minutos).
    private readonly ConcurrentDictionary<string, bool?> _taskStateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int Ac, int Dc)> _powerCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Calcula el estado de un ajuste (y sus badges) leyendo lo que haga falta
    /// del sistema.
    /// </summary>
    public OptimizationSettingState ReadState(OptimizationSetting setting)
    {
        var isToggle = IsToggleLike(setting);
        var isSelection = IsSelection(setting);

        bool? isOn = null;
        int? selectedIndex = null;
        long? numericValue = null;

        if (setting.Registry.Count > 0)
        {
            isOn = ReadToggleFromRegistry(setting);
            if (isSelection)
                selectedIndex = ResolveOptionIndex(setting);
        }
        else if (setting.Tasks.Count > 0)
        {
            isOn = ReadToggleFromTasks(setting);
        }
        else if (setting.Power.Count > 0)
        {
            var power = ReadPowerValue(setting);
            if (power is not null)
            {
                if (isSelection)
                {
                    selectedIndex = ResolvePowerOptionIndex(setting, power.Value.Ac);
                }
                else
                {
                    isOn = power.Value.Ac != 0;
                    numericValue = power.Value.Ac;
                }
            }
        }

        var hasBadgeData = HasBadgeData(setting);
        var (matchesRecommended, matchesDefault) = ComputeMatches(setting, isOn, selectedIndex, numericValue);
        var isCustom = ComputeIsCustom(setting, isOn, selectedIndex, numericValue, matchesRecommended, matchesDefault);

        return new OptimizationSettingState
        {
            Setting = setting,
            IsOn = isOn,
            SelectedOptionIndex = selectedIndex,
            NumericValue = numericValue,
            HasBadgeData = hasBadgeData,
            MatchesRecommended = matchesRecommended,
            MatchesDefault = matchesDefault,
            IsCustom = isCustom,
            UnsupportedReason = GetUnsupportedReason(setting),
        };
    }

    /// <summary>
    /// Calcula el resumen de badges de una categoría a partir de los estados ya
    /// leídos (no vuelve a tocar el sistema).
    ///
    /// El estado de cada ajuste se clasifica en UN solo badge y en el MISMO orden
    /// que usa la UI: recomendado → por defecto → personalizado. Un ajuste que
    /// coincide con lo recomendado cuenta como recomendado (aunque también
    /// coincida con el valor de fábrica), y uno que no coincide con ninguno de
    /// los dos cuenta como personalizado.
    /// </summary>
    public static OptimizationFeatureSummary Summarize(
        OptimizationFeature feature,
        IReadOnlyList<OptimizationSettingState> states)
    {
        int total = 0, recommended = 0, defaultCount = 0, custom = 0;

        foreach (var s in states)
        {
            if (s.HasBadgeData)
            {
                total++;
                if (s.MatchesRecommended) recommended++;
                else if (s.MatchesDefault) defaultCount++;
                else custom++;
            }
        }

        return new OptimizationFeatureSummary
        {
            Feature = feature,
            Total = total,
            Recommended = recommended,
            Default = defaultCount,
            Custom = custom,
        };
    }

    // ====== Estado del sistema ======

    /// <summary>
    /// Estado RECOMENDADO del ajuste (el que propone el catálogo). Se resuelve
    /// primero con el estado a nivel de ajuste y, si no hay, con la primera
    /// clave de registro (o tarea o powercfg) que tenga valor recomendado.
    ///
    /// Es lo que usan los perfiles preconfigurados: al elegir un perfil se
    /// marcan estos ajustes con el valor que el catálogo recomienda.
    /// </summary>
    public static bool? RecommendedState(OptimizationSetting setting)
    {
        // Los ajustes de opciones y los de rango no tienen "prendido/apagado":
        // su valor recomendado es una opción o un número (ver los dos métodos de
        // abajo). Devolver un bool acá inventaría un estado que no existe.
        if (IsSelection(setting) || IsNumeric(setting)) return null;

        if (setting.RecommendedToggleState.HasValue)
            return setting.RecommendedToggleState;

        foreach (var entry in setting.Registry)
        {
            var state = ToggleTargetState(entry.RecommendedValue, entry.EnabledValue, entry.DisabledValue);
            if (state.HasValue) return state;
        }

        foreach (var task in setting.Tasks)
            if (task.RecommendedState.HasValue) return task.RecommendedState;

        foreach (var power in setting.Power)
            if (power.RecommendedAc.HasValue) return power.RecommendedAc.Value != 0;

        return null;
    }

    /// <summary>
    /// Índice de la opción RECOMENDADA de un ajuste de tipo Selection (lo que
    /// marcan los perfiles preconfigurados). Null si ninguna está marcada.
    /// </summary>
    public static int? RecommendedOptionIndex(OptimizationSetting setting)
    {
        if (!IsSelection(setting)) return null;

        int index = setting.Options.FindIndex(o => o.IsRecommended);
        if (index >= 0) return index;

        // Los combos de energía no marcan la opción recomendada: el valor lo trae
        // powercfg (RecommendedAc) y la opción que lo representa es la del valor powercfg.
        foreach (var power in setting.Power)
        {
            if (!power.RecommendedAc.HasValue) continue;
            int match = setting.Options.FindIndex(o => PowerOptionValue(o) == power.RecommendedAc.Value);
            if (match >= 0) return match;
        }

        return null;
    }

    /// <summary>Índice de la opción que trae Windows de fábrica ("Custom" suele serlo).</summary>
    public static int? DefaultOptionIndex(OptimizationSetting setting)
    {
        if (!IsSelection(setting)) return null;

        int index = setting.Options.FindIndex(o => o.IsDefault);
        if (index >= 0) return index;

        // Mismo caso que arriba: el valor de fábrica de un ajuste de energía vive en
        // powercfg (DefaultAc), no en una marca de la opción.
        foreach (var power in setting.Power)
        {
            if (!power.DefaultAc.HasValue) continue;
            int match = setting.Options.FindIndex(o => PowerOptionValue(o) == power.DefaultAc.Value);
            if (match >= 0) return match;
        }

        return null;
    }

    /// <summary>
    /// Valor RECOMENDADO de un ajuste con slider (sale del valor recomendado de
    /// powercfg o, si no hay, de la primera clave de registro que lo defina).
    /// </summary>
    public static long? RecommendedNumericValue(OptimizationSetting setting)
    {
        if (!IsNumeric(setting)) return null;

        foreach (var power in setting.Power)
        {
            if (power.RecommendedAc.HasValue) return power.RecommendedAc.Value;
            if (power.RecommendedDc.HasValue) return power.RecommendedDc.Value;
        }

        foreach (var entry in setting.Registry)
        {
            if (!entry.RecommendedValue.HasValue) continue;
            if (entry.RecommendedValue.Value.ValueKind == JsonValueKind.Number
                && entry.RecommendedValue.Value.TryGetInt64(out var value))
                return value;
        }

        return null;
    }

    /// <summary>Valor que trae Windows de fábrica en un ajuste con slider.</summary>
    public static long? DefaultNumericValue(OptimizationSetting setting)
    {
        if (!IsNumeric(setting)) return null;

        foreach (var power in setting.Power)
        {
            if (power.DefaultAc.HasValue) return power.DefaultAc.Value;
            if (power.DefaultDc.HasValue) return power.DefaultDc.Value;
        }

        foreach (var entry in setting.Registry)
        {
            if (!entry.DefaultValue.HasValue) continue;
            if (entry.DefaultValue.Value.ValueKind == JsonValueKind.Number
                && entry.DefaultValue.Value.TryGetInt64(out var value))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Estado POR DEFECTO del ajuste (el que trae Windows de fábrica): es el
    /// contrapeso de <see cref="RecommendedState"/> y lo que usa el botón
    /// "restaurar" de cada fila.
    /// </summary>
    public static bool? DefaultState(OptimizationSetting setting)
    {
        foreach (var entry in setting.Registry)
        {
            // Toggle por existencia de la clave: la clave viene puesta de fábrica.
            if (IsKeyExistenceToggle(entry)) return true;

            var state = ToggleTargetState(entry.DefaultValue, entry.EnabledValue, entry.DisabledValue);
            if (state.HasValue) return state;
        }

        foreach (var task in setting.Tasks)
            if (task.DefaultState.HasValue) return task.DefaultState;

        foreach (var power in setting.Power)
            if (power.DefaultAc.HasValue) return power.DefaultAc.Value != 0;

        return null;
    }

    private static bool IsToggleLike(OptimizationSetting setting)
        => !IsSelection(setting) && !IsNumeric(setting)
           && (string.Equals(setting.InputType, "Toggle", StringComparison.OrdinalIgnoreCase)
               || string.Equals(setting.InputType, "CheckBox", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True si el ajuste se elige con un combo: trae opciones que mapean a valores
    /// de registro (o a un valor simple de powercfg). Se mira el DATO y no solo el
    /// inputType porque el catálogo heredado de Winhance marca todo como "Toggle":
    /// sin esta deducción, 100 ajustes de opciones se dibujarían como un switch.
    /// </summary>
    public static bool IsSelection(OptimizationSetting setting)
        => string.Equals(setting.InputType, "Selection", StringComparison.OrdinalIgnoreCase)
           || (setting.Options.Count > 0
               && setting.Options.Any(o => o.ValueMappings.Count > 0 || o.SimpleValue.HasValue));

    /// <summary>
    /// True si el ajuste se ajusta con un slider (rango numérico, p. ej. el estado
    /// mínimo del procesador en %).
    /// </summary>
    public static bool IsNumeric(OptimizationSetting setting)
        => !IsSelection(setting)
           && (string.Equals(setting.InputType, "NumericRange", StringComparison.OrdinalIgnoreCase)
               || setting.Numeric is not null);

    /// <summary>
    /// Determina si el toggle está activo: basta con que UNA de las claves de
    /// registro del ajuste esté en su estado "activado" (igual que Winhance).
    /// </summary>
    private static bool? ReadToggleFromRegistry(OptimizationSetting setting)
    {
        bool anyKnown = false;
        bool anyEnabled = false;

        foreach (var entry in setting.Registry)
        {
            var (exists, value) = ReadRegistryValue(entry);
            anyKnown = true;
            if (IsRegistryValueInEnabledState(entry, value, exists))
            {
                anyEnabled = true;
                break;
            }
        }

        if (!anyKnown) return null;
        return anyEnabled;
    }

    private bool? ReadToggleFromTasks(OptimizationSetting setting)
    {
        bool? result = null;
        foreach (var task in setting.Tasks)
        {
            var enabled = ReadTaskState(task);
            if (!enabled.HasValue) continue;
            result = result.GetValueOrDefault() || enabled.Value;
        }
        return result;
    }

    private (int Ac, int Dc)? ReadPowerValue(OptimizationSetting setting)
    {
        var scheme = ActivePowerSchemeGuid();
        if (scheme is null) return null;            foreach (var power in setting.Power)
            {
                var cacheKey = PowerKey(power.SubgroupGuid, power.SettingGuid);
                if (_powerCache.TryGetValue(cacheKey, out var cached)) return cached;

                var value = ReadPowerValues(scheme, power.SubgroupGuid, power.SettingGuid);
                if (value is null) continue;

                _powerCache[cacheKey] = value.Value;
                return value;
            }
        return null;
    }

    // Clave del Registro donde vive cada plan de energía con sus ajustes.
    private const string PowerSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    /// <summary>
    /// GUID del plan de energía activo ("ActivePowerScheme" dentro de PowerSchemes).
    /// Se lee en cada consulta y no se cachea: el usuario puede cambiar de plan
    /// desde el hub de Energía y los badges tienen que seguir ese cambio.
    /// </summary>
    private static string? ActivePowerSchemeGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PowerSchemesKey);
            return key?.GetValue("ActivePowerScheme") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Valores AC/DC de un ajuste del plan activo.
    ///
    /// Se leen del REGISTRO y no de la salida de powercfg: el texto de powercfg
    /// está LOCALIZADO ("Current AC Power Setting Index" solo existe en un Windows
    /// en inglés), así que en cualquier otro idioma el ajuste quedaba sin estado —
    /// y eso incluía los sliders de energía. Cada ajuste del plan guarda
    /// ACSettingIndex/DCSettingIndex como DWORD en
    /// ...\PowerSchemes\&lt;plan&gt;\&lt;subgrupo&gt;\&lt;ajuste&gt;, y si la clave no existe
    /// (plan de fábrica sin overrides) el ajuste se muestra sin estado en vez de
    /// inventar un valor.
    /// </summary>
    private static (int Ac, int Dc)? ReadPowerValues(string scheme, string subgroup, string setting)
    {
        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(subgroup) || string.IsNullOrWhiteSpace(setting))
            return null;

        // 1) Override explícito del plan activo: rápido y sin texto que interpretar.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $"{PowerSchemesKey}\\{scheme}\\{subgroup}\\{setting}");

            if (key?.GetValue("ACSettingIndex") is int ac)
            {
                int dc = key.GetValue("DCSettingIndex") is int d ? d : ac;
                return (ac, dc);
            }
        }
        catch
        {
            // Sigue con powercfg: es el que resuelve el valor heredado.
        }

        // 2) El plan no guarda el ajuste (nunca se cambió): powercfg devuelve el
        //    valor EFECTIVO. Se pide UN ajuste por vez y se leen los números 0x: el
        //    texto de powercfg está localizado y su orden de palabras no es un
        //    contrato, pero los dos últimos 0x de la salida son siempre AC y DC
        //    (antes vienen mínima/máxima/incremento y, en los ajustes enumerados,
        //    los índices posibles —que van sin 0x—).
        var output = RunProcess("powercfg.exe", $"/query SCHEME_CURRENT {subgroup} {setting}", 8000);
        if (output is null) return null;

        var values = new List<int>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.Contains("0x", StringComparison.OrdinalIgnoreCase)) continue;
            var index = ParseHexValue(line);
            if (index.HasValue) values.Add(index.Value);
        }

        if (values.Count == 0) return null;
        if (values.Count == 1) return (values[0], values[0]); // AC-only / DC-only
        return (values[^2], values[^1]);
    }

    /// <summary>Primer valor hexadecimal (0x...) de una línea de powercfg.</summary>
    private static int? ParseHexValue(string line)
    {
        int at = line.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        var hex = new string(line[(at + 2)..].TakeWhile(Uri.IsHexDigit).ToArray());
        if (hex.Length == 0) return null;

        return int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Resuelve el índice de la opción de un ajuste de tipo Selection: la opción
    /// cuyos mapeos de registro coinciden con el estado actual.
    /// </summary>
    private int? ResolveOptionIndex(OptimizationSetting setting)
    {
        if (setting.Options.Count == 0) return null;

        var current = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in setting.Registry)
        {
            var (exists, value) = ReadRegistryValue(entry);
            var key = entry.ValueName ?? "KeyExists";
            current[key] = exists ? value : null;
        }

        for (int i = 0; i < setting.Options.Count; i++)
        {
            var mappings = setting.Options[i].ValueMappings;
            if (mappings.Count == 0) continue;

            bool allMatch = true;
            foreach (var mapping in mappings)
            {
                current.TryGetValue(mapping.Key, out var currentValue);
                if (!ValuesEqual(currentValue, JsonToObject(mapping.Value)))
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch) return i;
        }

        // Ninguna opción coincide: el estado es personalizado. Si el ajuste pide
        // resolver a la opción por defecto, se usa esa.
        if (setting.ResolveUnmatchedToDefault)
        {
            int defaultIndex = setting.Options.FindIndex(o => o.IsDefault);
            if (defaultIndex >= 0) return defaultIndex;
        }

        return null;
    }

    /// <summary>
    /// Valor de powercfg de una opción de un combo de energía. El catálogo heredado
    /// lo guarda en ValueMappings["PowerCfgValue"] (no en SimpleValue), y sin esto
    /// el combo no resolvía su opción actual ni podía aplicarse.
    /// </summary>
    public static int? PowerOptionValue(OptimizationOption option)
    {
        if (option.SimpleValue.HasValue) return option.SimpleValue;

        foreach (var (key, mapped) in option.ValueMappings)
        {
            if (!string.Equals(key, "PowerCfgValue", StringComparison.OrdinalIgnoreCase)) continue;
            if (!mapped.HasValue || mapped.Value.ValueKind != JsonValueKind.Number) continue;
            if (mapped.Value.TryGetInt32(out var value)) return value;
        }

        return null;
    }

    private int? ResolvePowerOptionIndex(OptimizationSetting setting, int acIndex)
    {
        for (int i = 0; i < setting.Options.Count; i++)
        {
            var simple = PowerOptionValue(setting.Options[i]);
            if (simple.HasValue && simple.Value == acIndex) return i;
        }
        return null;
    }

    // ====== Badges ======

    /// <summary>
    /// True si el ajuste tiene con qué comparar (recomendado o valor de fábrica).
    /// Mismo criterio que Winhance: sin esto, el ajuste no muestra badges.
    /// </summary>
    public static bool HasBadgeData(OptimizationSetting setting)
    {
        bool isToggle = IsToggleLike(setting);

        bool hasRegistryData = setting.Registry.Any(r =>
            r.RecommendedValue.HasValue
            || (isToggle ? ToggleTargetState(r.DefaultValue, r.EnabledValue, r.DisabledValue).HasValue
                         : r.DefaultValue.HasValue));

        if (isToggle && setting.RecommendedToggleState.HasValue) hasRegistryData = true;

        bool hasSelectionData = IsSelection(setting)
            && setting.Options.Any(o => o.IsRecommended || o.IsDefault);

        bool hasTaskData = setting.Tasks.Any(t => t.RecommendedState.HasValue || t.DefaultState.HasValue);
        bool hasPowerData = setting.Power.Any(p => p.RecommendedAc.HasValue || p.DefaultAc.HasValue);

        return hasRegistryData || hasSelectionData || hasTaskData || hasPowerData;
    }

    private static (bool Recommended, bool Default) ComputeMatches(
        OptimizationSetting setting, bool? isOn, int? selectedIndex, long? numericValue)
    {
        bool matchesRecommended = true;
        bool matchesDefault = true;
        bool isToggle = IsToggleLike(setting);
        bool isSelection = IsSelection(setting);

        if (isToggle)
        {
            if (setting.RecommendedToggleState.HasValue && isOn.HasValue
                && isOn.Value != setting.RecommendedToggleState.Value)
                matchesRecommended = false;

            foreach (var entry in setting.Registry)
            {
                bool? recommendedState = setting.RecommendedToggleState
                    ?? (entry.RecommendedValue.HasValue
                        ? ToggleTargetState(entry.RecommendedValue, entry.EnabledValue, entry.DisabledValue)
                        : null);

                if (recommendedState.HasValue && isOn.HasValue && recommendedState.Value != isOn.Value)
                    matchesRecommended = false;

                bool? defaultState;
                if (entry.IsGroupPolicy && !entry.DefaultValue.HasValue)
                {
                    defaultState = ToggleTargetState(entry.DefaultValue, entry.EnabledValue, entry.DisabledValue);
                }
                else if (IsKeyExistenceToggle(entry))
                {
                    defaultState = true;
                }
                else
                {
                    defaultState = ToggleTargetState(entry.DefaultValue, entry.EnabledValue, entry.DisabledValue);
                }

                if (defaultState.HasValue && isOn.HasValue && defaultState.Value != isOn.Value)
                    matchesDefault = false;
            }

            foreach (var task in setting.Tasks)
            {
                if (task.RecommendedState.HasValue && isOn.HasValue && isOn.Value != task.RecommendedState.Value)
                    matchesRecommended = false;
                if (task.DefaultState.HasValue && isOn.HasValue && isOn.Value != task.DefaultState.Value)
                    matchesDefault = false;
            }
        }
        else if (isSelection)
        {
            if (selectedIndex.HasValue && selectedIndex.Value >= 0 && selectedIndex.Value < setting.Options.Count)
            {
                var option = setting.Options[selectedIndex.Value];

                // 1) Marcas explícitas de la opción (los combos comunes las traen).
                var anyRecommended = setting.Options.Any(o => o.IsRecommended);
                var anyDefault = setting.Options.Any(o => o.IsDefault);
                if (anyRecommended) matchesRecommended = option.IsRecommended;
                if (anyDefault) matchesDefault = option.IsDefault;

                // 2) Combos de energía: ni "recomendada" ni "de fábrica" están marcadas
                //    en la opción; el valor vive en powercfg. Sin esto quedaban siempre
                //    en "personalizado" aunque estuvieran en el valor recomendado.
                int? optionValue = PowerOptionValue(option);
                foreach (var power in setting.Power)
                {
                    if (power.RecommendedAc.HasValue)
                        matchesRecommended = optionValue == power.RecommendedAc.Value;
                    if (power.DefaultAc.HasValue)
                        matchesDefault = optionValue == power.DefaultAc.Value;
                }
            }
            else
            {
                matchesRecommended = false;
                matchesDefault = false;
            }
        }
        else if (numericValue.HasValue)
        {
            foreach (var entry in setting.Registry)
            {
                if (entry.RecommendedValue.HasValue
                    && !ValuesEqual(numericValue.Value, JsonToObject(entry.RecommendedValue)))
                    matchesRecommended = false;
                if (entry.DefaultValue.HasValue
                    && !ValuesEqual(numericValue.Value, JsonToObject(entry.DefaultValue)))
                    matchesDefault = false;
            }
        }

        // Plan de energía: se compara contra el valor de AC (o DC si no hay batería).
        foreach (var power in setting.Power)
        {
            if (!power.RecommendedAc.HasValue) continue;
            if (selectedIndex.HasValue)
            {
                if (setting.Options.Count > selectedIndex.Value
                    && PowerOptionValue(setting.Options[selectedIndex.Value]) != power.RecommendedAc.Value)
                    matchesRecommended = false;
            }
            else if (numericValue.HasValue && numericValue.Value != power.RecommendedAc.Value)
            {
                matchesRecommended = false;
            }

            if (power.DefaultAc.HasValue)
            {
                if (selectedIndex.HasValue)
                {
                    if (setting.Options.Count > selectedIndex.Value
                        && PowerOptionValue(setting.Options[selectedIndex.Value]) != power.DefaultAc.Value)
                        matchesDefault = false;
                }
                else if (numericValue.HasValue && numericValue.Value != power.DefaultAc.Value)
                {
                    matchesDefault = false;
                }
            }
        }

        return (matchesRecommended, matchesDefault);
    }

    private static bool ComputeIsCustom(
        OptimizationSetting setting, bool? isOn, int? selectedIndex, long? numericValue,
        bool matchesRecommended, bool matchesDefault)
    {
        bool isSelection = IsSelection(setting);
        bool isNumeric = IsNumeric(setting);

        if (isSelection)
        {
            if (setting.IsSubjectivePreference) return false;
            if (!selectedIndex.HasValue) return true;
            return selectedIndex.Value < 0 || selectedIndex.Value >= setting.Options.Count;
        }

        if (isNumeric)
        {
            return HasBadgeData(setting) && !matchesRecommended && !matchesDefault;
        }

        return false;
    }

    /// <summary>
    /// Mapea un valor objetivo a un estado de toggle (true = activado) usando los
    /// arrays de valores activados/desactivados de la clave.
    /// </summary>
    public static bool? ToggleTargetState(
        JsonElement? targetValue, List<JsonElement?>? enabledValue, List<JsonElement?>? disabledValue)
    {
        if (!targetValue.HasValue || targetValue.Value.ValueKind == JsonValueKind.Null)
        {
            if (ContainsNull(enabledValue)) return true;
            if (ContainsNull(disabledValue)) return false;
            return null;
        }

        var target = JsonToObject(targetValue.Value);
        if (ArrayContains(enabledValue, target)) return true;
        if (ArrayContains(disabledValue, target)) return false;
        return null;
    }

    private static bool ContainsNull(List<JsonElement?>? array)
        => array?.Any(v => !v.HasValue || v.Value.ValueKind == JsonValueKind.Null) == true;

    private static bool ArrayContains(List<JsonElement?>? array, object? value)
    {
        if (array is null) return false;
        foreach (var item in array)
        {
            if (!item.HasValue || item.Value.ValueKind == JsonValueKind.Null) continue;
            if (ValuesEqual(value, JsonToObject(item.Value))) return true;
        }
        return false;
    }

    private static bool IsKeyExistenceToggle(OptimizationRegistryEntry entry)
        => entry.ValueName is null
           && entry.EnabledValue is null
           && entry.DisabledValue is null
           && string.Equals(entry.ValueType, "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True si el valor actual de la clave está en su estado "activado".
    /// </summary>
    public static bool IsRegistryValueInEnabledState(
        OptimizationRegistryEntry entry, object? currentValue, bool valueExists)
    {
        // Toggle por existencia de la clave: "activado" = la clave está.
        if (entry.ValueName is null)
        {
            var inverted = !ContainsNull(entry.EnabledValue) && ContainsNull(entry.DisabledValue);
            return inverted ? !valueExists : valueExists;
        }

        // Valor compuesto: se compara el sub-valor.
        if (entry.CompositeStringKey is not null)
        {
            var composite = currentValue?.ToString() ?? "";
            var pairs = ParseCompositeString(composite);
            var enabled = FirstNonNullObject(entry.EnabledValue)?.ToString();
            if (pairs.TryGetValue(entry.CompositeStringKey, out var subValue))
                return string.Equals(subValue, enabled, StringComparison.OrdinalIgnoreCase);
            var defaultStr = entry.DefaultValue.HasValue ? JsonToObject(entry.DefaultValue.Value)?.ToString() : null;
            return string.Equals(defaultStr, enabled, StringComparison.OrdinalIgnoreCase);
        }

        if (entry.EnabledValue is null)
        {
            if (!valueExists) return false;
            if (ArrayContains(entry.DisabledValue, currentValue)) return false;
            if (currentValue is bool b && !b) return false;
            return true;
        }

        if (!valueExists) return ContainsNull(entry.EnabledValue);

        if (ArrayContains(entry.EnabledValue, currentValue)) return true;
        if (ArrayContains(entry.DisabledValue, currentValue)) return false;
        return false;
    }

    private static object? FirstNonNullObject(List<JsonElement?>? array)
    {
        if (array is null) return null;
        foreach (var item in array)
        {
            if (!item.HasValue || item.Value.ValueKind == JsonValueKind.Null) continue;
            return JsonToObject(item.Value);
        }
        return null;
    }

    private static Dictionary<string, string> ParseCompositeString(string composite)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in composite.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;
            result[trimmed[..idx].Trim()] = trimmed[(idx + 1)..].Trim();
        }
        return result;
    }

    /// <summary>Compara dos valores de registro tolerando diferencias de tipo.</summary>
    public static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (Equals(a, b)) return true;

        if (a is byte[] ba && b is byte[] bb) return ba.SequenceEqual(bb);
        if (a is string[] sa && b is string[] sb) return sa.SequenceEqual(sb, StringComparer.OrdinalIgnoreCase);

        try
        {
            return Convert.ToInt64(a, CultureInfo.InvariantCulture) == Convert.ToInt64(b, CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Convierte un valor del JSON del catálogo a un valor de .NET.</summary>
    public static object? JsonToObject(JsonElement? element)
    {
        if (element is null || !element.HasValue) return null;
        var value = element.Value;

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                if (value.TryGetInt32(out var i)) return i;
                if (value.TryGetInt64(out var l)) return l;
                return value.GetDouble();
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Array:
                return value.EnumerateArray().Select(e => e.GetString()).ToArray();
            default:
                return value.ToString();
        }
    }

    // ====== Registro ======

    /// <summary>
    /// Lee un valor de registro. Devuelve (existe, valor). La vista es 64 bits
    /// explícita porque la app es x86 y sin esto abriría Wow6432Node.
    /// </summary>
    public static (bool Exists, object? Value) ReadRegistryValue(OptimizationRegistryEntry entry)
    {
        try
        {
            var (hive, subKey) = SplitKeyPath(entry.KeyPath);
            if (hive is null) return (false, null);

            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null) return (false, null);

            if (entry.ValueName is null) return (true, null);

            var value = key.GetValue(entry.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return (value is not null, value);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>Separa "HKEY_LOCAL_MACHINE\SOFTWARE\..." en hive y sub-clave.</summary>
    public static (RegistryHive? Hive, string SubKey) SplitKeyPath(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath)) return (null, "");

        int idx = keyPath.IndexOf('\\');
        string hiveText = idx < 0 ? keyPath : keyPath[..idx];
        string subKey = idx < 0 ? "" : keyPath[(idx + 1)..];

        var hive = hiveText.ToUpperInvariant() switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => RegistryHive.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => RegistryHive.CurrentUser,
            "HKEY_CLASSES_ROOT" or "HKCR" => RegistryHive.ClassesRoot,
            "HKEY_USERS" or "HKU" => RegistryHive.Users,
            "HKEY_CURRENT_CONFIG" or "HKCC" => RegistryHive.CurrentConfig,
            _ => (RegistryHive?)null,
        };

        return (hive, subKey);
    }

    // ====== Tareas programadas ======

    private bool? ReadTaskState(OptimizationTaskEntry task)
    {
        var fullName = BuildTaskName(task);
        if (string.IsNullOrEmpty(fullName)) return null;

        if (_taskStateCache.TryGetValue(fullName, out var cached)) return cached;

        bool? state = null;
        try
        {
            var output = RunProcess("schtasks.exe", $"/Query /TN \"{fullName}\" /FO LIST /V", 8000);
            if (output is not null)
            {
                foreach (var line in output.Split('\n'))
                {
                    if (!line.Contains("Scheduled Task State", StringComparison.OrdinalIgnoreCase)) continue;
                    state = line.Contains("Disabled", StringComparison.OrdinalIgnoreCase) ? false : true;
                    break;
                }
            }
        }
        catch
        {
            state = null;
        }

        _taskStateCache[fullName] = state;
        return state;
    }

    private static string BuildTaskName(OptimizationTaskEntry task)
    {
        var path = task.TaskPath ?? "";
        if (path.Length == 0) return task.Id;
        if (!path.EndsWith('\\')) path += "\\";
        return path + task.Id;
    }

    // ====== Planes de energía ======

    private static string PowerKey(string subgroupGuid, string settingGuid)
        => $"{subgroupGuid}|{settingGuid}";

    // ====== Utilidades ======

    /// <summary>
    /// Ejecuta un proceso y devuelve su salida combinada (stdout + stderr).
    /// </summary>
    public static string? RunProcess(string fileName, string arguments, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return output;
            }

            return string.IsNullOrWhiteSpace(error) ? output : output + "\n" + error;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Motivo por el que el ajuste no aplica en esta máquina (o null si aplica).
    /// </summary>
    public static string? GetUnsupportedReason(OptimizationSetting setting)
    {
        if (setting.IsWindows11Only && !WindowsCapabilities.IsWindows11)
            return "Solo Windows 11";
        if (setting.IsWindows10Only && WindowsCapabilities.IsWindows11)
            return "Solo Windows 10";

        if (setting.MinimumBuildNumber is int min && Environment.OSVersion.Version.Build < min)
            return $"Requiere Windows build {min}";

        if (setting.MaximumBuildNumber is int max && Environment.OSVersion.Version.Build > max)
            return $"Requiere Windows build {max} o anterior";

        return null;
    }
}
