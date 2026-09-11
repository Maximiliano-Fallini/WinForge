using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary> /// Implementación del "Modo juego de WinForge (BETA)".
///
/// Al aplicar (juego corriendo y switch activo) se guarda un snapshot del estado
/// previo y se hacen los cambios; al cerrar el juego se restaura exactamente ese
/// snapshot:
/// · Servicios: solo se re-arrancan los que estaban corriendo antes (nunca se
/// enciende algo que ya estaba detenido/deshabilitado).
/// · Prioridades: se devuelve la prioridad ORIGINAL a los procesos de segundo
/// plano que se bajaron (no al juego: su prioridad la manejan sus reglas).
/// Si un proceso cerró en el camino o su PID fue reutilizado, se omite.
/// · Notificaciones: las toasts se pausan mientras dura el boost y al cerrar el
/// juego se restaura el estado exacto anterior (equivale al modo foco "solo
/// alarmas").
///
/// No se cambia el tipo de arranque de ningún servicio ni se escribe ninguna
/// política de Windows Update: todo es temporal y reversible.
/// </summary>
public sealed class GameBoostService : IGameBoostService
{
    private const string SettingKey = "gameboost.enabled";

    private readonly ISettingsService _settings;
    private readonly ILoggingService _logging;
    private readonly IProcessService _processService;

    private readonly object _lock = new();

    // Boost aplicado y pendiente de restaurar.
    private bool _active;

    // Snapshot: servicios que estaban corriendo y su tipo de arranque (se
    // re-arrancan al restaurar, salvo que el usuario los deshabilite entretanto).
    private List<ServiceSnapshot> _servicesToRestart = new();

    // Snapshot: prioridad original de los procesos bajados (pid, nombre y clase).
    private List<ProcessSnapshot> _processesToRestore = new();

    // Snapshot: interruptor maestro de notificaciones toast (null = la clave no
    // existía, es decir habilitadas por defecto — se borra al restaurar).
    private int? _toastsSnapshot;

    // Snapshot del Modo Juego de Windows (null = habilitado por defecto, se borra
    // al restaurar): se activa para la partida y se restaura al cerrarla.
    private int? _gameModeSnapshot;

    // El Modo Juego de WINDOWS no se pudo activar esta partida (huérfano: faltan
    // archivos de Game Bar). Se reporta en GameBoostApplyResult para que la UI avise.
    private bool _gameModeWarning;

    // Salud del Modo Juego de Windows (opcional: si el host no lo registró en DI,
    // el boost sigue funcionando sin tocar el Modo Juego de Windows).
    private readonly IWindowsGameModeHealthService? _gameMode;

    private sealed record ProcessSnapshot(int Pid, string Name, ProcessPriorityClass OriginalClass);
    private sealed record ServiceSnapshot(string Name, string StartType);

    /// <summary>Se dispara cuando el boost se aplica (juego iniciado), con el resumen.</summary>
    public event Action<GameBoostApplyResult>? BoostApplied;

    // Servicios con impacto directo en la actividad del sistema mientras se juega:
    // mantenimiento/indexado + telemetría/diagnóstico + cola de impresión.
    private static readonly string[] DefaultKillServices =
    {
        "wuauserv",         // Windows Update
        "UsoSvc",           // Update Orchestrator
        "BITS",             // Background Intelligent Transfer Service
        "SysMain",          // Superfetch/SysMain (prefetch agresivo)
        "WSearch",          // Búsqueda de Windows (indexado)
        "DiagTrack",        // Telemetría de diagnóstico
        "WerSvc",           // Informe de errores de Windows
        "DPS",              // Directivas de diagnóstico
        "Spooler"           // Cola de impresión
    };

    // Los servicios de telemetría/diagnóstico (DiagTrack, WerSvc, DPS) se muestran
    // en la UI con su estado REAL de Windows (grupo "telemetry") y TAMBIÉN entran
    // al conjunto que el boost detiene por partida: es una parada TEMPORAL que se
    // restaura al cerrar el juego, no el cambio persistente del apartado debloat.

    // Servicios de Hyper-V / virtualización: dejarlos Desactivado es el tweak
    // clásico de latencia para gaming (elimina la capa de hipervisor del arranque
    // y libera recursos). Igual que la telemetría, son cambios persistentes: no
    // entran al boost por partida. Los nombres existen en Win10/11 con Hyper-V,
    // Virtual Machine Platform o WSL2; si el equipo no los tiene, la card los
    // oculta solos (GetServiceStates no devuelve los que no existen).

