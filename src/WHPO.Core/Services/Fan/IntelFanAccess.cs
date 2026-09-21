using WHPO.Core.Services.Fan.Interop;
using WHPO.Core.Services.Interfaces;
using LibreHardwareMonitor.Hardware;

namespace WHPO.Core.Services.Fan;

/// <summary>
/// Acceso de ESCRITURA a los ventiladores de las GPU Intel (Arc) por IGCL.
///
/// LHM enumera las GPU Intel y publica sus RPM y temperaturas, pero no tiene
/// ningún canal de control: su implementación solo llama a ctlFanGetState. Por eso
/// la pestaña mostraba estas placas como "Solo lectura". Acá vive la parte que
/// falta: enumerar los fans por IGCL, preguntar si son controlables
/// (ctlFanGetProperties) y exponer un handle que fija el duty
/// (ctlFanSetFixedSpeedMode) o lo devuelve al driver (ctlFanSetDefaultMode).
///
/// La sesión de IGCL se abre una sola vez, se reutiliza entre lecturas y se cierra
/// al liberar el servicio. Todo lo que puede fallar (DLL ausente, driver viejo,
/// tarjeta sin ventiladores, placa no controlable) devuelve null y deja el motivo
/// en el log: la pestaña sigue mostrando el canal como solo lectura, nunca rompe.
/// </summary>
internal static class IntelFanAccess
{
    private const string LogTag = "FanControlService: IGCL:";

    private static readonly object Sync = new();
    private static IntelIgcl.CtlApiHandle _apiHandle;
    private static bool _sessionOpen;
    private static bool _sessionShared;
    private static bool _sessionFailed;
    private static string _sessionError = IntelIgcl.IsAvailable ? string.Empty : "ControlLib.dll ausente o sin las funciones de fan";
    private static List<IntelDevice>? _devices;
    private static readonly Dictionary<string, IgclFanWriteHandle> HandlesByDeviceFan = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedOnce = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Un ventilador de una GPU Intel, con lo que IGCL reporta de él.</summary>
    private sealed record IntelFan(int Index, IntelIgcl.CtlFanHandle Handle, bool CanControl, uint SupportedModes, int MaxRpm);

    /// <summary>Una GPU Intel abierta por IGCL (con sus ventiladores).</summary>
    private sealed record IntelDevice(string Name, string Normalized, List<IntelFan> Fans);

    public static bool IsSupported => IntelIgcl.IsAvailable;

    // =====================================================================
    // API que consume FanControlService
    // =====================================================================

    /// <summary>
    /// Handle de escritura para el fan <paramref name="fanIndex"/> de la GPU Intel
    /// <paramref name="gpu"/>, o null si IGCL no la puede controlar (y el canal queda
    /// como solo lectura, igual que hoy).
    /// </summary>
    public static IFanWriteHandle? TryGetWriteHandle(IHardware gpu, int fanIndex, ILoggingService log)
    {
        if (!IntelIgcl.IsAvailable) return null;

        try
        {
            lock (Sync)
            {
                if (!EnsureSession(log)) return null;

                var device = MatchDevice(gpu.Name);
                if (device == null)
                {
                    LogOnce(log, $"sin-dispositivo:{gpu.Name}",
                        $"{LogTag} la GPU '{gpu.Name}' no aparece en IGCL; se deja solo lectura.");
                    return null;
                }

                if (fanIndex < 0 || fanIndex >= device.Fans.Count)
                {
                    // LHM y IGCL enumeran los fans por separado: si no coinciden en
                    // cantidad no se inventa un mapeo (mejor solo lectura que escribirle
                    // al ventilador equivocado).
                    LogOnce(log, $"fans-desalineados:{device.Name}",
                        $"{LogTag} '{device.Name}': IGCL reporta {device.Fans.Count} ventilador(es) y LHM numera el índice {fanIndex}; " +
                        "se deja solo lectura.");
                    return null;
                }

                var fan = device.Fans[fanIndex];
                if (!fan.CanControl)
                {
                    LogOnce(log, $"no-controlable:{device.Name}:{fanIndex}",
                        $"{LogTag} '{device.Name}' fan {fanIndex + 1}: el driver no ofrece control " +
                        $"(canControl={fan.CanControl}, modos=0x{fan.SupportedModes:X}, maxRPM={fan.MaxRpm}); se deja solo lectura.");
                    return null;
                }

                var key = device.Normalized + "#" + fanIndex;
                if (!HandlesByDeviceFan.TryGetValue(key, out var handle))
                {
                    handle = new IgclFanWriteHandle(device.Name, fan);
                    HandlesByDeviceFan[key] = handle;
                    log.LogInfo($"{LogTag} '{device.Name}' fan {fanIndex + 1}: control por IGCL habilitado " +
                                $"(modos=0x{fan.SupportedModes:X}, maxRPM={fan.MaxRpm}).");
                }
                return handle;
            }
        }
        catch (Exception ex)
        {
            LogOnce(log, "error:" + ex.Message, $"{LogTag} no se pudo preparar el control de la GPU Intel: {ex.Message}");
            return null;
        }
    }

