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

    /// <summary>
    /// Activa o desactiva la escritura del log a archivo (app.log). Con la opción
    /// "Logs de desarrollo" apagada no se genera ningún archivo de log.
    /// </summary>
    void SetFileLoggingEnabled(bool enabled);

    /// <summary>Carpeta donde se escriben los logs (para "Abrir carpeta de logs").</summary>
    string LogDirectory { get; }

    /// <summary>Tamaño total en bytes de los archivos de log existentes.</summary>
    long GetLogFilesSize();

    /// <summary>Borra todos los archivos de log (si existen).</summary>
    void DeleteLogFiles();
}