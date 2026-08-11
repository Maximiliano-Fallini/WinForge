using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Dispatching;
using Microsoft.Extensions.DependencyInjection;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Controls;

namespace WHPO_UI.Views.Pages;

public sealed partial class MemoriaPage : Page
{
    private readonly IMemoryService _memoryService;
    private readonly ISettingsService _settingsService;
    private readonly ILoggingService _loggingService;
    private bool _dataLoaded;
    private bool _autoCleanupActive;
    private DispatcherQueueTimer? _memoryStatsTimer;

    // Cards de log oscuras en ambos temas (paneles oscuros) con texto claro explícito.
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush LightTextBrush = new(Windows.UI.Color.FromArgb(255, 0xE8, 0xEA, 0xED));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush CardBrush = new(Windows.UI.Color.FromArgb(255, 0x26, 0x2A, 0x31));

    private DateTime? _lastCleanupTime;

    public MemoriaPage()
    {
        try
        {
            InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
            _memoryService = App.Services.GetRequiredService<IMemoryService>();
            _settingsService = App.Services.GetRequiredService<ISettingsService>();
            _loggingService = App.Services.GetRequiredService<ILoggingService>();
            Loaded += OnLoaded;
        }
        catch (Exception ex)
        {
            if (DebugText != null)
                DebugText.Text = $"Error init: {ex.Message}";
            try { _loggingService?.LogError($"Error en constructor MemoriaPage: {ex}", ex); } catch { }
        }
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
            DebugText.Text = $"Error: {ex.Message}";
            try { _loggingService.LogError($"Error en OnLoaded MemoriaPage: {ex}", ex); } catch { }
        }
    }

    private async Task LoadDataAsync()
    {
        _loggingService.LogInfo("MemoriaPage: cargando datos...");

        // Inicializar ComboBox de tasa de sondeo
        InitializePollIntervalCombo();

        // Cargar estadísticas de memoria
        await RefreshMemoryStatsAsync();

        // Cargar configuración guardada
        LoadSavedSettings();

        // Suscribirse al evento de limpieza
        _memoryService.StandbyCleanupCompleted += OnStandbyCleanupCompleted;

        // Iniciar timer para actualizar estadísticas de memoria cada 2 segundos
        _memoryStatsTimer = DispatcherQueue.CreateTimer();
        _memoryStatsTimer.Interval = TimeSpan.FromSeconds(2);
        _memoryStatsTimer.Tick += async (s, e) =>
        {
            // No consultar WMI en segundo plano mientras la ventana está oculta en bandeja
            if (App.MainWindowInstance is { } w && !w.IsWindowVisible) return;
            await RefreshMemoryStatsAsync();
        };
        _memoryStatsTimer.Start();

        _loggingService.LogInfo("MemoriaPage: datos cargados");
    }

    private void InitializePollIntervalCombo()
    {
        PollIntervalCombo.Items.Clear();
        PollIntervalCombo.Items.Add("100 ms");
        PollIntervalCombo.Items.Add("250 ms");
        PollIntervalCombo.Items.Add("500 ms");
        PollIntervalCombo.Items.Add("1000 ms");
        PollIntervalCombo.Items.Add("2000 ms");
        PollIntervalCombo.Items.Add("5000 ms");
        PollIntervalCombo.SelectedIndex = 3; // 1000 ms por defecto
    }

    private void LoadSavedSettings()
    {
        // Cargar configuración de limpieza automática
        var stats = _memoryService.GetMemoryStats();
        var totalRamMB = Math.Max(8192, (int)Math.Round((double)stats.TotalPhysicalMB, MidpointRounding.AwayFromZero));
        var (defaultMinStandby, defaultMaxFree) = GetRecommendedValues(totalRamMB);
        // Mínimo 300 MB en ambos umbrales: valores guardados más chicos (ej. 0)
        // se suben al mínimo para no arrancar la limpieza con umbrales inválidos.
        var minStandby = Math.Max(300, _settingsService.Get("memory.minStandbyMB", defaultMinStandby));
        var maxFree = Math.Max(300, _settingsService.Get("memory.maxFreeMB", defaultMaxFree));
        MinStandbyTextBox.Text = $"{minStandby:F0}";
        MaxFreeTextBox.Text = $"{maxFree:F0}";

        // Restaurar la frecuencia de comprobación guardada
        int savedPollMs = _settingsService.Get("memory.pollIntervalMs", 1000);
        int pollIndex = PollIntervalCombo.Items.IndexOf($"{savedPollMs} ms");
        if (pollIndex >= 0)
            PollIntervalCombo.SelectedIndex = pollIndex;

        // Si la limpieza quedó iniciada en la sesión anterior, reflejarlo en la UI
        // (MainWindow ya la reinició al arrancar la app).
        if (_settingsService.Get("memory.autoStart", false))
        {
            _autoCleanupActive = true;
            SetAutoCleanupButtonState(true);
            Feedback.Success(AutoCleanupStatusText, $"Activo (standby ≥ {minStandby:F0} MB y libre ≤ {maxFree:F0} MB)", persistent: true);
        }
        UpdateInputsEnabled();
    }

    private async Task RefreshMemoryStatsAsync()
    {
        try
        {
            var stats = await Task.Run(() => _memoryService.GetMemoryStats());
            var pageFile = await Task.Run(() => _memoryService.GetPageFileStats());

            TotalMemoryText.Text = $"{stats.TotalPhysicalMB:F0} MB";
            StandbySizeText.Text = $"{stats.StandbyMB:F1} MB";
            PageFileText.Text = $"{pageFile.UsedPageFileMB:F0} / {pageFile.TotalPageFileMB:F0} MB";
            FreeMemoryText.Text = $"{stats.FreeMB:F0} MB";
            AvailableMemoryText.Text = $"{stats.AvailableMB:F0} MB";

            // Con la limpieza activa, mostrar en vivo por qué (no) limpia: caché y libre
            // actuales contra los umbrales. Verde cuando ambas condiciones se cumplen.
            if (_autoCleanupActive &&
                double.TryParse(MinStandbyTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double minStandby) &&
                double.TryParse(MaxFreeTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double maxFree))
            {
                bool cacheOk = stats.StandbyMB >= minStandby;
                bool freeOk = stats.FreeMB <= maxFree;
                Feedback.Set(AutoCleanupStatusText,
                    $"Activo · caché {stats.StandbyMB:F0} MB (≥ {minStandby:F0} {(cacheOk ? "✓" : "✗")}) · libre {stats.FreeMB:F0} MB (≤ {maxFree:F0} {(freeOk ? "✓" : "✗")})",
                    cacheOk && freeOk ? Feedback.SuccessBrush : Feedback.MutedBrush,
                    persistent: true);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error actualizando estadísticas de memoria", ex);
        }
    }

    private void OnStandbyCleanupCompleted(object? sender, StandbyCleanupEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _lastCleanupTime = e.Timestamp;
            LastCleanupText.Text = e.Timestamp.ToString("HH:mm:ss");

            var logEntry = new Border
            {
                Background = CardBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 2, 0, 2)
            };
            var logText = new TextBlock
            {
                Text = $"[{e.Timestamp:HH:mm:ss}] {(e.IsAutomatic ? "Automática" : "Manual")}: {e.FreedMB:F1} MB liberados",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = LightTextBrush
            };
            logEntry.Child = logText;
            AutoCleanupLogPanel.Children.Insert(0, logEntry);

            // Limitar a 10 entradas
            while (AutoCleanupLogPanel.Children.Count > 10)
            {
                AutoCleanupLogPanel.Children.RemoveAt(AutoCleanupLogPanel.Children.Count - 1);
            }

            // Actualizar estadísticas después de la limpieza
            _ = RefreshMemoryStatsAsync();
        });
    }

    private void NumericTextBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        // Solo permitir números y punto decimal
        args.Cancel = args.NewText.Any(c => c != '.' && !char.IsDigit(c));
    }

    // Mínimo 300 MB en los umbrales de la limpieza automática: no se arranca con un
    // valor menor y se avisa cuáles son los mínimos (300 y 300 MB).
    private const double MinThresholdMB = 300;

    // Los parámetros de la limpieza automática (umbrales, frecuencia y Autoajustar)
    // no se pueden modificar mientras está iniciada.
    private void UpdateInputsEnabled()
    {
        bool editable = !_autoCleanupActive;
        MinStandbyTextBox.IsEnabled = editable;
        MaxFreeTextBox.IsEnabled = editable;
        PollIntervalCombo.IsEnabled = editable;
        AutoConfigureButton.IsEnabled = editable;
    }

    private async void CleanStandbyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CleanStandbyButton.IsEnabled = false;
            Feedback.Running(CleanStandbyResultText, "Liberando memoria disponible...");

            var result = await _memoryService.CleanStandbyListAsync();

            if (result.Success)
                Feedback.Success(CleanStandbyResultText, result.Output);
            else
                Feedback.Error(CleanStandbyResultText, result.Output);

            await RefreshMemoryStatsAsync();
        }
        catch (Exception ex)
        {
            Feedback.Error(CleanStandbyResultText, ex.Message);
            _loggingService.LogError("Error en CleanStandbyButton_Click", ex);
        }
        finally
        {
            CleanStandbyButton.IsEnabled = true;
            // Devolver el foco al botón: al deshabilitarlo durante la limpieza, WinUI
            // mueve el foco al siguiente control (el TextBox "Activar cuando la caché
            // alcance") y el cursor queda "listo para escribir" ahí.
            CleanStandbyButton.Focus(FocusState.Programmatic);
        }
    }

    private void AutoConfigureButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var stats = _memoryService.GetMemoryStats();
            var totalRamMB = (int)Math.Round((double)stats.TotalPhysicalMB, MidpointRounding.AwayFromZero);

            var (recommendedMinStandby, recommendedMaxFree) = GetRecommendedValues(totalRamMB);

            MinStandbyTextBox.Text = $"{recommendedMinStandby:F0}";
            MaxFreeTextBox.Text = $"{recommendedMaxFree:F0}";

            Feedback.Success(AutoCleanupStatusText, $"Autoajuste listo: caché {recommendedMinStandby:F0} MB, RAM libre {recommendedMaxFree:F0} MB.");
        }
        catch (Exception ex)
        {
            Feedback.Error(AutoCleanupStatusText, $"Autoajuste falló: {ex.Message}");
            _loggingService.LogError("Error en AutoConfigureButton_Click", ex);
        }
    }

    // Valores recomendados por tamaño de RAM (Autoajustar): caché mínima a partir de
    // la cual limpiar y RAM libre máxima por debajo de la cual limpiar.
    private static readonly (int RamMB, double MinStandby, double MaxFree)[] RecommendedTiers =
    {
        (4 * 1024, 512, 512),
        (8 * 1024, 1024, 1024),
        (16 * 1024, 1024, 2048),
        (32 * 1024, 2048, 4096),
        (64 * 1024, 4096, 8192)
    };

    private static (double MinStandby, double MaxFree) GetRecommendedValues(double totalRamMB)
    {
        // Se usa el escalón más grande que no supere la RAM instalada
        // (mínimo el de 4 GB; por encima de 64 GB se usa el de 64 GB).
        var tier = RecommendedTiers.LastOrDefault(t => t.RamMB <= totalRamMB);
        if (tier.RamMB == 0) tier = RecommendedTiers[0];
        return (tier.MinStandby, tier.MaxFree);
    }

    // Estado del botón Iniciar/Detener de la limpieza automática: Detener usa rojo
    // (igual que "Detener test" en Estabilidad) para que la acción de parar se vea
    // igual en toda la app; Iniciar vuelve al estilo por defecto.
    private void SetAutoCleanupButtonState(bool running)
    {
        AutoCleanupButton.Content = running ? "Detener" : "Iniciar";
        if (running)
        {
            AutoCleanupButton.Background = Feedback.ErrorBrush;
            AutoCleanupButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            AutoCleanupButton.ClearValue(Button.BackgroundProperty);
            AutoCleanupButton.ClearValue(Button.ForegroundProperty);
        }
    }

    private void AutoCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_autoCleanupActive)
            {
                // Detener
                _memoryService.StopAutoCleanup();
                _autoCleanupActive = false;
                SetAutoCleanupButtonState(false);
                Feedback.Set(AutoCleanupStatusText, "Detenido", Feedback.MutedBrush, persistent: true);
                UpdateInputsEnabled();
                _settingsService.Set("memory.autoStart", false);
                _settingsService.Save();
                return;
            }

            // Validar valores: mínimo 300 MB en cada umbral. Si alguno no llega,
            // no se arranca y se avisan los mínimos (300 y 300 MB).
            if (!double.TryParse(MinStandbyTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double minStandby) || minStandby < MinThresholdMB)
            {
                Feedback.Error(AutoCleanupStatusText, $"No se pudo iniciar: los valores mínimos son {MinThresholdMB:F0} MB (lista en espera) y {MinThresholdMB:F0} MB (RAM libre).");
                return;
            }
            if (!double.TryParse(MaxFreeTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double maxFree) || maxFree < MinThresholdMB)
            {
                Feedback.Error(AutoCleanupStatusText, $"No se pudo iniciar: los valores mínimos son {MinThresholdMB:F0} MB (lista en espera) y {MinThresholdMB:F0} MB (RAM libre).");
                return;
            }

            // Obtener tasa de sondeo
            int pollIntervalMs = 1000;
            if (PollIntervalCombo.SelectedItem is string selected)
            {
                pollIntervalMs = int.Parse(selected.Replace(" ms", ""));
            }

            // Guardar configuración y recordar que se arrancó: se reaplica al abrir la app.
            _settingsService.Set("memory.minStandbyMB", minStandby);
            _settingsService.Set("memory.maxFreeMB", maxFree);
            _settingsService.Set("memory.pollIntervalMs", pollIntervalMs);
            _settingsService.Set("memory.autoStart", true);
            _settingsService.Save();

            // Iniciar
            _memoryService.StartAutoCleanup(minStandby, maxFree, pollIntervalMs);
            _autoCleanupActive = true;
            SetAutoCleanupButtonState(true);
            UpdateInputsEnabled();
            Feedback.Success(AutoCleanupStatusText, $"Activo (standby ≥ {minStandby:F0} MB y libre ≤ {maxFree:F0} MB)", persistent: true);
        }
        catch (Exception ex)
        {
            Feedback.Error(AutoCleanupStatusText, ex.Message);
            _loggingService.LogError("Error en AutoCleanupButton_Click", ex);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Con NavigationCacheMode.Enabled la página no vuelve a pasar por OnLoaded:
        // reanudar el timer de estadísticas al volver (OnNavigatedFrom lo detuvo).
        if (_dataLoaded)
            _memoryStatsTimer?.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _memoryStatsTimer?.Stop();
    }
}