    /// <summary>Estado del backend Intel para el diagnóstico de GPU del log.</summary>
    public static string DescribeBackend(IHardware gpu, ILoggingService log)
    {
        if (!IntelIgcl.IsAvailable) return _sessionError;

        try
        {
            lock (Sync)
            {
                if (!EnsureSession(log)) return _sessionError;

                var device = MatchDevice(gpu.Name);
                if (device == null) return "IGCL no enumera esta GPU (driver ausente o dispositivo no soportado)";

                int controllable = device.Fans.Count(f => f.CanControl);
                return $"{device.Fans.Count} ventilador(es) por IGCL, {controllable} controlable(s)";
            }
        }
        catch (Exception ex)
        {
            return $"error consultando IGCL: {ex.Message}";
        }
    }

    /// <summary>Cierra la sesión: tras dormir o desinstalar el driver hay que reabrir.</summary>
    public static void ResetSession()
    {
        lock (Sync)
        {
            CloseSessionLocked();
            _sessionFailed = false;
            _sessionError = IntelIgcl.IsAvailable ? string.Empty : "ControlLib.dll ausente o sin las funciones de fan";
            _devices = null;
            HandlesByDeviceFan.Clear();
            LoggedOnce.Clear();
        }
    }

    public static void Dispose()
    {
        lock (Sync) CloseSessionLocked();
    }

    // =====================================================================
    // Sesión de IGCL
    // =====================================================================

    private static void CloseSessionLocked()
    {
        // Con sesión compartida (la abrió LHM) NO se llama a ctlClose: cerrarla podría
        // tumbar la sesión que LHM está usando para leer RPM y temperaturas de la GPU.
        if (_sessionOpen && !_sessionShared)
        {
            try { IntelIgcl.ctlClose(_apiHandle); } catch { }
        }
        _sessionOpen = false;
        _sessionShared = false;
        _apiHandle = default;
        _devices = null;
        HandlesByDeviceFan.Clear();
    }

    private static bool EnsureSession(ILoggingService log)
    {
        if (_sessionOpen && _devices != null) return true;
        if (_sessionFailed) return false;

        var initArgs = new IntelIgcl.CtlInitArgs
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<IntelIgcl.CtlInitArgs>(),
            Version = 0,
            AppVersion = IntelIgcl.ImplVersion,
            Flags = (uint)IntelIgcl.CtlInitFlag.UseLevelZero,
            ApplicationUid = new IntelIgcl.CtlApplicationId { Data4 = new byte[8] }
        };

        var handle = new IntelIgcl.CtlApiHandle();
        int result = IntelIgcl.ctlInit(ref initArgs, ref handle);

        // 1 = "ya está abierta por otro llamador del proceso" (LHM, que la abre para
        // leer RPM). No es un error: sirve el handle igual. Si en ese caso el driver
        // no dejó handle, no hay sesión que usar.
        bool shared = result == IntelIgcl.CtlResultStillOpenByAnotherCaller;
        if ((result != IntelIgcl.CtlResultSuccess && !shared) || handle.IsNull)
        {
            _sessionFailed = true;
            _sessionError = $"ctlInit devolvió 0x{result:X8}";
            log.LogWarning($"{LogTag} no se pudo inicializar IGCL ({_sessionError}); los ventiladores de la GPU Intel quedan solo lectura.");
            return false;
        }

        _apiHandle = handle;
        _sessionOpen = true;
        _sessionShared = shared;

        try
        {
            _devices = EnumerateDevices(log);
            if (_devices.Count == 0)
            {
                _sessionFailed = true;
                _sessionError = "IGCL no reporta GPU Intel";
                log.LogWarning($"{LogTag} IGCL no reporta ninguna GPU Intel; los ventiladores quedan solo lectura.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _sessionFailed = true;
            _sessionError = ex.Message;
            log.LogWarning($"{LogTag} error enumerando dispositivos: {ex.Message}");
            return false;
        }

        return true;
    }

    private static List<IntelDevice> EnumerateDevices(ILoggingService log)
    {
        var list = new List<IntelDevice>();

        uint count = 0;
        if (IntelIgcl.ctlEnumerateDevices(_apiHandle, ref count, null) != IntelIgcl.CtlResultSuccess || count == 0)
            return list;

        count = Math.Min(count, (uint)IntelIgcl.MaxDevices);
        var handles = new IntelIgcl.CtlDeviceAdapterHandle[count];
        if (IntelIgcl.ctlEnumerateDevices(_apiHandle, ref count, handles) != IntelIgcl.CtlResultSuccess)
            return list;

        for (int i = 0; i < count; i++)
        {
            var props = new IntelIgcl.CtlDeviceAdapterProperties
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<IntelIgcl.CtlDeviceAdapterProperties>(),
                Name = string.Empty,
                Reserved = new byte[IntelIgcl.MaxReservedSize]
            };

            if (IntelIgcl.ctlGetDeviceProperties(handles[i], ref props) != IntelIgcl.CtlResultSuccess) continue;
            if (props.DeviceType != IntelIgcl.CtlDeviceType.Graphics) continue;
            if (props.PciVendorId != IntelIgcl.IntelVendorId) continue;

            string name = string.IsNullOrWhiteSpace(props.Name) ? $"Intel GPU {i}" : props.Name;
            list.Add(new IntelDevice(name, Normalize(name), EnumerateFans(handles[i], name, log)));
        }

        return list;
    }

