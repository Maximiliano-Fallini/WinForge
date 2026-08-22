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

    public int GetCurrentTimerResolution()
    {
        try
        {
            if (NtQueryTimerResolution(out _, out _, out int current) == 0)
            {
                _currentTimerResolution = current;
                return current;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error consultando resolución del temporizador", ex);
        }
        return _currentTimerResolution;
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
                int status = NtSetTimerResolution(TIMER_RESOLUTION_DEFAULT, false, out int current);
                if (status != 0)
                {
                    _loggingService.LogError($"NtSetTimerResolution (reset) falló con código {status}");
                    return new CommandResult(false, $"No se pudo restablecer la resolución del temporizador (código {status}).",
                        "No se pudo restablecer la resolución del temporizador (código {0}).", new object?[] { status });
                }

                _currentTimerResolution = current;
                double ms = current / 10000.0;
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
