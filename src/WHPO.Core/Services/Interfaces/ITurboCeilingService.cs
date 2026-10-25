using System.Collections.Generic;
using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Disponibilidad del nivel avanzado del techo de turbo (los límites por cantidad de
/// núcleos activos y los vatios máximos sostenidos). Ese nivel se escribe en registros
/// internos del CPU (MSR), y una app normal no puede: necesita un driver de kernel
/// firmado. Se declara acá para que la UI pueda explicar por qué NO está disponible en
/// vez de mostrar un control que no hace nada.
/// </summary>
public enum TurboAdvancedAvailability
{
    /// <summary>El equipo permitiría escribir los límites y hay acceso de kernel.</summary>
    Available,

    /// <summary>Falta el acceso a los registros del CPU (driver de kernel firmado).</summary>
    MissingKernelAccess,

    /// <summary>Los límites los maneja el firmware del procesador (AMD): no se escriben desde Windows.</summary>
    FirmwareManaged
}

/// <summary>
/// Conjunto de valores del plan de energía al que se aplica un cambio. Windows guarda uno
/// para «con corriente» (AC) y otro para «con batería» (DC); un equipo de escritorio solo
/// usa el de corriente.
/// </summary>
public enum TurboPowerMode
{
    /// <summary>Enchufado a la corriente (lo único que usa un equipo de escritorio).</summary>
    Ac,

    /// <summary>Con batería.</summary>
    Dc
}

/// <summary>
/// Un ajuste avanzado del procesador: una política de energía de Windows con rango
/// numérico (por ejemplo la preferencia de energía o el mínimo de núcleos activos).
/// No es overclock ni toca registros del CPU: son ajustes que Windows aplica por plan.
/// </summary>
public sealed class TurboAdvancedSetting
{
    /// <summary>Clave de traducción del nombre del ajuste.</summary>
    public string Key { get; set; } = "";

    /// <summary>Clave de traducción de la ayuda (qué hace, sin prometer rendimiento).</summary>
    public string HelpKey { get; set; } = "";

    /// <summary>GUID de la configuración dentro de "Administración de energía del procesador".</summary>
    public string SettingGuid { get; set; } = "";

    /// <summary>Símbolo de la unidad para mostrar ("%", "ms"). No se traduce.</summary>
    public string UnitSymbol { get; set; } = "";

    /// <summary>El equipo expone este ajuste (si no, no se muestra).</summary>
    public bool Exposed { get; set; }

    /// <summary>Valor actual y predeterminado con corriente (null si no se pueden leer).</summary>
    public int? Value { get; set; }

    public int? DefaultValue { get; set; }

    /// <summary>Valor actual y predeterminado con batería (null si no se pueden leer).</summary>
    public int? DcValue { get; set; }

    public int? DefaultDc { get; set; }

    /// <summary>
    /// Valor recomendado por el catálogo de Optimizaciones con corriente (null si no declara
    /// uno). Es el que aplica el atajo «Recomendado» de la card, para que la card y
    /// Optimizaciones no recomienden cosas distintas.
    /// </summary>
    public int? RecommendedValue { get; set; }

    /// <summary>Valor recomendado con batería, del mismo catálogo.</summary>
    public int? RecommendedDc { get; set; }

    public int Min { get; set; }

    public int Max { get; set; } = 100;

    public int Step { get; set; } = 1;
}

/// <summary>
/// Estado del techo de turbo del plan activo, leído del equipo. Todos los números vienen
/// de Windows o del CPU: lo que no se puede leer queda en null y la UI no lo muestra.
/// </summary>
public sealed class TurboCeilingState
{
    public string PlanGuid { get; set; } = "";

    /// <summary>Nombre del plan activo (el ajuste se aplica a ese plan).</summary>
    public string PlanName { get; set; } = "";

    /// <summary>El equipo expone el modo boost (si el turbo puede pasar del techo base).</summary>
    public bool BoostModeExposed { get; set; }

    /// <summary>Valor actual del modo boost, tal como lo reporta Windows.</summary>
    public int? BoostModeValue { get; set; }

    /// <summary>Valor del modo boost con batería.</summary>
    public int? BoostModeValueDc { get; set; }

    /// <summary>Valores que acepta este equipo, ordenados. Vacío si no lo expone.</summary>
    public List<int> BoostModeOptions { get; set; } = new();

    /// <summary>El equipo expone el estado máximo del procesador (el techo, turbo incluido).</summary>
    public bool LimitExposed { get; set; }

    /// <summary>Porcentaje actual del estado máximo del procesador.</summary>
    public int? LimitPercent { get; set; }

    /// <summary>Porcentaje con batería.</summary>
    public int? LimitPercentDc { get; set; }

    public int LimitMin { get; set; }

    public int LimitMax { get; set; } = 100;

    public int LimitStep { get; set; } = 1;

    /// <summary>Unidad del rango declarada por el sistema (normalmente "%").</summary>
    public string LimitUnits { get; set; } = "%";

    /// <summary>Predeterminado de Windows para el tipo de plan (a esto vuelve "Restaurar").</summary>
    public int? DefaultBoostMode { get; set; }

    public int? DefaultBoostModeDc { get; set; }

    public int? DefaultLimitPercent { get; set; }

    public int? DefaultLimitPercentDc { get; set; }

    /// <summary>Nombre del procesador (para el aviso del nivel avanzado).</summary>
    public string CpuName { get; set; } = "";

    public TurboAdvancedAvailability AdvancedAvailability { get; set; } = TurboAdvancedAvailability.MissingKernelAccess;

    /// <summary>Ajustes avanzados del procesador que expone este equipo.</summary>
    public List<TurboAdvancedSetting> Advanced { get; set; } = new();

    /// <summary>Hay algo que se pueda tocar (el techo, el boost o algún ajuste avanzado).</summary>
    public bool HasAnyControl => BoostModeExposed || LimitExposed || Advanced.Exists(a => a.Exposed);
}

/// <summary>
/// "Techo de turbo": mueve el techo de frecuencia del procesador dentro del plan de
/// energía activo (modo boost y estado máximo del procesador). Es política de energía de
/// Windows, así que no requiere driver, funciona en Intel y AMD y se revierte solo.
///
/// Lo que este servicio NO hace y la UI dice: no supera el turbo que el CPU ya admite
/// (eso lo decide el CPU y la BIOS), no es overclock y bajar el techo no acelera nada,
/// cambia calor y ruido por rendimiento.
/// </summary>
public interface ITurboCeilingService
{
    /// <summary>
    /// Lee el estado real del plan activo: qué expone el equipo, con qué rango y en qué
    /// valor está. No modifica nada.
    /// </summary>
    TurboCeilingState GetState();

    /// <summary>
    /// Aplica los valores indicados (null = no tocar ese control) sobre el plan activo en el
    /// modo de energía indicado, validando primero contra el rango que el equipo declara, y
    /// verifica después releyendo: si el equipo no lo aceptó, lo informa en vez de dar éxito.
    /// El valor del otro modo queda como estaba.
    /// </summary>
    Task<CommandResult> ApplyAsync(int? boostMode, int? limitPercent, TurboPowerMode mode = TurboPowerMode.Ac);

    /// <summary>
    /// Aplica ajustes avanzados del procesador (GUID de configuración → valor) en el modo de
    /// energía indicado. Cada valor se valida contra el rango que declara el equipo y se
    /// verifica releyendo. El valor del otro modo queda como estaba.
    /// </summary>
    Task<CommandResult> ApplyAdvancedAsync(IReadOnlyDictionary<string, int> values, TurboPowerMode mode = TurboPowerMode.Ac);

    /// <summary>
    /// Devuelve todas las configuraciones del techo de turbo y de los ajustes avanzados al
    /// valor predeterminado de Windows para ese tipo de plan en el modo de energía indicado
    /// (el que reporta el equipo, no uno inventado). El valor del otro modo queda como estaba.
    /// </summary>
    Task<CommandResult> RestoreDefaultsAsync(TurboPowerMode mode = TurboPowerMode.Ac);

    /// <summary>
    /// Aplica el perfil recomendado para jugar: el techo de frecuencia al máximo del plan
    /// (100 %) y el modo boost «Agresivo» si el equipo lo expone (si no, «Activado»).
    /// También aplica el valor recomendado de cada ajuste avanzado del procesador que el
    /// equipo exponga, tomado del catálogo de Optimizaciones (la misma fuente que usa el
    /// atajo de esa pantalla). Los ajustes sin recomendado, o cuyo valor no entra en el
    /// rango del equipo, se saltan. Valida y verifica igual que <see cref="ApplyAsync"/>.
    /// </summary>
    Task<CommandResult> ApplyRecommendedAsync(TurboPowerMode mode = TurboPowerMode.Ac);
}
