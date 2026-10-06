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
using WinForge.Component.Benchmark.Assets;
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

    // Los presets de calidad son GENERALES y viven en Graphics/SceneGraphicsPresets.cs: los comparte
    // cualquier escena y cualquier pantalla, y la página lo único que hace es aplicarlos sobre sus
    // combos. Antes vivían acá adentro, así que no se podían guardar ni comparar sin pasar por la UI.

    // Resolución de la VENTANA de la escena, escrita como la lista de resoluciones de cualquier juego:
    // las medidas en píxeles y, arriba, las dos que no son un número fijo. 0 = automática (la que entra
    // en el monitor); -1 = nativa (el área útil de la pantalla). Los números van pelados y no con el
    // apodo (720p, 1080p…): se elige la medida, y el apodo no cambia nada de lo que se corre.
    private static readonly (string LabelKey, int Width, int Height)[] ResolutionPresets =
    {
        ("Automática", 0, 0),
        ("Nativa", -1, -1),
        ("1280 × 720", 1280, 720),
        ("1600 × 900", 1600, 900),
        ("1920 × 1080", 1920, 1080),
        ("2560 × 1440", 2560, 1440),
        ("3840 × 2160", 3840, 2160)
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
    private readonly ComboBox _qualityCombo = new() { MinWidth = 300 };
    private readonly ComboBox _shadowsCombo = new() { MinWidth = 240 };
    private readonly ComboBox _environmentCombo = new() { MinWidth = 240 };
    private readonly ComboBox _resolutionCombo = new() { MinWidth = 240 };
    private readonly TextBlock _qualityText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _shadowsText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _environmentText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _resolutionText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private bool _applyingQuality;
    // True mientras se RESTAURA la configuración guardada de una escena: los combos cambian de valor
    // pero no es una decisión del usuario, así que no se marca "Personalizado" ni se guarda por esos
    // cambios (la restauración no toca lo guardado).
    private bool _restoringGraphics;
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
    private readonly Button _deleteButton = new() { IsEnabled = false };
    private readonly Button _clearHistoryButton = new() { IsEnabled = false };

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
            LoadGraphicsOptions();
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
        _deleteButton.Click += (_, _) => DeleteReport();
        _clearHistoryButton.Click += async (_, _) => await DeleteAllReportsAsync();
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
        mainPanel.Children.Add(BuildGraphicsCard());
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

        var apiRow = Row("API gráfica", _apiCombo, "API gráfica",
            "Con qué API de Windows se dibuja la escena: Direct3D 11, Direct3D 12, Vulkan u OpenGL. Las que no estén disponibles en este equipo aparecen deshabilitadas.");
        panel.Children.Add(apiRow);
        panel.Children.Add(Indented(_apiAvailabilityText));

        panel.Children.Add(Row("Escena", _sceneCombo, "Escena",
            "El diorama que se dibuja mientras se mide. Cada escena tiene su propio peso (geometría, sombras, reflejos): dos corridas de escenas distintas no se comparan."));
        _sceneText.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        panel.Children.Add(Indented(_sceneText));

        panel.Children.Add(Row("Placa de video", _adapterCombo, "Placa de video",
            "El adaptador con el que se dibuja. Automática usa la de más memoria: en un equipo con dos GPU (una integrada y una dedicada), conviene elegir la dedicada."));
        panel.Children.Add(Row("Presentación", _presentationCombo, "Presentación",
            "Cómo se abre la escena: en ventana, en pantalla completa sin bordes (se ve como pantalla completa sin cambiar el modo de video del monitor) o en pantalla completa exclusiva (menos intermediarios, pero puede fallar si otra app la tiene tomada)."));
        panel.Children.Add(Row("Duración", _durationCombo, "Duración",
            "Cuánto dura la MEDICIÓN después del calentamiento. Esc corta la corrida y el informe guarda lo medido hasta ahí."));
        // La franja de métricas sobre la escena no tiene switch: va siempre.

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

    /// <summary>
    /// Los ajustes gráficos de la corrida. Son los que cambian lo que se DIBUJA y por lo tanto lo que
    /// se mide, así que van antes de arrancar y quedan guardados en el informe (dos informes con
    /// distinta configuración no describen la misma escena).
    /// </summary>
    private Border BuildGraphicsCard()
    {
        var panel = new StackPanel { Spacing = 10 };

        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
        _retranslate.Add(() => title.Text = AppBridge.T("Gráficos de la escena"));
        panel.Children.Add(title);

        // Cada opción tiene la misma forma que en el menú de videojuego: el título —con el "?" que la
        // explica—, el desplegable con los nombres de las opciones y, abajo, la línea que describe lo
        // que está elegido en ese momento.
        panel.Children.Add(Row("Calidad gráfica", _qualityCombo, "Calidad gráfica",
            "Un nivel preajustado que fija todas las opciones de abajo de un golpe. Si después tocás una opción suelta, la calidad pasa a Personalizado."));
        _qualityText.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        panel.Children.Add(Indented(_qualityText));

        panel.Children.Add(Row("Sombras", _shadowsCombo, "Sombras",
            "Las sombras que proyectan los objetos. El nivel fija las dos cosas a la vez: si la pasada de sombras se dibuja y con cuánta resolución del mapa. Desactivadas la escena no cambia: se saca el costo de la pasada de profundidad y del muestreo, que es justo lo que la corrida mide."));
        _shadowsText.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        panel.Children.Add(Indented(_shadowsText));

        panel.Children.Add(Row("Iluminación global", _environmentCombo, "Iluminación global",
            "La luz ambiente y los reflejos que reciben los materiales, tomados del cielo real de la escena (HDRI). Desactivada, la escena se ilumina solo con las luces de su estilo y el fondo pasa a ser un cielo procedural: cambia la luz y los reflejos, no solo el fondo."));
        _environmentText.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        panel.Children.Add(Indented(_environmentText));

        panel.Children.Add(Row("Resolución", _resolutionCombo, "Resolución",
            "Resolución de la ventana de la escena; solo se aplica en modo ventana, porque en pantalla completa manda el monitor. Automática usa el tamaño que entra en el monitor y Nativa el área útil. Si lo pedido no entra, la corrida sale en la que entre y queda un aviso en el informe."));
        _resolutionText.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        panel.Children.Add(Indented(_resolutionText));

        var hint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        hint.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
        _retranslate.Add(() => hint.Text = AppBridge.T("Cada escena guarda su propia configuración y lo elegido queda anotado en el informe de la corrida."));
        panel.Children.Add(hint);
        _retranslate.Add(RefreshGraphicsTexts);

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
        _deleteButton.CornerRadius = new CornerRadius(6);
        _retranslate.Add(() =>
        {
            _copyButton.Content = AppBridge.T("Copiar informe");
            _openFolderButton.Content = AppBridge.T("Abrir carpeta de informes");
            _deleteButton.Content = AppBridge.T("Borrar informe");
        });
        buttons.Children.Add(_copyButton);
        buttons.Children.Add(_openFolderButton);
        buttons.Children.Add(_deleteButton);
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

        _clearHistoryButton.CornerRadius = new CornerRadius(6);
        _retranslate.Add(() => _clearHistoryButton.Content = AppBridge.T("Borrar todos los informes"));
        panel.Children.Add(_clearHistoryButton);
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

    /// <summary>
    /// Una fila de opción: el título en la columna fija de la izquierda y el control a la derecha. Si se
    /// pasa ayuda, al lado del título va el "?" con su tooltip (ver <see cref="InfoButton"/>): es el
    /// mismo recurso que usan las páginas de la app para explicar una opción sin meter la explicación en
    /// el desplegable, donde no entraría.
    /// </summary>
    private Grid Row(string labelKey, FrameworkElement control, string? helpTitle = null, string? helpText = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _retranslate.Add(() => label.Text = AppBridge.T(labelKey));

        if (helpText == null)
        {
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
        }
        else
        {
            // El rótulo y el "?" van en una grilla propia —rótulo elástico, botón fijo— y no en un
            // StackPanel horizontal: ahí el rótulo se mediría con ancho infinito, no envolvería y se
            // saldría de la columna encima del control.
            var labelCell = new Grid();
            labelCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labelCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            labelCell.Children.Add(label);

            var info = InfoButton(helpTitle ?? labelKey, helpText);
            Grid.SetColumn(info, 1);
            labelCell.Children.Add(info);

            Grid.SetColumn(labelCell, 0);
            grid.Children.Add(labelCell);
        }

        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>
    /// El "?" de ayuda de una opción, con el mismo estilo que el resto de la app: el icono apagado en un
    /// botón de 20 × 20 sin fondo ni borde, y el tooltip abajo con el título en seminegrita y el texto en
    /// tamaño chico (el mismo formato de los tooltips informativos de las páginas de fábrica).
    ///
    /// El tooltip se arma —y se rearma en cada cambio de idioma— con el texto FUENTE: la traducción se
    /// pide al construirlo, así que un idioma nuevo no deja el tooltip viejo pegado.
    /// </summary>
    private Button InfoButton(string titleKey, string textKey)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE946",
                FontSize = 12,
                Foreground = AppBridge.Brush("TextFillColorSecondaryBrush")
            },
            Width = 20,
            Height = 20,
            MinWidth = 20,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };

        _retranslate.Add(() =>
        {
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(new TextBlock
            {
                Text = AppBridge.T(titleKey),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            var body = new TextBlock
            {
                Text = AppBridge.T(textKey),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            body.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
            content.Children.Add(body);

            ToolTipService.SetToolTip(button, new ToolTip
            {
                Content = content,
                Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom,
                MaxWidth = 420,
                Padding = new Thickness(10, 7, 10, 7)
            });
        });

        return button;
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
            // después de armar la lista. Las escenas EN DESARROLLO se muestran apagadas: se ven en
            // la lista con su marca pero no se pueden elegir — el benchmark solo mide escenas
            // terminadas, y la de siempre (Neón) sigue siendo el default.
            _sceneCombo.Items.Add(new ComboBoxItem
            {
                Content = AppBridge.T(scene.Name),
                Tag = scene.Id,
                IsEnabled = !scene.InDevelopment
            });
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
        if (_sceneCombo.SelectedItem is ComboBoxItem { Tag: string id })
        {
            var next = SceneCatalog.Find(id) ?? SceneCatalog.Default;
            if (next.InDevelopment)
            {
                // Los ítems en desarrollo están APAGADOS, así que esto solo puede llegar por una
                // selección programática: se vuelve a la escena de siempre con un aviso, sin tocar
                // la configuración guardada de ninguna de las dos escenas.
                SelectByTag(_sceneCombo, SceneCatalog.Default.Id);
                if (!_running)
                    SetStatus(AppBridge.T("Esta escena todavía está en desarrollo: por ahora solo se puede correr {0}.", AppBridge.T(SceneCatalog.Default.Name)), warning: true);
                return;
            }
            if (!string.Equals(next.Id, _scene.Id, StringComparison.OrdinalIgnoreCase))
            {
                // Cada escena conserva SU configuración gráfica: la que se deja se guarda y la que
                // entra arranca con la suya (o con el preset de siempre si nunca se configuró).
                SaveGraphicsForCurrentScene();
                _scene = next;
                RestoreGraphicsForScene(_scene);
            }
            else
            {
                _scene = next;
            }
        }
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

    /// <summary>
    /// Los ajustes gráficos de la corrida. Dos detalles de la interfaz que no son cosméticos:
    /// <list type="bullet">
    /// <item>El PRESET se aplica opción por opción sobre los combos, y no sobre un modelo aparte:
    /// los combos son la única fuente del pedido, así que lo que se ve en pantalla es lo que se corre.</item>
    /// <item>Las opciones que no aplican se APAGAN en vez de quedar ahí sin hacer nada: la resolución
    /// solo tiene sentido en modo ventana, porque en pantalla completa manda el monitor.</item>
    /// </list>
    /// </summary>
    private void LoadGraphicsOptions()
    {
        _qualityCombo.Items.Clear();
        foreach (var preset in SceneGraphicsPresets.All)
        {
            _qualityCombo.Items.Add(new ComboBoxItem { Content = AppBridge.T(preset.LabelKey), Tag = preset });
        }
        // "Personalizado" no fija nada: se cae ahí solo cuando se toca una perilla suelta (ver
        // MarkCustomQuality). Por eso el ítem va sin Tag.
        _qualityCombo.Items.Add(new ComboBoxItem { Content = AppBridge.T(SceneGraphicsPresets.CustomLabelKey) });
        // Arranca en el preset de SIEMPRE, que es el valor de los combos que se acaban de armar: la
        // configuración guardada de la escena, si hay una, se restaura al final de este método.
        SelectPresetItem(SceneGraphicsPresets.Default.Id);

        // El desplegable de sombras es UNO solo y con los nombres de cualquier menú de juego: el nivel
        // fija las dos perillas del dato de una vez (si la pasada se dibuja y el tamaño del mapa). Un
        // combo aparte para el tamaño obligaría a elegir un número que nadie elige por su nombre.
        _shadowsCombo.Items.Clear();
        foreach (var level in SceneShadowQuality.All)
        {
            _shadowsCombo.Items.Add(new ComboBoxItem { Content = AppBridge.T(level.LabelKey), Tag = level });
        }
        SelectByTag(_shadowsCombo, SceneShadowQuality.Default);   // el de siempre

        _environmentCombo.Items.Clear();
        _environmentCombo.Items.Add(new ComboBoxItem { Content = AppBridge.T("Activada"), Tag = true });
        _environmentCombo.Items.Add(new ComboBoxItem { Content = AppBridge.T("Desactivada"), Tag = false });
        _environmentCombo.SelectedIndex = 0;

        _resolutionCombo.Items.Clear();
        foreach (var (labelKey, width, height) in ResolutionPresets)
        {
            _resolutionCombo.Items.Add(new ComboBoxItem { Content = AppBridge.T(labelKey), Tag = (width, height) });
        }
        _resolutionCombo.SelectedIndex = 0;

        RefreshGraphicsTexts();

        // El preset aplica sobre los combos; tocar una opción suelta saca al usuario del preset.
        // Todo cambio se guarda como la configuración de ESTA escena (ver SceneGraphicsStore).
        _qualityCombo.SelectionChanged += (_, _) => ApplyQualityPreset();
        _shadowsCombo.SelectionChanged += (_, _) => { RefreshShadowsText(); OnGraphicsChanged(); };
        _environmentCombo.SelectionChanged += (_, _) => { RefreshEnvironmentText(); OnGraphicsChanged(); };
        _resolutionCombo.SelectionChanged += (_, _) => { RefreshResolutionAvailability(); OnGraphicsChanged(); };
        _presentationCombo.SelectionChanged += (_, _) => RefreshResolutionAvailability();

        // Recién acá se puede restaurar la configuración de la escena elegida: los combos ya están
        // armados y los handlers puestos. Sin esto, cada visita arrancaría en el preset de siempre
        // aunque el usuario ya hubiera configurado la escena.
        RestoreGraphicsForScene(_scene);
    }

    /// <summary>
    /// Las líneas de descripción de la tarjeta, todas juntas. Se rehacen al cambiar cualquier opción, al
    /// cambiar de idioma y al arrancar o terminar una corrida: son el único lugar donde vive el detalle de
    /// lo elegido —un desplegable cerrado solo muestra el nombre de la opción—, así que no pueden quedar
    /// contando lo que estaba elegido antes.
    /// </summary>
    private void RefreshGraphicsTexts()
    {
        RefreshQualityText();
        RefreshShadowsText();
        RefreshEnvironmentText();
        RefreshResolutionAvailability();
    }

    /// <summary>Qué fija el nivel de calidad elegido (y qué es "Personalizado").</summary>
    private void RefreshQualityText() =>
        _qualityText.Text = AppBridge.T(SelectedPreset()?.DescriptionKey ?? SceneGraphicsPresets.CustomDescriptionKey);

    /// <summary>Qué hace el nivel de sombras elegido, con el costo que tiene.</summary>
    private void RefreshShadowsText() =>
        _shadowsText.Text = AppBridge.T(SelectedShadowQuality().DescriptionKey);

    /// <summary>Qué ilumina la escena con la iluminación global encendida y apagada.</summary>
    private void RefreshEnvironmentText() =>
        _environmentText.Text = SelectedEnvironment()
            ? AppBridge.T("La luz ambiente y los reflejos salen del cielo real de la escena (HDRI). Es la iluminación de siempre.")
            : AppBridge.T("La escena se ilumina solo con las luces de su estilo y el fondo pasa a ser un cielo procedural: además de cambiar la luz y los reflejos, se saltea la lectura del .hdr al arrancar.");

    /// <summary>
    /// Aplica el preset elegido a las perillas sueltas. Se hace a través de los COMBOS y no escribiendo
    /// el modelo: los combos son la única fuente de lo que se corre, así que un preset que tocara otra
    /// cosa podría dejar la pantalla diciendo una cosa y la corrida haciendo otra.
    /// </summary>
    private void ApplyQualityPreset()
    {
        if (_applyingQuality || _qualityCombo.SelectedItem is not ComboBoxItem { Tag: SceneGraphicsPreset preset }) return;

        _applyingQuality = true;
        try
        {
            // El nivel de sombras se resuelve con el tamaño NORMALIZADO: si un preset pidiera un valor
            // raro, el combo tiene que quedar mostrando el que se corre (el mismo que arma la textura).
            SelectShadowQuality(preset.Shadows, preset.ShadowMapSize);
            SelectByTag(_environmentCombo, preset.Environment);
            SelectByTag(_resolutionCombo, preset.Resolution);
            RefreshGraphicsTexts();
        }
        finally { _applyingQuality = false; }

        // Los cambios de arriba corrieron con el guard puesto (no se guardaron): el preset se guarda
        // UNA vez, ya con los valores finales.
        SaveGraphicsForCurrentScene();
    }

    /// <summary>
    /// Tocar una perilla suelta saca al usuario del preset: el preset deja de describir lo que hay
    /// elegido. Se cae a "Personalizado" en vez de seguir mostrando el nombre de un preset que ya no se
    /// cumple (un preset que miente es peor que no tener preset).
    /// </summary>
    private void MarkCustomQuality()
    {
        if (_applyingQuality || _qualityCombo.SelectedItem is ComboBoxItem { Tag: null }) return;
        foreach (var item in _qualityCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is null)
            {
                _qualityCombo.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>
    /// Elige el nivel de sombras que describe una configuración, con el tamaño ya normalizado por
    /// <see cref="SceneShadowQuality.From"/>: una configuración escrita a mano o por otro build tiene que
    /// caer en un nivel que exista, y tiene que ser el mismo que se va a crear en la placa.
    /// </summary>
    private void SelectShadowQuality(bool shadows, int shadowMapSize) =>
        SelectByTag(_shadowsCombo, SceneShadowQuality.From(shadows, shadowMapSize));

    /// <summary>Selecciona el ítem cuyo Tag es el valor pedido: los combos guardan el dato en el Tag.</summary>
    private static void SelectByTag<T>(ComboBox combo, T value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, value))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>Preset elegido en el combo de calidad (null = "Personalizado").</summary>
    private SceneGraphicsPreset? SelectedPreset() =>
        _qualityCombo.SelectedItem is ComboBoxItem { Tag: SceneGraphicsPreset preset } ? preset : null;

    /// <summary>Perillas gráficas elegidas, en la forma que reciben los cuatro backends.</summary>
    private SceneGraphicsOptions CurrentGraphicsOptions() => new()
    {
        Shadows = SelectedShadows(),
        ShadowMapSize = SelectedShadowMapSize(),
        Environment = SelectedEnvironment()
    };

    /// <summary>
    /// Preset elegido como TEXTO FUENTE (español) para el informe, o "Personalizado" si se tocó alguna
    /// perilla suelta. El informe guarda texto fuente y la página lo traduce al mostrarlo, igual que
    /// hace con el nombre de la escena.
    /// </summary>
    private string CurrentGraphicsPresetLabel() =>
        SelectedPreset()?.LabelKey ?? SceneGraphicsPresets.CustomLabelKey;

    /// <summary>Configuración gráfica elegida, en la forma que se guarda por escena.</summary>
    private SceneGraphicsConfig CurrentGraphicsConfig()
    {
        var resolution = SelectedRawResolution();
        return new SceneGraphicsConfig(
            SelectedPreset()?.Id ?? SceneGraphicsPresets.CustomId,
            SelectedShadows(), SelectedShadowMapSize(), SelectedEnvironment(),
            resolution.Width, resolution.Height);
    }

    /// <summary>
    /// Tamaño de ventana PEDIDO, tal como está en el combo (0 = automática, -1 = nativa). Es lo que se
    /// guarda y lo que se compara con los presets; la resolución EFECTIVA se resuelve al arrancar
    /// (ver SelectedResolution), porque "nativa" depende del monitor del momento.
    /// </summary>
    private (int Width, int Height) SelectedRawResolution() =>
        _resolutionCombo.SelectedItem is ComboBoxItem { Tag: (int width, int height) } ? (width, height) : (0, 0);

    /// <summary>
    /// Un cambio en una perilla suelta: sale del preset y se guarda como la configuración de la escena.
    /// Lo que hace un preset (y lo que hace la restauración) corre con guard puesto: no son decisiones
    /// del usuario, y el preset se guarda aparte, UNA vez, con los valores finales.
    /// </summary>
    private void OnGraphicsChanged()
    {
        if (_applyingQuality || _restoringGraphics) return;
        MarkCustomQuality();
        SaveGraphicsForCurrentScene();
    }

    /// <summary>Guarda lo elegido como la configuración de la escena actual.</summary>
    private void SaveGraphicsForCurrentScene()
    {
        if (_restoringGraphics || _qualityCombo.Items.Count == 0) return;
        SceneGraphicsStore.Save(_scene.Id, CurrentGraphicsConfig());
    }

    /// <summary>
    /// Configura la escena que entra: la suya guardada, o el preset de siempre si nunca se configuró.
    /// Se aplica sobre los combos —la única fuente de lo que se corre— y con guard puesto, así que la
    /// restauración no se confunde con una decisión del usuario ni dispara un guardado.
    /// </summary>
    private void RestoreGraphicsForScene(SceneDefinition scene)
    {
        if (_qualityCombo.Items.Count == 0) return;   // la tarjeta todavía no se armó

        var config = SceneGraphicsStore.Load(scene.Id);
        _restoringGraphics = true;
        try
        {
            if (config == null)
            {
                // Escena sin configuración guardada: el look de SIEMPRE, el mismo punto de partida de
                // un primer arranque y el que hace comparables los informes que ya están en disco.
                SelectShadowQuality(SceneGraphicsPresets.Default.Shadows, SceneGraphicsPresets.Default.ShadowMapSize);
                SelectByTag(_environmentCombo, SceneGraphicsPresets.Default.Environment);
                SelectByTag(_resolutionCombo, SceneGraphicsPresets.Default.Resolution);
                SelectPresetItem(SceneGraphicsPresets.Default.Id);
            }
            else
            {
                // El nivel de sombras se resuelve con el tamaño ya normalizado adentro de
                // SelectShadowQuality: una configuración escrita por otro build —o a mano— tiene que caer
                // en una opción que exista, y tiene que ser la MISMA que se va a crear en la placa.
                SelectShadowQuality(config.Shadows, config.ShadowMapSize);
                SelectByTag(_environmentCombo, config.Environment);
                SelectByTag(_resolutionCombo, (config.Width, config.Height));
                SelectPresetItem(config.ResolvedPresetId());
            }
            RefreshGraphicsTexts();
        }
        finally { _restoringGraphics = false; }
    }

    /// <summary>
    /// Selecciona el preset por id. Un id que ya no existe (configuración guardada por un build con
    /// otras perillas) cae a "Personalizado" en vez de mostrar el nombre de un preset que no describe
    /// lo que hay elegido.
    /// </summary>
    private void SelectPresetItem(string presetId)
    {
        foreach (var item in _qualityCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is SceneGraphicsPreset preset &&
                string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase))
            {
                _qualityCombo.SelectedItem = item;
                return;
            }
        }

        // "Personalizado" es el ítem sin Tag (ver LoadGraphicsOptions).
        foreach (var item in _qualityCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is null)
            {
                _qualityCombo.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>
    /// La resolución se elige solo en modo ventana y solo con la corrida detenida: en pantalla completa
    /// la escena ocupa el monitor y lo pedido no se usa.
    /// </summary>
    private void RefreshResolutionAvailability()
    {
        bool windowed = SelectedPresentation() == PresentationMode.Windowed;
        _resolutionCombo.IsEnabled = windowed && !_running;
        _resolutionText.Text = windowed
            ? AppBridge.T("La ventana de la escena: Automática usa el tamaño que entra en el monitor y Nativa el área útil. Si lo pedido no entra, la corrida sale en la que entre y queda un aviso en el informe.")
            : AppBridge.T("En pantalla completa la escena usa la resolución del monitor y lo elegido acá no se usa.");
    }

    // =====================================================================
    // Corrida
    // =====================================================================

    private async Task StartRunAsync()
    {
        if (_running) return;

        // Cinturón y tirantes: los ítems en desarrollo no se pueden elegir en el combo, así que esto
        // solo dispara si el estado quedó inconsistente. Una escena a medio hacer no se corre.
        if (_scene.InDevelopment)
        {
            SetStatus(AppBridge.T("Esta escena todavía está en desarrollo: por ahora solo se puede correr {0}.", AppBridge.T(SceneCatalog.Default.Name)), warning: true);
            return;
        }

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
        var resolution = SelectedResolution();

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
            // Sin sincronización vertical: la página no la ofrece porque un benchmark mide el techo del
            // equipo, no el refresco de la pantalla (con vsync, el FPS topea en la frecuencia del monitor
            // y lo que se mide es cuándo puede dibujar la pantalla, no cuánto da la placa). La perilla
            // sigue en el host porque el probador headless la usa para capturar frames completos.
            VSync = false,
            Graphics = CurrentGraphicsOptions(),
            GraphicsPreset = CurrentGraphicsPresetLabel(),
            WindowWidth = resolution.Width,
            WindowHeight = resolution.Height,
            ShowWindow = true,
            // La versión sale del componente (BenchmarkComponent), que es su única fuente:
            // escribirla a mano acá dejaba el informe con la versión vieja tras cada bump.
            ComponentVersion = new BenchmarkComponent().Version,
            AppVersion = AppBridge.AppVersion,
            SensorProvider = AppBridge.ReadSensors,
            Fingerprint = fingerprint
        };

        var progress = new Progress<SceneHost.RunProgress>(report =>
            _dispatcher.TryEnqueue(() => UpdateLive(report, durationSeconds)));

        try
        {
            // Assets de la escena (README cinematográfico §7): si el set no está en disco, se baja
            // ANTES de preparar la escena. Es lo que hace que una instalación limpia corra la escena
            // por defecto en vez de morir con "no encontré el modelo".
            if (!string.IsNullOrEmpty(_scene.AssetSet) && !BenchmarkAssetPacks.IsReady(_scene.AssetSet))
            {
                SetStatus(AppBridge.T("Descargando los assets de la escena…"), warning: false);
                var packProgress = new Progress<double>(value => _progress.Value = value);
                var pack = await BenchmarkAssetPacks
                    .EnsureAsync(_scene.AssetSet, packProgress, token)
                    .ConfigureAwait(true);
                if (!pack.Success)
                {
                    SetStatus(
                        AppBridge.T("No se pudieron preparar los assets de la escena: {0}", pack.Detail),
                        warning: true);
                    return;
                }
                SetStatus(AppBridge.T("Preparando la escena…"), warning: false);
            }

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

    /// <summary>
    /// Nivel de sombras elegido. Cae al nivel de siempre solo si la tarjeta todavía no se armó: el combo
    /// nunca queda sin selección (se elige un nivel al construirlo y al restaurar la escena).
    /// </summary>
    private SceneShadowQuality SelectedShadowQuality() =>
        _shadowsCombo.SelectedItem is ComboBoxItem { Tag: SceneShadowQuality level } ? level : SceneShadowQuality.Default;

    private bool SelectedShadows() => SelectedShadowQuality().Shadows;

    private int SelectedShadowMapSize() => SelectedShadowQuality().ShadowMapSize;

    private bool SelectedEnvironment() =>
        _environmentCombo.SelectedItem is ComboBoxItem { Tag: bool environment } && environment;

    /// <summary>
    /// Tamaño pedido para la ventana: "automática" viaja como 0 (el host elige el que entra en el
    /// monitor) y "nativa" se resuelve acá con el área útil del monitor donde está el cursor, que es
    /// el monitor donde va a aparecer la ventana.
    /// </summary>
    private (int Width, int Height) SelectedResolution()
    {
        if (_resolutionCombo.SelectedItem is not ComboBoxItem { Tag: (int width, int height) }) return (0, 0);
        if (width == 0) return (0, 0);
        if (width < 0) return Win32Window.WorkAreaUnderCursor();
        return (width, height);
    }

    private void SetRunningUi(bool running)
    {
        _startButton.IsEnabled = !running;
        _stopButton.IsEnabled = running;
        _apiCombo.IsEnabled = !running;
        _sceneCombo.IsEnabled = !running;
        _adapterCombo.IsEnabled = !running;
        _presentationCombo.IsEnabled = !running;
        _durationCombo.IsEnabled = !running;
        _shadowsCombo.IsEnabled = !running;
        _qualityCombo.IsEnabled = !running;
        _environmentCombo.IsEnabled = !running;
        RefreshGraphicsTexts();
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
        // Al terminar la corrida el informe queda en disco para poder compararlo después. Solo la
        // PRIMERA vez: esto también se llama al cambiar de idioma (para retraducir los rótulos) y
        // guardar de nuevo dejaba un informe duplicado por cada cambio de idioma.
        if (string.IsNullOrEmpty(report.FileName))
        {
            try
            {
                report.Save();
                BenchmarkStore.Trim();
            }
            catch { /* si no se pudo guardar, el informe igual está en pantalla */ }
        }

        _copyButton.IsEnabled = true;
        _deleteButton.IsEnabled = true;
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
        // La configuración gráfica va en su propio renglón: es lo que hace que dos informes se puedan
        // comparar entre sí (misma API, misma escena, mismo tamaño, mismos ajustes).
        // La sincronización vertical se nombra SOLO si se usó: la página no la ofrece —un benchmark se
        // corre sin vsync— y un renglón que dijera siempre "sin sincronización vertical" sería ruido. La
        // enciende únicamente el probador headless, y ahí sí importa que el informe lo deje anotado.
        AddReportRow("Gráficos", AppBridge.T("{0} · {1}{2}",
            report.Shadows ? AppBridge.T("sombras {0}²", report.ShadowMapSize) : AppBridge.T("sin sombras"),
            report.Environment ? AppBridge.T("entorno HDRI") : AppBridge.T("cielo procedural"),
            report.VSync ? AppBridge.T(" · con sincronización vertical") : ""));
        // El preset va en su propio renglón, cuando existe: es lo que deja ver de un vistazo si dos
        // corridas se configuraron igual. Un informe guardado antes de que existiera el dato no inventa
        // un preset (el detalle de las perillas está igual en el renglón de arriba).
        if (!string.IsNullOrWhiteSpace(report.GraphicsPreset))
            AddReportRow("Preset gráfico", AppBridge.T(report.GraphicsPreset));
        AddReportRow("Adaptador", string.IsNullOrWhiteSpace(report.AdapterDetail) ? report.AdapterName : $"{report.AdapterName} ({report.AdapterDetail})");
        AddReportRow("Frames medidos", AppBridge.T("{0} en {1:F2} s", stats.Frames, stats.DurationSeconds));
        AddReportRow("Duración", AppBridge.T("{0:F2} s medidos de {1} pedidos", stats.DurationSeconds, report.DurationSeconds));
        // El calentamiento se dice EXPLÍCITAMENTE: es la parte de la corrida que no entra en los
        // números (compilado de shaders, subida de geometría y constantes, primer uso de las caches
        // del driver). Sin este renglón el informe no se puede auditar: no hay forma de saber si
        // "2,30 ms de mediana" incluye la carga o no.
        AddReportRow("Calentamiento", WarmupText(report));
        AddReportRow("FPS promedio", $"{stats.AverageFps:F1}");
        AddReportRow("FPS mediana", $"{stats.MedianFps:F1}");
        AddReportRow("FPS máximo / mínimo", $"{stats.MaxFps:F1} / {stats.MinFps:F1}");
        AddReportRow("1% low / 0.1% low", $"{stats.Low1Fps:F1} / {stats.Low01Fps:F1} FPS");
        AddReportRow("Frame mediana / p99 / peor", $"{stats.MedianFrameMs:F3} / {stats.P99FrameMs:F3} / {stats.WorstFrameMs:F3} ms");
        AddReportRow("Frame peor en el segundo", $"{stats.WorstFrameAtSeconds:F2} s");
        AddReportRow("Hitches", AppBridge.T("{0} (umbral {1:F2} ms) · {2:F1} por minuto", stats.Hitches, stats.HitchThresholdMs, stats.HitchesPerMinute));
        AddReportRow("Fuera de presupuesto", AppBridge.T("{0:F2} % sobre {1:F2} ms", stats.OutOfBudgetPercent, stats.BudgetMs));
        AddReportRow("Inicio → final", AppBridge.T("{0:F0} → {1:F0} FPS ({2:+0.0;-0.0;0.0} %)", stats.StartFps, stats.EndFps, stats.DecayPercent));
        // Sin muestras de GPU no hay número: 0,000 ms / 0 % se leería como "la placa no trabajó".
        AddReportRow("GPU por frame", stats.GpuSamples > 0
            ? AppBridge.T("mediana {0:F3} ms · p99 {1:F3} ms · ocupada {2:F1} %", stats.GpuMedianMs, stats.GpuP99Ms, stats.GpuBusyPercent)
            : AppBridge.T("sin datos: el driver no devolvió tiempos de GPU en esta corrida"));
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
                // La clave del informe es un id estable: el rótulo se resuelve y se traduce acá.
                var line = Text($"{AppBridge.T(AppBridge.FingerprintLabel(pair.Key))}: {pair.Value}");
                line.Foreground = AppBridge.Brush("TextFillColorSecondaryBrush");
                _warningsPanel.Children.Add(line);
            }
        }
    }

    /// <summary>
    /// Texto del calentamiento descartado. Un informe guardado antes de que existiera el dato cae
    /// al calentamiento pedido por la escena y va sin conteo de frames.
    /// </summary>
    private static string WarmupText(BenchmarkReport report)
    {
        double seconds = report.WarmupElapsedSeconds > 0 ? report.WarmupElapsedSeconds : report.WarmupSeconds;
        return report.WarmupFrames > 0
            ? AppBridge.T("{0:F1} s y {1} frames fuera de los números (compilado de shaders, subida de geometría y caches del driver)", seconds, report.WarmupFrames)
            : AppBridge.T("{0:F1} s fuera de los números (compilado de shaders, subida de geometría y caches del driver)", seconds);
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

    /// <summary>
    /// Borra el informe que está en pantalla: el archivo en disco Y lo que se ve, porque dejar la
    /// tabla llena con números de un informe que ya no existe es peor que no tener informe.
    /// </summary>
    private void DeleteReport()
    {
        if (_lastReport == null) return;

        string? failure = null;
        try
        {
            // El nombre lo puso Save(): si está vacío, el informe nunca llegó al disco y solo hay
            // que sacarlo de la pantalla.
            if (!string.IsNullOrWhiteSpace(_lastReport.FileName))
            {
                string path = System.IO.Path.Combine(BenchmarkStore.Folder, _lastReport.FileName);
                if (!BenchmarkStore.Delete(path)) failure = path;
            }
        }
        catch (Exception ex) { failure = ex.Message; }

        ClearReport();
        RefreshHistory();
        SetStatus(failure == null
                ? AppBridge.T("Informe borrado.")
                : AppBridge.T("No se pudo borrar el informe: {0}", failure),
            warning: failure != null);
    }

    /// <summary>Saca el informe de la pantalla y vuelve a mostrar el cartel de "todavía no corriste".</summary>
    private void ClearReport()
    {
        _lastReport = null;
        _reportPanel.Children.Clear();
        _warningsPanel.Children.Clear();
        _copyButton.IsEnabled = false;
        _deleteButton.IsEnabled = false;
        if (_reportPanel.Tag is TextBlock empty) empty.Visibility = Visibility.Visible;
    }

    /// <summary>Borra TODO el historial guardado. Pide confirmación: no se puede deshacer.</summary>
    private async Task DeleteAllReportsAsync()
    {
        // Sin raíz visual (la página no está en el árbol todavía) no hay diálogo posible.
        if (XamlRoot == null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = AppBridge.T("¿Borrar los informes guardados?"),
            Content = AppBridge.T("Esto borra del disco todos los informes guardados y no se puede deshacer."),
            PrimaryButtonText = AppBridge.T("Borrar"),
            CloseButtonText = AppBridge.T("Cancelar"),
            // El cierre es la opción por defecto a propósito: borrar tiene que ser una decisión.
            DefaultButton = ContentDialogButton.Close
        };

        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        catch (Exception ex)
        {
            SetStatus(AppBridge.T("No se pudo abrir la confirmación: {0}", ex.Message), warning: true);
            return;
        }
        if (result != ContentDialogResult.Primary) return;

        int deleted = BenchmarkStore.DeleteAll();
        ClearReport();
        RefreshHistory();
        SetStatus(AppBridge.T("Se borraron {0} informes guardados.", deleted), warning: false);
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
        _clearHistoryButton.IsEnabled = files.Count > 0;
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
