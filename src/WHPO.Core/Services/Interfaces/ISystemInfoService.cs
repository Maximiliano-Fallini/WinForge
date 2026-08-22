using System.Collections.Generic;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio para obtener información real del sistema (hardware).
/// </summary>
public interface ISystemInfoService
{
    /// <summary>
    /// Obtiene información de la CPU.
    /// </summary>
    CpuInfo GetCpuInfo();

    /// <summary>
    /// Obtiene información de la memoria RAM.
    /// </summary>
    MemoryInfo GetMemoryInfo();

    /// <summary>
    /// Obtiene el modo de canal y la velocidad de la memoria RAM (módulos físicos).
    /// </summary>
    MemoryModuleInfo GetMemoryModuleInfo();

    /// <summary>
    /// Obtiene información del disco.
    /// </summary>
    List<DiskInfo> GetDiskInfo();

    /// <summary>
    /// Obtiene información de todas las GPUs.
    /// </summary>
    List<GpuInfo> GetGpuInfo();

    /// <summary>
    /// Obtiene información de red.
    /// </summary>
    List<NetworkAdapterInfo> GetNetworkInfo();

    /// <summary>
    /// Obtiene información de la placa base (motherboard).
    /// </summary>
    BoardInfo GetBoardInfo();

    /// <summary>
    /// Obtiene información del BIOS.
    /// </summary>
    BiosInfo GetBiosInfo();

    /// <summary>
    /// Obtiene información del sistema operativo.
    /// </summary>
    OsInfo GetOsInfo();

    /// <summary>
    /// Obtiene el estado de las funciones de seguridad del firmware:
    /// TPM (presente/activado y versión de especificación), Secure Boot e IOMMU.
    /// </summary>
    SecurityFeatures GetSecurityFeatures();

    /// <summary>
    /// Inicia la monitorización periódica.
    /// </summary>
    void StartMonitoring(int intervalMs = 1000);

    /// <summary>
    /// Detiene la monitorización.
    /// </summary>
    void StopMonitoring();

    /// <summary>
    /// Obtiene las métricas en caché más recientes.
    /// </summary>
    Task<SystemMetrics> GetCachedMetricsAsync();

    /// <summary>
    /// Obtiene la temperatura actual de la CPU en °C (con caché interna de 5 s).
    /// </summary>
    double GetCpuTemperature();

    /// <summary>
    /// Temperatura de CPU sin la caché de 5 s (solo 1 s): para gráficos en vivo
    /// que muestrean cada segundo, donde la caché larga produce saltos.
    /// </summary>
    double GetCpuTemperatureFresh();

    /// <summary>
    /// Obtiene el consumo actual de la CPU en watts (sensor de potencia de
    /// LibreHardwareMonitor, con caché interna de 5 s). Devuelve 0 si el hardware
    /// no expone sensor de potencia.
    /// </summary>
    double GetCpuPower();

    /// <summary>
    /// Obtiene el conjunto de instrucciones detectado del procesador (SSE, SSE2,
    /// AVX, AVX2, FMA, AVX-512, ...). Resultado cacheado: la primera llamada
    /// ejecuta la detección y las siguientes devuelven el mismo string.
    /// </summary>
    string GetCpuInstructionSet();

    /// <summary>
    /// Obtiene el porcentaje de uso actual de cada procesador lógico (por núcleo).
    /// El índice de la matriz corresponde a cada procesador lógico.
    /// </summary>
    double[] GetCpuCoreUsages();

    /// <summary>
    /// Obtiene la temperatura actual en °C de cada núcleo físico disponible.
    /// Puede devolver una lista vacía si el hardware no expone sensores por núcleo.
    /// </summary>
    double[] GetCpuCoreTemperatures();

    /// <summary>
    /// Obtiene la frecuencia real actual de la CPU en MHz (contador de rendimiento,
    /// la misma fuente que usa el Administrador de tareas).
    /// </summary>
    double GetCpuFrequency();

    /// <summary>
    /// Indica qué procesadores lógicos están estacionados (core parking).
    /// true = estacionado, false = activo. Devuelve null si el contador no está disponible.
    /// Un núcleo con 0% de uso NO es necesariamente estacionado: esta información
    /// proviene del contador "Parking Status" del sistema.
    /// </summary>
    bool[]? GetCpuCoreParkedStatus();

    /// <summary>
    /// Obtiene la cantidad de procesadores lógicos del sistema.
    /// </summary>
    int GetLogicalProcessorCount();

    /// <summary>
    /// Obtiene la temperatura actual de la primera GPU con sensor disponible, en °C.
    /// </summary>
    double GetGpuTemperature();

    /// <summary>
    /// Obtiene el consumo actual de la GPU en watts (sensor de potencia de
    /// LibreHardwareMonitor). Devuelve 0 si el hardware no expone el sensor.
    /// </summary>
    double GetGpuPower();

    /// <summary>
    /// Obtiene la frecuencia de núcleo actual de la GPU en MHz (sensor de reloj de
    /// LibreHardwareMonitor). Devuelve 0 si el hardware no expone el sensor.
    /// </summary>
    double GetGpuClockMHz();

