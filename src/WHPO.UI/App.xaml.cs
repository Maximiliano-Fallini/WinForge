using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WHPO.Core;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

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
        // Si ya hay una instancia, salir inmediatamente para no dejar procesos fantasma
        if (_instanceMutex == null || !_createdNew)
        {
            Exit();
            return;
        }

        // Pre-calentar el sensor de temperatura desde el arranque (carga el driver de
        // LHM en segundo plano) para que la pestaña Núcleos muestre la temperatura
        // de inmediato y no quede en "Cargando…" cuando el usuario navegue.
        var sysInfo = Services.GetRequiredService<ISystemInfoService>();
        _ = Task.Run(() => { try { sysInfo.GetCpuTemperature(); } catch { } });

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

        var settingsService = Services.GetRequiredService<ISettingsService>();
        if (settingsService.Get("window.startMinimized", false))
        {
            MainWindowInstance?.HideToTrayAtStartup();
        }
    }

    private void OnWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
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
