using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services.Overlay;

/// <summary>
/// Muestrea en vivo las métricas que muestra el overlay de juegos: uso/temp/MHz/watts
/// de CPU y GPU, RAM, y FPS del juego en primer plano (via ETW). Todo el trabajo
/// corre en un timer de fondo; el snapshot se publica de forma thread-safe.
///
/// Fuentes (todas livianas y con caché interna):
///  - CPU/GPU %: Performance Counter (la misma fuente que el Administrador de tareas).
///  - Temp/MHz/watts: LibreHardwareMonitor vía ISystemInfoService (con caché).
///  - RAM: IMemoryService (GlobalMemoryStatusEx nativo).
///  - FPS: IFpsMonitor (ETW DxgKrnl) sobre el proceso de la ventana en primer plano.
/// </summary>
public sealed class OverlayMetricsService : IOverlayMetricsService, IDisposable
{
    private readonly ILoggingService _logging;
    private readonly ISystemInfoService _systemInfo;
    private readonly IMemoryService _memory;
    private readonly IFpsMonitor _fpsMonitor;
    private readonly IInstalledGamesService _installedGames;
    private readonly object _lock = new();

    private Timer? _timer;
    private volatile bool _running;
    private OverlayMetrics? _latest;

    private PerformanceCounter? _cpuCounter;
    private readonly List<PerformanceCounter> _gpuCounters = new();

    // Nombres de hardware y configuración de RAM: estáticos, se resuelven una vez
    // (con caché) la primera vez que se muestrea. Si algo falla al arrancar (WMI
    // ocupado) se reintenta cada 10 s en lugar de quedar vacío para siempre.
    private bool _hardwareResolved;
    private long _lastHardwareAttempt;
    private string _cpuName = "";
    private string _gpuName = "";
    private string _ramConfig = "";
    private double _ramMhz;

    // Anti-parpadeo de FPS: se recuerda el último juego con FPS (pid + nombre).
    // El proceso se mantiene mientras siga presentando frames; el contador solo cae
    // a "--" cuando ningún proceso presenta.
    //
    // Selección del juego (en orden):
    //  1) El PRIMER PLANO presenta a ritmo de juego (>= 30 fps) → es el juego activo.
    //  2) Si no, el último juego conocido SI SIGUE presentando → se mantiene (evita
    //     que un emulador/browser a 8 fps o el escritorio pisen al juego real).
    //  3) Si no, el proceso con MAYOR tasa de presentación (juego en background).
    private const double GameLikeFpsThreshold = 30;
    private int _lastGamePid;
    private string _lastGameName = "";
    private readonly int _selfPid = Environment.ProcessId;

    // Diagnóstico: valor de FPS del último snapshot para loguear las transiciones
    // 0 ↔ valor (si el contador vuelve a parpadear, el log muestra la causa exacta).
    private double _lastShownFps;

    // Nombres reales de juegos instalados (biblioteca, SIN hardcodear): se cargan una
    // vez en background desde IInstalledGamesService y se mapean por ejecutable.
    private Dictionary<string, string>? _gameNameByExe;
    private bool _libraryLoadStarted;

    // API gráfica del juego actual (DX11/DX12/Unity/Vulkan/DX9), detectada por los
    // módulos cargados en el proceso; cacheada por pid (enumerar módulos es costoso).
    private int _gfxApiPid;
    private string _gfxApi = "";

    private static readonly string[] ExeSuffixes =
    {
        "-win64-shipping", "-win64", "-shipping", "-client", "-launcher",
        "_client", "_shipping", "_win64", "_64"
    };

    public OverlayMetricsService(
        ILoggingService logging,
        ISystemInfoService systemInfo,
        IMemoryService memory,
        IFpsMonitor fpsMonitor,
        IInstalledGamesService installedGames)
    {
        _logging = logging;
        _systemInfo = systemInfo;
        _memory = memory;
        _fpsMonitor = fpsMonitor;
        _installedGames = installedGames;
    }