    // Definición de la card: grupos en orden de aparición. Cada servicio lleva su
    // descripción (clave de traducción) para el tooltip de la UI.
    public static readonly (string Key, string NameKey, (string Service, string DescriptionKey)[] Services)[] ServiceGroups =
    {
        ("boost", "Servicios del optimizador", new (string, string)[]
        {
            ("wuauserv", "Windows Update: descarga e instala actualizaciones de Windows. Desactivarlo detiene las actualizaciones automáticas."),
            ("UsoSvc", "Orquestador de actualizaciones: coordina cuándo se buscan e instalan las actualizaciones de Windows."),
            ("BITS", "Transferencia inteligente en segundo plano: descarga archivos de Windows Update y la Store sin interrumpir al usuario."),
            ("SysMain", "Precarga en memoria de apps usadas frecuentemente (ex Superfetch). Puede generar lectura de disco constante mientras jugás."),
            ("WSearch", "Indexado de archivos para la búsqueda de Windows. Consume disco y CPU mientras indexa contenido nuevo."),
        }),
        ("hyperv", "Hyper-V y virtualización", new (string, string)[]
        {
            ("vmcompute", "Cómputo de host: administra máquinas virtuales y contenedores (Hyper-V, WSL2, Docker). Si no usás ninguna, puede ir Desactivado."),
            ("HvHost", "Host de hipervisor: presta servicios internos al hipervisor de Hyper-V."),
            ("hns", "Red de host: administra la red virtual de Hyper-V y contenedores. Deshabilitarlo rompe la red de WSL2 y Docker, no solo las VMs."),
            ("vmicguestinterface", "Interfaz de invitado: canal de comunicación entre el host y la máquina virtual."),
            ("vmicshutdown", "Apagado de invitado: permite apagar la máquina virtual desde el administrador de Hyper-V."),
            ("vmicheartbeat", "Latido: informa al host que la máquina virtual arrancó y está funcionando."),
            ("vmicvmsession", "PowerShell Direct: permite ejecutar comandos dentro de la VM sin red."),
            ("vmicrdv", "Escritorio remoto virtualizado: redirección de dispositivos RDP hacia la VM."),
            ("vmickvpexchange", "Intercambio de datos: deja que el host lea datos básicos de la VM (nombre, sistema operativo)."),
            ("vmictimesync", "Sincronización de hora: mantiene el reloj de la VM alineado con el del host."),
            ("vmicvss", "Solicitante de instantáneas: coordina copias de seguridad de la VM desde el host."),
        }),
        ("telemetry", "Telemetría", new (string, string)[]
        {
            ("DiagTrack", "Telemetría de diagnóstico: envía datos de uso y diagnóstico a Microsoft. Es seguro desactivarla."),
            ("WerSvc", "Informe de errores: envía reportes a Microsoft cuando un programa falla."),
            ("dmwappushservice", "WAP Push: gestión de dispositivos móviles y telemetría asociada. No se usa en PCs de escritorio normales."),
            ("DPS", "Directivas de diagnóstico: decide qué diagnósticos puede ejecutar Windows y resuelve problemas detectados. Sin uso mientras jugás."),
            ("DusmSvc", "Uso de datos: estadísticas de consumo de red por aplicación (panel de Datos de uso)."),
        }),
    // Grupos extra: como telemetría e Hyper-V, son gestión PERSISTENTE (3
    // estados leídos de Windows); no entran al boost por partida. La card
    // oculta sola los servicios que no existen en esta instalación.
        ("xbox", "Xbox y Game Bar", new (string, string)[]
        {
            ("XblAuthManager", "Autenticación de Xbox Live: inicia sesión en Xbox Live para juegos de la Store y Game Pass. Desactivarla rompe el login de Game Pass."),
            ("XblGameSave", "Guardado de Xbox: sincroniza partidas de juegos de la Store y Game Pass con la nube. Si no jugás títulos de la Store, puede ir Desactivado."),
            ("XboxNetApiSvc", "Redes de Xbox Live: multijugador y matchmaking de juegos con integración Xbox. Desactivarlo rompe el multijugador de esos juegos."),
            ("XboxGipSvc", "Accesorios Xbox: empareja y administra periféricos con protocolo Xbox (controles y auriculares inalámbricos)."),
        }),
        ("printing", "Impresión y escáner", new (string, string)[]
        {
            ("Spooler", "Cola de impresión: administra los trabajos de impresión. Sin impresora puede ir Desactivado; también se puede apagar solo durante la partida."),
            ("PrintNotify", "Extensiones de impresora: avisos de tinta, estado y apps del fabricante. No afecta a imprimir en sí."),
            ("StiSvc", "Adquisición de imágenes (WIA): comunicación con escáneres y cámaras. Sin escáner puede ir Desactivado."),
        }),
        ("location", "Ubicación y mapas", new (string, string)[]
        {
            ("lfsvc", "Geolocalización: calcula la posición del equipo (Wi-Fi, GPS). Los juegos de escritorio casi nunca la usan."),
            ("MapsBroker", "Mapas sin conexión: descarga y actualiza mapas para la app Mapas. Puede ir Desactivado."),
        }),
        ("legacy", "Legado / sin uso en PC", new (string, string)[]
        {
            ("Fax", "Fax: envía y recibe faxes con la configuración de fax de Windows. Sin uso práctico hoy."),
            ("RemoteRegistry", "Registro remoto: permite a administradores remotos leer el registro de este equipo. Desactivarlo también es una mejora de seguridad."),
            ("RetailDemo", "Modo demo de tienda: configuración para PCs en exhibición en comercios. Nunca se usa en una PC personal."),
            ("PhoneSvc", "Telefonía: administra dispositivos de telefonía (módems dial-up, SIP). Sin uso en PCs modernas."),
        }),
    };

