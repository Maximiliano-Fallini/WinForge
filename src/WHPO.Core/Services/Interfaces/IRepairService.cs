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
    /// Ejecuta SFC /scannow para reparar archivos de sistema corruptos.
    /// </summary>
    Task<RepairResult> RunSFCAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ejecuta DISM /RestoreHealth para reparar la imagen de Windows.
    /// </summary>
    Task<RepairResult> RunDISMAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ejecuta CHKDSK C: /scan para verificar y reparar el disco.
    /// </summary>
    Task<RepairResult> RunCHKDSKAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Repara el almacén de componentes de Windows.
    /// </summary>
    Task<RepairResult> RepairComponentStoreAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restablece la configuración de red a valores predeterminados.
    /// </summary>
    Task<RepairResult> ResetNetworkAsync();

    /// <summary>
    /// Vacía la caché de DNS.
    /// </summary>
    Task<RepairResult> FlushDNSAsync();

    /// <summary>
    /// Repara la tienda de Windows y sus aplicaciones.
    /// </summary>
    Task<RepairResult> RepairStoreAsync();

    /// <summary>
    /// Repara el perfil de usuario actual.
    /// </summary>
    Task<RepairResult> RepairUserProfileAsync();

    /// <summary>
    /// Obtiene la lista de todas las herramientas de reparación disponibles.
    /// </summary>
    List<RepairToolInfo> GetAvailableTools();

    /// <summary>
    /// Indica si la aplicación se ejecuta con privilegios de administrador.
    /// </summary>
    bool IsRunningElevated();
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