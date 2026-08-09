using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación del servicio de información del sistema usando WMI, Performance Counters y APIs nativas.
/// </summary>
public class SystemInfoService : ISystemInfoService, IDisposable
{
    private readonly ILoggingService _loggingService;
    private Timer? _monitoringTimer;
    private readonly object _lock = new();
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _cpuFreqCounter;
    private readonly List<PerformanceCounter> _diskReadCounters = new();
    private readonly List<PerformanceCounter> _diskWriteCounters = new();
    private readonly List<PerformanceCounter> _networkReceiveCounters = new();
    private readonly List<PerformanceCounter> _networkSendCounters = new();
    private readonly List<PerformanceCounter> _gpuCounters = new();
    private PerformanceCounter? _gpuTempCounter;
    private bool _diskCountersInitialized;
    private bool _networkCountersInitialized;
    private bool _gpuCountersInitialized;

    // Contadores de uso por procesador lógico (por núcleo)
    private readonly List<PerformanceCounter> _cpuCoreCounters = new();
    private bool _cpuCoreCountersInitialized;

    // Estado de core parking por procesador lógico (contador "Parking Status")
    private readonly List<PerformanceCounter> _cpuParkingCounters = new();
    private bool _cpuParkingInitialized;
    private bool _cpuParkingUnavailable;

    // Sensores de temperatura por núcleo: solo los "Core #N" (excluye "Core (Tctl/Tdie)")
    private static readonly System.Text.RegularExpressions.Regex CoreSensorRegex =
        new(@"^Core\s*#\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public event Action<SystemMetrics>? OnMetricsUpdated;

    private bool _initialized;
    private readonly object _initLock = new();

    // ===== Detección de instrucciones vía API nativa de Windows =====
    // GetLogicalProcessorInformationEx con RelationProcessorExtended expone los
    // 128 bits de ProcessorFeatures del procesador (bits PF_* de winnt.h).
    // Es 100% nativa: no depende del JIT, de intrinsics ni de la arquitectura del proceso.
    private const int RelationProcessorExtended = 6;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref int returnedLength);

    public SystemInfoService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _cpuCounter.NextValue();

