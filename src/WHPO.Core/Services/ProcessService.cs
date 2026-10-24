using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;    /// <summary>
    /// Gestión de procesos: enumera las aplicaciones/juegos con
    /// ventana visible, calcula CPU/RAM y aplica reglas persistidas (prioridad de CPU,
    /// afinidad de núcleos y prioridad de GPU) por ejecutable. Solo se aplica lo que el
    /// usuario configuró explícitamente: los valores por defecto no tocan nada.
    /// La detección de juegos en ejecución combina eventos WMI (biblioteca) con un
    /// watcher de ventana fullscreen en primer plano (juegos fuera de la biblioteca).
    /// </summary>
public sealed class ProcessService : IProcessService
{
    private readonly ILoggingService _logging;
    private readonly ISettingsService _settings;
    private readonly ICpuPowerService _powerPlan;
    private readonly IInstalledGamesService _games;

    // Carga autónoma del mapa exe→ruta (para no depender de que la página se visite).
    private bool _pathsLoaded;
    private readonly object _pathsLoadLock = new();

    // Monitoreo a nivel de app: aplica las reglas (CPU/afinidad/GPU/plan de energía)
    // cuando el juego corre y revierte el plan al cerrar. No depende de la página.
    private readonly object _planLock = new();
    private string? _appliedPlanExe;   // juego cuyo plan está activo ahora
    private string? _defaultPlanGuid;  // plan activo antes de aplicar reglas

