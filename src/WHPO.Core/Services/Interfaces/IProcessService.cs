using System.Diagnostics;

namespace WHPO.Core.Services.Interfaces;

/// <summary>Información de un proceso detectado (aplicación/juego con ventana visible).</summary>
public record ProcessAppInfo(
    int Id,
    string ProcessName,     // nombre sin extensión (ej. "chrome")
    string ExeFileName,     // nombre del archivo (ej. "chrome.exe")
    string? Title,          // título de la ventana principal
    double CpuPercent,
    double WorkingSetMB);

/// <summary>
/// Regla guardada para un ejecutable. Todos los campos null = no configurado
/// (default): no se toca nada del proceso salvo lo que el usuario elija.
/// </summary>
public record ProcessRule(
    int? CpuPriority,    // 0=Idle, 1=BelowNormal, 2=Normal, 3=AboveNormal, 4=High, 5=RealTime
    long? AffinityMask,  // máscara de núcleos (bit i = núcleo i)
    int? GpuPriority,    // 2=BelowNormal, 3=Normal, 4=AboveNormal
    string? PowerPlanGuid = null, // plan de energía a activar mientras el juego corre (null = no tocar)
    int? IoPriority = null);      // prioridad E/S: 0=VeryLow, 1=Low, 2=Normal, 3=High, 4=Critical

/// <summary>Qué partes de una regla no se pudieron aplicar a un proceso vivo (anti-cheat, proceso cerrado…).</summary>
public record RuleApplyFeedback(bool CpuFailed, bool AffinityFailed, bool GpuFailed, bool IoFailed = false)
{
    public bool AnyFailed => CpuFailed || AffinityFailed || GpuFailed || IoFailed;
}

/// <summary>
/// Estado de la regla de un juego para la UI de la biblioteca. Waiting incluye el
/// tramo launcher→juego (el proceso real todavía no apareció); Applied dice a qué
/// proceso se le aplicó; Failed es el timeout sin poder identificarlo.
/// </summary>
public enum GameRuleState
{
    None,
    Waiting,
    Applied,
    Failed
}

/// <summary>
/// Último estado conocido de la regla de un juego. <paramref name="Exe"/> es la clave
/// de la regla (el exe de la biblioteca), no necesariamente el proceso real: con un
/// launcher de por medio, <paramref name="ProcessName"/> es el proceso al que se le
/// aplicó la regla. <paramref name="Limited"/> indica que alguna dimensión no se pudo
/// aplicar (proceso protegido por anti-cheat).
/// </summary>
public sealed record GameRuleStatus(
    string Exe,
    GameRuleState State,
    string? Launcher = null,
    string? ProcessName = null,
    bool Limited = false,
    // La coincidencia fue por nombre/alias o por la ruta real aprendida, no solo
    // por estar dentro de la carpeta (evita que un launcher intermedio quede como
    // estado final si luego aparece el juego).
    bool Strong = false);

/// <summary>
/// Gestión de procesos: detecta las apps/juegos en ejecución,
/// aplica prioridad de CPU, afinidad de núcleos y prioridad de GPU, y persiste las
/// reglas por ejecutable. Nada se aplica sin una regla guardada (no se hardcodea
/// ningún valor a ningún proceso).
/// </summary>
public interface IProcessService
{



    /// <summary>Busca un proceso en ejecución por nombre de ejecutable (para añadir manual).</summary>
    ProcessAppInfo? FindProcess(string exeFileName);

    /// <summary>Aplica prioridad de CPU; true si se pudo (false si el proceso está protegido o se cerró).</summary>
    bool ApplyCpuPriority(int pid, int priority);
    /// <summary>Aplica afinidad de núcleos; true si se pudo.</summary>
    bool ApplyAffinity(int pid, long mask);

    /// <summary>Máscara de afinidad actual del proceso (0 si no se puede leer). Funciona en procesos protegidos.</summary>
    long GetAffinity(int pid);
    /// <summary>Aplica prioridad de GPU; true si se pudo.</summary>
    bool ApplyGpuPriority(int pid, int priority);

    /// <summary>true si el proceso se puede abrir con PROCESS_SET_INFORMATION (el derecho que exigen
    /// todas las aplicaciones en vivo). Los procesos kernel/sistema (System, lsass, csrss, servicios…)
    /// deniegan la apertura incluso elevado: false.</summary>
    bool CanOpenForModify(int pid);

    /// <summary>true si el exe es un componente del sistema (vive en System32/SysWOW64): a esos
    /// no se les escribe prioridad de nacimiento (PerfOptions se aplica por NOMBRE y afectaría a
    /// todos los procesos con ese nombre), así que solo pueden cambiarse EN VIVO.</summary>
    bool IsSystemProcessName(string exe);

