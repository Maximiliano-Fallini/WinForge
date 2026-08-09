using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Modos de configuracion de Windows Update disponibles en WHPO.
/// </summary>
public enum WindowsUpdateMode
{
    Default,
    Recommended,
    Disabled,
    Custom
}

/// <summary>
/// Estado detectado de las politicas de Windows Update.
/// </summary>
public record WindowsUpdatePolicyState(
    WindowsUpdateMode Mode,
    string Title,
    string Description);

/// <summary>
/// Resultado de aplicar una politica de Windows Update.
/// </summary>
public record WindowsUpdatePolicyResult(
    bool Success,
    string Message,
    string? Details = null,
    bool RestartRecommended = true);

/// <summary>
/// Gestiona las politicas locales de Windows Update.
/// </summary>
public interface IWindowsUpdateService
{
    /// <summary>Obtiene el modo de actualizaciones configurado actualmente.</summary>
    WindowsUpdatePolicyState GetCurrentPolicy();

    /// <summary>Aplica uno de los perfiles de actualizaciones de WHPO.</summary>
    Task<WindowsUpdatePolicyResult> ApplyPolicyAsync(WindowsUpdateMode mode);
}
