using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Salud del Modo Juego de Windows y activación automática por partida.
///
/// Cómo funciona el Modo Juego de Windows (verificado):
/// - Interruptor maestro: HKCU\Software\Microsoft\GameBar
///   · "AutoGameModeEnabled" (DWORD 1/0)
///   · "AllowAutoGameMode" (DWORD 1/0, el clásico de Windows 10 1709+)
///   Ausentes = habilitado por defecto; 0 = deshabilitado.
/// - AtlasOS (y debloaters similares) lo apagan SOLO con reglas de registro
///   (su "Disable Game Mode.reg" escribe 0): no borra archivos. Por eso, si
///   solo está deshabilitado, basta con reactivar las reglas (borrar las
///   claves o ponerlas en 1) para que vuelva a funcionar.
/// - El caso "huérfano" es distinto: si falta la infraestructura — el paquete
///   Appx "Microsoft.XboxGamingOverlay" (Game Bar, del que el Modo Juego depende)
///   o System32\GameBarPresenceWriter.dll — el interruptor queda muerto y NO
///   hay regla de registro que lo reviva: hay que reinstalar Game Bar (Store).
///
/// Integración con el Modo juego de WinForge: al iniciar una partida se toma
/// snapshot del valor previo, se activa el Modo Juego (si no lo estaba) y al
/// cerrar el juego se restaura EXACTAMENTE lo que estaba (mismo contrato que
/// los servicios y las notificaciones del boost).
/// </summary>
public sealed class WindowsGameModeHealthService : IWindowsGameModeHealthService
{
    private const string GameBarKeyPath = @"Software\Microsoft\GameBar";
    private const string AutoGameModeValue = "AutoGameModeEnabled";
    private const string AllowAutoGameModeValue = "AllowAutoGameMode";
    private const string GameOverlayPackageFamily = "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe";
    private static readonly string PresenceWriterDll = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "GameBarPresenceWriter.dll");

    // Caché corta del chequeo: la consulta del paquete Appx (Get-AppxPackage)
    // tarda 1-3 s y cada arranque de partida no debería pagar ese costo. El
    // resultado se re-computa si pasaron más de CheckCacheTtl (o se invalida
    // explícitamente cuando se escriben reglas que cambian el estado).
    private static readonly TimeSpan CheckCacheTtl = TimeSpan.FromSeconds(45);
    private (WindowsGameModeHealthInfo Info, DateTime At)? _checkCache;

    private readonly ILoggingService _logging;

    public WindowsGameModeHealthService(ILoggingService loggingService)
    {
        _logging = loggingService;
    }

    // =====================================================================
    // Chequeo de salud (con caché corta)
    // =====================================================================

    public async Task<WindowsGameModeHealthInfo> CheckAsync()
    {
        var now = DateTime.UtcNow;
        if (_checkCache is { } cached && now - cached.At < CheckCacheTtl)
            return cached.Info;

        var info = await ComputeCheckAsync();
        _checkCache = (info, now);
        return info;
    }

    private void InvalidateCache() => _checkCache = null;

    private static Task<WindowsGameModeHealthInfo> ComputeCheckAsync() => Task.Run(() =>
    {
        var (master, checks) = ReadMasterSwitchAndChecks();
        bool appxPresent = checks.First(c => c.Component.Contains("XboxGamingOverlay")).Present;
        bool writerPresent = checks.First(c => c.Component.Contains("GameBarPresenceWriter")).Present;

        bool infraOk = appxPresent && writerPresent;

        WindowsGameModeHealth status;
        string summary;
        if (!infraOk)
        {
            status = WindowsGameModeHealth.Orphaned;
            summary = "Huérfano: faltan archivos del Modo Juego de Windows. Reinstalá Game Bar desde la Microsoft Store (modificar el registro no alcanza).";
        }
        else if (master == false)
        {
            status = WindowsGameModeHealth.DisabledByRules;
            summary = "Deshabilitado por reglas de registro (estilo AtlasOS): se puede reactivar al instante sin reinstalar nada.";
        }
        else
        {
            status = WindowsGameModeHealth.Ok;
            summary = "Listo: el Modo Juego de Windows se activará correctamente al iniciar un juego.";
        }

        return new WindowsGameModeHealthInfo(status, master, appxPresent, writerPresent, summary, checks);
    });

    // =====================================================================
    // Reactivar (caso "solo deshabilitado")
    // =====================================================================

    public async Task<WindowsGameModeHealthInfo> EnableGameModeAsync()
    {
        var before = await CheckAsync();
        if (before.Status == WindowsGameModeHealth.Ok)
            return before; // ya estaba bien: nada que hacer

        if (before.Status == WindowsGameModeHealth.Orphaned)
            return before; // sin archivos no hay regla que alcance: no tocar

        // Reactivar reglas: valor 1 en ambos (equivalente al Enable Game Mode.reg
        // de AtlasOS). Se escriben explícitos en vez de borrar las claves para
        // que el usuario vea el estado real en la Configuración de Windows.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GameBarKeyPath);
            key.SetValue(AutoGameModeValue, 1, RegistryValueKind.DWord);
            key.SetValue(AllowAutoGameModeValue, 1, RegistryValueKind.DWord);
            _logging.LogInfo("GameModeHealth: Modo Juego reactivado (AutoGameModeEnabled=1, AllowAutoGameMode=1).");
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"GameModeHealth: no se pudo reactivar el Modo Juego: {ex.Message}");
        }

        // Las claves cambiaron: invalidar la caché y verificar de nuevo (puede
        // seguir huérfano si además faltan archivos).
        InvalidateCache();
        return await CheckAsync();
    }

    // =====================================================================
    // Activación por partida (snapshot → activar → restaurar)
    // =====================================================================

    public Task<int?> EnsureEnabledForSessionAsync() => Task.Run<int?>(() =>
    {
        int? previous = null; // null = las claves no existían (habilitado por defecto)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GameBarKeyPath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(GameBarKeyPath);
            if (key == null) return null;

            // Snapshot del estado previo: AutoGameModeEnabled si existe; si no,
            // AllowAutoGameMode; si tampoco, null (default habilitado).
            var auto = key.GetValue(AutoGameModeValue);
            var allow = key.GetValue(AllowAutoGameModeValue);
            previous = auto is int a ? a : (allow is int b ? b : null);

            // Activar (idempotente: si ya estaba en 1, no cambia nada).
            key.SetValue(AutoGameModeValue, 1, RegistryValueKind.DWord);
            key.SetValue(AllowAutoGameModeValue, 1, RegistryValueKind.DWord);

            _logging.LogInfo($"GameModeHealth: Modo Juego asegurado para la partida (estado previo: {(previous?.ToString() ?? "habilitado por defecto")}).");
            return previous;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"GameModeHealth: no se pudo asegurar el Modo Juego: {ex.Message}");
            return previous;
        }
    });

    /// <summary>
    /// Restaura el snapshot previo al cerrar la partida. Un 0 EXPLÍCITO se
    /// re-escribe (el usuario lo tenía apagado); null borra las claves (estaban
    /// ausentes = habilitado por defecto).
    /// </summary>
    public void RestoreForSessionEnd(int? snapshot)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GameBarKeyPath, writable: true);
            if (key == null) return;

            if (snapshot is int v)
            {
                key.SetValue(AutoGameModeValue, v, RegistryValueKind.DWord);
                key.SetValue(AllowAutoGameModeValue, v, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(AutoGameModeValue, throwOnMissingValue: false);
                key.DeleteValue(AllowAutoGameModeValue, throwOnMissingValue: false);
            }
            _logging.LogInfo("GameModeHealth: Modo Juego restaurado al estado previo de la partida.");
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"GameModeHealth: no se pudo restaurar el Modo Juego: {ex.Message}");
        }
    }

    // =====================================================================
    // Lectura de estado (registro + archivos)
    // =====================================================================

    /// <summary>
    /// Lee el interruptor maestro y arma la lista de checks (registro + Appx + DLL).
    /// La consulta del paquete Appx corre en un proceso PowerShell aparte: el
    /// proyecto Core no referencia las APIs de paquetes (net9.0 puro) y así se
    /// evita acoplarlo a Windows SDK.
    /// </summary>
    private static (bool? Master, List<(string Component, bool Present, string Detail)> Checks) ReadMasterSwitchAndChecks()
    {
        bool? master = null;
        var checks = new List<(string Component, bool Present, string Detail)>();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GameBarKeyPath);
            if (key != null)
            {
                if (key.GetValue(AutoGameModeValue) is int a) master = a != 0;
                else if (key.GetValue(AllowAutoGameModeValue) is int b) master = b != 0;
            }
            checks.Add((
                "GameBar (registro)",
                master != false,
                master == null ? "Claves ausentes = habilitado por defecto"
                               : $"AutoGameModeEnabled/AllowAutoGameMode = {(master == true ? "1" : "0")}"));
        }
        catch (Exception ex)
        {
            checks.Add(("GameBar (registro)", false, $"Error al leer: {ex.Message}"));
        }

        try
        {
            bool present = QueryAppxPackagePresent();
            checks.Add((
                "Appx Microsoft.XboxGamingOverlay",
                present,
                present ? "Paquete presente (infra de Game Bar / Modo Juego)"
                        : "Paquete ausente: Modo Juego huérfano (reinstalar Game Bar desde la Store)"));
        }
        catch (Exception ex)
        {
            checks.Add(("Appx Microsoft.XboxGamingOverlay", false, $"Error al consultar: {ex.Message}"));
        }

        try
        {
            bool present = File.Exists(PresenceWriterDll);
            checks.Add((
                "System32\\GameBarPresenceWriter.dll",
                present,
                present ? "DLL presente" : "DLL ausente: Modo Juego huérfano"));
        }
        catch (Exception ex)
        {
            checks.Add(("System32\\GameBarPresenceWriter.dll", false, $"Error al verificar: {ex.Message}"));
        }

        return (master, checks);
    }

    /// <summary>
    /// ¿Está registrado el paquete para el usuario actual? Get-AppxPackage tarda
    /// 1-3 s: se corre con timeout corto y sin perfil de carga.
    /// </summary>
    private static bool QueryAppxPackagePresent()
    {
        try
        {
            var psi = new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoProfile -NonInteractive -Command \"(Get-AppxPackage -Name 'Microsoft.XboxGamingOverlay') -ne $null\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var outTask = p.StandardOutput.ReadToEndAsync();
            p.WaitForExit(5000);
            if (!p.HasExited) { try { p.Kill(); } catch { } return false; }
            var text = outTask.Result.Trim();
            return text.Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}