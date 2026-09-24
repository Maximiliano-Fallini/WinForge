using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinForge.Component.Benchmark.Graphics;
using WinForge.Component.Benchmark.Host;
using WinForge.Component.Benchmark.Metrics;
using WinForge.Component.Benchmark.Scenes;

namespace WinForge.Component.Benchmark;

/// <summary>
/// Página del componente: elegís API, escena, forma de presentación y duración, corrés la
/// escena (con las métricas en vivo) y quedás con un informe comparable.
///
/// Todo el texto sale de <see cref="AppBridge.T"/> y todos los pinceles de
/// <see cref="AppBridge.Brush"/>: la página se traduce y reacciona al tema como cualquier
/// otra pestaña de la app, aunque viva en un assembly cargado en caliente.
/// </summary>
public sealed class BenchmarkPage : Page
{
    // Presets de duración en SEGUNDOS (modelo 3DMark): la escena es un viaje y dos corridas de
    // la misma duración recorren exactamente el mismo camino, sin importar el FPS de la placa.
    private static readonly (string LabelKey, int Seconds)[] DurationPresets =
    {
        ("Corta (30 segundos)", 30),
        ("Estándar (60 segundos)", 60),
        ("Larga (120 segundos)", 120)
    };

    // Forma de presentación. Por defecto la ventana sin bordes: se ve como pantalla completa sin
    // sacar el modo de video del monitor (el exclusivo queda para quien quiera medir sin el
    // compositor de Windows en el medio, y puede fallar si otra app lo tiene tomado).
    private static readonly (string LabelKey, PresentationMode Mode)[] PresentationPresets =
    {
        ("Ventana", PresentationMode.Windowed),
        ("Pantalla completa sin bordes", PresentationMode.BorderlessFullscreen),
        ("Pantalla completa exclusiva", PresentationMode.ExclusiveFullscreen)
    };

    private readonly DispatcherQueue _dispatcher;
    private readonly List<Action> _retranslate = new();
    private SceneDefinition _scene = SceneCatalog.Default;

    private readonly ComboBox _apiCombo = new() { MinWidth = 220 };
    private readonly ComboBox _sceneCombo = new() { MinWidth = 220 };
    private readonly TextBlock _sceneText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _adapterCombo = new() { MinWidth = 320 };
    private readonly ComboBox _presentationCombo = new() { MinWidth = 240 };
    private readonly ComboBox _durationCombo = new() { MinWidth = 240 };
    private readonly ToggleSwitch _hudToggle = new() { IsOn = true };
    private readonly Button _startButton = new() { MinWidth = 150 };
    private readonly Button _stopButton = new() { MinWidth = 150, IsEnabled = false };

