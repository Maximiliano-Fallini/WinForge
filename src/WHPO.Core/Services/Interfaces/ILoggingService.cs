namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio de logging para registrar eventos, errores y advertencias.
/// </summary>
public interface ILoggingService
{
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
    void LogDebug(string message);
}