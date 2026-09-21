using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Snapshot de un servicio de Windows para la pestaña "Servicios" de la
/// configuración del optimizador (todos los servicios del sistema).
/// </summary>
public sealed record SystemServiceInfo(
    string Name,
    string DisplayName,
    string StartMode,
    bool IsRunning,
    string? Description);

/// <summary>Estado de un servicio en Windows, alineado con el START_TYPE del SCM.</summary>
public enum ServiceStartState
{
 /// <summary>Desactivado (START_TYPE 4 DISABLED): no puede arrancar.</summary>
    Disabled = 0,
 /// <summary>Manual (DEMAND_START): arranca cuando algo lo pide.</summary>
    Manual = 1,
 /// <summary>Automático (AUTO_START): arranca con Windows.</summary>
    Auto = 2
}

/// <summary>Resumen de lo que aplicó el Modo juego de WinForge al iniciar un juego.</summary>
public sealed record GameBoostApplyResult(
    int RulesApplied,          // juegos en ejecución con regla de juego efectiva aplicada
    int ServicesOptimized,     // servicios CONFIRMADOS como detenidos por la partida (se restauran al cerrar)
    int ProcessesOptimized,    // procesos CONFIRMADOS con prioridad baja / Efficiency Mode
    int ProcessesKilled,       // procesos CONFIRMADOS como cerrados
    bool GameModeWarning = false,                    // Modo Juego de WINDOWS no se pudo activar (huérfano/Game Bar)
    IReadOnlyList<string>? ServicesResisted = null,  // servicios que seguían corriendo al verificar
    int ProcessesResisted = 0);                      // procesos que no aceptaron la optimización

/// <summary>
/// "Modo juego de WinForge (BETA)": al lanzar un juego, pausa
/// Windows Update, detiene servicios de mantenimiento/diagnóstico (SysMain,
/// DiagTrack, etc.) y baja la prioridad de procesos en segundo plano. Al cerrar
/// el juego, restaura el estado previo exacto: solo re-arranca los servicios que
/// estaban corriendo y devuelve la prioridad original a los procesos cambiados.
/// Si el switch está desactivado, no hace nada. Toda acción se registra en el
/// log (visible cuando «Logs de desarrollo» está activo).
/// </summary>
public interface IGameBoostService
{
    /// <summary>True si la optimización al iniciar un juego está activada.</summary>
    bool IsEnabled { get; }

    /// <summary>Activa/desactiva la optimización (persistida en settings). Al desactivar con un boost activo, restaura.</summary>
    void SetEnabled(bool enabled);

 /// <summary>
 /// Switch "Servicios Optimizados Automaticos": activo por defecto. Controla si el
 /// boost detiene temporalmente los servicios del optimizador al lanzar un juego
 /// (restaurándolos al cerrarlo). Apagado, el boost no toca servicios por partida.
 /// </summary>
    bool IsAutomaticServiceOptimizationEnabled { get; set; }

 /// <summary>
 /// Switch "Pausar tareas programadas": activo por defecto. Controla si el boost
 /// deshabilita temporalmente las tareas programadas de mantenimiento (defrag,
 /// escaneos de Windows Update y Defender, diagnóstico, telemetría) al lanzar un
 /// juego y las re-habilita al cerrarlo. Apagado, no se toca ninguna tarea.
 /// </summary>
    bool IsAutomaticTaskPauseEnabled { get; set; }

    /// <summary>Aplica la optimización (snapshot del estado previo incluido). No hace nada si el switch está desactivado.</summary>
    Task ApplyAsync();

    /// <summary>Restaura el estado previo (servicios y prioridades). No hace nada si no hay boost activo.</summary>
    Task RestoreAsync();

    /// <summary>
    /// Recupera un journal de una partida que no se cerró bien (crash o kill del
    /// proceso con el boost activo) y devuelve el estado previo. Se llama UNA vez al
    /// arrancar; devuelve cuántas acciones restauró (0 = no había nada pendiente).
    /// </summary>
    Task<int> RecoverPendingJournalAsync();

