using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private async void TimerResolutionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_timerResolutionActive)
            {
                // Detener: restablecer resolución
                var result = await _memoryService.ResetTimerResolutionAsync();
                _timerResolutionActive = false;
                TimerResolutionButton.Content = "Iniciar";
                TimerResolutionResultText.Text = result.Success
                    ? result.Output
                    : $"Error: {result.Output}";

                // Actualizar resolución actual después de detener
                var currentRes = _memoryService.GetCurrentTimerResolution();
                CurrentTimerResolutionText.Text = $"{currentRes / 10000.0:F3} ms";
                return;
            }

            // Validar resolución deseada (usar InvariantCulture para soportar punto decimal)
            if (!double.TryParse(DesiredResolutionTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double desiredMs) || desiredMs <= 0)
            {
                TimerResolutionResultText.Text = "Ingrese una resolución válida en ms.";
                return;
            }

            // Convertir ms a 100ns units
            int resolution100ns = (int)(desiredMs * 10000);

            // Validar contra la resolución máxima (mínimo valor numérico = mejor resolución)
            var maxRes = _memoryService.GetMaximumTimerResolution();
            if (resolution100ns < maxRes)
            {
                TimerResolutionResultText.Text = $"La resolución deseada no puede ser menor que la máxima ({maxRes / 10000.0:F3} ms).";
                return;
            }

            // Guardar configuración
            _settingsService.Set("memory.desiredResolutionMs", desiredMs);
            _settingsService.Save();

            var setResult = await _memoryService.SetTimerResolutionAsync(resolution100ns);

            if (setResult.Success)
            {
                _timerResolutionActive = true;
                TimerResolutionButton.Content = "Detener";
            }

            TimerResolutionResultText.Text = setResult.Success
                ? setResult.Output
                : $"Error: {setResult.Output}";

            // Actualizar resolución actual
            var current = _memoryService.GetCurrentTimerResolution();
            CurrentTimerResolutionText.Text = $"{current / 10000.0:F3} ms";
        }
        catch (Exception ex)
        {
            TimerResolutionResultText.Text = $"Error: {ex.Message}";
            _loggingService.LogError("Error en TimerResolutionButton_Click", ex);
        }
    }

    private async void ResetTimerResolutionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResetTimerResolutionButton.IsEnabled = false;
            TimerResolutionResultText.Text = "Restableciendo resolución del temporizador...";

            var result = await _memoryService.ResetTimerResolutionAsync();

            _timerResolutionActive = false;
            TimerResolutionButton.Content = "Iniciar";

            TimerResolutionResultText.Text = result.Success
                ? result.Output
                : $"Error: {result.Output}";

            // Actualizar resolución actual
            var current = _memoryService.GetCurrentTimerResolution();
            CurrentTimerResolutionText.Text = $"{current / 10000.0:F3} ms";
        }
        catch (Exception ex)
        {
            TimerResolutionResultText.Text = $"Error: {ex.Message}";
            _loggingService.LogError("Error en ResetTimerResolutionButton_Click", ex);
        }
        finally
        {
            ResetTimerResolutionButton.IsEnabled = true;
        }
    }
}
