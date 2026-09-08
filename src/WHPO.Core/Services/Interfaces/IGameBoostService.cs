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

    /// <summary>Aplica la optimización (snapshot del estado previo incluido). No hace nada si el switch está desactivado.</summary>
    Task ApplyAsync();

    /// <summary>Restaura el estado previo (servicios y prioridades). No hace nada si no hay boost activo.</summary>
    Task RestoreAsync();

    /// <summary>Lista de procesos en segundo plano a los que se aplica el boost (configurable; vacía = lista por defecto).</summary>
    List<string> GetBackgroundProcesses();

    /// <summary>Persiste la lista de procesos en segundo plano del boost.</summary>
    void SetBackgroundProcesses(List<string> processes);

    /// <summary>Lista por defecto de procesos en segundo plano (la que aplica si no hay configuración guardada).</summary>
    List<string> GetDefaultBackgroundProcesses();

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
