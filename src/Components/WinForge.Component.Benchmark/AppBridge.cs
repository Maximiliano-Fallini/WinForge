using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services.Interfaces;
using WinForge.Component.Benchmark.Metrics;

namespace WinForge.Component.Benchmark;

/// <summary>
/// Puente con la app que hospeda el componente.
///
/// El componente referencia WHPO.Core (los contratos: sensores, overlay, energía, ajustes),
/// pero las INSTANCIAS las construye la app en su contenedor de servicios. Acá hay un solo
/// salto por reflexión —la propiedad estática <c>App.Services</c>— y de ahí en adelante todo
/// es TIPADO: se resuelven interfaces de Core y no se invoca nada por nombre. Es el mismo
/// criterio con el que el Medidor de latencia llega a I18n y a los pinceles del tema.
///
/// Si el puente no está disponible (el componente abierto con una app vieja, o corriendo en
/// el harness), el componente lo dice y mide igual lo que puede medir solo: la escena y el
/// informe no dependen de la app.
/// </summary>
internal static class AppBridge
{
    private const string AppTypeName = "WHPO_UI.App";
    private const string I18NTypeName = "WHPO_UI.I18n";
    private const string ThemeBrushesTypeName = "WHPO_UI.ThemeBrushes";

    private static readonly object Sync = new();
    private static readonly List<Action> LanguageHandlers = new();
    private static bool _resolved;
    private static IServiceProvider? _services;
    private static MethodInfo? _translate;
    private static MethodInfo? _translateFormat;
    private static MethodInfo? _themeBrush;
    private static bool _languageHooked;
    private static bool _metricsStartedByUs;

    /// <summary>True si la app anfitriona se pudo resolver (sensores, tema e idioma disponibles).</summary>
    public static bool HasServices { get { Resolve(); return _services != null; } }

    /// <summary>Versión de la app anfitriona, leída del ejecutable (sin reflexión).</summary>
    public static string AppVersion { get; } = ReadAppVersion();

    // =====================================================================
    // Resolución perezosa
    // =====================================================================

