using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;
using WHPO.Core.Services.Fan;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Control de ventiladores sobre LibreHardwareMonitor: los accesos al chip
/// SuperIO (ITE / Nuvoton) viajan por el driver de kernel PawnIO (el mismo que
/// usa LHM 0.9.6+ en lugar de WinRing0). El servicio mantiene su propia
/// instancia de Computer (solo Motherboard) separada de la del SensorService y
/// serializa todo acceso a registros con un lock (LHM no es thread-safe).
///
/// El modo manual usa IControl.SetSoftware(0-100): LHM traduce el porcentaje al
/// registro PWM del chip y guarda los valores originales para poder restaurar
/// el control del BIOS con SetDefault() (al volver a Auto o al cerrar la app).
///
/// DETECCIÓN: LHM detecta el chip SuperIO una sola vez, en Open(). Si en ese
/// instante el mutex global del bus ISA está tomado (otro monitor de hardware
/// corriendo, o nuestra propia instancia del SensorService abriendo a la vez),
/// LpcIO devuelve la lista VACÍA sin error y ese Computer jamás reintenta. Por
/// eso, si el Computer abrió sin SuperIO, se re-abre hasta N veces con espera.
///
/// INSTALACIÓN DE PAWNIO: mismo patrón que el driver de Overclock USB —
/// binario oficial de una versión FIJA con hash SHA-256 pinneado, copia
/// opcional junto al exe (Assets\Drivers) y caché verificada en AppData. Solo
/// se ejecuta un instalador cuyo hash coincide: nunca un "latest" que pueda
/// cambiar debajo de nuestros pies.
/// </summary>
public class FanControlService : IFanControlService
{
    /// <summary>Reintentos de detección del SuperIO cuando Open() vino vacío.</summary>
    private const int MaxRedetectAttempts = 3;
    private const int RedetectDelayMs = 1500;

    private const string LogTag = "FanControlService:";

    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private readonly IPawnIoService _pawnIoService;
    private readonly object _lock = new();
    private Computer? _computer;
    private bool _initFailed;
    private DateTime _lastAttempt = DateTime.MinValue;

    // Re-detección del SuperIO (ver comentario de clase).
    private int _redetectCount;
    private DateTime _lastRedetect = DateTime.MinValue;

    // Índice por id de sensor de fan -> (hardware dueño, handle de ESCRITURA, sensor).
    // El handle abstrae el backend: LHM (SuperIO de la placa, NVAPI en NVIDIA, ADL
    // en AMD) o los backends propios por fabricante (IGCL en Intel, ADL en AMD) para
    // los canales que LHM publica solo de lectura. Ver WHPO.Core.Services.Fan.
    private readonly Dictionary<string, (IHardware Hardware, IFanWriteHandle Control, ISensor Sensor)> _controlsById = new();
    private readonly Dictionary<string, (IHardware Hardware, ISensor Sensor)> _fansById = new();

    // Sensores de temperatura que pueden alimentar una curva, por id estable de LHM.
    // Se refresca con el hardware ya actualizado (ver RefreshTemperatureSourcesLocked);
    // es lo que resuelve el sensor elegido por el usuario en cada tick.
    private sealed record TemperatureSourceEntry(string Name, IHardware Hardware, ISensor Sensor);
    private readonly Dictionary<string, TemperatureSourceEntry> _temperatureSourcesById = new(StringComparer.Ordinal);

    // Aviso de sensor elegido que ya no existe: una vez por canal, no por tick.
    private readonly HashSet<string> _missingSourceWarned = new(StringComparer.Ordinal);

    // True si en esta sesión se puso algún canal en modo software (manual, curva
    // o calibración). El cierre solo abre el hardware para restaurar si hace falta:
    // un usuario que nunca tocó un ventilador no paga el escaneo del bus al salir.
    private bool _touchedChannels;

    // Curvas por canal: puntos ordenados por temperatura + flag activo.
    // internal (no private) para que el harness de verificación del motor de curvas
    // (tools/CurveEngineProbe) pueda ejercer la planificación real, sin duplicarla.
    internal sealed class FanCurveState
    {
        public List<FanCurvePoint> Points = new();
        public bool Enabled;

        // Sensor que alimenta esta curva (vacío = automático: CPU en la placa, GPU
        // en la video). Se copia de settings al crear el estado para que el tick no
        // dependa de una lectura de settings por canal y para poder cambiarlo en
        // caliente desde SetTemperatureSource.
        public string TemperatureSourceId = "";

        // Dinámica de aplicación (persistida por canal): los filtros entre la
        // temperatura medida y el duty escrito. Todo en 0 = sin filtros.
        public double HysteresisC;
        public double ResponseSeconds;
        public double StepUpPercent;
        public double StepDownPercent;

        // Seguimiento del motor (no se persiste): última temperatura y duty
        // escritos, el cambio que está esperando el tiempo de respuesta, y si hay
        // una escalera de subida/bajada en curso (la decisión ya está tomada y solo
        // falta llegar; no se vuelve a filtrar).
        public double? LastAppliedTemp;
        public float? LastAppliedDuty;
        public double? PendingDuty;
        public DateTime PendingSinceUtc;
        public bool CatchUp;

        // Fail-safe: ticks consecutivos sin lectura de temperatura. Con 5 (unos
        // 10 s en el motor) el canal se devuelve al BIOS y la curva se desactiva:
        // sin sensor no hay curva que valga, y el último duty escrito puede dejar
        // el ventilador clavado abajo con el hardware caliente.
        public int MissedTempTicks;
        public bool SensorLost;
    }
    private readonly Dictionary<string, FanCurveState> _curves = new();

    // Una calibración a la vez: dos barridos simultáneos pelearían por el chip.
    private int _calibrating;

    // El servicio ya se está soltando: un barrido en curso tiene que cortar, si no
    // volvería a escribir duty después de que Dispose dejó todo en manos del BIOS.
    private volatile bool _disposed;

    // Motor de curvas: aplica los duties interpolados fuera del hilo de UI.
    private Timer? _curveTimer;
    private int _curveBusy;
    private int _curveTickCounter;
    private bool _curveErrorLogged;

    private bool _readErrorLogged;

    // El diagnóstico de GPU se deja una sola vez por sesión (ver LogGpuDiagnostics).
    private bool _gpuDiagnosticsLogged;

    public FanControlService(
        ILoggingService loggingService,
        ISettingsService settingsService,
        IPawnIoService pawnIoService)
    {
        _loggingService = loggingService;
        _settingsService = settingsService;
        _pawnIoService = pawnIoService;
    }

    public FanControlStatus GetStatus()
    {
        EnsureInitialized();

        // El estado del driver vive en el servicio compartido: Sensores y
        // Ventiladores ven exactamente lo mismo, y la instalación no depende de
        // ninguno de los dos componentes.
        var pawnIo = _pawnIoService.GetStatus();
        bool installed = pawnIo.Installed;
        string? version = pawnIo.Version;

        string? chipName = null;
        int fanCount = 0;
        lock (_lock)
        {
            var channels = CollectChannels(update: true);
            fanCount = channels.Sum(c => c.Controls.Count + c.Fans.Count);
            var names = channels.Select(c => c.ChipName).Distinct().ToList();
            chipName = names.Count > 0 ? string.Join(" + ", names) : null;
        }

        return new FanControlStatus(
            PawnIOInstalled: installed,
            PawnIOVersion: version,
            HardwareAccessible: fanCount > 0,
            ChipName: chipName,
            FanCount: fanCount);
    }

