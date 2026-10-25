using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WHPO.Core.Optimizations;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Techo de turbo sobre el plan de energía activo (ver <see cref="ITurboCeilingService"/>).
///
/// Reglas del servicio, para que el apartado no mienta:
/// <list type="bullet">
/// <item>Solo se muestran los controles que el equipo realmente expone: si el catálogo
/// del sistema no conoce la configuración, se marca como no expuesta.</item>
/// <item>Los valores de fábrica salen del propio sistema (predeterminado del esquema);
/// "Restaurar" escribe ese valor, nunca uno supuesto.</item>
/// <item>Después de aplicar se vuelve a leer: si el valor no quedó, se informa que el
/// equipo no lo aceptó (BIOS o firmware) en vez de reportar éxito.</item>
/// <item>El nivel avanzado (límites por núcleos y vatios) no se ofrece como control
/// porque requiere acceso a los registros del CPU: se explica por qué.</item>
/// </list>
/// </summary>
public class TurboCeilingService : ITurboCeilingService
{
    // GUIDs documentados por Microsoft de la "Administración de energía del procesador".
    private const string ProcessorSubgroup = "54533251-82be-4824-96c1-47b60b740d00";

    /// <summary>PROCTHROTTLEMAX: estado máximo del procesador (el techo, turbo incluido).</summary>
    private const string MaxProcessorState = "bc5038f7-23e0-4960-96da-33abaf5935ec";

    /// <summary>PERFBOOSTMODE: modo boost del procesador (si el turbo pasa del techo base).</summary>
    private const string BoostModeSetting = "be337238-0d82-4146-a960-4f3749d470c7";

    /// <summary>
    /// Valores de modo boost que documenta Windows. Se usan como respaldo cuando el
    /// equipo no declara su propia lista de valores posibles.
    /// </summary>
    private static readonly int[] DocumentedBoostModes = { 0, 1, 2, 3, 4, 5, 6 };

    /// <summary>
    /// Ajustes avanzados que ofrece la card: políticas de energía de Windows con rango
    /// numérico real (no modos, no overclock). El nombre y la ayuda se traducen en la UI
    /// con estas claves; el rango y el valor salen siempre del equipo.
    /// </summary>
    private static readonly (string Guid, string Key, string HelpKey, string Unit)[] AdvancedCatalog =
    {
        ("36687f9e-e3a5-4dbf-b1dc-15eb381c6863",
            "Preferencia de energía del procesador",
            "0 empuja al máximo rendimiento y 100 a la mayor eficiencia, según cómo el procesador administre su frecuencia.",
            "%"),
        ("0cc5b647-c1df-4637-891a-dec35c318583",
            "Mínimo de núcleos activos",
            "Porcentaje de núcleos que Windows mantiene despiertos como mínimo: el resto puede estacionarse cuando no hace falta.",
            "%"),
        // PROCTHROTTLEMIN: el piso del escalado de frecuencia (QuickCPU lo llama Frequency
        // Scaling). Es el ajuste que faltaba para tener el juego completo junto a core
        // parking y la preferencia de energía; el techo ya está arriba, como «Límite de
        // rendimiento».
        ("893dee8e-2bef-41e0-89c6-b55d0929964c",
            "Mínimo de frecuencia del procesador",
            "El piso del escalado de frecuencia: el porcentaje por debajo del cual Windows no baja el procesador. Subirlo mantiene los núcleos despiertos y a más frecuencia (responde antes, gasta y calienta más); bajarlo ahorra energía en reposo.",
            "%")
    };

    /// <summary>
    /// Valores recomendados por GUID de powercfg, separados por modo (corriente y batería),
    /// tomados del catálogo de Optimizaciones. Es la única fuente de recomendados de la app:
    /// la card no mantiene su propia tabla, así no puede recomendar algo distinto.
    /// </summary>
    private static readonly Lazy<(Dictionary<string, int> Ac, Dictionary<string, int> Dc)> CatalogRecommended =
        new(BuildCatalogRecommended);