    private static void Resolve()
    {
        if (_resolved) return;
        lock (Sync)
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var appAssembly = Application.Current?.GetType().Assembly;
                if (appAssembly == null) return;

                var appType = appAssembly.GetType(AppTypeName, throwOnError: false);
                var servicesProperty = appType?.GetProperty("Services", BindingFlags.Public | BindingFlags.Static);
                _services = servicesProperty?.GetValue(null) as IServiceProvider;

                var i18n = appAssembly.GetType(I18NTypeName, throwOnError: false);
                _translate = i18n?.GetMethod("T", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string) }, null);
                _translateFormat = i18n?.GetMethod("T", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(object?[]) }, null);

                var brushes = appAssembly.GetType(ThemeBrushesTypeName, throwOnError: false);
                _themeBrush = brushes?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string) }, null);
            }
            catch
            {
                _services = null;
            }
        }
    }

    /// <summary>Servicio de Core por contrato (null si la app no lo tiene registrado).</summary>
    public static T? GetService<T>() where T : class
    {
        Resolve();
        try { return _services?.GetService(typeof(T)) as T; }
        catch { return null; }
    }

    // =====================================================================
    // Idioma y tema (como cualquier página de la app)
    // =====================================================================

    /// <summary>Traduce con el motor de la app; sin app o sin clave devuelve el texto fuente.</summary>
    public static string T(string source)
    {
        Resolve();
        try { return _translate?.Invoke(null, new object[] { source }) as string ?? source; }
        catch { return source; }
    }

    /// <summary>Traduce una plantilla con marcadores ({0}, {1}…).</summary>
    public static string T(string template, params object?[] args)
    {
        Resolve();
        try
        {
            return _translateFormat?.Invoke(null, new object?[] { template, args }) as string
                   ?? string.Format(template, args);
        }
        catch { return template; }
    }

    /// <summary>Pincel del tema EFECTIVO de la ventana (nunca los recursos del sistema).</summary>
    public static Brush Brush(string key)
    {
        Resolve();
        try
        {
            if (_themeBrush?.Invoke(null, new object[] { key }) is Brush brush) return brush;
        }
        catch { }
        try
        {
            if (Application.Current?.Resources?.TryGetValue(key, out var value) == true && value is Brush fallback)
                return fallback;
        }
        catch { }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(128, 128, 128, 128));
    }

    /// <summary>
    /// Se suscribe al cambio de idioma de la app. El handler corre en el hilo que cambió el
    /// idioma (el de UI), igual que en las páginas de fábrica.
    /// </summary>
    public static void OnLanguageChanged(Action handler)
    {
        Resolve();
        lock (Sync)
        {
            LanguageHandlers.Add(handler);
            if (_languageHooked) return;

            try
            {
                var appAssembly = Application.Current?.GetType().Assembly;
                var i18n = appAssembly?.GetType(I18NTypeName, throwOnError: false);
                var languageChanged = i18n?.GetEvent("LanguageChanged", BindingFlags.Public | BindingFlags.Static);
                if (languageChanged == null) return;

                // El evento es Action: se puede suscribir un delegado directo por reflexión.
                languageChanged.AddEventHandler(null, new Action(() =>
                {
                    Action[] snapshot;
                    lock (Sync) snapshot = LanguageHandlers.ToArray();
                    foreach (var callback in snapshot) callback();
                }));
                _languageHooked = true;
            }
            catch { }
        }
    }

    // =====================================================================
    // Sensores (mismas métricas que el overlay de la app)
    // =====================================================================

    /// <summary>
    /// Última lectura de sensores de la app, o null si todavía no hay ninguna. Es la MISMA
    /// fuente que alimenta el overlay horizontal: uso, temperatura, frecuencia y potencia de
    /// GPU y CPU, VRAM y RAM.
    /// </summary>
    public static SensorSample? ReadSensors()
    {
        var metrics = GetService<IOverlayMetricsService>()?.Latest;
        if (metrics == null) return null;
        return new SensorSample(
            metrics.GpuUsagePercent,
            metrics.GpuTempCelsius,
            metrics.GpuMhz,
            metrics.GpuWatts,
            metrics.GpuMemUsedMb,
            metrics.GpuVramTotalMb,
            metrics.CpuUsagePercent,
            metrics.CpuTempCelsius,
            metrics.CpuMhz,
            metrics.CpuWatts,
            metrics.RamPercent,
            metrics.RamUsedMb,
            metrics.RamTotalMb);
    }

    /// <summary>
    /// Se asegura de que el muestreo de métricas de la app esté corriendo mientras dura la
    /// corrida (el overlay puede estar apagado). Devuelve true si lo arrancamos NOSOTROS: en ese
    /// caso hay que apagarlo al terminar, para no dejarle el muestreo prendido al usuario.
    /// </summary>
    public static bool EnsureMetricsRunning()
    {
        var metrics = GetService<IOverlayMetricsService>();
        if (metrics == null) return false;
        try
        {
            if (metrics.IsRunning) return false;
            metrics.Start();
            _metricsStartedByUs = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Apaga el muestreo solo si lo arrancamos nosotros.</summary>
    public static void StopMetricsIfWeStarted()
    {
        if (!_metricsStartedByUs) return;
        _metricsStartedByUs = false;
        try { GetService<IOverlayMetricsService>()?.Stop(); } catch { }
    }

    /// <summary>
    /// Huella del equipo para el informe: es lo que hace VÁLIDA una comparación entre corridas.
    /// Todo dato del sistema se toma de los servicios de la app; lo que no se puede leer, no
    /// se inventa (queda afuera).
    /// </summary>
    public static Dictionary<string, string> BuildFingerprint()
    {
        var fingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        fingerprint["Sistema operativo"] = DescribeOs();
        fingerprint["Equipo"] = Safe(() => Environment.MachineName);

        var systemInfo = GetService<ISystemInfoService>();
        if (systemInfo != null)
        {
            fingerprint["CPU"] = Safe(() =>
            {
                var cpu = systemInfo.GetCpuInfo();
                return cpu == null ? "" : $"{cpu.Name} ({cpu.PhysicalCores}C/{cpu.LogicalProcessors}H, hasta {cpu.MaxFrequencyMHz:F0} MHz)";
            });
            fingerprint["RAM"] = Safe(() =>
            {
                var modules = systemInfo.GetMemoryModuleInfo();
                var memory = systemInfo.GetMemoryInfo();
                if (modules == null) return "";
                double totalGb = memory != null ? memory.TotalBytes / 1024.0 / 1024.0 / 1024.0 : 0;
                string size = totalGb > 0 ? $"{totalGb:F1} GB" : $"{modules.ModuleCount} módulo(s)";
                return $"{size} · {modules.ChannelMode} · {modules.SpeedMHz} MHz";
            });
            fingerprint["GPU"] = Safe(() =>
                string.Join(" | ", systemInfo.GetGpuInfo().Select(g => $"{g.Name} (driver {g.DriverVersion}, {g.DedicatedMemoryBytes / 1024 / 1024 / 1024} GB)")));
            fingerprint["Set de instrucciones"] = Safe(() => systemInfo.GetCpuInstructionSet() ?? "");
        }

        fingerprint["Plan de energía"] = Safe(() =>
        {
            var power = GetService<ICpuPowerService>();
            if (power == null) return "";
            string guid = power.GetActivePowerPlanGuid() ?? "";
            string name = power.GetPowerPlans().FirstOrDefault(p => string.Equals(p.Guid, guid, StringComparison.OrdinalIgnoreCase))?.Name ?? "";
            return string.IsNullOrWhiteSpace(name) ? guid : $"{name} ({guid})";
        });

        // La resolución importa: una corrida en 4K no se compara con una en 1080p.
        fingerprint["Resolución de pantalla"] = Safe(() =>
        {
            int width = NativeMethodsScreenWidth();
            int height = NativeMethodsScreenHeight();
            return width > 0 && height > 0 ? $"{width}×{height}" : "";
        });

        // Lo que no se pudo leer se descarta en vez de quedar como una clave vacía.
        foreach (var key in fingerprint.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToList())
            fingerprint.Remove(key);

        return fingerprint;
    }

    private static string DescribeOs()
    {
        var os = GetService<ISystemInfoService>()?.GetOsInfo();
        if (os == null) return Environment.OSVersion.VersionString;
        return $"{os.Name} {os.Version} (build {os.BuildNumber}, {os.Architecture})";
    }

    private static string Safe(Func<string> read)
    {
        try { return read() ?? ""; }
        catch { return ""; }
    }

    private static int NativeMethodsScreenWidth() => Host.NativeMethods.GetSystemMetrics(Host.NativeMethods.SM_CXSCREEN);

    private static int NativeMethodsScreenHeight() => Host.NativeMethods.GetSystemMetrics(Host.NativeMethods.SM_CYSCREEN);

    private static string ReadAppVersion()
    {
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) return "";
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.ProductVersion ?? info.FileVersion ?? "";
        }
        catch { return ""; }
    }
}
