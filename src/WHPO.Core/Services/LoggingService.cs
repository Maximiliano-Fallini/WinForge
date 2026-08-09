using Microsoft.Extensions.Logging;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación del servicio de logging usando Microsoft.Extensions.Logging.
/// También escribe a archivo para debugging.
/// </summary>
public class LoggingService : ILoggingService
{
    private readonly ILogger<LoggingService> _logger;
    private static readonly object _fileLock = new();
    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WHPO", "app.log");

    public LoggingService(ILogger<LoggingService> logger)
    {
        _logger = logger;
    }

    private void WriteToFile(string level, string message)
    {
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public void LogInfo(string message)
    {
        _logger.LogInformation(message);
        WriteToFile("INFO", message);
    }

    public void LogWarning(string message)
    {
        _logger.LogWarning(message);
        WriteToFile("WARN", message);
    }

    public void LogError(string message, Exception? exception = null)
    {
        if (exception != null)
        {
            _logger.LogError(exception, message);
            WriteToFile("ERROR", $"{message} | {exception}");
        }
        else
        {
            _logger.LogError(message);
            WriteToFile("ERROR", message);
        }
    }

    public void LogDebug(string message)
    {
        _logger.LogDebug(message);
        WriteToFile("DEBUG", message);
    }
}
