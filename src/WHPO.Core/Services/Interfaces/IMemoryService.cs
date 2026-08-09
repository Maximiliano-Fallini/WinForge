using System;
using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio para el gestor de memoria y latencia: standby list cleaner, timer resolution, limpieza automática y estadísticas.
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Obtiene las estadísticas actuales de memoria del sistema.
    /// </summary>
    MemoryStats GetMemoryStats();

    /// <summary>
    /// Obtiene el tamaño actual de la lista standby en MB.
    /// </summary>
    double GetStandbyListSizeMB();

    /// <summary>
    /// Obtiene el uso del archivo de paginación en MB.
    /// </summary>
    PageFileStats GetPageFileStats();

    /// <summary>
    /// Limpia la lista standby de memoria (libera RAM en caché).
    /// </summary>
    Task<CommandResult> CleanStandbyListAsync();

    /// <summary>
    /// Obtiene la resolución actual del temporizador del sistema en 100ns.
    /// </summary>
    int GetCurrentTimerResolution();

    /// <summary>
    /// Obtiene la resolución mínima del temporizador del sistema en 100ns.
    /// </summary>
    int GetMinimumTimerResolution();

    /// <summary>
    /// Obtiene la resolución máxima del temporizador del sistema en 100ns.
    /// </summary>
    int GetMaximumTimerResolution();

    /// <summary>
    /// Obtiene información del temporizador de rendimiento (TSC).
    /// </summary>
    PerformanceTimerInfo GetPerformanceTimerInfo();

    /// <summary>
    /// Establece la resolución del temporizador del sistema.
    /// </summary>
    /// <param name="resolution100ns">Resolución en unidades de 100ns (mínimo 5000 = 0.5ms).</param>
    Task<CommandResult> SetTimerResolutionAsync(int resolution100ns);

    /// <summary>
    /// Restablece la resolución del temporizador al valor por defecto del sistema.
    /// </summary>
    Task<CommandResult> ResetTimerResolutionAsync();

    /// <summary>
    /// Obtiene el número de solicitudes globales de resolución de temporizador activas.
    /// </summary>
    int GetGlobalTimerResolutionRequests();

    /// <summary>
    /// Inicia la limpieza automática de la lista standby con las condiciones especificadas.
    /// </summary>
    /// <param name="minStandbyMB">Tamaño mínimo de la lista standby para purgar.</param>
    /// <param name="maxFreeMB">Memoria libre máxima para purgar.</param>
    /// <param name="pollIntervalMs">Intervalo de sondeo en milisegundos.</param>
    void StartAutoCleanup(double minStandbyMB, double maxFreeMB, int pollIntervalMs);

    /// <summary>
    /// Detiene la limpieza automática de la lista standby.
    /// </summary>
    void StopAutoCleanup();

    /// <summary>
    /// Indica si la limpieza automática está activa.
    /// </summary>
    bool IsAutoCleanupActive { get; }

    /// <summary>
    /// Evento que se dispara cuando se completa una limpieza de la lista standby.
    /// </summary>
    event EventHandler<StandbyCleanupEventArgs>? StandbyCleanupCompleted;
}

/// <summary>
/// Estadísticas de memoria del sistema.
/// </summary>
public record MemoryStats(
    ulong TotalPhysicalMB,
    ulong AvailableMB,
    ulong UsedMB,
    double UsedPercent,
    double StandbyMB,
    double CachedMB,
    double FreeMB
);

/// <summary>
/// Estadísticas del archivo de paginación.
/// </summary>
public record PageFileStats(
    ulong TotalPageFileMB,
    ulong UsedPageFileMB,
    ulong FreePageFileMB,
    double UsedPercent
);

/// <summary>
/// Información del temporizador de rendimiento (TSC).
/// </summary>
public record PerformanceTimerInfo(
    string Name,
    double FrequencyMHz
);

/// <summary>
/// Argumentos del evento de limpieza de lista standby.
/// </summary>
public class StandbyCleanupEventArgs : EventArgs
{
    public double FreedMB { get; }
    public DateTime Timestamp { get; }
    public bool IsAutomatic { get; }

    public StandbyCleanupEventArgs(double freedMB, bool isAutomatic)
    {
        FreedMB = freedMB;
        IsAutomatic = isAutomatic;
        Timestamp = DateTime.Now;
    }
}