    public bool IsRunning => _running;

    public OverlayMetrics? Latest => _latest;

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            EnsureCounters();
            _fpsMonitor.Start();
            _running = true;
            _timer = new Timer(_ => Sample(), null, 0, 500);
            _logging.LogInfo("OverlayMetricsService: muestreo iniciado (500 ms)");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            _timer?.Dispose();
            _timer = null;
            _fpsMonitor.Stop();
            _latest = null;
            _logging.LogInfo("OverlayMetricsService: muestreo detenido");
        }
    }

    private void EnsureCounters()
    {
        try
        {
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuCounter.NextValue();
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverlayMetrics: contador CPU no disponible: {ex.Message}");
            _cpuCounter = null;
        }

        // El uso total de GPU es la suma de las instancias 3D ("GPU Engine", la misma
        // fuente que el Administrador de tareas). La instancia "_Total" no existe en
        // todos los drivers, así que se enumeran las instancias engtype_3d.
        if (_gpuCounters.Count == 0)
        {
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                var instances = category.GetInstanceNames();
                var engineInstances = instances.Where(i => i.Contains("engtype_3d")).ToArray();
                if (engineInstances.Length == 0)
                    engineInstances = instances;
                foreach (var instance in engineInstances)
                {
                    try
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        counter.NextValue();
                        _gpuCounters.Add(counter);
                    }
                    catch { }
                }
                if (_gpuCounters.Count == 0)
                    _logging.LogWarning("OverlayMetrics: sin instancias de GPU Engine disponibles");
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"OverlayMetrics: contador GPU no disponible: {ex.Message}");
            }
        }
    }

    private void Sample()
    {
        try
        {
            double cpuUsage = _cpuCounter?.NextValue() ?? 0;
            double gpuUsage = 0;
            foreach (var counter in _gpuCounters)
            {
                try { gpuUsage += counter.NextValue(); }
                catch { }
            }
            gpuUsage = Math.Min(gpuUsage, 100);

            var cpuTemp = _systemInfo.GetCpuTemperatureFresh();
            var cpuMhz = _systemInfo.GetCpuFrequency();
            var cpuWatts = _systemInfo.GetCpuPower();

            var gpuTemp = _systemInfo.GetGpuTemperature();
            var gpuMhz = _systemInfo.GetGpuClockMHz();
            var gpuWatts = _systemInfo.GetGpuPower();

            EnsureHardware();

            double ramPercent = 0;
            double ramUsedMb = 0;
            try
            {
                var mem = _memory.GetMemoryStats();
                ramPercent = mem.UsedPercent;
                ramUsedMb = mem.UsedMB;
            }
            catch { }

            // FPS: selección robusta del juego activo (ver comentario del campo
            // _lastGamePid). Verificado con probes ETW: un juego presenta frames de
            // forma continua aunque no esté en primer plano; el problema era que un
            // emulador/browser en primer plano presentaba a baja tasa e "pisaba" al
            // juego real, dejando el contador en "--" cuando pausaba.
            EnsureGameLibraryLoaded();
            int gamePid = 0;
            string gameName = "";
            double fps = 0, low1 = 0, low01 = 0;
            if (_fpsMonitor.IsRunning)
            {
                int fgPid = GetForegroundProcessId();
                double fgFps = fgPid > 0 && fgPid != _selfPid ? _fpsMonitor.GetFps(fgPid) : 0;

                if (fgFps >= GameLikeFpsThreshold)
                {
                    // 1) El primer plano presenta a ritmo de juego: es el juego activo.
                    _lastGamePid = fgPid;
                    _lastGameName = GetGameDisplayName(fgPid);
                    gamePid = fgPid;
                    fps = fgFps;
                    low1 = _fpsMonitor.GetLow1(fgPid);
                    low01 = _fpsMonitor.GetLow01(fgPid);
                }
                else if (_lastGamePid > 0)
                {
                    // 2) El último juego conocido aún presenta: mantenerlo (evita que
                    // el emulador a 8 fps, el escritorio o la propia app pisen al juego).
                    double gFps = _fpsMonitor.GetFps(_lastGamePid);
                    if (gFps > 0)
                    {
                        gamePid = _lastGamePid;
                        fps = gFps;
                        low1 = _fpsMonitor.GetLow1(_lastGamePid);
                        low01 = _fpsMonitor.GetLow01(_lastGamePid);
                    }
                }

                if (gamePid == 0)
                {
                    // 3) Sin juego activo: el proceso con mayor tasa de presentación
                    // (cubre el juego corriendo en background, p. ej. otro monitor).
                    // Piso de 5 fps para no mostrar ruido de procesos idle (~2 fps).
                    var best = _fpsMonitor.GetMostActiveProcess(_selfPid);
                    if (best.Pid > 0 && best.Fps >= 5)
                    {
                        _lastGamePid = best.Pid;
                        _lastGameName = GetGameDisplayName(best.Pid);
                        gamePid = best.Pid;
                        fps = best.Fps;
                        low1 = _fpsMonitor.GetLow1(best.Pid);
                        low01 = _fpsMonitor.GetLow01(best.Pid);
                    }
                }

                // Log SOLO de transiciones 0 ↔ valor (si el contador vuelve a
                // parpadear, el log dice exactamente qué pasó).
                bool nowZero = fps <= 0;
                bool wasZero = _lastShownFps <= 0;
                if (nowZero != wasZero)
                {
                    var best = _fpsMonitor.GetMostActiveProcess(_selfPid);
                    _logging.LogInfo($"OverlayMetrics FPS: {_lastShownFps:F0} -> {fps:F0} | fg={fgPid} game={gamePid} last={_lastGamePid} best={best.Pid}@{best.Fps:F0}");
                }
                _lastShownFps = fps;

                gameName = _lastGameName;
                _fpsMonitor.Prune();
            }

            _latest = new OverlayMetrics(
                CpuUsagePercent: Math.Max(0, cpuUsage),
                CpuTempCelsius: cpuTemp,
                CpuMhz: cpuMhz,
                CpuWatts: cpuWatts,
                GpuUsagePercent: Math.Max(0, gpuUsage),
                GpuTempCelsius: gpuTemp,
                GpuMhz: gpuMhz,
                GpuWatts: gpuWatts,
                RamPercent: ramPercent,
                RamUsedMb: ramUsedMb,
                RamConfig: _ramConfig,
                RamMhz: _ramMhz,
                CpuName: _cpuName,
                GpuName: _gpuName,
                Fps: fps,
                FpsLow1: low1,
                FpsLow01: low01,
                GamePid: gamePid,
                GameName: gameName,
                GfxApi: gamePid > 0 ? GetGfxApi(gamePid) : "",
                FpsMonitorActive: _fpsMonitor.IsRunning);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverlayMetrics: error muestreando: {ex.Message}");
        }
    }

    // ===== Hardware (nombres + configuración RAM, con caché) =====

    private void EnsureHardware()
    {
        if (_hardwareResolved) return;
        long now = Environment.TickCount64;
        if (now - _lastHardwareAttempt < 10_000) return;
        _lastHardwareAttempt = now;

        bool ok = true;
        try
        {
            _cpuName = ShortenName(_systemInfo.GetCpuInfo().Name);
            if (string.IsNullOrWhiteSpace(_cpuName)) ok = false;
        }
        catch { ok = false; }
        try
        {
            var gpus = _systemInfo.GetGpuInfo();
            // La GPU principal = la de más VRAM dedicada. La integrada suele reportar
            // VRAM falsa (~512 MB por el AdapterRAM de WMI) y no debe ganarle a la
            // dedicada (RTX 4060 Ti 8 GB) ni quedarse como "la primera".
            var primary = gpus.Where(g => g.DedicatedMemoryBytes > 0)
                              .OrderByDescending(g => g.DedicatedMemoryBytes)
                              .FirstOrDefault();
            _gpuName = primary != null ? ShortenName(primary.Name) : "";
            if (string.IsNullOrWhiteSpace(_gpuName)) ok = false;
        }
        catch { ok = false; }
        try
        {
            var modules = _systemInfo.GetMemoryModuleInfo();
            if (modules.ModuleCount > 0 && modules.ModuleSizeBytes > 0)
            {
                double gb = modules.ModuleSizeBytes / (1024.0 * 1024 * 1024);
                _ramConfig = $"{modules.ModuleCount}x{gb:F0} GB";
            }
            _ramMhz = modules.SpeedMHz;
            if (string.IsNullOrWhiteSpace(_ramConfig)) ok = false;
        }
        catch { ok = false; }

        _hardwareResolved = ok;
    }

    /// <summary>
    /// Acorta el nombre de la CPU/GPU para que entre en la columna del overlay:
    /// quita marcas/descriptores comunes y lo limita a 16 caracteres.
    /// </summary>
    private static string ShortenName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name;
        foreach (var token in new[]
        {
            "(R)", "(TM)", "(C)", "AMD ", "Intel(R) ", "Intel ", "Core(TM) ", "NVIDIA ", "GeForce ",
            "Radeon(TM) ", "Radeon (TM) ", "Radeon ", "ATI ", "Series", "Graphics", "Video Card",
            "Display Adapter", "Family", "Processor", "CPU", "APU", "Dual-Core ", "Quad-Core ",
            "Hexa-Core ", "Octa-Core ", "Dodeca-Core ", "6-Core ", "8-Core ", "10-Core ",
            "12-Core ", "14-Core ", "16-Core ", "20-Core ", "24-Core ", "32-Core ", "64-Core "
        })
        {
            s = s.Replace(token, "", StringComparison.OrdinalIgnoreCase);
        }
        s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
        return s.Length <= 16 ? s : s.Substring(0, 16).TrimEnd() + "…";
    }

    // ===== Ventana en primer plano =====

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static int GetForegroundProcessId()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }
        catch { return 0; }
    }

    // ===== Nombre del juego (fuentes reales, sin tabla hardcodeada) =====

    /// <summary>
    /// Nombre real del juego del proceso, en cascada:
    ///  1) Biblioteca de juegos instalados (IInstalledGamesService) por ejecutable.
    ///  2) Título real de la ventana del proceso.
    ///  3) Nombre de producto del ejecutable (FileVersionInfo).
    ///  4) Nombre del proceso limpiado genéricamente (sufijos UE, capitalización).
    /// </summary>
    private string GetGameDisplayName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var exe = p.ProcessName;

            if (_gameNameByExe != null && _gameNameByExe.TryGetValue(exe, out var libName) &&
                !string.IsNullOrWhiteSpace(libName))
                return libName;

            var title = CleanTitle(GetWindowTitle(pid));
            if (title.Length > 0) return title;

            try
            {
                var vi = p.MainModule?.FileVersionInfo;
                if (vi != null && !string.IsNullOrWhiteSpace(vi.ProductName))
                {
                    var prod = vi.ProductName.Trim();
                    if (prod.Length > 0 && prod.Length <= 40 &&
                        !prod.Equals(exe, StringComparison.OrdinalIgnoreCase))
                        return prod;
                }
            }
            catch { }

            return GetGameDisplayName(exe);
        }
        catch { return ""; }
    }

    /// <summary>Limpia el nombre del proceso: quita sufijos de build UE y capitaliza.</summary>
    private static string GetGameDisplayName(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return "";
        var stripped = exeName;
        foreach (var suffix in ExeSuffixes)
            stripped = stripped.Replace(suffix, "", StringComparison.OrdinalIgnoreCase);
        stripped = stripped.Trim();

        var pretty = stripped.Replace('_', ' ').Replace('-', ' ');
        pretty = System.Text.RegularExpressions.Regex.Replace(pretty, "\\s+", " ").Trim();
        if (pretty.Length == 0) return exeName;
        var parts = pretty.Split(' ');
        for (int i = 0; i < parts.Length; i++)
        {
            var w = parts[i];
            if (w.Length > 0 && char.IsLower(w[0]))
                parts[i] = char.ToUpper(w[0]) + w.Substring(1);
        }
        return string.Join(" ", parts);
    }

    /// <summary>Mapea los juegos de la biblioteca por ejecutable (una vez, en background).</summary>
    private void EnsureGameLibraryLoaded()
    {
        if (_libraryLoadStarted) return;
        _libraryLoadStarted = true;
        Task.Run(async () =>
        {
            try
            {
                var games = await _installedGames.GetInstalledGamesAsync();
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in games)
                {
                    if (string.IsNullOrWhiteSpace(g.ExeFileName)) continue;
                    var exe = Path.GetFileNameWithoutExtension(g.ExeFileName);
                    if (!string.IsNullOrWhiteSpace(exe)) dict[exe] = g.Name;
                }
                _gameNameByExe = dict;
                _logging.LogInfo($"OverlayMetrics: {dict.Count} juegos de la biblioteca mapeados por ejecutable");
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"OverlayMetrics: no se pudo cargar la biblioteca: {ex.Message}");
            }
        });
    }

    // ===== Título de ventana del proceso =====

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static string GetWindowTitle(int pid)
    {
        string title = "";
        try
        {
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint wpid);
                if (wpid != (uint)pid) return true;
                if (!IsWindowVisible(h)) return true;
                var sb = new System.Text.StringBuilder(512);
                GetWindowText(h, sb, 512);
                var t = sb.ToString().Trim();
                if (t.Length > 0) { title = t; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return title;
    }

    private static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title.Trim();
        // Quitar sufijos de estado/plataforma: "Juego - Partida" -> "Juego".
        int idx = t.IndexOf(" - ", StringComparison.Ordinal);
        if (idx > 0) t = t.Substring(0, idx).Trim();
        if (t.Length > 40) t = t.Substring(0, 40).TrimEnd() + "…";
        return t;
    }

    // ===== API gráfica (DX11/DX12/Unity/Vulkan/DX9) =====

    private string GetGfxApi(int pid)
    {
        if (pid == _gfxApiPid) return _gfxApi;
        _gfxApiPid = pid;
        _gfxApi = DetectGfxApi(pid);
        return _gfxApi;
    }

    /// <summary>
    /// Detecta la API gráfica del proceso por los módulos cargados. El orden importa:
    /// Unity carga d3d11/d3d12 también, y los juegos DX12 suelen cargar d3d11.dll de
    /// compatibilidad — por eso unity primero y d3d12 antes que d3d11.
    /// </summary>
    private static string DetectGfxApi(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // En procesos protegidos por anti-cheat puede lanzar "Acceso denegado"
            // si la app no está elevada; la app corre elevada, así que funciona.
            foreach (ProcessModule m in p.Modules)
            {
                var n = m.ModuleName;
                if (string.IsNullOrEmpty(n)) continue;
                if (n.Equals("unityplayer.dll", StringComparison.OrdinalIgnoreCase)) return "Unity";
                if (n.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase)) return "DX12";
                if (n.Equals("d3d11.dll", StringComparison.OrdinalIgnoreCase)) return "DX11";
                if (n.Equals("vulkan-1.dll", StringComparison.OrdinalIgnoreCase)) return "Vulkan";
                if (n.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase)) return "DX9";
            }
            return "";
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        Stop();
        _cpuCounter?.Dispose();
        foreach (var counter in _gpuCounters) counter.Dispose();
        _gpuCounters.Clear();
    }
}
