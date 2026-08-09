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
}
