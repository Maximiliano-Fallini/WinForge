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
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refreshTimer;

    public TemporizadorPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _memoryService = App.Services.GetRequiredService<IMemoryService>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        Loaded += OnLoaded;
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
                MinTimerResolutionText.Text = $"{min / 10000.0:F3} ms";
                MaxTimerResolutionText.Text = $"{max / 10000.0:F3} ms";
            });
        });

        // Cargar resolución deseada guardada
        var desiredRes = _settingsService.Get("memory.desiredResolutionMs", 0.5);
        DesiredResolutionTextBox.Text = $"{desiredRes:F1}";

        // Actualizar la resolución actual en vivo cada 2 segundos
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += (s, e) =>
        {
            // No consultar en segundo plano mientras la ventana está oculta en bandeja
            if (App.MainWindowInstance is { } w && !w.IsWindowVisible) return;
            var current = _memoryService.GetCurrentTimerResolution();
            CurrentTimerResolutionText.Text = $"{current / 10000.0:F3} ms";
        };
        _refreshTimer.Start();

        // Si la resolución quedó iniciada en la sesión anterior, reflejarlo en la UI
        // (MainWindow ya la reaplicó al arrancar la app).
        if (_settingsService.Get("timer.autoStart", false))
        {
            _timerResolutionActive = true;
            SetTimerButtonState(true);
        }
        UpdateInputsEnabled();

        _loggingService.LogInfo("TemporizadorPage: datos cargados");
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Reanudar el refresco de la resolución al volver a la página
        // (NavigationCacheMode.Enabled no vuelve a pasar por OnLoaded).
        if (_dataLoaded)
            _refreshTimer?.Start();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _refreshTimer?.Stop();
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

    // Estado del botón Iniciar/Detener: Detener usa rojo (igual que "Detener test"
    // en Estabilidad) para que la acción de parar se vea igual en toda la app.
    private void SetTimerButtonState(bool running)
    {
        TimerResolutionButton.Content = running ? "Detener" : "Iniciar";
        if (running)
        {
            TimerResolutionButton.Background = Feedback.ErrorBrush;
            TimerResolutionButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            TimerResolutionButton.Background = (SolidColorBrush)App.Current.Resources["AccentBrush"];
            TimerResolutionButton.Foreground = (SolidColorBrush)App.Current.Resources["AccentForegroundBrush"];
        }
    }

    private async void TimerResolutionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_timerResolutionActive)
            {
                // Detener: restablecer resolución
                var result = await _memoryService.ResetTimerResolutionAsync();
                _timerResolutionActive = false;
                SetTimerButtonState(false);
                UpdateInputsEnabled();
                _settingsService.Set("timer.autoStart", false);
                _settingsService.Save();
                if (result.Success)
                    Feedback.Success(TimerResolutionResultText, result.Output);
                else
                    Feedback.Error(TimerResolutionResultText, result.Output);

                // Actualizar resolución actual después de detener
                var currentRes = _memoryService.GetCurrentTimerResolution();
                CurrentTimerResolutionText.Text = $"{currentRes / 10000.0:F3} ms";
                return;
            }

            // Validar resolución deseada (usar InvariantCulture para soportar punto decimal)
            if (!double.TryParse(DesiredResolutionTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double desiredMs) || desiredMs <= 0)
            {
                Feedback.Error(TimerResolutionResultText, "Ingrese una resolución válida en ms.");
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
                Feedback.Error(TimerResolutionResultText, $"La resolución deseada no puede ser más fina que {finestRes / 10000.0:F3} ms (la máxima que soporta el sistema).");
                return;
            }
            if (resolution100ns > coarsestRes)
            {
                Feedback.Error(TimerResolutionResultText, $"La resolución deseada no puede ser más gruesa que {coarsestRes / 10000.0:F3} ms (la mínima que soporta el sistema).");
                return;
            }

            // Guardar configuración
            _settingsService.Set("memory.desiredResolutionMs", desiredMs);
            _settingsService.Save();

            var setResult = await _memoryService.SetTimerResolutionAsync(resolution100ns);

            if (setResult.Success)
            {
                _timerResolutionActive = true;
                SetTimerButtonState(true);
                UpdateInputsEnabled();
                // Recordar que se arrancó: se reaplica al abrir la app la próxima vez.
                _settingsService.Set("timer.autoStart", true);
                _settingsService.Save();
            }

            if (setResult.Success)
                Feedback.Success(TimerResolutionResultText, setResult.Output);
            else
                Feedback.Error(TimerResolutionResultText, setResult.Output);

            // Actualizar resolución actual
            var current = _memoryService.GetCurrentTimerResolution();
            CurrentTimerResolutionText.Text = $"{current / 10000.0:F3} ms";
        }
        catch (Exception ex)
        {
            Feedback.Error(TimerResolutionResultText, ex.Message);
            _loggingService.LogError("Error en TimerResolutionButton_Click", ex);
        }
    }

    // Autoajustar: pone la resolución recomendada (0,5 ms, la mejor para juegos/audio)
    // en el campo. No inicia: el usuario le da Iniciar después, igual que en la limpieza.
    private void AutoajustarTimerButton_Click(object sender, RoutedEventArgs e)
    {
        DesiredResolutionTextBox.Text = "0.5";
        Feedback.Success(TimerResolutionResultText, "Autoajuste listo: resolución deseada 0,5 ms.");
    }
}