    private readonly TextBlock _apiAvailabilityText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _statusText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _liveFpsText = new() { FontSize = 26, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _liveFrameText = new() { FontSize = 26, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _liveGpuText = new() { FontSize = 26, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _liveProgressText = new() { FontSize = 12 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1 };

    private readonly StackPanel _machinePanel = new() { Spacing = 6 };
    private readonly StackPanel _reportPanel = new() { Spacing = 4 };
    private readonly StackPanel _warningsPanel = new() { Spacing = 4 };
    private readonly StackPanel _historyPanel = new() { Spacing = 6 };
    private readonly Button _copyButton = new() { IsEnabled = false };
    private readonly Button _openFolderButton = new();

    private readonly DispatcherQueueTimer _sensorTimer;
    private BenchmarkReport? _lastReport;
    private CancellationTokenSource? _cancellation;
    private bool _running;
    private bool _startedMetrics;

    public BenchmarkPage()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("La página necesita un DispatcherQueue.");
        Content = BuildLayout();

        // El muestreo de sensores de la página es solo de lectura y de 1 Hz: no compite con la
        // corrida (que muestrea aparte, dentro de su propio hilo).
        _sensorTimer = _dispatcher.CreateTimer();
        _sensorTimer.Interval = TimeSpan.FromSeconds(1);
        _sensorTimer.Tick += (_, _) => RefreshMachinePanel();

        Loaded += (_, _) =>
        {
            LoadApiOptions();
            LoadSceneOptions();
            LoadAdapterOptions();
            LoadPresentationOptions();
            LoadDurationOptions();
            _sensorTimer.Start();
            RefreshMachinePanel();
            RefreshHistory();
        };
        Unloaded += (_, _) => _sensorTimer.Stop();

        AppBridge.OnLanguageChanged(() => _dispatcher.TryEnqueue(() =>
        {
            foreach (var action in _retranslate.ToArray()) action();
            if (_lastReport != null) BuildReport(_lastReport);
        }));

        _startButton.Click += async (_, _) => await StartRunAsync();
        _stopButton.Click += (_, _) => _cancellation?.Cancel();
        _copyButton.Click += (_, _) => CopyReport();
        _openFolderButton.Click += (_, _) => OpenReportsFolder();
    }

    // =====================================================================
    // Armado de la interfaz
    // =====================================================================

    private UIElement BuildLayout()
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new FontIcon { Glyph = "\uE9D9", FontSize = 20 });
        var headerTexts = new StackPanel { Spacing = 2 };
        var title = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold };
        var subtitle = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        subtitle.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        headerTexts.Children.Add(title);
        headerTexts.Children.Add(subtitle);
        header.Children.Add(headerTexts);

        _retranslate.Add(() =>
        {
            title.Text = AppBridge.T("Benchmark de escenas 3D");
            subtitle.Text = AppBridge.T("Corré escenas 3D propias y mirá las métricas reales del equipo: FPS, percentiles, hitches, ms de GPU y de CPU por frame, temperaturas, frecuencias y potencias. Sin puntaje: cada número se explica solo.");
        });

        var mainPanel = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        mainPanel.Children.Add(header);
        mainPanel.Children.Add(BuildConfigurationCard());
        mainPanel.Children.Add(BuildLiveCard());
        mainPanel.Children.Add(BuildReportCard());
        mainPanel.Children.Add(BuildHistoryCard());