    /// <summary>
    /// Conjunto completo de servicios gestionados por la card (todos los grupos):
    /// es lo que la UI consulta para pintar los 3 estados.
    /// </summary>
    public static string[] ManagedServices =>
        ServiceGroups.SelectMany(g => g.Services.Select(s => s.Service)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Servicios que el boost DETIENE al iniciar el juego (excluye Hyper-V).</summary>
    public static string[] BoostStopServices =>
        (string[])DefaultKillServices.Clone();

    // Procesos en segundo plano a los que se aplica el boost POR DEFECTO (la lista es
    // configurable desde la UI: ver GetBackgroundProcesses). IMPORTANTE: esta lista
    // NUNCA incluye juegos ni procesos del sistema; el juego tiene sus propias reglas
    // de prioridad/afinidad que este boost no toca.
    public static readonly string[] DefaultBackgroundProcesses =
    {
        "TextInputHost", "ctfmon", "SearchHost", "SearchApp", "Widgets",
        "WidgetService", "OneDrive", "StartMenuExperienceHost",
        "ShellExperienceHost", "PhoneExperienceHost",
        "RuntimeBroker", "backgroundTaskHost", "taskhostw", "dllhost",
        "sihost", "GameBarPresenceWriter", "GameBarFullWindowProcess",
        "SecurityHealthSystray", "CompatTelRunner", "UserOOBEBroker"
    };

    private const string BackgroundProcessesKey = "gameboost.backgroundProcesses";
    private const string KillProcessesKey = "gameboost.killProcesses";
    private const string AutoServiceOptimizationKey = "gameboost.automaticServiceOptimization";

 /// <summary>
 /// Switch "Servicios Optimizados Automaticos" (pestaña Servicios de la
 /// configuración del boost): activo por defecto. Cuando está activo, el boost
 /// detiene temporalmente los servicios de <see cref="BoostStopServices"/> al
 /// lanzar un juego y los restaura al cerrarlo. Cuando está apagado, el boost
 /// NO toca ningún servicio por partida. Es independiente de los cambios
 /// persistentes que el usuario haga en la pestaña Servicios (sc config).
 /// </summary>
    public bool IsAutomaticServiceOptimizationEnabled
    {
        get => _settings.Get(AutoServiceOptimizationKey, true);
        set
        {
            _settings.Set(AutoServiceOptimizationKey, value);
            _settings.Save();
            _logging.LogInfo($"GameBoost: optimización automática de servicios {(value ? "activada" : "desactivada")} (por partida).");
        }
    }

    public GameBoostService(ISettingsService settings, ILoggingService logging, IProcessService processService, IWindowsGameModeHealthService? gameModeHealth = null)
    {
        _settings = settings;
        _logging = logging;
        _processService = processService;
        _gameMode = gameModeHealth;

        // El cierre de juegos se detecta con los eventos WMI de ProcessService.
        _processService.RunningGamesChanged += OnRunningGamesChanged;

        if (!_processService.WmiEventsActive)
            _logging.LogWarning("GameBoost: WMI no está activo — la restauración al cerrar el juego no estará disponible.");
    }

    public bool IsEnabled => _settings.Get(SettingKey, false);

    public void SetEnabled(bool enabled)
    {
        _settings.Set(SettingKey, enabled);
        _settings.Save();
        _logging.LogDebug($"GameBoost: switch {(enabled ? "activado" : "desactivado")}");

        // Si se apaga con un boost activo, restaurar de inmediato.
        if (!enabled)
            _ = RestoreAsync();
    }

    // ===== Configuración (tuerca junto al switch) =====

    public List<string> GetBackgroundProcesses()
    {
        // Lista efectiva = defaults FIJOS + agregados del usuario (configurables).
        // Los defaults viven en código para que una actualización pueda sumar nuevos
        // sin pisar lo guardado; el setting solo contiene los agregados.
        var result = new List<string>(DefaultBackgroundProcesses);
        var saved = _settings.Get(BackgroundProcessesKey, new List<string>());
        if (saved != null)
        {
            foreach (var raw in saved)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var clean = raw.Trim();
                if (!result.Contains(clean, StringComparer.OrdinalIgnoreCase))
                    result.Add(clean);
            }
        }
        return result;
    }

