using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

public sealed partial class TemporizadorPage : Page
{
    private readonly IMemoryService _memoryService;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private bool _dataLoaded;
    private bool _timerResolutionActive;
    private bool _suppressToggle; // evita reentrada al sincronizar los switches por código
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;

 // Windows 11 = build 22000+ (donde las peticiones de temporizador son por proceso
 // y el flag GlobalTimerResolutionRequest es el que hace que funcione).
    private static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public TemporizadorPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _memoryService = App.Services.GetRequiredService<IMemoryService>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        Loaded += OnLoaded;
        ApplyGlobalDesc();
        I18n.LanguageChanged += OnLanguageChanged;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // La página usa NavigationCacheMode.Enabled: el evento se re-suscribe en
        // OnNavigatedTo para no filtrar la instancia cacheada.
        I18n.LanguageChanged -= OnLanguageChanged;
        _refreshTimer?.Stop();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Reanudar el refresco de la resolución al volver a la página
        // (NavigationCacheMode.Enabled no vuelve a pasar por OnLoaded).
        if (_dataLoaded)
            _refreshTimer?.Start();
        I18n.LanguageChanged += OnLanguageChanged;
    }

    // Descripción del switch global: el remate "Requiere reiniciar..." va en amarillo
    // (WarningBrush) con Inlines, igual que el badge (BETA) de GestionarProcesos.
    // Se reconstruye al cambiar el idioma (el walker de I18n no traduce Inlines).
    private void ApplyGlobalDesc()
    {
        GlobalDescText.Inlines.Clear();
        GlobalDescText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = I18n.T("Activa la clave GlobalTimerResolutionRequests del kernel para que la petición aplique a todo el sistema. ")
        });
        GlobalDescText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = I18n.T("Requiere reiniciar"),
            Foreground = Feedback.WarningBrush
        });
    }

    private void OnLanguageChanged()
    {
        ApplyGlobalDesc();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_dataLoaded) return;
        try
        {
            await LoadDataAsync();
            _dataLoaded = true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error en OnLoaded TemporizadorPage: {ex}", ex);
        }
    }

    private async Task LoadDataAsync()
    {
        _loggingService.LogInfo("TemporizadorPage: cargando datos...");

        await Task.Run(() =>
        {
            var perfTimer = _memoryService.GetPerformanceTimerInfo();
            var current = _memoryService.GetCurrentTimerResolution();
            var min = _memoryService.GetMinimumTimerResolution();
            var max = _memoryService.GetMaximumTimerResolution();
            DispatcherQueue.TryEnqueue(() =>
            {
                PerformanceTimerText.Text = $"{perfTimer.Name} {perfTimer.FrequencyMHz:F0} MHz";
                CurrentTimerResolutionText.Text = $"{current / 10000.0:F3} ms";
 // Mín/máx son capacidades del sistema (siempre constantes): leerlas
 // directamente de NtQueryTimerResolution es correcto y más estable.
                MinTimerResolutionText.Text = $"{min / 10000.0:F3} ms";
                MaxTimerResolutionText.Text = $"{max / 10000.0:F3} ms";
            });
        });

        // Cargar resolución deseada guardada
        var desiredRes = _settingsService.Get("memory.desiredResolutionMs", 0.5);
        DesiredResolutionTextBox.Text = $"{desiredRes:F1}";

        // Switch global: refleja el estado REAL de la clave de registro del kernel
        // (el mecanismo que usa ). En Windows 11 se recomienda (badge verde); en
        // Windows 10 no aporta nada, así que se muestra deshabilitado con nota.
        var keyEnabled = await Task.Run(() => _memoryService.IsGlobalTimerResolutionRequestEnabled());
        _suppressToggle = true;
        GlobalTimerToggle.IsOn = IsWindows11 && keyEnabled;
        GlobalTimerToggle.IsEnabled = IsWindows11;
        GlobalRecommendedBadge.Visibility = IsWindows11 ? Visibility.Visible : Visibility.Collapsed;
        GlobalWin10Note.Visibility = IsWindows11 ? Visibility.Collapsed : Visibility.Visible;
        _suppressToggle = false;

        // Actualizar la resolución actual EN VIVO: lee la resolución efectiva real del
        // sistema (equivalente — se mide el tick, no la petición del propio proceso),
        // así reacciona cuando lo activa, o cuando un juego/ventana la fuerza a 1 ms.
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(500);
        _refreshTimer.Tick += (s, e) =>
        {
            // No consultar en segundo plano mientras la ventana está oculta en bandeja
            if (App.MainWindowInstance is { } w && !w.IsWindowVisible) return;
 // La medición tarda ~30-60 ms: no bloquear la UI.
            _ = Task.Run(() =>
            {
                var current = _memoryService.GetCurrentTimerResolution();
                DispatcherQueue.TryEnqueue(() =>
                    CurrentTimerResolutionText.Text = $"{current / 10000.0:F3} ms");
            });
        };
        _refreshTimer.Start();

        // El ajuste de resolución está siempre habilitado: no hay switch maestro.
        var timerRunningAtClose = _settingsService.Get("timer.autoStart", false);

 // Si la resolución quedó iniciada en la sesión anterior, reflejarlo en la UI
 // (MainWindow ya la reaplicó al arrancar la app).
        if (timerRunningAtClose)
        {
            SyncRunningState(true);
        }
        UpdateInputsEnabled();

        _loggingService.LogInfo("TemporizadorPage: datos cargados");
    }

    private void NumericTextBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        // Solo permitir números y punto decimal
        args.Cancel = args.NewText.Any(c => c != '.' && !char.IsDigit(c));
    }

    // La resolución deseada y el Autoajustar no se pueden usar mientras el ajuste
    // está iniciado (igual que en la limpieza automática).
    private void UpdateInputsEnabled()
    {
        DesiredResolutionTextBox.IsEnabled = !_timerResolutionActive;
        AutoajustarTimerButton.IsEnabled = !_timerResolutionActive;
    }

    // ===== Botón Iniciar/Detener =====
 // Es el control que arranca/detiene la resolución. La función está siempre
 // habilitada: no hay switch maestro previo.
    private async void TimerResolutionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_timerResolutionActive)
        {
            await StopTimerAsync();
            return;
        }

        await StartTimerAsync();
    }

 // Sincroniza el estado visual del botón con el estado real del temporizador.
 // Detener usa
 // rojo (igual que "Detener test" en Estabilidad).
    private void SyncRunningState(bool running)
    {
        _timerResolutionActive = running;
        TimerResolutionButton.Content = running ? "Detener" : "Iniciar";
        if (running)
        {
            TimerResolutionButton.Background = Feedback.ErrorBrush;
            TimerResolutionButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            TimerResolutionButton.Background = ThemeBrushes.Get("AccentBrush");
            TimerResolutionButton.Foreground = ThemeBrushes.Get("AccentForegroundBrush");
        }
        UpdateInputsEnabled();
    }

 // ===== Switch global: Usar GlobalTimerResolutionRequest =====
 // Igual que : este switch NO modifica la llamada a NtSetTimerResolution (que
 // siempre es una petición normal); escribe/borra la clave del kernel
 // GlobalTimerResolutionRequests, que se lee al boot y hace que las peticiones
 // normales se apliquen a todo el sistema.
    private async void GlobalTimerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;

        var enable = GlobalTimerToggle.IsOn;
        var result = await _memoryService.SetGlobalTimerResolutionRequestEnabledAsync(enable);

        if (!result.Success)
        {
 // Revertir el switch si no se pudo escribir (p. ej. sin permisos)
            _suppressToggle = true;
            GlobalTimerToggle.IsOn = !enable;
            _suppressToggle = false;
        }

        Feedback.Result(TimerResolutionResultText, result);
    }

    private async Task StartTimerAsync()
    {
        try
        {
            // Validar resolución deseada (usar InvariantCulture para soportar punto decimal)
            if (!double.TryParse(DesiredResolutionTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double desiredMs) || desiredMs <= 0)
            {
                Feedback.Error(TimerResolutionResultText, "Ingrese una resolución válida en ms.");
                SyncRunningState(false);
                return;
            }

            // Convertir ms a 100ns units
            int resolution100ns = (int)(desiredMs * 10000);

            // Validar rango: no más fina que la máxima posible (0,5 ms) ni más gruesa
            // que la mínima posible (15,625 ms). OJO: en NtQueryTimerResolution la
            // "máxima" es la más fina (valor numérico menor) y la "mínima" la más gruesa.
            var finestRes = _memoryService.GetMaximumTimerResolution();
            var coarsestRes = _memoryService.GetMinimumTimerResolution();
            if (resolution100ns < finestRes)
            {
                Feedback.Error(TimerResolutionResultText, I18n.T("La resolución deseada no puede ser más fina que {0} ms (la máxima que soporta el sistema).", $"{finestRes / 10000.0:F3}"));
                SyncRunningState(false);
                return;
            }
            if (resolution100ns > coarsestRes)
            {
                Feedback.Error(TimerResolutionResultText, I18n.T("La resolución deseada no puede ser más gruesa que {0} ms (la mínima que soporta el sistema).", $"{coarsestRes / 10000.0:F3}"));
                SyncRunningState(false);
                return;
            }

            // Guardar configuración
            _settingsService.Set("memory.desiredResolutionMs", desiredMs);
            _settingsService.Save();

            var setResult = await _memoryService.SetTimerResolutionAsync(resolution100ns);

            if (setResult.Success)
            {
                SyncRunningState(true);
 // Recordar que se arrancó: se reaplica al abrir la app la próxima vez.
                _settingsService.Set("timer.autoStart", true);
                _settingsService.Save();
            }
            else
            {
                SyncRunningState(false);
            }

            Feedback.Result(TimerResolutionResultText, setResult);

            // Actualizar resolución actual (medición fuera de la UI: tarda ~30-60 ms)
            var current = await Task.Run(() => _memoryService.GetCurrentTimerResolution());
            CurrentTimerResolutionText.Text = $"{current / 10000.0:F3} ms";
        }
        catch (Exception ex)
        {
            Feedback.Error(TimerResolutionResultText, ex.Message);
            _loggingService.LogError("Error iniciando resolución del temporizador", ex);
            SyncRunningState(false);
        }
    }

    private async Task StopTimerAsync()
    {
        try
        {
 // Detener: restablecer resolución
            var result = await _memoryService.ResetTimerResolutionAsync();
            SyncRunningState(false);
            _settingsService.Set("timer.autoStart", false);
            _settingsService.Save();
            Feedback.Result(TimerResolutionResultText, result);

 // Actualizar resolución actual después de detener (medición fuera de la UI)
            var currentRes = await Task.Run(() => _memoryService.GetCurrentTimerResolution());
            CurrentTimerResolutionText.Text = $"{currentRes / 10000.0:F3} ms";
        }
        catch (Exception ex)
        {
            Feedback.Error(TimerResolutionResultText, ex.Message);
            _loggingService.LogError("Error deteniendo resolución del temporizador", ex);
        }
    }

    // Autoajustar: pone la resolución recomendada (0,5 ms, la mejor para juegos/audio)
    // en el campo. No inicia: el usuario activa el switch después, igual que en la limpieza.
    private void AutoajustarTimerButton_Click(object sender, RoutedEventArgs e)
    {
        DesiredResolutionTextBox.Text = "0.5";
        Feedback.Success(TimerResolutionResultText, "Autoajuste listo: resolución deseada 0,5 ms.");
    }
}