    // exe → carpeta de instalación: matchea por ruta procesos cuyo nombre difiere
    // del exe detectado (ej. Smite.exe lanza SmiteGame-Win64-Shipping.exe).
    private readonly object _pathsLock = new();
    private Dictionary<string, string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);

    // Alias stub → exe real del juego: si la detección (vieja) eligió un stub de
    // anti-cheat/consola como exe principal (start_protected_game.exe de EAC,
    // vconsole2.exe de CS2), favoritos y reglas guardados con ese nombre se resuelven
    // al exe real para que sigan funcionando sin tocar el settings.json del usuario.
    private readonly Dictionary<string, string> _stubAliases = new(StringComparer.OrdinalIgnoreCase);

    // Alias proceso → exe de la biblioteca: los emuladores Android no ejecutan el
    // juego en su exe principal sino en una VM/orquestador con otro nombre
    // (NoxVMHandle.exe en Nox, aow_exe.exe en GameLoop, crosvm.exe en Play Games).
    // Sin esto, una regla guardada para el emulador solo dispararía con el exe
    // exacto, así que el modo juego no se activaba al abrir el juego.
    private readonly Dictionary<string, string> _processAliases = new(StringComparer.OrdinalIgnoreCase);

    // Reglas de sesión ("Actual"): se recuerdan entre reinicios (caché en settings)
    // y aplican SOLO a lanzamientos desde WinForge (sesión de lanzamiento). Un juego
    // abierto directo desde su launcher no las recibe (ese es el trabajo de
    // "Siempre"). Campos null = sin override de sesión para esa dimensión; la
    // sesión gana sobre la regla guardada campo por campo. Solo las borra
    // "Eliminar reglas".
    private readonly object _sessionLock = new();
    private readonly Dictionary<string, ProcessRule> _sessionRules = new(StringComparer.OrdinalIgnoreCase);

    // Snapshot de juegos conocidos en ejecución: lo alimentan los eventos WMI
    // (Win32_ProcessStartTrace/StopTrace) para que la biblioteca muestre el estado
    // en vivo sin polling. Se publica con un evento solo cuando cambia.
    private readonly object _runningLock = new();
    private HashSet<string> _runningGames = new(StringComparer.OrdinalIgnoreCase);

    // ===== Sesiones de lanzamiento (launcher → juego) =====
    // Una sesión se abre al lanzar un juego con regla y sigue la cadena hasta 3
    // minutos. No es configuración por juego: el launcher es un detalle de runtime
    // y el mismo juego puede arrancar con o sin él. La sesión solo aporta el
    // contexto (launcher + carpeta de instalación) para identificar el proceso real
    // cuando nace; después la regla queda enganchada al proceso como siempre.
    private readonly object _launchSessionLock = new();
    private readonly Dictionary<string, LaunchSession> _launchSessions = new(StringComparer.OrdinalIgnoreCase);
    private const int LaunchSessionTimeoutSeconds = 180;
    // Ventana de estabilización de la cadena: un proceso que solo coincide por
    // carpeta no recibe la regla hasta que pasa este tiempo sin que aparezca un
    // hijo real. Así el launcher intermedio (Steam → launcher del juego → juego)
    // no se lleva la regla del juego. Los nombres de launcher/bootstrapper esperan
    // un poco más: son los que más probablemente tengan un hijo detrás.
    private const int LaunchChainSettleSeconds = 10;
    private readonly Timer _launchSessionTimer;
    private int _launchSessionTickBusy;

    // Ruta real del proceso del juego aprendida en un lanzamiento anterior:
    // ruleKey (exe de la biblioteca) → ruta del proceso que terminó recibiendo la
    // regla. Es una caché automática, no configuración por juego: el usuario no
    // tiene que elegir nada y se puede reconstruir sola si el juego se mueve.
    private readonly object _resolvedPathsLock = new();
    private Dictionary<string, string> _resolvedPaths = new(StringComparer.OrdinalIgnoreCase);
    private const string ResolvedPathsKey = "process.resolvedPaths";

    // Último estado de regla por exe de la biblioteca (para la UI).
    private readonly object _ruleStatusLock = new();
    private readonly Dictionary<string, GameRuleStatus> _ruleStatuses = new(StringComparer.OrdinalIgnoreCase);

    public event Action? RunningGamesChanged;
    public event Action<GameRuleStatus>? GameRuleStatusChanged;

    public IReadOnlyCollection<string> RunningGameExes
    {
        get { lock (_runningLock) return new List<string>(_runningGames); }
    }

    public IReadOnlyCollection<string> RunningRuleExes
    {
        get
        {
            lock (_eventLock)
            {
                return _procGames.Values
                    .Where(v => v.RuleKey != null)
                    .Select(v => v.RuleKey!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public bool HasRunningGameActivity
    {
        get
        {
            if (RunningGameExes.Count > 0) return true;
            lock (_eventLock)
                return _procGames.Values.Any(v => v.RuleKey != null);
        }
    }

    // Monitoreo por eventos WMI (cero polling): Win32_ProcessStartTrace/StopTrace
    // avisan cuando nace o muere cualquier proceso. La suscripción requiere admin
    // (la app corre elevada); si no está disponible, los juegos simplemente se
    // muestran como "no en ejecución" (no hay polling de respaldo).
    private readonly ProcessEventWatcher _eventWatcher = new();

    // pid → proceso rastreado (juego de la biblioteca, proceso que matchea una
    // regla o proceso agregado por el watcher fullscreen). Solo se usa en modo
    // eventos; permite saber cuándo se cerró un juego (para limpiar la regla de
    // sesión y revertir el plan) sin re-enumerar.
    private readonly object _eventLock = new();
    private readonly Dictionary<int, RunningProc> _procGames = new();

    // Watcher complementario por fullscreen (cada 2 s): agrega juegos fuera de la
    // biblioteca al set compartido. Ver región "Detección complementaria por fullscreen".
    private readonly Timer _fullscreenTimer;

    // Caché de las reglas persistidas para los eventos de proceso: los handlers de
    // WMI corren por cada nacimiento/muerte de CUALQUIER proceso del sistema, así
    // que no se puede parsear el JSON de settings en cada uno. Se invalida en cada
    // SaveRule/RemoveRule.
    private readonly object _rulesCacheLock = new();
    private Dictionary<string, ProcessRule> _rulesCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _rulesCacheValid;

    // Launchers (Battle.net, Epic, GOG, Xbox…): el botón "Iniciar" de la biblioteca
    // los consulta. Se rastrean por eventos WMI (los mismos que ya cubren TODO
    // proceso), sin polling: cada nacimiento/muerte actualiza un HashSet barato.
    private readonly object _launcherLock = new();
    private readonly HashSet<string> _runningLaunchers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LauncherNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Battle.net", "EpicGamesLauncher", "GalaxyClient", "Steam", "Xbox", "XboxStub", "GamingApp",
        // Blacksmith (Dark and Darker): el binario real del launcher de Ironmace es
        // BlacksmithIM.exe; "Blacksmith" se mantiene por compatibilidad.
        "Blacksmith", "BlacksmithIM"
    };
    public event Action? LauncherStateChanged;

    /// <summary>
    /// Procesos que forman parte de la cadena de lanzamiento pero NUNCA son "el
    /// juego": clientes de launcher, helpers, updaters, instaladores y crash
    /// reporters. La sesión de lanzamiento los saltea para no aplicarles la regla
    /// del juego (el launcher es compartido por varios juegos y puede estar abierto
    /// por otro motivo). Los stubs de anti-cheat NO están acá a propósito: algunos
    /// juegos corren detrás de start_protected_game.exe y ese eslabón sí es parte
    /// del juego, no del launcher.
    /// </summary>
    private static readonly HashSet<string> LaunchChainLauncherProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // Steam / Epic / Battle.net / GOG / Ubisoft / EA / Riot / Xbox
        "steam", "steamwebhelper", "steamerrorreporter", "gameoverlayui", "steamservice",
        "epicgameslauncher", "epicwebhelper",
        "battle.net", "battle.net helper", "blizzard error",
        "galaxyclient", "galaxyclientservice", "goggalaxy",
        "ubisoftconnect", "ubisoftgamelauncher", "uplay", "upc",
        "eadesktop", "ealauncher", "origin", "originwebhelperservice",
        "riotclientservices", "riotclientux", "riotclientuxrender", "riotclientcrashhandler",
        // Otros clientes que lanzan juegos por URI/comando
        "rockstargameslauncher", "rockstarservice", "socialclubhelper",
        "hoyoplay", "hoyoplaylauncher", "wargaminggamecenter", "wgc",
        "nexonlauncher", "nexonclient", "gaijinlauncher", "jagexlauncher",
        "amazongames", "amazongameslauncher", "xbox", "xboxapp", "gamingapp",
        "gamingservices", "itch", "blacksmith", "blacksmithim",
        "blacksmithbootstrap", "blacksmithworker",
        // Herramientas de la cadena que no representan al juego
        "updater", "patcher", "installer", "uninstaller", "setup",
        "crashreportclient", "crashreporter", "crashpad_handler",
        "unitycrashhandler", "unitycrashhandler32", "unitycrashhandler64"
    };

    /// <summary>Nombre de proceso que forma parte del launcher/actualizador, no del juego.</summary>
    private static bool IsLaunchChainNoise(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName ?? "").Trim();
        if (name.Length == 0) return true;
        return LauncherNames.Contains(name) || LaunchChainLauncherProcesses.Contains(name);
    }

    /// <summary>
    /// Proceso con pinta de eslabón intermedio (launcher/bootstrapper/updater del
    /// propio juego). No se descarta para siempre: la sesión le da una ventana más
    /// larga para que aparezca el hijo real; si no aparece, se lo usa como fallback.
    /// </summary>
    private static bool IsLikelyIntermediateLauncherName(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName ?? "").ToLowerInvariant();
        if (name.Length == 0) return false;
        return name.Contains("launcher") || name.Contains("bootstrap")
            || name.Contains("updater") || name.Contains("patcher");
    }

    /// <summary>Helpers que acompañan al juego pero no son el juego final.</summary>
    private static bool IsChainHelperName(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName ?? "").ToLowerInvariant();
        if (name.Length == 0) return true;
        return name.Contains("helper") || name.Contains("service") || name.Contains("crash")
            || name.Contains("report") || name.Contains("overlay") || name.Contains("webview")
            || name.Contains("renderer") || name.Contains("utility") || name.Contains("console")
            || name.Contains("broker") || name.Contains("surrogate");
    }
    public bool WmiEventsActive { get; private set; }

    public Dictionary<string, ProcessRule> GetRulesCached()
    {
        lock (_rulesCacheLock)
        {
            if (!_rulesCacheValid)
            {
                _rulesCache = GetRules();
                _rulesCacheValid = true;
            }
            return _rulesCache;
        }
    }

    private void InvalidateRulesCache()
    {
        lock (_rulesCacheLock) _rulesCacheValid = false;
    }

    private sealed class RunningProc
    {
        public required string ProcessName; // con .exe
        public string? Path;                // ruta del exe (para matchear por carpeta)
        public string? ExeName;             // exe de la biblioteca (badge "En ejecución")
        public string? RuleKey;             // regla (persistente o de sesión) que matchea
    }

    /// <summary>
    /// Contexto de un lanzamiento: qué regla se está esperando, con qué launcher y
    /// en qué carpeta. Se abre al lanzar desde WinForge (o desde la bandeja) y la
    /// usa el matcher para reconocer el proceso real aunque su nombre no coincida
    /// con el exe de la biblioteca.
    /// </summary>
    private sealed class LaunchSession
    {
        public required string RuleKey { get; init; }
        public string? Launcher { get; set; }
        public string? InstallDir { get; set; }
        public DateTime DeadlineUtc { get; set; }
        public HashSet<int> AppliedPids { get; } = new();
        /// <summary>Inicio del proceso al que se le aplicó la regla. Windows puede
        /// reutilizar el PID en un relanzamiento; comparar el inicio evita saltarse
        /// el proceso nuevo creyendo que ya recibió la regla.</summary>
        public Dictionary<int, DateTime> AppliedProcessStartUtc { get; } = new();
        public bool Applied { get; set; }
        /// <summary>Exe principal resuelto de la carpeta (o ruta aprendida); permite
        /// distinguir el juego real de un launcher intermedio.</summary>
        public string? ExpectedExePath { get; set; }
        public bool ExpectedResolved { get; set; }
        /// <summary>La ruta esperada salió de un lanzamiento anterior ya verificado:
        /// se puede aplicar casi al instante; la resuelta por escaneo espera la
        /// ventana de estabilización por si el exe más grande es un launcher.</summary>
        public bool ExpectedFromMemory { get; set; }
        /// <summary>Primera vez que el barrido vio cada candidato, para darle tiempo
        /// a que la cadena launcher→juego se estabilice antes de aplicar.</summary>
        public Dictionary<int, DateTime> FirstSeenUtc { get; } = new();
        /// <summary>Serializa la aplicación desde WMI, el timer y el barrido inicial.</summary>
        public object Sync { get; } = new();
        public int ScanBusy;

        /// <summary>Prioridades originales de los procesos aplicados en débil: si
        /// después aparece el juego real (fuerte), se les devuelven.</summary>
        public Dictionary<int, OriginalPriorities> WeakApplied { get; } = new();
    }

    /// <summary>Prioridades originales de un proceso aplicado en débil (para
    /// devolvérselas si aparece el juego real). Null/cero = no se pudo leer.</summary>
    private sealed record OriginalPriorities(int? Cpu, long Affinity, int? Gpu, int? Io);

    private enum LaunchMatchQuality
    {
        None = 0,
        Path = 1,
        Expected = 2,
        Name = 3
    }

    /// <summary>Candidato vivo dentro de una sesión de lanzamiento.</summary>
    private sealed class LaunchCandidate
    {
        public required int Pid { get; init; }
        public required string Name { get; init; }
        public string? Path { get; init; }
        public LaunchMatchQuality Quality { get; init; }
        public bool IsStub { get; init; }
        public bool IsLauncherLike { get; init; }
        public bool IsHelperLike { get; init; }
        public DateTime StartUtc { get; init; }
        /// <summary>True si debajo hay otro candidato real (no stub/helper): este
        /// proceso es un eslabón intermedio, no el juego final.</summary>
        public bool HasMatchingChild { get; set; }
    }

    public int ProcessorCount { get; } = Math.Max(1, Environment.ProcessorCount);

    public ProcessService(ILoggingService logging, ISettingsService settings, ICpuPowerService powerPlan, IInstalledGamesService games)
    {
        _logging = logging;
        _settings = settings;
        _powerPlan = powerPlan;
        _games = games;

        // Reglas de sesión ("Actual") persistidas en un caché (settings): sobreviven
        // al reinicio de la app y solo las borra "Eliminar reglas". Aplican
        // únicamente a lanzamientos desde WinForge (ver sesión de lanzamiento).
        try
        {
            var persistedSession = _settings.Get("process.sessionRules", new Dictionary<string, ProcessRule>());
            if (persistedSession != null)
                foreach (var kv in persistedSession)
                    if (!string.IsNullOrEmpty(kv.Key)) _sessionRules[kv.Key] = kv.Value;
        }
        catch { }

        // Rutas reales aprendidas de lanzamientos anteriores. Si el settings tiene
        // una entrada vieja que ya no existe, se ignora al usarla (GetRememberedPath).
        try
        {
            var savedPaths = _settings.Get(ResolvedPathsKey, new Dictionary<string, string>());
            if (savedPaths != null)
                foreach (var kv in savedPaths)
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                        _resolvedPaths[kv.Key] = kv.Value;
        }
        catch { }

        // Fuente del estado en vivo: eventos WMI (cero polling). Aplican reglas a
        // juegos que arrancan con la app abierta y detectan el cierre para limpiar
        // la sesión y revertir el plan. Si la suscripción falla (sin admin o CIM
        // caído) NO hay respaldo por polling: los juegos se muestran como "no en
        // ejecución" y las reglas se aplican igual al lanzar desde la biblioteca.
        if (_eventWatcher.TryStart(out var wmiError))
        {
            WmiEventsActive = true;
            _eventWatcher.ProcessStarted += OnProcessStarted;
            _eventWatcher.ProcessStopped += OnProcessStopped;
            _logging.LogInfo("ProcessService: monitoreo por eventos WMI activo (sin polling)");
            // Los eventos solo cubren lo que pasa después de suscribirse: sembrar el
            // estado inicial (procesos ya corriendo) con una única enumeración.
            _ = Task.Run(SeedEventState);
        }
        else
        {
            // El error real (típicamente WBEM_E_ACCESS_DENIED si la app no corre
            // elevada) se registra para diagnosticar de una.
            _logging.LogWarning($"ProcessService: WMI no disponible: {wmiError}");
        }

        // Detección complementaria por fullscreen (cada 2 s): cubre juegos fuera de
        // la biblioteca (emuladores, itch.io, DRM-free...). Solo agrega al set
        // compartido; el cierre lo limpia el evento WMI (o el propio watcher sin WMI).
        _fullscreenTimer = new Timer(FullscreenTick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        // Sesiones de lanzamiento: tick liviano que solo hace trabajo si hay una
        // sesión abierta (partida lanzada desde WinForge con regla configurada).
        _launchSessionTimer = new Timer(LaunchSessionTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _logging.LogDebug("ProcessService: watcher de fullscreen activo (cada 2 s)");

        _ = Task.Run(ReconcileBirthPriorities);
    }

    /// <summary>
    /// Carga una vez (fondo) el mapa exe→carpeta de instalación escaneando los
    /// launchers, así el matching por ruta funciona aunque el usuario nunca haya
    /// abierto la página. La página puede reemplazarlo con un mapa más completo.
    /// </summary>
    private void EnsureKnownPathsLoaded()
    {
        lock (_pathsLoadLock)
        {
            if (_pathsLoaded) return;
            _pathsLoaded = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var games = await _games.GetInstalledGamesAsync();
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in games)
                    if (!string.IsNullOrEmpty(g.ExeFileName) && !string.IsNullOrEmpty(g.InstallPath))
                        map[g.ExeFileName] = g.InstallPath;
                UpdateKnownPaths(map, merge: true);

                // Red de seguridad del enganche por ruta: si alguna entrada quedó
                // apuntando a una carpeta que ya no existe, se re-resuelve en segundo
                // plano y se publican las rutas corregidas, así el modo juego sigue
                // enganchando aunque el usuario nunca abra la biblioteca.
                await RepairStaleInstallPathsAsync(games);
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"ProcessService: rutas de juegos: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Repara las rutas de instalación vencidas de la biblioteca (juegos de la Store,
    /// cuya carpeta lleva la versión en el nombre, y cualquier juego o emulador movido)
    /// y publica las rutas nuevas en el mapa del enganche.
    ///
    /// Corre una sola vez por sesión (lo llama la carga inicial del mapa) y solo si
    /// hay alguna ruta rota: en el caso normal no cuesta nada.
    /// </summary>
    private async Task RepairStaleInstallPathsAsync(List<InstalledGame> games)
    {
        try
        {
            if (games.Count == 0) return;
            bool anyBroken = false;
            foreach (var g in games)
            {
                if (InstalledGamesService.HasMissingInstallPath(g)) { anyBroken = true; break; }
            }
            if (!anyBroken) return;

            var repairs = await _games.RepairMissingInstallPathsAsync(games);
            if (repairs.Count == 0) return;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, fresh) in repairs)
                if (!string.IsNullOrEmpty(fresh.ExeFileName) && !string.IsNullOrEmpty(fresh.InstallPath))
                    map[fresh.ExeFileName] = fresh.InstallPath;
            UpdateKnownPaths(map, merge: true);
            _logging.LogInfo($"ProcessService: {repairs.Count} ruta(s) de juego reparada(s) para el enganche");
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: reparar rutas vencidas: {ex.Message}");
        }
    }

    /// <summary>
    /// Actualiza el mapa exe→carpeta de instalación y reconstruye los alias de stubs
    /// (stub anti-cheat/consola → exe real del juego), para que favoritos y reglas
    /// guardados con el nombre viejo del stub sigan resolviendo al juego correcto.
    /// </summary>
    public void SetKnownInstallPaths(Dictionary<string, string> exeToInstallPath)
    {
        UpdateKnownPaths(exeToInstallPath, merge: false);
    }

    private void UpdateKnownPaths(Dictionary<string, string> map, bool merge)
    {
        lock (_pathsLock)
        {
            if (merge)
            {
                foreach (var kv in map) _knownPaths[kv.Key] = kv.Value;
            }
            else
            {
                _knownPaths = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            }

            // Alias de proceso del catálogo de emuladores (VM/orquestadores): se
            // resuelven al exe de la biblioteca para que las reglas lo matcheen.
            _processAliases.Clear();
            foreach (var exe in _knownPaths.Keys)
            {
                foreach (var alias in EmulatorCatalog.HookProcessNames(exe))
                    if (!string.IsNullOrEmpty(alias))
                        _processAliases[alias] = exe;
            }

            _stubAliases.Clear();
            foreach (var (exe, installPath) in _knownPaths)
            {
                if (string.IsNullOrEmpty(installPath)) continue;
                try
                {
                    var best = GameExeResolver.FindBestGameExePath(installPath);
                    string real = string.IsNullOrEmpty(Path.GetFileName(best ?? "")) ? exe : Path.GetFileName(best);
                    foreach (var stub in GameExeResolver.FindStubExePaths(installPath))
                    {
                        var stubName = Path.GetFileName(stub);
                        if (!string.IsNullOrEmpty(stubName) && !_stubAliases.ContainsKey(stubName))
                            _stubAliases[stubName] = real;
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>Resuelve un exe guardado a su forma real (alias de stubs anti-cheat/consola).</summary>
    private string ResolveExeAlias(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return exe;
        lock (_pathsLock)
            return _stubAliases.TryGetValue(exe, out var real) ? real : exe;
    }

    /// <summary>
    /// Un proceso matchea la regla si su nombre coincide, si es un alias declarado
    /// del exe de la regla (VM de un emulador: NoxVMHandle.exe para Nox.exe,
    /// aow_exe.exe para GameLoop...) o si su ruta está dentro de la carpeta de
    /// instalación del juego.
    /// </summary>
    private bool MatchesRuleKey(string processName, string? processPath, string ruleExe)
    {
        if (MatchesRuleName(processName, ruleExe)) return true;
        return MatchesRulePath(processPath, ruleExe);
    }

    /// <summary>Nombre exacto o alias declarado (VM de emulador, stub resuelto…).</summary>
    private bool MatchesRuleName(string processName, string ruleExe)
    {
        if (string.Equals(processName, ruleExe, StringComparison.OrdinalIgnoreCase)) return true;
        lock (_pathsLock)
        {
            // Alias primero: es más barato que el StartsWith de rutas y cubre el caso
            // en el que la VM del emulador corre desde otra carpeta.
            return _processAliases.TryGetValue(processName, out var primary)
                && string.Equals(primary, ruleExe, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>El proceso vive dentro de la carpeta de instalación del juego.</summary>
    private bool MatchesRulePath(string? processPath, string ruleExe)
    {
        if (string.IsNullOrEmpty(processPath)) return false;
        lock (_pathsLock)
        {
            return _knownPaths.TryGetValue(ruleExe, out var dir)
                && IsUnderDirectory(processPath, dir);
        }
    }

    private static bool IsUnderDirectory(string? path, string? directory)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory)) return false;
        var dir = directory.TrimEnd('\\');
        return path.Length > dir.Length
            && path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // ===== P/Invoke =====

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    // ===== Detección por fullscreen =====
    // El estado de notificaciones del sistema ya sabe si hay una app fullscreen
    // (multi-monitor incluido): QUNS_BUSY o QUNS_RUNNING_D3D_FULL_SCREEN.
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);

    private enum QueryUserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // Prioridad de GPU por proceso (D3DKMT_SCHEDULINGPRIORITYCLASS).
    // Devuelven NTSTATUS: 0 = STATUS_SUCCESS.
    // IMPORTANTE: el entry point exportado es "D3DKMT...PriorityClass" (sin sufijo);
    // se usa EntryPoint explícito porque el nombre del método C# es distinto.
    [DllImport("gdi32.dll", EntryPoint = "D3DKMTSetProcessSchedulingPriorityClass", SetLastError = true)]
    private static extern int D3DKMTSetProcessSchedulingPriorityClassGdi32(IntPtr hProcess, uint Priority);

    [DllImport("gdi32full.dll", EntryPoint = "D3DKMTSetProcessSchedulingPriorityClass", SetLastError = true)]
    private static extern int D3DKMTSetProcessSchedulingPriorityClassGdi32Full(IntPtr hProcess, uint Priority);    [DllImport("gdi32.dll", EntryPoint = "D3DKMTGetProcessSchedulingPriorityClass", SetLastError = true)]
    private static extern int D3DKMTGetProcessSchedulingPriorityClassGdi32(IntPtr hProcess, out uint Priority);

    [DllImport("gdi32full.dll", EntryPoint = "D3DKMTGetProcessSchedulingPriorityClass", SetLastError = true)]
    private static extern int D3DKMTGetProcessSchedulingPriorityClassGdi32Full(IntPtr hProcess, out uint Priority);

    // Nombre de imagen de un proceso con solo PROCESS_QUERY_LIMITED_INFORMATION:
    // MainModule exige más derechos y falla en procesos protegidos (Easy Anti-Cheat),
    // mientras que esto es lo mismo que usa el Administrador de tareas.
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    // Afinidad de CPU por proceso. Se usa P/Invoke directo (y no el setter
    // managed Process.ProcessorAffinity) para controlar el manejo de errores,
    // normalizar la máscara y verificar que la afinidad realmente se aplicó.
    private const uint ProcessSetInformation = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr dwProcessAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(IntPtr hProcess, out IntPtr lpProcessAffinityMask, out IntPtr lpSystemAffinityMask);

    // Prioridad de CPU por clase (SetPriorityClass): abrir con solo
    // PROCESS_SET_INFORMATION permite aplicarla a más procesos que el acceso total.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    // Snapshot Toolhelp para leer el PID padre sin abrir el proceso objetivo
    // (funciona incluso si el proceso está protegido por anti-cheat).
    private const uint Th32csSnapprocess = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private static long? _systemAffinityMask;

    /// <summary>
    /// Máscara de los procesadores activos del sistema (grupo del proceso actual),
    /// calculada una sola vez. SetProcessAffinityMask falla con
    /// ERROR_INVALID_PARAMETER si la máscara pedida tiene bits fuera de esta, así
    /// que toda máscara se normaliza contra ella antes de aplicarse.
    /// </summary>
    private static long SystemAffinityMask
    {
        get
        {
            if (_systemAffinityMask is long cached) return cached;
            long mask = 0;
            try
            {
                if (GetProcessAffinityMask(Process.GetCurrentProcess().Handle, out _, out var sys))
                    mask = sys.ToInt64();
            }
            catch { }
            if (mask == 0)
                mask = Environment.ProcessorCount >= 64 ? -1L : ((1L << Environment.ProcessorCount) - 1);
            _systemAffinityMask = mask;
            return mask;
        }
    }

    /// <summary>
    /// Ruta del ejecutable de un proceso. Intenta MainModule primero (rápido) y,
    /// si falla (procesos protegidos por anti-cheat), usa QueryFullProcessImageName
    /// con PROCESS_QUERY_LIMITED_INFORMATION.
    /// </summary>
    private static void TryGetProcessPath(Process p, out string? path)
    {
        path = null;
        try { path = p.MainModule?.FileName; } catch { }
        if (!string.IsNullOrEmpty(path)) return;
        try
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, p.Id);
            if (h == IntPtr.Zero) return;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                uint size = 1024;
                if (QueryFullProcessImageName(h, 0, sb, ref size))
                    path = sb.ToString();
            }
            finally { CloseHandle(h); }
        }
        catch { }
    }




    // ===== Detección =====

    public ProcessAppInfo? FindProcess(string exeFileName)
    {
        var target = (exeFileName ?? "").Trim();
        if (string.IsNullOrEmpty(target)) return null;
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            target += ".exe";

        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.HasExited) continue;
                    var exe = SafeExeName(p);
                    if (string.Equals(exe, target, StringComparison.OrdinalIgnoreCase))
                    {
                        double wsMB = p.WorkingSet64 / (1024.0 * 1024.0);
                        var handle = p.MainWindowHandle;
                        return new ProcessAppInfo(p.Id, p.ProcessName, exe,
                            (handle != IntPtr.Zero && IsWindowVisible(handle)) ? SafeTitle(p) : null,
                            0, wsMB);
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string SafeExeName(Process p)
    {
        TryGetProcessPath(p, out var path);
        if (!string.IsNullOrEmpty(path))
            return Path.GetFileName(path);
        return p.ProcessName + ".exe";
    }

    private static string? SafeTitle(Process p)
    {
        try
        {
            var t = p.MainWindowTitle;
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        catch { return null; }
    }

    // ===== Aplicar reglas =====

    public bool ApplyCpuPriority(int pid, int priority)
    {
        try
        {
            // Elevar (Alta/Tiempo real) exige SeIncreaseBasePriorityPrivilege: se
            // habilita explícitamente (igual que en la E/S; el setter managed de
            // .NET no siempre lo hace). Se abre con SOLO PROCESS_SET_INFORMATION
            // (como GPU/E/S): muchos procesos aceptan ese derecho aunque denieguen
            // el acceso total que pide Process.GetProcessById, así la prioridad se
            // puede aplicar EN VIVO a más procesos (servicios, apps del sistema).
            if (priority >= 4) EnableBasePriorityPrivilege();
            IntPtr h = OpenProcess(ProcessSetInformation, false, pid);
            if (h == IntPtr.Zero)
            {
                _logging.LogWarning($"ProcessService: no se pudo abrir {pid} para prioridad CPU (error 0x{Marshal.GetLastWin32Error():X8})");
                return false;
            }
            try
            {
                uint value = priority switch
                {
                    0 => 0x40,    // IDLE_PRIORITY_CLASS
                    1 => 0x4000,  // BELOW_NORMAL_PRIORITY_CLASS
                    3 => 0x8000,  // ABOVE_NORMAL_PRIORITY_CLASS
                    4 => 0x80,    // HIGH_PRIORITY_CLASS
                    5 => 0x100,   // REALTIME_PRIORITY_CLASS
                    _ => 0x20     // NORMAL_PRIORITY_CLASS
                };
                bool ok = SetPriorityClass(h, value);
                if (!ok)
                    _logging.LogWarning($"ProcessService: SetPriorityClass({pid}, {priority}) falló: 0x{Marshal.GetLastWin32Error():X8}");
                return ok;
            }
            finally
            {
                CloseHandle(h);
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: no se pudo aplicar prioridad CPU a {pid}: {ex.Message}");
            return false;
        }
    }

    public bool ApplyAffinity(int pid, long mask)
    {
        if (mask <= 0) return true;
        // Normalizar: bits fuera de los procesadores activos del sistema hacen
        // fallar SetProcessAffinityMask con ERROR_INVALID_PARAMETER.
        mask &= SystemAffinityMask;
        if (mask == 0)
        {
            _logging.LogWarning($"ProcessService: máscara de afinidad sin bits válidos para {pid}");
            return false;
        }

        if (ApplyAffinityToProcess(pid, mask)) return true;

        // El proceso está protegido (anti-cheat: Easy Anti-Cheat deniega
        // SetProcessAffinityMask en vivo). Los hijos heredan la máscara de
        // afinidad del padre al nacer, así que se aplica a la cadena de
        // lanzamiento (launcher / stub anti-cheat del mismo directorio): el
        // juego real termina corriendo con la afinidad pedida aunque su proceso
        // esté bloqueado.
        if (ApplyAffinityToAncestors(pid, mask))
        {
            _logging.LogWarning($"ProcessService: afinidad 0x{mask:X} de {pid} aplicada a su cadena de lanzamiento (proceso protegido; el juego la hereda al nacer)");
            return true;
        }
        return false;
    }

    /// <summary>Aplica la afinidad directamente a un proceso (con verificación y reintento).</summary>
    private bool ApplyAffinityToProcess(int pid, long mask)
    {
        try
        {
            IntPtr h = OpenProcess(ProcessSetInformation, false, pid);
            if (h == IntPtr.Zero)
            {
                _logging.LogWarning($"ProcessService: no se pudo abrir {pid} para afinidad (error 0x{Marshal.GetLastWin32Error():X8})");
                return false;
            }
            try
            {
                bool ok = SetProcessAffinityMask(h, new IntPtr(mask));
                if (!ok)
                {
                    _logging.LogWarning($"ProcessService: SetProcessAffinityMask({pid}, 0x{mask:X}) falló: 0x{Marshal.GetLastWin32Error():X8}");
                    return false;
                }
                // Verificar: algunos procesos protegidos (anti-cheat) revierten la
                // afinidad apenas se aplica; si no quedó, reintentar una vez y
                // reportar el fracaso en vez de "aplicado" en silencio.
                if (!AffinityTookEffect(h, mask))
                {
                    Thread.Sleep(250);
                    ok = SetProcessAffinityMask(h, new IntPtr(mask));
                    if (!ok || !AffinityTookEffect(h, mask))
                    {
                        _logging.LogWarning($"ProcessService: la afinidad 0x{mask:X} de {pid} fue revertida (anti-cheat o política del sistema)");
                        return false;
                    }
                }
                return true;
            }
            finally { CloseHandle(h); }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: no se pudo aplicar afinidad a {pid}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Camina la cadena de procesos padre aplicando la máscara a los eslabones
    /// de lanzamiento (mismo directorio que el proceso objetivo o launchers
    /// conocidos). Nunca toca procesos del sistema ni de otras carpetas.
    /// </summary>
    private bool ApplyAffinityToAncestors(int pid, long mask)
    {
        string? targetPath = GetProcessPathByPid(pid);
        string? targetDir = string.IsNullOrEmpty(targetPath)
            ? null
            : Path.GetDirectoryName(targetPath)?.TrimEnd('\\');

        var visited = new HashSet<int> { pid };
        int current = pid;
        for (int depth = 0; depth < 8; depth++)
        {
            current = GetParentPid(current);
            if (current <= 0 || !visited.Add(current)) return false;

            string? ancPath = GetProcessPathByPid(current);
            string? ancName = string.IsNullOrEmpty(ancPath) ? null : Path.GetFileNameWithoutExtension(ancPath);
            bool sameDir = !string.IsNullOrEmpty(ancPath) && targetDir != null
                && string.Equals(Path.GetDirectoryName(ancPath)?.TrimEnd('\\'), targetDir, StringComparison.OrdinalIgnoreCase);
            bool knownLauncher = ancName != null && LauncherNames.Contains(ancName);
            // Si la ruta del juego no se puede leer (EAC también bloquea la
            // lectura), el check de mismo directorio no alcanza: califica también
            // cualquier proceso dentro de una carpeta de instalación conocida
            // (el stub anti-cheat start_protected_game.exe está en la carpeta del juego).
            bool inKnownGameFolder = IsKnownGameFolder(ancPath);
            if (!sameDir && !knownLauncher && !inKnownGameFolder) continue;

            if (ApplyAffinityToProcess(current, mask)) return true;
        }
        return false;
    }

    /// <summary>¿El archivo está dentro de la carpeta de instalación de un juego conocido?</summary>
    private bool IsKnownGameFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        lock (_pathsLock)
        {
            foreach (var dir in _knownPaths.Values)
            {
                if (!string.IsNullOrEmpty(dir) && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>PID del proceso padre (snapshot Toolhelp, sin abrir el proceso objetivo).</summary>
    private static int GetParentPid(int pid)
    {
        var map = GetParentPidMap();
        return map.TryGetValue(pid, out var parent) ? parent : -1;
    }

    /// <summary>
    /// Mapa pid → pid padre con UN solo snapshot Toolhelp. El barrido de la cadena
    /// de lanzamiento lo usa para descartar el launcher intermedio (el padre del
    /// juego) sin abrir ni consultar cada proceso por separado.
    /// </summary>
    private static Dictionary<int, int> GetParentPidMap()
    {
        var map = new Dictionary<int, int>();
        try
        {
            IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
            if (snap == IntPtr.Zero) return map;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32First(snap, ref entry)) return map;
                do
                {
                    map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                } while (Process32Next(snap, ref entry));
            }
            finally { CloseHandle(snap); }
        }
        catch { }
        return map;
    }

    /// <summary>Inicio del proceso (UTC), para distinguir un PID reutilizado.</summary>
    private static DateTime? GetProcessStartUtc(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return null;
            return p.StartTime.ToUniversalTime();
        }
        catch { return null; }
    }

    /// <summary>Ruta del exe de un proceso con solo PROCESS_QUERY_LIMITED_INFORMATION (funciona en procesos protegidos).</summary>
    private static string? GetProcessPathByPid(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = 1024;
            if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
        }
        finally { CloseHandle(h); }
        return null;
    }

    /// <summary>
    /// Máscara de afinidad actual de un proceso (para la tabla). Abre con
    /// PROCESS_QUERY_LIMITED_INFORMATION: funciona en procesos protegidos por
    /// anti-cheat, donde el getter managed Process.ProcessorAffinity falla.
    /// </summary>
    public long GetAffinity(int pid)
    {
        try
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                if (GetProcessAffinityMask(h, out var mask, out _)) return mask.ToInt64();
            }
            finally { CloseHandle(h); }
        }
        catch { }
        return 0;
    }

    /// <summary>¿La afinidad pedida quedó efectivamente aplicada al proceso?</summary>
    private static bool AffinityTookEffect(IntPtr h, long mask)
    {
        try
        {
            if (GetProcessAffinityMask(h, out var current, out _))
                return (current.ToInt64() & mask) == mask;
        }
        catch { }
        return true; // si no se puede leer, no bloquear la aplicación
    }

    public bool CanOpenForModify(int pid)
    {
        // Los procesos kernel/sistema (System, lsass, csrss, servicios…) deniegan
        // la apertura con SET_INFORMATION incluso elevado; los normales se abren.
        IntPtr h = OpenProcess(ProcessSetInformation, false, pid);
        if (h == IntPtr.Zero) return false;
        CloseHandle(h);
        return true;
    }

    public bool ApplyGpuPriority(int pid, int priority)
    {
        // D3DKMT_SCHEDULINGPRIORITYCLASS: 2=BelowNormal, 3=Normal, 4=AboveNormal.
        // Se abre con PROCESS_SET_INFORMATION igual que la lectura: D3DKMTSet solo
        // exige ese derecho (verificado empíricamente) y con acceso acotado se puede
        // aplicar a más procesos que con el handle de acceso total.
        try
        {
            IntPtr h = OpenProcess(ProcessSetInformation, false, pid);
            if (h == IntPtr.Zero)
            {
                _logging.LogWarning($"ProcessService: no se pudo abrir {pid} para aplicar prioridad GPU (0x{Marshal.GetLastWin32Error():X8})");
                return false;
            }
            try
            {
                int result = TrySetGpuPriority(h, (uint)priority);
                if (result != 0)
                    _logging.LogWarning($"ProcessService: D3DKMTSetProcessSchedulingPriorityClass devolvió 0x{result:X8} para {pid} (prioridad {priority})");
                return result == 0;
            }
            finally
            {
                CloseHandle(h);
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: no se pudo aplicar prioridad GPU a {pid}: {ex.Message}");
            return false;
        }
    }

    private static int TrySetGpuPriority(IntPtr hProcess, uint priority)
    {
        // gdi32.dll es el exportador canónico; gdi32full existe en otras versiones
        // de Windows. Si gdi32 devuelve un error real (ej. acceso denegado en un
        // proceso protegido), se reporta ESE error y no se pisa con el fallback.
        int r;
        try
        {
            r = D3DKMTSetProcessSchedulingPriorityClassGdi32(hProcess, priority);
            if (r == 0) return 0;
        }
        catch (EntryPointNotFoundException)
        {
            r = -1; // gdi32 sin la API: usar el resultado del fallback
        }
        try
        {
            int r2 = D3DKMTSetProcessSchedulingPriorityClassGdi32Full(hProcess, priority);
            return r == -1 ? r2 : r;
        }
        catch (EntryPointNotFoundException)
        {
            return r; // fallback inexistente: reportar el resultado real de gdi32
        }
    }

    public int LastGpuPriorityStatus { get; private set; }

    public int? GetGpuPriority(int pid)
    {
        LastGpuPriorityStatus = 0;
        // Se abre con PROCESS_SET_INFORMATION (y no con el handle de acceso total de
        // Process.GetProcessById): es el único derecho que D3DKMTGet exige (verificado
        // empíricamente) y con un derecho más acotado se pueden abrir procesos que
        // deniegan el acceso total (sistema, anti-cheat) sin generar "Acceso denegado".
        IntPtr h = OpenProcess(ProcessSetInformation, false, pid);
        if (h == IntPtr.Zero)
        {
            // No se pudo abrir: proceso protegido (deniega el acceso) o ya cerrado.
            // Condición normal: no spamear el log. Si fue por acceso denegado se
            // reporta como tal para que la UI muestre "protegido: anti-cheat".
            int err = Marshal.GetLastWin32Error();
            LastGpuPriorityStatus = err == 5 ? unchecked((int)0xC0000022) : -1;
            return null;
        }
        try
        {
            uint value = 0;
            int result = TryGetGpuPriority(h, out value);
            LastGpuPriorityStatus = result;
            // ACCESS_DENIED (0xC0000022) es lo esperado en procesos protegidos
            // (anti-cheat/sistema) e INVALID_PARAMETER (0xC000000D) en procesos sin
            // contexto de planificador GPU (servicios, procesos del sistema): ambos
            // son condiciones normales y no deben spamear el log cada refresco.
            if (result != 0 && result != unchecked((int)0xC0000022)
                && result != unchecked((int)0xC000000D))
                _logging.LogWarning($"ProcessService: D3DKMTGetProcessSchedulingPriorityClass devolvió 0x{result:X8} para {pid}");
            return result == 0 ? (int)value : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static int TryGetGpuPriority(IntPtr hProcess, out uint priority)
    {
        // Misma lógica que TrySetGpuPriority: reportar el error real de gdi32 y no
        // enmascararlo con el fallback inexistente de gdi32full.
        int r;
        try
        {
            r = D3DKMTGetProcessSchedulingPriorityClassGdi32(hProcess, out priority);
            if (r == 0) return 0;
        }
        catch (EntryPointNotFoundException)
        {
            r = -1;
            priority = 0;
        }
        try
        {
            int r2 = D3DKMTGetProcessSchedulingPriorityClassGdi32Full(hProcess, out priority);
            return r == -1 ? r2 : r;
        }
        catch (EntryPointNotFoundException)
        {
            return r;
        }
    }

    // ===== Prioridad de E/S (IO_PRIORITY_HINT) =====
    // No hay API Win32 pública para la prioridad de E/S: se usa
    // NtSetInformationProcess(ProcessIoPriority=33) con un IO_PRIORITY_HINT
    // (0=VeryLow, 1=Low, 2=Normal, 3=High, 4=Critical).
    // No tiene clave de nacimiento bien documentada (PerfOptions\IoPriority es
    // semidocumentada), así que solo aplica en vivo, como la prioridad de GPU.
    private const int ProcessIoPriority = 33;

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr hProcess, int ProcessInformationClass, ref uint ProcessInformation, uint ProcessInformationLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr hProcess, int ProcessInformationClass, ref uint ProcessInformation, uint ProcessInformationLength, out uint ReturnLength);

    // SeIncreaseBasePriorityPrivilege: NtSetInformationProcess exige este privilegio
    // para fijar prioridad E/S Alta (3) / Crítica (4) — el mismo que pide la prioridad
    // de CPU Alta/Tiempo real. El API managed de .NET (Process.PriorityClass) lo
    // habilita internamente; acá se hace explícito porque se llama NtSetInformationProcess
    // directo. Se habilita UNA vez (el token no cambia mientras la app corre).
    private static bool _basePriorityPrivilegeEnabled;

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges; // un solo privilegio (count = 1)
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out Luid lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    private static void EnableBasePriorityPrivilege()
    {
        if (_basePriorityPrivilegeEnabled) return;
        _basePriorityPrivilegeEnabled = true;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out var hToken))
                return;
            try
            {
                if (!LookupPrivilegeValue(null, "SeIncreaseBasePriorityPrivilege", out var luid))
                    return;
                var tp = new TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Privileges = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
                };
                AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(hToken); }
        }
        catch { }
    }

    public bool ApplyIoPriority(int pid, int priority)
    {
        try
        {
            // Alta/Crítica exigen SeIncreaseBasePriorityPrivilege; el resto no.
            // Habilita una sola vez y es barato.
            if (priority >= 3) EnableBasePriorityPrivilege();
            IntPtr h = OpenProcess(ProcessSetInformation, false, pid);
            if (h == IntPtr.Zero)
            {
                _logging.LogWarning($"ProcessService: no se pudo abrir {pid} para prioridad E/S (error 0x{Marshal.GetLastWin32Error():X8})");
                return false;
            }
            try
            {
                uint value = (uint)priority;
                int status = NtSetInformationProcess(h, ProcessIoPriority, ref value, sizeof(uint));
                if (status != 0)
                    _logging.LogWarning($"ProcessService: NtSetInformationProcess(IoPriority) devolvió 0x{status:X8} para {pid} (prioridad {priority})");
                return status == 0;
            }
            finally { CloseHandle(h); }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: no se pudo aplicar prioridad E/S a {pid}: {ex.Message}");
            return false;
        }
    }

    public int? GetIoPriority(int pid)
    {
        try
        {
            IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                uint value = 0;
                int status = NtQueryInformationProcess(h, ProcessIoPriority, ref value, sizeof(uint), out _);
                // ACCESS_DENIED (0xC0000022) es lo esperado en procesos protegidos
                // (anti-cheat/sistema): no es un error real y no debe spamear el log.
                if (status != 0 && status != unchecked((int)0xC0000022))
                    _logging.LogWarning($"ProcessService: NtQueryInformationProcess(IoPriority) devolvió 0x{status:X8} para {pid}");
                return status == 0 ? (int)value : null;
            }
            finally { CloseHandle(h); }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: leer prioridad E/S {pid}: {ex.Message}");
            return null;
        }
    }

    public void ApplyRule(ProcessAppInfo app, ProcessRule rule)
        => ApplyRuleWithFeedback(app, rule);

    /// <summary>
    /// Aplica una regla a un proceso y devuelve qué partes no se pudieron aplicar
    /// (procesos protegidos por anti-cheat, cerrados, etc.). La UI lo usa para
    /// avisar al usuario en vez de fallar en silencio.
    /// </summary>
    public RuleApplyFeedback ApplyRuleWithFeedback(ProcessAppInfo app, ProcessRule rule)
    {
        if (rule == null) return new RuleApplyFeedback(false, false, false);
        bool cpuFailed = rule.CpuPriority is int cpu && !ApplyCpuPriority(app.Id, cpu);
        bool affFailed = rule.AffinityMask is long mask && mask > 0 && !ApplyAffinity(app.Id, mask);
        bool gpuFailed = rule.GpuPriority is int gpu && !ApplyGpuPriority(app.Id, gpu);
        bool ioFailed = rule.IoPriority is int io && !ApplyIoPriority(app.Id, io);
        return new RuleApplyFeedback(cpuFailed, affFailed, gpuFailed, ioFailed);
    }

    /// <summary>
    /// Sigue la cadena de apertura del juego aplicando su regla efectiva (la de
    /// sesión "Actual" gana sobre la guardada) mientras arranca. La ventana de
    /// seguimiento es de 3 minutos y cubre el caso launcher→juego: el proceso real
    /// suele nacer con otro nombre, en una subcarpeta o detrás de un stub de
    /// anti-cheat. No aplica la regla al launcher. Es idempotente: GameLauncher ya
    /// abrió la sesión antes de lanzar y esto la vuelve a pedir después.
    /// Vive en el servicio (no en la página) para no depender de que la ventana
    /// siga abierta ni de la navegación.
    /// </summary>
    public void ApplyLaunchChainRule(string exeFileName)
        => BeginLaunchSession(exeFileName);

    /// <summary>
    /// Abre (o reutiliza) la sesión de lanzamiento del juego. Solo hace algo si hay
    /// una regla efectiva: sin regla no hay nada que aplicar y la UI no necesita
    /// estado. La carpeta de instalación permite reconocer el proceso real por
    /// ruta; el launcher solo se usa para el mensaje de la UI y para excluir sus
    /// propios procesos del matching.
    /// </summary>
    public void BeginLaunchSession(string exeFileName, string? launcher = null, string? installPath = null)
    {
        if (string.IsNullOrWhiteSpace(exeFileName)) return;
        var exe = Path.GetFileName(exeFileName);
        if (string.IsNullOrWhiteSpace(exe)) return;
        exe = ResolveExeAlias(exe);
        if (RuleIsEmpty(GetEffectiveRule(exe))) return;

        EnsureKnownPathsLoaded();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            lock (_pathsLock)
                _knownPaths.TryGetValue(exe, out installPath);
        }

        // Ruta real aprendida en un lanzamiento anterior: si el juego se movió, se
        // ignora y el barrido vuelve a resolverlo desde la carpeta.
        string? learnedPath = GetRememberedPath(exe);

        var now = DateTime.UtcNow;
        LaunchSession session;
        bool created = false;
        lock (_launchSessionLock)
        {
            if (_launchSessions.TryGetValue(exe, out var existing) && existing.DeadlineUtc > now)
            {
                if (!string.IsNullOrWhiteSpace(launcher)) existing.Launcher ??= launcher.Trim();
                if (!string.IsNullOrWhiteSpace(installPath)) existing.InstallDir ??= installPath.Trim();
                if (!string.IsNullOrWhiteSpace(learnedPath))
                {
                    // La ruta aprendida es más confiable que el exe más grande de la
                    // carpeta: reemplaza a la resuelta en el primer lanzamiento.
                    existing.ExpectedExePath = learnedPath;
                    existing.ExpectedResolved = true;
                    existing.ExpectedFromMemory = true;
                }
                // El mismo lanzamiento pide la sesión dos veces (antes de abrir el
                // proceso y al confirmarlo); extender el plazo no cambia nada ahí y
                // cubre el relanzamiento del juego dentro de la ventana de espera.
                existing.DeadlineUtc = now.AddSeconds(LaunchSessionTimeoutSeconds);
                session = existing;
            }
            else
            {
                _launchSessions.Remove(exe);
                session = new LaunchSession
                {
                    RuleKey = exe,
                    Launcher = string.IsNullOrWhiteSpace(launcher) ? null : launcher.Trim(),
                    InstallDir = string.IsNullOrWhiteSpace(installPath) ? null : installPath.Trim(),
                    ExpectedExePath = learnedPath,
                    ExpectedResolved = !string.IsNullOrWhiteSpace(learnedPath),
                    ExpectedFromMemory = !string.IsNullOrWhiteSpace(learnedPath),
                    DeadlineUtc = now.AddSeconds(LaunchSessionTimeoutSeconds)
                };
                _launchSessions[exe] = session;
                created = true;
            }
        }

        if (created)
        {
            SetGameRuleStatus(new GameRuleStatus(exe, GameRuleState.Waiting, session.Launcher));
            _logging.LogInfo($"ProcessService: sesión de lanzamiento de {exe} (launcher={session.Launcher ?? "directo"}, carpeta={session.InstallDir ?? "desconocida"}, exe esperado={session.ExpectedExePath ?? "se resuelve al escanear"}); se busca el proceso real por hasta {LaunchSessionTimeoutSeconds} s.");
        }

        // Barrido inmediato en segundo plano: cubre el caso sin WMI y el de un
        // proceso que ya estaba arrancando. No se hace en el hilo de UI (el botón
        // Iniciar no debe esperar a enumerar procesos). El tick de 1 s sigue después.
        _ = Task.Run(() => ScanProcessesForLaunchSession(session));
    }

    /// <summary>Ruta real aprendida para una regla, si todavía existe en disco.</summary>
    private string? GetRememberedPath(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        string? path;
        lock (_resolvedPathsLock) _resolvedPaths.TryGetValue(exe, out path);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return File.Exists(path) ? path : null; }
        catch { return null; }
    }

    /// <summary>
    /// Guarda la ruta del proceso que realmente recibió la regla. Es la parte que
    /// hace que el próximo lanzamiento no tenga que adivinar entre el launcher
    /// intermedio y el juego. No se guardan launchers, stubs ni helpers.
    /// </summary>
    private void RememberResolvedPath(string ruleKey, string? processPath)
    {
        if (string.IsNullOrWhiteSpace(ruleKey) || string.IsNullOrWhiteSpace(processPath)) return;
        try
        {
            if (!File.Exists(processPath)) return;
            var name = Path.GetFileName(processPath);
            if (string.IsNullOrEmpty(name) || IsLaunchChainNoise(name)
                || IsLikelyIntermediateLauncherName(name) || IsChainHelperName(name)
                || GameExeResolver.IsStubExeName(name))
                return;

            Dictionary<string, string> snapshot;
            lock (_resolvedPathsLock)
            {
                if (_resolvedPaths.TryGetValue(ruleKey, out var existing)
                    && string.Equals(existing, processPath, StringComparison.OrdinalIgnoreCase))
                    return;
                _resolvedPaths[ruleKey] = processPath;
                snapshot = new Dictionary<string, string>(_resolvedPaths, StringComparer.OrdinalIgnoreCase);
            }
            try
            {
                _settings.Set(ResolvedPathsKey, snapshot);
                // Sin Save la ruta aprendida quedaba solo en memoria y el próximo
                // arranque de la app volvía a adivinar el exe entre los de la carpeta.
                _settings.Save();
            }
            catch (Exception ex) { _logging.LogDebug($"ProcessService: guardar ruta real de {ruleKey}: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"ProcessService: recordar ruta de {ruleKey}: {ex.Message}");
        }
    }

    /// <summary>Resuelve una sola vez el exe principal de la carpeta (en el barrido, no en la UI).</summary>
    private static void EnsureExpectedExe(LaunchSession session)
    {
        if (session.ExpectedResolved) return;
        session.ExpectedResolved = true;
        try
        {
            if (string.IsNullOrWhiteSpace(session.InstallDir)) return;
            var resolved = GameExeResolver.FindBestGameExePath(session.InstallDir);
            if (!string.IsNullOrWhiteSpace(resolved)) session.ExpectedExePath = resolved;
        }
        catch { }
    }

    public GameRuleStatus? GetGameRuleStatus(string exeFileName)
    {
        if (string.IsNullOrWhiteSpace(exeFileName)) return null;
        var exe = ResolveExeAlias(Path.GetFileName(exeFileName));
        lock (_ruleStatusLock)
            return _ruleStatuses.TryGetValue(exe, out var status) ? status : null;
    }

    /// <summary>Tiempo que queda de la ventana de seguimiento launcher→juego (null si no hay sesión activa).</summary>
    public TimeSpan? GetLaunchSessionRemaining(string exeFileName)
    {
        if (string.IsNullOrWhiteSpace(exeFileName)) return null;
        var exe = ResolveExeAlias(Path.GetFileName(exeFileName));
        lock (_launchSessionLock)
        {
            if (_launchSessions.TryGetValue(exe, out var session))
            {
                var left = session.DeadlineUtc - DateTime.UtcNow;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
        return null;
    }

    private void SetGameRuleStatus(GameRuleStatus status)
    {
        lock (_ruleStatusLock) _ruleStatuses[status.Exe] = status;
        try { GameRuleStatusChanged?.Invoke(status); }
        catch (Exception ex) { _logging.LogDebug($"ProcessService: notificar estado de regla de {status.Exe}: {ex.Message}"); }
    }

    /// <summary>Tick de las sesiones: expira las vencidas y reintenta enganchar el proceso real.</summary>
    private void LaunchSessionTick(object? state)
    {
        if (Interlocked.Exchange(ref _launchSessionTickBusy, 1) == 1) return;
        try
        {
            List<LaunchSession> sessions;
            lock (_launchSessionLock)
            {
                if (_launchSessions.Count == 0) return;
                sessions = _launchSessions.Values.ToList();
            }

            var now = DateTime.UtcNow;
            foreach (var session in sessions)
            {
                if (now >= session.DeadlineUtc)
                {
                    bool removed;
                    lock (_launchSessionLock) removed = _launchSessions.Remove(session.RuleKey);
                    if (!removed) continue;

                    bool applied;
                    lock (session.Sync) applied = session.Applied;
                    if (!applied)
                    {
                        SetGameRuleStatus(new GameRuleStatus(session.RuleKey, GameRuleState.Failed, session.Launcher));
                        _logging.LogWarning($"ProcessService: no se encontró el proceso del juego {session.RuleKey} (launcher={session.Launcher ?? "directo"}) en {LaunchSessionTimeoutSeconds} s; la regla no se aplicó.");
                    }
                    // Si ya se había aplicado, el estado queda Applied mientras el
                    // proceso viva; UnregisterProcess lo limpia al cerrar el juego.
                    continue;
                }

                ScanProcessesForLaunchSession(session);
            }
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"ProcessService: tick de sesiones de lanzamiento: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _launchSessionTickBusy, 0);
        }
    }

    /// <summary>
    /// Busca los procesos de la cadena y aplica la regla al juego final, no al
    /// launcher intermedio. La cadena se deja estabilizar: un candidato que solo
    /// coincide por carpeta espera unos segundos, y si debajo aparece otro proceso
    /// real, el padre se descarta. La regla recién se aplica a las hojas de la
    /// cadena. Al final se guarda la ruta real para el próximo lanzamiento.
    /// </summary>
    private void ScanProcessesForLaunchSession(LaunchSession session)
    {
        // El timer, el barrido inicial y el evento WMI pueden coincidir: un solo
        // barrido a la vez por sesión.
        if (Interlocked.Exchange(ref session.ScanBusy, 1) == 1) return;
        try
        {
            EnsureExpectedExe(session);
            var now = DateTime.UtcNow;
            var candidates = new List<LaunchCandidate>();
            var alivePids = new HashSet<int>();

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    alivePids.Add(p.Id);
                    if (p.HasExited) continue;
                    string pname = p.ProcessName + ".exe";
                    if (IsLaunchChainNoise(pname)) continue;
                    bool already;
                    lock (session.Sync) already = session.AppliedPids.Contains(p.Id);
                    if (already) continue;
                    TryGetProcessPath(p, out var ppath);
                    var quality = MatchLaunchSession(session, pname, ppath);
                    if (quality == LaunchMatchQuality.None) continue;

                    DateTime start = now;
                    try { start = p.StartTime; } catch { }
                    candidates.Add(new LaunchCandidate
                    {
                        Pid = p.Id,
                        Name = pname,
                        Path = ppath,
                        Quality = quality,
                        IsStub = GameExeResolver.IsStubExeName(pname)
                            || (!string.IsNullOrEmpty(ppath) && GameExeResolver.IsStubExe(ppath)),
                        IsLauncherLike = IsLikelyIntermediateLauncherName(pname),
                        IsHelperLike = IsChainHelperName(pname),
                        StartUtc = start
                    });
                }
                catch { }
                finally { p.Dispose(); }
            }

            // Limpiar pids que ya murieron también de la lista de aplicados: si
            // Windows reutiliza el pid en un relanzamiento, la regla no puede
            // quedar bloqueada para el proceso nuevo (pasa sin WMI activo, que es
            // el único que avisa del cierre).
            lock (session.Sync)
            {
                foreach (var pid in session.AppliedPids.ToList())
                {
                    bool reused = alivePids.Contains(pid)
                        && session.AppliedProcessStartUtc.TryGetValue(pid, out var appliedStart)
                        && GetProcessStartUtc(pid) is DateTime currentStart
                        && currentStart != appliedStart;
                    if (!alivePids.Contains(pid) || reused)
                    {
                        session.AppliedPids.Remove(pid);
                        session.AppliedProcessStartUtc.Remove(pid);
                    }
                }
            }

            if (candidates.Count == 0)
            {
                lock (session.Sync)
                {
                    foreach (var pid in session.FirstSeenUtc.Keys.Where(pid => !alivePids.Contains(pid)).ToList())
                        session.FirstSeenUtc.Remove(pid);
                }
                return;
            }

            // Primera vez visto de cada candidato: la ventana de estabilización se
            // cuenta desde que aparece, no desde que se abrió la sesión (un juego
            // puede tardar en arrancar y no queremos aplicar al launcher por eso).
            lock (session.Sync)
            {
                var alive = new HashSet<int>(candidates.Select(c => c.Pid));
                foreach (var pid in session.FirstSeenUtc.Keys.Where(pid => !alive.Contains(pid)).ToList())
                    session.FirstSeenUtc.Remove(pid);
                foreach (var c in candidates)
                    session.FirstSeenUtc.TryAdd(c.Pid, now);
            }

            // Marcar los padres que tienen un candidato real debajo. Ese es el
            // launcher intermedio: no recibe la regla; la recibe el hijo.
            var byPid = candidates.ToDictionary(c => c.Pid);
            var parentMap = GetParentPidMap();
            foreach (var child in candidates)
            {
                if (child.IsStub || child.IsHelperLike) continue;
                int current = child.Pid;
                for (int depth = 0; depth < 8; depth++)
                {
                    if (!parentMap.TryGetValue(current, out var parent) || parent <= 0) break;
                    if (byPid.TryGetValue(parent, out var parentCandidate))
                        parentCandidate.HasMatchingChild = true;
                    current = parent;
                }
            }

            // Solo las hojas reales: el juego final y sus componentes. Los stubs y
            // helpers no se muestran ni se usan como "el juego".
            var leaves = candidates
                .Where(c => !c.HasMatchingChild && !c.IsStub && !c.IsHelperLike)
                .OrderByDescending(c => c.Quality)
                .ThenByDescending(c => c.StartUtc)
                .ThenByDescending(c => c.Path?.Length ?? 0)
                .ToList();
            if (leaves.Count == 0) return;

            // Si lo único que quedó es un launcher (sin hijo real todavía), se
            // sigue esperando. Nunca se le aplica la regla al launcher intermedio:
            // la sesión vence y la UI lo informa como "no se pudo identificar".
            var applySet = leaves.Where(c => !c.IsLauncherLike).ToList();
            if (applySet.Count == 0) return;

            var ready = applySet.Where(c =>
            {
                bool direct = (c.Quality == LaunchMatchQuality.Name && !c.IsLauncherLike)
                    || (c.Quality == LaunchMatchQuality.Expected && session.ExpectedFromMemory);
                var settle = direct ? TimeSpan.Zero : TimeSpan.FromSeconds(LaunchChainSettleSeconds);
                return IsCandidateSettled(session, c, now, settle);
            }).ToList();
            if (ready.Count == 0) return;

            ready.Sort((a, b) =>
            {
                int q = b.Quality.CompareTo(a.Quality);
                if (q != 0) return q;
                int s = b.StartUtc.CompareTo(a.StartUtc);
                if (s != 0) return s;
                return (b.Path?.Length ?? 0).CompareTo(a.Path?.Length ?? 0);
            });

            foreach (var c in ready)
            {
                MarkSessionProcess(c.Pid, c.Name, c.Path, session.RuleKey);
                ApplyRuleToSessionProcess(session, c.Pid, c.Name, c.Path, c.Quality);
            }
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"ProcessService: barrido de la sesión de {session.RuleKey}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref session.ScanBusy, 0);
        }
    }

    private static bool IsCandidateSettled(LaunchSession session, LaunchCandidate candidate, DateTime now, TimeSpan settle)
    {
        if (settle <= TimeSpan.Zero) return true;
        DateTime first;
        lock (session.Sync)
            if (!session.FirstSeenUtc.TryGetValue(candidate.Pid, out first)) return false;
        return now - first >= settle;
    }

    /// <summary>
    /// Deja registrado en el estado de procesos que este pid es el juego de la
    /// regla (aunque haya nacido con otro nombre). Sin esto, el barrido aplicaría
    /// la regla pero el cierre del proceso no limpiaría el estado ni el plan.
    /// </summary>
    private void MarkSessionProcess(int pid, string pname, string? ppath, string ruleKey)
    {
        bool changed = false;
        lock (_eventLock)
        {
            if (_procGames.TryGetValue(pid, out var existing))
            {
                if (!string.Equals(existing.RuleKey, ruleKey, StringComparison.OrdinalIgnoreCase))
                {
                    existing.RuleKey = ruleKey;
                    changed = true;
                }
                existing.ProcessName = pname;
                existing.Path ??= ppath;
            }
            else
            {
                _procGames[pid] = new RunningProc { ProcessName = pname, Path = ppath, RuleKey = ruleKey };
                changed = true;
            }
        }
        if (changed) RunningGamesChanged?.Invoke();
    }

    /// <summary>
    /// ¿Qué tan fuerte es la coincidencia de este proceso con la sesión? Nombre o
    /// alias es la señal más confiable; la ruta exacta del exe esperado, la
    /// siguiente; estar dentro de la carpeta, solo un candidato débil.
    /// </summary>
    private LaunchMatchQuality MatchLaunchSession(LaunchSession session, string pname, string? ppath)
    {
        if (MatchesRuleName(pname, session.RuleKey)) return LaunchMatchQuality.Name;
        if (!string.IsNullOrEmpty(ppath) && !string.IsNullOrEmpty(session.ExpectedExePath)
            && string.Equals(ppath, session.ExpectedExePath, StringComparison.OrdinalIgnoreCase))
            return LaunchMatchQuality.Expected;
        if (IsUnderDirectory(ppath, session.InstallDir) || MatchesRulePath(ppath, session.RuleKey))
            return LaunchMatchQuality.Path;
        return LaunchMatchQuality.None;
    }

    /// <summary>Sesión activa que corresponde a un proceso (null si ninguna).</summary>
    private LaunchSession? FindLaunchSessionForProcess(string pname, string? ppath, out LaunchMatchQuality quality)
    {
        quality = LaunchMatchQuality.None;
        List<LaunchSession> sessions;
        var now = DateTime.UtcNow;
        lock (_launchSessionLock)
            sessions = _launchSessions.Values.Where(s => s.DeadlineUtc > now).ToList();

        LaunchSession? best = null;
        foreach (var session in sessions)
        {
            var q = MatchLaunchSession(session, pname, ppath);
            if (q > quality)
            {
                quality = q;
                best = session;
            }
        }
        return best;
    }

    /// <summary>Aplica la regla efectiva al proceso identificado y actualiza el estado de la UI.</summary>
    private void ApplyRuleToSessionProcess(LaunchSession session, int pid, string pname, string? ppath, LaunchMatchQuality quality)
    {
        var rules = GetRulesCached();
        var sessionRules = GetSessionRulesSnapshot();
        sessionRules.TryGetValue(session.RuleKey, out var sr);
        rules.TryGetValue(session.RuleKey, out var pr);
        var effective = Merge(sr, pr);
        if (RuleIsEmpty(effective))
        {
            lock (_launchSessionLock) _launchSessions.Remove(session.RuleKey);
            SetGameRuleStatus(new GameRuleStatus(session.RuleKey, GameRuleState.None, session.Launcher));
            return;
        }

        // El evento WMI, el barrido inicial y el tick pueden llegar con el mismo pid:
        // se marca antes de aplicar para que la regla se aplique una sola vez.
        DateTime? processStart = GetProcessStartUtc(pid);
        bool first;
        lock (session.Sync)
        {
            if (session.AppliedPids.Contains(pid))
            {
                // Mismo PID, mismo proceso: ya se le aplicó. Si el PID se reutilizó
                // (otro inicio), es un proceso nuevo y sí hay que aplicarle.
                if (!processStart.HasValue
                    || !session.AppliedProcessStartUtc.TryGetValue(pid, out var appliedStart)
                    || appliedStart == processStart.Value)
                    return;
                session.AppliedPids.Remove(pid);
                session.WeakApplied.Remove(pid);
            }
            session.AppliedPids.Add(pid);
            if (processStart.HasValue) session.AppliedProcessStartUtc[pid] = processStart.Value;
            first = !session.Applied;
            session.Applied = true;
        }

        bool strong = quality >= LaunchMatchQuality.Expected;
        // Coincidencia débil (carpeta): se guardan las prioridades originales por si
        // después aparece el juego real y hay que devolverle este intermedio las suyas.
        if (!strong)
            SnapshotWeakApplied(session, pid);

        var feedback = ApplyEffectiveRule(pid, session.RuleKey, sessionRules, rules);

        // El juego real (fuerte) superó a un intermedio débil: al intermedio se le
        // devuelven sus prioridades originales (no era el juego).
        if (strong)
            RevertWeakApplied(session, pid);

        RememberResolvedPath(session.RuleKey, ppath);
        if (!string.IsNullOrWhiteSpace(ppath))
        {
            var name = Path.GetFileName(ppath);
            if (!string.IsNullOrEmpty(name) && !IsLaunchChainNoise(name)
                && !IsLikelyIntermediateLauncherName(name) && !IsChainHelperName(name)
                && !GameExeResolver.IsStubExeName(name))
            {
                session.ExpectedExePath = ppath;
                session.ExpectedResolved = true;
                session.ExpectedFromMemory = true;
            }
        }
        ReportGameRuleApplied(session.RuleKey, session.Launcher, pname, feedback,
            force: first, strong: strong);
        if (first)
            _logging.LogInfo($"ProcessService: regla de {session.RuleKey} aplicada al proceso real {pname} (pid {pid}; launcher={session.Launcher ?? "directo"}).");
    }

    /// <summary>Guarda las prioridades originales de un proceso aplicado en débil
    /// (coincidencia por carpeta): si después aparece el juego real, se revierten.</summary>
    private void SnapshotWeakApplied(LaunchSession session, int pid)
    {
        int? cpu = null;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return;
            try
            {
                cpu = p.PriorityClass switch
                {
                    ProcessPriorityClass.Idle => 0,
                    ProcessPriorityClass.BelowNormal => 1,
                    ProcessPriorityClass.Normal => 2,
                    ProcessPriorityClass.AboveNormal => 3,
                    ProcessPriorityClass.High => 4,
                    ProcessPriorityClass.RealTime => 5,
                    _ => (int?)null
                };
            }
            catch { }
        }
        catch { return; }
        long affinity = GetAffinity(pid);
        int? gpu = GetGpuPriority(pid);
        int? io = GetIoPriority(pid);
        if (cpu == null && affinity <= 0 && gpu == null && io == null) return;
        lock (session.Sync) session.WeakApplied[pid] = new OriginalPriorities(cpu, affinity, gpu, io);
    }

    /// <summary>Devuelve sus prioridades originales a los intermedios aplicados en
    /// débil cuando el juego real ya recibió la regla (excepto al propio juego).</summary>
    private void RevertWeakApplied(LaunchSession session, int exceptPid)
    {
        List<(int Pid, OriginalPriorities Orig)> pending;
        lock (session.Sync)
        {
            pending = session.WeakApplied
                .Where(kv => kv.Key != exceptPid)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
            foreach (var (pid, _) in pending) session.WeakApplied.Remove(pid);
        }
        foreach (var (pid, orig) in pending)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) continue;
                if (orig.Cpu is int cpu) ApplyCpuPriority(pid, cpu);
                if (orig.Affinity > 0) ApplyAffinity(pid, orig.Affinity);
                if (orig.Gpu is int gpu) ApplyGpuPriority(pid, gpu);
                if (orig.Io is int io) ApplyIoPriority(pid, io);
                _logging.LogInfo($"ProcessService: prioridades originales devueltas a {pid} (era un intermedio, ya está el juego real).");
            }
            catch { }
        }
    }

    /// <summary>
    /// Publica el estado Applied sin pisar un Applied anterior sano: el primer
    /// proceso identificado es el que se muestra (normalmente el juego), no un hijo
    /// posterior (anti-cheat, crash handler, etc.). Sí se actualiza cuando el nuevo
    /// proceso es mejor: deja de haber limitaciones, se pasó de un stub/helper al
    /// juego real, o se pasó de una coincidencia débil (solo carpeta) a una fuerte
    /// (nombre/ruta aprendida).
    /// </summary>
    private void ReportGameRuleApplied(string ruleKey, string? launcher, string processName, RuleApplyFeedback feedback, bool force = false, bool strong = false)
    {
        var current = GetGameRuleStatus(ruleKey);
        bool improved = current is { State: GameRuleState.Applied }
            && ((current.Limited && !feedback.AnyFailed)
                || (!current.Strong && strong)
                || (IsLaunchChainNoise(current.ProcessName ?? "") || GameExeResolver.IsStubExeName(current.ProcessName ?? ""))
                   && !(IsLaunchChainNoise(processName) || GameExeResolver.IsStubExeName(processName)));
        if (!force && current is { State: GameRuleState.Applied } && !improved)
            return;

        SetGameRuleStatus(new GameRuleStatus(
            ruleKey,
            GameRuleState.Applied,
            launcher ?? current?.Launcher,
            processName,
            feedback.AnyFailed,
            strong));
    }

    /// <summary>
    /// Si un proceso no matchea la regla directamente, hereda la de un ancestro que
    /// sí (el juego real lanza anti-cheat/hijos con otro nombre). El launcher queda
    /// afuera: si el ancestro no tiene RuleKey, no se propaga nada.
    /// </summary>
    private string? FindAncestorRuleKey(int pid, Dictionary<string, ProcessRule> rules, Dictionary<string, ProcessRule> session)
    {
        var keys = EffectiveKeys(rules, session);
        if (keys.Count == 0) return null;

        var visited = new HashSet<int> { pid };
        int current = pid;
        // 4 niveles alcanzan para game → stub → juego; caminar todo el árbol de un
        // proceso ajeno (explorer → userinit → …) sería caro y no aporta.
        for (int depth = 0; depth < 4; depth++)
        {
            current = GetParentPid(current);
            if (current <= 0 || !visited.Add(current)) return null;

            lock (_eventLock)
            {
                if (_procGames.TryGetValue(current, out var parent) && parent.RuleKey != null)
                    return parent.RuleKey;
            }

            try
            {
                using var p = Process.GetProcessById(current);
                if (p.HasExited) return null;
                string pname = p.ProcessName + ".exe";
                // Un launcher no presta su regla a sus hijos (no tiene); si aparece
                // uno en la cadena, se corta.
                if (IsLaunchChainNoise(pname)) return null;
                TryGetProcessPath(p, out var ppath);
                foreach (var key in keys)
                    if (MatchesRuleKey(pname, ppath, key)) return key;

                // Si el ancestro tiene ruta legible y está fuera de una carpeta de
                // juego conocida, no es parte de la cadena del juego: cortar.
                if (!string.IsNullOrEmpty(ppath) && !IsKnownGameFolder(ppath)) return null;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Quita un pid de las sesiones activas (el proceso murió).</summary>
    private void ReleaseLaunchSessionPid(int pid)
    {
        lock (_launchSessionLock)
        {
            foreach (var session in _launchSessions.Values)
                lock (session.Sync)
                {
                    session.AppliedPids.Remove(pid);
                    session.AppliedProcessStartUtc.Remove(pid);
                    session.FirstSeenUtc.Remove(pid);
                    session.WeakApplied.Remove(pid);
                }
        }
    }

    // ===== Reglas persistidas =====

    public Dictionary<string, ProcessRule> GetRules()
    {
        var rules = _settings.Get("process.rules", new Dictionary<string, ProcessRule>()) ?? new();
        // Migrar en la lectura las reglas guardadas con el nombre del stub al exe
        // real (mismo caso que los favoritos): así la regla de CS2 guardada como
        // vconsole2.exe sigue aplicando a cs2.exe y a toda su carpeta.
        var aliased = new Dictionary<string, ProcessRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in rules)
            aliased[ResolveExeAlias(kv.Key)] = kv.Value;
        rules = aliased;
        // El plan de energía ya no se persiste por juego (el "Siempre" del plan se
        // eliminó; el plan permanente se maneja en su apartado propio). Migrar en
        // la lectura los planes viejos guardados para que no sigan aplicando.
        if (rules.Any(kv => !string.IsNullOrEmpty(kv.Value.PowerPlanGuid)))
        {
            foreach (var key in rules.Keys.ToList())
                rules[key] = rules[key] with { PowerPlanGuid = null };
        }
        return rules;
    }

    // ===== Reglas de sesión ("Actual": solo la apertura actual del juego) =====

    public void SetSessionRule(string exe, ProcessRule rule)
    {
        lock (_sessionLock)
        {
            if (RuleIsEmpty(rule))
            {
                // Todo "Por defecto" = sin override de sesión.
                _sessionRules.Remove(exe);
            }
            else
            {
                _sessionRules[exe] = rule;
            }
        }
        PersistSessionRules();
    }

    public ProcessRule? GetSessionRule(string exe)
    {
        lock (_sessionLock)
            return _sessionRules.TryGetValue(exe, out var r) ? r : null;
    }

    public void ClearSessionRule(string exe)
    {
        lock (_sessionLock)
        {
            _sessionRules.Remove(exe);
        }
        PersistSessionRules();
    }

    /// <summary>Persiste el snapshot de reglas de sesión (caché "Actual" que sobrevive al reinicio).</summary>
    private void PersistSessionRules()
    {
        Dictionary<string, ProcessRule> snapshot;
        lock (_sessionLock)
            snapshot = new Dictionary<string, ProcessRule>(_sessionRules, StringComparer.OrdinalIgnoreCase);
        try
        {
            _settings.Set("process.sessionRules", snapshot);
            _settings.Save();
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: guardar reglas de sesión: {ex.Message}");
        }
    }

    public ProcessRule? GetEffectiveRule(string exe)
    {
        var rules = GetRules();
        rules.TryGetValue(exe, out var persistent);
        ProcessRule? session;
        lock (_sessionLock)
            _sessionRules.TryGetValue(exe, out session);
        return Merge(session, persistent);
    }

    /// <summary>Regla efectiva: la de sesión con los campos de la guardada como respaldo.</summary>
    private static ProcessRule? Merge(ProcessRule? session, ProcessRule? persistent)
    {
        if (session == null) return persistent;
        if (persistent == null) return session;
        return new ProcessRule(
            session.CpuPriority ?? persistent.CpuPriority,
            session.AffinityMask ?? persistent.AffinityMask,
            session.GpuPriority ?? persistent.GpuPriority,
            session.PowerPlanGuid ?? persistent.PowerPlanGuid,
            session.IoPriority ?? persistent.IoPriority);
    }

    private static bool RuleIsEmpty(ProcessRule? rule)
        => rule == null
        || (rule.CpuPriority == null && rule.AffinityMask == null && rule.GpuPriority == null
            && string.IsNullOrEmpty(rule.PowerPlanGuid)
            && rule.IoPriority == null);

    private Dictionary<string, ProcessRule> GetSessionRulesSnapshot()
    {
        lock (_sessionLock)
            return new Dictionary<string, ProcessRule>(_sessionRules, StringComparer.OrdinalIgnoreCase);
    }

    public void SaveRule(string exe, ProcessRule rule)
    {
        // El plan de energía no se guarda por juego (solo sesión): el apartado de
        // energía maneja el plan permanente.
        rule = rule with { PowerPlanGuid = null };
        var rules = GetRules();
        bool empty = RuleIsEmpty(rule);
        if (empty)
        {
            rules.Remove(exe);
            RemoveBirthPriority(exe);
        }
        else
        {
            rules[exe] = rule;
            // Prioridad de nacimiento: si la regla define prioridad de CPU, el exe
            // nace con ella (PerfOptions); si no, se limpia la clave que pueda haber.
            // En hilo de fondo porque enumera los exes de la carpeta de instalación.
            _ = Task.Run(() => { try { SyncBirthPriority(exe, rule); } catch (Exception ex) { _logging.LogDebug($"ProcessService: sincronizar prioridad de nacimiento de {exe}: {ex.Message}"); } });
        }
        _settings.Set("process.rules", rules);
        _settings.Save();
        InvalidateRulesCache();
    }

    public void RemoveRule(string exe)
    {
        var rules = GetRules();
        if (rules.Remove(exe))
        {
            RemoveBirthPriority(exe);
            _settings.Set("process.rules", rules);
            _settings.Save();
            InvalidateRulesCache();
        }
    }

    // ===== Prioridad de nacimiento (PerfOptions) =====
    // Windows aplica CpuPriorityClass e IoPriority al CREAR el proceso (Image File
    // Execution Options): el proceso nace con esa prioridad, antes de que el
    // anti-cheat pueda bloquear cambios (EAC niega el cambio en un proceso ya
    // corriendo, incluso elevado). Se escribe para el exe de la regla y para todos
    // los exe de su carpeta de instalación (el proceso real suele tener otro nombre).

    private const string IfeoKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private static int? PerfOptionsCpuValue(int cpuPriority) => cpuPriority switch
    {
        0 => 1, // Idle
        1 => 5, // Below Normal
        2 => 2, // Normal
        3 => 6, // Above Normal
        4 => 3, // High
        5 => 4, // Realtime
        _ => null
    };

    /// <summary>
    /// Valor de PerfOptions\IoPriority para una prioridad E/S. Windows solo aplica
    /// al nacer hasta "Normal" (0=VeryLow, 1=Low, 2=Normal); Alta (3) y Crítica (4)
    /// NO existen al nacer (documentado: el mecanismo IFEO se limita a Normal y
    /// menor). Devuelve null para esas → la clave se borra y aplican solo en vivo.
    /// </summary>
    private static int? PerfOptionsIoValue(int ioPriority) => ioPriority switch
    {
        0 => 0, // Very Low
        1 => 1, // Low
        2 => 2, // Normal
        _ => null // 3=High, 4=Critical: imposibles al nacer, solo en vivo
    };

    /// <summary>
    /// Escribe las prioridades de nacimiento (PerfOptions) del exe: CpuPriorityClass
    /// y/o IoPriority. Cada dimensión con valor se escribe; sin valor se borra esa
    /// clave (así cambiar de "Baja" a "Alta" no deja la E/S vieja fijada al nacer,
    /// y el proceso nace con el default). Se aplica al exe de la regla y a todos los
    /// exe de su carpeta de instalación (el proceso real suele tener otro nombre).
    /// </summary>
    /// <summary>true si el exe es un componente del sistema (vive en System32/SysWOW64).
    /// A esos no se les escribe prioridad de nacimiento: PerfOptions se aplica por NOMBRE
    /// y afectaría a TODOS los procesos con ese nombre (svchost, lsass…), no solo al que
    /// se quiso regular.</summary>
    private static bool IsSystemComponent(string exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (File.Exists(Path.Combine(sys32, exe))) return true;
            string syswow = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            if (!string.Equals(syswow, sys32, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(syswow, exe))) return true;
        }
        catch { }
        return false;
    }

    /// <summary>true si el exe es un componente del sistema (la UI lo usa para avisar que
    /// el "Siempre" solo puede aplicar EN VIVO, sin registro de nacimiento).</summary>
    public bool IsSystemProcessName(string exe) => IsSystemComponent(exe);

    private void ApplyBirthPriority(string exe, int? cpuPriority, int? ioPriority)
    {
        var cpuVal = cpuPriority is int c ? PerfOptionsCpuValue(c) : null;
        var ioVal = ioPriority is int i ? PerfOptionsIoValue(i) : null;
        if (cpuVal == null && ioVal == null) return;
        var targets = new List<string> { exe };
        try
        {
            lock (_pathsLock)
            {
                if (_knownPaths.TryGetValue(exe, out var dir) && !string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    // Componentes del sistema: no enumerar su carpeta (System32 tiene miles
                    // de exes) ni escribir nacimiento para ninguno de sus binarios.
                    bool systemDir = dir.StartsWith(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        StringComparison.OrdinalIgnoreCase);
                    if (!systemDir)
                        targets.AddRange(Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
                            .Select(Path.GetFileName)
                            .Where(f => !string.IsNullOrEmpty(f))!);
                }
            }
        }
        catch { /* sin carpeta conocida: solo el exe de la regla */ }
        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Procesos del sistema: solo en vivo, nunca al nacer (PerfOptions por nombre
            // afectaría a todos los procesos con ese nombre).
            if (IsSystemComponent(target)) continue;
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey($@"{IfeoKeyPath}\{target}\PerfOptions");
                if (key == null) continue;
                if (cpuVal != null) key.SetValue("CpuPriorityClass", cpuVal.Value, RegistryValueKind.DWord);
                else key.DeleteValue("CpuPriorityClass", throwOnMissingValue: false);
                if (ioVal != null) key.SetValue("IoPriority", ioVal.Value, RegistryValueKind.DWord);
                else key.DeleteValue("IoPriority", throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"ProcessService: prioridad de nacimiento {target}: {ex.Message}");
            }
        }
    }

    private void RemoveBirthPriority(string exe)
    {
        // Mismos targets que ApplyBirthPriority: el exe de la regla y todos los exe
        // de su carpeta de instalación (el proceso real suele tener otro nombre).
        // Antes solo se borraba el PerfOptions del exe de la regla y quedaban
        // huérfanos que seguían fijando la prioridad al nacer tras quitar la regla.
        var targets = new List<string> { exe };
        try
        {
            lock (_pathsLock)
            {
                if (_knownPaths.TryGetValue(exe, out var dir) && !string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    bool systemDir = dir.StartsWith(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        StringComparison.OrdinalIgnoreCase);
                    if (!systemDir)
                        targets.AddRange(Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
                            .Select(Path.GetFileName)
                            .Where(f => !string.IsNullOrEmpty(f))!);
                }
            }
        }
        catch { /* sin carpeta conocida: solo el exe de la regla */ }
        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsSystemComponent(target)) continue;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{IfeoKeyPath}\{target}", writable: true);
                key?.DeleteSubKeyTree("PerfOptions", throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"ProcessService: quitar prioridad de nacimiento {target}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Sincroniza las claves PerfOptions con la regla: escribe CPU y/o E/S al nacer
    /// (según lo que la regla defina y Windows permita), o borra todo si la regla
    /// quedó vacía (o con solo valores que no existen al nacer, ej. E/S Alta).
    /// </summary>
    private void SyncBirthPriority(string exe, ProcessRule? rule)
    {
        int? cpu = rule?.CpuPriority;
        int? io = rule?.IoPriority;
        bool any = (cpu is int c && PerfOptionsCpuValue(c) != null)
                || (io is int i && PerfOptionsIoValue(i) != null);
        if (any) ApplyBirthPriority(exe, cpu, io);
        else RemoveBirthPriority(exe);
    }

    private bool HasKnownPaths()
    {
        lock (_pathsLock) return _knownPaths.Count > 0;
    }

    /// <summary>Al iniciar, sincroniza las claves PerfOptions con las reglas guardadas (recupera escrituras interrumpidas).</summary>
    private void ReconcileBirthPriorities()
    {
        try
        {
            EnsureKnownPathsLoaded();
            // Esperar a que el mapa exe→carpeta termine de cargar (es asíncrono)
            // para escribir PerfOptions también a los exes reales del juego.
            for (int i = 0; i < 50 && !HasKnownPaths(); i++) Thread.Sleep(200);
            foreach (var (exe, rule) in GetRules())
            {
                try { SyncBirthPriority(exe, rule); } catch (Exception ex) { _logging.LogDebug($"ProcessService: reconciliar prioridad de nacimiento de {exe}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: reconciliar prioridades de nacimiento: {ex.Message}");
        }
    }

    /// <summary>
    /// ¿El launcher indicado está corriendo? Con WMI activo es una lectura de un
    /// HashSet alimentado por eventos (cero polling). Sin WMI, un chequeo único
    /// por nombre (no periódico) para no dejar el botón congelado.
    /// </summary>
    public bool IsLauncherRunning(string procName)
    {
        // Blacksmith (Dark and Darker): el proceso real del launcher es
        // BlacksmithIM.exe (no existe Blacksmith.exe); se aceptan ambos.
        bool blacksmithQuery = procName.Equals("Blacksmith", StringComparison.OrdinalIgnoreCase);
        if (WmiEventsActive)
        {
            lock (_launcherLock)
            {
                if (_runningLaunchers.Contains(procName)) return true;
                if (blacksmithQuery && _runningLaunchers.Contains("BlacksmithIM")) return true;
                return false;
            }
        }
        try
        {
            if (Process.GetProcessesByName(procName).Length > 0) return true;
            return blacksmithQuery && Process.GetProcessesByName("BlacksmithIM").Length > 0;
        }
        catch { return false; }
    }

    // ===== Plan de energía por juego (aplicar al correr, revertir al cerrar) =====

    public ProcessAppInfo? FindRunningProcess(string exe)
    {
        EnsureKnownPathsLoaded();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.HasExited) continue;
                    TryGetProcessPath(p, out var ppath);
                    if (!MatchesRuleKey(p.ProcessName + ".exe", ppath, exe)) continue;
                    double wsMB = p.WorkingSet64 / (1024.0 * 1024.0);
                    return new ProcessAppInfo(p.Id, p.ProcessName, p.ProcessName + ".exe", null, 0, wsMB);
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Ruta del ejecutable de un proceso (con fallback para procesos protegidos).</summary>
    public string? GetProcessPath(Process process)
    {
        TryGetProcessPath(process, out var path);
        return path;
    }

    /// <summary>
    /// Todos los procesos corriendo que matchean la regla (por nombre o por ruta
    /// dentro de la carpeta de instalación). Sirve para aplicar la regla al arrancar
    /// un juego: cubre tanto el launcher como el proceso real si tiene otro nombre.
    /// </summary>
    public List<ProcessAppInfo> FindRunningProcessesForRule(string ruleExe)
    {
        EnsureKnownPathsLoaded();
        var result = new List<ProcessAppInfo>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.HasExited) continue;
                    TryGetProcessPath(p, out var ppath);
                    if (!MatchesRuleKey(p.ProcessName + ".exe", ppath, ruleExe)) continue;
                    double wsMB = p.WorkingSet64 / (1024.0 * 1024.0);
                    result.Add(new ProcessAppInfo(p.Id, p.ProcessName, p.ProcessName + ".exe", null, 0, wsMB));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    public void ApplyPowerPlanIfRunning(string exe, string? planGuid)
    {
        if (string.IsNullOrEmpty(planGuid)) return;
        EnsureKnownPathsLoaded();
        try
        {
            bool running = Process.GetProcesses().Any(p =>
            {
                try
                {
                    if (p.HasExited) return false;
                    TryGetProcessPath(p, out var ppath);
                    return MatchesRuleKey(p.ProcessName + ".exe", ppath, exe);
                }
                catch { return false; }
            });
            if (!running) return;
            lock (_planLock)
            {
                _defaultPlanGuid ??= _powerPlan.GetActivePowerPlanGuid();
                _ = _powerPlan.SetActivePowerPlanAsync(planGuid);
                _appliedPlanExe = exe;
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: no se pudo activar el plan de energía para {exe}: {ex.Message}");
        }
    }

    public void RevertPowerPlanIfApplied(string exe)
    {
        lock (_planLock)
        {
            if (_appliedPlanExe != exe) return;
            _defaultPlanGuid ??= _powerPlan.GetActivePowerPlanGuid();
            _ = _powerPlan.SetActivePowerPlanAsync(_defaultPlanGuid);
            _appliedPlanExe = null;
        }
    }

    // ===== Modo eventos WMI (cero polling) =====
    // Los eventos de Win32_ProcessStartTrace/StopTrace solo cubren lo que pasa
    // DESPUÉS de suscribirse, así que el estado inicial (procesos ya corriendo al
    // arrancar la app) se siembra con una única enumeración. A partir de ahí, cada
    // nacimiento/muerte de proceso llega por evento y se mantiene: el badge de la
    // biblioteca, la aplicación de reglas guardadas (las de sesión solo van por
    // lanzamiento desde WinForge) y la reversión del plan de energía.

    private static List<string> EffectiveKeys(Dictionary<string, ProcessRule> rules, Dictionary<string, ProcessRule> session)
    {
        var keys = new List<string>(rules.Count + session.Count);
        keys.AddRange(rules.Keys);
        foreach (var k in session.Keys)
            if (!keys.Contains(k, StringComparer.OrdinalIgnoreCase))
                keys.Add(k);
        return keys;
    }

    /// <summary>
    /// Una sola enumeración al arrancar: siembra el set de juegos en ejecución
    /// (badge), rastrea los procesos que matchean una regla guardada, aplica sus
    /// reglas y activa el plan de energía si corresponde. Después de esto
    /// todo llega por eventos.
    /// </summary>
    private void SeedEventState()
    {
        try
        {
            EnsureKnownPathsLoaded();
            // Esperar el mapa exe→carpeta (sale de la caché de juegos, rápido) para
            // no perder el matching por ruta de procesos que ya corren.
            for (int i = 0; i < 25 && !HasKnownPaths(); i++) Thread.Sleep(200);

            var rules = GetRulesCached();
            // Fondo = solo guardadas: las de sesión ("Actual") aplican únicamente a
            // lanzamientos desde WinForge, nunca al matching de fondo.
            var session = new Dictionary<string, ProcessRule>(StringComparer.OrdinalIgnoreCase);
            var keys = EffectiveKeys(rules, session);

            var runningNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyRuleActivity = false;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.HasExited) continue;
                    string pname = p.ProcessName + ".exe";
                    TryGetProcessPath(p, out var ppath);

                    // LauncherNames guarda nombres sin .exe (Steam, Battle.net…).
                    string bareName = Path.GetFileNameWithoutExtension(pname);
                    if (LauncherNames.Contains(bareName))
                        lock (_launcherLock) _runningLaunchers.Add(bareName);

                    bool isKnown;
                    lock (_pathsLock) isKnown = _knownPaths.ContainsKey(pname);
                    if (isKnown) runningNow.Add(pname);

                    string? ruleKey = null;
                    if (!IsLaunchChainNoise(pname))
                    {
                        foreach (var key in keys)
                        {
                            if (MatchesRuleKey(pname, ppath, key)) { ruleKey = key; break; }
                        }
                        // Un launcher intermedio que solo coincide por carpeta no
                        // hereda la regla del juego. Si coincide por nombre, sí: la
                        // biblioteca puede haber detectado ese exe como el juego.
                        if (ruleKey != null && IsLikelyIntermediateLauncherName(pname)
                            && !MatchesRuleName(pname, ruleKey))
                            ruleKey = null;
                        // El proceso real puede ser un hijo con otro nombre (stub de
                        // anti-cheat, launcher del propio juego): hereda la regla del
                        // ancestro que sí matcheó. Solo se camina el árbol cuando hay
                        // un indicio (carpeta de juego conocida, ruta ilegible o nombre
                        // de stub): recorrer todos los procesos del arranque costaba
                        // varias instantáneas de Toolhelp por cada uno.
                        if (ruleKey == null && !IsSystemProcessName(pname)
                            && (ppath == null || IsKnownGameFolder(ppath) || GameExeResolver.IsStubExeName(pname)))
                            ruleKey = FindAncestorRuleKey(p.Id, rules, session);
                    }
                    if (isKnown || ruleKey != null)
                    {
                        lock (_eventLock)
                        {
                            // Conservar el badge del watcher fullscreen si existía
                            // (ver RegisterProcess): el cierre lo sacará del set.
                            string? badge = isKnown ? pname : null;
                            if (badge == null && _procGames.TryGetValue(p.Id, out var existing))
                                badge = existing.ExeName;
                            _procGames[p.Id] = new RunningProc
                            {
                                ProcessName = pname,
                                Path = ppath,
                                ExeName = badge,
                                RuleKey = ruleKey
                            };
                        }
                        if (ruleKey != null)
                        {
                            var feedback = ApplyEffectiveRule(p.Id, ruleKey, session, rules);
                            ReportGameRuleApplied(ruleKey, null, pname, feedback);
                            anyRuleActivity = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logging.LogDebug($"ProcessService: aplicar la regla efectiva a un proceso del juego: {ex.Message}");
                }
            }

            bool runningChanged;
            lock (_runningLock)
            {
                runningChanged = !_runningGames.SetEquals(runningNow);
                if (runningChanged) _runningGames = runningNow;
            }
            // anyRuleActivity: un juego con regla ya estaba corriendo al abrir la app;
            // el Modo juego tiene que activarse igual (no esperar a un cambio de badge).
            if (runningChanged || anyRuleActivity)
                RunningGamesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: sembrar estado de procesos: {ex.Message}");
        }
    }

    private void OnProcessStarted(int pid, string rawName)
    {
        string pname = rawName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? rawName
            : rawName + ".exe";
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return;
            TryGetProcessPath(p, out var ppath);
            RegisterProcess(pid, pname, ppath);
        }
        catch
        {
            // El proceso murió entre el evento y acá: registrar por nombre igual
            // para no perder su cierre (el stop event llega igual por el pid).
            RegisterProcess(pid, pname, null);
        }
    }

    private void OnProcessStopped(int pid, string rawName)
    {
        string pname = rawName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? rawName
            : rawName + ".exe";
        string bareName = Path.GetFileNameWithoutExtension(pname);
        if (LauncherNames.Contains(bareName))
        {
            bool launcherChanged;
            lock (_launcherLock) launcherChanged = _runningLaunchers.Remove(bareName);
            if (launcherChanged) LauncherStateChanged?.Invoke();
        }
        UnregisterProcess(pid);
    }

    /// <summary>
    /// Un proceso nació: si es un juego conocido se actualiza el badge al instante;
    /// si matchea una regla (por nombre o por ruta dentro de la carpeta de
    /// instalación) se rastrea, se le aplica la regla efectiva y se activa su plan.
    /// </summary>
    private void RegisterProcess(int pid, string pname, string? ppath)
    {
        // Launchers: los eventos ya cubren todo proceso; mantener un HashSet es casi gratis.
        // LauncherNames guarda nombres sin .exe (Steam, Battle.net…), así que se normaliza.
        string bareName = Path.GetFileNameWithoutExtension(pname);
        if (LauncherNames.Contains(bareName))
        {
            bool launcherChanged;
            lock (_launcherLock) launcherChanged = _runningLaunchers.Add(bareName);
            if (launcherChanged) LauncherStateChanged?.Invoke();
        }

        bool isKnown;
        lock (_pathsLock) isKnown = _knownPaths.ContainsKey(pname);

        bool changed = false;
        if (isKnown)
        {
            lock (_runningLock)
                if (_runningGames.Add(pname)) changed = true;
        }

        // Fondo = solo reglas guardadas: las de sesión ("Actual") aplican únicamente
        // a lanzamientos desde WinForge, nunca al matching de fondo.
        var rules = GetRulesCached();
        var session = new Dictionary<string, ProcessRule>(StringComparer.OrdinalIgnoreCase);
        bool noise = IsLaunchChainNoise(pname);
        // La sesión se busca antes del conteo rápido: con una regla de sesión y cero
        // guardadas, la sesión abierta es lo único que hay que atender.
        LaunchMatchQuality preQuality = LaunchMatchQuality.None;
        var preSession = noise ? null : FindLaunchSessionForProcess(pname, ppath, out preQuality);
        if (!isKnown && rules.Count == 0 && preSession == null) return;
        string? ruleKey = null;
        if (!noise)
        {
            foreach (var key in EffectiveKeys(rules, session))
            {
                if (MatchesRuleKey(pname, ppath, key)) { ruleKey = key; break; }
            }
            // Igual que en el sembrado: un launcher intermedio no recibe la regla
            // cuando la coincidencia fue solo por carpeta.
            if (ruleKey != null && IsLikelyIntermediateLauncherName(pname)
                && !MatchesRuleName(pname, ruleKey))
                ruleKey = null;
            // El juego real puede ser un hijo con otro nombre (stub de anti-cheat,
            // launcher del propio juego): hereda la regla del ancestro que sí matcheó.
            // Igual que en el sembrado inicial, solo se camina el árbol con un indicio
            // (carpeta conocida, ruta ilegible o stub): hacerlo por cada proceso del
            // sistema era caro y no aportaba.
            if (ruleKey == null && !IsSystemProcessName(pname)
                && (ppath == null || IsKnownGameFolder(ppath) || GameExeResolver.IsStubExeName(pname)))
                ruleKey = FindAncestorRuleKey(pid, rules, session);
        }

        // Sesión de lanzamiento: si hay una abierta y este proceso es el juego, la
        // sesión manda (aporta launcher + carpeta y el estado de la UI). Una
        // coincidencia débil (solo carpeta / exe esperado) NO se aplica acá: la
        // decide el barrido, que espera a que la cadena se estabilice. Así el
        // launcher intermedio no se lleva la regla antes que el juego.
        var launchSession = preSession;
        var launchQuality = preQuality;
        if (launchSession != null)
        {
            bool strongName = launchQuality == LaunchMatchQuality.Name
                && !IsLikelyIntermediateLauncherName(pname)
                && !GameExeResolver.IsStubExeName(pname);
            ruleKey = strongName ? launchSession.RuleKey : null;
        }

        if (isKnown || ruleKey != null)
        {
            lock (_eventLock)
            {
                // Si el watcher fullscreen ya lo agregó como juego (ExeName), conservar
                // el badge: el cierre lo sacará del set de juegos en ejecución.
                string? badge = isKnown ? pname : null;
                if (badge == null && _procGames.TryGetValue(pid, out var existing))
                    badge = existing.ExeName;
                _procGames[pid] = new RunningProc
                {
                    ProcessName = pname,
                    Path = ppath,
                    ExeName = badge,
                    RuleKey = ruleKey
                };
            }
        }

        if (ruleKey != null)
        {
            if (launchSession != null)
            {
                ApplyRuleToSessionProcess(launchSession, pid, pname, ppath, launchQuality);
            }
            else if (!noise)
            {
                var feedback = ApplyEffectiveRule(pid, ruleKey, session, rules);
                ReportGameRuleApplied(ruleKey, null, pname, feedback);
            }
        }

        // El Modo juego también se entera de los procesos con regla que no son un
        // juego "conocido" por nombre (juego real con otro exe).
        if (changed || (ruleKey != null && !isKnown)) RunningGamesChanged?.Invoke();
    }

    /// <summary>
    /// Un proceso murió: se lo quita del rastreo, se actualiza el badge si era un
    /// juego conocido y se revierte el plan de energía. La regla de sesión se
    /// recuerda (no se limpia al cerrar).
    /// </summary>
    private void UnregisterProcess(int pid)
    {
        RunningProc? proc;
        bool lastForRule = false;
        lock (_eventLock)
        {
            _procGames.TryGetValue(pid, out proc);
            _procGames.Remove(pid);
            if (proc?.RuleKey != null)
            {
                lastForRule = !_procGames.Values.Any(v =>
                    string.Equals(v.RuleKey, proc.RuleKey, StringComparison.OrdinalIgnoreCase));
            }
        }
        if (proc == null) return;

        // La sesión de lanzamiento deja de esperar a este pid, pero sigue abierta
        // por si el juego reinicia su proceso dentro de la ventana de 3 minutos.
        ReleaseLaunchSessionPid(pid);

        bool fired = false;
        // Badge: si era un juego conocido y no queda ninguna otra instancia con ese
        // nombre, sale del set de "en ejecución".
        if (proc.ExeName != null)
        {
            bool stillRunning;
            lock (_eventLock)
                stillRunning = _procGames.Values.Any(v =>
                    v.ExeName != null && v.ExeName.Equals(proc.ExeName, StringComparison.OrdinalIgnoreCase));
            if (!stillRunning)
            {
                lock (_runningLock)
                {
                    if (_runningGames.Remove(proc.ExeName))
                    {
                        RunningGamesChanged?.Invoke();
                        fired = true;
                    }
                }
            }
        }

        // La regla de sesión ("Actual") se recuerda: no se limpia al cerrar el juego
        // (solo la borra "Eliminar reglas"). El plan de energía sí se revierte.
        if (proc.RuleKey != null)
        {
            string key = proc.RuleKey;
            RevertPlanIfNoLongerRunning(key);

            if (lastForRule)
            {
                var current = GetGameRuleStatus(key);
                SetGameRuleStatus(new GameRuleStatus(key, GameRuleState.None, current?.Launcher, current?.ProcessName));
            }
        }

        // Sin instancias con regla, el Modo juego puede restaurar (si tampoco hay un
        // juego conocido en ejecución; HasRunningGameActivity lo decide allá).
        if (lastForRule && !fired) RunningGamesChanged?.Invoke();
    }

    // ===== Detección complementaria por fullscreen =====
    // Capa adicional sobre los eventos WMI: cada 2 s consulta si el sistema tiene
    // una app fullscreen en primer plano y, si pertenece a un proceso desconocido
    // (fuera de la biblioteca y de la blacklist), lo suma al set compartido de
    // juegos en ejecución. El watcher SOLO agrega: el cierre lo limpia el evento
    // WMI (UnregisterProcess). Si WMI no está activo, el propio watcher purga los
    // pids muertos (PruneFullscreenWatcher).
    private int _fullscreenTickRunning;

    private static readonly HashSet<string> FullscreenExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers (F11)
        "msedge", "chrome", "firefox", "opera", "brave", "vivaldi", "iexplore",
        // Reproductores y streaming
        "vlc", "mpv", "wmplayer", "potplayer", "potplayermini", "spotify", "kodi",
        // Productividad, comunicación y escritorios remotos
        "winword", "excel", "powerpnt", "outlook", "onenote", "teams", "zoom",
        "discord", "slack", "mstsc", "anydesk", "teamviewer",
        // Launchers en modo vitrina (Big Picture / tiendas)
        "steam", "steamwebhelper", "epicgameslauncher", "galaxyclient", "battle.net",
        "xbox", "xboxstub", "gamingapp",
        // Shell, consolas y apps del sistema
        "explorer", "dwm", "sihost", "taskhostw", "runtimebroker",
        "applicationframehost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "searchhost", "searchapp", "widgets", "widgetservice",
        "photosapp", "mspaint", "systemsettings", "lockapp", "logonui",
        "conhost", "windowsterminal"
    };

    private void FullscreenTick(object? _)
    {
        // El callback puede reentrar si un tick tarda más que el periodo: no superponer.
        if (Interlocked.Exchange(ref _fullscreenTickRunning, 1) != 0) return;
        try
        {
            // 1) Early exit: ¿el OS reporta una app fullscreen? (PRESENTATION_MODE
            // es PowerPoint, no cuenta como juego).
            if (SHQueryUserNotificationState(out var state) != 0 ||
                (state != QueryUserNotificationState.Busy &&
                 state != QueryUserNotificationState.RunningD3DFullScreen))
                return;

            // 2) ¿De quién es la ventana en primer plano?
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return;
            if (pid == Environment.ProcessId) return; // nuestra propia app

            using (var p = Process.GetProcessById((int)pid))
            {
                if (p.HasExited) return;
                string pname = p.ProcessName + ".exe";
                TryGetProcessPath(p, out var ppath);

                // 3) Filtros: blacklist, procesos del sistema, ya conocidos
                // (biblioteca) o ya rastreados (regla o el propio watcher).
                if (FullscreenExclusions.Contains(pname)) return;
                if (ppath != null &&
                    ppath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        StringComparison.OrdinalIgnoreCase))
                    return;
                bool isKnown;
                lock (_pathsLock) isKnown = _knownPaths.ContainsKey(pname);
                if (isKnown) return; // ya lo cubre la biblioteca vía WMI
                lock (_eventLock)
                {
                    if (_procGames.ContainsKey((int)pid)) return;
                    _procGames[(int)pid] = new RunningProc { ProcessName = pname, Path = ppath, ExeName = pname };
                }

                // 4) Sumar al set compartido (badge de biblioteca, reglas y boost).
                bool changed;
                lock (_runningLock) changed = _runningGames.Add(pname);
                if (changed)
                {
                    _logging.LogDebug($"ProcessService: fullscreen en primer plano detectado como juego: {pname} (pid {pid}, fuera de la biblioteca)");
                    RunningGamesChanged?.Invoke();
                }
            }
        }
        catch
        {
            // Proceso protegido o que murió en el camino: se ignora; el próximo tick reintenta.
        }
        finally
        {
            // Sin WMI no hay eventos de cierre: purgar acá (con WMI lo hace UnregisterProcess).
            if (!WmiEventsActive) PruneFullscreenWatcher();
            Interlocked.Exchange(ref _fullscreenTickRunning, 0);
        }
    }

    /// <summary>
    /// Solo corre sin WMI: purga los pids que agregó el watcher y que ya no existen
    /// (con verificación de nombre para no tropezar con reutilización de pids).
    /// </summary>
    private void PruneFullscreenWatcher()
    {
        List<int>? dead = null;
        lock (_eventLock)
        {
            foreach (var (pid, proc) in _procGames)
            {
                if (proc.RuleKey != null) continue; // rastreado por reglas: lo limpia la sesión
                bool alive = false;
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    alive = !p.HasExited &&
                            string.Equals(p.ProcessName + ".exe", proc.ProcessName, StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                if (!alive) (dead ??= new List<int>()).Add(pid);
            }
        }
        if (dead == null) return;
        foreach (var pid in dead)
            UnregisterProcess(pid);
    }

    /// <summary>Aplica la regla efectiva (sesión gana sobre guardada) a un proceso recién visto.</summary>
    private RuleApplyFeedback ApplyEffectiveRule(int pid, string ruleKey, Dictionary<string, ProcessRule> session, Dictionary<string, ProcessRule> rules)
    {
        var failed = new RuleApplyFeedback(false, false, false);
        try
        {
            session.TryGetValue(ruleKey, out var sr);
            rules.TryGetValue(ruleKey, out var pr);
            var effective = Merge(sr, pr);
            if (RuleIsEmpty(effective)) return failed;

            bool cpuFailed = false, affinityFailed = false, gpuFailed = false, ioFailed = false;
            if (effective!.CpuPriority is int cpu) cpuFailed = !ApplyCpuPriority(pid, cpu);
            if (effective.AffinityMask is long mask && mask > 0) affinityFailed = !ApplyAffinity(pid, mask);
            if (effective.GpuPriority is int gpu) gpuFailed = !ApplyGpuPriority(pid, gpu);
            if (effective.IoPriority is int io) ioFailed = !ApplyIoPriority(pid, io);

            if (!string.IsNullOrEmpty(effective.PowerPlanGuid))
                ApplyPlanFor(ruleKey, effective.PowerPlanGuid!);

            failed = new RuleApplyFeedback(cpuFailed, affinityFailed, gpuFailed, ioFailed);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: aplicar regla {ruleKey} a {pid}: {ex.Message}");
        }
        return failed;
    }

    /// <summary>Activa el plan del primer juego con regla que se detecta; el resto espera
    /// hasta que el juego activo cierre el suyo.</summary>
    private void ApplyPlanFor(string ruleKey, string planGuid)
    {
        lock (_planLock)
        {
            if (_appliedPlanExe != null) return; // otro juego ya tiene el plan activo
            _defaultPlanGuid ??= _powerPlan.GetActivePowerPlanGuid();
            _ = _powerPlan.SetActivePowerPlanAsync(planGuid);
            _appliedPlanExe = ruleKey;
        }
    }

    /// <summary>Si el plan activo era de este juego y ya no queda ninguna instancia, revierte al plan por defecto.</summary>
    private void RevertPlanIfNoLongerRunning(string ruleKey)
    {
        lock (_planLock)
        {
            if (_appliedPlanExe != ruleKey) return;
            lock (_eventLock)
            {
                if (_procGames.Values.Any(v => v.RuleKey == ruleKey)) return; // sigue corriendo
            }
            _defaultPlanGuid ??= _powerPlan.GetActivePowerPlanGuid();
            _ = _powerPlan.SetActivePowerPlanAsync(_defaultPlanGuid);
            _appliedPlanExe = null;
        }
    }

    // ===== Favoritos =====

    public List<string> GetFavorites()
    {
        var favs = _settings.Get("process.favorites", new List<string>()) ?? new();
        // Migrar en la lectura los favoritos guardados con el nombre del stub
        // (detección vieja) al exe real del juego: SMITE 2 se guardó como
        // start_protected_game.exe (EAC) y CS2 como vconsole2.exe; ahora son
        // Hemingway.exe y cs2.exe. No se toca el settings: se resuelve en memoria.
        // Los stubs/launchers (ej. BlacksmithBootstrap.exe) no pueden ser favoritos:
        // si quedó uno guardado de antes, no debe aparecer en la bandeja.
        // Un favorito con exe vacío/blanco (ej. "" que quedó de marcar CS2 cuando el
        // escáner no había resuelto su exe) tampoco: era un ítem sin nombre ni ícono
        // en el menú de la bandeja. Se descarta en memoria acá y, si había alguno,
        // la lista limpia se persiste de una vez para que no vuelva a pasar.
        var cleaned = favs
            .Select(ResolveExeAlias)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Where(f => !GameExeResolver.IsStubExeName(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cleaned.Count != favs.Count)
        {
            _settings.Set("process.favorites", cleaned);
            try { _settings.Save(); }
            catch (Exception ex) { _logging.LogWarning($"Procesos: limpiar favoritos inválidos: {ex.Message}"); }
        }
        return cleaned;
    }

    public void ToggleFavorite(string exe)
    {
        // Nunca guardar favoritos sin exe: al margen de la raíz del escáner, un
        // favorito vacío aparece en la bandeja sin nombre ni ícono (el bug de CS2).
        if (string.IsNullOrWhiteSpace(exe)) return;
        var favs = GetFavorites();
        if (favs.Contains(exe, StringComparer.OrdinalIgnoreCase))
            favs.RemoveAll(f => string.Equals(f, exe, StringComparison.OrdinalIgnoreCase));
        else
            favs.Add(exe);
        _settings.Set("process.favorites", favs);
        _settings.Save();
    }

    public bool IsFavorite(string exe)
    {
        var favs = GetFavorites();
        return favs.Any(f => string.Equals(f, exe, StringComparison.OrdinalIgnoreCase))
            // El exe puede llegar con el nombre del stub (caché vieja): el favorito
            // guardado ya se resolvió al exe real, así que comparar contra el alias.
            || favs.Any(f => string.Equals(f, ResolveExeAlias(exe), StringComparison.OrdinalIgnoreCase));
    }

    // ===== Lista manual =====

    /// <summary>
    /// Formato de entrada: &quot;exe&quot; | &quot;Nombre|exe&quot; (anterior) | &quot;Nombre|exe|ruta&quot;.
    /// El formato de 3 partes incluye la carpeta de instalación (para extraer el ícono
    /// del exe); el de 2 partes se mantiene por compatibilidad con entradas viejas.
    /// </summary>
    private static (string? Name, string Exe, string? Path) ParseEntry(string raw)
    {
        var parts = raw.Split('|');
        if (parts.Length == 3)
            return (string.IsNullOrEmpty(parts[0]) ? null : parts[0], parts[1], string.IsNullOrEmpty(parts[2]) ? null : parts[2]);
        if (parts.Length == 2)
            return (parts[0], parts[1], null); // formato anterior "Nombre|exe"
        return (null, raw, null);
    }

    public List<string> GetManualExes()
        => GetManualEntries().Select(e => e.Exe).ToList();

    public List<(string Exe, string? Name, string? InstallPath)> GetManualEntries()
    {
        var result = new List<(string, string?, string?)>();
        foreach (var raw in _settings.Get("process.manual", new List<string>()) ?? new())
        {
            var (name, exe, path) = ParseEntry(raw);
            // Entradas viejas de launchers/instaladores agregadas a mano (ej.
            // BlacksmithBootstrap.exe): un launcher no es un juego y no debe
            // listarse ni en la biblioteca ni en la bandeja.
            if (!string.IsNullOrEmpty(exe) && GameExeResolver.IsStubExeName(exe)) continue;
            result.Add((exe, name, path));
        }
        return result;
    }

    public void AddManualExe(string exe, string? displayName = null, string? installPath = null)
    {
        var list = _settings.Get("process.manual", new List<string>()) ?? new();
        string entry;
        if (!string.IsNullOrWhiteSpace(installPath))
            entry = $"{displayName ?? ""}|{exe}|{installPath}";
        else if (!string.IsNullOrWhiteSpace(displayName))
            entry = $"{displayName}|{exe}";
        else
            entry = exe;
        bool exists = false;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(ParseEntry(list[i]).Exe, exe, StringComparison.OrdinalIgnoreCase))
            {
                // Ya existe: actualizo nombre/ruta si vienen (sin duplicar).
                list[i] = entry;
                exists = true;
                break;
            }
        }
        if (!exists)
            list.Add(entry);
        _settings.Set("process.manual", list);

        // Si el exe estaba oculto o eliminado (por ejemplo, se había sacado de la
        // biblioteca antes), re-agregarlo a mano lo restaura: si el usuario lo quiere
        // de vuelta, el ocultamiento/eliminación previos no deben hacer que la card
        // no aparezca aunque el mensaje diga "se agregó".
        var hidden = GetHiddenExes();
        if (hidden.RemoveAll(h => string.Equals(h, exe, StringComparison.OrdinalIgnoreCase)) > 0)
            _settings.Set("process.hidden", hidden);
        var deleted = GetDeletedGames();
        if (deleted.RemoveAll(d => string.Equals(d, exe, StringComparison.OrdinalIgnoreCase)) > 0)
            _settings.Set("process.deleted", deleted);

        _settings.Save();
    }

    public void RemoveManualExe(string exe)
    {
        var list = _settings.Get("process.manual", new List<string>()) ?? new();
        if (list.RemoveAll(f => string.Equals(ParseEntry(f).Exe, exe, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _settings.Set("process.manual", list);
            _settings.Save();
        }

        // También eliminar el launcher configurado si existe
        var launchers = GetManualGameLaunchers();
        if (launchers.Remove(exe))
        {
            _settings.Set("process.manual.launchers", launchers);
            _settings.Save();
        }
    }

    /// <summary>Borra TODOS los juegos manuales (Re-detectar los elimina).</summary>
    public void ClearManualExes()
    {
        _settings.Remove("process.manual");
        _settings.Remove("process.manual.launchers");
        _settings.Save();
    }

    /// <summary>Configura el launcher de un juego manual (ej: "Blacksmith" para Dark and Darker).</summary>
    public void SetManualGameLauncher(string exe, string launcher)
    {
        var launchers = GetManualGameLaunchers();
        launchers[exe] = launcher;
        _settings.Set("process.manual.launchers", launchers);
        _settings.Save();
    }

    /// <summary>Obtiene el launcher configurado para un juego manual (null si no tiene).</summary>
    public string? GetManualGameLauncher(string exe)
    {
        var launchers = GetManualGameLaunchers();
        return launchers.TryGetValue(exe, out var launcher) ? launcher : null;
    }

    /// <summary>Obtiene todos los juegos manuales con su launcher configurado.</summary>
    public Dictionary<string, string> GetManualGameLaunchers()
    {
        return _settings.Get<Dictionary<string, string>>("process.manual.launchers", null)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    // ===== Ocultos (juegos eliminados de la biblioteca) =====

    public List<string> GetHiddenExes()
        => _settings.Get("process.hidden", new List<string>()) ?? new();

    public void HideExe(string exe)
    {
        var list = GetHiddenExes();
        if (!list.Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(exe);
            _settings.Set("process.hidden", list);
            _settings.Save();
        }
    }

    public void UnhideExe(string exe)
    {
        var hidden = GetHiddenExes();
        if (hidden.RemoveAll(h => string.Equals(h, exe, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _settings.Set("process.hidden", hidden);
            _settings.Save();
        }
    }

    public void ClearHiddenExes()
    {
        if (GetHiddenExes().Count == 0) return;
        _settings.Set("process.hidden", new List<string>());
        _settings.Save();
    }

    // ===== Eliminados de la biblioteca (vuelven con Re-detectar) =====

    public List<string> GetDeletedGames()
        => _settings.Get("process.deleted", new List<string>()) ?? new();

    public void DeleteGame(string exe)
    {
        var list = GetDeletedGames();
        if (!list.Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(exe);
            _settings.Set("process.deleted", list);
            _settings.Save();
        }
    }

    public void ClearDeletedGames()
    {
        if (GetDeletedGames().Count == 0) return;
        _settings.Set("process.deleted", new List<string>());
        _settings.Save();
    }
}
