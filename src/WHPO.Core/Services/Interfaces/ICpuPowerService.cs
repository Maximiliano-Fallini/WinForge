using System.Collections.Generic;
using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Plan de energía de Windows con su estado.
/// </summary>
public record PowerPlanInfo(
    string Guid,
    string Name,
    bool IsActive
);

/// <summary>
/// Configuración individual de un plan (ID de configuración + nombre + valores AC/DC).
/// </summary>
public sealed class PowerSettingInfo
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string AcValue { get; set; } = "";
    public string DcValue { get; set; } = "";

    public PowerSettingInfo(string guid, string name)
    {
        Guid = guid;
        Name = name;
    }
}

/// <summary>
/// Subgrupo de un plan (p. ej. "Administración de energía del procesador", "Batería").
/// </summary>
public sealed class PowerSubgroupInfo
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public List<PowerSettingInfo> Settings { get; } = new();

    public PowerSubgroupInfo(string guid, string name)
    {
        Guid = guid;
        Name = name;
    }
}

/// <summary>
/// Detalle completo de un plan: subgrupos con sus configuraciones y valores AC/DC.
/// </summary>
public sealed class PowerPlanDetail
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public List<PowerSubgroupInfo> Subgroups { get; } = new();

    public PowerPlanDetail(string guid, string name)
    {
        Guid = guid;
        Name = name;
    }
}

/// <summary>
/// Ajuste individual para crear un plan custom (GUID de subgrupo, GUID de
/// configuración y valores AC/DC).
/// </summary>
public record PowerPlanTuning(string SubgroupGuid, string SettingGuid, uint AcValue, uint DcValue);

/// <summary>
/// Servicio para la gestión de planes de energía de Windows (powercfg).
/// </summary>
public interface ICpuPowerService
{
    /// <summary>
    /// Obtiene todos los planes de energía disponibles del sistema.
    /// </summary>
    List<PowerPlanInfo> GetPowerPlans();

    /// <summary>
    /// Obtiene el GUID del plan de energía activo actualmente.
    /// </summary>
    string GetActivePowerPlanGuid();

    /// <summary>
    /// Establece el plan de energía activo.
    /// </summary>
    Task<CommandResult> SetActivePowerPlanAsync(string planGuid);

    /// <summary>
    /// Obtiene el detalle completo de un plan (subgrupos + configuraciones con valores AC/DC).
    /// </summary>
    PowerPlanDetail? GetPowerPlanDetails(string planGuid);

    /// <summary>
    /// Obtiene la descripción localizada de un plan desde el registro.
    /// </summary>
    string GetPowerPlanDescription(string planGuid);

    /// <summary>
    /// Renombra un plan de energía.
    /// </summary>
    Task<CommandResult> RenamePowerPlanAsync(string planGuid, string newName);

    /// <summary>
    /// Elimina un plan de energía (no se puede borrar el plan activo).
    /// </summary>
    Task<CommandResult> DeletePowerPlanAsync(string planGuid);

    /// <summary>
    /// Instala un plan oficial oculto de Windows duplicando su esquema base
    /// (powercfg -duplicatescheme). Reversible: se puede borrar después.
    /// </summary>
    Task<CommandResult> InstallBuiltInSchemeAsync(string schemeGuid);

    /// <summary>
    /// Crea un plan custom duplicando un esquema base, lo renombra y aplica los
    /// ajustes AC/DC indicados, dejándolo activo al final.
    /// </summary>
    Task<CommandResult> CreateCustomPowerPlanAsync(string name, string baseSchemeGuid, IReadOnlyList<PowerPlanTuning> tunings);
}
