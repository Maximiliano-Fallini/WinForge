using System.Threading;
using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Resultado de una operación de reparación.
/// </summary>
public record RepairResult(
    bool Success,
    string Message,
    string? Details = null,
    bool RequiresAdmin = false
);

/// <summary>
/// Servicio de reparación del sistema.
/// Ejecuta herramientas como SFC, DISM, CHKDSK, etc.
/// </summary>
public interface IRepairService
{
    /// <summary>
    /// Repara la tienda de Windows y sus aplicaciones.
    /// </summary>
    Task<RepairResult> RepairStoreAsync(IProgress<string>? progress = null);

    /// <summary>
    /// Repara el perfil de usuario actual.
    /// </summary>
    Task<RepairResult> RepairUserProfileAsync();

    /// <summary>
    /// Obtiene la lista de todas las herramientas de reparación disponibles.
    /// </summary>
    List<RepairToolInfo> GetAvailableTools();
}

/// <summary>
/// Información de una herramienta de reparación.
/// </summary>
public record RepairToolInfo(
    string Id,
    string Name,
    string Description,
    string Compatibility,
    bool RequiresAdmin,
    bool IsLongRunning
);