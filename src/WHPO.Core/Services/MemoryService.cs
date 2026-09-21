using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación del gestor de memoria y latencia usando APIs nativas de Windows.
/// </summary>
public class MemoryService : IMemoryService
{
    private readonly ILoggingService _loggingService;
    private Timer? _autoCleanupTimer;
    private bool _autoCleanupActive;
    // Guard de reentrada: el callback puede durar más que el intervalo (la purga hace
    // un Sleep de 500 ms y el sondeo mínimo es de 100 ms), lo que solaparía llamadas.
    private int _autoCleanupRunning;
    private double _minStandbyMB = 1024;
    private double _maxFreeMB = 4096;
    private int _pollIntervalMs = 1000;
    private int _currentTimerResolution = 156250; // 15.625ms por defecto en Windows
    private int _requestedTimerResolution;        // valor que ESTA app solicitó (para liberarlo exacto)

    // ====== Resolución REAL del tick (medida) ======
    // En Win10 2004+/Win11 NtQueryTimerResolution solo refleja la petición del PROPIO
    // proceso; la resolución efectiva global puede ser más fina (otras apps la fuerzan,
    // p. ej. herramientas de optimización). La única forma de leer el dato real es medir
    // el paso del reloj de interrupción (QueryInterruptTime) contra QPC.
    private const int MinTickChanges = 5;             // saltos mínimos para aceptar la medición
    private const int MinSampleMs = 40;               // piso de ventana de medición
    private const int InterruptSampleTimeoutMs = 250; // techo de seguridad del muestreo
    private const long HighResPollDueHns = -1000;     // sondeo cada 0,1 ms (sub-tick). Cuanto
    // más fino el sondeo, menor la latencia
    // de detección de cada salto: con 0,4 ms
    // el error por intervalo era ±0,4 ms y en
    // ticks gruesos (pocos saltos en la ventana)
    // la mediana se iba ±20% (ej: "1,740 ms").
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    private const uint TIMER_MODIFY_STATE = 0x0002;
    private const uint SYNCHRONIZE = 0x00100000;
    private double _measuredTimerResolutionMs = 15.625;
    private bool _ownTimerRequestActive; // esta app tiene una petición activa (Iniciar)
    private IntPtr _highResTimer;
    private readonly object _measureLock = new();
    private static readonly long QpcFrequency = QueryQpcFrequency();

    private IntPtr GetHighResTimer()
    {
        if (_highResTimer != IntPtr.Zero)
            return _highResTimer;
        try
        {
    // Timer de alta resolución (Win10 1803+): permite esperas sub-milisegundo
    // precisas para sondear el tick más fino (0,5 ms) sin busy-wait.
            _highResTimer = CreateWaitableTimerEx(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, SYNCHRONIZE | TIMER_MODIFY_STATE);
        }
        catch (Exception ex)
        {
    // Sin el timer el medidor cae al sondeo grueso (Sleep): degradación
    // controlada en vez de romper cada medición con la excepción del P/Invoke.
            _loggingService.LogWarning($"Timer de alta resolución no disponible, muestreo degradado: {ex.Message}");
            _highResTimer = IntPtr.Zero;
        }
        return _highResTimer;
    }

    private static long QueryQpcFrequency()
    {
        QueryPerformanceFrequency(out long f);
        return f;
    }
    // OJO con la nomenclatura de NtQueryTimerResolution: MinimumResolution es la MÁS
    // GRUESA (15.625ms) y MaximumResolution la MÁS FINA (0.5ms).
    private int _minTimerResolution = 156250; // 15.625ms (la más gruesa)
    private int _maxTimerResolution = 5000; // 0.5ms (la más fina)
    private bool _timerResolutionQueried = false;
    private System.Diagnostics.PerformanceCounter? _standbyCounter;
    private bool _standbyCounterInitialized = false;
    private readonly object _statsCacheLock = new();
    private MemoryStats? _cachedMemoryStats;
    private DateTime _lastMemoryStatsRefresh = DateTime.MinValue;
    private PageFileStats? _cachedPageFileStats;
    private DateTime _lastPageFileStatsRefresh = DateTime.MinValue;
    private static readonly TimeSpan StatsCacheDuration = TimeSpan.FromSeconds(1);