    /// <summary>
    /// Se dispara cuando el boost EMPIEZA a aplicar los cambios. Los comandos del
    /// boost (sc stop, cierre de procesos) son asíncronos: solo piden la acción, y su
    /// efecto real se confirma después. Con este aviso la UI muestra "aplicando…"
    /// mientras tanto, en vez de anunciar un resultado que todavía no se verificó.
    /// </summary>
    event Action? BoostApplying;

    /// <summary>
    /// Se dispara cuando el boost se aplicó Y se verificó, con el resumen real
    /// (servicios confirmados como detenidos, procesos confirmados, etc.).
    /// </summary>
    event Action<GameBoostApplyResult>? BoostApplied;

    /// <summary>
    /// Se dispara si el boost se canceló a mitad de camino (típicamente el juego se
    /// cerró antes de poder confirmar): la UI limpia el estado "aplicando…" que ya no
    /// se va a completar.
    /// </summary>
    event Action? BoostCancelled;

    /// <summary>Lista de procesos en segundo plano a los que se aplica el boost (configurable; vacía = lista por defecto).</summary>
    List<string> GetBackgroundProcesses();

    /// <summary>Persiste la lista de procesos en segundo plano del boost.</summary>
    void SetBackgroundProcesses(List<string> processes);

    /// <summary>Lista por defecto de procesos en segundo plano (la que aplica si no hay configuración guardada).</summary>
    List<string> GetDefaultBackgroundProcesses();

    /// <summary>
    /// Lista efectiva de tareas programadas a pausar por partida (whitelist de
    /// mantenimiento + agregados del usuario). Paths completos del Task Scheduler.
    /// </summary>
    List<string> GetPauseTaskPaths();

    /// <summary>Persiste las tareas agregadas por el usuario (las de la whitelist son fijas; las protegidas se rechazan).</summary>
    void SetPauseTaskPaths(List<string> paths);

    /// <summary>Lista por defecto de tareas a pausar (la whitelist de mantenimiento).</summary>
    List<string> GetDefaultPauseTaskPaths();

 /// <summary>Lista de procesos a CERRAR al iniciar un juego (configurable; el usuario los agrega).</summary>
    List<string> GetKillProcesses();

 /// <summary>Persiste la lista de procesos a cerrar al iniciar un juego.</summary>
    void SetKillProcesses(List<string> processes);

 /// <summary>
 /// Estado actual de cada servicio gestionado, leído DIRECTAMENTE de Windows
 /// vía WMI (independiente del idioma del sistema): si un servicio no existe o
 /// no se pudo leer, no aparece en el diccionario. Incluye todos los grupos.
 /// </summary>
    Dictionary<string, ServiceStartState> GetServiceStates(IEnumerable<string> serviceNames);

 /// <summary>True si cada servicio está corriendo en este momento (consulta WMI por lotes).</summary>
    Dictionary<string, bool> GetServicesRunning(IEnumerable<string> serviceNames);

 /// <summary>True si el servicio está corriendo en este momento (consulta WMI).</summary>
    bool IsServiceRunningNow(string serviceName);

 /// <summary>
 /// Persiste el estado elegido por el usuario en Windows (sc config) y devuelve
 /// el estado real que quedó aplicado (puede diferir si falló).
 /// </summary>
    ServiceStartState SetServiceStartState(string serviceName, ServiceStartState state);

 /// <summary>
 /// Snapshot de TODOS los servicios del sistema (una sola consulta WMI): nombre,
 /// nombre para mostrar, modo de arranque, corriendo y descripción. Lo usa la
 /// pestaña "Servicios" de la configuración del optimizador para listar
 /// cualquier servicio de Windows, no solo los grupos curados.
 /// </summary>
    List<SystemServiceInfo> GetAllServicesSnapshot();
}