                // Frecuencia real de la CPU: el contador "Actual Frequency" (la misma
                // fuente que usa el Administrador de tareas). OJO: en AMD, "Processor
                // Frequency" devuelve la frecuencia nominal fija (base), mientras que
                // "Actual Frequency" reporta la frecuencia dinámica real (boost).
                try
                {
                    _cpuFreqCounter = new PerformanceCounter("Processor Information", "Actual Frequency", "_Total", true);
                    _cpuFreqCounter.NextValue();
                    _loggingService.LogInfo("Contador de frecuencia CPU inicializado (Actual Frequency)");
                }
                catch (Exception freqEx)
                {
                    // Fallback: "Processor Frequency" (nominal fijo en algunos AMD, pero
                    // es lo mejor disponible en sistemas antiguos)
                    try
                    {
                        _cpuFreqCounter = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true);
                        _cpuFreqCounter.NextValue();
                        _loggingService.LogInfo("Contador de frecuencia CPU inicializado (Processor Frequency, fallback)");
                    }
                    catch (Exception freqEx2)
                    {
                        _cpuFreqCounter = null;
                        _loggingService.LogWarning($"Contador de frecuencia no disponible, se usará WMI: {freqEx.Message} / {freqEx2.Message}");
                    }
                }

                Thread.Sleep(200);

                _initialized = true;
                _loggingService.LogInfo("Contador CPU inicializado correctamente");
            }
            catch (Exception ex)
            {
                _loggingService.LogError("Error inicializando contador CPU", ex);
                _initialized = true;
            }
        }
    }

    private void EnsureDiskCountersInitialized()
    {
        if (_diskCountersInitialized) return;
        lock (_initLock)
        {
            if (_diskCountersInitialized) return;
            EnsureInitialized();
            try
            {
                InitializeDiskCounters();
                _diskCountersInitialized = true;
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"No se pudieron inicializar los contadores de disco: {ex.Message}");
                _diskCountersInitialized = true;
            }
        }
    }

    private void EnsureNetworkCountersInitialized()
    {
        if (_networkCountersInitialized) return;
        lock (_initLock)
        {
            if (_networkCountersInitialized) return;
            EnsureInitialized();
            try
            {
                InitializeNetworkCounters();
                _networkCountersInitialized = true;
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"No se pudieron inicializar los contadores de red: {ex.Message}");
                _networkCountersInitialized = true;
            }
        }
    }

    private void EnsureGpuCountersInitialized()
    {
        if (_gpuCountersInitialized) return;
        lock (_initLock)
        {
            if (_gpuCountersInitialized) return;
            EnsureInitialized();
            try
            {
                InitializeGpuCounters();
                _gpuCountersInitialized = true;
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"Error inicializando contadores GPU (no crítico): {ex.Message}");
                _gpuCountersInitialized = true;
            }
        }
    }

    private void InitializeDiskCounters()
    {
        try
        {
            var diskCategory = new PerformanceCounterCategory("PhysicalDisk");
            var diskInstances = diskCategory.GetInstanceNames();
            foreach (var instance in diskInstances)
            {
                if (instance == "_Total") continue;
                _diskReadCounters.Add(new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instance, true));
                _diskWriteCounters.Add(new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instance, true));
            }
            _loggingService.LogInfo($"Contadores de disco inicializados: {_diskReadCounters.Count} instancias");
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudieron inicializar contadores de disco: {ex.Message}");
        }
    }

    private void InitializeNetworkCounters()
    {
        try
        {
            var netCategory = new PerformanceCounterCategory("Network Interface");
            var netInstances = netCategory.GetInstanceNames();
            foreach (var instance in netInstances)
            {
                _networkReceiveCounters.Add(new PerformanceCounter("Network Interface", "Bytes Received/sec", instance, true));
                _networkSendCounters.Add(new PerformanceCounter("Network Interface", "Bytes Sent/sec", instance, true));
            }
            _loggingService.LogInfo($"Contadores de red inicializados: {_networkReceiveCounters.Count} instancias");
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudieron inicializar contadores de red: {ex.Message}");
        }
    }

    private void InitializeGpuCounters()
    {
        try
        {
            // Windows 10/11 usa "GPU Engine" en vez de "GPU" para los contadores de rendimiento de GPU
            _loggingService.LogInfo("Intentando acceder a categoría GPU Engine...");
            var gpuCategory = new PerformanceCounterCategory("GPU Engine");
            _loggingService.LogInfo("Categoría GPU Engine encontrada");
            var instances = gpuCategory.GetInstanceNames();
            _loggingService.LogInfo($"Instancias de GPU Engine: {instances.Length}");
            
            if (instances.Length > 0)
            {
                // Buscar instancias con engtype_3d que representan el uso de GPU 3D
                var engineInstances = instances.Where(i => i.Contains("engtype_3d")).ToArray();
                if (engineInstances.Length == 0)
                {
                    engineInstances = instances;
                }
                
                _loggingService.LogInfo($"Instancias de GPU 3D encontradas: {engineInstances.Length}");
                
                try
                {
                    // El uso total de GPU es la suma de todas las instancias 3D
                    foreach (var instance in engineInstances)
                    {
                        try
                        {
                            var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                            counter.NextValue();
                            _gpuCounters.Add(counter);
                        }
                        catch { }
                    }
                    Thread.Sleep(100);
                    _loggingService.LogInfo($"Contadores GPU Engine inicializados: {_gpuCounters.Count} instancias 3D");
                }
                catch (Exception ex)
                {
                    _loggingService.LogError($"Error creando contadores GPU Engine: {ex.Message}", ex);
                }
                
                // Temperatura de GPU no está disponible vía Performance Counters
                _loggingService.LogInfo("Temperatura GPU no disponible via Performance Counters");
            }
            else
            {
                _loggingService.LogWarning("No hay instancias de GPU Engine disponibles en Performance Counters");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"No se pudo inicializar contador de GPU: {ex.Message}", ex);
        }
    }

    public CpuInfo GetCpuInfo()
    {
        EnsureInitialized();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                var name = obj["Name"]?.ToString()?.Trim() ?? "Unknown";
                var logicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? 0);
                var physicalCores = Convert.ToInt32(obj["NumberOfCores"] ?? 0);
                var maxFreq = Convert.ToDouble(obj["MaxClockSpeed"] ?? 0);

                int l2CacheKB = 0, l3CacheKB = 0;
                if (obj["L2CacheSize"] != null) l2CacheKB = Convert.ToInt32(obj["L2CacheSize"]);
                if (obj["L3CacheSize"] != null) l3CacheKB = Convert.ToInt32(obj["L3CacheSize"]);

                var dataWidth = Convert.ToInt32(obj["DataWidth"] ?? 0);
                var architecture = dataWidth == 64 ? "x64" : "x86";

                bool virtualizationEnabled = false;
                if (obj["VirtualizationFirmwareEnabled"] != null)
                    virtualizationEnabled = Convert.ToBoolean(obj["VirtualizationFirmwareEnabled"]);

                bool smtEnabled = logicalProcessors > physicalCores && physicalCores > 0;

                string cpuId = "", stepping = "", model = "", family = "";
                try
                {
                    using var idSearcher = new ManagementObjectSearcher("SELECT ProcessorId, Stepping, Model, Family FROM Win32_Processor");
                    foreach (ManagementObject idObj in idSearcher.Get())
                    {
                        cpuId = idObj["ProcessorId"]?.ToString() ?? "";
                        stepping = idObj["Stepping"]?.ToString() ?? "";
                        model = idObj["Model"]?.ToString() ?? "";
                        family = idObj["Family"]?.ToString() ?? "";
                    }
                }
                catch { }

                string instructionSet = DetectCpuInstructionFlags();
                double busSpeedMHz = 100;
                double coreVoltageVID = 0;

                var currentFreq = GetCurrentCpuFrequency();
                var cpuTemp = GetCpuTemperature();

                return new CpuInfo(
                    Name: name,
                    LogicalProcessors: logicalProcessors,
                    PhysicalCores: physicalCores,
                    CurrentUsagePercent: _cpuCounter?.NextValue() ?? 0,
                    CurrentFrequencyMHz: currentFreq,
                    MaxFrequencyMHz: maxFreq,
                    TemperatureCelsius: cpuTemp,
                    L2CacheKB: l2CacheKB,
                    L3CacheKB: l3CacheKB,
                    VirtualizationEnabled: virtualizationEnabled,
                    Architecture: architecture,
                    SmtEnabled: smtEnabled,
                    InstructionSet: instructionSet,
                    CoreVoltageVID: coreVoltageVID,
                    CurrentFreqMHz: currentFreq,
                    BusSpeedMHz: busSpeedMHz,
                    CpuId: cpuId,
                    Stepping: stepping,
                    Model: model,
                    Family: family
                );
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo info de CPU", ex);
        }

        return new CpuInfo("Unknown", 0, 0, 0, 0, 0, 0, 0, 0, false, "Unknown", false, "Unknown", 0, 0, 0, "", "", "", "");
    }

    /// <summary>
    /// Detecta el conjunto de instrucciones del procesador mediante un enfoque multi-capa:
    /// 1. CPUID vía hardware intrinsics (System.Runtime.Intrinsics.X86) - el más completo.
    /// 2. API nativa GetLogicalProcessorInformationEx (ProcessorFeatures de 128 bits).
    /// 3. WMI como último respaldo.
    /// Esto garantiza que la UI muestre las instrucciones reales y no solo "x86-64".
    /// </summary>
    private string DetectCpuInstructionFlags()
    {
        var flags = new List<string>();

        // ----- Capa 1: CPUID vía intrinsics (si el JIT lo soporta) -----
        bool cpuidSucceeded = false;
        try
        {
            if (X86Base.IsSupported)
            {
                flags.AddRange(DetectCpuidFlags());
                cpuidSucceeded = flags.Count > 0;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Error detectando instrucciones vía CPUID intrinsics: {ex.Message}");
        }

        // ----- Capa 2: API nativa de Windows (ProcessorFeatures) -----
        // Se ejecuta SIEMPRE para complementar; no depende del JIT ni de intrinsics.
        var nativeFlags = DetectNativeWindowsFlags();
        foreach (var f in nativeFlags)
        {
            if (!flags.Contains(f))
                flags.Add(f);
        }

        // ----- Capa 3: WMI como respaldo -----
        bool useWmi = !cpuidSucceeded && nativeFlags.Count == 0;
        if (useWmi)
        {
            _loggingService.LogWarning("CPUID y API nativa no detectaron instrucciones, usando fallback WMI");
            flags.AddRange(DetectInstructionsViaWmi());
        }

        // Asegurar x86-64 al menos (long mode es un requisito para procesos de 64 bits)
        if (!flags.Contains("x86-64") && (Environment.Is64BitOperatingSystem || Environment.Is64BitProcess))
        {
            flags.Add("x86-64");
        }
        if (flags.Contains("x86-64"))
        {
            flags.Remove("x64");
        }

        _loggingService.LogInfo($"Instrucciones CPU detectadas: {string.Join(", ", flags.Distinct())}");
        return string.Join(", ", flags.Distinct());
    }

    /// <summary>
    /// Detección completa vía CPUID usando System.Runtime.Intrinsics.X86.X86Base.CpuId().
    /// </summary>
    private List<string> DetectCpuidFlags()
    {
        var flags = new List<string>();

        // CPUID leaf 1: Feature information
        (int _, int _, int ecx1, int edx1) = X86Base.CpuId(1, 0);
        uint edx1u = (uint)edx1, ecx1u = (uint)ecx1;

        // EDX bits (legacy features)
        if ((edx1u & (1u << 23)) != 0) flags.Add("MMX");
        if ((edx1u & (1u << 25)) != 0) flags.Add("SSE");
        if ((edx1u & (1u << 26)) != 0) flags.Add("SSE2");

        // ECX bits (modern features)
        if ((ecx1u & (1u << 0)) != 0) flags.Add("SSE3");
        if ((ecx1u & (1u << 1)) != 0) flags.Add("PCLMULQDQ");
        if ((ecx1u & (1u << 9)) != 0) flags.Add("SSSE3");
        if ((ecx1u & (1u << 12)) != 0) flags.Add("FMA");
        if ((ecx1u & (1u << 13)) != 0) flags.Add("CMPXCHG16B");
        if ((ecx1u & (1u << 19)) != 0) flags.Add("SSE4.1");
        if ((ecx1u & (1u << 20)) != 0) flags.Add("SSE4.2");
        if ((ecx1u & (1u << 23)) != 0) flags.Add("POPCNT");
        if ((ecx1u & (1u << 25)) != 0) flags.Add("AES");
        if ((ecx1u & (1u << 28)) != 0) flags.Add("AVX");
        if ((ecx1u & (1u << 29)) != 0) flags.Add("F16C");
        if ((ecx1u & (1u << 30)) != 0) flags.Add("RDRAND");

        // CPUID leaf 7 subleaf 0: Extended features (EBX, ECX, EDX)
        (int _, int ebx7, int ecx7, int edx7) = X86Base.CpuId(7, 0);
        uint ebx7u = (uint)ebx7, ecx7u = (uint)ecx7, edx7u = (uint)edx7;

        // EBX
        if ((ebx7u & (1u << 3)) != 0) flags.Add("BMI1");
        if ((ebx7u & (1u << 5)) != 0) flags.Add("AVX2");
        if ((ebx7u & (1u << 8)) != 0) flags.Add("BMI2");
        if ((ebx7u & (1u << 11)) != 0) flags.Add("RTM");
        if ((ebx7u & (1u << 16)) != 0) flags.Add("AVX-512F");
        if ((ebx7u & (1u << 17)) != 0) flags.Add("AVX-512DQ");
        if ((ebx7u & (1u << 18)) != 0) flags.Add("RDSEED");
        if ((ebx7u & (1u << 19)) != 0) flags.Add("ADX");
        if ((ebx7u & (1u << 25)) != 0) flags.Add("AVX-512PF");
        if ((ebx7u & (1u << 26)) != 0) flags.Add("AVX-512ER");
        if ((ebx7u & (1u << 27)) != 0) flags.Add("AVX-512CD");
        if ((ebx7u & (1u << 28)) != 0) flags.Add("SHA");
        if ((ebx7u & (1u << 29)) != 0) flags.Add("AVX-512BW");
        if ((ebx7u & (1u << 30)) != 0) flags.Add("AVX-512VL");

        // ECX
        if ((ecx7u & (1u << 1)) != 0) flags.Add("AVX-512VBMI");
        if ((ecx7u & (1u << 6)) != 0) flags.Add("AVX-512VBMI2");
        if ((ecx7u & (1u << 10)) != 0) flags.Add("AVX-512VPOPCNTDQ");
        if ((ecx7u & (1u << 11)) != 0) flags.Add("GFNI");
        if ((ecx7u & (1u << 12)) != 0) flags.Add("VAES");
        if ((ecx7u & (1u << 13)) != 0) flags.Add("VPCLMULQDQ");
        if ((ecx7u & (1u << 14)) != 0) flags.Add("AVX-512VNNI");
        if ((ecx7u & (1u << 16)) != 0) flags.Add("AVX-512BITALG");
        if ((ecx7u & (1u << 22)) != 0) flags.Add("AMX-BF16");
        if ((ecx7u & (1u << 24)) != 0) flags.Add("AMX-TILE");
        if ((ecx7u & (1u << 25)) != 0) flags.Add("AMX-INT8");

        // EDX
        if ((edx7u & (1u << 4)) != 0) flags.Add("AVX-5124VNNIW");
        if ((edx7u & (1u << 5)) != 0) flags.Add("AVX-5124FMAPS");
        if ((edx7u & (1u << 14)) != 0) flags.Add("AVX-512VP2INTERSECT");

        // CPUID leaf 0x80000001: Extended processor features
        (int _, int _, int ecx8, int edx8) = X86Base.CpuId(unchecked((int)0x80000001), 0);
        uint ecx8u = (uint)ecx8, edx8u = (uint)edx8;

        // ECX bits
        if ((ecx8u & (1u << 5)) != 0) flags.Add("LZCNT");
        if ((ecx8u & (1u << 6)) != 0) flags.Add("SSE4A");      // AMD
        if ((ecx8u & (1u << 8)) != 0) flags.Add("PREFETCHW");
        if ((ecx8u & (1u << 9)) != 0) flags.Add("SSE5");       // AMD XOP
        if ((ecx8u & (1u << 11)) != 0) flags.Add("XOP");       // AMD
        if ((ecx8u & (1u << 12)) != 0) flags.Add("FMA4");      // AMD

        // EDX bits
        if ((edx8u & (1u << 20)) != 0) flags.Add("NX");
        if ((edx8u & (1u << 26)) != 0) flags.Add("PAGE1GB");   // AMD: 1GB pages
        if ((edx8u & (1u << 29)) != 0) flags.Add("x86-64");
        if ((edx8u & (1u << 30)) != 0) flags.Add("RDTSCP");

        return flags;
    }

    /// <summary>
    /// Detección vía API nativa de Windows GetLogicalProcessorInformationEx con
    /// RelationProcessorExtended, que expone los 128 bits de ProcessorFeatures.
    /// No depende del JIT ni de hardware intrinsics: funcional siempre en Windows.
    /// Los bits corresponden a PF_* (winnt.h): SSE=6, SSE2=10, AVX=39, etc.
    /// </summary>
    private List<string> DetectNativeWindowsFlags()
    {
        var flags = new List<string>();

        int bufferLength = 0;
        GetLogicalProcessorInformationEx(RelationProcessorExtended, IntPtr.Zero, ref bufferLength);
        if (bufferLength <= 0)
            return flags;

        IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorExtended, buffer, ref bufferLength))
                return flags;

            // Recorrer la estructura encadenada (campo Size indica el largo de cada relación)
            IntPtr ptr = buffer;
            int remaining = bufferLength;
            while (remaining >= 8)
            {
                int relationship = Marshal.ReadInt32(ptr);
                int size = Marshal.ReadInt32(ptr, 4);
                if (size <= 0)
                    break;

                if (relationship == RelationProcessorExtended)
                {
                    // Layout de PROCESSOR_RELATIONSHIP (union dentro de SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX):
                    //   offset 0:  Flags(1) + EfficiencyClass(1) + Reserved[20] = 22 bytes
                    //   offset 22: GroupCount (WORD = 2 bytes)
                    //   offset 24: GroupMask[GroupCount] (GROUP_AFFINITY: IntPtr.Size + Group(2) + Reserved[6])
                    //   después:   ProcessorFeatures[2] (128 bits = 16 bytes)
                    int groupCount = Marshal.ReadInt16(ptr, 8 + 22) & 0xFFFF;
                    int affinitySize = IntPtr.Size + 8;  // x64: 16, x86: 12
                    int featuresOffset = 8 + 22 + 2 + (groupCount * affinitySize);

                    if (remaining >= featuresOffset + 16)
                    {
                        ulong features0 = (ulong)Marshal.ReadInt64(ptr, featuresOffset);      // bits 0-63

                        // Bits PF_* oficiales de winnt.h para ProcessorFeatures
                        // (RelationProcessorExtended). MMX=3, SSE=6, SSE2=10, SSE3=13,
                        // SSSE3=36, SSE4.1=37, SSE4.2=38, AVX=39, AVX2=40, AVX-512F=41...
                        if ((features0 & (1UL << 3)) != 0) flags.Add("MMX");
                        if ((features0 & (1UL << 6)) != 0) flags.Add("SSE");
                        if ((features0 & (1UL << 10)) != 0) flags.Add("SSE2");
                        if ((features0 & (1UL << 13)) != 0) flags.Add("SSE3");
                        if ((features0 & (1UL << 36)) != 0) flags.Add("SSSE3");
                        if ((features0 & (1UL << 37)) != 0) flags.Add("SSE4.1");
                        if ((features0 & (1UL << 38)) != 0) flags.Add("SSE4.2");
                        if ((features0 & (1UL << 39)) != 0) flags.Add("AVX");
                        if ((features0 & (1UL << 40)) != 0) flags.Add("AVX2");
                        if ((features0 & (1UL << 41)) != 0) flags.Add("AVX-512F");
                        if ((features0 & (1UL << 42)) != 0) flags.Add("AVX-512DQ");
                        if ((features0 & (1UL << 44)) != 0) flags.Add("AVX-512BW");
                        if ((features0 & (1UL << 45)) != 0) flags.Add("AVX-512VBMI");
                        if ((features0 & (1UL << 46)) != 0) flags.Add("AVX-512VBMI2");
                        if ((features0 & (1UL << 48)) != 0) flags.Add("AVX-512IFMA");
                        if ((features0 & (1UL << 49)) != 0) flags.Add("AVX-512VPOPCNTDQ");
                        if ((features0 & (1UL << 50)) != 0) flags.Add("AVX-512CD");
                        if ((features0 & (1UL << 52)) != 0) flags.Add("AVX-512VNNI");
                        if ((features0 & (1UL << 55)) != 0) flags.Add("AVX10");
                        if ((features0 & (1UL << 57)) != 0) flags.Add("FMA");
                    }
                    break; // la relación extendida suele ser única y suficiente
                }

                ptr += size;
                remaining -= size;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Error detectando instrucciones vía API nativa: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return flags;
    }

    /// <summary>
    /// Fallback: detección de instrucciones vía WMI FeatureSet (menos confiable).
    /// Solo se usa si System.Runtime.Intrinsics no está disponible.
    /// </summary>
    private List<string> DetectInstructionsViaWmi()
    {
        var flags = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["FeatureSet"] != null)
                {
                    try
                    {
                        uint featureSet = Convert.ToUInt32(obj["FeatureSet"]);
                        // Mapeo correcto de bits de FeatureSet de Win32_Processor
                        if ((featureSet & (1u << 23)) != 0) flags.Add("MMX");
                        if ((featureSet & (1u << 25)) != 0) flags.Add("SSE");
                        if ((featureSet & (1u << 26)) != 0) flags.Add("SSE2");
                        if ((featureSet & (1u << 0)) != 0) flags.Add("SSE3");
                    }
                    catch { }
                }

                int dataWidth = 0;
                if (obj["DataWidth"] != null)
                    dataWidth = Convert.ToInt32(obj["DataWidth"]);
                if (dataWidth == 64 && !flags.Contains("x86-64"))
                    flags.Add("x86-64");
            }
        }
        catch { }
        return flags;
    }

    private double GetCurrentCpuFrequency()
    {
        // 1) Preferir el contador de rendimiento "Processor Frequency": refleja la
        //    frecuencia dinámica real (turbo boost), igual que el Administrador de tareas.
        try
        {
            if (_cpuFreqCounter != null)
            {
                var freq = _cpuFreqCounter.NextValue();
                if (freq > 0)
                    return freq;
            }
        }
        catch { }

        // 2) Fallback: Win32_Processor.CurrentClockSpeed (frecuencia base nominal)
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                return Convert.ToDouble(obj["CurrentClockSpeed"] ?? 0);
            }
        }
        catch { }
        return 0;
    }

    private void EnsureCpuCoreCountersInitialized()
    {
        if (_cpuCoreCountersInitialized) return;
        lock (_initLock)
        {
            if (_cpuCoreCountersInitialized) return;
            EnsureInitialized();
            try
            {
                // La categoría "Processor Information" usa instancias tipo "0,0", "0,1"... (nodo NUMA, índice),
                // mientras que "Processor" usa instancias simples "0", "1", "2", ... (una por procesador lógico).
                // Usamos "Processor" con "% Processor Time" por instancia para obtener el uso por núcleo.
                var count = Environment.ProcessorCount;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var counter = new PerformanceCounter("Processor", "% Processor Time", i.ToString(), true);
                        counter.NextValue(); // primera llamada siempre devuelve 0
                        _cpuCoreCounters.Add(counter);
                    }
                    catch { }
                }
                _loggingService.LogInfo($"Contadores por núcleo inicializados: {_cpuCoreCounters.Count}");
                _cpuCoreCountersInitialized = true;
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"Error inicializando contadores por núcleo: {ex.Message}");
                _cpuCoreCountersInitialized = true;
            }
        }
    }

    public int GetLogicalProcessorCount()
    {
        try { return Environment.ProcessorCount; }
        catch { return 0; }
    }

    public double[] GetCpuCoreUsages()
    {
        EnsureCpuCoreCountersInitialized();
        var result = new double[_cpuCoreCounters.Count];
        for (int i = 0; i < _cpuCoreCounters.Count; i++)
        {
            try
            {
                var v = _cpuCoreCounters[i].NextValue();
                result[i] = v < 0 ? 0 : v;
            }
            catch { result[i] = 0; }
        }
        return result;
    }

    public double[] GetCpuCoreTemperatures()
    {
        EnsureLhmInitialized();
        if (_computer == null) return Array.Empty<double>();

        var temps = new List<double>();
        try
        {
            lock (_lhmLock)
            {
                foreach (var hardware in _computer.Hardware)
                {
                    if (hardware.HardwareType != HardwareType.Cpu) continue;
                    hardware.Update();
                    // Sensores por núcleo: nombres tipo "Core #1", "Core #2", ...
                    // (solo los Core #N: excluye "Core (Tctl/Tdie)" y "Core (Tdie)")
                    var coreSensors = hardware.Sensors
                        .Where(s => s.SensorType == SensorType.Temperature)
                        .Where(s => CoreSensorRegex.IsMatch(s.Name))
                        .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var sensor in coreSensors)
                    {
                        if (sensor.Value is float f && f > 0 && f < 120)
                            temps.Add(f);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_lhmReadErrorLogged)
            {
                _lhmReadErrorLogged = true;
                _loggingService.LogWarning($"Error leyendo temperatura por núcleo via LibreHardwareMonitor: {ex.Message}");
            }
        }
        return temps.ToArray();
    }

    /// <summary>
    /// Frecuencia real actual de la CPU en MHz (contador de rendimiento).
    /// </summary>
    public double GetCpuFrequency()
    {
        EnsureInitialized();
        return GetCurrentCpuFrequency();
    }

    // =====================================================================
    // Core parking (estado de estacionamiento por núcleo)
    //
    // Windows expone el estado real en el contador de rendimiento
    // "Processor Information\Parking Status" (una instancia por procesador
    // lógico, valor 0 = activo / 1 = estacionado). Es la misma fuente que usan
    // ParkControl y el Administrador de tareas. Importante: un núcleo con 0% de
    // uso NO está necesariamente estacionado — el estado viene de este contador.
    // =====================================================================

    private void EnsureCpuParkingCountersInitialized()
    {
        if (_cpuParkingInitialized || _cpuParkingUnavailable) return;
        lock (_initLock)
        {
            if (_cpuParkingInitialized || _cpuParkingUnavailable) return;
            try
            {
                var category = new PerformanceCounterCategory("Processor Information");
                var instanceMap = new Dictionary<int, string>();
                foreach (var inst in category.GetInstanceNames())
                {
                    // Instancias con formato "0,N" (grupo NUMA, índice de procesador lógico)
                    var parts = inst.Split(',');
                    if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var idx))
                        instanceMap[idx] = inst;
                }

                var count = Environment.ProcessorCount;
                for (int i = 0; i < count; i++)
                {
                    if (!instanceMap.TryGetValue(i, out var inst)) continue;
                    try
                    {
                        var counter = new PerformanceCounter("Processor Information", "Parking Status", inst, true);
                        counter.NextValue(); // primera llamada devuelve 0
                        _cpuParkingCounters.Add(counter);
                    }
                    catch { }
                }

                if (_cpuParkingCounters.Count == 0)
                {
                    _cpuParkingUnavailable = true;
                    _loggingService.LogWarning("Contador de parking por núcleo no disponible en este sistema");
                }
                else
                {
                    _cpuParkingInitialized = true;
                    _loggingService.LogInfo($"Contadores de parking por núcleo inicializados: {_cpuParkingCounters.Count}");
                }
            }
            catch (Exception ex)
            {
                _cpuParkingUnavailable = true;
                _loggingService.LogWarning($"Parking por núcleo no disponible: {ex.Message}");
            }
        }
    }

    public bool[]? GetCpuCoreParkedStatus()
    {
        EnsureCpuParkingCountersInitialized();
        if (_cpuParkingCounters.Count == 0) return null;

        var result = new bool[_cpuParkingCounters.Count];
        for (int i = 0; i < _cpuParkingCounters.Count; i++)
        {
            try { result[i] = _cpuParkingCounters[i].NextValue() > 0.5; }
            catch { }
        }
        return result;
    }

    // =====================================================================
    // Temperatura CPU (fuente principal: LibreHardwareMonitor)
    //
    // LibreHardwareMonitor lee la temperatura directo del hardware (MSR/SMU
    // vía su driver WinRing0), sin depender de aplicaciones externas como MSI
    // Afterburner. El driver solo necesita elevación para cargar, y esta app
    // corre con requireAdministrator, así que funciona de forma autónoma en el
    // instalable. En Ryzen 7000 el sensor clave es "Core (Tctl/Tdie)".
    //
    // Fallback: WMI MSAcpi_ThermalZoneTemperature con timeout estricto. Funciona
    // en portátiles y algunos desktops; se ejecuta en Task con Wait(2000) para
    // que nunca pueda colgarse.
    // =====================================================================

    private readonly Dictionary<string, (double Temp, DateTime Checked)> _cpuTempCache = new();
    private readonly object _cpuTempLock = new();
    private bool _cpuTempWmiFailed;

    // ===== LibreHardwareMonitor (acceso serializado: no es thread-safe) =====
    private Computer? _computer;
    private readonly object _lhmLock = new();
    private bool _lhmFailed;
    private bool _lhmReadErrorLogged;
    private DateTime _lastLhmAttempt = DateTime.MinValue;

    private void EnsureLhmInitialized()
    {
        // Si el driver no pudo cargar (ej. en ese momento no estaba listo o hubo un
        // conflicto transitorio), se reintenta cada 30 s en vez de rendirse para
        // siempre: en PCs variadas el servicio del driver puede tardar en iniciar.
        if (_computer != null) return;
        if (_lhmFailed && (DateTime.Now - _lastLhmAttempt).TotalSeconds < 30) return;
        lock (_lhmLock)
        {
            if (_computer != null) return;
            if (_lhmFailed && (DateTime.Now - _lastLhmAttempt).TotalSeconds < 30) return;
            _lastLhmAttempt = DateTime.Now;
            try
            {
                var computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = false,
                    IsMemoryEnabled = false,
                    IsStorageEnabled = false,
                    IsControllerEnabled = false
                };
                computer.Open();
                _computer = computer;
                _lhmFailed = false;
                _loggingService.LogInfo("LibreHardwareMonitor inicializado (CPU + GPU)");
            }
            catch (Exception ex)
            {
                _lhmFailed = true;
                _loggingService.LogWarning($"LibreHardwareMonitor no disponible: {ex.Message}");
            }
        }
    }

    public double GetCpuTemperature()
    {
        lock (_cpuTempLock)
        {
            if (_cpuTempCache.TryGetValue("cpu", out var cached) &&
                (DateTime.Now - cached.Checked).TotalSeconds < 5)
                return cached.Temp;
        }

        double temp = GetCpuTemperatureViaLhm();
        if (temp <= 0)
            temp = GetCpuTemperatureViaAcpi();
        if (temp <= 0)
            temp = ReadPerfThermalZoneTemperature();
        if (temp <= 0)
            temp = ReadTemperatureProbe();

        lock (_cpuTempLock)
            _cpuTempCache["cpu"] = (temp, DateTime.Now);

        return temp;
    }

    private double GetCpuTemperatureViaLhm()
    {
        EnsureLhmInitialized();
        if (_computer == null) return 0;
        try
        {
            lock (_lhmLock)
            {
                foreach (var hardware in _computer.Hardware)
                {
                    if (hardware.HardwareType != HardwareType.Cpu) continue;
                    hardware.Update();

                    // Preferir el sensor que mejor representa la temperatura real:
                    // AMD: "Core (Tctl/Tdie)" > "Core (Tdie)" > "Core (Tctl)";
                    // Intel: "CPU Package" > "Core (Tjmax)" > "Core Average".
                    var sensor = hardware.Sensors
                        .Where(s => s.SensorType == SensorType.Temperature)
                        .OrderByDescending(s => ScoreCpuTempSensor(s.Name))
                        .FirstOrDefault();

                    if (sensor?.Value is float f && f > 0 && f < 120)
                        return f;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_lhmReadErrorLogged)
            {
                _lhmReadErrorLogged = true;
                _loggingService.LogWarning($"Error leyendo temperatura CPU via LibreHardwareMonitor: {ex.Message}");
            }
        }
        return 0;
    }

    private static int ScoreCpuTempSensor(string sensorName)
    {
        var n = sensorName.ToLowerInvariant();
        if (n.Contains("tctl") && n.Contains("tdie")) return 100; // AMD Zen: Tctl/Tdie
        if (n.Contains("package")) return 90;                     // Intel
        if (n.Contains("tdie")) return 80;                        // AMD Zen: die real
        if (n.Contains("tctl")) return 70;                        // AMD Zen: Tctl
        if (n.Contains("average")) return 60;
        if (n.Contains("tjmax")) return 50;
        return 0;
    }

    private double GetCpuTemperatureViaAcpi()
    {
        // Si ya falló WMI antes, no reintentar para evitar lag
        if (_cpuTempWmiFailed)
            return 0;

        try
        {
            var task = Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                foreach (ManagementObject obj in searcher.Get())
                {
                    var rawTemp = Convert.ToUInt32(obj["CurrentTemperature"] ?? 0);
                    // Valor en décimas de Kelvin: (rawTemp / 10) - 273.15
                    var temp = (rawTemp / 10.0) - 273.15;
                    if (temp > 0 && temp < 120)
                        return temp;
                }
                return 0.0;
            });

            // Timeout estricto: MSAcpi puede colgarse en muchos sistemas
            if (task.Wait(TimeSpan.FromSeconds(2)))
                return task.Result;
        }
        catch (Exception ex)
        {
            if (!_cpuTempWmiFailed)
            {
                _cpuTempWmiFailed = true;
                _loggingService.LogWarning($"Temperatura CPU via WMI ACPI no disponible: {ex.Message}");
            }
        }
        return 0;
    }

    // =====================================================================
    // Fallbacks adicionales de temperatura (para PCs variadas con Windows)
    //
    // 1) Win32_PerfFormattedData_Counters_ThermalZoneInformation: la zona
    //    térmica ACPI expuesta como contador de rendimiento (Temperature en
    //    décimas de Kelvin). Funciona en algunos equipos donde MSAcpi falla.
    // 2) Win32_TemperatureProbe: sonda térmica del hardware (CurrentReading en
    //    décimas de Kelvin). Rara vez está poblada, pero existe en equipos con
    //    drivers de monitoreo.
    // Ambas se validan con rangos de cordura (0-120 °C) para descartar lecturas
    // basura o en unidades distintas. Son compartidas entre CPU y GPU.
    // =====================================================================
    private bool _perfThermalFailed;
    private bool _probeFailed;

    private static double QueryPerfThermalZoneTemperature()
    {
        double best = 0;
        using var searcher = new ManagementObjectSearcher(
            "SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
        foreach (ManagementObject obj in searcher.Get())
        {
            if (obj["Temperature"] == null) continue;
            var t = (Convert.ToUInt32(obj["Temperature"]) / 10.0) - 273.15;
            if (t > 0 && t < 120 && t > best) best = t;
        }
        return best;
    }

    private static double QueryTemperatureProbe()
    {
        double best = 0;
        using var searcher = new ManagementObjectSearcher(
            "SELECT CurrentReading FROM Win32_TemperatureProbe");
        foreach (ManagementObject obj in searcher.Get())
        {
            if (obj["CurrentReading"] == null) continue;
            var t = (Convert.ToUInt32(obj["CurrentReading"]) / 10.0) - 273.15;
            if (t > 0 && t < 120 && t > best) best = t;
        }
        return best;
    }

    private double ReadPerfThermalZoneTemperature()
    {
        if (_perfThermalFailed) return 0;
        try
        {
            // Timeout estricto: algunas consultas WMI pueden colgarse en ciertos
            // equipos (igual que MSAcpi). Se ejecuta en Task con Wait(2s) para
            // que nunca bloquee el timer de monitoreo.
            var task = Task.Run(QueryPerfThermalZoneTemperature);
            return task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : 0;
        }
        catch (Exception ex)
        {
            _perfThermalFailed = true;
            _loggingService.LogWarning($"Zona térmica (contador de rendimiento) no disponible: {ex.Message}");
            return 0;
        }
    }

    private double ReadTemperatureProbe()
    {
        if (_probeFailed) return 0;
        try
        {
            var task = Task.Run(QueryTemperatureProbe);
            return task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : 0;
        }
        catch (Exception ex)
        {
            _probeFailed = true;
            _loggingService.LogWarning($"Win32_TemperatureProbe no disponible: {ex.Message}");
            return 0;
        }
    }

    // =====================================================================
    // Temperatura de GPU estilo Administrador de tareas (D3DKMT)
    // Task Manager lee la temperatura vía D3DKMTQueryAdapterInfo con
    // KMTQAITYPE_ADAPTERPERFDATA (62), cuya salida D3DKMT_ADAPTER_PERFDATA
    // incluye Temperature en décimas de °C (la misma fuente térmica que
    // nvidia-smi). El adaptador se abre desde el HDC de la pantalla
    // (D3DKMTOpenAdapterFromHdc) y el nombre de la GPU se obtiene con
    // EnumDisplayDevices, sin COM.
    // OJO: D3DKMT_HANDLE es UINT (32 bits), NO un puntero (d3dukmdt.h).
    // =====================================================================
    private const uint KMTQAITYPE_ADAPTERPERFDATA = 62;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMHDC
    {
        public IntPtr hDc;              // D3DKMT_PTR(HDC)
        public uint hAdapter;           // D3DKMT_HANDLE = UINT
        public Luid AdapterLuid;        // out (el kernel lo escribe: mantener el layout completo)
        public uint VidPnSourceId;      // out
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;           // D3DKMT_HANDLE = UINT
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;           // D3DKMT_HANDLE = UINT
        public uint Type;               // KMTQUERYADAPTERINFOTYPE
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    // D3DKMT_ADAPTER_PERFDATA (d3dkmthk.h): Temperature en décimas de °C
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;   // in
        public ulong MemoryFrequency;       // out
        public ulong MaxMemoryFrequency;    // out
        public ulong MaxMemoryFrequencyOC;  // out
        public ulong MemoryBandwidth;       // out
        public ulong PCIEBandwidth;         // out
        public uint FanRPM;                 // out
        public uint Power;                  // out: décimas de %
        public uint Temperature;            // out: décimas de °C
        public byte PowerStateOverride;     // out
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int D3DKMTOpenAdapterFromHdc(ref D3DKMT_OPENADAPTERFROMHDC pData);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER pData);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO pData);

    private readonly Dictionary<string, (double Temp, DateTime Checked)> _gpuTempCache = new();
    private readonly object _gpuTempLock = new();
    private bool _gpuTempWmiFailed;
    private bool _gpuTempD3dFailed;

    private double GetGpuTemperature(string gpuName)
    {
        var key = NormalizeName(gpuName);
        if (key.Length == 0) return 0;

        // Caché POR GPU (5s): si la consulta de una GPU falla (ej. iGPU AMD sin
        // display), no debe bloquear el resultado de la otra GPU (dGPU NVIDIA).
        lock (_gpuTempLock)
        {
            if (_gpuTempCache.TryGetValue(key, out var cached) &&
                (DateTime.Now - cached.Checked).TotalSeconds < 5)
                return cached.Temp;
        }

        double temp = QueryGpuTemperature(gpuName);

        lock (_gpuTempLock)
            _gpuTempCache[key] = (temp, DateTime.Now);

        return temp;
    }

    // ===== Temperatura GPU pública (para bandeja/UI sin la consulta completa de GetGpuInfo) =====
    private readonly List<string> _gpuNames = new();
    private bool _gpuNamesLoaded;

    public double GetGpuTemperature()
    {
        try
        {
            // Resolver los nombres de GPU una sola vez (Win32_VideoController)
            lock (_initLock)
            {
                if (!_gpuNamesLoaded)
                {
                    try
                    {
                        using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var n = obj["Name"]?.ToString()?.Trim();
                            if (!string.IsNullOrEmpty(n) && !_gpuNames.Contains(n))
                                _gpuNames.Add(n);
                        }
                    }
                    catch { }
                    if (_gpuNames.Count == 0)
                        _gpuNames.Add("GPU");
                    _gpuNamesLoaded = true;
                }
            }

            // Devolver la temperatura de la primera GPU con sensor disponible
            foreach (var name in _gpuNames)
            {
                var t = GetGpuTemperature(name);
                if (t > 0)
                    return t;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Error obteniendo temperatura GPU para la bandeja: {ex.Message}");
        }
        return 0;
    }

    private double QueryGpuTemperature(string gpuName)
    {
        var gpuNameLower = gpuName.ToLowerInvariant();

        // 1) LibreHardwareMonitor: lee la temperatura directo del sensor de la GPU
        //    (NVIDIA/AMD/Intel) vía su driver, sin aplicaciones externas.
        double temp = GetGpuTemperatureViaLhm(gpuName);
        if (temp > 0)
            return temp;

        // 2) D3DKMTQueryAdapterInfo (KMTQAITYPE_ADAPTERPERFDATA): la misma fuente
        //    que usa el Administrador de tareas de Windows (WDDM 2.4+).
        temp = GetGpuTemperatureViaD3D(gpuName);
        if (temp > 0)
            return temp;

        // 3) Fallback NVIDIA: nvidia-smi
        if (gpuNameLower.Contains("nvidia") || gpuNameLower.Contains("geforce") || gpuNameLower.Contains("rtx") || gpuNameLower.Contains("gtx") || gpuNameLower.Contains("quadro"))
        {
            temp = GetGpuTemperatureViaNvidiaSmi();
            if (temp > 0)
                return temp;
        }
        // 4) Fallback AMD/Intel: zona térmica WMI (aproximada, puede fallar)
        else if (gpuNameLower.Contains("amd") || gpuNameLower.Contains("radeon") || gpuNameLower.Contains("intel"))
        {
            temp = GetGpuTemperatureViaWmi();
            if (temp > 0)
                return temp;
        }

        // 5) Fallbacks genéricos finales (último recurso en cualquier equipo):
        //    zona térmica vía contador de rendimiento y sonda de temperatura.
        temp = ReadPerfThermalZoneTemperature();
        if (temp > 0)
            return temp;

        return ReadTemperatureProbe();
    }

    private double GetGpuTemperatureViaLhm(string gpuName)
    {
        EnsureLhmInitialized();
        if (_computer == null) return 0;
        try
        {
            var normalized = NormalizeName(gpuName);
            if (normalized.Length == 0) return 0;

            lock (_lhmLock)
            {
                foreach (var hardware in _computer.Hardware)
                {
                    if (hardware.HardwareType != HardwareType.GpuNvidia &&
                        hardware.HardwareType != HardwareType.GpuAmd &&
                        hardware.HardwareType != HardwareType.GpuIntel)
                        continue;

                    var hwNorm = NormalizeName(hardware.Name);
                    if (hwNorm.Length == 0) continue;
                    if (!hwNorm.Contains(normalized, StringComparison.Ordinal) &&
                        !normalized.Contains(hwNorm, StringComparison.Ordinal))
                        continue;

                    hardware.Update();

                    // Preferir el sensor "GPU Core" (el que muestra el Administrador
                    // de tareas); Hot Spot y Memory Junction quedan como respaldo.
                    var sensor = hardware.Sensors
                        .Where(s => s.SensorType == SensorType.Temperature)
                        .OrderByDescending(s => ScoreGpuTempSensor(s.Name))
                        .FirstOrDefault();

                    if (sensor?.Value is float f && f > 0 && f < 120)
                        return f;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_lhmReadErrorLogged)
            {
                _lhmReadErrorLogged = true;
                _loggingService.LogWarning($"Error leyendo temperatura GPU via LibreHardwareMonitor: {ex.Message}");
            }
        }
        return 0;
    }

    private static int ScoreGpuTempSensor(string sensorName)
    {
        var n = sensorName.ToLowerInvariant();
        if (n.Contains("core")) return 100;
        if (n.Contains("gpu")) return 90;
        if (n.Contains("hot spot") || n.Contains("hotspot")) return 80;
        if (n.Contains("memory junction") || n.Contains("mem")) return 70;
        return 0;
    }

    private double GetGpuTemperatureViaD3D(string gpuName)
    {
        try
        {
            var normalized = NormalizeName(gpuName);
            if (normalized.Length == 0) return 0;

            // Enumerar dispositivos de pantalla: el DeviceString coincide con el
            // nombre WMI de la GPU (ej. "NVIDIA GeForce RTX 4060 Ti").
            // Nota: solo aparecen las GPUs que manejan al menos un display;
            // una dGPU sin display conectado cae a los fallbacks (nvidia-smi/WMI).
            for (uint i = 0; ; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref dd, 0))
                    break;

                var deviceNorm = NormalizeName(dd.DeviceString);
                if (deviceNorm.Length > 0 &&
                    (deviceNorm.Contains(normalized, StringComparison.Ordinal) ||
                     normalized.Contains(deviceNorm, StringComparison.Ordinal)))
                {
                    var temp = QueryAdapterTemperatureViaHdc(dd.DeviceName);
                    if (temp > 0)
                        return temp;
                }
            }
        }
        catch (Exception ex)
        {
            // Solo loguear una vez para evitar spam
            if (!_gpuTempD3dFailed)
            {
                _gpuTempD3dFailed = true;
                _loggingService.LogWarning($"Error obteniendo temperatura GPU vía D3DKMT: {ex.Message}");
            }
        }
        return 0;
    }

    private static double QueryAdapterTemperatureViaHdc(string deviceName)
    {
        IntPtr hdc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
            return 0;
        try
        {
            var open = new D3DKMT_OPENADAPTERFROMHDC { hDc = hdc, hAdapter = 0 };
            if (D3DKMTOpenAdapterFromHdc(ref open) != 0 || open.hAdapter == 0)
                return 0;
            try
            {
                // D3DKMT_ADAPTER_PERFDATA: temperatura (y clocks/fan/consumo) en décimas
                int size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    for (int b = 0; b < size; b++)
                        Marshal.WriteByte(buffer, b, 0); // PhysicalAdapterIndex = 0

                    var query = new D3DKMT_QUERYADAPTERINFO
                    {
                        hAdapter = open.hAdapter,
                        Type = KMTQAITYPE_ADAPTERPERFDATA,
                        pPrivateDriverData = buffer,
                        PrivateDriverDataSize = (uint)size
                    };

                    if (D3DKMTQueryAdapterInfo(ref query) == 0)
                    {
                        var perf = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(buffer);
                        // Rango de cordura: 0 °C a 200 °C
                        if (perf.Temperature > 0 && perf.Temperature < 2000)
                            return perf.Temperature / 10.0;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
        finally
        {
            DeleteDC(hdc);
        }
        return 0;
    }

    private static string NormalizeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var chars = s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars);
    }

    private double GetGpuTemperatureViaNvidiaSmi()
    {
        // nvidia-smi suele estar en el System32 real (lo instala el driver).
        // Como la app es x86, se resuelve vía Sysnative; si no, se usa PATH.
        string exePath = "nvidia-smi";
        try
        {
            var candidate = Path.Combine(RepairService.NativeSystemDirectory, "nvidia-smi.exe");
            if (File.Exists(candidate))
                exePath = candidate;
        }
        catch { }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--query-gpu=temperature.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(3000);
                if (double.TryParse(output, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var temp))
                    return temp;
            }
        }
        catch (Exception ex)
        {
            // Solo loguear una vez para evitar spam
            if (!_gpuTempWmiFailed)
            {
                _loggingService.LogWarning($"Error obteniendo temperatura GPU via nvidia-smi: {ex.Message}");
                _gpuTempWmiFailed = true;
            }
        }
        return 0;
    }

    /// <summary>
    /// VRAM total vía nvidia-smi (memory.total en MB) para GPUs NVIDIA.
    /// Win32_VideoController.AdapterRAM es un UInt32 y con GPUs de más de 4 GB
    /// devuelve 0 o valores truncados (una RTX 4060 Ti de 8 GB reporta 4 GB).
    /// </summary>
    private static long GetGpuVramBytes(string gpuName)
    {
        var lower = gpuName.ToLowerInvariant();
        if (!(lower.Contains("nvidia") || lower.Contains("geforce") || lower.Contains("rtx") ||
              lower.Contains("gtx") || lower.Contains("quadro")))
            return 0;

        try
        {
            string exePath = "nvidia-smi";
            var candidate = Path.Combine(RepairService.NativeSystemDirectory, "nvidia-smi.exe");
            if (File.Exists(candidate))
                exePath = candidate;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(3000);
                // Con varias GPUs nvidia-smi devuelve una línea por GPU: tomar la primera
                var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (double.TryParse(firstLine, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var mb) && mb > 0)
                    return (long)(mb * 1024 * 1024);
            }
        }
        catch { }
        return 0;
    }

    private double GetGpuTemperatureViaWmi()
    {
        // Si ya falló WMI antes, no reintentar para evitar spam de logs y lag
        if (_gpuTempWmiFailed)
            return 0;

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject obj in searcher.Get())
            {
                var rawTemp = Convert.ToUInt32(obj["CurrentTemperature"] ?? 0);
                // El valor está en décimas de Kelvin: (rawTemp / 10) - 273.15
                var temp = (rawTemp / 10.0) - 273.15;
                if (temp > 0 && temp < 120)
                    return temp;
            }
        }
        catch (Exception ex)
        {
            // Solo loguear una vez para evitar spam
            if (!_gpuTempWmiFailed)
            {
                _loggingService.LogWarning($"Error obteniendo temperatura GPU via WMI: {ex.Message}");
                _gpuTempWmiFailed = true;
            }
        }
        return 0;
    }

    public MemoryInfo GetMemoryInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                var totalKB = Convert.ToInt64(obj["TotalVisibleMemorySize"] ?? 0);
                var freeKB = Convert.ToInt64(obj["FreePhysicalMemory"] ?? 0);
                var totalBytes = totalKB * 1024;
                var freeBytes = freeKB * 1024;
                var usedBytes = totalBytes - freeBytes;
                var usagePercent = totalBytes > 0 ? (double)usedBytes / totalBytes * 100 : 0;

                _loggingService.LogInfo($"Memoria detectada: Total={FormatBytes(totalBytes)}, Usada={FormatBytes(usedBytes)}, %={usagePercent:F1}");
                return new MemoryInfo(totalBytes, freeBytes, usedBytes, usagePercent, 0, usedBytes);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo info de memoria", ex);
        }

        return new MemoryInfo(0, 0, 0, 0, 0, 0);
    }

    public List<DiskInfo> GetDiskInfo()
    {
        EnsureInitialized();
        EnsureDiskCountersInitialized();
        var disks = new List<DiskInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Model, MediaType, Size, InterfaceType FROM Win32_DiskDrive");
            foreach (ManagementObject obj in searcher.Get())
            {
                var deviceId = obj["DeviceID"]?.ToString() ?? "";
                var model = obj["Model"]?.ToString()?.Trim() ?? "";
                var mediaType = obj["MediaType"]?.ToString() ?? "";
                var interfaceType = obj["InterfaceType"]?.ToString() ?? "";
                var totalSize = Convert.ToInt64(obj["Size"] ?? 0);

                var freeSpace = GetDiskFreeSpace(deviceId);
                var usagePercent = totalSize > 0 ? (double)(totalSize - freeSpace) / totalSize * 100 : 0;

                double readSpeed = 0, writeSpeed = 0;
                var index = disks.Count;
                if (index < _diskReadCounters.Count)
                {
                    readSpeed = _diskReadCounters[index].NextValue() / (1024 * 1024);
                    writeSpeed = _diskWriteCounters[index].NextValue() / (1024 * 1024);
                }

                disks.Add(new DiskInfo(
                    DeviceId: deviceId,
                    Model: $"{model} ({interfaceType})",
                    MediaType: mediaType,
                    TotalSizeBytes: totalSize,
                    FreeSpaceBytes: freeSpace,
                    UsagePercent: usagePercent,
                    ReadSpeedMBps: readSpeed,
                    WriteSpeedMBps: writeSpeed,
                    TemperatureCelsius: 0,
                    IsHealthy: true
                ));

                _loggingService.LogInfo($"Disco detectado: {model} ({interfaceType}) - Total: {FormatBytes(totalSize)}");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo info de discos", ex);
        }

        return disks;
    }

    private long GetDiskFreeSpace(string deviceId)
    {
        try
        {
            using var partitionSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{deviceId}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
            
            foreach (ManagementObject partition in partitionSearcher.Get())
            {
                using var logicalSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                
                foreach (ManagementObject logical in logicalSearcher.Get())
                {
                    return Convert.ToInt64(logical["FreeSpace"] ?? 0);
                }
            }
        }
        catch { }
        return 0;
    }

    public List<GpuInfo> GetGpuInfo()
    {
        EnsureInitialized();
        EnsureGpuCountersInitialized();
        var gpus = new List<GpuInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion, DriverDate, VideoProcessor FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim() ?? "Unknown";
                var adapterRam = Convert.ToInt64(obj["AdapterRAM"] ?? 0);
                var driverVersion = obj["DriverVersion"]?.ToString()?.Trim() ?? "";
                var driverDate = obj["DriverDate"]?.ToString() ?? "";
                var videoProcessor = obj["VideoProcessor"]?.ToString()?.Trim() ?? "";

                // AdapterRAM es un UInt32: con GPUs de más de 4 GB devuelve 0 o valores
                // truncados (ej. una RTX 4060 Ti de 8 GB reporta 4 GB). Para NVIDIA se usa
                // nvidia-smi (memory.total en MB) como fuente confiable.
                var vramBytes = GetGpuVramBytes(name);
                if (vramBytes <= 0)
                    vramBytes = adapterRam > 0 ? adapterRam : 0;

                double gpuUsage = 0;
                double gpuTemp = 0;
                double coreClock = 0;
                double memClock = 0;

                _loggingService.LogInfo($"GPU detectada: {name} | VRAM: {FormatBytes(vramBytes)}");
                
                // Intentar obtener uso de GPU desde PerformanceCounter
                if (_gpuCounters.Count > 0)
                {
                    try
                    {
                        // El uso total de GPU es la suma de todas las instancias 3D
                        foreach (var counter in _gpuCounters)
                        {
                            try
                            {
                                gpuUsage += counter.NextValue();
                            }
                            catch { }
                        }
                        gpuUsage = Math.Min(gpuUsage, 100);
                        _loggingService.LogInfo($"GPU Usage: {gpuUsage:F1}%");
                    }
                    catch (Exception ex)
                    {
                        _loggingService.LogWarning($"Error leyendo uso GPU: {ex.Message}");
                    }
                }
                else
                {
                    _loggingService.LogWarning("GPU Counter es NULL");
                }
                
                // Intentar obtener temperatura de GPU (nvidia-smi para NVIDIA, WMI para AMD/Intel)
                gpuTemp = GetGpuTemperature(name);
                if (gpuTemp > 0)
                {
                    _loggingService.LogInfo($"GPU Temp: {gpuTemp:F1}°C");
                }
                else
                {
                    _loggingService.LogInfo("GPU Temp no disponible");
                }

                _loggingService.LogInfo($"GPU Final: Uso={gpuUsage:F1}%, Temp={gpuTemp:F1}°C");
                
                gpus.Add(new GpuInfo(
                    Name: name,
                    DedicatedMemoryBytes: vramBytes,
                    SharedMemoryBytes: 0,
                    UsagePercent: gpuUsage,
                    TemperatureCelsius: gpuTemp,
                    CoreClockMHz: coreClock,
                    MemoryClockMHz: memClock,
                    DriverVersion: $"{driverVersion} ({driverDate})"
                ));

                _loggingService.LogInfo($"GPU detectada: {name} - VRAM: {FormatBytes(vramBytes)}, Uso: {gpuUsage:F1}%, Driver: {driverVersion}");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo info de GPU", ex);
        }

        if (gpus.Count == 0)
            gpus.Add(new GpuInfo("Unknown", 0, 0, 0, 0, 0, 0, ""));
        
        return gpus;
    }

    public List<NetworkAdapterInfo> GetNetworkInfo()
    {
        EnsureInitialized();
        EnsureNetworkCountersInitialized();
        var adapters = new List<NetworkAdapterInfo>();
        try
        {
            // Query sin NetEnabled - más permisiva
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True");
            int adapterIndex = 0;
            
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim() ?? "";
                var description = obj["Description"]?.ToString()?.Trim() ?? "";
                var mac = obj["MACAddress"]?.ToString()?.Replace(":", "-") ?? "";
                var speedBps = Convert.ToDouble(obj["Speed"] ?? 0);
                var speedMbps = speedBps / 1_000_000;
                var netConnectionStatus = Convert.ToInt32(obj["NetConnectionStatus"] ?? 0);
                var isConnected = netConnectionStatus == 2;
                var deviceId = obj["DeviceID"]?.ToString() ?? "";

                // Saltar adaptadores virtuales
                var descLower = description.ToLowerInvariant();
                if (descLower.Contains("wsl") || descLower.Contains("virtual") || descLower.Contains("vmware") || descLower.Contains("vpn") || descLower.Contains("bluetooth"))
                    continue;

                // Obtener IP
                string ip = "";
                try
                {
                    var configSearcher = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_NetworkAdapter.DeviceID='{deviceId}'}} WHERE AssocClass = Win32_NetworkAdapterSetting");
                    foreach (ManagementObject config in configSearcher.Get())
                    {
                        var ipArray = config["IPAddress"] as string[];
                        if (ipArray != null && ipArray.Length > 0)
                            ip = string.Join(", ", Array.FindAll(ipArray, x => !string.IsNullOrEmpty(x) && !x.StartsWith("fe80::", StringComparison.OrdinalIgnoreCase)));
                        break;
                    }
                }
                catch { }

                // Velocidad actual
                double rxSpeed = 0, txSpeed = 0;
                if (adapterIndex < _networkReceiveCounters.Count)
                {
                    try
                    {
                        rxSpeed = _networkReceiveCounters[adapterIndex].NextValue() / (1024 * 1024);
                        txSpeed = _networkSendCounters[adapterIndex].NextValue() / (1024 * 1024);
                    }
                    catch { }
                }

                var connectionType = DetermineConnectionType(description, speedMbps);

                adapters.Add(new NetworkAdapterInfo(
                    Name: name,
                    Description: description,
                    MacAddress: mac,
                    IpAddress: ip,
                    SpeedMbps: speedMbps,
                    ReceiveSpeedMBps: rxSpeed,
                    TransmitSpeedMBps: txSpeed,
                    IsConnected: isConnected,
                    ConnectionType: connectionType
                ));

                _loggingService.LogInfo($"Red detectada: {name} - IP: {ip} - Vel: {speedMbps:F0} Mbps - Conectado: {isConnected}");
                adapterIndex++;
            }

            if (adapters.Count == 0)
                _loggingService.LogWarning("No se encontraron adaptadores de red físicos");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo info de red", ex);
        }

        return adapters;
    }

    public BoardInfo GetBoardInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard");
            foreach (ManagementObject obj in searcher.Get())
            {
                return new BoardInfo(
                    Manufacturer: CleanBoardField(obj["Manufacturer"]?.ToString() ?? ""),
                    Product: CleanBoardField(obj["Product"]?.ToString() ?? ""),
                    Version: CleanBoardField(obj["Version"]?.ToString() ?? ""),
                    SerialNumber: CleanBoardField(obj["SerialNumber"]?.ToString() ?? "")
                );
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo info de placa base", ex);
        }
        return new BoardInfo("Unknown", "Unknown", "Unknown", "Unknown");
    }

    /// <summary>
    /// Limpia placeholders genéricos que devuelven los fabricantes de placas
    /// (ej. "Default string", "To be filled by O.E.M.") mostrando "N/A".
    /// </summary>
    private static string CleanBoardField(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "N/A";
        var v = value.Trim();
        if (v.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("System Serial Number", StringComparison.OrdinalIgnoreCase))
            return "N/A";
        return v;
    }

    public BiosInfo GetBiosInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, Version FROM Win32_BIOS");
            foreach (ManagementObject obj in searcher.Get())
            {
                return new BiosInfo(
                    Manufacturer: obj["Manufacturer"]?.ToString()?.Trim() ?? "Unknown",
                    Version: obj["Version"]?.ToString()?.Trim() ?? "Unknown",
                    ReleaseDate: obj["ReleaseDate"]?.ToString()?.Trim() ?? "Unknown",
                    SMBIOSBIOSVersion: obj["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? "Unknown"
                );
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo info de BIOS", ex);
        }
        return new BiosInfo("Unknown", "Unknown", "Unknown", "Unknown");
    }

    public OsInfo GetOsInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, CSName FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                return new OsInfo(
                    Name: obj["Caption"]?.ToString()?.Trim() ?? "Unknown",
                    Version: obj["Version"]?.ToString()?.Trim() ?? "Unknown",
                    BuildNumber: obj["BuildNumber"]?.ToString()?.Trim() ?? "Unknown",
                    Architecture: obj["OSArchitecture"]?.ToString()?.Trim() ?? "Unknown",
                    InstallDate: obj["InstallDate"]?.ToString()?.Trim() ?? "Unknown",
                    LastBootTime: obj["LastBootUpTime"]?.ToString()?.Trim() ?? "Unknown",
                    ComputerName: obj["CSName"]?.ToString()?.Trim() ?? "Unknown"
                );
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo info del SO", ex);
        }
        return new OsInfo("Unknown", "Unknown", "Unknown", "Unknown", "Unknown", "Unknown", "Unknown");
    }

    private string DetermineConnectionType(string description, double speedMbps)
    {
        var desc = description.ToLowerInvariant();
        if (desc.Contains("wi-fi") || desc.Contains("wifi") || desc.Contains("wireless") || desc.Contains("802.11"))
            return "WiFi";
        if (desc.Contains("ethernet") || desc.Contains("gigabit") || desc.Contains("realtek") || desc.Contains("intel"))
            return "Ethernet";
        if (speedMbps >= 100)
            return "Ethernet";
        return "Desconocido";
    }

    public void StartMonitoring(int intervalMs = 1000)
    {
        EnsureInitialized();
        lock (_lock)
        {
            if (_monitoringTimer != null) return;

            _monitoringTimer = new Timer(async _ => await UpdateMetricsAsync(), null, 0, intervalMs);
            _loggingService.LogInfo($"Monitoring iniciado cada {intervalMs}ms");
        }
    }

    public void StopMonitoring()
    {
        lock (_lock)
        {
            _monitoringTimer?.Dispose();
            _monitoringTimer = null;
            _loggingService.LogInfo("Monitoring detenido");
        }
    }

    private SystemMetrics? _cachedMetrics;
    public async Task<SystemMetrics> GetCachedMetricsAsync()
    {
        if (_cachedMetrics == null)
        {
            await UpdateMetricsAsync();
        }
        return _cachedMetrics ?? new SystemMetrics(0, 0, 0, 0, 0, new(), new GpuMetrics(0,0,0,0,0), new());
    }

    // Caché para datos estáticos que no cambian cada segundo
    private CpuInfo? _cachedCpuInfo;
    private List<DiskInfo>? _cachedDiskInfo;
    private List<GpuInfo>? _cachedGpuInfo;
    private List<NetworkAdapterInfo>? _cachedNetworkInfo;
    private string? _primaryGpuName;
    private DateTime _lastFullRefresh;

    /// <summary>
    /// Actualización ligera de métricas: solo lee contadores de rendimiento (sin WMI).
    /// Los datos estáticos (nombre CPU, discos, GPU, red) se cachean y se refrescan cada 10 segundos.
    /// </summary>
    private async Task UpdateMetricsAsync()
    {
        try
        {
            // Refrescar datos estáticos cada 10 segundos (no cada segundo)
            bool needFullRefresh = (DateTime.Now - _lastFullRefresh).TotalSeconds > 10
                || _cachedCpuInfo == null || _cachedDiskInfo == null;

            if (needFullRefresh)
            {
                _cachedCpuInfo = await Task.Run(() => GetCpuInfo());
                _cachedDiskInfo = await Task.Run(() => GetDiskInfo());
                _cachedGpuInfo = await Task.Run(() => GetGpuInfo());
                _cachedNetworkInfo = await Task.Run(() => GetNetworkInfo());
                _lastFullRefresh = DateTime.Now;

                // Determinar GPU primario una sola vez
                _primaryGpuName = _cachedGpuInfo.FirstOrDefault(g =>
                    !g.Name.Contains("Radeon(TM)", StringComparison.OrdinalIgnoreCase) &&
                    !g.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase))?.Name
                    ?? _cachedGpuInfo.FirstOrDefault()?.Name
                    ?? "Unknown";
            }

            // Lectura ligera de contadores (sin WMI, sin lag)
            double cpuUsage = 0;
            try { cpuUsage = _cpuCounter?.NextValue() ?? 0; } catch { }

            // Memoria: consulta WMI ligera (es rápida)
            var memoryInfo = await Task.Run(() => GetMemoryInfo());

            // GPU: solo leer contadores, sin WMI
            double gpuUsage = 0;
            double gpuTemp = 0;
            if (_cachedGpuInfo != null)
            {
                // Leer temperatura desde caché (se actualiza cada 5s internamente)
                var primaryGpu = _cachedGpuInfo.FirstOrDefault(g => g.Name == _primaryGpuName)
                    ?? _cachedGpuInfo.FirstOrDefault();
                if (primaryGpu != null)
                {
                    gpuTemp = GetGpuTemperature(primaryGpu.Name);
                }

                // Leer uso de GPU desde performance counters
                if (_gpuCounters.Count > 0)
                {
                    try
                    {
                        foreach (var counter in _gpuCounters)
                        {
                            try { gpuUsage += counter.NextValue(); } catch { }
                        }
                        gpuUsage = Math.Min(gpuUsage, 100);
                    }
                    catch { }
                }
            }

            // Discos: solo leer contadores de velocidad
            var diskMetrics = new List<DiskMetrics>();
            if (_cachedDiskInfo != null)
            {
                for (int i = 0; i < _cachedDiskInfo.Count; i++)
                {
                    var disk = _cachedDiskInfo[i];
                    double readSpeed = 0, writeSpeed = 0;
                    if (i < _diskReadCounters.Count)
                    {
                        try
                        {
                            readSpeed = _diskReadCounters[i].NextValue() / (1024 * 1024);
                            writeSpeed = _diskWriteCounters[i].NextValue() / (1024 * 1024);
                        }
                        catch { }
                    }
                    diskMetrics.Add(new DiskMetrics(disk.DeviceId, readSpeed, writeSpeed, disk.UsagePercent, disk.TemperatureCelsius));
                }
            }

            // Red: solo leer contadores de velocidad
            var networkMetrics = new List<NetworkMetrics>();
            if (_cachedNetworkInfo != null)
            {
                for (int i = 0; i < _cachedNetworkInfo.Count; i++)
                {
                    var net = _cachedNetworkInfo[i];
                    double rxSpeed = 0, txSpeed = 0;
                    if (i < _networkReceiveCounters.Count)
                    {
                        try
                        {
                            rxSpeed = _networkReceiveCounters[i].NextValue() / (1024 * 1024);
                            txSpeed = _networkSendCounters[i].NextValue() / (1024 * 1024);
                        }
                        catch { }
                    }
                    networkMetrics.Add(new NetworkMetrics(net.Name, rxSpeed, txSpeed, net.IsConnected));
                }
            }

            // Frecuencia CPU en vivo (contador de rendimiento; fallback al valor cacheado)
            double cpuFreq = _cachedCpuInfo?.CurrentFrequencyMHz ?? 0;
            try
            {
                if (_cpuFreqCounter != null)
                {
                    var f = _cpuFreqCounter.NextValue();
                    if (f > 0) cpuFreq = f;
                }
            }
            catch { }

            // Temperatura CPU en vivo (caché interna de 5s, igual que la GPU)
            double cpuTemp = GetCpuTemperature();

            var gpuMetricsFinal = new GpuMetrics(gpuUsage, gpuTemp, 0, 0, 0);

            _cachedMetrics = new SystemMetrics(
                CpuUsagePercent: cpuUsage,
                CpuFrequencyMHz: cpuFreq,
                CpuTemperatureCelsius: cpuTemp,
                MemoryUsedBytes: memoryInfo.UsedBytes,
                MemoryUsagePercent: memoryInfo.UsagePercent,
                Disks: diskMetrics,
                Gpu: gpuMetricsFinal,
                Network: networkMetrics
            );

            OnMetricsUpdated?.Invoke(_cachedMetrics);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error actualizando métricas", ex);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public void Dispose()
    {
        StopMonitoring();
        _cpuCounter?.Dispose();
        _cpuFreqCounter?.Dispose();
        foreach (var c in _diskReadCounters) c.Dispose();
        foreach (var c in _diskWriteCounters) c.Dispose();
        foreach (var c in _networkReceiveCounters) c.Dispose();
        foreach (var c in _networkSendCounters) c.Dispose();
        foreach (var c in _gpuCounters) c.Dispose();
        _gpuTempCounter?.Dispose();
        foreach (var c in _cpuCoreCounters) c.Dispose();
        foreach (var c in _cpuParkingCounters) c.Dispose();
        if (_computer != null)
        {
            try { _computer.Close(); } catch { }
            _computer = null;
        }
        GC.SuppressFinalize(this);
    }
}