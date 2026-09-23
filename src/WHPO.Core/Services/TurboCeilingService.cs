using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
                UnitSymbol = unit
            };

            if (setting?.AcValue != null)
            {
                advanced.Exposed = true;
                advanced.Value = (int)setting.AcValue.Value;
                if (setting.DefaultAc != null) advanced.DefaultValue = (int)setting.DefaultAc.Value;
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

    public async Task<CommandResult> ApplyAsync(int? boostMode, int? limitPercent)
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

            if (boostMode != null)
            {
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, BoostModeSetting, (uint)boostMode.Value, (uint)boostMode.Value);
                if (!write.Success) return write;
            }

            if (limitPercent != null)
            {
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, MaxProcessorState, (uint)limitPercent.Value, (uint)limitPercent.Value);
                if (!write.Success) return write;
            }

            // Verificación: se vuelve a leer y se compara con lo pedido. Si el equipo no lo
            // aceptó (BIOS, firmware, política), se dice; no se reporta un éxito falso.
            var after = GetState();
            if (limitPercent != null && after.LimitPercent != limitPercent.Value)
                return new CommandResult(false,
                    "El equipo no aplicó el límite: puede estar bloqueado por el firmware o la BIOS.",
                    "El equipo no aplicó el límite: puede estar bloqueado por el firmware o la BIOS.");
            if (boostMode != null && after.BoostModeValue != boostMode.Value)
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

    public async Task<CommandResult> ApplyAdvancedAsync(IReadOnlyDictionary<string, int> values)
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
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, pair.Key, (uint)pair.Value, (uint)pair.Value);
                if (!write.Success) return write;
            }

            // Verificación: se vuelve a leer y se compara. Si el equipo no lo aceptó, se dice.
            var after = GetState();
            foreach (var pair in values)
            {
                var setting = after.Advanced.Find(a => a.SettingGuid.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                if (setting == null || setting.Value != pair.Value)
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

    public async Task<CommandResult> RestoreDefaultsAsync()
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

            if (state.LimitExposed && state.DefaultLimitPercent == null)
                return new CommandResult(false,
                    "El sistema no reporta el valor predeterminado del estado máximo del procesador.",
                    "El sistema no reporta el valor predeterminado del estado máximo del procesador.");

            if (state.LimitExposed)
            {
                var limit = state.DefaultLimitPercent!.Value;
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, MaxProcessorState, (uint)limit, (uint)limit);
                if (!write.Success) return write;
            }

            if (state.BoostModeExposed && state.DefaultBoostMode != null)
            {
                var boost = state.DefaultBoostMode.Value;
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, BoostModeSetting, (uint)boost, (uint)boost);
                if (!write.Success) return write;
            }

            // Ajustes avanzados: cada uno vuelve al predeterminado que reporta el sistema.
            foreach (var setting in state.Advanced)
            {
                if (!setting.Exposed || setting.DefaultValue == null) continue;
                var value = setting.DefaultValue.Value;
                var write = await _cpuPowerService.SetPowerSettingAsync(
                    state.PlanGuid, ProcessorSubgroup, setting.SettingGuid, (uint)value, (uint)value);
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
