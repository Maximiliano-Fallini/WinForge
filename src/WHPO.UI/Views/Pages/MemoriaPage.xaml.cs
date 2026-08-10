using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        var minStandby = _settingsService.Get("memory.minStandbyMB", defaultMinStandby);
        var maxFree = _settingsService.Get("memory.maxFreeMB", defaultMaxFree);
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
            AutoCleanupButton.Content = "Detener";
            AutoCleanupStatusText.Text = $"Activo (standby ≥ {minStandby:F0} MB y libre ≤ {maxFree:F0} MB)";
        }
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

    // Muestra el resultado de la limpieza; con texto vacío colapsa el elemento para
    // no dejar espacio muerto entre el botón y el separador.
    private void SetResultText(TextBlock tb, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            tb.Visibility = Visibility.Collapsed;
            return;
        }
        tb.Visibility = Visibility.Visible;
        tb.Text = text;
    }

    private async void CleanStandbyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CleanStandbyButton.IsEnabled = false;
            SetResultText(CleanStandbyResultText, "Liberando memoria disponible...");

            var result = await _memoryService.CleanStandbyListAsync();

            SetResultText(CleanStandbyResultText, result.Success
                ? result.Output
                : $"Error: {result.Output}");

            await RefreshMemoryStatsAsync();
        }
        catch (Exception ex)
        {
            SetResultText(CleanStandbyResultText, $"Error: {ex.Message}");
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

            AutoCleanupStatusText.Text = $"Autoajuste listo: caché {recommendedMinStandby:F0} MB, RAM libre {recommendedMaxFree:F0} MB.";
        }
        catch (Exception ex)
        {
            AutoCleanupStatusText.Text = $"Error al autoajustar: {ex.Message}";
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

    private void AutoCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_autoCleanupActive)
            {
                // Detener
                _memoryService.StopAutoCleanup();
                _autoCleanupActive = false;
                AutoCleanupButton.Content = "Iniciar";
                AutoCleanupStatusText.Text = "Detenido";
                _settingsService.Set("memory.autoStart", false);
                _settingsService.Save();
                return;
            }

            // Validar valores
            if (!double.TryParse(MinStandbyTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double minStandby) || minStandby < 0)
            {
                AutoCleanupStatusText.Text = "Ingrese un valor válido para la lista en espera mínima.";
                return;
            }
            if (!double.TryParse(MaxFreeTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double maxFree) || maxFree < 0)
            {
                AutoCleanupStatusText.Text = "Ingrese un valor válido para la RAM libre máxima.";
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
            AutoCleanupButton.Content = "Detener";
            AutoCleanupStatusText.Text = $"Activo (standby ≥ {minStandby:F0} MB y libre ≤ {maxFree:F0} MB)";
        }
        catch (Exception ex)
        {
            AutoCleanupStatusText.Text = $"Error: {ex.Message}";
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