    /// <summary>Lee la clase de prioridad de GPU real del proceso (D3DKMT): 1=Idle, 2=BelowNormal, 3=Normal, 4=AboveNormal, 5=High, 6=Realtime; null si no se puede.</summary>
    int? GetGpuPriority(int pid);
    /// <summary>NTSTATUS del último GetGpuPriority (0 = éxito); útil para mostrar el motivo del fallo.</summary>
    int LastGpuPriorityStatus { get; }

    /// <summary>Aplica prioridad de E/S (IO_PRIORITY_HINT: 0=VeryLow, 1=Low, 2=Normal, 3=High, 4=Critical); true si se pudo.</summary>
    bool ApplyIoPriority(int pid, int priority);
    /// <summary>Prioridad de E/S actual del proceso (NtQueryInformationProcess): 0=VeryLow, 1=Low, 2=Normal, 3=High, 4=Critical; null si no se puede leer.</summary>
    int? GetIoPriority(int pid);

    /// <summary>Busca el proceso en ejecución de un juego, matcheando por nombre o por ruta dentro de su carpeta de instalación (launchers cuyo proceso real tiene otro nombre).</summary>
    ProcessAppInfo? FindRunningProcess(string exe);

    /// <summary>Todos los procesos corriendo que matchean una regla (nombre o ruta de instalación).</summary>
    List<ProcessAppInfo> FindRunningProcessesForRule(string ruleExe);

    /// <summary>Ruta del ejecutable de un proceso (con fallback para procesos protegidos por anti-cheat).</summary>
    string? GetProcessPath(Process process);

    /// <summary>Aplica una regla a un proceso si sigue vivo (si no, no hace nada).</summary>
    void ApplyRule(ProcessAppInfo app, ProcessRule rule);

    /// <summary>Aplica una regla y devuelve qué partes fallaron (para avisar al usuario).</summary>
    RuleApplyFeedback ApplyRuleWithFeedback(ProcessAppInfo app, ProcessRule rule);

    /// <summary>
    /// Sigue la cadena de apertura del juego aplicando su regla efectiva (la de
    /// sesión "Actual" gana sobre la guardada) mientras arranca: busca el proceso
    /// real por nombre, alias o carpeta de instalación, sin aplicar la regla al
    /// launcher. La ventana de seguimiento es de 3 minutos; cada proceso recibe la
    /// regla una sola vez. Es la red de seguridad de los eventos WMI para la cadena
    /// launcher→juego.
    /// </summary>
    void ApplyLaunchChainRule(string exeFileName);

    /// <summary>
    /// Abre una sesión de lanzamiento para un juego con regla: desde este momento se
    /// sigue la cadena launcher→juego y se aplica la regla al proceso real cuando
    /// aparece. Es idempotente (GameLauncher la abre antes de lanzar y
    /// ApplyLaunchChainRule la vuelve a pedir después). No persiste nada por juego:
    /// el launcher es un detalle de runtime, no una configuración.
    /// </summary>
    void BeginLaunchSession(string exeFileName, string? launcher = null, string? installPath = null);

    /// <summary>Último estado conocido de la regla de un juego (null si nunca se evaluó).</summary>
    GameRuleStatus? GetGameRuleStatus(string exeFileName);

    /// <summary>Tiempo que queda de la ventana de seguimiento launcher→juego (null si no hay sesión activa).</summary>
    TimeSpan? GetLaunchSessionRemaining(string exeFileName);

    /// <summary>Se dispara cuando cambia el estado de la regla de un juego (para la UI de la biblioteca).</summary>
    event Action<GameRuleStatus>? GameRuleStatusChanged;

    // ===== Reglas persistidas por ejecutable =====
    Dictionary<string, ProcessRule> GetRules();
    /// <summary>Copia cacheada de las reglas (se invalida en cada SaveRule/RemoveRule): evita parsear el JSON en cada tick de la tabla de procesos.</summary>
    Dictionary<string, ProcessRule> GetRulesCached();
    void SaveRule(string exe, ProcessRule rule);
    void RemoveRule(string exe);

    /// <summary>Retira la prioridad de nacimiento que la app escribió para un juego que ya
    /// no existe (su carpeta anotada desapareció). La regla guardada se conserva: si el
    /// juego vuelve, la prioridad se reescribe sola. No hace nada si el juego sigue en
    /// disco o si nunca se le escribió nada.</summary>
    void CleanupBirthPriorityForMissingGame(string exe);

    // ===== Reglas de sesión (solo la apertura actual del juego) =====
    /// <summary>Regla de sesión: se aplica solo mientras el juego esté abierto y NO se persiste (ni escribe en el registro). Una regla toda null la elimina.</summary>
    void SetSessionRule(string exe, ProcessRule rule);
    /// <summary>Regla de sesión actual del exe (null si no hay).</summary>
    ProcessRule? GetSessionRule(string exe);
    /// <summary>Regla efectiva: la de sesión con los campos de la guardada como respaldo (la sesión gana campo por campo).</summary>
    ProcessRule? GetEffectiveRule(string exe);
    /// <summary>Elimina la regla de sesión del exe.</summary>
    void ClearSessionRule(string exe);

