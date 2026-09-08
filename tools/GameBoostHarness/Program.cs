// Harness de prueba del GameBoostService (solo diagnóstico: correr elevado).
// Escenarios:
//   A) boost ON + optimización de servicios ON  → stop/restore de los servicios del boost
//   B) boost ON + optimización de servicios OFF → no toca ningún servicio
//   C) boost OFF                                → no hace nada
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

// ===== Fake IProcessService: GameBoost solo usa ApplyCpuPriority/RunningGameExes/WmiEventsActive =====
internal sealed class FakeProcessService : IProcessService
{
    public event Action? RunningGamesChanged;
    public event Action? LauncherStateChanged;
    public IReadOnlyCollection<string> RunningGameExes { get; } = Array.Empty<string>();
    public bool WmiEventsActive => true;
    public int ProcessorCount => Environment.ProcessorCount;
    public int LastGpuPriorityStatus => 0;
    public bool ApplyCpuPriority(int pid, int priority) => true;
    public bool ApplyAffinity(int pid, long mask) => true;
    public long GetAffinity(int pid) => 0;
    public bool ApplyGpuPriority(int pid, int priority) => true;
    public bool ApplyIoPriority(int pid, int priority) => true;
    public int? GetGpuPriority(int pid) => null;
    public int? GetIoPriority(int pid) => null;
    public bool CanOpenForModify(int pid) => true;
    public bool IsSystemProcessName(string exe) => false;
    public ProcessAppInfo? FindProcess(string exeFileName) => null;
    public ProcessAppInfo? FindRunningProcess(string exe) => null;
    public List<ProcessAppInfo> FindRunningProcessesForRule(string ruleExe) => new();
    public string? GetProcessPath(Process process) => null;
    public void ApplyRule(ProcessAppInfo app, ProcessRule rule) { }
    public RuleApplyFeedback ApplyRuleWithFeedback(ProcessAppInfo app, ProcessRule rule) => new(false, false, false);
    public void ApplyLaunchChainRule(string exeFileName) { }
    public Dictionary<string, ProcessRule> GetRules() => new();
    public Dictionary<string, ProcessRule> GetRulesCached() => new();
    public void SaveRule(string exe, ProcessRule rule) { }
    public void RemoveRule(string exe) { }
    public void SetSessionRule(string exe, ProcessRule rule) { }
    public ProcessRule? GetSessionRule(string exe) => null;
    public ProcessRule? GetEffectiveRule(string exe) => null;
    public void ClearSessionRule(string exe) { }
    public void ApplyPowerPlanIfRunning(string exe, string? planGuid) { }
    public void RevertPowerPlanIfApplied(string exe) { }
    public void SetKnownInstallPaths(Dictionary<string, string> exeToInstallPath) { }
    public List<string> GetFavorites() => new();
    public void ToggleFavorite(string exe) { }
    public bool IsFavorite(string exe) => false;
    public List<string> GetManualExes() => new();
    public List<(string Exe, string? Name, string? InstallPath)> GetManualEntries() => new();
    public void AddManualExe(string exe, string? displayName = null, string? installPath = null) { }
    public void RemoveManualExe(string exe) { }
    public void ClearManualExes() { }
    public void SetManualGameLauncher(string exe, string launcher) { }
    public string? GetManualGameLauncher(string exe) => null;
    public Dictionary<string, string> GetManualGameLaunchers() => new();
    public List<string> GetHiddenExes() => new();
    public void HideExe(string exe) { }
    public void UnhideExe(string exe) { }
    public void ClearHiddenExes() { }
    public List<string> GetDeletedGames() => new();
    public void DeleteGame(string exe) { }
    public void ClearDeletedGames() { }
    public bool IsLauncherRunning(string procName) => false;
}

internal static class Program
{
    private static readonly List<string> _lines = new();
    private static int _fails;

    private static void Report(string line)
    {
        Console.WriteLine(line);
        _lines.Add(line);
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Report($"  [PASS] {name} {detail}");
        else { Report($"  [FAIL] {name} {detail}"); _fails++; }
    }

    private static int Main()
    {
        var resultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WHPO", "gameboost-test-results.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        Report($"=== Harness GameBoost — admin={isAdmin} — {DateTime.Now:HH:mm:ss} ===");
        if (!isAdmin)
        {
            Report("[FATAL] Sin privilegios de administrador: sc stop/start va a fallar.");
            File.WriteAllLines(resultPath, _lines);
            return 2;
        }

        // Settings aislados: no toca el settings.json real del usuario.
        var settingsDir = Path.Combine(Path.GetTempPath(), "whpo-boost-harness-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(settingsDir);

        var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Debug));
        var logging = new LoggingService(loggerFactory.CreateLogger<LoggingService>());
        logging.SetFileLoggingEnabled(true);
        var settings = new SettingsService(logging, settingsDir);
        var boost = new GameBoostService(settings, logging, new FakeProcessService());

