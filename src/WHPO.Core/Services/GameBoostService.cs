using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>    /// Implementación del "Modo juego de WinForge (BETA)".
///
/// Al aplicar (juego corriendo y switch activo) se guarda un snapshot del estado
/// previo y se hacen los cambios; al cerrar el juego se restaura exactamente ese
/// snapshot:
///   · Servicios: solo se re-arrancan los que estaban corriendo antes (nunca se
///     enciende algo que ya estaba detenido/deshabilitado).
///   · Prioridades: se devuelve la prioridad ORIGINAL a los procesos de segundo
///     plano que se bajaron (no al juego: su prioridad la manejan sus reglas).
///     Si un proceso cerró en el camino o su PID fue reutilizado, se omite.
///   · Notificaciones: las toasts se pausan mientras dura el boost y al cerrar el
///     juego se restaura el estado exacto anterior (equivale al modo foco "solo
///     alarmas").
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

    // Snapshot: interruptor maestro de notificaciones toast (null = la clave no
    // existía, es decir habilitadas por defecto — se borra al restaurar).
    private int? _toastsSnapshot;

    private sealed record ProcessSnapshot(int Pid, string Name, ProcessPriorityClass OriginalClass);
    private sealed record ServiceSnapshot(string Name, string StartType);

    // Pausa temporal de Windows Update: solo se detienen los servicios, sin cambiar
    // su tipo de inicio, así al reiniciar Windows vuelven a la normalidad solos.
    private static readonly string[] WindowsUpdateServices = { "wuauserv", "UsoSvc", "BITS" };

    // Servicios de mantenimiento/diagnóstico/telemetría que se detienen mientras se juega.
    private static readonly string[] MaintenanceServices =
    {
        "SysMain",          // Superfetch/SysMain (prefetch agresivo)
        "DiagTrack",        // Diagnósticos y telemetría
        "WerSvc",           // Informe de errores de Windows
        "dmwappushservice", // WAP Push (telemetría de dispositivos)
        "DusmSvc",          // Uso de datos
        "WSearch"           // Búsqueda de Windows (indexado)
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
    private const string GlobalPlanKey = "gameboost.powerPlanGuid";

    public GameBoostService(ISettingsService settings, ILoggingService logging, IProcessService processService)
    {
        _settings = settings;
        _logging = logging;
        _processService = processService;

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

    public string? GetGlobalPowerPlanGuid()
    {
        var guid = _settings.Get(GlobalPlanKey, string.Empty);
        return string.IsNullOrWhiteSpace(guid) ? null : guid;
    }

    public void SetGlobalPowerPlanGuid(string? planGuid)
    {
        _settings.Set(GlobalPlanKey, string.IsNullOrWhiteSpace(planGuid) ? string.Empty : planGuid);
        _settings.Save();
        _logging.LogDebug($"GameBoost: plan de energía global {(string.IsNullOrWhiteSpace(planGuid) ? "(ninguno)" : planGuid)}.");
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

        return Task.Run(ApplyCore);
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

    private void ApplyCore()
    {
        try
        {
            _logging.LogInfo("GameBoost: aplicando optimización de procesos al iniciar el juego...");

            // Lista configurable de procesos en segundo plano (tuerca del switch).
            var backgroundProcesses = GetBackgroundProcesses();

            // 1) Snapshot del estado previo, ANTES de tocar nada.
            var servicesToRestart = SnapshotRunningServices();
            var processesToRestore = SnapshotBackgroundPriorities(backgroundProcesses);
            _toastsSnapshot = ReadToastsEnabled();

            // 2) Comprometer el snapshot de forma atómica con la reserva de _active:
            //    si una restauración (juego cerrado muy rápido, switch apagado o la app
            //    en cierre) se ejecutó mientras hacíamos el snapshot, ya no queda nada
            //    que aplicar — commitear igual dejaría cambios aplicados con _active = false
            //    (sin dueño que los restaure).
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

            // 3) Aplicar los cambios.
            StopServices(WindowsUpdateServices.Concat(MaintenanceServices));
            DeprioritizeBackgroundProcesses(backgroundProcesses);
            PauseToasts();

            // 4) Plan de energía global: solo si ningún juego ya activó el suyo (el plan
            //    por juego configurado en la card tiene prioridad sobre el global).
            var globalPlan = GetGlobalPowerPlanGuid();
            if (!string.IsNullOrWhiteSpace(globalPlan))
            {
                bool applied = _processService.TryApplyGlobalPowerPlan(globalPlan!);
                _logging.LogDebug($"GameBoost: plan de energía global {(applied ? "activado" : "omitido (un juego ya activó el suyo)")}.");
            }

            _logging.LogInfo($"GameBoost: optimización aplicada ({servicesToRestart.Count} servicios y {processesToRestore.Count} procesos a restaurar al cerrar).");
        }
        catch (Exception ex)
        {
            _logging.LogError("GameBoost: error al aplicar la optimización.", ex);
        }
    }

    private List<ServiceSnapshot> SnapshotRunningServices()
    {
        var running = new List<ServiceSnapshot>();
        foreach (var svc in WindowsUpdateServices.Concat(MaintenanceServices))
        {
            if (IsServiceRunning(svc))
            {
                var startType = GetServiceStartType(svc);
                running.Add(new ServiceSnapshot(svc, startType ?? ""));
                _logging.LogDebug($"GameBoost: {svc} estaba corriendo (tipo {startType}) — se restaurará al cerrar.");
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
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch { }
        }
        return snap;
    }

    private void StopServices(IEnumerable<string> services)
    {
        foreach (var svc in services)
            RunServiceCommand(svc, "stop");
    }

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
            catch { }
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

    // ===== Restaurar =====

    private void RestoreCore()
    {
        List<ServiceSnapshot> servicesToRestart;
        List<ProcessSnapshot> processesToRestore;

        lock (_lock)
        {
            if (!_active) return;
            _active = false;
            servicesToRestart = _servicesToRestart;
            processesToRestore = _processesToRestore;
            _servicesToRestart = new();
            _processesToRestore = new();
        }

        try
        {
            _logging.LogInfo("GameBoost: restaurando estado previo...");

            // El plan global de GameBoost se revierte primero: si un juego con plan
            // propio lo tomó, este método no toca nada (el juego lo revierte al salir).
            _processService.RevertGlobalPowerPlan();

            foreach (var svc in servicesToRestart)
            {
                // Si el usuario deshabilitó el servicio durante la sesión de juego,
                // NO lo re-arrancamos: no hay que "encender" lo que el usuario apagó.
                var currentType = GetServiceStartType(svc.Name);
                if (currentType is not null && currentType.Contains("DISABLED", StringComparison.OrdinalIgnoreCase))
                {
                    _logging.LogInfo($"GameBoost: {svc.Name} está deshabilitado ahora — se omite su re-arranque.");
                    continue;
                }
                RunServiceCommand(svc.Name, "start");
            }

            foreach (var snap in processesToRestore)
                RestoreProcessPriority(snap);

            RestoreToasts();
            _logging.LogInfo("GameBoost: estado previo restaurado.");
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
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"query {serviceName}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is null) return false;

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Devuelve la línea "START_TYPE ..." de <c>sc qc</c>, o null si no se pudo leer.
    /// Se usa para detectar si un servicio quedó deshabilitado (DISABLED) y no debe re-arrancarse.
    /// </summary>
    private string? GetServiceStartType(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"qc {serviceName}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is null) return null;

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("START_TYPE", StringComparison.OrdinalIgnoreCase))
                    return line.Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void RunServiceCommand(string serviceName, string action)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"{action} {serviceName}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                _logging.LogWarning($"GameBoost: no se pudo iniciar sc {action} {serviceName}");
                return;
            }

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            var text = (outTask.Result + errTask.Result).Trim();
            _logging.LogDebug($"GameBoost: sc {action} {serviceName} → {(string.IsNullOrEmpty(text) ? "OK" : text)}");
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"GameBoost: error al ejecutar sc {action} {serviceName}: {ex.Message}");
        }
    }
}
