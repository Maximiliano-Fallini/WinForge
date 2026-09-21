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

    // Snapshot: tareas programadas que el boost deshabilitó por la partida (solo
    // las que estaban HABILITADAS: nunca se "enciende" una que el usuario apagó).
    private List<string> _pausedTasks = new();

    // Snapshot: valor previo de MaintenanceDisabled (null = la clave no existía,
    // se borra al restaurar). _maintenanceDisabledTouched marca si se escribió.
    private int? _maintenanceDisabledSnapshot;
    private bool _maintenanceDisabledTouched;

    // Journal persistido en disco: cubre el caso que el restore en memoria no ve
    // (crash o kill del proceso con el boost activo). Ver RecoverPendingJournalAsync.
    private BoostJournalSnapshot? _journal;

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

    /// <summary>Se dispara cuando el boost empieza a aplicar los cambios (ver interfaz).</summary>
    public event Action? BoostApplying;

    /// <summary>Se dispara cuando el boost se aplicó y se verificó, con el resumen real.</summary>
    public event Action<GameBoostApplyResult>? BoostApplied;

    /// <summary>Se dispara si el boost se canceló a mitad de camino (ver interfaz).</summary>
    public event Action? BoostCancelled;

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

    // Tareas programadas que el boost deshabilita POR PARTIDA y re-habilita al
    // cerrar. Whitelist CERRADA de rutas exactas: solo mantenimiento/updates/
    // diagnóstico/telemetría. Defender: SOLO los escaneos programados y su limpieza
    // de caché — el motor/antivirus es un servicio y NO se toca (la protección
    // en vivo sigue intacta). Lo mismo con pantalla compartida/streaming: estas
    // tareas son background puro y no tocan captura, audio ni red en caliente.
    // Los paths son con el nombre ORIGINAL del Task Scheduler (en inglés en todas
    // las instalaciones: los nombres de carpeta/tarea no se localizan).
    public static readonly string[] DefaultPauseTaskPaths =
    {
        // ===== Windows Update y su mantenimiento =====
        @"\Microsoft\Windows\WindowsUpdate\Scheduled Start",
        @"\Microsoft\Windows\WindowsUpdate\UpdateAssistant",
        @"\Microsoft\Windows\WindowsUpdate\UpdateAssistantWakeup",
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan",
        @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan Static Task",
        @"\Microsoft\Windows\UpdateOrchestrator\Universal Orchestrator Start",
        @"\Microsoft\Windows\InstallService\ScanForUpdates",
        @"\Microsoft\Windows\InstallService\ScanForUpdatesAsUser",
        @"\Microsoft\Windows\InstallService\SmartRetry",
        @"\Microsoft\Windows\InstallService\WakeUpAndScan",
        @"\Microsoft\Windows\InstallService\WakeUpAndContinueUpdates",

        // ===== Defender: SOLO escaneos programados y mantenimiento de caché =====
        @"\Microsoft\Windows\Defender\Windows Defender Scheduled Scan",
        @"\Microsoft\Windows\Defender\Windows Defender Cache Maintenance",
        @"\Microsoft\Windows\Defender\Windows Defender Cleanup",
        @"\Microsoft\Windows\Defender\Windows Defender Verification",

        // ===== Disco =====
        @"\Microsoft\Windows\Defrag\ScheduledDefrag",          // defrag/TRIM
        @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",

        // ===== Diagnóstico / telemetría =====
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"\Microsoft\Windows\Application Experience\PcaPatchDbTask",
        @"\Microsoft\Windows\Application Experience\SdbinstMergeDbTasks",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
        @"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
        @"\Microsoft\Windows\Feedback\Siuf\DmClient",
        @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
        @"\Microsoft\Windows\Autochk\Proxy",
        @"\Microsoft\Windows\Maintenance\WinSAT",
    };

    // Tareas que NUNCA se deshabilitan, ni siquiera si el usuario las agrega a su
    // lista: de seguridad de sesión (no de escaneo), de la propia app y de
    // inicio/cierre del sistema. La detección es por path normalizado.
    private static readonly string[] ProtectedTaskFragments =
    {
        @"\Microsoft\Windows\SessionEnv",
        @"\Microsoft\Windows\CloudExperienceHost",
        @"\Microsoft\Windows\PushToInstall",
        @"\Microsoft\Windows\Wininet",
        @"\Microsoft\Windows\Wininet2",
        @"\Microsoft\Windows\WinJS",
        @"\Microsoft\Windows\AppxDeploymentClient",
        @"\Microsoft\Windows\StateRepository",
        @"\Microsoft\Windows\WinForge",
        @"\WinForge\",
    };

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
    private const string PauseTasksKey = "gameboost.pauseTasks";
    private const string AutoTaskPauseKey = "gameboost.automaticTaskPause";

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

    /// <summary>
    /// Switch "Pausar tareas programadas" (pestaña Servicios de la configuración
    /// del boost): activo por defecto. Cuando está activo, el boost deshabilita
    /// temporalmente las tareas programadas de mantenimiento/pausables (defrag,
    /// escaneos de Windows Update y Defender, diagnóstico, telemetría) al lanzar un
    /// juego y las re-habilita al cerrarlo. Es INDEPENDIENTE del cambio persistente
    /// de tareas: nunca se toca nada fuera de la whitelist + lista del usuario.
    /// No reemplaza la optimización de servicios (algunos corren por trigger):
    /// se complementan.
    /// </summary>
    public bool IsAutomaticTaskPauseEnabled
    {
        get => _settings.Get(AutoTaskPauseKey, true);
        set
        {
            _settings.Set(AutoTaskPauseKey, value);
            _settings.Save();
            _logging.LogInfo($"GameBoost: pausa de tareas programadas {(value ? "activada" : "desactivada")} (por partida).");
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

    // ===== Tareas programadas (configuración) =====

    /// <summary>
    /// Lista efectiva de tareas a pausar por partida: defaults FIJOS + agregados
    /// del usuario (mismo patrón que los procesos de segundo plano: los defaults
    /// viven en código y el setting solo contiene los agregados).
    /// </summary>
    public List<string> GetPauseTaskPaths()
    {
        var result = new List<string>(DefaultPauseTaskPaths);
        var saved = _settings.Get(PauseTasksKey, new List<string>());
        if (saved != null)
        {
            foreach (var raw in saved)
            {
                var clean = NormalizeTaskPath(raw);
                if (clean.Length == 0) continue;
                if (!result.Contains(clean, StringComparer.OrdinalIgnoreCase))
                    result.Add(clean);
            }
        }
        return result;
    }

    /// <summary>Persiste las tareas agregadas por el usuario (defaults fuera: son fijos).</summary>
    public void SetPauseTaskPaths(List<string> paths)
    {
        var defaults = new HashSet<string>(DefaultPauseTaskPaths, StringComparer.OrdinalIgnoreCase);
        var clean = (paths ?? new())
            .Select(NormalizeTaskPath)
            .Where(p => p.Length > 0 && !defaults.Contains(p) && !IsProtectedTaskPath(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settings.Set(PauseTasksKey, clean);
        _settings.Save();
        _logging.LogInfo($"GameBoost: tareas agregadas a la pausa por partida ({clean.Count}).");
    }

    public List<string> GetDefaultPauseTaskPaths()
        => new(DefaultPauseTaskPaths);

    /// <summary>
    /// Normaliza el path de una tarea: trim, sin comillas, separadores '\' y
    /// prefijo de raíz. Vacío si no parece un path de tarea válido.
    /// </summary>
    private static string NormalizeTaskPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var clean = raw.Trim().Trim('"').Replace('/', '\\');
        if (!clean.StartsWith("\\", StringComparison.Ordinal))
            clean = "\\" + clean;
        clean = clean.TrimEnd('\\');
        // Mínimo "\Carpeta\Tarea": un path de un solo nivel no identifica tarea.
        if (clean.Count(c => c == '\\') < 2) return "";
        return clean;
    }

    /// <summary>True si el path de tarea cae en la zona protegida (no se pausa nunca).</summary>
    private static bool IsProtectedTaskPath(string taskPath)
    {
        foreach (var fragment in ProtectedTaskFragments)
            if (taskPath.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

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
        // True una vez que se avisó "aplicando…": solo entonces tiene sentido avisar
        // que se canceló (si el error ocurre antes, la UI nunca entró en ese estado).
        bool applying = false;
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

            // Avisar a la UI que empieza la fase de cambios: de acá en adelante los
            // comandos son asíncronos y todavía no hay ningún resultado confirmado.
            applying = true;
            BoostApplying?.Invoke();

            // Journal (crash-safe): se persiste ANTES de mutar nada y se actualiza
            // después de cada fase. Si WinForge crashea o la matan con el boost
            // activo, el próximo arranque detecta el journal y devuelve el estado
            // previo: los servicios/tareas no quedan colgados hasta reiniciar Windows.
            var journal = new BoostJournalSnapshot
            {
                SessionToken = Guid.NewGuid().ToString("N"),
                AppPid = Environment.ProcessId,
                AppStartTicks = GameBoostJournal.CurrentProcessStartTicks(),
                StartedUtc = DateTime.UtcNow.ToString("o"),
                RunningGameExes = _processService.RunningGameExes.ToList(),
                Services = servicesToRestart
                    .Select(s => new BoostJournalService { Name = s.Name, StartType = s.StartType, WasRunning = true })
                    .ToList(),
                Processes = processesToRestore
                    .Select(p => new BoostJournalProcess { Pid = p.Pid, Name = p.Name, OriginalPriority = p.OriginalClass })
                    .ToList(),
                ToastsSnapshot = _toastsSnapshot,
                GameModeSnapshot = _gameModeSnapshot,
            };
            _journal = journal;
            GameBoostJournal.Save(journal, _logging);

            // 3) Pedir los cambios (solo se detienen los servicios elegidos en la
            // configuración; sc stop solo PIDE la parada, no espera a que ocurra).
            StopServices(userKillServices);
            var killRequestedPids = KillUserProcesses();
            DeprioritizeBackgroundProcesses(backgroundProcesses);
            PauseToasts();
            journal.AppliedSteps.Add("services");
            journal.AppliedSteps.Add("processes");
            journal.ToastsPaused = true;
            journal.AppliedSteps.Add("toasts");
            journal.GameModeTouched = _gameModeSnapshot is not null;
            if (journal.GameModeTouched) journal.AppliedSteps.Add("gamemode");
            GameBoostJournal.Save(journal, _logging);

            // Tareas programadas + Mantenimiento automático: mismo trato que los
            // servicios (por partida, con snapshot previo). Apagado el switch, el
            // boost no toca ninguna tarea ni el mecanismo global de mantenimiento.
            if (IsAutomaticTaskPauseEnabled)
            {
                var pauseTaskPaths = GetPauseTaskPaths();
                _logging.LogInfo($"GameBoost: tareas a pausar por partida: {string.Join(", ", pauseTaskPaths)} ({pauseTaskPaths.Count}).");
                _pausedTasks = PauseScheduledTasks();
                PauseAutomaticMaintenance();
                _logging.LogInfo($"GameBoost: {_pausedTasks.Count} tareas programadas pausadas hasta cerrar el juego.");

                journal.Tasks = _pausedTasks
                    .Select(p => new BoostJournalTask { Path = p, WasEnabled = true })
                    .ToList();
                journal.MaintenanceDisabledTouched = _maintenanceDisabledTouched;
                journal.MaintenanceDisabledSnapshot = _maintenanceDisabledSnapshot;
                journal.AppliedSteps.Add("tasks");
                if (_maintenanceDisabledTouched) journal.AppliedSteps.Add("maintenance");
                GameBoostJournal.Save(journal, _logging);
            }
            else
            {
                _logging.LogInfo("GameBoost: pausa de tareas programadas DESACTIVADA — no se deshabilita ninguna tarea en esta partida.");
            }

            _logging.LogInfo($"GameBoost: optimización solicitada ({servicesToRestart.Count} servicios y {processesToRestore.Count} procesos a restaurar al cerrar).");

            // 4) VERIFICAR el resultado real antes de reportarlo. `sc stop` vuelve al
            // instante con el servicio todavía en STOP_PENDING y Process.Kill() no
            // espera a que el proceso termine de salir: hasta acá solo había pedidos,
            // no resultados. Reportarlos como "optimizado" era adelantarle un éxito al
            // usuario que todavía no había ocurrido.
            var servicesResisted = await VerifyServicesStoppedAsync(servicesToRestart);

            if (!IsActive)
            {
                // El juego se cerró (o el usuario apagó el switch) mientras se
                // verificaba: RestoreCore ya devolvió todo al estado previo, así que no
                // hay ningún resumen de partida que reportar.
                _logging.LogInfo("GameBoost: se restauró durante la verificación — no se reporta resumen de la partida.");
                BoostCancelled?.Invoke();
                return;
            }

            int killedConfirmed = CountConfirmedKilled(killRequestedPids);
            var (optimizedConfirmed, processesResisted) = CountConfirmedOptimized(processesToRestore);
            int servicesStopped = servicesToRestart.Count - servicesResisted.Count;

            _logging.LogInfo(
                $"GameBoost: optimización confirmada ({servicesStopped} de {servicesToRestart.Count} servicios detenidos, " +
                $"{optimizedConfirmed} procesos optimizados, {killedConfirmed} cerrados" +
                (servicesResisted.Count > 0 ? $"; siguen corriendo: {string.Join(", ", servicesResisted)}" : "") + ").");

            // Notificar a la UI con el resumen VERIFICADO del boost (reglas de juego,
            // servicios detenidos, procesos optimizados y procesos cerrados).
            var result = new GameBoostApplyResult(
                CountRulesApplied(),
                servicesStopped,
                optimizedConfirmed,
                killedConfirmed,
                _gameModeWarning,
                servicesResisted,
                processesResisted);
            BoostApplied?.Invoke(result);
        }
        catch (Exception ex)
        {
            _logging.LogError("GameBoost: error al aplicar la optimización.", ex);
            // La UI quedó en "aplicando…": avisar que no va a llegar ningún resumen.
            if (applying) BoostCancelled?.Invoke();
        }
    }

    // ===== Verificación del resultado real =====
    // Los comandos del boost son asíncronos (ver más arriba), así que el resumen que
    // ve el usuario se arma recién después de CONFIRMAR el estado, y lo que no se pudo
    // confirmar se reporta aparte en vez de contarse como logro.

    private const int VerifyTimeoutMs = 8000; // plazo máximo para confirmar los servicios
    private const int VerifyPollMs = 400;     // intervalo entre consultas WMI

    private bool IsActive
    {
        get { lock (_lock) return _active; }
    }

    /// <summary>
    /// Espera (con plazo) a que los servicios del snapshot dejen de estar corriendo y
    /// devuelve los que seguían corriendo: puede ser normal (Windows los reinicia por
    /// trigger-start) o un stop que no se concretó.
    /// </summary>
    private async Task<List<string>> VerifyServicesStoppedAsync(List<ServiceSnapshot> services)
    {
        var names = services.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return new();

        var deadline = DateTime.UtcNow.AddMilliseconds(VerifyTimeoutMs);
        while (true)
        {
            var running = GetServicesRunning(names);
            var pending = names.Where(n => running.GetValueOrDefault(n)).ToList();
            if (pending.Count == 0) return pending;          // todos confirmados detenidos
            if (DateTime.UtcNow >= deadline) return pending;  // se agotó el plazo
            if (!IsActive) return pending;                    // se restauró: cortar acá
            await Task.Delay(VerifyPollMs);
        }
    }

    /// <summary>Cuenta los procesos a los que se les pidió cerrar y ya no existen.</summary>
    private int CountConfirmedKilled(List<int> requestedPids)
    {
        int confirmed = 0;
        foreach (var pid in requestedPids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) confirmed++;
                else _logging.LogDebug($"GameBoost: el proceso {p.ProcessName} (pid {pid}) sigue vivo tras el cierre solicitado.");
            }
            catch (ArgumentException)
            {
                confirmed++; // el PID ya no existe: el cierre se concretó
            }
            catch (Exception ex)
            {
                _logging.LogDebug($"GameBoost: no se pudo verificar el cierre del pid {pid}: {ex.Message}");
            }
        }
        return confirmed;
    }

    /// <summary>
    /// Confirma cuántos de los procesos del snapshot quedaron realmente optimizados
    /// (prioridad baja o Efficiency Mode activo). Los que ya no existen no se cuentan, y
    /// los que siguen en prioridad normal se reportan como no confirmados (típico en
    /// procesos protegidos por anti-cheat, que rechazan el cambio).
    /// </summary>
    private (int Optimized, int Resisted) CountConfirmedOptimized(List<ProcessSnapshot> snapshot)
    {
        int optimized = 0, resisted = 0;
        foreach (var snap in snapshot)
        {
            try
            {
                using var p = Process.GetProcessById(snap.Pid);
                if (p.HasExited) continue;
                // El PID pudo ser reutilizado por otro proceso: no contar algo distinto.
                if (!string.Equals(p.ProcessName, snap.Name, StringComparison.OrdinalIgnoreCase)) continue;

                bool lowPriority = p.PriorityClass is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal;
                bool eco = EfficiencyMode.IsEnabled(snap.Pid) == true;
                if (lowPriority || eco) optimized++;
                else resisted++;
            }
            catch (Exception ex)
            {
                _logging.LogDebug($"GameBoost: {snap.Name} (pid {snap.Pid}) no se pudo verificar: {ex.Message}");
            }
        }
        return (optimized, resisted);
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
    private List<int> KillUserProcesses()
    {
        var toKill = GetKillProcesses();
        if (toKill.Count == 0) return new();

 // Lista de procesos que NUNCA se cierran, aunque el usuario los agregue.
        var protectedProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "MemCompression",
            "csrss", "smss", "wininit", "winlogon", "lsass", "services", "svchost",
            "dwm", "fontdrvhost", "conhost", "dllhost", "RuntimeBroker", "audiodg",
            "MsMpEng", "spoolsv", "WudfHost", "WinForge"
        };

        _logging.LogInfo($"GameBoost: cerrando {toKill.Count} procesos seleccionados por el usuario...");
        var requested = new List<int>();
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
                        requested.Add(p.Id);
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
        _logging.LogInfo($"GameBoost: {requested.Count} cierres solicitados.");
        return requested;
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

    // ===== Journal: recuperación tras un cierre inesperado =====

    /// <summary>
    /// Si quedó un journal de una partida que no se cerró bien (crash o kill del
    /// proceso), restaura el estado previo y lo borra. Se llama UNA vez al arrancar.
    /// Devuelve la cantidad de acciones restauradas (0 = no había nada pendiente).
    /// </summary>
    public async Task<int> RecoverPendingJournalAsync()
    {
        var journal = GameBoostJournal.Load(_logging);
        if (journal == null) return 0;

        // Journal de ESTA misma instancia viva: no hay nada que recuperar (la app
        // sigue corriendo y su boost se restaura por el camino normal).
        if (journal.AppPid == Environment.ProcessId
            && journal.AppStartTicks == GameBoostJournal.CurrentProcessStartTicks())
            return 0;

        _logging.LogWarning($"GameBoost: journal de una partida no cerrada (pasos: {string.Join(", ", journal.AppliedSteps)}) — restaurando el estado previo.");
        int restored = 0;
        try
        {
            restored = await Task.Run(() => RestoreFromJournal(journal));
        }
        catch (Exception ex)
        {
            _logging.LogError("GameBoost: falló la recuperación del journal.", ex);
        }
        GameBoostJournal.Delete(_logging);
        _logging.LogInfo($"GameBoost: journal recuperado ({restored} acciones restauradas).");
        return restored;
    }

    /// <summary>Restaura el estado guardado en un journal (idempotente, best-effort).</summary>
    private int RestoreFromJournal(BoostJournalSnapshot j)
    {
        int restored = 0;

        foreach (var svc in j.Services)
        {
            if (!svc.WasRunning) continue;
            var currentState = GetServiceStates(new[] { svc.Name }).GetValueOrDefault(svc.Name);
            if (currentState == ServiceStartState.Disabled)
            {
                _logging.LogInfo($"GameBoost: {svc.Name} está deshabilitado ahora — se omite su re-arranque.");
                continue;
            }
            RunServiceCommand(svc.Name, "start");
            restored++;
        }

        foreach (var p in j.Processes)
        {
            RestoreProcessPriority(new ProcessSnapshot(p.Pid, p.Name, p.OriginalPriority));
            restored++;
        }

        foreach (var t in j.Tasks)
            if (t.WasEnabled && ResumeTask(t.Path)) restored++;

        if (j.MaintenanceDisabledTouched)
            RestoreAutomaticMaintenance(j.MaintenanceDisabledSnapshot);

        if (j.ToastsPaused)
        {
            _toastsSnapshot = j.ToastsSnapshot;
            RestoreToasts();
        }

        if (_gameMode != null && j.GameModeTouched)
            _gameMode.RestoreForSessionEnd(j.GameModeSnapshot);

        return restored;
    }

    // ===== Tareas programadas y Mantenimiento automático (por partida) =====
    // Se consultan y cambian con schtasks.exe: la salida /XML trae <Enabled> en
    // inglés (independiente del idioma del sistema — misma lección que WMI vs sc
    // localizado). Todo es best-effort: un fallo de una tarea no rompe el boost.

    /// <summary>
    /// Deshabilita las tareas de la lista (whitelist + usuario) y devuelve las que
    /// quedaron deshabilitadas. Solo se registran las que estaban HABILITADAS (una
    /// tarea ya apagada por el usuario no se "enciende" al restaurar).
    /// </summary>
    private List<string> PauseScheduledTasks()
    {
        var paused = new List<string>();
        try
        {
            foreach (var path in GetPauseTaskPaths())
            {
                if (IsProtectedTaskPath(path))
                {
                    _logging.LogDebug($"GameBoost: tarea '{path}' protegida — se omite.");
                    continue;
                }
                if (!TaskExists(path))
                {
                    _logging.LogDebug($"GameBoost: tarea '{path}' no existe — se omite.");
                    continue;
                }
                if (!IsTaskEnabled(path))
                {
                    _logging.LogDebug($"GameBoost: tarea '{path}' ya estaba deshabilitada — no se toca.");
                    continue;
                }
                if (RunSchtasks($"/Change /TN \"{path}\" /DISABLE"))
                {
                    paused.Add(path);
                    _logging.LogDebug($"GameBoost: tarea '{path}' deshabilitada por la partida.");
                }
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"GameBoost: pausa de tareas programadas incompleta: {ex.Message}");
        }
        return paused;
    }

    /// <summary>Re-habilita una tarea al restaurar (best-effort, idempotente).</summary>
    private bool ResumeTask(string taskPath)
    {
        if (!TaskExists(taskPath))
        {
            _logging.LogDebug($"GameBoost: tarea '{taskPath}' ya no existe al restaurar — se omite su re-habilitación.");
            return false; // la tarea pudo desinstalarse
        }
        var ok = RunSchtasks($"/Change /TN \"{taskPath}\" /ENABLE");
        if (ok)
            _logging.LogDebug($"GameBoost: tarea '{taskPath}' re-habilitada.");
        return ok;
    }

    /// <summary>¿Existe la tarea? (schtasks /Query, exit code 0 = existe).</summary>
    private bool TaskExists(string taskPath)
        => RunSchtasks($"/Query /TN \"{taskPath}\"");

    /// <summary>
    /// ¿Está habilitada la tarea? Lee el XML de la tarea (schtasks /Query /XML).
    /// El <Enabled> de la TAREA vive dentro de <Settings>: los triggers traen el
    /// suyo ANTES en el documento y su valor no es el de la tarea. Tag ausente =
    /// habilitada (default del esquema del Task Scheduler — las tareas recién
    /// creadas no lo escriben). XML ilegible → false ("no habilitada"): la tarea
    /// se saltea y no se toca, que es el lado seguro.
    /// </summary>
    private static bool IsTaskEnabled(string taskPath)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{taskPath}\" /XML")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p == null) return false;
            var xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return false;
            var settingsStart = xml.IndexOf("<Settings", StringComparison.OrdinalIgnoreCase);
            if (settingsStart < 0) return false;
            var settingsEnd = xml.IndexOf("</Settings>", settingsStart, StringComparison.OrdinalIgnoreCase);
            if (settingsEnd < 0) return false;
            var scope = xml.Substring(settingsStart, settingsEnd - settingsStart);
            var tagStart = scope.IndexOf("<Enabled>", StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0) return true; // ausente = habilitada (default del esquema)
            var tagEnd = scope.IndexOf("</Enabled>", tagStart, StringComparison.OrdinalIgnoreCase);
            if (tagEnd < 0) return false;
            var value = scope.Substring(tagStart + "<Enabled>".Length, tagEnd - (tagStart + "<Enabled>".Length)).Trim();
            return !value.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ===== Mantenimiento automático de Windows (paraguas) =====
    // Interruptor global del Automatic Maintenance: 1 = deshabilitado. Snapshot y
    // restauración idénticos al patrón de las toasts: valor previo exacto, y si la
    // clave no existía se BORRA (el default de Windows es "activado"). Si el
    // usuario ya lo tenía en 1, no se toca nada.
    private const string MaintenanceKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";
    private const string MaintenanceDisabledValue = "MaintenanceDisabled";

    /// <summary>Deshabilita el Mantenimiento automático si estaba activo. Devuelve true si se escribió.</summary>
    private bool PauseAutomaticMaintenance()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MaintenanceKeyPath, writable: true);
            if (key == null)
            {
                _logging.LogDebug("GameBoost: clave de Mantenimiento automático inexistente — se omite el paraguas.");
                return false;
            }
            var current = key.GetValue(MaintenanceDisabledValue) is int i ? i : (int?)null;
            if (current == 1)
            {
                _logging.LogDebug("GameBoost: el Mantenimiento automático ya estaba deshabilitado — no se toca.");
                return false;
            }
            _maintenanceDisabledSnapshot = current;
            _maintenanceDisabledTouched = true;
            key.SetValue(MaintenanceDisabledValue, 1, RegistryValueKind.DWord);
            _logging.LogDebug("GameBoost: Mantenimiento automático pausado por la partida.");
            return true;
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"GameBoost: no se pudo pausar el Mantenimiento automático: {ex.Message}");
            return false;
        }
    }

    /// <summary>Restaura el Mantenimiento automático al estado previo exacto (o borra la clave).</summary>
    private void RestoreAutomaticMaintenance(int? snapshot)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MaintenanceKeyPath, writable: true);
            if (key == null) return;
            if (snapshot is int original)
                key.SetValue(MaintenanceDisabledValue, original, RegistryValueKind.DWord);
            else
                key.DeleteValue(MaintenanceDisabledValue, throwOnMissingValue: false);
            _logging.LogDebug("GameBoost: Mantenimiento automático restaurado al estado anterior.");
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"GameBoost: no se pudo restaurar el Mantenimiento automático: {ex.Message}");
        }
    }

    /// <summary>
    /// Ejecuta schtasks.exe y devuelve true si terminó con exit code 0. La salida
    /// se registra (debug) para diagnóstico.
    /// </summary>
    private bool RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                _logging.LogWarning($"GameBoost: no se pudo iniciar schtasks {args}");
                return false;
            }

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            var text = (outTask.Result + errTask.Result).Trim();
            if (p.ExitCode != 0)
                _logging.LogDebug($"GameBoost: schtasks {args} → exit {p.ExitCode}: {text}");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"GameBoost: error al ejecutar schtasks {args}: {ex.Message}");
            return false;
        }
    }

    // ===== Restaurar =====

    private void RestoreCore()
    {
        List<ServiceSnapshot> servicesToRestart;
        List<ProcessSnapshot> processesToRestore;
        List<string> pausedTasks;
        bool maintenanceTouched;
        int? maintenanceSnapshot;

        lock (_lock)
        {
            if (!_active) return;
            _active = false;
            servicesToRestart = _servicesToRestart;
            processesToRestore = _processesToRestore;
            _servicesToRestart = new();
            _processesToRestore = new();
            pausedTasks = _pausedTasks;
            _pausedTasks = new();
            maintenanceTouched = _maintenanceDisabledTouched;
            maintenanceSnapshot = _maintenanceDisabledSnapshot;
            _maintenanceDisabledTouched = false;
            _maintenanceDisabledSnapshot = null;
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

            // Tareas programadas: re-habilitar SOLO las que estaban habilitadas (el
            // snapshot no guarda las que ya estaban deshabilitadas: nunca se
            // "enciende" algo que el usuario apagó). Mantenimiento automático:
            // devolver el valor exacto previo del registro.
            int tasksResumed = 0;
            foreach (var task in pausedTasks)
                if (ResumeTask(task)) tasksResumed++;
            if (maintenanceTouched)
                RestoreAutomaticMaintenance(maintenanceSnapshot);

            // Restaurar el Modo Juego de Windows al estado previo de la partida
            // (0 explícito vuelve a 0; claves ausentes se borran de nuevo).
            if (_gameMode != null)
            {
                var snapshot = _gameModeSnapshot;
                _gameModeSnapshot = null;
                _gameMode.RestoreForSessionEnd(snapshot);
            }

            _logging.LogInfo($"GameBoost: estado previo restaurado ({tasksResumed} tareas re-habilitadas).");

            // Restauración completa: el journal ya no hace falta (si quedara, el
            // próximo arranque intentaría restaurar algo que ya está restaurado).
            GameBoostJournal.Delete(_logging);
            _journal = null;
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
