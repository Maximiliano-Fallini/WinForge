namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Gestiona el inicio automatico de WHPO para el usuario actual.
/// </summary>
public interface IStartupService
{
    bool IsEnabled();
    StartupOperationResult SetEnabled(bool enabled);
}

public record StartupOperationResult(bool Success, string Message);