    public void SetBackgroundProcesses(List<string> processes)
    {
        // Se persisten SOLO los agregados por el usuario (sin .exe, sin duplicados,
        // sin nombres que ya sean defaults: esos son fijos y no hace falta guardarlos).
        var defaults = new HashSet<string>(DefaultBackgroundProcesses, StringComparer.OrdinalIgnoreCase);
        var clean = (processes ?? new())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Where(n => !defaults.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.Set(BackgroundProcessesKey, clean);
        _settings.Save();
        _logging.LogInfo($"GameBoost: procesos agregados al boost ({clean.Count}).");
    }

    public List<string> GetDefaultBackgroundProcesses()
        => new(DefaultBackgroundProcesses);

 /// <summary>Procesos que el usuario eligió CERRAR al iniciar un juego.</summary>
    public List<string> GetKillProcesses()
    {
        var saved = _settings.Get(KillProcessesKey, new List<string>());
        if (saved == null) return new();
        return saved
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

 /// <summary>Persiste la lista de procesos a cerrar al iniciar un juego.</summary>
    public void SetKillProcesses(List<string> processes)
    {
        var clean = (processes ?? new())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.Set(KillProcessesKey, clean);
        _settings.Save();
        _logging.LogInfo($"GameBoost: procesos a cerrar actualizados ({clean.Count}).");
    }

 // ===== Estado de servicios en Windows (3 estados: Desactivado/Manual/Activado) =====
 // IMPORTANTE: el estado se lee por WMI (Win32_Service), NO por `sc qc`/`sc query`:
 // la salida de sc.exe está localizada (en Windows en español la línea es
 // "TIPO_INICIO", no "START_TYPE", y "EJECUTANDO", no "RUNNING"), mientras que WMI
 // devuelve valores de enumeración en inglés (Auto/Manual/Disabled/Running)
 // independientes del idioma del sistema.

 /// <summary>
 /// Consulta por WMI (una sola pasada sobre Win32_Service) el StartMode y el
 /// State de los servicios pedidos. Los que no existen no aparecen.
 /// </summary>
    private Dictionary<string, (string StartMode, string State)> QueryServicesWmi(IEnumerable<string> serviceNames)
    {
        var wanted = new HashSet<string>(serviceNames, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, StartMode, State FROM Win32_Service");
            foreach (var mo in searcher.Get())
            {
                var name = mo["Name"]?.ToString();
                if (name == null || !wanted.Contains(name)) continue;
                result[name] = (mo["StartMode"]?.ToString() ?? "", mo["State"]?.ToString() ?? "");
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"GameBoost: consulta WMI de servicios falló: {ex.Message}");
        }
        return result;
    }

    public Dictionary<string, ServiceStartState> GetServiceStates(IEnumerable<string> serviceNames)
    {
        var result = new Dictionary<string, ServiceStartState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, (startMode, _)) in QueryServicesWmi(serviceNames))
        {
            var state = startMode switch
            {
                "Disabled" => ServiceStartState.Disabled,
                "Manual" => ServiceStartState.Manual,
                "Auto" or "Boot" or "System" => ServiceStartState.Auto,
                _ => (ServiceStartState?)null
            };
            if (state.HasValue)
                result[name] = state.Value;
        }
        return result;
    }

