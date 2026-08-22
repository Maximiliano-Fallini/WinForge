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
    // Pinceles desde los recursos de tema de la app (claro/oscuro).
    private static Microsoft.UI.Xaml.Media.SolidColorBrush CardBrush => ThemeBrushes.Get("CardBackgroundBrush");

    private DateTime? _lastCleanupTime;

    // Preajustes de la limpieza automática (desplegable "Preajuste").
    private enum AutoCleanupPreset { Liviano, Normal, Agresivo }

    private ComboBoxItem _presetLightItem = null!;
    private ComboBoxItem _presetNormalItem = null!;
    private ComboBoxItem _presetAggressiveItem = null!;
    // Ítem "Personalizado": estado del combo cuando los umbrales se editaron a mano
    // y no coinciden con ningún preajuste (seleccionarlo no aplica nada).
    private ComboBoxItem _presetCustomItem = null!;

    // Mientras se inicializa el combo (o se restauran valores guardados) no se
    // aplica ningún preajuste: los umbrales ya cargados mandan.
    private bool _suppressPresetApply;

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

        // Inicializar ComboBox de tasa de sondeo y de preajuste
        InitializePollIntervalCombo();
        InitializePresetCombo();

        // Los ítems del desplegable de preajuste se crean en código: se re-traducen
        // al cambiar de idioma (la página queda en caché, no hace falta desuscribirse).
        I18n.LanguageChanged += OnLanguageChanged;

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

    private void InitializePresetCombo()
    {
        PresetCombo.Items.Clear();
        _presetLightItem = new ComboBoxItem { Content = "Liviano", Tag = AutoCleanupPreset.Liviano };
        _presetNormalItem = new ComboBoxItem { Content = "Normal", Tag = AutoCleanupPreset.Normal };
        _presetAggressiveItem = new ComboBoxItem { Content = "Agresivo", Tag = AutoCleanupPreset.Agresivo };
        _presetCustomItem = new ComboBoxItem { Content = "Personalizado", Tag = null };
        PresetCombo.Items.Add(_presetLightItem);
        PresetCombo.Items.Add(_presetNormalItem);
        PresetCombo.Items.Add(_presetAggressiveItem);
        PresetCombo.Items.Add(_presetCustomItem);

        // Normal por defecto. No aplica nada: se respetan los valores guardados.
        _suppressPresetApply = true;
        PresetCombo.SelectedIndex = 1;
        _suppressPresetApply = false;
    }

    // Los ComboBoxItem no se realizan en el árbol visual hasta abrir el desplegable,
    // así que el recorrido de I18n no los alcanza: se traducen por código (mismo
    // patrón que los ítems del tema en Configuración).
    private void OnLanguageChanged()
    {
        _presetLightItem.Content = I18n.T("Liviano");
        _presetNormalItem.Content = I18n.T("Normal");
        _presetAggressiveItem.Content = I18n.T("Agresivo");
        _presetCustomItem.Content = I18n.T("Personalizado");
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetApply) return;
        if (PresetCombo.SelectedItem is not ComboBoxItem { Tag: AutoCleanupPreset preset }) return;
        ApplyPreset(preset);
    }

    // Los umbrales cambiaron (a mano o al aplicar un preajuste): el combo refleja
    // si los valores actuales coinciden con algún preajuste o si son personalizados.
    private void ThresholdTextChanged(object sender, TextChangedEventArgs e)
        => RefreshPresetComboFromValues();

    /// <summary>
    /// Fuente de verdad del desplegable: compara los umbrales actuales con los de
    /// cada preajuste (según la RAM instalada). Si coinciden, selecciona ese preajuste
    /// (sin re-aplicar); si no, selecciona "Personalizado". Sin esto, editar los
    /// valores a mano dejaba el combo mostrando el último preajuste elegido.
    /// </summary>
    private void RefreshPresetComboFromValues()
    {
        if (PresetCombo == null || PresetCombo.Items.Count == 0) return;
        if (!double.TryParse(MinStandbyTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double min) ||
            !double.TryParse(MaxFreeTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double max))
            return;

        double totalRamMB;
        try
        {
            var stats = _memoryService.GetMemoryStats();
            totalRamMB = Math.Max(8192, (int)Math.Round((double)stats.TotalPhysicalMB, MidpointRounding.AwayFromZero));
        }
        catch { return; }

        ComboBoxItem target = _presetCustomItem;
        foreach (var (preset, item) in new[]
        {
            (AutoCleanupPreset.Liviano, _presetLightItem),
            (AutoCleanupPreset.Normal, _presetNormalItem),
            (AutoCleanupPreset.Agresivo, _presetAggressiveItem)
        })
        {
            var (pm, pf) = GetPresetValues(totalRamMB, preset);
            if (Math.Abs(min - pm) < 0.001 && Math.Abs(max - pf) < 0.001)
            {
                target = item;
                break;
            }
        }

        if (!ReferenceEquals(PresetCombo.SelectedItem, target))
        {
            _suppressPresetApply = true;
            PresetCombo.SelectedIndex = PresetCombo.Items.IndexOf(target);
            _suppressPresetApply = false;
        }
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

        // El desplegable se restaura solo: los TextChanged de los umbrales disparan
        // RefreshPresetComboFromValues, que selecciona el preajuste que coincide con
        // los valores guardados (o "Personalizado" si se editaron a mano).

        // Si la limpieza quedó iniciada en la sesión anterior, reflejarlo en la UI
        // (MainWindow ya la reinició al arrancar la app).
        if (_settingsService.Get("memory.autoStart", false))
        {
            _autoCleanupActive = true;
            SetAutoCleanupButtonState(true);
            Feedback.Success(AutoCleanupStatusText, I18n.T("Activo (standby ≥ {0} MB y libre ≤ {1} MB)", $"{minStandby:F0}", $"{maxFree:F0}"), persistent: true);
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
                Text = I18n.T("[{0}] {1}: {2} MB liberados", e.Timestamp.ToString("HH:mm:ss"), e.IsAutomatic ? I18n.T("Automática") : I18n.T("Manual"), $"{e.FreedMB:F1}"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
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

    // Los parámetros de la limpieza automática (umbrales, frecuencia y preajuste) no
    // se pueden modificar mientras está iniciada.
    private void UpdateInputsEnabled()
    {
        bool editable = !_autoCleanupActive;
        MinStandbyTextBox.IsEnabled = editable;
        MaxFreeTextBox.IsEnabled = editable;
        PollIntervalCombo.IsEnabled = editable;
        PresetCombo.IsEnabled = editable;
    }

    private async void CleanStandbyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CleanStandbyButton.IsEnabled = false;
            Feedback.Running(CleanStandbyResultText, "Liberando memoria disponible...");

            var result = await _memoryService.CleanStandbyListAsync();

            if (result.Success)
                Feedback.Result(CleanStandbyResultText, result);
            else
                Feedback.Result(CleanStandbyResultText, result);

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

    // Aplica un preajuste: rellena los umbrales según el tamaño de RAM instalada.
    // No inicia la limpieza: el usuario le da Iniciar después (igual que Autoajustar).
    private void ApplyPreset(AutoCleanupPreset preset)
    {
        try
        {
            var stats = _memoryService.GetMemoryStats();
            var totalRamMB = (int)Math.Round((double)stats.TotalPhysicalMB, MidpointRounding.AwayFromZero);

            var (minStandby, maxFree) = GetPresetValues(totalRamMB, preset);

            MinStandbyTextBox.Text = $"{minStandby:F0}";
            MaxFreeTextBox.Text = $"{maxFree:F0}";

            Feedback.Success(AutoCleanupStatusText, I18n.T("Preajuste aplicado: caché {0} MB, RAM libre {1} MB.", $"{minStandby:F0}", $"{maxFree:F0}"));
        }
        catch (Exception ex)
        {
            Feedback.Error(AutoCleanupStatusText, I18n.T("Autoajuste falló: {0}", ex.Message));
            _loggingService.LogError("Error al aplicar preajuste de limpieza automática", ex);
        }
    }

    // Valores recomendados por tamaño de RAM (base = Normal): caché mínima a partir de
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

    // Umbrales según el preajuste, escalados al tamaño de RAM instalada. La base es
    // la recomendación actual (Normal): Liviano limpia poco seguido (caché alta y RAM
    // muy baja) y Agresivo limpia seguido (caché baja y RAM libre alta).
    private static (double MinStandby, double MaxFree) GetPresetValues(double totalRamMB, AutoCleanupPreset preset)
    {
        var (baseMin, baseMax) = GetRecommendedValues(totalRamMB);
        double Cap(double v) => Math.Round(Math.Max(MinThresholdMB, v), MidpointRounding.AwayFromZero);
        return preset switch
        {
            AutoCleanupPreset.Liviano => (Cap(baseMin * 2), Cap(baseMax * 0.5)),
            AutoCleanupPreset.Agresivo => (Cap(baseMin * 0.5), Cap(baseMax * 2)),
            _ => (Cap(baseMin), Cap(baseMax))
        };
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
                Feedback.Error(AutoCleanupStatusText, I18n.T("No se pudo iniciar: los valores mínimos son {0} MB (lista en espera) y {0} MB (RAM libre).", $"{MinThresholdMB:F0}"));
                return;
            }
            if (!double.TryParse(MaxFreeTextBox.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double maxFree) || maxFree < MinThresholdMB)
            {
                Feedback.Error(AutoCleanupStatusText, I18n.T("No se pudo iniciar: los valores mínimos son {0} MB (lista en espera) y {0} MB (RAM libre).", $"{MinThresholdMB:F0}"));
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
            Feedback.Success(AutoCleanupStatusText, I18n.T("Activo (standby ≥ {0} MB y libre ≤ {1} MB)", $"{minStandby:F0}", $"{maxFree:F0}"), persistent: true);
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
