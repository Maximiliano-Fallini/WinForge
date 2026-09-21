using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación del monitoreo de sensores con LibreHardwareMonitor.
///
/// Usa una instancia propia de Computer con CPU, GPU, placa madre, memoria y
/// almacenamiento habilitados — separada de la instancia liviana (CPU+GPU) que
/// usan Núcleos/Estabilidad — para no encarecer los caminos calientes. El driver
/// (WinRing0) es compartido por el runtime de LHM, así que ambas instancias
/// conviven sin problema.
///
/// LibreHardwareMonitor no es thread-safe: todo acceso se serializa con un lock.
/// Los promedios son de sesión (acumulados desde que el servicio monitorea).
/// </summary>
public class SensorService : ISensorService
{
    private readonly ILoggingService _loggingService;
    private readonly object _lock = new();
    private Computer? _computer;
    private bool _initFailed;
    private DateTime _lastAttempt = DateTime.MinValue;
    private bool _readErrorLogged;

    // Promedios de sesión por sensor (clave = Identifier del sensor).
    private readonly Dictionary<string, (double Sum, long Count)> _averages = new();

    public SensorService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _computer != null;
        }
    }

    public List<SensorGroupInfo> GetSensorGroups()
    {
        EnsureInitialized();

        var groups = new List<SensorGroupInfo>();
        if (_computer == null) return groups;

        try
        {
            lock (_lock)
            {
                foreach (var hardware in _computer.Hardware)
                {
                    try
                    {
                        hardware.Update();
                    }
                    catch
                    {
                        // Un hardware puntual puede fallar sin tirar abajo el resto.
                        continue;
                    }

                    var categories = ReadCategories(hardware);

                    // Sub-hardware (ej. sub-dispositivos de una GPU): se suman a las
                    // categorías del padre para no fragmentar la grilla.
                    foreach (var sub in hardware.SubHardware)
                    {
                        try
                        {
                            sub.Update();
                        }
                        catch
                        {
                            continue;
                        }
                        MergeCategories(categories, ReadCategories(sub));
                    }

                    if (categories.Count > 0)
                    {
                        var kind = KindFor(hardware.HardwareType);
                        var displayName = kind == SensorGroupKind.Memory ? "System Memory" : hardware.Name;
                        groups.Add(new SensorGroupInfo(displayName, categories, kind));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_readErrorLogged)
            {
                _readErrorLogged = true;
                _loggingService.LogWarning($"Error leyendo sensores via LibreHardwareMonitor: {ex.Message}");
            }
        }

        return groups;
    }

    private List<SensorCategoryInfo> ReadCategories(IHardware hardware)
    {
        var categories = new List<SensorCategoryInfo>();

        foreach (var sensor in hardware.Sensors)
        {
            var categoryName = CategoryFor(sensor);
            var category = categories.FirstOrDefault(c => c.Name == categoryName);
            if (category == null)
            {
                category = new SensorCategoryInfo(categoryName, new List<SensorReadingInfo>());
                categories.Add(category);
            }

            var unit = UnitFor(sensor.SensorType);
            var value = sensor.Value;
            double? average = null;

            if (value.HasValue)
            {
                var key = sensor.Identifier?.ToString() ?? sensor.Name;
                if (_averages.TryGetValue(key, out var acc))
                {
                    _averages[key] = (acc.Sum + value.Value, acc.Count + 1);
                    average = _averages[key].Sum / _averages[key].Count;
                }
                else
                {
                    _averages[key] = (value.Value, 1);
                    average = value.Value;
                }
            }

            category.Sensors.Add(new SensorReadingInfo(
                Name: DisplayNameFor(sensor),
                Current: value.HasValue ? (double)value.Value : (double?)null,
                Min: sensor.Min.HasValue ? (double)sensor.Min.Value : (double?)null,
                Max: sensor.Max.HasValue ? (double)sensor.Max.Value : (double?)null,
                Average: average,
                Unit: unit,
                Kind: KindFor(sensor.SensorType)));
        }

        return categories;
    }

    private static void MergeCategories(List<SensorCategoryInfo> target, List<SensorCategoryInfo> source)
    {
        foreach (var src in source)
        {
            var existing = target.FirstOrDefault(c => c.Name == src.Name);
            if (existing != null)
                existing.Sensors.AddRange(src.Sensors);
            else
                target.Add(src);
        }
    }

    // LHM nombra con genéricos "Temperature #N" los canales del chip SuperIO de la
    // placa (en el BIOS suelen ser CPU/System/VRM…). Sin más información, esos canales
    // son lo más parecido a la temperatura de los núcleos que expone el hardware: la
    // grilla muestra "Core #N" en vez del nombre genérico, igual que los sensores de
    // núcleo que ya nombra LHM ("Core #1", "CPU Core #1", ...).
    private static readonly Regex GenericTemperatureRegex = new(
        @"^Temperature\s*#\s*(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string DisplayNameFor(ISensor sensor)
    {
        if (sensor.SensorType == SensorType.Temperature)
        {
            var m = GenericTemperatureRegex.Match(sensor.Name);
            if (m.Success)
                return $"Core #{m.Groups[1].Value}";
        }
        return sensor.Name;
    }

    // Agrupación en categorías: los sensores de temperatura de Intel que miden el
    // margen al máximo térmico ("Distance to Tjmax") van a su propia categoría.
    private static string CategoryFor(ISensor sensor) => sensor.SensorType switch
    {
        SensorType.Temperature when sensor.Name.Contains("tjmax", StringComparison.OrdinalIgnoreCase) => "Distancia a TjMax",
        SensorType.Temperature => "Temperatura",
        SensorType.Load => "Uso",
        SensorType.Clock => "Velocidad de reloj",
        SensorType.Power => "Potencia",
        SensorType.Voltage => "Voltaje",
        SensorType.Fan => "Ventiladores",
        SensorType.Data or SensorType.SmallData => "Datos",
        SensorType.Throughput => "Transferencia",
        _ => "Otros"
    };

    // LHM 0.9.4 no expone la unidad del sensor: se deriva del tipo.
    private static string UnitFor(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Power => "W",
        SensorType.Voltage => "V",
        SensorType.Fan => "RPM",
        SensorType.Clock => "MHz",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "MB/s",
        _ => ""
    };

    private static SensorGroupKind KindFor(HardwareType type) => type switch
    {
        HardwareType.Cpu => SensorGroupKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => SensorGroupKind.Gpu,
        HardwareType.Memory => SensorGroupKind.Memory,
        HardwareType.Motherboard => SensorGroupKind.Motherboard,
        HardwareType.Storage => SensorGroupKind.Storage,
        _ => SensorGroupKind.Other
    };

    private static SensorReadingKind KindFor(SensorType type) => type switch
    {
        SensorType.Temperature => SensorReadingKind.Temperature,
        SensorType.Load => SensorReadingKind.Load,
        SensorType.Power => SensorReadingKind.Power,
        SensorType.Voltage => SensorReadingKind.Voltage,
        SensorType.Fan => SensorReadingKind.Fan,
        SensorType.Clock => SensorReadingKind.Clock,
        _ => SensorReadingKind.Other
    };

    private void EnsureInitialized()
    {
        if (_computer != null) return;
        if (_initFailed && (DateTime.Now - _lastAttempt).TotalSeconds < 10) return;

        lock (_lock)
        {
            if (_computer != null) return;
            if (_initFailed && (DateTime.Now - _lastAttempt).TotalSeconds < 10) return;
            _lastAttempt = DateTime.Now;

            try
            {
                var computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsMemoryEnabled = true,
                    IsStorageEnabled = true,
                    IsControllerEnabled = false
                };
                computer.Open();
                _computer = computer;
                _initFailed = false;
                _loggingService.LogInfo("SensorService: LibreHardwareMonitor inicializado (CPU, GPU, placa, memoria, disco)");
            }
            catch (Exception ex)
            {
                _initFailed = true;
                _loggingService.LogWarning($"SensorService: LibreHardwareMonitor no disponible: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (_computer != null)
        {
            try { _computer.Close(); } catch { }
            _computer = null;
        }
        GC.SuppressFinalize(this);
    }
}
