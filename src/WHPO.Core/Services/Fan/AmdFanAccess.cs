using WHPO.Core.Services.Fan.Interop;
using WHPO.Core.Services.Interfaces;
using LibreHardwareMonitor.Hardware;

namespace WHPO.Core.Services.Fan;

/// <summary>
/// Acceso de ESCRITURA a los ventiladores de las GPU AMD por ADL Overdrive5
/// (atiadlxx.dll, la librería del driver de AMD).
///
/// LHM ya sabe controlar AMD por este mismo camino, pero sólo cuando logra armar su
/// canal: su implementación crea el control y lo activa únicamente si la lectura de
/// % responde en ese momento (ver AmdGpu.cs). Cuando eso falla, la GPU queda como
/// "solo lectura" aunque el driver sí acepte escrituras. Este acceso pregunta
/// aparte por el soporte de fan (FanSpeedInfo) y, si está, expone el handle que
/// escribe el duty (FanSpeed_Set) y lo devuelve al driver (FanSpeedToDefault_Set).
///
/// Todo diagnóstico (driver ausente, adaptador sin soporte, rango del ventilador)
/// queda en el log: la pestaña no cambia, sólo se habilita el control del canal.
/// </summary>
internal static class AmdFanAccess
{
    private const string LogTag = "FanControlService: ADL:";

    private static readonly object Sync = new();

    // ADL guarda el puntero al allocador: la instancia tiene que sobrevivir a la
    // llamada de creación porque el driver la usa después para reservar buffers.
    private static readonly AmdAdl.AdlMainMemoryAllocDelegate Allocator = AmdAdl.MainMemoryAlloc;

    private static IntPtr _context;
    private static bool _sessionOpen;
    private static bool _sessionFailed;
    private static string _sessionError = DescribeUnavailable();
    private static List<AmdAdapter>? _adapters;
    private static readonly Dictionary<int, AdlFanWriteHandle> HandlesByAdapter = new();
    private static readonly HashSet<string> LoggedOnce = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Un adaptador AMD con soporte de ventilador por Overdrive5.</summary>
    private sealed record AmdAdapter(
        int Index,
        string Name,
        string Normalized,
        bool Active,
        int OverdriveLevel,
        int MinPercent,
        int MaxPercent,
        bool CanWritePercent,
        int FanFlags);

    private static string DescribeUnavailable()
    {
        if (!AmdAdl.IsAvailable) return "atiadlxx.dll ausente (driver de AMD no instalado)";
        if (!AmdAdl.HasFanOverdrive5) return "la versión instalada de atiadlxx.dll no exporta el control de fan de Overdrive5";
        return string.Empty;
    }

    public static bool IsSupported => AmdAdl.IsAvailable && AmdAdl.HasFanOverdrive5;

    // =====================================================================
    // API que consume FanControlService
    // =====================================================================

    /// <summary>
    /// Handle de escritura para el ventilador <paramref name="fanIndex"/> de la GPU
    /// AMD <paramref name="gpu"/>. AMD expone un solo controlador térmico por placa
    /// (índice 0), que es el que LHM publica como fan/control 0.
    /// </summary>
    public static IFanWriteHandle? TryGetWriteHandle(IHardware gpu, int fanIndex, ILoggingService log)
    {
        if (!IsSupported) return null;

        // AMD: un único controlador térmico por adaptador. Si LHM numera otro índice
        // (raro), no se escribe: mejor solo lectura que mandar el duty a otro canal.
        if (fanIndex != 0)
        {
            LogOnce(log, $"indice:{fanIndex}",
                $"{LogTag} {gpu.Name}: LHM numera el ventilador {fanIndex}; ADL sólo expone el controlador térmico 0. Se deja solo lectura.");
            return null;
        }

        try
        {
            lock (Sync)
            {
                if (!EnsureSession(log)) return null;

                var adapter = MatchAdapter(gpu.Name);
                if (adapter == null)
                {
                    LogOnce(log, $"sin-adaptador:{gpu.Name}",
                        $"{LogTag} la GPU '{gpu.Name}' no aparece en ADL con soporte de fan; se deja solo lectura.");
                    return null;
                }

                if (!adapter.CanWritePercent)
                {
                    LogOnce(log, $"sin-escritura:{adapter.Name}",
                        $"{LogTag} '{adapter.Name}': el driver no reporta escritura de % (flags=0x{adapter.FanFlags:X}); se deja solo lectura.");
                    return null;
                }

                if (!HandlesByAdapter.TryGetValue(adapter.Index, out var handle))
                {
                    handle = new AdlFanWriteHandle(adapter.Index, adapter.Name, adapter.MinPercent, adapter.MaxPercent);
                    HandlesByAdapter[adapter.Index] = handle;
                    log.LogInfo($"{LogTag} '{adapter.Name}': control por ADL habilitado " +
                                $"(rango {adapter.MinPercent}-{adapter.MaxPercent}%, Overdrive {adapter.OverdriveLevel}).");
                }
                return handle;
            }
        }
        catch (Exception ex)
        {
            LogOnce(log, "error:" + ex.Message, $"{LogTag} no se pudo preparar el control de la GPU AMD: {ex.Message}");
            return null;
        }
    }