    public Dictionary<string, bool> GetServicesRunning(IEnumerable<string> serviceNames)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, (_, state)) in QueryServicesWmi(serviceNames))
            result[name] = state.Equals("Running", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public bool IsServiceRunningNow(string serviceName)
        => GetServicesRunning(new[] { serviceName }).GetValueOrDefault(serviceName);

    public ServiceStartState SetServiceStartState(string serviceName, ServiceStartState state)
    {
        var startValue = state switch
        {
            ServiceStartState.Disabled => "disabled",
            ServiceStartState.Manual => "demand",
            _ => "auto"
        };
        RunServiceArgs($"config {serviceName} start= {startValue}");
        var actual = GetServiceStates(new[] { serviceName }).GetValueOrDefault(serviceName, state);
        _logging.LogInfo($"GameBoost: {serviceName} → {state} (quedó {actual}).");
        return actual;
    }

 /// <summary>
 /// Snapshot de todos los servicios del sistema con UNA consulta WMI (la misma
 /// tabla Win32_Service que ya se consulta por lotes, ahora sin filtro de
 /// nombres). Incluye DisplayName y Description para la UI. Corre en hilo de
 /// fondo: la página lo llama dentro de Task.Run.
 /// </summary>
    public List<SystemServiceInfo> GetAllServicesSnapshot()
    {
        var result = new List<SystemServiceInfo>(200);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, DisplayName, StartMode, State, Description FROM Win32_Service");
            foreach (var mo in searcher.Get())
            {
                try
                {
                    var name = mo["Name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    result.Add(new SystemServiceInfo(
                        name!,
                        mo["DisplayName"]?.ToString() ?? name!,
                        mo["StartMode"]?.ToString() ?? "",
                        string.Equals(mo["State"]?.ToString(), "Running", StringComparison.OrdinalIgnoreCase),
                        mo["Description"]?.ToString()));
                }
                catch { /* fila malformada: saltar */ }
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"GameBoost: snapshot de todos los servicios falló: {ex.Message}");
        }
        return result;
    }

    public Task ApplyAsync()
    {
        lock (_lock)
        {
            if (!IsEnabled)
            {
                _logging.LogDebug("GameBoost: desactivado — no se hace nada.");
                return Task.CompletedTask;
            }
            if (_active)
            {
                _logging.LogDebug("GameBoost: ya activo — se omite.");
                return Task.CompletedTask;
            }
            _active = true; // reservar: evita aplicar dos veces en paralelo
        }

        return Task.Run(() => ApplyCore());
    }

    public Task RestoreAsync()
    {
        lock (_lock)
        {
            if (!_active) return Task.CompletedTask;
        }

        return Task.Run(RestoreCore);
    }

    // ===== Aplicar =====

    private async Task ApplyCore()
    {
        try
        {
            _gameModeWarning = false;
            _logging.LogInfo("GameBoost: aplicando optimización de procesos al iniciar el juego...");

            // Lista configurable de procesos en segundo plano (tuerca del switch).
            var backgroundProcesses = GetBackgroundProcesses();
 // Servicios que el boost detiene al iniciar el juego (mantenimiento, indexado,
 // telemetría/diagnóstico e impresión). Los deshabilitados por el usuario ya no
 // corren: el snapshot los saltea solos. El switch "Servicios Optimizados
 // Automaticos" apagado = el boost no detiene servicios por partida (solo
 // gestiona procesos).
            var autoServiceOptimization = IsAutomaticServiceOptimizationEnabled;
            var userKillServices = autoServiceOptimization ? BoostStopServices : Array.Empty<string>();
            if (!autoServiceOptimization)
                _logging.LogInfo("GameBoost: optimización automática de servicios DESACTIVADA — no se detiene ningún servicio en esta partida.");

            // 1) Snapshot del estado previo, ANTES de tocar nada.
            var servicesToRestart = SnapshotRunningServices(userKillServices);
            if (autoServiceOptimization)
                _logging.LogInfo($"GameBoost: servicios a detener por partida: {string.Join(", ", userKillServices)} ({servicesToRestart.Count} corriendo ahora; se restauran al cerrar el juego).");
            var processesToRestore = SnapshotBackgroundPriorities(backgroundProcesses);
            _toastsSnapshot = ReadToastsEnabled();

            // Modo Juego de WINDOWS (no el de WinForge): verificar la infraestructura
            // ANTES de activar las reglas. Si faltan archivos (huérfano — debloat
            // extremo tipo Game Bar removida), las reglas de registro no alcanzan:
            // se omite la activación y se avisa a la UI. Si solo está deshabilitado
            // por reglas (AtlasOS), se reactiva para la partida con snapshot.
            if (_gameMode != null)
            {
                var health = await _gameMode.CheckAsync();
                if (health.Status == WindowsGameModeHealth.Orphaned)
                {
                    _gameModeWarning = true;
                    _gameModeSnapshot = null;
                    _logging.LogWarning("GameBoost: Modo Juego de WINDOWS huérfano (faltan archivos de Game Bar): no se pudo activar para la partida. Reinstalá Game Bar desde la Microsoft Store. El boost de WinForge sigue aplicando procesos/servicios.");
                }
                else
                {
                    _gameModeSnapshot = await _gameMode.EnsureEnabledForSessionAsync();
                }
            }

            // 2) Comprometer el snapshot de forma atómica con la reserva de _active:
            // si una restauración (juego cerrado muy rápido, switch apagado o la app
            // en cierre) se ejecutó mientras hacíamos el snapshot, ya no queda nada
            // que aplicar — commitear igual dejaría cambios aplicados con _active = false
            // (sin dueño que los restaure).
            bool shouldApply;
            lock (_lock)
            {
                shouldApply = _active;
                if (shouldApply)
                {
                    _servicesToRestart = servicesToRestart;
                    _processesToRestore = processesToRestore;
                }
            }

            if (!shouldApply)
            {
                _logging.LogDebug("GameBoost: se solicitó restaurar durante la preparación — se cancela la aplicación.");
                return;
            }

            // 3) Aplicar los cambios.
 // Solo se detienen los servicios seleccionados en la configuración.
            StopServices(userKillServices);
            int killed = KillUserProcesses();
            DeprioritizeBackgroundProcesses(backgroundProcesses);
            PauseToasts();

            _logging.LogInfo($"GameBoost: optimización aplicada ({servicesToRestart.Count} servicios y {processesToRestore.Count} procesos a restaurar al cerrar).");

            // Notificar a la UI con el resumen del boost aplicado (reglas de juego,
            // servicios detenidos, procesos optimizados y procesos cerrados).
            var result = new GameBoostApplyResult(
                CountRulesApplied(),
                servicesToRestart.Count,
                processesToRestore.Count,
                killed,
                _gameModeWarning);
            BoostApplied?.Invoke(result);
        }
        catch (Exception ex)
        {
            _logging.LogError("GameBoost: error al aplicar la optimización.", ex);
        }
    }

    private List<ServiceSnapshot> SnapshotRunningServices(IEnumerable<string> services)
    {
        var running = new List<ServiceSnapshot>();
 // Una sola consulta WMI para todos los servicios: StartMode + State.
        var all = QueryServicesWmi(services);
        foreach (var svc in services)
        {
            if (!all.TryGetValue(svc, out var info))
            {
                _logging.LogDebug($"GameBoost: {svc} no existe — se omite.");
                continue;
            }
            if (info.State.Equals("Running", StringComparison.OrdinalIgnoreCase))
            {
                running.Add(new ServiceSnapshot(svc, info.StartMode));
                _logging.LogDebug($"GameBoost: {svc} estaba corriendo (modo {info.StartMode}) — se restaurará al cerrar.");
            }
            else
            {
                _logging.LogDebug($"GameBoost: {svc} ya estaba detenido/deshabilitado — no se encenderá al restaurar.");
            }
        }
        return running;
    }

    private List<ProcessSnapshot> SnapshotBackgroundPriorities(List<string> backgroundProcesses)
    {
        var snap = new List<ProcessSnapshot>();
        foreach (var name in backgroundProcesses)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.HasExited) continue;
                        snap.Add(new ProcessSnapshot(p.Id, p.ProcessName, p.PriorityClass));
                    }
                    catch (Exception ex)
                    {
                        _logging.LogDebug($"GameBoost: no se pudo registrar un proceso en el snapshot: {ex.Message}");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logging.LogDebug($"GameBoost: snapshot de procesos incompleto: {ex.Message}");
            }
        }
        return snap;
    }

    private void StopServices(IEnumerable<string> services)
    {
        foreach (var svc in services)
            RunServiceCommand(svc, "stop");
    }

 /// <summary>
 /// Cierra los procesos que el usuario eligió para la lista de "cerrar". No se
 /// hace snapshot ni se restauran: el usuario los eligió cerrar, así que se
 /// quedan cerrados. Se salte procesos críticos y la propia app.
 /// </summary>
    private int KillUserProcesses()
    {
        var toKill = GetKillProcesses();
        if (toKill.Count == 0) return 0;

 // Lista de procesos que NUNCA se cierran, aunque el usuario los agregue.
        var protectedProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "MemCompression",
            "csrss", "smss", "wininit", "winlogon", "lsass", "services", "svchost",
            "dwm", "fontdrvhost", "conhost", "dllhost", "RuntimeBroker", "audiodg",
            "MsMpEng", "spoolsv", "WudfHost", "WinForge"
        };

        _logging.LogInfo($"GameBoost: cerrando {toKill.Count} procesos seleccionados por el usuario...");
        int killed = 0;
        foreach (var name in toKill)
        {
            if (protectedProcs.Contains(name))
            {
                _logging.LogDebug($"GameBoost: no se cierra '{name}' (protegido).");
                continue;
            }
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.HasExited) continue;
                        p.Kill();
                        killed++;
                        _logging.LogDebug($"GameBoost: cerrado {name} (pid {p.Id}).");
                    }
                    catch (Exception ex)
                    {
                        _logging.LogDebug($"GameBoost: no se pudo cerrar {name} (pid {p.Id}): {ex.Message}");
                    }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"GameBoost: cierre de procesos incompleto: {ex.Message}");
            }
        }
        _logging.LogInfo($"GameBoost: {killed} procesos cerrados.");
        return killed;
    }

    /// <summary>
    /// Cuenta cuántos juegos en ejecución tienen una regla de juego efectiva (sesión
    /// "Actual" o guardada) con al menos una dimensión configurada. Las reglas las
    /// aplica ProcessService al detectar el juego; acá solo se reportan para el
    /// resumen del Modo juego.
    /// </summary>
    private int CountRulesApplied()
    {
        int count = 0;
        try
        {
            foreach (var exe in _processService.RunningGameExes)
            {
                var rule = _processService.GetEffectiveRule(exe);
                if (rule != null && RuleHasContent(rule))
                    count++;
            }
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"GameBoost: no se pudieron contar las reglas aplicadas: {ex.Message}");
        }
        return count;
    }

    private static bool RuleHasContent(ProcessRule r)
        => r.CpuPriority != null || r.AffinityMask != null || r.GpuPriority != null
           || !string.IsNullOrEmpty(r.PowerPlanGuid) || r.IoPriority != null;

    private void DeprioritizeBackgroundProcesses(List<string> backgroundProcesses)
    {
        _logging.LogDebug("GameBoost: bajando prioridad y activando Efficiency Mode de procesos en segundo plano...");
        foreach (var name in backgroundProcesses)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.HasExited) continue;
                        // 1 = Below Normal ("Baja") + Efficiency Mode (EcoQoS): prioridad
                        // baja, hilos a E-cores en CPUs híbridas y power throttling. El
                        // snapshot ya guardó la prioridad original para restaurar; el
                        // Efficiency Mode se apaga explícitamente al restaurar.
                        bool ok = _processService.ApplyCpuPriority(p.Id, 1);
                        bool em = EfficiencyMode.Set(p.Id, enabled: true);
                        _logging.LogDebug($"GameBoost: {name} (pid {p.Id}) → {(ok ? "Baja" : "sin cambios")} + EM {(em ? "ON" : "no disponible")}");
                    }
                    catch (Exception ex)
                    {
                        _logging.LogDebug($"GameBoost: no se pudo cambiar prioridad de {name}: {ex.Message}");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"GameBoost: restauración de procesos incompleta: {ex.Message}");
            }
        }
    }

    // ===== Notificaciones toast =====
    // Interruptor maestro de notificaciones de Windows ("Obtener notificaciones de
    // apps y otros emisores"): 1 = habilitadas, 0 = deshabilitadas. Equivale al modo
    // foco "solo alarmas". Es HKCU: no requiere permisos extra y se revierte al
    // valor anterior exacto (o se borra la clave si antes no existía).
    private const string NotificationsKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string ToastsEnabledValue = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    private int? ReadToastsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(NotificationsKeyPath);
            return key?.GetValue(ToastsEnabledValue) is int i ? i : null;
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"GameBoost: no se pudo leer el estado de notificaciones: {ex.Message}");
            return null;
        }
    }

    private void PauseToasts()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(NotificationsKeyPath);
            key.SetValue(ToastsEnabledValue, 0, RegistryValueKind.DWord);
            _logging.LogDebug("GameBoost: notificaciones pausadas (toasts deshabilitadas).");
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"GameBoost: no se pudieron pausar las notificaciones: {ex.Message}");
        }
    }

    private void RestoreToasts()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(NotificationsKeyPath);
            if (_toastsSnapshot is int original)
                key.SetValue(ToastsEnabledValue, original, RegistryValueKind.DWord);
            else
                key.DeleteValue(ToastsEnabledValue, throwOnMissingValue: false);
            _logging.LogDebug("GameBoost: notificaciones restauradas al estado anterior.");
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"GameBoost: no se pudieron restaurar las notificaciones: {ex.Message}");
        }
    }

    // ===== Restaurar =====

    private void RestoreCore()
    {
        List<ServiceSnapshot> servicesToRestart;
        List<ProcessSnapshot> processesToRestore;

        lock (_lock)
        {
            if (!_active) return;
            _active = false;
            servicesToRestart = _servicesToRestart;
            processesToRestore = _processesToRestore;
            _servicesToRestart = new();
            _processesToRestore = new();
        }

        try
        {
            _logging.LogInfo("GameBoost: restaurando estado previo...");

            foreach (var svc in servicesToRestart)
            {
                // Si el usuario deshabilitó el servicio durante la sesión de juego,
                // NO lo re-arrancamos: no hay que "encender" lo que el usuario apagó.
                var currentState = GetServiceStates(new[] { svc.Name }).GetValueOrDefault(svc.Name);
                if (currentState == ServiceStartState.Disabled)
                {
                    _logging.LogInfo($"GameBoost: {svc.Name} está deshabilitado ahora — se omite su re-arranque.");
                    continue;
                }
                RunServiceCommand(svc.Name, "start");
            }

            foreach (var snap in processesToRestore)
                RestoreProcessPriority(snap);

            RestoreToasts();

            // Restaurar el Modo Juego de Windows al estado previo de la partida
            // (0 explícito vuelve a 0; claves ausentes se borran de nuevo).
            if (_gameMode != null)
            {
                var snapshot = _gameModeSnapshot;
                _gameModeSnapshot = null;
                _gameMode.RestoreForSessionEnd(snapshot);
            }

            _logging.LogInfo("GameBoost: estado previo restaurado.");
        }
        catch (Exception ex)
        {
            _logging.LogError("GameBoost: error al restaurar.", ex);
        }
    }

    private void RestoreProcessPriority(ProcessSnapshot snap)
    {
        try
        {
            using var p = Process.GetProcessById(snap.Pid);
            if (p.HasExited)
            {
                _logging.LogDebug($"GameBoost: {snap.Name} (pid {snap.Pid}) ya cerró — se omite su restauración.");
                return;
            }

            // El PID pudo ser reutilizado por otro proceso: no tocar algo distinto.
            if (!string.Equals(p.ProcessName, snap.Name, StringComparison.OrdinalIgnoreCase))
            {
                _logging.LogDebug($"GameBoost: pid {snap.Pid} ahora es {p.ProcessName} (antes {snap.Name}) — se omite.");
                return;
            }

            bool ok = _processService.ApplyCpuPriority(snap.Pid, PriorityCode(snap.OriginalClass));
            // Apagar Efficiency Mode explícitamente (StateMask = 0): vuelve al estado
            // normal aunque el proceso lo haya tenido activo por otro motivo.
            bool emOff = EfficiencyMode.Set(snap.Pid, enabled: false);
            _logging.LogDebug($"GameBoost: {snap.Name} (pid {snap.Pid}) restaurada a {snap.OriginalClass} → {(ok ? "OK" : "sin cambios")}, EM {(emOff ? "OFF" : "no disponible")}");
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"GameBoost: no se pudo restaurar {snap.Name} (pid {snap.Pid}): {ex.Message}");
        }
    }

    private static int PriorityCode(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.Idle => 0,
        ProcessPriorityClass.BelowNormal => 1,
        ProcessPriorityClass.Normal => 2,
        ProcessPriorityClass.AboveNormal => 3,
        ProcessPriorityClass.High => 4,
        ProcessPriorityClass.RealTime => 5,
        _ => 2
    };

    // ===== Detección de cierre del juego (WMI) =====

    private void OnRunningGamesChanged()
    {
        try
        {
            if (!IsEnabled) return;

            if (_processService.RunningGameExes.Count == 0)
            {
                _logging.LogDebug("GameBoost: no quedan juegos corriendo — restaurando.");
                _ = RestoreAsync();
            }
            else
            {
                // Red de seguridad: si el juego se lanzó sin pasar por el botón
                // "Iniciar" (p. ej. desde el launcher), aplicar igual.
                _ = ApplyAsync();
            }
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"GameBoost: RunningGamesChanged: {ex.Message}");
        }
    }

    // ===== Utilidades =====

    private bool IsServiceRunning(string serviceName)
        => GetServicesRunning(new[] { serviceName }).GetValueOrDefault(serviceName);

    private void RunServiceCommand(string serviceName, string action)
        => RunServiceArgs($"{action} {serviceName}");

 /// <summary>Ejecuta <c>sc.exe {args}</c> y registra la salida.</summary>
    private void RunServiceArgs(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                _logging.LogWarning($"GameBoost: no se pudo iniciar sc {args}");
                return;
            }

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            var text = (outTask.Result + errTask.Result).Trim();
            _logging.LogDebug($"GameBoost: sc {args} → {(string.IsNullOrEmpty(text) ? "OK" : text)}");
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"GameBoost: error al ejecutar sc {args}: {ex.Message}");
        }
    }
}