    // Constantes para NtSetSystemInformation
    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryPurgeStandbyList = 4;
    private const int MemoryPurgeLowPriorityStandbyList = 5;

    // Privilegio SeProfileSingleProcessPrivilege (necesario para purgar lista standby)
    private const int SeProfileSingleProcessPrivilege = 13;
    private bool _privilegeEnabled = false;

    // Constantes para NtSetTimerResolution
    private const int TIMER_RESOLUTION_MINIMUM = 5000; // 0.5ms en 100ns units
    private const int TIMER_RESOLUTION_DEFAULT = 156250; // 15.625ms

 // NOTA: NO se usa el flag 0x80000000 de
 // NtSetTimerResolution: hace SIEMPRE una petición normal y, para el alcance
 // global, escribe la clave de registro del kernel GlobalTimerResolutionRequests
 // (HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel), que se lee al
 // boot. El flag 0x80000000 está bloqueado/ignorado en Windows 11 24H2+ (probado).
 // La petición normal respeta esa clave: si está en 1, ya es global.

    public MemoryService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
        EnablePrivilege();
    }

    private void EnablePrivilege()
    {
        try
        {
            int status = RtlAdjustPrivilege(SeProfileSingleProcessPrivilege, true, false, out bool enabled);
            if (status == 0)
            {
                _privilegeEnabled = true;
                _loggingService.LogInfo("Privilegio SeProfileSingleProcessPrivilege habilitado");
            }
            else
            {
                _loggingService.LogWarning($"No se pudo habilitar SeProfileSingleProcessPrivilege (código {status})");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error habilitando privilegio", ex);
        }
    }

    public bool IsAutoCleanupActive => _autoCleanupActive;

    public event EventHandler<StandbyCleanupEventArgs>? StandbyCleanupCompleted;

    // ====== P/Invoke ======

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_TIMER_INFORMATION
    {
        public ulong TimerResolution;
        public ulong TimerCount;
        public ulong TimerDueTime;
        public ulong TimerPeriod;
        public ulong TimerRequestCount;
    }

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSetSystemInformation(int SystemInformationClass, ref uint SystemInformation, int SystemInformationLength);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int RtlAdjustPrivilege(int Privilege, bool Enable, bool CurrentThread, out bool Enabled);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSetTimerResolution(int DesiredResolution, bool SetResolution, out int CurrentResolution);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtQueryTimerResolution(out int MinimumResolution, out int MaximumResolution, out int CurrentResolution);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, ref SYSTEM_TIMER_INFORMATION SystemInformation, int SystemInformationLength, out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryPerformanceFrequency(out long lpFrequency);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernelbase.dll")]
    private static extern void QueryInterruptTime(out ulong lpInterruptTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    // ====== Implementación ======

    public MemoryStats GetMemoryStats()
    {
        lock (_statsCacheLock)
        {
            var now = DateTime.UtcNow;
            if (_cachedMemoryStats != null && now - _lastMemoryStatsRefresh < StatsCacheDuration)
            {
                return _cachedMemoryStats;
            }
        }

        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                _loggingService.LogError("GlobalMemoryStatusEx falló", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                return new MemoryStats(0, 0, 0, 0, 0, 0, 0);
            }

            ulong totalMB = status.ullTotalPhys / (1024 * 1024);
            ulong availableMB = status.ullAvailPhys / (1024 * 1024);
            ulong usedMB = totalMB - availableMB;
            double usedPercent = totalMB > 0 ? (double)usedMB / totalMB * 100 : 0;

            double standbyMB = GetStandbyListSizeMB();
            double cachedMB = standbyMB; // La lista standby es la mayor parte de la caché
            // Libre REAL (sin caché): la disponible (ullAvailPhys) incluye la lista standby,
            // y para la condición de limpieza interesa el libre real, no el disponible.
            double freeMB = Math.Max(0, (double)availableMB - standbyMB);

            var stats = new MemoryStats(totalMB, availableMB, usedMB, usedPercent, standbyMB, cachedMB, freeMB);
            lock (_statsCacheLock)
            {
                _cachedMemoryStats = stats;
                _lastMemoryStatsRefresh = DateTime.UtcNow;
            }
            return stats;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo estadísticas de memoria", ex);
            return new MemoryStats(0, 0, 0, 0, 0, 0, 0);
        }
    }

    public double GetStandbyListSizeMB()
    {
        try
        {
            // Usar Performance Counter cacheado para obtener la lista standby.
            // Crear un PerformanceCounter nuevo en cada llamada es muy costoso y causa picos de CPU.
            if (_standbyCounter == null)
            {
                _standbyCounter = new System.Diagnostics.PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes");
            }

            var standbyBytes = _standbyCounter.NextValue();

            // Solo la primera vez esperar para obtener un valor inicial estable
            if (!_standbyCounterInitialized)
            {
                Thread.Sleep(100);
                standbyBytes = _standbyCounter.NextValue();
                _standbyCounterInitialized = true;
            }

            return standbyBytes / (1024.0 * 1024.0);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo tamaño de lista standby", ex);
            // Fallback: estimar usando la memoria disponible
            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref status))
                {
                    // La lista standby suele ser una parte de la memoria disponible
                    return status.ullAvailPhys / (1024.0 * 1024.0) * 0.3;
                }
            }
            catch { }
            return 0;
        }
    }

    public PageFileStats GetPageFileStats()
    {
        lock (_statsCacheLock)
        {
            var now = DateTime.UtcNow;
            if (_cachedPageFileStats != null && now - _lastPageFileStatsRefresh < StatsCacheDuration)
            {
                return _cachedPageFileStats;
            }
        }

        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                return new PageFileStats(0, 0, 0, 0);
            }

            ulong totalPageFileMB = status.ullTotalPageFile / (1024 * 1024);
            ulong availPageFileMB = status.ullAvailPageFile / (1024 * 1024);
            ulong usedPageFileMB = totalPageFileMB > availPageFileMB ? totalPageFileMB - availPageFileMB : 0;
            double usedPercent = totalPageFileMB > 0 ? (double)usedPageFileMB / totalPageFileMB * 100 : 0;

            var stats = new PageFileStats(totalPageFileMB, usedPageFileMB, availPageFileMB, usedPercent);
            lock (_statsCacheLock)
            {
                _cachedPageFileStats = stats;
                _lastPageFileStatsRefresh = DateTime.UtcNow;
            }
            return stats;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error obteniendo estadísticas de paginación", ex);
            return new PageFileStats(0, 0, 0, 0);
        }
    }

    public async Task<CommandResult> CleanStandbyListAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                double beforeMB = GetStandbyListSizeMB();

                // Purgar lista standby: comando 4 = MemoryPurgeStandbyList
                uint command = MemoryPurgeStandbyList;
                int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(uint));

                if (status != 0)
                {
                    // Intentar con lista de baja prioridad: comando 5 = MemoryPurgeLowPriorityStandbyList
                    command = MemoryPurgeLowPriorityStandbyList;
                    status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(uint));
                }

                if (status != 0)
                {
                    _loggingService.LogError($"NtSetSystemInformation falló con código {status}");
                    return new CommandResult(false, $"No se pudo limpiar la lista standby (código {status}). Asegúrese de ejecutar como administrador.",
                        "No se pudo limpiar la lista standby (código {0}). Asegúrese de ejecutar como administrador.", new object?[] { status });
                }

                // Esperar un momento para que el sistema actualice las estadísticas
                Thread.Sleep(500);
                InvalidateStatsCache();
                double afterMB = GetStandbyListSizeMB();
                double freedMB = Math.Max(0, beforeMB - afterMB);

                _loggingService.LogInfo($"Lista standby limpiada: {freedMB:F1} MB liberados");
                StandbyCleanupCompleted?.Invoke(this, new StandbyCleanupEventArgs(freedMB, false));

                return new CommandResult(true, $"Lista standby limpiada correctamente. {freedMB:F1} MB liberados.",
                    "Lista standby limpiada correctamente. {0} MB liberados.", new object?[] { $"{freedMB:F1}" });
            }
            catch (Exception ex)
            {
                _loggingService.LogError("Error limpiando lista standby", ex);
                return new CommandResult(false, ex.Message);
            }
        });
    }

    private void InvalidateStatsCache()
    {
        lock (_statsCacheLock)
        {
            _cachedMemoryStats = null;
            _cachedPageFileStats = null;
            _lastMemoryStatsRefresh = DateTime.MinValue;
            _lastPageFileStatsRefresh = DateTime.MinValue;
        }
    }

 /// <summary>
 /// Resolución actual del temporizador, tal como la reporta Windows
 /// (NtQueryTimerResolution): 0,500 ms con "Iniciar" activo, 15,625 ms al Detener.
 ///
 /// NOTA: no se usa la medición del tick de interrupción como fuente primaria.
 /// En Windows 11 24H2+ el timer de alta resolución que necesita el sondeo SUBE
 /// la resolución del sistema durante el muestreo, que se autoperturba y siempre
 /// termina midiendo ~0,5 ms sin importar la resolución real (probado
 /// empíricamente: la medición daba 0,518 ms con el sistema en 15,625). La
 /// medición queda solo como último recurso si la consulta falla.
 /// </summary>
    public int GetCurrentTimerResolution()
    {
 // NtQueryTimerResolution refleja nuestra petición si está activa y el
 // valor por-proceso del sistema si no: siempre coherente con lo que
 // Iniciar/Detener deberían mostrar.
        try
        {
            if (NtQueryTimerResolution(out _, out _, out int current) == 0 && current > 0)
                return current;
        }
        catch { /* caer a medición */ }
        return MeasureEffectiveTimerResolution();
    }

 /// <summary>
 /// Resoluciones que Windows realmente usa (serie de mitades del tick de 15,625 ms
 /// más los valores "redondos" que piden las apps). Sirven para ajustar la medición:
 /// el tick real del sistema siempre es uno de estos valores, así que si la medición
 /// cae a menos del 1,2% de uno de ellos (la separación mínima entre candidatos es
 /// ~2,4%), ese ES el valor.
 /// </summary>
    private static readonly double[] KnownTickMs =
    {
        0.48828125, 0.5, 0.9765625, 1.0, 1.953125, 2.0,
        3.90625, 4.0, 7.8125, 8.0, 10.0, 15.625
    };

    private const double SnapTolerance = 0.012; // 1,2%

    private static double SnapToKnownTick(double tickMs)
    {
        foreach (var known in KnownTickMs)
        {
            if (Math.Abs(tickMs - known) / known <= SnapTolerance)
                return known;
        }
        return tickMs;
    }

 /// <summary>
 /// Mide el tick observando cuántas veces SALTA el reloj de interrupción
 /// (QueryInterruptTime) en una ventana cronometrada con QPC. Sondea cada 0,4 ms
 /// con un timer de alta resolución para no perder saltos ni siquiera a 0,5 ms.
 /// Estimador: MEDIANA de los intervalos QPC entre saltos consecutivos. El promedio
 /// simple se inflaba cuando un sondeo atrasado por jitter cruzaba 2 fronteras de
 /// tick (contadas como 1 salto → un intervalo del doble que arrastraba el promedio
 /// por encima del real, p. ej. "0,505 ms" a un tick de 0,5 ms); la mediana los
 /// ignora. El resultado se ajusta a la serie de valores reales de Windows.
 /// Devuelve unidades de 100 ns.
 /// </summary>
    private int MeasureEffectiveTimerResolution()
    {
        lock (_measureLock)
        {
            try
            {
                IntPtr hTimer = GetHighResTimer();
                bool useHighRes = hTimer != IntPtr.Zero;

                QueryInterruptTime(out ulong last);
                int changes = 0;
                var changeQpcs = new List<long>(64);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                while (sw.ElapsedMilliseconds < InterruptSampleTimeoutMs)
                {
                    if (useHighRes)
                    {
                        long due = HighResPollDueHns;
                        if (!SetWaitableTimer(hTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                        {
                            useHighRes = false; // timer roto: caer al sondeo grueso
                            continue;
                        }
                        WaitForSingleObject(hTimer, 60);
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }

                    QueryInterruptTime(out ulong now);
                    if (now != last)
                    {
                        changes++;
                        QueryPerformanceCounter(out long qNow);
                        changeQpcs.Add(qNow);
                        last = now;
                    }
                    if (changes >= MinTickChanges && sw.ElapsedMilliseconds >= MinSampleMs)
                        break;
                }

 // El handle del timer se conserva para las siguientes mediciones
 // (MemoryService es singleton); no se cierra por medición.

                if (changes >= MinTickChanges)
                {
 // Intervalos entre saltos consecutivos (en ms)
                    var intervals = new List<double>(changeQpcs.Count - 1);
                    for (int i = 1; i < changeQpcs.Count; i++)
                        intervals.Add((changeQpcs[i] - changeQpcs[i - 1]) * 1000.0 / QpcFrequency);

                    intervals.Sort();
                    double tickMs = intervals.Count % 2 == 1
                        ? intervals[intervals.Count / 2]
                        : (intervals[intervals.Count / 2 - 1] + intervals[intervals.Count / 2]) / 2.0;

 // Valor absurdo (>20 ms por tick) = muestreo corrupto: conservar el anterior.
                    if (tickMs > 0 && tickMs <= 20.0)
                        _measuredTimerResolutionMs = SnapToKnownTick(tickMs);
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"No se pudo medir la resolución del tick: {ex.Message}");
            }

            return (int)Math.Round(_measuredTimerResolutionMs * 10000);
        }
    }

    public int GetMinimumTimerResolution()
    {
        try
        {
            if (NtQueryTimerResolution(out int min, out _, out _) == 0)
            {
                _minTimerResolution = min;
                return min;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error consultando resolución mínima del temporizador", ex);
        }
        return _minTimerResolution;
    }

    public int GetMaximumTimerResolution()
    {
        try
        {
            if (NtQueryTimerResolution(out _, out int max, out _) == 0)
            {
                _maxTimerResolution = max;
                return max;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error consultando resolución máxima del temporizador", ex);
        }
        return _maxTimerResolution;
    }

    public PerformanceTimerInfo GetPerformanceTimerInfo()
    {
        try
        {
            if (QueryPerformanceFrequency(out long frequency))
            {
                double mhz = frequency / 1000000.0;
                return new PerformanceTimerInfo("TSC (Time Stamp Counter)", mhz);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error consultando temporizador de rendimiento", ex);
        }
        return new PerformanceTimerInfo("TSC (Time Stamp Counter)", 0);
    }

    public int GetGlobalTimerResolutionRequests()
    {
        try
        {
            var info = new SYSTEM_TIMER_INFORMATION();
            int status = NtQuerySystemInformation(3, ref info, Marshal.SizeOf<SYSTEM_TIMER_INFORMATION>(), out _);
            if (status == 0)
            {
                return (int)info.TimerRequestCount;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error consultando solicitudes de resolución de temporizador", ex);
        }
        return 0;
    }

 // ===== Clave de registro GlobalTimerResolutionRequests =====
 // NO se usa el flag 0x80000000 de NtSetTimerResolution: se escribe esta clave del kernel
 // (HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel). El kernel la lee al
 // boot y, si está en 1, trata las peticiones normales (timeBeginPeriod/NtSetTimerResolution)
 // como globales — el comportamiento "viejo" de Win10. Sin la clave, en Win11 las peticiones
 // quedan por proceso y la resolución del sistema no baja. Requiere reinicio para aplicarse.

    private const string KernelSessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string GlobalTimerResolutionRequestsValue = "GlobalTimerResolutionRequests";

 /// <summary>
 /// Devuelve true si la clave GlobalTimerResolutionRequests está activada (1) en el registro.
 /// NOTA: aunque la clave esté en 1, el kernel solo la aplica desde el próximo boot.
 /// </summary>
    public bool IsGlobalTimerResolutionRequestEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(KernelSessionManagerKey);
            var value = key?.GetValue(GlobalTimerResolutionRequestsValue);
            return value != null && Convert.ToInt32(value) == 1;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error leyendo GlobalTimerResolutionRequests", ex);
            return false;
        }
    }

 /// <summary>
 /// Escribe (1) o borra la clave GlobalTimerResolutionRequests, equivalente.
 /// Requiere permisos de administrador. El efecto completo aplica tras reiniciar.
 /// </summary>
    public async Task<CommandResult> SetGlobalTimerResolutionRequestEnabledAsync(bool enabled)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(KernelSessionManagerKey, writable: true);
                if (key == null)
                {
                    return new CommandResult(false, "No se pudo abrir la clave del kernel del registro. Ejecute como administrador.",
                        "No se pudo abrir la clave del kernel del registro. Ejecute como administrador.");
                }

                if (enabled)
                {
                    key.SetValue(GlobalTimerResolutionRequestsValue, 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
                else
                {
                    if (key.GetValue(GlobalTimerResolutionRequestsValue) != null)
                        key.DeleteValue(GlobalTimerResolutionRequestsValue);
                }

                string msg = enabled
                    ? "GlobalTimerResolutionRequests activada. El kernel la aplicará al reiniciar el equipo."
                    : "GlobalTimerResolutionRequests desactivada. El kernel la aplicará al reiniciar el equipo.";
                _loggingService.LogInfo(msg);
                return new CommandResult(true, msg, msg);
            }
            catch (Exception ex)
            {
                _loggingService.LogError("Error escribiendo GlobalTimerResolutionRequests", ex);
                return new CommandResult(false, $"No se pudo modificar GlobalTimerResolutionRequests: {ex.Message}",
                    "No se pudo modificar GlobalTimerResolutionRequests: {0}", new object?[] { ex.Message });
            }
        });
    }

    public async Task<CommandResult> SetTimerResolutionAsync(int resolution100ns)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Validar rango
                if (resolution100ns < TIMER_RESOLUTION_MINIMUM)
                {
                    resolution100ns = TIMER_RESOLUTION_MINIMUM;
                }

                int status = NtSetTimerResolution(resolution100ns, true, out int current);
                if (status != 0)
                {
                    _loggingService.LogError($"NtSetTimerResolution falló con código {status}");
                    return new CommandResult(false, $"No se pudo establecer la resolución del temporizador (código {status}).",
                        "No se pudo establecer la resolución del temporizador (código {0}).", new object?[] { status });
                }

                _currentTimerResolution = current;
                _requestedTimerResolution = resolution100ns; // para liberarla exacta al Detener
                _ownTimerRequestActive = true;
                double effectiveMs = current / 10000.0;
                double requestedMs = resolution100ns / 10000.0;
                // Windows aplica siempre la solicitud MÁS FINA de todos los procesos: si otra
                // aplicación pide una resolución más fina que la nuestra, la efectiva queda
                // en esa y la nuestra queda registrada hasta que esa solicitud termine.
                string message;
                string template;
                object?[] args;
                if (Math.Abs(current - resolution100ns) > 1)
                {
                    message = $"Solicitud registrada: {requestedMs:F3} ms. La resolución efectiva quedó en {effectiveMs:F3} ms porque otra aplicación pide una más fina; se aplicará cuando esa solicitud termine.";
                    template = "Solicitud registrada: {0} ms. La resolución efectiva quedó en {1} ms porque otra aplicación pide una más fina; se aplicará cuando esa solicitud termine.";
                    args = new object?[] { $"{requestedMs:F3}", $"{effectiveMs:F3}" };
                }
                else
                {
                    message = $"Resolución del temporizador establecida a {effectiveMs:F3} ms.";
                    template = "Resolución del temporizador establecida a {0} ms.";
                    args = new object?[] { $"{effectiveMs:F3}" };
                }
                _loggingService.LogInfo(message);
                return new CommandResult(true, message, template, args);
            }
            catch (Exception ex)
            {
                _loggingService.LogError("Error estableciendo resolución del temporizador", ex);
                return new CommandResult(false, ex.Message);
            }
        });
    }

    public async Task<CommandResult> ResetTimerResolutionAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
 // La liberación debe usar el MISMO valor que se solicitó: el kernel
 // da de baja la petición que coincida con el desired. Liberar con un
 // valor distinto (ej. el default 156250) no suelta la petición fina
 // (ej. 0,5 ms) y la resolución quedaba clavada en ella tras Detener.
                int desired = _requestedTimerResolution != 0 ? _requestedTimerResolution : TIMER_RESOLUTION_MINIMUM;
                int status = NtSetTimerResolution(desired, false, out int current);
                if (status != 0)
                {
                    _loggingService.LogError($"NtSetTimerResolution (reset) falló con código {status}");
                    return new CommandResult(false, $"No se pudo restablecer la resolución del temporizador (código {status}).",
                        "No se pudo restablecer la resolución del temporizador (código {0}).", new object?[] { status });
                }

                _currentTimerResolution = current;
                _ownTimerRequestActive = false; // petición liberada
                _requestedTimerResolution = 0;

 // Mostrar lo que Windows reporta ahora (15,625 ms si nada más fuerza
 // el temporizador; la otra app puede seguir pidiendo lo suyo).
                int after = current;
                try
                {
                    if (NtQueryTimerResolution(out _, out _, out int nowCurrent) == 0 && nowCurrent > 0)
                        after = nowCurrent;
                }
                catch { }
                double ms = after / 10000.0;
                _loggingService.LogInfo($"Resolución del temporizador restablecida a {ms:F3} ms");
                return new CommandResult(true, $"Resolución del temporizador restablecida a {ms:F3} ms.",
                    "Resolución del temporizador restablecida a {0} ms.", new object?[] { $"{ms:F3}" });
            }
            catch (Exception ex)
            {
                _loggingService.LogError("Error restableciendo resolución del temporizador", ex);
                return new CommandResult(false, ex.Message);
            }
        });
    }

    public void StartAutoCleanup(double minStandbyMB, double maxFreeMB, int pollIntervalMs)
    {
        if (minStandbyMB < 0) minStandbyMB = 0;
        if (maxFreeMB < 0) maxFreeMB = 0;
        if (pollIntervalMs < 100) pollIntervalMs = 100;
        if (pollIntervalMs > 60000) pollIntervalMs = 60000;

        _minStandbyMB = minStandbyMB;
        _maxFreeMB = maxFreeMB;
        _pollIntervalMs = pollIntervalMs;

        _autoCleanupTimer?.Dispose();
        _autoCleanupTimer = new Timer(AutoCleanupCallback, null, TimeSpan.FromMilliseconds(pollIntervalMs), TimeSpan.FromMilliseconds(pollIntervalMs));
        _autoCleanupActive = true;

        _loggingService.LogInfo($"Limpieza automática iniciada: standby >= {minStandbyMB:F0} MB y libre <= {maxFreeMB:F0} MB, sondeo cada {pollIntervalMs} ms");
    }

    public void StopAutoCleanup()
    {
        _autoCleanupTimer?.Dispose();
        _autoCleanupTimer = null;
        _autoCleanupActive = false;

        _loggingService.LogInfo("Limpieza automática detenida");
    }

    private void AutoCleanupCallback(object? state)
    {
        // Si la corrida anterior sigue activa (purga con Sleep de 500 ms vs. sondeo de
        // hasta 100 ms), descartar este tick: no se limpia dos veces en paralelo.
        if (Interlocked.CompareExchange(ref _autoCleanupRunning, 1, 0) != 0) return;
        try
        {
            double standbyMB = GetStandbyListSizeMB();
            var stats = GetMemoryStats();

            // Condiciones: standby >= mínimo Y memoria libre <= máximo
            if (standbyMB >= _minStandbyMB && stats.FreeMB <= _maxFreeMB)
            {
                double beforeMB = standbyMB;

                uint command = MemoryPurgeStandbyList;
                int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(uint));

                if (status == 0)
                {
                    Thread.Sleep(500);
                    double afterMB = GetStandbyListSizeMB();
                    double freedMB = Math.Max(0, beforeMB - afterMB);

                    _loggingService.LogInfo($"Limpieza automática: {freedMB:F1} MB liberados (standby={beforeMB:F0} MB, libre={stats.FreeMB:F0} MB)");
                    StandbyCleanupCompleted?.Invoke(this, new StandbyCleanupEventArgs(freedMB, true));
                }
                else
                {
                    _loggingService.LogWarning($"Limpieza automática falló con código {status}");
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error en limpieza automática", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _autoCleanupRunning, 0);
        }
    }
}
