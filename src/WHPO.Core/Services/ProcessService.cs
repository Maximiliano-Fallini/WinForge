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
    /// Gestión de procesos estilo Process Lasso: enumera las aplicaciones/juegos con
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

    // Reglas de sesión ("Actual"): solo la apertura actual del juego. En memoria,
    // nunca se persisten ni escriben en el registro. Campos null = sin override de
    // sesión para esa dimensión; la sesión gana sobre la regla guardada campo por
    // campo. Se limpian solas cuando el juego cierra (ver UnregisterProcess).
    private readonly object _sessionLock = new();
    private readonly Dictionary<string, ProcessRule> _sessionRules = new(StringComparer.OrdinalIgnoreCase);

    // Snapshot de juegos conocidos en ejecución: lo alimentan los eventos WMI
    // (Win32_ProcessStartTrace/StopTrace) para que la biblioteca muestre el estado
    // en vivo sin polling. Se publica con un evento solo cuando cambia.
    private readonly object _runningLock = new();
    private HashSet<string> _runningGames = new(StringComparer.OrdinalIgnoreCase);

    public event Action? RunningGamesChanged;

    public IReadOnlyCollection<string> RunningGameExes
    {
        get { lock (_runningLock) return new List<string>(_runningGames); }
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
        "Battle.net", "EpicGamesLauncher", "GalaxyClient", "Steam", "Xbox", "XboxStub", "GamingApp"
    };
    public event Action? LauncherStateChanged;
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

    public int ProcessorCount { get; } = Math.Max(1, Environment.ProcessorCount);

    public ProcessService(ILoggingService logging, ISettingsService settings, ICpuPowerService powerPlan, IInstalledGamesService games)
    {
        _logging = logging;
        _settings = settings;
        _powerPlan = powerPlan;
        _games = games;

        // Reglas de sesión ("Actual") persistidas en un caché (settings): sobreviven
        // al reinicio de la app. Se limpian solas al cerrar la última instancia del
        // juego (UnregisterProcess → ClearSessionRule), igual que en memoria.
        try
        {
            var persistedSession = _settings.Get("process.sessionRules", new Dictionary<string, ProcessRule>());
            if (persistedSession != null)
                foreach (var kv in persistedSession)
                    if (!string.IsNullOrEmpty(kv.Key)) _sessionRules[kv.Key] = kv.Value;
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
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"ProcessService: rutas de juegos: {ex.Message}");
            }
        });
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

    /// <summary>Un proceso matchea la regla si su nombre coincide, o si su ruta está dentro de la carpeta de instalación del juego.</summary>
    private bool MatchesRuleKey(string processName, string? processPath, string ruleExe)
    {
        if (string.Equals(processName, ruleExe, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrEmpty(processPath)) return false;
        lock (_pathsLock)
        {
            return _knownPaths.TryGetValue(ruleExe, out var dir)
                && !string.IsNullOrEmpty(dir)
                && processPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
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
        // esté bloqueado. Es la técnica documentada de Process Lasso para EAC.
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
        try
        {
            IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
            if (snap == IntPtr.Zero) return -1;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32First(snap, ref entry)) return -1;
                do
                {
                    if (entry.th32ProcessID == (uint)pid)
                        return (int)entry.th32ParentProcessID;
                } while (Process32Next(snap, ref entry));
            }
            finally { CloseHandle(snap); }
        }
        catch { }
        return -1;
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
    // (0=VeryLow, 1=Low, 2=Normal, 3=High, 4=Critical), igual que Process Lasso.
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
    /// sesión "Actual" gana sobre la guardada) mientras arranca: cada 5 s y por
    /// máximo 25 s, incluso con la ventana oculta. Es la red de seguridad de los
    /// eventos WMI para la cadena launcher→juego (el proceso real suele nacer con
    /// otro nombre o estar protegido); cada proceso recibe la regla una sola vez.
    /// Vive en el servicio (no en la página) para no depender de que la ventana
    /// siga abierta ni de la navegación.
    /// </summary>
    public void ApplyLaunchChainRule(string exeFileName)
        => _ = Task.Run(() => ApplyLaunchChainRuleCore(exeFileName));

    private async Task ApplyLaunchChainRuleCore(string exeFileName)
    {
        try
        {
            var rule = GetEffectiveRule(exeFileName);
            if (RuleIsEmpty(rule)) return;
            var applied = new HashSet<int>();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var app in FindRunningProcessesForRule(exeFileName))
                {
                    if (!applied.Add(app.Id)) continue;
                    ApplyRule(app, rule!);
                    if (!string.IsNullOrEmpty(rule!.PowerPlanGuid))
                        ApplyPlanFor(exeFileName, rule.PowerPlanGuid);
                }
                await Task.Delay(5000);
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: regla de lanzamiento {exeFileName}: {ex.Message}");
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

    private bool HasSessionRules()
    {
        lock (_sessionLock) return _sessionRules.Count > 0;
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
            _ = Task.Run(() => { try { SyncBirthPriority(exe, rule); } catch { } });
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
                try { SyncBirthPriority(exe, rule); } catch { }
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
        if (WmiEventsActive)
        {
            lock (_launcherLock)
                return _runningLaunchers.Contains(procName);
        }
        try { return Process.GetProcessesByName(procName).Length > 0; }
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
        catch { }
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
    // biblioteca, la aplicación de reglas (persistentes y de sesión), la limpieza
    // de la sesión al cerrar el juego y la reversión del plan de energía.

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
    /// (badge), rastrea los procesos que matchean una regla (guardada o de sesión),
    /// aplica sus reglas y activa el plan de energía si corresponde. Después de esto
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
            var session = GetSessionRulesSnapshot();
            var keys = EffectiveKeys(rules, session);

            var runningNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.HasExited) continue;
                    string pname = p.ProcessName + ".exe";
                    TryGetProcessPath(p, out var ppath);

                    if (LauncherNames.Contains(pname))
                        lock (_launcherLock) _runningLaunchers.Add(pname);

                    bool isKnown;
                    lock (_pathsLock) isKnown = _knownPaths.ContainsKey(pname);
                    if (isKnown) runningNow.Add(pname);

                    string? ruleKey = null;
                    foreach (var key in keys)
                    {
                        if (MatchesRuleKey(pname, ppath, key)) { ruleKey = key; break; }
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
                            ApplyEffectiveRule(p.Id, ruleKey, session, rules);
                    }
                }
                catch { }
            }

            lock (_runningLock)
            {
                if (!_runningGames.SetEquals(runningNow))
                {
                    _runningGames = runningNow;
                    RunningGamesChanged?.Invoke();
                }
            }
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
        if (LauncherNames.Contains(pname))
        {
            bool launcherChanged;
            lock (_launcherLock) launcherChanged = _runningLaunchers.Remove(pname);
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
        if (LauncherNames.Contains(pname))
        {
            bool launcherChanged;
            lock (_launcherLock) launcherChanged = _runningLaunchers.Add(pname);
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

        // Sin reglas configuradas, el único trabajo es el badge (ya hecho): salir
        // sin copiar el snapshot de sesión (los eventos corren por cada proceso).
        var rules = GetRulesCached();
        if (!isKnown && rules.Count == 0 && !HasSessionRules()) return;
        var session = GetSessionRulesSnapshot();

        string? ruleKey = null;
        foreach (var key in EffectiveKeys(rules, session))
        {
            if (MatchesRuleKey(pname, ppath, key)) { ruleKey = key; break; }
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
            ApplyEffectiveRule(pid, ruleKey, session, rules);

        if (changed) RunningGamesChanged?.Invoke();
    }

    /// <summary>
    /// Un proceso murió: se lo quita del rastreo, se actualiza el badge si era un
    /// juego conocido, y si era la última instancia de un exe con regla de sesión
    /// ("Actual") se limpia la sesión y se revierte el plan de energía.
    /// </summary>
    private void UnregisterProcess(int pid)
    {
        RunningProc? proc;
        lock (_eventLock)
        {
            _procGames.TryGetValue(pid, out proc);
            _procGames.Remove(pid);
        }
        if (proc == null) return;

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
                        RunningGamesChanged?.Invoke();
                }
            }
        }

        // Regla de sesión ("Actual"): si era la última instancia del juego, la
        // sesión terminó → se limpia sola (en memoria, nunca persiste).
        if (proc.RuleKey != null)
        {
            string key = proc.RuleKey;
            lock (_sessionLock)
            {
                if (_sessionRules.ContainsKey(key))
                {
                    bool stillRunning;
                    lock (_eventLock)
                        stillRunning = _procGames.Values.Any(v => v.RuleKey == key);
                    if (!stillRunning)
                    {
                        // ClearSessionRule (y no remover directo): persiste la
                        // limpieza en el caché de sesión.
                        ClearSessionRule(key);
                    }
                }
            }
            RevertPlanIfNoLongerRunning(key);
        }
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
            //    es PowerPoint, no cuenta como juego).
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
                //    (biblioteca) o ya rastreados (regla o el propio watcher).
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

    public bool TryApplyGlobalPowerPlan(string planGuid)
    {
        lock (_planLock)
        {
            if (_appliedPlanExe != null) return false; // un juego ya activó el suyo: gana el plan por juego
            _defaultPlanGuid ??= _powerPlan.GetActivePowerPlanGuid();
            _ = _powerPlan.SetActivePowerPlanAsync(planGuid);
            _appliedPlanExe = GlobalPlanRuleKey;
            return true;
        }
    }

    public void RevertGlobalPowerPlan()
    {
        lock (_planLock)
        {
            // Solo revierte si el plan activo es el global. Si un juego con plan propio
            // lo reemplazó, ese juego revierte el suyo al cerrar (RevertPlanIfNoLongerRunning).
            if (_appliedPlanExe != GlobalPlanRuleKey) return;
            _defaultPlanGuid ??= _powerPlan.GetActivePowerPlanGuid();
            _ = _powerPlan.SetActivePowerPlanAsync(_defaultPlanGuid);
            _appliedPlanExe = null;
        }
    }

    /// <summary>Aplica la regla efectiva (sesión gana sobre guardada) a un proceso recién visto.</summary>
    private void ApplyEffectiveRule(int pid, string ruleKey, Dictionary<string, ProcessRule> session, Dictionary<string, ProcessRule> rules)
    {
        try
        {
            session.TryGetValue(ruleKey, out var sr);
            rules.TryGetValue(ruleKey, out var pr);
            var effective = Merge(sr, pr);
            if (RuleIsEmpty(effective)) return;

            if (effective!.CpuPriority is int cpu) ApplyCpuPriority(pid, cpu);
            if (effective.AffinityMask is long mask && mask > 0) ApplyAffinity(pid, mask);
            if (effective.GpuPriority is int gpu) ApplyGpuPriority(pid, gpu);
            if (effective.IoPriority is int io) ApplyIoPriority(pid, io);

            if (!string.IsNullOrEmpty(effective.PowerPlanGuid))
                ApplyPlanFor(ruleKey, effective.PowerPlanGuid!);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"ProcessService: aplicar regla {ruleKey} a {pid}: {ex.Message}");
        }
    }

    /// <summary>Clave reservada para el plan global del GameBoost (nunca colisiona con un exe).</summary>
    private const string GlobalPlanRuleKey = "__gameboost_global__";

    /// <summary>Activa el plan del primer juego con regla que se detecta; el resto espera.
    /// Excepción: si el plan activo es el GLOBAL del GameBoost, un juego con plan propio
    /// lo reemplaza (el plan por juego tiene prioridad sobre el global).</summary>
    private void ApplyPlanFor(string ruleKey, string planGuid)
    {
        lock (_planLock)
        {
            if (_appliedPlanExe != null && _appliedPlanExe != GlobalPlanRuleKey) return; // otro juego ya tiene el plan activo
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
        return favs
            .Select(ResolveExeAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void ToggleFavorite(string exe)
    {
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

        // Si el exe estaba oculto (por ejemplo, se había "eliminado de la biblioteca"
        // antes, que lo oculta), re-agregarlo a mano lo desoculta: si el usuario lo
        // quiere de vuelta, el ocultamiento previo no debe hacer que la card no
        // aparezca aunque el mensaje diga "se agregó".
        var hidden = GetHiddenExes();
        if (hidden.RemoveAll(h => string.Equals(h, exe, StringComparison.OrdinalIgnoreCase)) > 0)
            _settings.Set("process.hidden", hidden);

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
}