    private static List<IntelFan> EnumerateFans(IntelIgcl.CtlDeviceAdapterHandle device, string deviceName, ILoggingService log)
    {
        var fans = new List<IntelFan>();

        uint fanCount = 0;
        if (IntelIgcl.ctlEnumFans(device, ref fanCount, null) != IntelIgcl.CtlResultSuccess || fanCount == 0)
            return fans;

        var fanHandles = new IntelIgcl.CtlFanHandle[fanCount];
        if (IntelIgcl.ctlEnumFans(device, ref fanCount, fanHandles) != IntelIgcl.CtlResultSuccess)
            return fans;

        for (int i = 0; i < fanCount; i++)
        {
            var props = new IntelIgcl.CtlFanProperties
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<IntelIgcl.CtlFanProperties>(),
                Version = 0
            };

            bool canControl = false;
            uint modes = 0;
            int maxRpm = 0;

            if (IntelIgcl.ctlFanGetProperties(fanHandles[i], ref props) == IntelIgcl.CtlResultSuccess)
            {
                // Sólo se acepta el modo fijo (el que escribimos): si el driver no lo
                // lista, el ventilador no se toca. supportedModes es un bitfield
                // (1 << ctl_fan_speed_mode_t), no los valores del enum: el bit se arma
                // con FanModeFixedBit. Con el layout de properties leído mal,
                // canControl/modos quedan en 0 y el canal se muestra solo lectura.
                canControl = props.CanControl != 0 && (props.SupportedModes & IntelIgcl.FanModeFixedBit) != 0;
                modes = props.SupportedModes;
                maxRpm = props.MaxRpm;
            }
            else
            {
                log.LogDebug($"{LogTag} '{deviceName}' fan {i + 1}: ctlFanGetProperties falló; se asume sin control.");
            }

            fans.Add(new IntelFan(i, fanHandles[i], canControl, modes, maxRpm));
        }

        log.LogInfo($"{LogTag} '{deviceName}': {fans.Count} ventilador(es) detectado(s) por IGCL " +
                    $"({fans.Count(f => f.CanControl)} controlable(s)).");
        return fans;
    }

    // =====================================================================
    // Utilidades
    // =====================================================================

    /// <summary>
    /// Empareja una GPU de LHM con una de IGCL por nombre normalizado (sacar marcas
    /// y espacios). Si los nombres no coinciden pero hay UNA sola GPU Intel de cada
    /// lado, se asume que es la misma (es el caso normal: una sola Arc).
    /// </summary>
    private static IntelDevice? MatchDevice(string lhmName)
    {
        if (_devices == null || _devices.Count == 0) return null;

        string normalized = Normalize(lhmName);
        var exact = _devices.FirstOrDefault(d => d.Normalized == normalized);
        if (exact != null) return exact;

        var partial = _devices.FirstOrDefault(d =>
            d.Normalized.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(d.Normalized, StringComparison.OrdinalIgnoreCase));
        if (partial != null) return partial;

        return _devices.Count == 1 ? _devices[0] : null;
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
    // Handle de escritura por IGCL
    // =====================================================================

    private sealed class IgclFanWriteHandle : IFanWriteHandle
    {
        private readonly string _deviceName;
        private readonly IntelFan _fan;

        public IgclFanWriteHandle(string deviceName, IntelFan fan)
        {
            _deviceName = deviceName;
            _fan = fan;
        }

        public FanWriteMode Mode { get; private set; } = FanWriteMode.Undefined;

        public float? ReadPercent()
        {
            try
            {
                lock (Sync)
                {
                    int percent = -1;
                    if (IntelIgcl.ctlFanGetState(_fan.Handle, IntelIgcl.CtlFanSpeedUnits.Percent, ref percent) != IntelIgcl.CtlResultSuccess)
                        return null;
                    return percent < 0 ? null : percent;
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
                int value = (int)Math.Round(Math.Clamp(percent, 0f, 100f));
                var speed = new IntelIgcl.CtlFanSpeed
                {
                    Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<IntelIgcl.CtlFanSpeed>(),
                    Version = 0,
                    Units = IntelIgcl.CtlFanSpeedUnits.Percent,
                    Speed = value
                };

                lock (Sync)
                {
                    int result = IntelIgcl.ctlFanSetFixedSpeedMode(_fan.Handle, ref speed);
                    if (result != IntelIgcl.CtlResultSuccess)
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
                    if (IntelIgcl.ctlFanSetDefaultMode(_fan.Handle) != IntelIgcl.CtlResultSuccess)
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

        public override string ToString() => $"IGCL {_deviceName} fan {_fan.Index + 1}";
    }
}
