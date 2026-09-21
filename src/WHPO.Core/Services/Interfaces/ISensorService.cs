using System.Collections.Generic;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Tipo de sensor: se usa para agrupar en categorías, formatear la unidad y
/// colorear el valor según el estado.
/// </summary>
public enum SensorReadingKind
{
    Temperature,
    Load,
    Power,
    Voltage,
    Fan,
    Clock,
    Other
}

/// <summary>
/// Lectura de un sensor individual con sus columnas de la grilla.
/// </summary>
/// <param name="Name">Nombre del sensor (ej. "Core #1", "CPU Package").</param>
/// <param name="Current">Valor actual (null si no hay lectura).</param>
/// <param name="Min">Mínimo acumulado por LHM (null si no hay lectura).</param>
/// <param name="Max">Máximo acumulado por LHM (null si no hay lectura).</param>
/// <param name="Average">Promedio de la sesión (calculado por el servicio).</param>
/// <param name="Unit">Unidad (ej. "°C", "%", "RPM").</param>
/// <param name="Kind">Tipo de sensor.</param>
public record SensorReadingInfo(
    string Name,
    double? Current,
    double? Min,
    double? Max,
    double? Average,
    string Unit,
    SensorReadingKind Kind);

/// <summary>
/// Categoría de sensores de un hardware (ej. "Temperatura", "Uso", "Velocidad de reloj").
/// </summary>
public record SensorCategoryInfo(string Name, List<SensorReadingInfo> Sensors);

/// <summary>
/// Tipo de hardware de un grupo de sensores: permite elegir el ícono del encabezado.
/// </summary>
public enum SensorGroupKind
{
    Cpu,
    Gpu,
    Memory,
    Motherboard,
    Storage,
    Other
}

/// <summary>
/// Grupo de sensores de un mismo hardware (ej. "Intel Core i7-13700K"), con sus
/// categorías. El nombre del grupo es el encabezado desplegable de la grilla.
/// </summary>
public record SensorGroupInfo(string Name, List<SensorCategoryInfo> Categories, SensorGroupKind Kind);

/// <summary>
/// Servicio de monitoreo de sensores basado en LibreHardwareMonitor, con una
/// instancia propia (CPU, GPU, placa, memoria, almacenamiento) separada de la que
/// usan las páginas de Núcleos/Estabilidad, para mantener los caminos calientes
/// livianos. El acceso no es thread-safe: se serializa con un lock interno.
/// </summary>
public interface ISensorService : IDisposable
{
    /// <summary>Indica si LibreHardwareMonitor pudo inicializarse (driver cargado).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Devuelve los grupos de sensores (por hardware) con sus categorías y lecturas
    /// actuales, mínimo, máximo y promedio de sesión. Vacío si el hardware no
    /// expone sensores o LHM no está disponible.
    /// </summary>
    List<SensorGroupInfo> GetSensorGroups();
}
