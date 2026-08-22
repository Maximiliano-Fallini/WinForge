using System.Text.Json;
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

    // "Logs de desarrollo" (ajuste logging.developerLogs): cuando está apagado no se
    // escribe nada a archivo, para no generar app.log en segundo plano. Se lee el
    // settings.json directamente (sin depender de ISettingsService, que a su vez
    // depende de este servicio) y se puede cambiar en vivo desde la UI.
    private volatile bool _fileLoggingEnabled = ReadDeveloperLogsSetting();

    // Tope de tamaño del log: al superarlo se rota a app.log.old (pisando el anterior)
    // y se arranca uno nuevo, así el disco nunca se llena pero queda historial reciente.
    private const long MaxLogBytes = 5 * 1024 * 1024; // 5 MB

    // Archivos considerados "logs" (app.log, el rotado y el de errores no controlados).
    private static readonly string[] LogFileNames = { "app.log", "app.log.old", "errors.log" };

    public LoggingService(ILogger<LoggingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Lee el ajuste "logs de desarrollo" del settings.json al arrancar. Evita
    /// depender de ISettingsService (que depende de este servicio: ciclo de DI).
    /// </summary>
    private static bool ReadDeveloperLogsSetting()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WHPO", "settings.json");
            if (!File.Exists(settingsPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return doc.RootElement.TryGetProperty("logging.developerLogs", out var el)
                && el.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    public void SetFileLoggingEnabled(bool enabled)
    {
        _fileLoggingEnabled = enabled;
    }

    public string LogDirectory => Path.GetDirectoryName(_logPath) ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WHPO");

    /// <summary>Tamaño total en bytes de los archivos de log existentes.</summary>
    public long GetLogFilesSize()
    {
        long total = 0;
        try
        {
            var dir = Path.GetDirectoryName(_logPath)!;
            foreach (var name in LogFileNames)
            {
                var fi = new FileInfo(Path.Combine(dir, name));
                if (fi.Exists) total += fi.Length;
            }
        }
        catch { }
        return total;
    }

    /// <summary>Borra todos los archivos de log (si existen).</summary>
    public void DeleteLogFiles()
    {
        try
        {
            var dir = Path.GetDirectoryName(_logPath)!;
            foreach (var name in LogFileNames)
            {
                try { var p = Path.Combine(dir, name); if (File.Exists(p)) File.Delete(p); } catch { }
            }
        }
        catch { }
    }

    private void WriteIfEnabled(string level, string message)
    {
        if (!_fileLoggingEnabled) return;
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                RotateIfNeeded();
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(_logPath);
            if (!fi.Exists || fi.Length < MaxLogBytes) return;

            var oldPath = _logPath + ".old";
            if (File.Exists(oldPath))
                File.Delete(oldPath);
            File.Move(_logPath, oldPath);
        }
        catch { }
    }

    public void LogInfo(string message)
    {
        _logger.LogInformation(message);
        WriteIfEnabled("INFO", message);
    }

    public void LogWarning(string message)
    {
        _logger.LogWarning(message);
        WriteIfEnabled("WARN", message);
    }

    public void LogError(string message, Exception? exception = null)
    {
        if (exception != null)
        {
            _logger.LogError(exception, message);
            WriteIfEnabled("ERROR", $"{message} | {exception}");
        }
        else
        {
            _logger.LogError(message);
            WriteIfEnabled("ERROR", message);
        }
    }

    public void LogDebug(string message)
    {
        _logger.LogDebug(message);
        WriteIfEnabled("DEBUG", message);
    }
}