    /// <summary>Estado del backend AMD para el diagnóstico de GPU del log.</summary>
    public static string DescribeBackend(IHardware gpu, ILoggingService log)
    {
        if (!IsSupported) return _sessionError;

        try
        {
            lock (Sync)
            {
                if (!EnsureSession(log)) return _sessionError;

                var adapter = MatchAdapter(gpu.Name);
                if (adapter == null) return "ADL no encuentra este adaptador con soporte de fan (Overdrive5)";

                return $"{adapter.MinPercent}-{adapter.MaxPercent}% por ADL Overdrive {adapter.OverdriveLevel}" +
                       (adapter.CanWritePercent ? string.Empty : ", sin escritura de %");
            }
        }
        catch (Exception ex)
        {
            return $"error consultando ADL: {ex.Message}";
        }
    }

    public static void ResetSession()
    {
        lock (Sync)
        {
            CloseSessionLocked();
            _sessionFailed = false;
            _sessionError = DescribeUnavailable();
            _adapters = null;
            HandlesByAdapter.Clear();
            LoggedOnce.Clear();
        }
    }

    public static void Dispose()
    {
        lock (Sync) CloseSessionLocked();
    }

    // =====================================================================
    // Sesión de ADL
    // =====================================================================

    private static void CloseSessionLocked()
    {
        if (_sessionOpen && _context != IntPtr.Zero)
        {
            try { AmdAdl.ADL2_Main_Control_Destroy(_context); } catch { }
        }
        _sessionOpen = false;
        _context = IntPtr.Zero;
        _adapters = null;
        HandlesByAdapter.Clear();
    }

    private static bool EnsureSession(ILoggingService log)
    {
        if (_sessionOpen && _adapters != null) return true;
        if (_sessionFailed) return false;

        var context = IntPtr.Zero;
        var status = AmdAdl.ADL2_Main_Control_Create(Allocator, 1, ref context);
        if (status != AmdAdl.AdlStatus.Ok || context == IntPtr.Zero)
        {
            _sessionFailed = true;
            _sessionError = $"ADL2_Main_Control_Create devolvió {status}";
            log.LogWarning($"{LogTag} no se pudo abrir ADL ({_sessionError}); los ventiladores de la GPU AMD quedan solo lectura.");
            return false;
        }

        _context = context;
        _sessionOpen = true;

        try
        {
            _adapters = EnumerateAdapters(log);
        }
        catch (Exception ex)
        {
            _sessionFailed = true;
            _sessionError = ex.Message;
            log.LogWarning($"{LogTag} error enumerando adaptadores: {ex.Message}");
            return false;
        }

        return true;
    }

    private static List<AmdAdapter> EnumerateAdapters(ILoggingService log)
    {
        var list = new List<AmdAdapter>();

        int count = 0;
        if (AmdAdl.ADL2_Adapter_NumberOfAdapters_Get(_context, ref count) != AmdAdl.AdlStatus.Ok || count <= 0)
        {
            log.LogWarning($"{LogTag} ADL no reporta adaptadores.");
            return list;
        }

        var info = new AmdAdl.AdlAdapterInfo[count];
        if (AmdAdl.GetAdapterInfo(_context, info) != AmdAdl.AdlStatus.Ok)
        {
            log.LogWarning($"{LogTag} ADL no pudo leer la información de los adaptadores.");
            return list;
        }

        foreach (var adapter in info)
        {
            string name = string.IsNullOrWhiteSpace(adapter.AdapterName) ? adapter.DisplayName : adapter.AdapterName;

            // ADL reporta el VendorID mal en Windows (bug conocido): se acepta si el
            // UDID trae el vendor de AMD o si el nombre lo delata.
            bool isAmd = adapter.VendorId == AmdAdl.AtiVendorId
                         || (adapter.Udid?.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ?? false)
                         || (name?.Contains("AMD", StringComparison.OrdinalIgnoreCase) ?? false)
                         || (name?.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ?? false);
            if (!isAmd) continue;

            int active = 0;
            try { AmdAdl.ADL2_Adapter_Active_Get(_context, adapter.AdapterIndex, out active); } catch { }

            int supported = 0, enabled = 0, level = 0;
            try { AmdAdl.ADL2_Overdrive_Caps(_context, adapter.AdapterIndex, ref supported, ref enabled, ref level); } catch { }

            var fanInfo = new AmdAdl.AdlFanSpeedInfo
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<AmdAdl.AdlFanSpeedInfo>()
            };
            var fanStatus = AmdAdl.ADL2_Overdrive5_FanSpeedInfo_Get(_context, adapter.AdapterIndex, 0, ref fanInfo);
            if (fanStatus != AmdAdl.AdlStatus.Ok)
            {
                // Sin info de fan el adaptador no expone control (es el caso normal de
                // una iGPU, que no tiene ventilador propio).
                log.LogDebug($"{LogTag} '{name}': sin info de fan (Overdrive5 devolvió {fanStatus}); no se puede controlar.");
                continue;
            }

            bool canWrite = fanInfo.Flags == 0 || (fanInfo.Flags & AmdAdl.SupportsPercentWrite) != 0;

            list.Add(new AmdAdapter(
                adapter.AdapterIndex,
                name ?? $"AMD GPU {adapter.AdapterIndex}",
                Normalize(name ?? string.Empty),
                active == AmdAdl.AdlTrue,
                level,
                Math.Max(0, fanInfo.MinPercent),
                fanInfo.MaxPercent > 0 ? fanInfo.MaxPercent : 100,
                canWrite,
                fanInfo.Flags));
        }