        var svcs = GameBoostService.BoostStopServices;
        Report($"Servicios bajo test: {string.Join(", ", svcs)}");

        try
        {
            ScenarioA(boost, svcs).GetAwaiter().GetResult();
            ScenarioB(boost, svcs).GetAwaiter().GetResult();
            ScenarioC(boost, svcs).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Report($"[FATAL] {ex}");
        }

        Report(_fails == 0 ? "=== RESULTADO: TODO OK ===" : $"=== RESULTADO: {_fails} CHECKS FALLARON ===");
        File.WriteAllLines(resultPath, _lines);
        return _fails == 0 ? 0 : 1;
    }

    private static Dictionary<string, bool> Snapshot(GameBoostService boost, string[] svcs)
        => boost.GetServicesRunning(svcs);

    private static string Fmt(Dictionary<string, bool> s)
        => string.Join(", ", s.Select(kv => $"{kv.Key}={(kv.Value ? "RUN" : "STOP")}"));

    // Espera a que los servicios indicados queden Running (sc start a veces deja START_PENDING).
    private static async Task WaitRunningAsync(GameBoostService boost, IEnumerable<string> names, int timeoutSec = 20)
    {
        var list = names.ToList();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            var s = boost.GetServicesRunning(list);
            if (s.All(kv => kv.Value)) return;
            await Task.Delay(1000);
        }
    }

    // ===== Escenario A: boost ON + optimización de servicios ON =====
    private static async Task ScenarioA(GameBoostService boost, string[] svcs)
    {
        Report("\n--- ESCENARIO A: boost ON + optimización de servicios ON ---");
        boost.SetEnabled(true);
        boost.IsAutomaticServiceOptimizationEnabled = true;

        var before = Snapshot(boost, svcs);
        Report($"  Antes:   {Fmt(before)}");

        await boost.ApplyAsync();
        await Task.Delay(2000); // settle: WMI refleja el stop

        var during = Snapshot(boost, svcs);
        Report($"  Boost:   {Fmt(during)}");

        foreach (var svc in svcs)
            if (before.GetValueOrDefault(svc))
                Check($"{svc} detenido durante boost", !during.GetValueOrDefault(svc));

        await boost.RestoreAsync();
        await WaitRunningAsync(boost, svcs.Where(s => before.GetValueOrDefault(s)));

        var after = Snapshot(boost, svcs);
        Report($"  Después: {Fmt(after)}");
        foreach (var svc in svcs)
            if (before.GetValueOrDefault(svc))
                Check($"{svc} re-arrancado al restaurar", after.GetValueOrDefault(svc));

        // El modo de arranque no debe cambiar (el boost es temporal).
        var modes = boost.GetServiceStates(svcs);
        Report($"  Modos de arranque finales: {string.Join(", ", modes.Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    // ===== Escenario B: boost ON + optimización de servicios OFF =====
    private static async Task ScenarioB(GameBoostService boost, string[] svcs)
    {
        Report("\n--- ESCENARIO B: boost ON + optimización de servicios OFF ---");
        boost.SetEnabled(true);
        boost.IsAutomaticServiceOptimizationEnabled = false;

        var before = Snapshot(boost, svcs);
        Report($"  Antes:   {Fmt(before)}");

        await boost.ApplyAsync();
        await Task.Delay(2000);

        var during = Snapshot(boost, svcs);
        Report($"  Boost:   {Fmt(during)}");

        foreach (var svc in svcs)
            Check($"{svc} intacto (switch apagado no detiene servicios)",
                during.GetValueOrDefault(svc) == before.GetValueOrDefault(svc));

        await boost.RestoreAsync(); // no-op: sin snapshot
    }

    // ===== Escenario C: boost OFF =====
    private static async Task ScenarioC(GameBoostService boost, string[] svcs)
    {
        Report("\n--- ESCENARIO C: boost OFF ---");
        boost.SetEnabled(false); // dispara RestoreAsync interno (no-op)

        var before = Snapshot(boost, svcs);
        Report($"  Antes:   {Fmt(before)}");

        await boost.ApplyAsync(); // debe ser no-op
        await Task.Delay(1500);

        var after = Snapshot(boost, svcs);
        Report($"  Después: {Fmt(after)}");

        foreach (var svc in svcs)
            Check($"{svc} intacto con boost desactivado",
                after.GetValueOrDefault(svc) == before.GetValueOrDefault(svc));
    }
}

