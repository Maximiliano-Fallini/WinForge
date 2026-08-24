using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WHPO.Core;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Services;

namespace WHPO_UI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow? MainWindowInstance { get; private set; }
    private static System.Threading.Mutex? _instanceMutex;
    private static bool _createdNew = false;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Configurar Dependency Injection
        var settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WHPO");

        var services = new ServiceCollection();
        services.AddWHPOServices(settingsDirectory);

        // Registrar implementaciones de UI
        services.AddSingleton<IThemeApplier, ThemeApplier>();
        services.AddSingleton<MacroHotkeyService>();
        services.AddSingleton<OverlayService>();
        // Singleton: el keep-alive del bloqueo de escaneo Wi-Fi debe sobrevivir a
        // la navegación entre pestañas (RedPage se recrea al salir/entrar).
        services.AddSingleton<WlanOptimizerService>();

        Services = services.BuildServiceProvider();

        // Evitar múltiples instancias de la aplicación (como ISLC: single-instance por sesión)
        _instanceMutex = new System.Threading.Mutex(true, @"Local\WHPO.UI.SingleInstance", out _createdNew);
        if (!_createdNew)
        {
            // Ya hay una instancia corriendo en esta sesión: cerrar esta inmediatamente
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return;
        }

        var logPath = Path.Combine(settingsDirectory, "errors.log");
        var logService = Services.GetRequiredService<ILoggingService>();

        // Eager-init del GameBoost: suscribe su restauración a los eventos WMI
        // desde el arranque, para que la optimización siga funcionando aunque no
        // se abra la biblioteca o se navegue a otra pestaña.
        _ = Services.GetService<IGameBoostService>();

        // Manejo de excepciones no controladas en UI thread
        this.UnhandledException += (s, e) =>
        {
            try
            {
                logService.LogError($"Excepción no controlada UI: {e.Exception}");
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] UI Unhandled: {e.Exception}{Environment.NewLine}");
            }
            catch { }
            e.Handled = true; // Marcar como manejada para evitar cierre de la app
        };

        // Manejo de excepciones no controladas en background threads
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString());
                logService.LogError($"Excepción crítica AppDomain: {ex}");
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] AppDomain Unhandled: {ex}{Environment.NewLine}");
            }
            catch { }
        };

        // Manejo de excepciones en tareas no observadas
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try
            {
                logService.LogError($"Excepción en task no observada: {e.Exception}");
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] Task Unobserved: {e.Exception}{Environment.NewLine}");
            }
            catch { }
            e.SetObserved();
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Si ya hay una instancia, salir inmediatamente para no dejar procesos fantasma.
        if (_instanceMutex == null || !_createdNew)
        {
            Exit();
            return;
        }

        // La app necesita permisos de administrador (reglas por registro, planes de
        // energía, eventos WMI). El manifest ya pide elevation, pero con UAC
        // deshabilitado o en contextos restringidos puede arrancar sin permisos:
        // avisar y salir en vez de funcionar rota en silencio.
        if (!IsAdministrator())
        {
            try
            {
                Services.GetRequiredService<ILoggingService>()
                    .LogWarning("Arranque sin permisos de administrador: se muestra aviso y se cierra la app.");
                // Inicializar el idioma para que el aviso respete el idioma elegido.
                I18n.Initialize(Services.GetRequiredService<ISettingsService>());
            }
            catch { }
            ShowAdminRequiredDialog();
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            Exit();
            return;
        }

        // Pre-calentar el sensor de temperatura desde el arranque (carga el driver de
        // LHM en segundo plano) para que la pestaña Núcleos muestre la temperatura
        // de inmediato y no quede en "Cargando…" cuando el usuario navegue.
        var sysInfo = Services.GetRequiredService<ISystemInfoService>();
        _ = Task.Run(() => { try { sysInfo.GetCpuTemperature(); } catch { } });

        // Vigilante de atajos de macros desde el arranque: los atajos funcionan sin
        // tener que visitar la pestaña "Teclado y Macros". Idempotente (la página
        // también lo asegura al navegar).
        Services.GetRequiredService<MacroHotkeyService>().EnsureStarted();

        // Marcador de sesión en el log: ayuda a separar corridas en fase de desarrollo.
        Services.GetRequiredService<ILoggingService>().LogInfo("===== WinForge iniciado =====");

        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        MainWindowInstance = _window as MainWindow;

        // Inicializar el tema DESPUÉS de crear la ventana: ThemeApplier ignora
        // ApplyTheme cuando no hay ventana, así que aplicarlo antes dejaba la app
        // en modo oscuro aunque la configuración dijera "claro" al reabrirla.
        var themeService = Services.GetRequiredService<IThemeService>();
        if (themeService is ThemeService ts)
        {
            ts.Initialize();
        }

        _window.Activate();

        // Chequeo de actualizaciones al abrir la app (async, no bloquea el arranque):
        // muestra el ícono "Actualizar a vX" / "Versión X en desarrollo" en el navbar.
        MainWindowInstance?.BeginUpdateCheck();

        var settingsService = Services.GetRequiredService<ISettingsService>();
        var startupService = Services.GetRequiredService<IStartupService>();

        // Primer arranque tras la instalación: el instalador deja HKLM\Software\WinForge\
        // FirstRunStartup=1 para que la app cree la tarea de inicio automático con su
        // propio mecanismo schtasks (la ruta real del exe instalado). Así "Iniciar con
        // Windows" funciona de entrada en cualquier máquina; el toggle de Configuración
        // la gestiona después normalmente. Se consume y borra la marca en el acto.
        try
        {
            using var firstRunKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\WinForge", writable: true);
            if (firstRunKey?.GetValue("FirstRunStartup") as string == "1")
            {
                if (startupService.SetEnabled(true, startMinimized: false).Success)
                {
                    try { firstRunKey.DeleteValue("FirstRunStartup", throwOnMissingValue: false); }
                    catch { /* best-effort */ }
                    Services.GetRequiredService<ILoggingService>()
                        .LogInfo("Inicio automatico creado en primer arranque (FirstRunStartup).");
                }
            }
        }
        catch { /* la marca es best-effort: si falla, el toggle sigue funcionando */ }

        // "Iniciar minimizado" se aplica SOLO cuando Windows lanza la app al iniciar
        // sesión: el valor del registro Run de "Iniciar con Windows" lleva el flag
        // --start-minimized cuando la opción está activa, así que un arranque manual
        // (doble clic, acceso directo) siempre abre la ventana normalmente.
        // Compat: instalaciones que activaron la opción antes de que existiera el flag
        // siguen minimizando en todo arranque hasta que la página de Configuración
        // normalice el valor del registro.
        bool startMinimized = StartupMinimizedRequested();
        if (!startMinimized
            && settingsService.Get("window.startMinimized", false)
            && !startupService.HasStartMinimizedFlag())
        {
            startMinimized = startupService.IsEnabled();
        }
        if (startMinimized)
        {
            MainWindowInstance?.HideToTrayAtStartup();
        }
    }

    // ===== Chequeo de administrador al arrancar =====

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Aviso de que la app necesita ejecutarse como administrador (antes de crear la ventana).</summary>
    private static void ShowAdminRequiredDialog()
    {
        const uint MbIconWarning = 0x30;      // MB_ICONWARNING
        const uint MbSetForeground = 0x10000; // MB_SETFOREGROUND
        MessageBoxW(IntPtr.Zero,
            I18n.T("Esta aplicación necesita ejecutarse como administrador para funcionar (reglas de juegos, planes de energía y monitoreo de procesos). Reiniciala con \"Ejecutar como administrador\"."),
            I18n.T("WinForge necesita permisos de administrador"),
            MbIconWarning | MbSetForeground);
    }

    /// <summary>¿El proceso se lanzó con el flag de arranque minimizado (registro Run)?</summary>
    private static bool StartupMinimizedRequested()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], StartupService.StartMinimizedArg, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void OnWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        // Cierre real de la app: restaurar el escaneo Wi-Fi si el optimizador WLAN
        // quedó activo (bloqueo de fondo / modo streaming) para no dejarlo bloqueado.
        try
        {
            var wlan = Services.GetService<WlanOptimizerService>();
            if (wlan != null && (wlan.BlockScanActive || wlan.StreamingActive))
            {
                foreach (var a in wlan.GetAdapters())
                    wlan.RestoreDefaults(a.Guid);
            }
        }
        catch { }

        // Restaurar el "Modo juego de WinForge (BETA)" si quedó
        // activo: si el usuario cierra WinForge con un juego corriendo, los servicios
        // detenidos y las prioridades bajadas deben volver a su estado previo. Se
        // espera la restauración (con tope) antes de terminar el proceso: si se
        // dejara correr en background, el proceso moriría antes y quedarían servicios
        // del sistema detenidos hasta reiniciar Windows (o re-abrir la app).
        try
        {
            var boost = Services.GetService<IGameBoostService>();
            if (boost != null)
            {
                var restoreTask = boost.RestoreAsync();
                if (!restoreTask.Wait(TimeSpan.FromSeconds(8)))
                {
                    Services.GetService<ILoggingService>()?.LogWarning(
                        "GameBoost: la restauración al cerrar no terminó en 8 s — se continúa el cierre igual.");
                }
            }
        }
        catch { }

        // Liberar el driver de LibreHardwareMonitor del SensorService (instancia
        // propia con CPU/GPU/placa/memoria/disco): SystemInfoService se dispone
        // aparte al detener el monitoreo. Cierra su Computer y libera el driver
        // WinRing0 de forma ordenada en vez de dejarlo al cleanup del proceso.
        try { Services.GetService<ISensorService>()?.Dispose(); }
        catch { }

        // Liberar el mutex al cerrar la ventana
        if (_instanceMutex != null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
                _instanceMutex.Dispose();
            }
            catch { }
            _instanceMutex = null;
        }
    }
}