        log.LogInfo($"{LogTag} adaptadores AMD con soporte de fan: {list.Count}" +
                    (list.Count > 0 ? $" ({string.Join(", ", list.Select(a => $"{a.Name} {a.MinPercent}-{a.MaxPercent}%"))})" : string.Empty));
        return list;
    }

    // =====================================================================
    // Utilidades
    // =====================================================================

    /// <summary>
    /// Empareja la GPU AMD de LHM con el adaptador de ADL. Los nombres coinciden en
    /// general (LHM toma el nombre de ADL); si no, con un solo adaptador con fan se
    /// asume que es el mismo.
    /// </summary>
    private static AmdAdapter? MatchAdapter(string lhmName)
    {
        if (_adapters == null || _adapters.Count == 0) return null;

        string normalized = Normalize(lhmName);
        var exact = _adapters.FirstOrDefault(a => a.Normalized == normalized);
        if (exact != null) return exact;

        var partial = _adapters.FirstOrDefault(a =>
            a.Normalized.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(a.Normalized, StringComparison.OrdinalIgnoreCase));
        if (partial != null) return partial;

        if (_adapters.Count == 1) return _adapters[0];

        // Varios adaptadores: preferir el activo (el que tiene pantalla conectada).
        return _adapters.FirstOrDefault(a => a.Active);
    }

    private static string Normalize(string name) =>
        name.Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim()
            .ToLowerInvariant();

    private static void LogOnce(ILoggingService log, string key, string message)
    {
        if (LoggedOnce.Add(key)) log.LogWarning(message);
    }

    // =====================================================================
    // Handle de escritura por ADL
    // =====================================================================

    private sealed class AdlFanWriteHandle : IFanWriteHandle
    {
        private readonly int _adapterIndex;
        private readonly string _adapterName;
        private readonly int _minPercent;
        private readonly int _maxPercent;

        public AdlFanWriteHandle(int adapterIndex, string adapterName, int minPercent, int maxPercent)
        {
            _adapterIndex = adapterIndex;
            _adapterName = adapterName;
            _minPercent = minPercent;
            _maxPercent = maxPercent;
        }

        public FanWriteMode Mode { get; private set; } = FanWriteMode.Undefined;

        public float? ReadPercent()
        {
            try
            {
                lock (Sync)
                {
                    var value = new AmdAdl.AdlFanSpeedValue
                    {
                        Size = System.Runtime.InteropServices.Marshal.SizeOf<AmdAdl.AdlFanSpeedValue>(),
                        SpeedType = AmdAdl.SpeedTypePercent
                    };

                    if (AmdAdl.ADL2_Overdrive5_FanSpeed_Get(_context, _adapterIndex, 0, ref value) != AmdAdl.AdlStatus.Ok)
                        return null;

                    return value.FanSpeed;
                }
            }
            catch
            {
                return null;
            }
        }

        public bool SetSoftware(float percent)
        {
            try
            {
                int value = (int)Math.Round(Math.Clamp(percent, _minPercent, _maxPercent));
                var fan = new AmdAdl.AdlFanSpeedValue
                {
                    Size = System.Runtime.InteropServices.Marshal.SizeOf<AmdAdl.AdlFanSpeedValue>(),
                    SpeedType = AmdAdl.SpeedTypePercent,
                    Flags = AmdAdl.FlagUserDefinedSpeed,
                    FanSpeed = value
                };

                lock (Sync)
                {
                    if (AmdAdl.ADL2_Overdrive5_FanSpeed_Set(_context, _adapterIndex, 0, ref fan) != AmdAdl.AdlStatus.Ok)
                        return false;
                }

                Mode = FanWriteMode.Software;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool SetDefault()
        {
            try
            {
                lock (Sync)
                {
                    if (AmdAdl.ADL2_Overdrive5_FanSpeedToDefault_Set(_context, _adapterIndex, 0) != AmdAdl.AdlStatus.Ok)
                        return false;
                }

                Mode = FanWriteMode.Default;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override string ToString() => $"ADL {_adapterName}";
    }
}