    public List<FanControlInfo> GetFans()
    {
        EnsureInitialized();
        var list = new List<FanControlInfo>();
        if (_computer == null) return list;

        try
        {
            lock (_lock)
            {
                _controlsById.Clear();
                _fansById.Clear();

                var channels = CollectChannels(update: true);
                RefreshTemperatureSourcesLocked();
                LogGpuDiagnostics();

                foreach (var channel in channels)
                {
                    var owner = channel.Owner;
                    var chipName = channel.ChipName;
                    var isGpu = IsGpu(owner);

                    // 1) Canales con control PWM: el sensor de control lleva el
                    //    nombre del fan y su IControl escribe el duty. Igual en el
                    //    SuperIO (PawnIO) y en las GPU: LHM publica el canal de
                    //    control con NVAPI en NVIDIA (NvAPI_GPU_SetCoolerLevels) y
                    //    con ADL Overdrive5 en AMD (ADL2_Overdrive5_FanSpeed_Set).
                    //    Intel queda afuera: su implementación (IGCL) solo publica
                    //    RPM, así que cae en el caso 2 de más abajo.
                    foreach (var c in channel.Controls)
                    {
                        var id = c.Identifier?.ToString() ?? $"{chipName}/control/{c.Index}";
                        var rpm = channel.Fans.FirstOrDefault(f => f.Index == c.Index)?.Value;
                        var channelTemp = ReferenceTemperatureC(owner, id, out var channelSource, out var channelSynthetic);
                        var selectedSource = GetTemperatureSource(id);
                        var control = c.Control;
                        IFanWriteHandle? write = control != null ? new LhmFanWriteHandle(control) : null;

                        if (write != null)
                            _controlsById[id] = (owner, write, c);
                        if (rpm.HasValue)
                            _fansById[id] = (owner, c);

                        list.Add(new FanControlInfo(
                            Id: id,
                            ChipName: chipName,
                            Name: DisplayName(id, c.Name),
                            Rpm: rpm,
                            HasControl: write != null,
                            IsManual: write?.Mode == FanWriteMode.Software,
                            // Duty REAL del canal: tras el Update, el valor del
                            // sensor Control refleja el registro PWM del chip
                            // (superIO.Controls[i] ya viene en % 0-100, ver
                            // NCT677X: value/2.55f). Es el "% de uso" en vivo del
                            // ventilador, igual que muestra FanControl.
                            DutyPercent: c.Value,
                            IsGpu: isGpu,
                            Temperature: channelTemp,
                            IsCurveActive: _curves.TryGetValue(id, out var cs) && cs.Enabled,
                            RawName: c.Name,
                            HasCustomName: HasCustomName(id),
                            TemperatureSourceId: selectedSource,
                            TemperatureSourceName: channelSource,
                            TemperatureSourceSynthetic: channelSynthetic));
                    }

                    // 2) Canales SIN control en LHM: GPUs Intel (IGCL solo publica
                    //    RPM; LHM 0.9.6 no crea canal de control), GPUs AMD cuando LHM
                    //    no llega a activar su canal, y headers de placa sin PWM.
                    //    Si el fabricante tiene backend propio de escritura (Intel por
                    //    IGCL, AMD por ADL), el canal pasa a ser controlable con el
                    //    MISMO id: la card y los botones no cambian, solo deja de
                    //    decir "Solo lectura".
                    foreach (var f in channel.Fans.Where(f => channel.Controls.All(c => c.Index != f.Index)))
                    {
                        var id = f.Identifier?.ToString() ?? $"{chipName}/fan/{f.Index}";
                        var channelTemp = ReferenceTemperatureC(owner, id, out var channelSource, out var channelSynthetic);
                        var selectedSource = GetTemperatureSource(id);
                        IFanWriteHandle? vendorWrite = isGpu
                            ? GpuFanVendor.TryGetWriteHandle(owner, f.Index, _loggingService)
                            : null;

                        if (vendorWrite != null)
                            _controlsById[id] = (owner, vendorWrite, f);
                        _fansById[id] = (owner, f);
                        list.Add(new FanControlInfo(
                            Id: id,
                            ChipName: chipName,
                            Name: DisplayName(id, f.Name),
                            Rpm: f.Value,
                            HasControl: vendorWrite != null,
                            IsManual: vendorWrite?.Mode == FanWriteMode.Software,
                            DutyPercent: vendorWrite?.ReadPercent(),
                            IsGpu: isGpu,
                            Temperature: channelTemp,
                            IsCurveActive: _curves.TryGetValue(id, out var cs2) && cs2.Enabled,
                            RawName: f.Name,
                            HasCustomName: HasCustomName(id),
                            TemperatureSourceId: selectedSource,
                            TemperatureSourceName: channelSource,
                            TemperatureSourceSynthetic: channelSynthetic));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_readErrorLogged)
            {
                _readErrorLogged = true;
                _loggingService.LogWarning($"{LogTag} error leyendo ventiladores: {ex.Message}");
            }
        }

        return list;
    }

    public bool SetManualDuty(string fanId, float percent)
    {
        EnsureInitialized();
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;

        lock (_lock)
        {
            if (!_controlsById.TryGetValue(fanId, out var entry))
            {
                GetFans(); // rellena el índice de ids
                if (!_controlsById.TryGetValue(fanId, out entry))
                    return false;
            }

            try
            {
                entry.Hardware.Update();
                // SetSoftware pone el canal en modo software (manual) y escribe
                // el duty; guarda el modo/valor originales para SetDefault().
                // El handle es de LHM (SuperIO/NVAPI/ADL de LHM) o del backend del
                // fabricante (IGCL/ADL propios): si el backend rechaza la escritura,
                // no se reporta como aplicada.
                if (!entry.Control.SetSoftware(percent))
                {
                    _loggingService.LogWarning($"{LogTag} {fanId}: el backend rechazó el duty {percent:0}%");
                    return false;
                }
                _touchedChannels = true;
                _loggingService.LogInfo($"{LogTag} {fanId} duty manual {percent:0}%");
                return true;
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"{LogTag} SetManualDuty {fanId} falló: {ex.Message}");
                return false;
            }
        }
    }

    public bool SetAuto(string fanId)
    {
        lock (_lock)
        {
            if (!_controlsById.TryGetValue(fanId, out var entry))
            {
                GetFans(); // rellena el índice de ids
                if (!_controlsById.TryGetValue(fanId, out entry))
                    return false;
            }

            try
            {
                if (!entry.Control.SetDefault())
                {
                    _loggingService.LogWarning($"{LogTag} {fanId}: el backend rechazó volver al control automático");
                    return false;
                }
                _loggingService.LogInfo($"{LogTag} {fanId} devuelto al control del BIOS");
                return true;
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"{LogTag} SetAuto {fanId} falló: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// True si hay curvas para reaplicar al arrancar: algún flag autoStart en true
    /// con puntos válidos (≥2) en settings. Solo lee settings.json: NO abre el
    /// hardware (LHM/Computer), así el arranque no toca el bus ISA cuando no hay
    /// nada que reaplicar. Puerta de entrada barata antes de ReapplyCurvesOnStartup.
    /// </summary>
    public bool HasCurvesToReapply()
    {
        try
        {
            foreach (var key in _settingsService.GetKeys(CurveAutoStartPrefix))
            {
                var fanId = key.Substring(CurveAutoStartPrefix.Length);
                if (string.IsNullOrEmpty(fanId)) continue;
                if (!_settingsService.Get(key, false)) continue;
                // Curva en memoria de esta sesión (ya tocada): manda su estado.
                lock (_lock)
                {
                    if (_curves.TryGetValue(fanId, out var cs))
                    {
                        if (cs.Enabled && cs.Points.Count >= 2) return true;
                        continue;
                    }
                }
                // Persistida de una sesión anterior: puntos guardados.
                if (ParseCurve(_settingsService.Get(CurvePrefix + fanId, string.Empty)).Count >= 2)
                    return true;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"{LogTag} chequeo de curvas al iniciar falló: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Reaplica las curvas que estaban activas cuando se cerró la app. El apagado
    /// devuelve los canales al BIOS a propósito (para no dejarlos con un duty fijo),
    /// así que el recuerdo vive en un flag aparte que sí sobrevive. Devuelve cuántas
    /// se pudieron reaplicar.
    /// </summary>
    public int ReapplyCurvesOnStartup()
    {
        // El hardware puede tardar en levantar al arrancar (driver recién cargado,
        // bus ISA ocupado): se reintenta un rato antes de darse por vencido. Los
        // flags quedan intactos, así que el próximo arranque vuelve a intentar.
        List<FanControlInfo> fans = new();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try { fans = GetFans(); }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"{LogTag} reaplicar al iniciar: no se pudo leer el hardware: {ex.Message}");
                return 0;
            }

            if (fans.Any(f => f.HasControl)) break;
            if (attempt < 3)
            {
                _loggingService.LogDebug($"{LogTag} reaplicar al iniciar: sin canales controlables (intento {attempt}/3), reintento");
                Thread.Sleep(2000);
            }
        }

        var controllable = fans.Where(f => f.HasControl).Select(f => f.Id).ToList();
        if (controllable.Count == 0)
        {
            _loggingService.LogInfo($"{LogTag} reaplicar al iniciar: sin canales controlables (driver o hardware no disponible)");
            return 0;
        }

        int applied = 0;
        foreach (var id in controllable)
        {
            if (!_settingsService.Get(CurveAutoStartPrefix + id, false)) continue;

            var points = GetFanCurve(id);
            // Una curva sin puntos suficientes no puede tomar el canal.
            if (points.Count < 2) continue;

            try
            {
                SetFanCurve(id, points, enabled: true);
                ClearSensorLost(id); // el reaplicado resuelve el estado de sensor perdido
                _loggingService.LogInfo($"{LogTag} curva de {id} reaplicada al iniciar");
                applied++;
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"{LogTag} no se pudo reaplicar la curva de {id}: {ex.Message}");
            }
        }

        if (applied > 0)
            _loggingService.LogInfo($"{LogTag} curvas reaplicadas al iniciar: {applied}");
        return applied;
    }

    public void RestoreAll()
    {
        var disabledCurves = new List<string>();
        lock (_lock)
        {
            if (_controlsById.Count == 0)
                GetFans();

            foreach (var id in _controlsById.Keys.ToList())
            {
                try
                {
                    _controlsById[id].Control.SetDefault();
                }
                catch
                {
                    // Mejor esfuerzo: si el canal ya no existe, se ignora.
                }
            }

            // "Todos al automático (BIOS)" apaga también las curvas activas:
            // si quedaran habilitadas, el motor re-aplicaría el duty al tick.
            foreach (var (id, state) in _curves)
            {
                if (state.Enabled)
                {
                    state.Enabled = false;
                    disabledCurves.Add(id);
                }
            }
            _touchedChannels = false;
            _loggingService.LogInfo($"{LogTag} todos los canales devueltos al BIOS");
        }

        foreach (var id in disabledCurves)
        {
            _settingsService.Set(CurveEnabledPrefix + id, false);
            // Devolver todo al BIOS es una decisión explícita: no se reaplica al abrir.
            _settingsService.Set(CurveAutoStartPrefix + id, false);
        }
        if (disabledCurves.Count > 0)
            _settingsService.Save();
    }

    /// <summary>
    /// Reanuda el control al volver de una suspensión. Dormir la PC rompe dos cosas de
    /// las que dependemos: el firmware puede devolver los canales al modo BIOS (el duty
    /// que escribimos se pierde) y el acceso al chip a través de LHM/PawnIO puede quedar
    /// apuntando a un estado que ya no existe. Por eso se reabre el hardware y se
    /// re-aplican las curvas de inmediato, sin esperar al próximo tick (hasta 2 s).
    /// </summary>
    public void ResumeFromSleep()
    {
        int active;
        lock (_lock) active = _curves.Count(c => c.Value.Enabled);

        _loggingService.LogInfo($"{LogTag} reanudado de suspensión: revalidando el control ({active} curva(s) activa(s))");

        try
        {
            Reinitialize();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"{LogTag} revalidar tras la suspensión falló: {ex.Message}");
        }

        // Reinitialize() vacía el índice de canales: hay que reconstruirlo antes de
        // poder reescribir un duty (y de paso refresca las lecturas del chip).
        try { GetFans(); } catch { }

        // El flag de error es de por vida del proceso: tras dormir, un fallo nuevo
        // merece registrarse.
        _curveErrorLogged = false;

        if (active > 0)
        {
            // Dormir puede haber dejado el chip reprogramado por el BIOS: se olvida
            // el seguimiento para que el tick vuelva a escribir el duty de cada curva
            // activa, sin que la histéresis lo saltee por creer que ya está aplicado.
            lock (_lock)
            {
                foreach (var state in _curves.Values.Where(c => c.Enabled))
                    ResetCurveTracking(state);
            }

            try { CurveTick(); } catch { }
            _loggingService.LogInfo($"{LogTag} tras la suspensión: {active} curva(s) reaplicadas");
        }
    }

    public void Reinitialize()
    {
        lock (_lock)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
            _controlsById.Clear();
            _fansById.Clear();
            _initFailed = false;
            _lastAttempt = DateTime.MinValue;
            _redetectCount = 0;
        }
        // Los handles del driver (IGCL/ADL) pertenecen al estado anterior: se
        // descartan y se vuelven a abrir en la próxima consulta.
        GpuFanVendor.ResetSessions();

        EnsureInitialized();
        _loggingService.LogInfo($"{LogTag} hardware reinicializado");
    }

    /// <summary>
    /// Compatibilidad: la instalación real vive en <see cref="IPawnIoService"/>.
    /// Se conserva porque la página de Ventiladores y los componentes externos
    /// pueden llamar a este método; así el driver sigue siendo uno solo.
    /// </summary>
    public Task<PawnIOInstallResult> InstallPawnIOSilentAsync()
        => _pawnIoService.InstallAsync();

    public void Dispose()
    {
        _disposed = true;

        // El motor se detiene ANTES de restaurar: si siguiera vivo, el próximo tick
        // volvería a escribir el duty que estamos soltando.
        lock (_lock)
        {
            try { _curveTimer?.Dispose(); } catch { }
            _curveTimer = null;
        }

        // Al cerrar, los canales vuelven al BIOS pero las curvas y su autostart se
        // CONSERVAN: cerrar la app no es "soltar las curvas". El botón "Todos al
        // automático" usa RestoreAll, que sí las apaga (decisión explícita).
        try { RestoreChannelsToBios(); } catch { }

        lock (_lock)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
        // Los backends propios de GPU (IGCL/ADL) mantienen su sesión con el driver:
        // se cierran recién acá, después de devolver los canales a automático.
        GpuFanVendor.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Devuelve TODOS los canales al BIOS sin tocar las curvas ni el autostart.
    /// Es el camino del apagado: la máquina no puede quedar con un duty fijo, pero
    /// las curvas tienen que volver a aplicarse en el próximo arranque.
    /// Para la acción explícita del usuario está <see cref="RestoreAll"/>, que además
    /// apaga las curvas.
    /// </summary>
    private void RestoreChannelsToBios()
    {
        lock (_lock)
        {
            // Sin ningún duty nuestro escrito, no hay nada que soltar y no vale
            // la pena abrir el hardware (bus ISA lento) durante el apagado.
            if (!_touchedChannels) return;

            if (_controlsById.Count == 0)
            {
                try { GetFans(); } catch { }
            }

            foreach (var id in _controlsById.Keys.ToList())
            {
                try { _controlsById[id].Control.SetDefault(); } catch { }
            }

            _touchedChannels = false;
            _loggingService.LogInfo($"{LogTag} canales devueltos al BIOS al cerrar (curvas conservadas)");
        }
    }

    // =====================================================================
    // Inicialización y re-detección del SuperIO
    // =====================================================================

    private static Computer NewComputer() => new()
    {
        IsMotherboardEnabled = true,
        // GPU: los ventiladores de la placa de video NO son SuperIO — viven en
        // el controlador de la GPU. LHM los expone con la API de cada fabricante:
        // NVAPI en NVIDIA (lectura de RPM + escritura de duty vía
        // NvAPI_GPU_SetCoolerLevels), ADL Overdrive5 en AMD (lectura + escritura
        // vía ADL2_Overdrive5_FanSpeed_Set) e IGCL en Intel (solo lectura en LHM:
        // la escritura la aporta el backend propio, ver WHPO.Core.Services.Fan).
        // Sin esto, la pestaña no ve los ventiladores de la gráfica.
        IsGpuEnabled = true,
        // CPU: la curva de los headers de la placa necesita la temperatura del
        // paquete del CPU como referencia (el cooler cuelga de ahí).
        IsCpuEnabled = true,
        IsMemoryEnabled = false,
        IsStorageEnabled = false,
        IsControllerEnabled = false
    };

    private void EnsureInitialized()
    {
        if (_computer == null)
        {
            if (_initFailed && (DateTime.Now - _lastAttempt).TotalSeconds < 10) return;

            lock (_lock)
            {
                if (_computer == null && (!_initFailed || (DateTime.Now - _lastAttempt).TotalSeconds >= 10))
                {
                    _lastAttempt = DateTime.Now;
                    try
                    {
                        var computer = NewComputer();
                        computer.Open();
                        _computer = computer;
                        _initFailed = false;
                        _loggingService.LogInfo($"{LogTag} LibreHardwareMonitor inicializado (SuperIO de la placa vía PawnIO + GPUs por NVAPI/ADL/IGCL)");
                    }
                    catch (Exception ex)
                    {
                        _initFailed = true;
                        _loggingService.LogWarning($"{LogTag} LibreHardwareMonitor no disponible: {ex.Message}");
                    }
                }
            }
        }

        // El Computer abrió pero no hay ningún chip SuperIO: la detección de
        // LHM corre una sola vez en Open() y falla en silencio si el bus ISA
        // estaba ocupado. Re-abrir con espera (tope de intentos).
        if (_computer != null && !SuperIoDetected())
        {
            lock (_lock)
            {
                if (_computer == null || SuperIoDetectedLocked()) return;
                if (_redetectCount >= MaxRedetectAttempts) return;
                if ((DateTime.Now - _lastRedetect).TotalMilliseconds < RedetectDelayMs) return;

                _lastRedetect = DateTime.Now;
                _redetectCount++;
                _loggingService.LogInfo($"{LogTag} sin chip SuperIO detectado (bus ISA ocupado en Open?): reintentando {_redetectCount}/{MaxRedetectAttempts}");

                try { _computer.Close(); } catch { }
                _computer = null;

                // Los handles del driver (IGCL/ADL) se sueltan ANTES de reabrir: con una
                // sesión propia abierta, el ctlInit de LHM devuelve "ya abierta por otro
                // llamador" y LHM trata eso como fallo, con lo que perdería los sensores
                // de la GPU Intel. Soltando primero, LHM la reabre y nosotros después.
                GpuFanVendor.ResetSessions();

                try
                {
                    var computer = NewComputer();
                    computer.Open();
                    _computer = computer;
                    _initFailed = false;
                }
                catch (Exception ex)
                {
                    _initFailed = true;
                    _loggingService.LogWarning($"{LogTag} re-apertura de LibreHardwareMonitor falló: {ex.Message}");
                }
            }
        }
    }

    private bool SuperIoDetected()
    {
        lock (_lock) return SuperIoDetectedLocked();
    }

    // Requiere _lock tomado. True si el nodo Motherboard tiene sub-hardware
    // (los chips SuperIO detectados por LpcIO); al detectarlos resetea el
    // contador de reintentos.
    private bool SuperIoDetectedLocked()
    {
        if (_computer == null) return false;
        foreach (var hw in _computer.Hardware)
        {
            if (hw.SubHardware.Any())
            {
                _redetectCount = 0;
                return true;
            }
        }
        return false;
    }

    // =====================================================================
    // Enumeración de canales de ventilador (SuperIO + GPU)
    // =====================================================================

    private sealed record FanChannel(IHardware Owner, string ChipName, List<ISensor> Controls, List<ISensor> Fans);

    // Reúne, por "chip", los sensores Control y Fan de: (a) el sub-hardware
    // SuperIO del Motherboard y (b) cada GPU (sensores en el propio hardware).
    // update=false solo enumera lo ya activado; update=true refresca lecturas
    // (necesario: LHM "activa" los sensores de RPM tras el primer Update).
    // Requiere _lock tomado.
    private List<FanChannel> CollectChannels(bool update)
    {
        var list = new List<FanChannel>();
        if (_computer == null) return list;

        foreach (var hw in _computer.Hardware)
        {
            if (update) { try { hw.Update(); } catch { } }
            AddChannels(list, hw, hw.Name);

            foreach (var sub in hw.SubHardware)
            {
                if (update) { try { sub.Update(); } catch { } }
                AddChannels(list, sub, sub.Name);
            }
        }

        return list;
    }

    private static void AddChannels(List<FanChannel> list, IHardware owner, string chipName)
    {
        var controls = owner.Sensors.Where(s => s.SensorType == SensorType.Control).ToList();
        var fans = owner.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
        if (controls.Count > 0 || fans.Count > 0)
            list.Add(new FanChannel(owner, chipName, controls, fans));
    }

    // True si el dueño del canal es una GPU (los fans de video no son SuperIO).
    private static bool IsGpu(IHardware hw) =>
        hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// <summary>Nombre de fabricante para el log (LHM expone el tipo, no un string).</summary>
    private static string GpuVendorName(HardwareType type) => type switch
    {
        HardwareType.GpuNvidia => "NVIDIA",
        HardwareType.GpuAmd => "AMD",
        HardwareType.GpuIntel => "Intel",
        _ => type.ToString()
    };

    /// <summary>
    /// True si atiadlxx.dll (la librería ADL del driver de AMD) está en System32.
    /// Sin ella LHM no puede abrir ni leer/escribir los ventiladores de una GPU
    /// AMD: el grupo AMD no se crea y la gráfica directamente no aparece.
    /// </summary>
    private static bool IsAmdAdlPresent()
    {
        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return File.Exists(Path.Combine(system32, "atiadlxx.dll"));
        }
        catch { return false; }
    }

    /// <summary>
    /// Deja en el log qué expone cada GPU para el control de ventiladores. Los tres
    /// casos que se ven iguales en la UI ("la GPU no aparece" o "es de solo lectura")
    /// se separan acá:
    ///   - NVIDIA sin cooler settings: driver viejo o API no disponible.
    ///   - AMD sin canal de control: la tarjeta/driver no soporta ADL Overdrive, o
    ///     falta atiadlxx.dll (driver de AMD incompleto). LHM solo activa el canal
    ///     cuando _overdriveApiSupported es true; si no lo activa, el backend propio
    ///     (ADL Overdrive5) igual da escritura cuando la tarjeta la soporta.
    ///   - Intel: LHM solo publica RPM (IGCL sin canal de control). La escritura la
    ///     aporta el backend propio por IGCL; si el driver no reporta modo fijo
    ///     (ctlFanGetProperties), el canal queda "Solo lectura" y el log lo dice.
    /// Se ejecuta una sola vez por sesión. Requiere _lock tomado.
    /// </summary>
    private void LogGpuDiagnostics()
    {
        if (_gpuDiagnosticsLogged) return;
        _gpuDiagnosticsLogged = true;
        if (_computer == null) return;

        try
        {
            bool anyGpu = false;
            foreach (var hw in _computer.Hardware)
            {
                if (!IsGpu(hw)) continue;
                anyGpu = true;

                var vendor = GpuVendorName(hw.HardwareType);
                var controls = hw.Sensors.Count(s => s.SensorType == SensorType.Control);
                var fans = hw.Sensors.Count(s => s.SensorType == SensorType.Fan);

                // El backend propio del fabricante es el que decide si un canal sin
                // control en LHM se puede controlar igual (IGCL en Intel, ADL en AMD).
                var backend = GpuFanVendor.DescribeBackend(hw, _loggingService);

                if (controls > 0)
                {
                    _loggingService.LogInfo(
                        $"{LogTag} GPU {vendor} '{hw.Name}': {controls} canal(es) de control + {fans} de RPM " +
                        $"(API {GpuControlApi(vendor)}; backend {backend}).");
                    continue;
                }

                // Sin canal de control: se explica el motivo concreto del fabricante.
                var hint = hw.HardwareType switch
                {
                    HardwareType.GpuAmd => IsAmdAdlPresent()
                        ? "atiadlxx.dll presente: la tarjeta o el driver no reporta soporte de Overdrive (ADL2_Overdrive_Caps)."
                        : "atiadlxx.dll AUSENTE en System32: falta el driver de AMD completo (solo hay driver básico de Windows).",
                    HardwareType.GpuNvidia => "el driver no expone cooler settings (NVAPI_GPU_GetCoolerSettings/FanCoolers).",
                    HardwareType.GpuIntel => "LHM no publica canal de control para Intel (solo RPM por IGCL); " +
                        "la escritura depende del backend propio (IGCL/ctlFanSetFixedSpeedMode) según lo que reporte el driver.",
                    _ => "el driver no expone canal de control."
                };

                _loggingService.LogWarning(
                    $"{LogTag} GPU {vendor} '{hw.Name}': sin canal de control en LHM ({fans} sensor(es) de RPM). " +
                    $"Motivo: {hint} Backend propio: {backend}.");
            }

            if (!anyGpu)
                _loggingService.LogWarning(
                    $"{LogTag} no se detectó ninguna GPU por LHM (¿driver del fabricante ausente o bloqueado por otro monitor de hardware?).");
        }
        catch { }
    }

    /// <summary>API que usa LHM para el control de duty de cada fabricante (para el log).</summary>
    private static string GpuControlApi(string vendor) => vendor switch
    {
        "NVIDIA" => "NVAPI/NvAPI_GPU_SetCoolerLevels",
        "AMD" => "ADL Overdrive5/ADL2_Overdrive5_FanSpeed_Set",
        "Intel" => "IGCL (lectura; escritura por backend propio)",
        _ => "desconocida"
    };

    // =====================================================================
    // Curvas de ventilador (temperatura → duty, con motor interno)
    // =====================================================================

    /// <summary>
    /// Temperatura de referencia del canal (°C) para las curvas: el sensor elegido
    /// por el usuario o, si no hay ninguno, el automático (paquete del CPU para los
    /// headers de la placa, núcleo de la GPU para los canales de video). Devuelve
    /// también el nombre del sensor usado y si es virtual, para que la card muestre
    /// contra qué está reaccionando.
    /// Requiere _lock tomado (usa _computer y el índice de sensores).
    /// </summary>
    private double? ReferenceTemperatureC(IHardware owner, string fanId, out string? sourceName, out bool synthetic)
    {
        sourceName = null;
        synthetic = false;
        try
        {
            // El estado en memoria manda: es el que SetTemperatureSource actualiza
            // en caliente. Para un canal sin curva todavía se lee de settings.
            string selected;
            lock (_lock)
            {
                selected = _curves.TryGetValue(fanId, out var state)
                    ? state.TemperatureSourceId
                    : GetTemperatureSource(fanId);
            }

            if (!string.IsNullOrEmpty(selected))
            {
                if (selected == MixMaxCpuGpuId)
                {
                    sourceName = MixMaxCpuGpuName;
                    synthetic = true;
                    return MaxCpuGpuTemperatureC();
                }

                if (_temperatureSourcesById.TryGetValue(selected, out var entry))
                {
                    sourceName = entry.Name;
                    return entry.Sensor.Value;
                }

                // El sensor guardado ya no está (GPU apagada, hardware cambiado):
                // se cae al automático en vez de soltar el canal. Se avisa una vez
                // por canal para no llenar el log en cada tick.
                if (_missingSourceWarned.Add(fanId))
                    _loggingService.LogWarning($"{LogTag} {fanId}: el sensor guardado '{selected}' no está disponible; se usa el automático");
            }

            if (IsGpu(owner))
            {
                var sensor = GpuTemperatureSensor(owner);
                sourceName = sensor?.Name;
                return sensor?.Value;
            }

            var cpuSensor = CpuTemperatureSensor();
            sourceName = cpuSensor?.Name;
            return cpuSensor?.Value;
        }
        catch
        {
            // Sin lectura de temperatura: la curva no aplica duty extra.
            return null;
        }
    }

    // Sensor de temperatura de la GPU: el mismo que usa la app como referencia
    // automática (prefiere "GPU Core").
    private static ISensor? GpuTemperatureSensor(IHardware gpu)
        => gpu.Sensors
            .Where(s => s.SensorType == SensorType.Temperature)
            .OrderBy(s => s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();

    // Sensor de temperatura del CPU: paquete/CCD/Tctl y, si no hay, la lectura más
    // alta (más representativa que el promedio para gobernar un cooler).
    private ISensor? CpuTemperatureSensor()
    {
        if (_computer == null) return null;
        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType != HardwareType.Cpu) continue;
            var sensor = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature &&
                (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains("CCD", StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)));
            if (sensor != null) return sensor;
            return hw.Sensors
                .Where(s => s.SensorType == SensorType.Temperature)
                .OrderByDescending(s => s.Value ?? double.MinValue)
                .FirstOrDefault();
        }
        return null;
    }

    // El virtual de los fans de caja: el componente más caliente de CPU y GPU.
    private double? MaxCpuGpuTemperatureC()
    {
        double? max = null;
        foreach (var entry in _temperatureSourcesById.Values)
        {
            if (entry.Hardware.HardwareType != HardwareType.Cpu && !IsGpu(entry.Hardware)) continue;
            if (entry.Sensor.Value is not float value) continue;
            max = max.HasValue ? Math.Max(max.Value, value) : value;
        }

        if (max.HasValue) return max;

        // Índice vacío (arranque muy temprano): se resuelve contra el hardware.
        var cpu = CpuTemperatureSensor()?.Value;
        if (cpu.HasValue) max = cpu;
        if (_computer != null)
        {
            foreach (var hw in _computer.Hardware.Where(IsGpu))
            {
                foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Temperature))
                {
                    if (s.Value is not float value) continue;
                    max = max.HasValue ? Math.Max(max.Value, value) : value;
                }
            }
        }
        return max;
    }

    /// <summary>
    /// Reconstruye el índice de sensores con el hardware YA actualizado (no hace
    /// Update propio: el llamador lo hizo). Requiere _lock tomado.
    /// </summary>
    private void RefreshTemperatureSourcesLocked()
    {
        _temperatureSourcesById.Clear();
        if (_computer == null) return;

        foreach (var hw in AllHardware())
        {
            foreach (var sensor in hw.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;
                // "Distance to Tjmax" es un margen hasta el límite, no una temperatura
                // absoluta: usarlo en una curva la haría reaccionar al revés.
                if (IsThermalMargin(sensor)) continue;
                var id = sensor.Identifier?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                _temperatureSourcesById[id] = new TemperatureSourceEntry(FriendlySensorName(hw, sensor), hw, sensor);
            }
        }
    }

    private static bool IsThermalMargin(ISensor sensor) =>
        sensor.Name.Contains("Distance to Tjmax", StringComparison.OrdinalIgnoreCase) ||
        sensor.Name.Contains("Thermal Margin", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<IHardware> AllHardware()
    {
        if (_computer == null) yield break;
        foreach (var hw in _computer.Hardware)
        {
            yield return hw;
            foreach (var sub in hw.SubHardware) yield return sub;
        }
    }

    private static string FriendlySensorName(IHardware hw, ISensor sensor)
        => hw.HardwareType switch
        {
            // CPU y GPU ya traen nombres claros ("CPU Package", "GPU Core").
            HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => sensor.Name,
            // En la placa el sensor suele ser genérico ("Temperature #3"): se
            // antepone el chip para poder distinguirlo.
            _ => $"{hw.Name} · {sensor.Name}"
        };

    // Clasificación del sensor por rol, para ordenar el desplegable y mostrar el
    // grupo delante del nombre cuando el nombre no lo dice por sí solo.
    private static FanTemperatureGroup ClassifyTemperatureSource(IHardware hw, ISensor sensor)
    {
        if (hw.HardwareType == HardwareType.Cpu) return FanTemperatureGroup.Cpu;
        if (IsGpu(hw)) return FanTemperatureGroup.Gpu;
        if (sensor.Name.Contains("VRM", StringComparison.OrdinalIgnoreCase)) return FanTemperatureGroup.Vrm;
        if (hw.HardwareType == HardwareType.Motherboard) return FanTemperatureGroup.Motherboard;
        return FanTemperatureGroup.Other;
    }

    private static int GroupOrder(FanTemperatureGroup group) => group switch
    {
        FanTemperatureGroup.Cpu => 0,
        FanTemperatureGroup.Gpu => 1,
        FanTemperatureGroup.Vrm => 2,
        FanTemperatureGroup.Motherboard => 3,
        _ => 4
    };

    // Dentro de cada grupo, primero el sensor que mejor representa al componente:
    // paquete del CPU, núcleo de la GPU, VRM y sensor T antes que los genéricos.
    private int SourceRoleOrder(string sourceId)
    {
        if (!_temperatureSourcesById.TryGetValue(sourceId, out var entry)) return 99;
        var name = entry.Sensor.Name;

        if (entry.Hardware.HardwareType == HardwareType.Cpu)
        {
            if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("CCD", StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.Contains("Core", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        if (IsGpu(entry.Hardware))
        {
            if (name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase)) return 1;
            if (name.Contains("Memory", StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        if (name.Contains("VRM", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("T_Sensor", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("T Sensor", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("System", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Chipset", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    /// <summary>
    /// Piso de duty de una curva. El rango entre 1% y el piso de arranque del
    /// ventilador es el peor de los dos mundos: el canal queda energizado, la app
    /// cree que está enfriando y el ventilador no gira. Se saltea entero: cualquier
    /// pedido mayor que cero sube al piso, y 0 sigue siendo apagar.
    ///
    /// Static e internal para poder ejercitarlo sin hardware, igual que
    /// <see cref="PlanCurveDuty"/> y <see cref="CurveMissingTemp"/>.
    /// </summary>
    internal static float ApplyDutyFloor(float duty, double floorPercent)
    {
        if (duty <= 0) return 0;
        if (floorPercent <= 0) return duty;

        var floor = (float)Math.Clamp(floorPercent, 0, 100);
        return duty >= floor ? duty : floor;
    }

    /// <summary>Duty interpolado linealmente en la curva del canal (null si la curva no aplica).</summary>
    internal static double? Interpolate(IReadOnlyList<FanCurvePoint> points, double tempC)
    {
        if (points.Count == 0) return null;

        // Antes del primer punto y después del último: clamp al duty del extremo.
        if (tempC <= points[0].TemperatureC) return points[0].DutyPercent;
        var last = points[^1];
        if (tempC >= last.TemperatureC) return last.DutyPercent;

        for (int i = 1; i < points.Count; i++)
        {
            if (tempC <= points[i].TemperatureC)
            {
                var a = points[i - 1];
                var b = points[i];
                var span = b.TemperatureC - a.TemperatureC;
                if (span <= 0) return b.DutyPercent;
                var t = (tempC - a.TemperatureC) / span;
                return a.DutyPercent + t * (b.DutyPercent - a.DutyPercent);
            }
        }
        return last.DutyPercent;
    }

    public List<FanCurvePoint> GetFanCurve(string fanId)
    {
        lock (_lock)
        {
            if (_curves.TryGetValue(fanId, out var cs))
                return new List<FanCurvePoint>(cs.Points);
        }
        // Persistida de una sesión anterior (todavía no tocada en esta sesión).
        return ParseCurve(_settingsService.Get(CurvePrefix + fanId, string.Empty));
    }

    public bool IsFanCurveEnabled(string fanId)
    {
        lock (_lock)
        {
            if (_curves.TryGetValue(fanId, out var cs)) return cs.Enabled;
        }
        return _settingsService.Get(CurveEnabledPrefix + fanId, false);
    }

    public FanCurveDynamics GetCurveDynamics(string fanId)
    {
        lock (_lock)
        {
            if (_curves.TryGetValue(fanId, out var cs))
                return new FanCurveDynamics(cs.HysteresisC, cs.ResponseSeconds, cs.StepUpPercent, cs.StepDownPercent);
        }
        return LoadCurveDynamics(fanId);
    }

    private const string CurveProfilePrefix = "fan.curve.profile.";

    // Piso de duty por canal (el "Start %" de FanControl). Fuera de las claves de
    // dinámica porque no es un filtro de la curva: es un límite del ventilador.
    private const string MinDutyPrefix = "fan.minduty.";

    public string GetCurveProfile(string fanId)
    {
        try { return _settingsService.Get(CurveProfilePrefix + fanId, string.Empty); }
        catch { return string.Empty; }
    }

    public void SetCurveProfile(string fanId, string profileKey)
    {
        try
        {
            _settingsService.Set(CurveProfilePrefix + fanId, profileKey ?? string.Empty);
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"{LogTag} guardar perfil de {fanId} falló: {ex.Message}");
        }
    }

    public void SetCurveDynamics(string fanId, FanCurveDynamics dynamics)
    {
        var clean = new FanCurveDynamics(
            Math.Clamp(dynamics.HysteresisC, 0, 20),
            Math.Clamp(dynamics.ResponseSeconds, 0, 60),
            Math.Clamp(dynamics.StepUpPercent, 0, 100),
            Math.Clamp(dynamics.StepDownPercent, 0, 100));

        lock (_lock)
        {
            // Si el canal ya está en memoria, la nueva dinámica rige desde el
            // próximo tick: se olvida el seguimiento para no arrastrar decisiones
            // tomadas con los valores viejos.
            if (_curves.TryGetValue(fanId, out var state))
            {
                state.HysteresisC = clean.HysteresisC;
                state.ResponseSeconds = clean.ResponseSeconds;
                state.StepUpPercent = clean.StepUpPercent;
                state.StepDownPercent = clean.StepDownPercent;
                ResetCurveTracking(state);
            }
        }

        _settingsService.Set(CurveHysteresisPrefix + fanId, clean.HysteresisC);
        _settingsService.Set(CurveResponsePrefix + fanId, clean.ResponseSeconds);
        _settingsService.Set(CurveStepUpPrefix + fanId, clean.StepUpPercent);
        _settingsService.Set(CurveStepDownPrefix + fanId, clean.StepDownPercent);
        _settingsService.Save();

        _loggingService.LogInfo(
            $"{LogTag} dinámica de {fanId}: histéresis {clean.HysteresisC:0.#} °C, respuesta {clean.ResponseSeconds:0.#} s, subida {clean.StepUpPercent:0.#} %/tick, bajada {clean.StepDownPercent:0.#} %/tick");
    }

    public double GetMinDuty(string fanId) => MinDutyFor(fanId);

    /// <summary>
    /// Piso de duty del canal. Se lee en cada tick del motor, igual que el sensor de
    /// temperatura del canal: es una preferencia que puede cambiar en caliente y no
    /// justifica un caché propio.
    /// </summary>
    private float MinDutyFor(string fanId)
    {
        try
        {
            var value = _settingsService.Get(MinDutyPrefix + fanId, 0.0);
            return (float)Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    public void SetMinDuty(string fanId, double percent)
    {
        var clean = Math.Clamp(double.IsFinite(percent) ? percent : 0, 0, 100);

        try
        {
            _settingsService.Set(MinDutyPrefix + fanId, clean);
            _settingsService.Save();

            // El piso nuevo rige en el próximo tick. Si la curva ya está aplicada y el
            // duty escrito cae dentro del rango nuevo, el motor lo corrige solo (el
            // duty pedido cambia y deja de coincidir con el aplicado).
            _loggingService.LogInfo($"{LogTag} {fanId}: piso de duty {clean:0}%" +
                (clean > 0 ? string.Empty : " (sin piso)"));
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"{LogTag} guardar piso de {fanId} falló: {ex.Message}");
        }
    }

    public void SetFanCurve(string fanId, List<FanCurvePoint> points, bool enabled)
    {
        EnsureInitialized();

        var clean = (points ?? new List<FanCurvePoint>())
            .Where(p => p is not null
                // Math.Clamp(NaN) devuelve NaN: un punto inválido cruzaría tal
                // cual a la config y revienta el dibujo al recargar.
                && double.IsFinite(p.TemperatureC) && double.IsFinite(p.DutyPercent))
            .Select(p => new FanCurvePoint(
                Math.Clamp(p.TemperatureC, 20, 100),
                Math.Clamp(p.DutyPercent, 0, 100)))
            .OrderBy(p => p.TemperatureC)
            .ToList();

        bool effectiveEnabled = enabled && clean.Count >= 2;

        // Para escribir el duty hace falta el índice de canales, que lo arma
        // GetFans(): si nadie abrió todavía la página (arranque, reaplicación
        // automática), se arma acá. Sin esto la curva quedaría activa pero el motor
        // no tendría a qué canal escribirle.
        if (effectiveEnabled && _controlsById.Count == 0)
        {
            try { GetFans(); } catch { }
        }

        lock (_lock)
        {
            // El estado en memoria se conserva: lleva la dinámica del canal y el
            // seguimiento del motor. Acá solo cambian los puntos y el flag.
            if (!_curves.TryGetValue(fanId, out var state))
            {
                state = NewCurveState(fanId);
                _curves[fanId] = state;
            }

            state.Points = clean;
            state.Enabled = effectiveEnabled;
            if (effectiveEnabled) _touchedChannels = true;
            // Reescribir la curva olvida el seguimiento: la edición se aplica en el
            // próximo tick, sin esperar histéresis ni tiempo de respuesta.
            ResetCurveTracking(state);

            if (!effectiveEnabled)
            {
                // Al desactivar, devolver el canal al BIOS.
                if (_controlsById.TryGetValue(fanId, out var entry))
                {
                    try { entry.Control.SetDefault(); } catch { }
                }
            }
        }

        // Persistir (puntos aunque esté desactivada, para no perder la edición;
        // el flag solo si está activa). El de "estaba aplicada" sigue al estado: si
        // el usuario suelta la curva, no vuelve sola en el próximo arranque.
        _settingsService.Set(CurvePrefix + fanId, FormatCurve(clean));
        _settingsService.Set(CurveEnabledPrefix + fanId, effectiveEnabled);
        _settingsService.Set(CurveAutoStartPrefix + fanId, effectiveEnabled);
        _settingsService.Save();

        if (effectiveEnabled) EnsureCurveEngine();
        _loggingService.LogInfo($"{LogTag} curva de {fanId}: {clean.Count} puntos, {(effectiveEnabled ? "activada" : "desactivada")}");
    }

    /// <summary>
    /// Motor de curvas: cada 2 s relee temperaturas/RPM y aplica el duty
    /// interpolado a cada canal con curva activa. Corre en el ThreadPool y se
    /// auto-apaga si no hay curvas activas.
    /// </summary>
    private void EnsureCurveEngine()
    {
        lock (_lock)
        {
            if (_curveTimer != null) return;
            if (!_curves.Values.Any(c => c.Enabled)) return;
            _curveTimer = new Timer(_ => CurveTick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            _loggingService.LogInfo($"{LogTag} motor de curvas iniciado");
        }
    }

    private void CurveTick()
    {
        if (Interlocked.Exchange(ref _curveBusy, 1) == 1) return;
        try
        {
            EnsureInitialized();

            // Sin índice de canales el motor no puede escribirle a nadie: lo
            // reconstruye (hardware que apareció tarde, re-detección tras dormir).
            if (_controlsById.Count == 0)
            {
                try { GetFans(); } catch { }
            }

            List<(string Id, FanCurveState State, IFanWriteHandle Control, float Duty, double Temp)> toApply = new();
            var failedSafe = new List<string>();
            bool anyEnabled = false;

            // Cadencia adaptativa: el tick corre cada 1 s, pero el barrido del chip
            // SuperIO (bus ISA, lento) solo hace falta cada 2. En los ticks livianos
            // se actualizan CPU y GPU (MSR / NVAPI / ADL, baratos), que es de donde
            // salen las temperaturas: la curva gana reactividad donde es barata y
            // el ritmo efectivo de los canales de placa sigue siendo el de siempre (2 s).
            // El Motherboard no tiene sensores propios: todo está en su sub-hardware
            // (SuperIO), así que saltarlo entero es el corte correcto.
            bool heavyTick = Interlocked.Increment(ref _curveTickCounter) % 2 == 1;

            lock (_lock)
            {
                if (_computer == null) return;
                foreach (var hw in _computer.Hardware)
                {
                    if (!heavyTick && hw.SubHardware.Any()) continue;
                    try { hw.Update(); } catch { }
                    foreach (var sub in hw.SubHardware)
                    { try { sub.Update(); } catch { } }
                }

                // Un sensor nuevo (otra GPU, un cabezal que apareció) entra al
                // índice sin esperar a que la página vuelva a listar los fans.
                RefreshTemperatureSourcesLocked();

                var now = DateTime.UtcNow;

                foreach (var (id, state) in _curves)
                {
                    if (!state.Enabled) continue;
                    anyEnabled = true;

                    // El dueño del canal viene del índice de controles; si el canal
                    // desapareció (re-detección), se omite este tick.
                    if (!_controlsById.TryGetValue(id, out var entry)) continue;

                    var temp = ReferenceTemperatureC(entry.Hardware, id, out _, out _);
                    if (!temp.HasValue)
                    {
                        // Fail-safe: la decisión (contar ticks, soltar al BIOS al
                        // límite) vive en CurveMissingTemp para poder ejercitarla sin
                        // hardware; acá solo la acción física que depende del canal.
                        if (CurveMissingTemp(state))
                        {
                            try { entry.Control.SetDefault(); } catch { }
                            failedSafe.Add(id);
                        }
                        continue;
                    }
                    state.MissedTempTicks = 0;
                    state.SensorLost = false;

                    // Lo que pide la curva a esta temperatura, antes de la dinámica
                    // del canal (histéresis, respuesta, rampa).
                    var asked = Interpolate(state.Points, temp.Value);
                    if (!asked.HasValue) continue;

                    // El piso se aplica ANTES de la dinámica: así histéresis, tiempo de
                    // respuesta y rampas comparan contra el duty que realmente se
                    // escribe, y no contra uno que el ventilador no puede cumplir.
                    var raw = ApplyDutyFloor((float)Math.Round(asked.Value), MinDutyFor(id));

                    // PlanCurveDuty decide el duty de ESTE tick, o null si todavía no
                    // corresponde escribir (y de paso actualiza el seguimiento).
                    if (PlanCurveDuty(state, temp.Value, raw, now) is not float duty) continue;

                    toApply.Add((id, state, entry.Control, duty, temp.Value));
                }
            }

            // Aviso y persistencia del fail-safe, fuera del lock. Sin persistir el
            // flag desactivado, el reaplicado al iniciar volvería a activar la curva
            // de un canal cuyo sensor se perdió (y volvería al mismo estado muerto).
            if (failedSafe.Count > 0)
            {
                foreach (var id in failedSafe)
                {
                    _settingsService.Set(CurveEnabledPrefix + id, false);
                    // Igual que al soltar al BIOS: no reaplicar al abrir.
                    _settingsService.Set(CurveAutoStartPrefix + id, false);
                    _loggingService.LogWarning($"{LogTag} {id}: sensor de temperatura perdido; canal devuelto al BIOS y curva desactivada");
                }
                _settingsService.Save();
            }

            foreach (var plan in toApply)
            {
                try
                {
                    // Traza de la curva aplicada: sin esto no hay forma de reconstruir
                    // después por qué el ventilador terminó en un duty u otro (qué
                    // temperatura leyó el motor en el momento de escribir).
                    _loggingService.LogDebug(
                        $"{LogTag} curva {plan.Id}: {plan.Temp:0.#} °C -> {plan.Duty:0}%");

                    // Los backends propios devuelven false en vez de lanzar cuando el
                    // driver rechaza la escritura: sin este chequeo el motor daría por
                    // aplicado un duty que nunca llegó al hardware.
                    if (!plan.Control.SetSoftware(plan.Duty))
                    {
                        _loggingService.LogWarning(
                            $"{LogTag} curva {plan.Id}: el backend rechazó el duty {plan.Duty:0}%; se reintenta en el próximo tick");
                        continue;
                    }

                    // El seguimiento se actualiza recién cuando la escritura llegó:
                    // si falla, el próximo tick lo vuelve a intentar.
                    lock (_lock)
                    {
                        plan.State.LastAppliedDuty = plan.Duty;
                        plan.State.LastAppliedTemp = plan.Temp;
                        plan.State.PendingDuty = null;
                    }
                }
                catch (Exception ex)
                {
                    if (!_curveErrorLogged)
                    {
                        _curveErrorLogged = true;
                        _loggingService.LogWarning($"{LogTag} curva: no se pudo escribir duty en {plan.Id}: {ex.Message}");
                    }
                }
            }

            if (!anyEnabled)
            {
                lock (_lock)
                {
                    _curveTimer?.Dispose();
                    _curveTimer = null;
                    _loggingService.LogInfo($"{LogTag} motor de curvas detenido (sin curvas activas)");
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"{LogTag} tick de curvas falló: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _curveBusy, 0);
        }
    }

    // Fail-safe del motor: un tick sin lectura de temperatura suma; al llegar al
    // límite suelta el canal (desactiva la curva y marca el aviso para la UI) y
    // devuelve true. Cuenta varios ticks y no uno solo para no reaccionar a una
    // lectura perdida. Static e internal para que el harness lo verifique sin
    // hardware. Requiere _lock tomado cuando lo llama CurveTick.
    internal static bool CurveMissingTemp(FanCurveState state)
    {
        state.MissedTempTicks++;
        if (state.MissedTempTicks < MissedTempLimit) return false;

        state.Enabled = false;
        state.MissedTempTicks = 0;
        state.SensorLost = true;
        return true;
    }

    // Decide el duty que corresponde escribir en este tick, aplicando la dinámica
    // del canal (histéresis, tiempo de respuesta y límites de subida/bajada).
    // Devuelve null cuando la dinámica dice "todavía no" o cuando no hay nada que
    // cambiar. Requiere _lock tomado (lee y actualiza el seguimiento del motor).
    internal static float? PlanCurveDuty(FanCurveState state, double temp, float raw, DateTime now)
    {
        var points = state.Points;

        // En los extremos de la curva (el primer y el último punto, y todo lo que
        // queda fuera de ese rango) la dinámica se ignora: si el CPU llega al
        // máximo, el ventilador responde al instante.
        bool atExtreme = points.Count > 0 &&
            (temp <= points[0].TemperatureC || temp >= points[^1].TemperatureC);

        // Primera aplicación (o curva recién editada): se escribe ya. Sin referencia
        // previa no hay contra qué comparar la temperatura ni qué rampantear.
        if (state.LastAppliedDuty is not float current)
            return raw;

        // Nada que hacer: el duty escrito ya es el que pide la curva.
        if (Math.Abs(raw - current) < 0.5f)
        {
            state.PendingDuty = null;
            state.CatchUp = false;
            return null;
        }

        // Mientras se cierra una escalera ya decidida (los límites por actualización
        // necesitan varios ticks), no se vuelve a filtrar: la decisión está tomada y
        // solo falta llegar.
        if (!atExtreme && !state.CatchUp)
        {
            // Histéresis: si la temperatura no se alejó de aquella en la que se aplicó
            // el duty actual más que la banda, el duty se queda donde está.
            if (state.HysteresisC > 0 && state.LastAppliedTemp is double lastTemp &&
                Math.Abs(temp - lastTemp) < state.HysteresisC)
            {
                state.PendingDuty = null;
                return null;
            }

            // Tiempo de respuesta: el cambio tiene que sostenerse N segundos antes de
            // aplicarse, para filtrar picos momentáneos.
            //
            // La cuenta mide la INTENCIÓN (pedir más o menos que el duty que está
            // escrito), no el valor exacto: comparando el valor exacto la cuenta se
            // reiniciaba en cada tick mientras la temperatura seguía subiendo o
            // bajando, así que el ventilador quedaba pegado al duty viejo hasta que la
            // temperatura se estabilizara — justo al revés de lo buscado (frenar una
            // rampa sostenida en lugar de ignorar un pico).
            // Solo se reinicia cuando el pedido se da vuelta (un pico que sube y
            // vuelve), que es el caso que este filtro tiene que descartar.
            if (state.ResponseSeconds > 0)
            {
                bool wantsUp = raw > current;
                bool pendingUp = state.PendingDuty is double pending && pending > current;

                if (state.PendingDuty is not double || pendingUp != wantsUp)
                {
                    state.PendingDuty = raw;
                    state.PendingSinceUtc = now;
                    return null;
                }

                // El pedido sigue apuntando al mismo lado: se actualiza el valor (para
                // escribir el más nuevo cuando venza la cuenta) sin reiniciarla.
                state.PendingDuty = raw;
                if ((now - state.PendingSinceUtc).TotalSeconds < state.ResponseSeconds)
                    return null;
            }
        }

        state.PendingDuty = null;

        // Límites por actualización: el duty avanza como máximo StepUp % si sube y
        // StepDown % si baja. Si queda distancia por recorrer, se marca la escalera
        // para seguir en los próximos ticks aunque la temperatura no se mueva.
        float target = raw;
        if (raw > current && state.StepUpPercent > 0)
            target = Math.Min(raw, current + (float)state.StepUpPercent);
        else if (raw < current && state.StepDownPercent > 0)
            target = Math.Max(raw, current - (float)state.StepDownPercent);

        target = Math.Clamp(target, 0f, 100f);
        if (Math.Abs(target - current) < 0.5f)
        {
            // El límite dejaría el duty donde está: nada que escribir.
            state.CatchUp = false;
            return null;
        }

        state.CatchUp = Math.Abs(target - raw) >= 0.5f;
        return target;
    }

    // Requiere _lock tomado. Olvida el seguimiento del motor, así el próximo tick
    // aplica el duty pedido sin arrastrar histéresis, respuesta ni escalera.
    private static void ResetCurveTracking(FanCurveState state)
    {
        state.LastAppliedTemp = null;
        state.LastAppliedDuty = null;
        state.PendingDuty = null;
        state.PendingSinceUtc = default;
        state.CatchUp = false;
    }

    // =====================================================================
    // Nombres personalizados (persisten en settings.json)
    // =====================================================================

    private const string CustomNamePrefix = "fan.name.";

    // El nombre mostrado: personalizado si existe, si no el genérico de LHM.
    private string DisplayName(string id, string fallback) =>
        _settingsService.Get(CustomNamePrefix + id, string.Empty) is { Length: > 0 } custom
            ? custom
            : fallback;

    public void SetCustomName(string fanId, string? name)
    {
        var key = CustomNamePrefix + fanId;
        if (string.IsNullOrWhiteSpace(name))
            _settingsService.Set(key, string.Empty); // borra
        else
            _settingsService.Set(key, name.Trim());
        _loggingService.LogInfo($"{LogTag} nombre personalizado de {fanId}: '{name}'");
    }

    // True si el fail-safe tuvo que soltar este canal al BIOS por perder el sensor
    // de temperatura. La UI lo muestra en la card y lo limpia al reaplicar.
    public bool WasSensorLost(string fanId) =>
        _curves.TryGetValue(fanId, out var s) && s.SensorLost;

    // Limpia el aviso de sensor perdido (la UI lo llama al reaplicar la curva o al
    // elegir BIOS (auto): en ambos casos el usuario ya vio y resolvió el estado).
    public void ClearSensorLost(string fanId)
    {
        if (_curves.TryGetValue(fanId, out var s) && s.SensorLost)
        {
            s.SensorLost = false;
            s.MissedTempTicks = 0;
            _loggingService.LogInfo($"{LogTag} {fanId}: aviso de sensor perdido limpiado");
        }
    }

    public string GetCustomName(string fanId) =>
        _settingsService.Get(CustomNamePrefix + fanId, string.Empty);

    private bool HasCustomName(string fanId) => GetCustomName(fanId).Length > 0;

    private const string TemperatureSourcePrefix = "fan.sensor.";

    // Sensor virtual para los ventiladores de caja: el máximo de CPU y GPU.
    private const string MixMaxCpuGpuId = "mix:cpu-gpu:max";
    private const string MixMaxCpuGpuName = "Máximo de CPU y GPU";

    public List<FanTemperatureSource> GetTemperatureSources()
    {
        EnsureInitialized();
        lock (_lock)
        {
            if (_computer == null) return new List<FanTemperatureSource>();

            // Si nadie llamó a GetFans todavía, el índice está vacío: se actualiza
            // una vez para tener nombres y valores. En el camino normal GetFans ya
            // dejó el índice al día y esto no vuelve a tocar el bus ISA.
            if (_temperatureSourcesById.Count == 0)
            {
                foreach (var hw in AllHardware())
                {
                    try { hw.Update(); } catch { }
                }
                RefreshTemperatureSourcesLocked();
            }

            // Se devuelven todos los sensores, también los que no reportan en este
            // instante: la UI filtra los que no tienen lectura, pero el índice del
            // motor los conserva para que un sensor elegido pueda volver sin perder
            // la configuración del canal.
            var list = _temperatureSourcesById
                .Select(kv => new FanTemperatureSource(
                    kv.Key,
                    kv.Value.Name,
                    kv.Value.Sensor.Value,
                    Group: ClassifyTemperatureSource(kv.Value.Hardware, kv.Value.Sensor)))
                // Clasificados por rol: CPU, GPU, VRM, placa y el resto. Dentro de
                // cada grupo, primero la lectura que gobierna (paquete/núcleo).
                .OrderBy(s => GroupOrder(s.Group))
                .ThenBy(s => SourceRoleOrder(s.Id), Comparer<int>.Default)
                .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            // Va primera para que sea fácil de encontrar entre los sensores reales.
            list.Insert(0, new FanTemperatureSource(
                MixMaxCpuGpuId, MixMaxCpuGpuName, MaxCpuGpuTemperatureC(), Synthetic: true));
            return list;
        }
    }

    public string GetTemperatureSource(string fanId)
    {
        try { return _settingsService.Get(TemperatureSourcePrefix + fanId, string.Empty) ?? string.Empty; }
        catch { return string.Empty; }
    }

    public void SetTemperatureSource(string fanId, string? sourceId)
    {
        var value = sourceId ?? string.Empty;
        try
        {
            _settingsService.Set(TemperatureSourcePrefix + fanId, value);
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"{LogTag} guardar sensor de {fanId} falló: {ex.Message}");
        }

        lock (_lock)
        {
            if (_curves.TryGetValue(fanId, out var state))
            {
                state.TemperatureSourceId = value;
                // La elección nueva se aplica ya, sin arrastrar histéresis ni
                // respuesta decididas con el sensor anterior.
                ResetCurveTracking(state);
            }
        }

        _loggingService.LogInfo($"{LogTag} {fanId}: sensor de temperatura = {(value.Length == 0 ? "automático" : value)}");
    }

    // =====================================================================
    // Persistencia de curvas en settings.json ("temp:duty;temp:duty…")
    // =====================================================================

    private const string CurvePrefix = "fan.curve.";
    private const string CurveEnabledPrefix = "fan.curve.enabled.";
    private const string CurveHysteresisPrefix = "fan.curve.hysteresis.";
    private const string CurveResponsePrefix = "fan.curve.response.";
    private const string CurveStepUpPrefix = "fan.curve.stepup.";
    private const string CurveStepDownPrefix = "fan.curve.stepdown.";

    // "Estaba aplicada cuando se cerró la app": el apagado devuelve los canales al
    // BIOS a propósito (para no dejarlos con un duty fijo), así que el flag de
    // 'enabled' no sirve para recordarlo. Este sí, y es lo que se relee al abrir.
    private const string CurveAutoStartPrefix = "fan.curve.autoStart.";

    // Defaults de la dinámica: una histéresis de 2 °C y 3 s de respuesta alcanzan
    // para que el ventilador deje de "cazar" con cada oscilación de 1 °C; los
    // límites de subida/bajada vienen apagados (0) para no agregar lentitud de
    // fábrica a una curva que responde bien.
    private const double DefaultHysteresisC = 2;
    private const double DefaultResponseSeconds = 3;
    private const double DefaultStepUpPercent = 0;
    private const double DefaultStepDownPercent = 0;

    // Fail-safe: ticks consecutivos sin temperatura antes de soltar el canal al
    // BIOS (a 1 s de tick son ~5 s; tolera una lectura perdida sin soltar).
    private const int MissedTempLimit = 5;

    // Dinámica persistida del canal, con los defaults si nunca se configuró.
    private FanCurveDynamics LoadCurveDynamics(string fanId) => new(
        _settingsService.Get(CurveHysteresisPrefix + fanId, DefaultHysteresisC),
        _settingsService.Get(CurveResponsePrefix + fanId, DefaultResponseSeconds),
        _settingsService.Get(CurveStepUpPrefix + fanId, DefaultStepUpPercent),
        _settingsService.Get(CurveStepDownPrefix + fanId, DefaultStepDownPercent));

    // Estado nuevo de una curva: sin puntos (los pone SetFanCurve) y con la
    // dinámica del canal. Requiere _lock tomado (lo llama SetFanCurve).
    private FanCurveState NewCurveState(string fanId)
    {
        var dynamics = LoadCurveDynamics(fanId);
        return new FanCurveState
        {
            HysteresisC = dynamics.HysteresisC,
            ResponseSeconds = dynamics.ResponseSeconds,
            StepUpPercent = dynamics.StepUpPercent,
            StepDownPercent = dynamics.StepDownPercent,
            TemperatureSourceId = GetTemperatureSource(fanId)
        };
    }

    private static string FormatCurve(List<FanCurvePoint> points) =>
        string.Join(";", points.Select(p => $"{p.TemperatureC:0}:{p.DutyPercent:0}"));

    private static List<FanCurvePoint> ParseCurve(string serialized)
    {
        var points = new List<FanCurvePoint>();
        if (string.IsNullOrWhiteSpace(serialized)) return points;
        foreach (var part in serialized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var halves = part.Split(':');                if (halves.Length == 2 &&
                    double.TryParse(halves[0], System.Globalization.CultureInfo.InvariantCulture, out var t) &&
                    double.TryParse(halves[1], System.Globalization.CultureInfo.InvariantCulture, out var d) &&
                    double.IsFinite(t) && double.IsFinite(d)) // "NaN" parsea: se descarta
                {
                    points.Add(new FanCurvePoint(t, d));
                }
        }
        return points.OrderBy(p => p.TemperatureC).ToList();
    }

    // =====================================================================
    // Calibración del canal (medir el ventilador, no leer una tabla)
    // =====================================================================

    private const string CalibrationPrefix = "fan.calibration.";

    // Un paso cada 10% y 2,5 s de espera por paso (el ventilador y el tacómetro
    // necesitan asentarse): el barrido completo —subida y bajada— tarda ~1 min.
    private const int CalibrationStepPercent = 10;
    private const int CalibrationSettleMs = 2500;

    // Cuánto tiene que moverse un tacómetro para considerarlo "el de este canal",
    // y cuánto el duty leído para considerarlo vivo (y no un registro estático).
    private const double CalibrationMinRpmDelta = 60;
    private const double CalibrationMinDutyDelta = 10;

    // Un paso del barrido: qué se pidió y qué se midió después de asentar.
    private sealed record CalibrationSample(int RequestedPercent, double? DutyReadback, Dictionary<string, double?> Rpms);

    public async Task<FanCalibration> CalibrateAsync(string fanId, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (!_controlsById.ContainsKey(fanId)) GetFans(); // rellena el índice de ids
        if (!_controlsById.ContainsKey(fanId))
            throw new InvalidOperationException($"El canal {fanId} no tiene control PWM: no se puede calibrar.");

        if (Interlocked.CompareExchange(ref _calibrating, 1, 0) == 1)
            throw new InvalidOperationException("Ya hay una calibración en curso.");

        // Estado a restaurar: la curva propia del canal se suspende, porque el
        // motor re-aplicaría su duty cada 2 s y arruinaría el barrido.
        bool wasCurve = IsFanCurveEnabled(fanId);
        var savedCurve = GetFanCurve(fanId);
        bool hasCurve = wasCurve && savedCurve.Count >= 2;

        var ascent = new List<CalibrationSample>();
        var descent = new List<CalibrationSample>();
        try
        {
            if (hasCurve) SetFanCurve(fanId, savedCurve, enabled: false);

            _loggingService.LogInfo($"{LogTag} calibración de {fanId}: barrido 0→100→0");

            for (int pct = 0; pct <= 100; pct += CalibrationStepPercent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(pct);
                ascent.Add(await RunStepAsync(fanId, pct, cancellationToken));
            }

            for (int pct = 100 - CalibrationStepPercent; pct >= 0; pct -= CalibrationStepPercent)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(pct);
                descent.Add(await RunStepAsync(fanId, pct, cancellationToken));
            }
        }
        finally
        {
            try
            {
                if (hasCurve) SetFanCurve(fanId, savedCurve, enabled: true);
                else SetAuto(fanId);
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"{LogTag} no se pudo restaurar {fanId} tras calibrar: {ex.Message}");
            }
            Interlocked.Exchange(ref _calibrating, 0);
        }

        var result = AnalyzeCalibration(ascent, descent);
        _settingsService.Set(CalibrationPrefix + fanId, result);
        _settingsService.Save();

        string tach = result.TachResponds ? result.TachSensorName ?? "—" : "sin respuesta";
        _loggingService.LogInfo($"{LogTag} calibración de {fanId}: arranca en {result.StartPercent:0}%, "
            + $"{result.MinRpm:0}–{result.MaxRpm:0} RPM, tacómetro: {tach}, duty leído {(result.DutyReadbackLive ? "vivo" : "estático")}");
        return result;
    }

    public FanCalibration? GetCalibration(string fanId) =>
        _settingsService.Get<FanCalibration>(CalibrationPrefix + fanId);

    public void ResetCalibration(string fanId)
    {
        _settingsService.Remove(CalibrationPrefix + fanId);
        _settingsService.Save();
        _loggingService.LogInfo($"{LogTag} calibración de {fanId} borrada");
    }

    // Aplica el duty pedido, deja asentar y mide. La escritura y la lectura van
    // bajo lock; la espera queda afuera para no bloquear el resto del servicio.
    private async Task<CalibrationSample> RunStepAsync(string fanId, int percent, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FanControlService));

        lock (_lock) WriteDutyLocked(fanId, percent);
        await Task.Delay(CalibrationSettleMs, cancellationToken).ConfigureAwait(false);

        double? duty;
        var rpms = new Dictionary<string, double?>();
        lock (_lock) (duty, rpms) = ReadChannelLocked(fanId);

        return new CalibrationSample(percent, duty, rpms);
    }

    // Requiere _lock tomado.
    private void WriteDutyLocked(string fanId, int percent)
    {
        var entry = _controlsById[fanId];
        try
        {
            entry.Hardware.Update();
            if (!entry.Control.SetSoftware(percent))
                _loggingService.LogWarning($"{LogTag} calibración: el backend rechazó {percent}% en {fanId}");
            else
                _touchedChannels = true;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"{LogTag} calibración: no se pudo escribir {percent}% en {fanId}: {ex.Message}");
        }
    }

    // Requiere _lock tomado. Devuelve el duty leído del canal y el RPM de TODOS los
    // tacómetros de esa cadena: el chip no garantiza que el índice del tacómetro
    // coincida con el del control, así que no emparejamos por índice — miramos
    // cuál se mueve cuando movemos este canal.
    private (double? DutyReadback, Dictionary<string, double?> Rpms) ReadChannelLocked(string fanId)
    {
        var rpms = new Dictionary<string, double?>();
        if (!_controlsById.TryGetValue(fanId, out var entry)) return (null, rpms);

        double? duty = null;
        try
        {
            var channels = CollectChannels(update: true);
            var chain = channels.FirstOrDefault(ch => ReferenceEquals(ch.Owner, entry.Hardware));
            if (chain != null)
            {
                foreach (var f in chain.Fans) rpms[f.Name] = f.Value;
                duty = entry.Sensor.Value;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"{LogTag} calibración: lectura de {fanId} falló: {ex.Message}");
        }

        return (duty, rpms);
    }

    // Convierte el barrido en el resultado: qué tacómetro respondió, dónde arranca,
    // dónde para, mínimo/máximo y la relación %→RPM medida.
    private static FanCalibration AnalyzeCalibration(List<CalibrationSample> ascent, List<CalibrationSample> descent)
    {
        // El tacómetro del canal es el que más se movió durante el barrido. Si
        // ninguno se movió, el canal no reporta RPM (o no tiene el cable).
        string? responded = null;
        double bestDelta = 0;
        foreach (var name in ascent.SelectMany(s => s.Rpms.Keys).Distinct())
        {
            var values = ascent
                .Select(s => s.Rpms.TryGetValue(name, out var v) ? v : null)
                .Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (values.Count < 3) continue;

            double delta = values.Max() - values.Min();
            if (delta > bestDelta) { bestDelta = delta; responded = name; }
        }

        bool tachResponds = responded != null && bestDelta >= CalibrationMinRpmDelta;
        double RpmOf(CalibrationSample s) =>
            responded != null && s.Rpms.TryGetValue(responded, out var v) && v.HasValue ? v.Value : 0;

        double? startPercent = null, minRpm = null, maxRpm = null;
        var percentToRpm = new List<FanCurvePoint>();

        if (tachResponds)
        {
            foreach (var s in ascent)
            {
                double rpm = RpmOf(s);
                percentToRpm.Add(new FanCurvePoint(s.RequestedPercent, Math.Round(rpm, 1)));

                if (rpm > 0)
                {
                    startPercent ??= s.RequestedPercent;
                    minRpm = minRpm.HasValue ? Math.Min(minRpm.Value, rpm) : rpm;
                    maxRpm = maxRpm.HasValue ? Math.Max(maxRpm.Value, rpm) : rpm;
                }
            }
        }

        // Punto de parada: yendo para abajo, el primer escalón donde volvió a cero.
        double? stopPercent = null;
        if (tachResponds)
        {
            bool spun = false;
            foreach (var s in descent)
            {
                if (RpmOf(s) > 0) spun = true;
                else if (spun) { stopPercent = s.RequestedPercent; break; }
            }
        }

        // ¿El duty leído acompañó al pedido, o es un registro quieto del firmware?
        var readbacks = ascent.Select(s => s.DutyReadback)
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
        bool dutyLive = readbacks.Count >= 3 && readbacks.Max() - readbacks.Min() >= CalibrationMinDutyDelta;

        return new FanCalibration
        {
            TachResponds = tachResponds,
            TachSensorName = tachResponds ? responded : null,
            StartPercent = startPercent,
            StopPercent = stopPercent,
            MinRpm = minRpm,
            MaxRpm = maxRpm,
            DutyReadbackLive = dutyLive,
            PercentToRpm = percentToRpm,
            CompletedUtc = DateTime.UtcNow,
        };
    }

}