    private static (Dictionary<string, int> Ac, Dictionary<string, int> Dc) BuildCatalogRecommended()
    {
        var ac = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var feature in OptimizationCatalogProvider.Catalog.Features)
                foreach (var setting in feature.Settings)
                    foreach (var power in setting.Power)
                    {
                        if (string.IsNullOrWhiteSpace(power.SettingGuid)) continue;
                        if (power.RecommendedAc != null) ac.TryAdd(power.SettingGuid, power.RecommendedAc.Value);
                        if (power.RecommendedDc != null) dc.TryAdd(power.SettingGuid, power.RecommendedDc.Value);
                    }
        }
        catch
        {
            // Sin catálogo no hay recomendados: la card sigue mostrando lo que el equipo
            // expone y «Recomendado» solo mueve el techo y el boost.
        }
        return (ac, dc);
    }

    private static int? RecommendedCatalogValue(string settingGuid, TurboPowerMode mode)
    {
        var map = mode == TurboPowerMode.Dc ? CatalogRecommended.Value.Dc : CatalogRecommended.Value.Ac;
        return map.TryGetValue(settingGuid, out var value) ? value : null;
    }

    /// <summary>Valor AC que se escribe: el pedido si el modo es AC; si no, el que ya tenía.</summary>
    private static uint AcFor(TurboPowerMode mode, int requested, int? currentOther)
        => (uint)(mode == TurboPowerMode.Ac ? requested : currentOther ?? requested);

    /// <summary>Valor DC que se escribe: el pedido si el modo es DC; si no, el que ya tenía.</summary>
    private static uint DcFor(TurboPowerMode mode, int requested, int? currentOther)
        => (uint)(mode == TurboPowerMode.Dc ? requested : currentOther ?? requested);

    private readonly ICpuPowerService _cpuPowerService;
    private readonly ISystemInfoService _systemInfoService;
    private readonly ILoggingService _loggingService;

    public TurboCeilingService(
        ICpuPowerService cpuPowerService,
        ISystemInfoService systemInfoService,
        ILoggingService loggingService)
    {
        _cpuPowerService = cpuPowerService;
        _systemInfoService = systemInfoService;
        _loggingService = loggingService;
    }

    public TurboCeilingState GetState()
    {
        var state = new TurboCeilingState();
        try
        {
            state.PlanGuid = _cpuPowerService.GetActivePowerPlanGuid() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(state.PlanGuid))
            {
                // Sin plan activo no hay nada que leer (y ningún número que mostrar).
                state.CpuName = ReadCpuName();
                state.AdvancedAvailability = ResolveAdvancedAvailability(state.CpuName);
                return state;
            }

            foreach (var plan in _cpuPowerService.GetPowerPlans())
            {
                if (plan.Guid.Equals(state.PlanGuid, StringComparison.OrdinalIgnoreCase))
                {
                    state.PlanName = plan.Name;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(state.PlanName)) state.PlanName = state.PlanGuid;

            LoadLimit(state);
            LoadBoostMode(state);
            LoadAdvanced(state);

            state.CpuName = ReadCpuName();
            state.AdvancedAvailability = ResolveAdvancedAvailability(state.CpuName);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error leyendo el techo de turbo", ex);
        }
        return state;
    }

    private void LoadLimit(TurboCeilingState state)
    {
        var setting = _cpuPowerService.GetPowerSettingState(state.PlanGuid, ProcessorSubgroup, MaxProcessorState);
        if (setting?.AcValue == null) return;   // el equipo no lo expone: no se muestra

        state.LimitExposed = true;
        state.LimitPercent = (int)setting.AcValue.Value;
        if (setting.DefaultAc != null) state.DefaultLimitPercent = (int)setting.DefaultAc.Value;
        if (setting.DcValue != null) state.LimitPercentDc = (int)setting.DcValue.Value;
        if (setting.DefaultDc != null) state.DefaultLimitPercentDc = (int)setting.DefaultDc.Value;
        if (setting.HasRange && setting.Max > setting.Min)
        {
            state.LimitMin = (int)setting.Min;
            state.LimitMax = (int)setting.Max;
            state.LimitStep = setting.Step > 0 ? (int)setting.Step : 1;
        }
        if (!string.IsNullOrWhiteSpace(setting.Units)) state.LimitUnits = setting.Units;
    }

    private void LoadBoostMode(TurboCeilingState state)
    {
        var setting = _cpuPowerService.GetPowerSettingState(state.PlanGuid, ProcessorSubgroup, BoostModeSetting);
        if (setting?.AcValue == null) return;

        state.BoostModeExposed = true;
        state.BoostModeValue = (int)setting.AcValue.Value;
        if (setting.DefaultAc != null) state.DefaultBoostMode = (int)setting.DefaultAc.Value;
        if (setting.DcValue != null) state.BoostModeValueDc = (int)setting.DcValue.Value;
        if (setting.DefaultDc != null) state.DefaultBoostModeDc = (int)setting.DefaultDc.Value;

        // Los valores que acepta el equipo, si los declara; si no, los documentados.
        var options = setting.PossibleValues != null && setting.PossibleValues.Count > 0
            ? setting.PossibleValues.Select(v => (int)v.Value).Distinct().OrderBy(v => v).ToList()
            : DocumentedBoostModes.ToList();

        // El valor actual siempre tiene que estar en la lista, aunque el equipo no lo
        // declare entre los posibles (si no, el desplegable quedaría apuntando a nada).
        if (!options.Contains(state.BoostModeValue.Value)) options.Add(state.BoostModeValue.Value);
        options.Sort();
        state.BoostModeOptions = options;
    }

    /// <summary>
    /// Lee los ajustes avanzados del procesador. Solo se marcan como expuestos los que el
    /// sistema conoce y tienen valor: lo que no se puede leer no se muestra, en vez de
    /// aparecer con un número inventado.
    /// </summary>
    private void LoadAdvanced(TurboCeilingState state)
    {
        foreach (var (guid, key, helpKey, unit) in AdvancedCatalog)
        {
            var setting = _cpuPowerService.GetPowerSettingState(state.PlanGuid, ProcessorSubgroup, guid);
            var advanced = new TurboAdvancedSetting
            {
                SettingGuid = guid,
                Key = key,
                HelpKey = helpKey,
                UnitSymbol = unit,
                RecommendedValue = RecommendedCatalogValue(guid, TurboPowerMode.Ac),
                RecommendedDc = RecommendedCatalogValue(guid, TurboPowerMode.Dc)
            };

            if (setting?.AcValue != null)
            {
                advanced.Exposed = true;
                advanced.Value = (int)setting.AcValue.Value;
                if (setting.DefaultAc != null) advanced.DefaultValue = (int)setting.DefaultAc.Value;
                if (setting.DcValue != null) advanced.DcValue = (int)setting.DcValue.Value;
                if (setting.DefaultDc != null) advanced.DefaultDc = (int)setting.DefaultDc.Value;
                if (setting.HasRange && setting.Max > setting.Min)
                {
                    advanced.Min = (int)setting.Min;
                    advanced.Max = (int)setting.Max;
                    advanced.Step = setting.Step > 0 ? (int)setting.Step : 1;
                }
            }

            state.Advanced.Add(advanced);
        }
    }

    private string ReadCpuName()
    {
        try { return _systemInfoService.GetCpuInfo()?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Por qué el nivel avanzado no está disponible hoy: los límites de turbo por núcleos
    /// y los vatios se escriben en registros internos del CPU (MSR), y desde una app de
    /// usuario eso no se puede: hace falta acceso de kernel y la app todavía no escribe
    /// ahí. En AMD, además, esos límites los maneja el firmware del procesador.
    /// Nunca se declara como disponible: mientras no se implemente, la UI explica por qué.
    /// </summary>
    private static TurboAdvancedAvailability ResolveAdvancedAvailability(string cpuName)
    {
        if (cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) ||
            cpuName.Contains("Athlon", StringComparison.OrdinalIgnoreCase) ||
            cpuName.Contains("Threadripper", StringComparison.OrdinalIgnoreCase))
            return TurboAdvancedAvailability.FirmwareManaged;

        return TurboAdvancedAvailability.MissingKernelAccess;
    }

    public async Task<CommandResult> ApplyAsync(int? boostMode, int? limitPercent, TurboPowerMode mode = TurboPowerMode.Ac)
    {
        try
        {
            // Se relee el estado antes de escribir: el rango y los valores válidos son los
            // del equipo en este momento, no los que la UI tenía cuando pintó el control.
            var state = GetState();
            if (string.IsNullOrWhiteSpace(state.PlanGuid))
                return new CommandResult(false, "No hay un plan de energía activo.",
                    "No hay un plan de energía activo.");

            // Sin ningún valor pedido no hay nada que aplicar: reportar éxito sería mentir.
            if (boostMode == null && limitPercent == null)
                return new CommandResult(false, "No hay ningún cambio para aplicar.",
                    "No hay ningún cambio para aplicar.");

            if (boostMode != null && !state.BoostModeExposed)
                return new CommandResult(false, "Este equipo no expone el modo boost del procesador.",
                    "Este equipo no expone el modo boost del procesador.");
            if (boostMode != null && !state.BoostModeOptions.Contains(boostMode.Value))
                return new CommandResult(false, "El valor del modo boost no es válido para este equipo.",
                    "El valor del modo boost no es válido para este equipo.");

            if (limitPercent != null)
            {
                if (!state.LimitExposed)
                    return new CommandResult(false, "Este equipo no expone el estado máximo del procesador.",
                        "Este equipo no expone el estado máximo del procesador.");
                if (limitPercent.Value < state.LimitMin || limitPercent.Value > state.LimitMax)
                    return new CommandResult(false,
                        $"El valor está fuera del rango que acepta el equipo ({state.LimitMin}–{state.LimitMax} {state.LimitUnits}).",
                        "El valor está fuera del rango que acepta el equipo ({0}–{1} {2}).",
                        new object[] { state.LimitMin, state.LimitMax, state.LimitUnits });
            }

            // Se escribe solo el valor del modo elegido: el del otro modo viaja tal como
            // estaba, para no pisar la configuración de batería al tocar la de corriente (ni
            // al revés).
            if (boostMode != null)
            {
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, BoostModeSetting,
                    AcFor(mode, boostMode.Value, state.BoostModeValueDc),
                    DcFor(mode, boostMode.Value, state.BoostModeValue));
                if (!write.Success) return write;
            }

            if (limitPercent != null)
            {
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, MaxProcessorState,
                    AcFor(mode, limitPercent.Value, state.LimitPercentDc),
                    DcFor(mode, limitPercent.Value, state.LimitPercent));
                if (!write.Success) return write;
            }

            // Verificación: se vuelve a leer y se compara con lo pedido. Si el equipo no lo
            // aceptó (BIOS, firmware, política), se dice; no se reporta un éxito falso.
            var after = GetState();
            var appliedLimit = mode == TurboPowerMode.Dc ? after.LimitPercentDc : after.LimitPercent;
            var appliedBoost = mode == TurboPowerMode.Dc ? after.BoostModeValueDc : after.BoostModeValue;
            if (limitPercent != null && appliedLimit != limitPercent.Value)
                return new CommandResult(false,
                    "El equipo no aplicó el límite: puede estar bloqueado por el firmware o la BIOS.",
                    "El equipo no aplicó el límite: puede estar bloqueado por el firmware o la BIOS.");
            if (boostMode != null && appliedBoost != boostMode.Value)
                return new CommandResult(false,
                    "El equipo no aplicó el modo boost: puede estar bloqueado por el firmware o la BIOS.",
                    "El equipo no aplicó el modo boost: puede estar bloqueado por el firmware o la BIOS.");

            return new CommandResult(true, "Techo de turbo aplicado.", "Techo de turbo aplicado.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error aplicando el techo de turbo", ex);
            return new CommandResult(false, ex.Message);
        }
    }

    public async Task<CommandResult> ApplyAdvancedAsync(IReadOnlyDictionary<string, int> values, TurboPowerMode mode = TurboPowerMode.Ac)
    {
        try
        {
            if (values == null || values.Count == 0)
                return new CommandResult(false, "No hay ningún cambio para aplicar.",
                    "No hay ningún cambio para aplicar.");

            // Se relevéa el estado: el rango válido es el que declara el equipo ahora, no el
            // que la UI tenía cuando pintó el control.
            var state = GetState();
            if (string.IsNullOrWhiteSpace(state.PlanGuid))
                return new CommandResult(false, "No hay un plan de energía activo.",
                    "No hay un plan de energía activo.");

            foreach (var pair in values)
            {
                var setting = state.Advanced.Find(a => a.SettingGuid.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                if (setting == null || !setting.Exposed)
                    return new CommandResult(false, "Este equipo no expone ese ajuste del procesador.",
                        "Este equipo no expone ese ajuste del procesador.");
                if (pair.Value < setting.Min || pair.Value > setting.Max)
                    return new CommandResult(false,
                        $"El valor está fuera del rango que acepta el equipo ({setting.Min}–{setting.Max} {setting.UnitSymbol}).",
                        "El valor está fuera del rango que acepta el equipo ({0}–{1} {2}).",
                        new object[] { setting.Min, setting.Max, setting.UnitSymbol });
            }

            foreach (var pair in values)
            {
                var current = state.Advanced.Find(a => a.SettingGuid.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                var other = mode == TurboPowerMode.Dc ? current?.Value : current?.DcValue;
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, pair.Key,
                    AcFor(mode, pair.Value, other),
                    DcFor(mode, pair.Value, other));
                if (!write.Success) return write;
            }

            // Verificación: se vuelve a leer y se compara. Si el equipo no lo aceptó, se dice.
            var after = GetState();
            foreach (var pair in values)
            {
                var setting = after.Advanced.Find(a => a.SettingGuid.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                var applied = mode == TurboPowerMode.Dc ? setting?.DcValue : setting?.Value;
                if (setting == null || applied != pair.Value)
                    return new CommandResult(false,
                        "El equipo no aplicó el ajuste: puede estar bloqueado por el firmware o la BIOS.",
                        "El equipo no aplicó el ajuste: puede estar bloqueado por el firmware o la BIOS.");
            }

            return new CommandResult(true, "Ajustes avanzados aplicados.", "Ajustes avanzados aplicados.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error aplicando ajustes avanzados del procesador", ex);
            return new CommandResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Perfil recomendado para jugar: el techo de frecuencia al máximo del plan (100 %) y el
    /// modo boost «Agresivo» si el equipo lo expone. Si el firmware no declara ese modo, cae a
    /// «Activado»; si tampoco, solo se mueve el límite. Además aplica el recomendado de cada
    /// ajuste avanzado que el equipo exponga, tomado del catálogo de Optimizaciones: la misma
    /// fuente que usa el atajo de esa pantalla, así la app no recomienda dos cosas distintas.
    /// Los ajustes sin recomendado, fuera del rango del equipo o que ya están en ese valor se
    /// saltan. El recomendado es el del modo elegido: el catálogo declara uno para corriente y
    /// otro para batería. Valida y verifica releyendo antes de reportar éxito.
    /// </summary>
    public async Task<CommandResult> ApplyRecommendedAsync(TurboPowerMode mode = TurboPowerMode.Ac)
    {
        try
        {
            var state = GetState();
            if (string.IsNullOrWhiteSpace(state.PlanGuid))
                return new CommandResult(false, "No hay un plan de energía activo.",
                    "No hay un plan de energía activo.");

            if (!state.HasAnyControl)
                return new CommandResult(false, "Este equipo no expone ningún ajuste del techo de turbo.",
                    "Este equipo no expone ningún ajuste del techo de turbo.");

            int? limit = state.LimitExposed ? Math.Clamp(100, state.LimitMin, state.LimitMax) : null;

            int? boost = null;
            if (state.BoostModeExposed)
            {
                if (state.BoostModeOptions.Contains(2)) boost = 2;      // Agresivo
                else if (state.BoostModeOptions.Contains(1)) boost = 1; // Activado
            }

            // Ajustes avanzados: se aplica el recomendado del catálogo para el modo elegido,
            // solo si el equipo lo expone, entra en su rango y todavía no está en ese valor.
            var advanced = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in state.Advanced)
            {
                if (!setting.Exposed) continue;
                var recommended = mode == TurboPowerMode.Dc ? setting.RecommendedDc : setting.RecommendedValue;
                var current = mode == TurboPowerMode.Dc ? setting.DcValue : setting.Value;
                if (recommended == null) continue;
                var value = recommended.Value;
                if (value < setting.Min || value > setting.Max) continue;
                if (current == value) continue;
                advanced[setting.SettingGuid] = value;
            }

            var hasRecommended = state.LimitExposed || state.BoostModeExposed
                || state.Advanced.Exists(a => a.Exposed &&
                    (mode == TurboPowerMode.Dc ? a.RecommendedDc : a.RecommendedValue) != null);
            if (!hasRecommended)
                return new CommandResult(false, "Este equipo no expone ningún ajuste del techo de turbo.",
                    "Este equipo no expone ningún ajuste del techo de turbo.");

            if (limit != null || boost != null)
            {
                var result = await ApplyAsync(boost, limit, mode);
                if (!result.Success) return result;
            }

            if (advanced.Count > 0)
            {
                var advancedResult = await ApplyAdvancedAsync(advanced, mode);
                if (!advancedResult.Success) return advancedResult;
            }

            return new CommandResult(true, "Perfil recomendado aplicado.", "Perfil recomendado aplicado.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error aplicando el perfil recomendado del techo de turbo", ex);
            return new CommandResult(false, ex.Message);
        }
    }

    public async Task<CommandResult> RestoreDefaultsAsync(TurboPowerMode mode = TurboPowerMode.Ac)
    {
        try
        {
            var state = GetState();
            if (string.IsNullOrWhiteSpace(state.PlanGuid))
                return new CommandResult(false, "No hay un plan de energía activo.",
                    "No hay un plan de energía activo.");

            if (!state.HasAnyControl)
                return new CommandResult(false, "Este equipo no expone ningún ajuste del techo de turbo.",
                    "Este equipo no expone ningún ajuste del techo de turbo.");

            // El predeterminado que se restaura es el del modo elegido: Windows guarda uno
            // para corriente y otro para batería, y no tienen por qué ser el mismo.
            var limitDefault = mode == TurboPowerMode.Dc ? state.DefaultLimitPercentDc : state.DefaultLimitPercent;
            if (state.LimitExposed && limitDefault == null)
                return new CommandResult(false,
                    "El sistema no reporta el valor predeterminado del estado máximo del procesador.",
                    "El sistema no reporta el valor predeterminado del estado máximo del procesador.");

            if (state.LimitExposed)
            {
                var limit = limitDefault!.Value;
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, MaxProcessorState,
                    AcFor(mode, limit, state.LimitPercentDc),
                    DcFor(mode, limit, state.LimitPercent));
                if (!write.Success) return write;
            }

            var boostDefault = mode == TurboPowerMode.Dc ? state.DefaultBoostModeDc : state.DefaultBoostMode;
            if (state.BoostModeExposed && boostDefault != null)
            {
                var boost = boostDefault.Value;
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, BoostModeSetting,
                    AcFor(mode, boost, state.BoostModeValueDc),
                    DcFor(mode, boost, state.BoostModeValue));
                if (!write.Success) return write;
            }

            // Ajustes avanzados: cada uno vuelve al predeterminado que reporta el sistema para
            // el modo elegido (el de batería y el de corriente son distintos).
            foreach (var setting in state.Advanced)
            {
                if (!setting.Exposed) continue;
                var value = mode == TurboPowerMode.Dc ? setting.DefaultDc : setting.DefaultValue;
                var other = mode == TurboPowerMode.Dc ? setting.Value : setting.DcValue;
                if (value == null) continue;
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, setting.SettingGuid,
                    AcFor(mode, value.Value, other),
                    DcFor(mode, value.Value, other));
                if (!write.Success) return write;
            }

            return new CommandResult(true, "Techo de turbo restaurado.", "Techo de turbo restaurado.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error restaurando el techo de turbo", ex);
            return new CommandResult(false, ex.Message);
        }
    }
}