    /// <summary>
    /// Evento que se dispara cuando hay nuevos datos de métricas.
    /// </summary>
    event Action<SystemMetrics> OnMetricsUpdated;
}

/// <summary>
/// Información de la CPU.
/// </summary>
public record CpuInfo(
    string Name,
    int LogicalProcessors,
    int PhysicalCores,
    double CurrentUsagePercent,
    double CurrentFrequencyMHz,
    double MaxFrequencyMHz,
    double TemperatureCelsius,
    int L2CacheKB,
    int L3CacheKB,
    bool VirtualizationEnabled,
    string Architecture,
    bool SmtEnabled,
    string InstructionSet,
    double CoreVoltageVID,
    double CurrentFreqMHz,
    double BusSpeedMHz,
    string CpuId,
    string Stepping,
    string Model,
    string Family
);

/// <summary>
/// Información de la memoria RAM.
/// </summary>
public record MemoryInfo(
    long TotalBytes,
    long AvailableBytes,
    long UsedBytes,
    double UsagePercent,
    long CachedBytes,
    long CommittedBytes
);

/// <summary>
/// Modo de canal, velocidad y configuración física (cantidad × tamaño) de los
/// módulos de memoria. <see cref="ModuleSizeBytes"/> es el tamaño por módulo
/// (el máximo si los módulos son de distinta capacidad).
/// </summary>
public record MemoryModuleInfo(
    string ChannelMode,
    int SpeedMHz,
    int ModuleCount,
    long ModuleSizeBytes
);

/// <summary>
/// Información de disco.
/// </summary>
public record DiskInfo(
    string DeviceId,
    string Model,
    string MediaType,
    long TotalSizeBytes,
    long FreeSpaceBytes,
    double UsagePercent,
    double ReadSpeedMBps,
    double WriteSpeedMBps,
    int TemperatureCelsius,
    bool IsHealthy
);

/// <summary>
/// Información de la GPU.
/// </summary>
public record GpuInfo(
    string Name,
    long DedicatedMemoryBytes,
    long SharedMemoryBytes,
    double UsagePercent,
    double TemperatureCelsius,
    double CoreClockMHz,
    double MemoryClockMHz,
    string DriverVersion
);

/// <summary>
/// Información de adaptador de red.
/// </summary>
public record NetworkAdapterInfo(
    string Name,
    string Description,
    string MacAddress,
    string IpAddress,
    double SpeedMbps,
    double ReceiveSpeedMBps,
    double TransmitSpeedMBps,
    bool IsConnected,
    string ConnectionType,
    string NetConnectionId = ""   // nombre de la interfaz en Conexiones de red ("Ethernet", "Wi-Fi")
);

/// <summary>
/// Métricas del sistema actualizadas periódicamente.
/// </summary>
public record SystemMetrics(
    double CpuUsagePercent,
    double CpuFrequencyMHz,
    double CpuTemperatureCelsius,
    long MemoryUsedBytes,
    double MemoryUsagePercent,
    List<DiskMetrics> Disks,
    GpuMetrics Gpu,
    List<NetworkMetrics> Network
);

/// <summary>
/// Métricas de disco en tiempo real.
/// </summary>
public record DiskMetrics(
    string DeviceId,
    double ReadSpeedMBps,
    double WriteSpeedMBps,
    double UsagePercent,
    int TemperatureCelsius
);

/// <summary>
/// Métricas de GPU en tiempo real.
/// </summary>
public record GpuMetrics(
    double UsagePercent,
    double TemperatureCelsius,
    double CoreClockMHz,
    double MemoryClockMHz,
    long DedicatedMemoryUsedBytes
);

/// <summary>
/// Métricas de red en tiempo real.
/// </summary>
public record NetworkMetrics(
    string AdapterName,
    double ReceiveSpeedMBps,
    double TransmitSpeedMBps,
    bool IsConnected
);

/// <summary>
/// Información de la placa base (motherboard).
/// </summary>
public record BoardInfo(
    string Manufacturer,
    string Product,
    string Version,
    string SerialNumber
);

/// <summary>
/// Información del BIOS.
/// </summary>
public record BiosInfo(
    string Manufacturer,
    string Version,
    string ReleaseDate,
    string SMBIOSBIOSVersion
);

/// <summary>
/// Información del sistema operativo.
/// </summary>
public record OsInfo(
    string Name,
    string Version,
    string BuildNumber,
    string Architecture,
    string InstallDate,
    string LastBootTime,
    string ComputerName
);

/// <summary>
/// Estado de las funciones de seguridad del firmware (TPM, Secure Boot, IOMMU).
/// UefiFirmware es true cuando el sistema arrancó en UEFI (Secure Boot solo aplica ahí).
/// </summary>
public record SecurityFeatures(
    bool TpmPresent,
    bool TpmEnabled,
    string TpmSpecVersion,
    bool UefiFirmware,
    bool SecureBootEnabled,
    bool IommuPresent
);
