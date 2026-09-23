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

    /// <summary>Valor actual y predeterminado del esquema (null si no se pueden leer).</summary>
    public int? Value { get; set; }

    public int? DefaultValue { get; set; }

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

    /// <summary>Valores que acepta este equipo, ordenados. Vacío si no lo expone.</summary>
    public List<int> BoostModeOptions { get; set; } = new();

    /// <summary>El equipo expone el estado máximo del procesador (el techo, turbo incluido).</summary>
    public bool LimitExposed { get; set; }

    /// <summary>Porcentaje actual del estado máximo del procesador.</summary>
    public int? LimitPercent { get; set; }

    public int LimitMin { get; set; }

    public int LimitMax { get; set; } = 100;

    public int LimitStep { get; set; } = 1;

    /// <summary>Unidad del rango declarada por el sistema (normalmente "%").</summary>
    public string LimitUnits { get; set; } = "%";

    /// <summary>Predeterminado de Windows para el tipo de plan (a esto vuelve "Restaurar").</summary>
    public int? DefaultBoostMode { get; set; }

    public int? DefaultLimitPercent { get; set; }

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
    /// Aplica los valores indicados (null = no tocar ese control) sobre el plan activo,
    /// validando primero contra el rango que el equipo declara, y verifica después
    /// re-leyendo: si el equipo no lo aceptó, lo informa en vez de dar éxito.
    /// </summary>
    Task<CommandResult> ApplyAsync(int? boostMode, int? limitPercent);

    /// <summary>
    /// Aplica ajustes avanzados del procesador (GUID de configuración → valor). Cada valor
    /// se valida contra el rango que declara el equipo y se verifica releyendo.
    /// </summary>
    Task<CommandResult> ApplyAdvancedAsync(IReadOnlyDictionary<string, int> values);

    /// <summary>
    /// Devuelve todas las configuraciones del techo de turbo y de los ajustes avanzados al
    /// valor predeterminado de Windows para ese tipo de plan (el que reporta el equipo, no
    /// uno inventado).
    /// </summary>
    Task<CommandResult> RestoreDefaultsAsync();
}
