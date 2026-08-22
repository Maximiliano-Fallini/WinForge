namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Gestiona el inicio automatico de WHPO para el usuario actual.
/// </summary>
public interface IStartupService
{
    bool IsEnabled();

    /// <summary>Indica si el valor del registro de inicio trae el flag de minimizado.</summary>
    bool HasStartMinimizedFlag();

    /// <summary>
    /// Activa/desactiva el inicio con Windows. Con <paramref name="startMinimized"/>
    /// se agrega el flag al valor del registro para que la app arranque minimizada
    /// solo cuando Windows la lanza al iniciar sesión.
    /// </summary>
    StartupOperationResult SetEnabled(bool enabled, bool startMinimized = false);
}

public record StartupOperationResult(bool Success, string Message);