        // Traducir todo lo estático ahora (y en cada cambio de idioma).
        foreach (var action in _retranslate.ToArray()) action();

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = mainPanel
        };
    }

    private Border BuildConfigurationCard()
    {
        var panel = new StackPanel { Spacing = 10 };

        var apiRow = Row("API gráfica", _apiCombo);
        panel.Children.Add(apiRow);
        panel.Children.Add(Indented(_apiAvailabilityText));

        panel.Children.Add(Row("Escena", _sceneCombo));
        _sceneText.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        panel.Children.Add(Indented(_sceneText));

        panel.Children.Add(Row("Placa de video", _adapterCombo));
        panel.Children.Add(Row("Presentación", _presentationCombo));
        panel.Children.Add(Row("Duración", _durationCombo));
        panel.Children.Add(Row("Métricas sobre la escena", _hudToggle));

        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        hint.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        _retranslate.Add(() => hint.Text = AppBridge.T("Durante la corrida, Esc corta la medición y cierra la escena. El informe guarda lo medido hasta ahí."));
        panel.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        buttons.Children.Add(_startButton);
        buttons.Children.Add(_stopButton);
        panel.Children.Add(buttons);

        _statusText.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        panel.Children.Add(_statusText);
        _retranslate.Add(() => { _startButton.Content = AppBridge.T("Iniciar corrida"); _stopButton.Content = AppBridge.T("Detener"); });
        _statusText.Text = AppBridge.T("Listo para correr.");

        _startButton.Background = AppBridge.Brush("AccentBrush");
        _startButton.Foreground = AppBridge.Brush("AccentForegroundBrush");
        _startButton.CornerRadius = new CornerRadius(6);
        _startButton.Padding = new Thickness(16, 8, 16, 8);
        _stopButton.CornerRadius = new CornerRadius(6);
        _stopButton.Padding = new Thickness(16, 8, 16, 8);

        return Card(panel);
    }

    private Border BuildLiveCard()
    {
        var panel = new StackPanel { Spacing = 12 };

        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
        _retranslate.Add(() => title.Text = AppBridge.T("En vivo"));
        panel.Children.Add(title);

        var values = new Grid { ColumnSpacing = 24 };
        for (int i = 0; i < 3; i++) values.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        values.Children.Add(LabeledValue("FPS", _liveFpsText, 0));
        values.Children.Add(LabeledValue("Frame", _liveFrameText, 1));
        values.Children.Add(LabeledValue("GPU por frame", _liveGpuText, 2));
        panel.Children.Add(values);

        panel.Children.Add(_progress);
        panel.Children.Add(Indented(_liveProgressText));

        var machineTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        _retranslate.Add(() => machineTitle.Text = AppBridge.T("Estado del equipo"));
        panel.Children.Add(machineTitle);
        panel.Children.Add(_machinePanel);

        return Card(panel);
    }

    private Border BuildReportCard()
    {
        var panel = new StackPanel { Spacing = 10 };
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
        _retranslate.Add(() => title.Text = AppBridge.T("Resultado de la última corrida"));
        panel.Children.Add(title);

        var empty = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        empty.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        _retranslate.Add(() => empty.Text = AppBridge.T("Todavía no corriste ninguna escena en esta sesión."));
        panel.Children.Add(empty);
        _reportPanel.Tag = empty;   // se oculta cuando hay informe

        panel.Children.Add(_reportPanel);
        panel.Children.Add(_warningsPanel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _copyButton.CornerRadius = new CornerRadius(6);
        _openFolderButton.CornerRadius = new CornerRadius(6);
        _retranslate.Add(() => { _copyButton.Content = AppBridge.T("Copiar informe"); _openFolderButton.Content = AppBridge.T("Abrir carpeta de informes"); });
        buttons.Children.Add(_copyButton);
        buttons.Children.Add(_openFolderButton);
        panel.Children.Add(buttons);

        return Card(panel);
    }

    private Border BuildHistoryCard()
    {
        var panel = new StackPanel { Spacing = 8 };
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
        _retranslate.Add(() => title.Text = AppBridge.T("Corridas guardadas"));
        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        hint.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        _retranslate.Add(() => hint.Text = AppBridge.T("Cada corrida se guarda como un informe con la huella del equipo: dos informes solo se comparan si describen la misma máquina y la misma configuración."));
        panel.Children.Add(title);
        panel.Children.Add(hint);
        panel.Children.Add(_historyPanel);
        return Card(panel);
    }

    private static Border Card(UIElement child) => new()
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16),
        BorderThickness = new Thickness(1),
        BorderBrush = AppBridge.Brush("CardBorderBrush"),
        Child = child
    };

    private Grid Row(string labelKey, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _retranslate.Add(() => label.Text = AppBridge.T(labelKey));
        Grid.SetColumn(label, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(label);
        grid.Children.Add(control);
        return grid;
    }

    private static StackPanel Indented(FrameworkElement child)
    {
        var panel = new StackPanel { Margin = new Thickness(232, 0, 0, 0) };
        panel.Children.Add(child);
        return panel;
    }

    private StackPanel LabeledValue(string labelKey, TextBlock value, int column)
    {
        var label = new TextBlock { FontSize = 12 };
        _retranslate.Add(() => label.Text = AppBridge.T(labelKey));
        label.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(label);
        panel.Children.Add(value);
        Grid.SetColumn(panel, column);
        return panel;
    }

    // =====================================================================
    // Opciones
    // =====================================================================

    private void LoadApiOptions()
    {
        _apiCombo.Items.Clear();
        var availability = BackendRegistry.ProbeAll();

        foreach (var entry in availability)
        {
            var item = new ComboBoxItem
            {
                Content = entry.Available ? entry.ApiName : AppBridge.T("{0} (no disponible)", entry.ApiName),
                IsEnabled = entry.Available,
                Tag = entry.Api
            };
            _apiCombo.Items.Add(item);
            if (entry.Available) _apiCombo.SelectedItem ??= item;
        }

        // Lo que no está disponible se explica abajo: una opción gris sin motivo no se puede arreglar.
        var unavailable = availability.Where(a => !a.Available).ToList();
        _apiAvailabilityText.Text = unavailable.Count == 0
            ? AppBridge.T("Todas las APIs del plan están disponibles en este equipo.")
            : AppBridge.T("No disponibles en este equipo: {0}.", string.Join(" · ", unavailable.Select(a => $"{a.ApiName}: {a.Reason}")));
    }

    private void LoadSceneOptions()
    {
        _sceneCombo.Items.Clear();
        foreach (var scene in SceneCatalog.All)
        {
            // El nombre se guarda como texto fuente y se traduce acá: el idioma puede cambiar
            // después de armar la lista.
            _sceneCombo.Items.Add(new ComboBoxItem { Content = AppBridge.T(scene.Name), Tag = scene.Id });
        }
        _sceneCombo.SelectedIndex = 0;
        _sceneCombo.SelectionChanged += (_, _) => SelectScene();
        _retranslate.Add(() =>
        {
            for (int i = 0; i < _sceneCombo.Items.Count && i < SceneCatalog.All.Count; i++)
            {
                if (_sceneCombo.Items[i] is ComboBoxItem item) item.Content = AppBridge.T(SceneCatalog.All[i].Name);
            }
            _sceneText.Text = AppBridge.T(_scene.Description) + " " + AppBridge.T("El viaje es función del tiempo: dos corridas de la misma duración recorren exactamente el mismo escenario y se pueden comparar.");
        });
        SelectScene();
    }

    private void SelectScene()
    {
        if (_sceneCombo.SelectedItem is ComboBoxItem { Tag: string id }) _scene = SceneCatalog.Find(id) ?? SceneCatalog.Default;
        _sceneText.Text = AppBridge.T(_scene.Description) + " " + AppBridge.T("El viaje es función del tiempo: dos corridas de la misma duración recorren exactamente el mismo escenario y se pueden comparar.");
    }

    private void LoadAdapterOptions()
    {
        _adapterCombo.Items.Clear();
        _adapterCombo.Items.Add(new ComboBoxItem { Content = AppBridge.T("Automática (la de más memoria)"), Tag = -1 });
        foreach (var adapter in BackendRegistry.ListAdapters(GraphicsApi.D3D11))
        {
            // El nombre del adaptador no se traduce (es el que reporta el driver); el sufijo sí.
            string content = adapter.IsSoftware
                ? AppBridge.T("{0} (software)", adapter.Name)
                : AppBridge.T("{0} · {1} MB", adapter.Name, adapter.DedicatedVideoMemory / (1024 * 1024));
            _adapterCombo.Items.Add(new ComboBoxItem { Content = content, Tag = adapter.Index });
        }
        _adapterCombo.SelectedIndex = 0;
    }

    private void LoadPresentationOptions()
    {
        _presentationCombo.Items.Clear();
        foreach (var (labelKey, mode) in PresentationPresets)
        {
            _presentationCombo.Items.Add(new ComboBoxItem { Content = AppBridge.T(labelKey), Tag = mode });
        }
        _presentationCombo.SelectedIndex = 1;
    }

    private void LoadDurationOptions()
    {
        _durationCombo.Items.Clear();
        foreach (var (labelKey, seconds) in DurationPresets)
        {
            _durationCombo.Items.Add(new ComboBoxItem { Content = AppBridge.T(labelKey), Tag = seconds });
        }
        _durationCombo.SelectedIndex = 1;
    }

    // =====================================================================
    // Corrida
    // =====================================================================

    private async Task StartRunAsync()
    {
        if (_running) return;

        if (_apiCombo.SelectedItem is not ComboBoxItem { Tag: GraphicsApi api })
        {
            SetStatus(AppBridge.T("Elegí una API gráfica disponible."), warning: true);
            return;
        }
        if (!BackendRegistry.Probe(api).Available)
        {
            SetStatus(AppBridge.T("Esa API no está disponible en este equipo."), warning: true);
            return;
        }

        int durationSeconds = _durationCombo.SelectedItem is ComboBoxItem { Tag: int value } ? value : _scene.DefaultDurationSeconds;
        int adapterIndex = _adapterCombo.SelectedItem is ComboBoxItem { Tag: int index } ? index : -1;
        var presentation = SelectedPresentation();
        bool showHud = _hudToggle.IsOn;

        _running = true;
        _cancellation = new CancellationTokenSource();
        SetRunningUi(true);
        _progress.Value = 0;
        _liveFpsText.Text = "--";
        _liveFrameText.Text = "--";
        _liveGpuText.Text = "--";
        SetStatus(AppBridge.T("Preparando la escena…"), warning: false);

        // El muestreo de sensores de la app tiene que estar vivo para que el HUD y el informe
        // tengan temperatura, frecuencia y potencia. Si lo arrancamos nosotros, lo apagamos.
        _startedMetrics = AppBridge.EnsureMetricsRunning();
        var fingerprint = AppBridge.BuildFingerprint();
        var token = _cancellation.Token;

        var request = new SceneHost.RunRequest
        {
            Api = api,
            Scene = _scene,
            Presentation = presentation,
            DurationSeconds = durationSeconds,
            WarmupSeconds = _scene.WarmupSeconds,
            VSync = false,
            ShowWindow = true,
            ShowHud = showHud,
            ComponentVersion = "0.1.0",
            AppVersion = AppBridge.AppVersion,
            SensorProvider = AppBridge.ReadSensors,
            Fingerprint = fingerprint
        };

        var progress = new Progress<SceneHost.RunProgress>(report =>
            _dispatcher.TryEnqueue(() => UpdateLive(report, durationSeconds)));

        try
        {
            var result = await Task.Run(() => SceneHost.Run(request, progress, token), token).ConfigureAwait(true);
            _lastReport = result;
            BuildReport(result);
            _progress.Value = 1;
            SetStatus(result.Completed
                    ? AppBridge.T("Corrida completa: el informe quedó guardado en la carpeta de informes.")
                    : AppBridge.T("Corrida cortada: {0}", result.AbortReason ?? ""),
                warning: !result.Completed);
        }
        catch (OperationCanceledException)
        {
            SetStatus(AppBridge.T("Corrida cancelada."), warning: true);
        }
        catch (Exception ex)
        {
            // Sin acumular: acá se cuenta qué falló exactamente, no un "error inesperado".
            SetStatus(AppBridge.T("No se pudo correr la escena: {0}", ex.Message), warning: true);
        }
        finally
        {
            _running = false;
            _cancellation?.Dispose();
            _cancellation = null;
            SetRunningUi(false);
            if (_startedMetrics) AppBridge.StopMetricsIfWeStarted();
            _startedMetrics = false;
            RefreshHistory();
        }
    }

    private PresentationMode SelectedPresentation() =>
        _presentationCombo.SelectedItem is ComboBoxItem { Tag: PresentationMode mode }
            ? mode
            : PresentationMode.Windowed;

    private void SetRunningUi(bool running)
    {
        _startButton.IsEnabled = !running;
        _stopButton.IsEnabled = running;
        _apiCombo.IsEnabled = !running;
        _sceneCombo.IsEnabled = !running;
        _adapterCombo.IsEnabled = !running;
        _presentationCombo.IsEnabled = !running;
        _durationCombo.IsEnabled = !running;
        _hudToggle.IsEnabled = !running;
    }

    private void UpdateLive(SceneHost.RunProgress report, int durationSeconds)
    {
        _liveFpsText.Text = report.Fps > 0 ? $"{report.Fps:F0}" : "--";
        _liveFrameText.Text = report.FrameMs > 0 ? $"{report.FrameMs:F2} ms" : "--";
        _liveGpuText.Text = report.GpuMs > 0 ? $"{report.GpuMs:F2} ms" : "--";
        _progress.Value = durationSeconds > 0 ? Math.Clamp(report.ElapsedSeconds / durationSeconds, 0, 1) : 0;
        _liveProgressText.Text = AppBridge.T("{0:F0} s de {1:F0} s · {2} frames medidos",
            report.ElapsedSeconds, (double)durationSeconds, report.Frame);
    }

    private void SetStatus(string text, bool warning)
    {
        _statusText.Text = text;
        _statusText.Foreground = warning ? AppBridge.Brush("WarningBrush") : AppBridge.Brush("TextFillColorSecondaryBrush");
    }

    // =====================================================================
    // Informe
    // =====================================================================

    private void BuildReport(BenchmarkReport report)
    {
        // Al terminar la corrida el informe queda en disco para poder compararlo después.
        try
        {
            report.Save();
            BenchmarkStore.Trim();
        }
        catch { /* si no se pudo guardar, el informe igual está en pantalla */ }

        _copyButton.IsEnabled = true;
        if (_reportPanel.Tag is TextBlock empty) empty.Visibility = Visibility.Collapsed;

        var stats = report.Stats;
        _reportPanel.Children.Clear();

        if (!stats.HasData)
        {
            _reportPanel.Children.Add(Text(AppBridge.T("La corrida no dejó frames medidos.")));
            return;
        }

        // La escena y la presentación se guardan en español (texto fuente) y se traducen acá:
        // el mismo informe se puede volver a leer con otro idioma activo.
        string presentation = string.IsNullOrWhiteSpace(report.Presentation) ? "" : AppBridge.T(report.Presentation);
        AddReportRow("Escena", $"{AppBridge.T(report.SceneName)} · {report.Backend} · {report.Width}×{report.Height} · {presentation}");
        AddReportRow("Adaptador", string.IsNullOrWhiteSpace(report.AdapterDetail) ? report.AdapterName : $"{report.AdapterName} ({report.AdapterDetail})");
        AddReportRow("Frames medidos", AppBridge.T("{0} en {1:F2} s", stats.Frames, stats.DurationSeconds));
        AddReportRow("Duración", AppBridge.T("{0:F2} s medidos de {1} pedidos", stats.DurationSeconds, report.DurationSeconds));
        AddReportRow("FPS promedio", $"{stats.AverageFps:F1}");
        AddReportRow("FPS mediana", $"{stats.MedianFps:F1}");
        AddReportRow("FPS máximo / mínimo", $"{stats.MaxFps:F1} / {stats.MinFps:F1}");
        AddReportRow("1% low / 0.1% low", $"{stats.Low1Fps:F1} / {stats.Low01Fps:F1} FPS");
        AddReportRow("Frame mediana / p99 / peor", $"{stats.MedianFrameMs:F3} / {stats.P99FrameMs:F3} / {stats.WorstFrameMs:F3} ms");
        AddReportRow("Frame peor en el segundo", $"{stats.WorstFrameAtSeconds:F2} s");
        AddReportRow("Hitches", AppBridge.T("{0} (umbral {1:F2} ms) · {2:F1} por minuto", stats.Hitches, stats.HitchThresholdMs, stats.HitchesPerMinute));
        AddReportRow("Fuera de presupuesto", AppBridge.T("{0:F2} % sobre {1:F2} ms", stats.OutOfBudgetPercent, stats.BudgetMs));
        AddReportRow("Inicio → final", AppBridge.T("{0:F0} → {1:F0} FPS ({2:+0.0;-0.0;0.0} %)", stats.StartFps, stats.EndFps, stats.DecayPercent));
        AddReportRow("GPU por frame", AppBridge.T("mediana {0:F3} ms · p99 {1:F3} ms · ocupada {2:F1} %", stats.GpuMedianMs, stats.GpuP99Ms, stats.GpuBusyPercent));
        AddReportRow("CPU por frame (envío)", AppBridge.T("mediana {0:F3} ms · p99 {1:F3} ms", stats.CpuMedianMs, stats.CpuP99Ms));
        AddReportRow("Entrega del frame", AppBridge.T("mediana {0:F3} ms · p99 {1:F3} ms", stats.PresentMedianMs, stats.PresentP99Ms));
        if (stats.QueueWaitMedianMs > 0)
        {
            AddReportRow("Espera de la cola de la GPU", AppBridge.T("mediana {0:F3} ms · promedio {1:F3} ms", stats.QueueWaitMedianMs, stats.QueueWaitAverageMs));
        }
        AddReportRow("Límite detectado", AppBridge.T(stats.LimitingFactor));

        var sensors = report.Sensors;
        if (sensors.HasData)
        {
            AddReportRow("GPU (sensores)", AppBridge.T("uso {0:F0} % · temp {1:F0} °C (máx {2:F0}) · {3:F0} MHz · {4:F0} W", 
                sensors.GpuUsageAveragePercent, sensors.GpuTemperatureAverageCelsius, sensors.GpuTemperatureMaxCelsius,
                sensors.GpuClockAverageMHz, sensors.GpuWattsAverage));
            if (sensors.VramUsedMaxMb > 0)
                AddReportRow("VRAM", AppBridge.T("máx {0:F0} MB de {1:F0} MB", sensors.VramUsedMaxMb, sensors.VramTotalMb));
            AddReportRow("CPU (sensores)", AppBridge.T("uso {0:F0} % · temp {1:F0} °C (máx {2:F0}) · {3:F0} MHz", 
                sensors.CpuUsageAveragePercent, sensors.CpuTemperatureAverageCelsius, sensors.CpuTemperatureMaxCelsius, sensors.CpuClockAverageMHz));
            if (sensors.RamUsedMaxMb > 0)
                AddReportRow("RAM", AppBridge.T("máx {0:F1} GB de {1:F1} GB", sensors.RamUsedMaxMb / 1024.0, sensors.RamTotalMb / 1024.0));
        }
        else
        {
            AddReportRow("Sensores", AppBridge.T("sin datos: no se pudieron leer los sensores del equipo en esta corrida"));
        }

        _warningsPanel.Children.Clear();
        if (report.Warnings.Count > 0)
        {
            var warningsTitle = Text(AppBridge.T("A tener en cuenta"));
            warningsTitle.FontWeight = FontWeights.SemiBold;
            _warningsPanel.Children.Add(warningsTitle);
            foreach (var warning in report.Warnings)
            {
                var line = Text("· " + AppBridge.T(warning.Template, warning.Args));
                line.Foreground = AppBridge.Brush("WarningBrush");
                _warningsPanel.Children.Add(line);
            }
        }

        if (report.System.Count > 0)
        {
            var fingerprintTitle = Text(AppBridge.T("Huella del equipo (así se comparan dos informes)"));
            fingerprintTitle.FontWeight = FontWeights.SemiBold;
            fingerprintTitle.Margin = new Thickness(0, 8, 0, 0);
            _warningsPanel.Children.Add(fingerprintTitle);
            foreach (var pair in report.System)
            {
                var line = Text($"{AppBridge.T(pair.Key)}: {pair.Value}");
                line.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
                _warningsPanel.Children.Add(line);
            }
        }
    }

    private void AddReportRow(string labelKey, string value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = Text(AppBridge.T(labelKey));
        label.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        var valueText = Text(value);
        valueText.IsTextSelectionEnabled = true;
        Grid.SetColumn(label, 0);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(label);
        grid.Children.Add(valueText);
        _reportPanel.Children.Add(grid);
    }

    private static TextBlock Text(string text) => new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = text };

    private void CopyReport()
    {
        if (_lastReport == null) return;
        try
        {
            var package = new DataPackage();
            package.SetText(_lastReport.ToJson());
            Clipboard.SetContent(package);
            SetStatus(AppBridge.T("Informe copiado al portapapeles (JSON)."), warning: false);
        }
        catch (Exception ex)
        {
            SetStatus(AppBridge.T("No se pudo copiar el informe: {0}", ex.Message), warning: true);
        }
    }

    private void OpenReportsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(BenchmarkStore.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{BenchmarkStore.Folder}\"") { UseShellExecute = true });
        }
        catch { }
    }

    // =====================================================================
    // Sensores y historial
    // =====================================================================

    private void RefreshMachinePanel()
    {
        _machinePanel.Children.Clear();
        var sample = AppBridge.ReadSensors();
        if (sample == null)
        {
            var line = Text(AppBridge.HasServices
                ? AppBridge.T("Todavía no hay lecturas de sensores: arrancan con la corrida.")
                : AppBridge.T("Sensores no disponibles en esta sesión (la app anfitriona no expuso sus servicios)."));
            line.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
            _machinePanel.Children.Add(line);
            return;
        }

        var data = sample.Value;
        _machinePanel.Children.Add(Text(AppBridge.T("GPU: uso {0} · temp {1} · {2} · {3} · VRAM {4}",
            Percent(data.GpuUsagePercent), Celsius(data.GpuTemperatureCelsius), GigaHertz(data.GpuClockMHz),
            Watts(data.GpuWatts), Gigabytes(data.VramUsedMb))));
        _machinePanel.Children.Add(Text(AppBridge.T("CPU: uso {0} · temp {1} · {2} · {3}",
            Percent(data.CpuUsagePercent), Celsius(data.CpuTemperatureCelsius), GigaHertz(data.CpuClockMHz), Watts(data.CpuWatts))));
        _machinePanel.Children.Add(Text(AppBridge.T("RAM: uso {0} · {1} de {2} GB",
            Percent(data.RamUsagePercent), (data.RamUsedMb / 1024.0).ToString("F1"), (data.RamTotalMb / 1024.0).ToString("F1"))));
    }

    private void RefreshHistory()
    {
        _historyPanel.Children.Clear();
        var files = BenchmarkStore.ListRecent(5);
        if (files.Count == 0)
        {
            var empty = Text(AppBridge.T("No hay informes guardados todavía."));
            empty.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
            _historyPanel.Children.Add(empty);
            return;
        }

        foreach (var file in files)
        {
            var report = BenchmarkStore.Load(file);
            if (report == null) continue;
            var stats = report.Stats;
            var line = Text(AppBridge.T("{0} · {1} · {2} · {3:F0} FPS mediana · GPU {4:F2} ms · {5}",
                report.Timestamp.ToString("dd/MM HH:mm"),
                AppBridge.T(report.SceneName),
                report.Backend,
                stats.MedianFps,
                stats.GpuMedianMs,
                report.Completed ? AppBridge.T("completa") : AppBridge.T("cortada")));
            line.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
            _historyPanel.Children.Add(line);
        }
    }

    private static string Percent(double value) => value > 0 ? $"{value:F0} %" : "--";
    private static string Celsius(double value) => value > 0 ? $"{value:F0} °C" : "--";
    private static string GigaHertz(double mhz) => mhz > 0 ? $"{mhz / 1000.0:F2} GHz" : "--";
    private static string Watts(double value) => value > 0 ? $"{value:F0} W" : "--";
    private static string Gigabytes(double mb) => mb > 0 ? $"{mb / 1024.0:F1} GB" : "--";
}