    // ===== Estado de ejecución (eventos WMI, sin polling) =====
    /// <summary>Se dispara cuando cambia el conjunto de juegos conocidos en ejecución (lo alimentan los eventos WMI de nacimiento/muerte de procesos).</summary>
    event Action? RunningGamesChanged;
    /// <summary>Exe de los juegos conocidos que están corriendo ahora (snapshot de los eventos WMI).</summary>
    IReadOnlyCollection<string> RunningGameExes { get; }

    /// <summary>
    /// Exes de reglas (juegos de la biblioteca) que tienen al menos un proceso vivo,
    /// aunque el proceso real tenga otro nombre (launcher de por medio). Es lo que
    /// usa el Modo juego para saber que hay una partida en curso.
    /// </summary>
    IReadOnlyCollection<string> RunningRuleExes { get; }

    /// <summary>
    /// True si hay actividad de juego: un juego conocido en ejecución o un proceso
    /// que matchea una regla (incluida la cadena launcher→juego). Reemplaza el
    /// chequeo por RunningGameExes.Count para reconocer la partida aunque el juego
    /// real no se llame como el exe de la biblioteca. El Modo juego lo usa para
    /// saber cuándo ya no queda nada corriendo y restaurar (no para activarse:
    /// solo se activa al lanzar el juego desde WinForge).
    /// </summary>
    bool HasRunningGameActivity { get; }

    // ===== Launchers (botón "Iniciar" de la biblioteca) =====
    /// <summary>Se dispara cuando un launcher (Battle.net, Epic, GOG, Xbox…) nace o muere.</summary>
    event Action? LauncherStateChanged;
    /// <summary>¿El launcher indicado está corriendo? Con WMI activo es una lectura de un HashSet alimentado por eventos (cero polling); sin WMI, un chequeo único por nombre.</summary>
    bool IsLauncherRunning(string procName);
    /// <summary>True si la suscripción a eventos WMI está activa (sin ella no hay estado en vivo por eventos).</summary>
    bool WmiEventsActive { get; }

    /// <summary>Si el juego está corriendo, activa ya su plan de energía (para que no haya que esperar el tick).</summary>
    void ApplyPowerPlanIfRunning(string exe, string? planGuid);
    /// <summary>Si el plan aplicado era el de este juego, vuelve al plan por defecto.</summary>
    void RevertPowerPlanIfApplied(string exe);

    /// <summary>
    /// Registra exe → carpeta de instalación para matchear por ruta: muchos juegos
    /// corren con un proceso distinto al exe detectado (ej. Smite.exe lanza
    /// SmiteGame-Win64-Shipping.exe). La UI la llama al refrescar la biblioteca.
    /// </summary>
    void SetKnownInstallPaths(Dictionary<string, string> exeToInstallPath);

    // ===== Favoritos =====
    List<string> GetFavorites();
    void ToggleFavorite(string exe);
    bool IsFavorite(string exe);

    // ===== Lista manual (ejecutables agregados a mano aunque no estén corriendo) =====
    List<string> GetManualExes();
    /// <summary>Entradas manuales: exe, nombre visible (null si solo se conoce el exe) y carpeta de instalación (opcional).</summary>
    List<(string Exe, string? Name, string? InstallPath)> GetManualEntries();
    void AddManualExe(string exe, string? displayName = null, string? installPath = null);
    void RemoveManualExe(string exe);
    /// <summary>Borra TODOS los juegos manuales (Re-detectar los elimina).</summary>
    void ClearManualExes();

    /// <summary>Configura el launcher de un juego manual (ej: "Blacksmith" para Dark and Darker).</summary>
    void SetManualGameLauncher(string exe, string launcher);
    /// <summary>Obtiene el launcher configurado para un juego manual (null si no tiene).</summary>
    string? GetManualGameLauncher(string exe);
    /// <summary>Obtiene todos los juegos manuales con su launcher configurado.</summary>
    Dictionary<string, string> GetManualGameLaunchers();

    // ===== Ocultos (juegos que no se muestran en la biblioteca) =====
    List<string> GetHiddenExes();
    void HideExe(string exe);
    /// <summary>Deja de ocultar un ejecutable: vuelve a aparecer en la biblioteca.</summary>
    void UnhideExe(string exe);
    /// <summary>Quita TODOS los ocultados (Re-detectar vuelve a mostrar todo).</summary>
    void ClearHiddenExes();

    // ===== Eliminados de la biblioteca (no se muestran en ningún lado; vuelven con Re-detectar) =====
    List<string> GetDeletedGames();
    void DeleteGame(string exe);
    /// <summary>Quita TODOS los eliminados (Re-detectar los muestra de nuevo).</summary>
    void ClearDeletedGames();

    int ProcessorCount { get; }
}
