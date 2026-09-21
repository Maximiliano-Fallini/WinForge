using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WHPO.Core.Components;

namespace WinForge.Component.LatencyMeter;

/// <summary>
/// Componente "Medidor de latencia" (inspirado en LatencyMon): mide en tiempo real
/// la duración de DPCs e ISRs del kernel vía ETW, muestra un gráfico de barras
/// (verde &lt; 500 µs, amarillo &lt; 1000 µs, rojo ≥ 1000 µs — umbral de cortes de
/// audio) y un ranking de drivers.
///
/// UI construida 100% en código (sin XAML compilado — ver DemoComponent).
///
/// Integración con la app: este proyecto SOLO referencia WHPO.Core (el contrato),
/// así que I18n/ThemeBrushes de la app se resuelven por reflexión sobre el
/// assembly que está corriendo (Application.Current.GetType().Assembly), con
/// fallbacks seguros si no están. Así la página se traduce y reacciona al tema
/// con el mismo motor que el resto de la app sin acoplarse a su assembly.
/// </summary>
public sealed class LatencyMeterComponent : IWinForgeComponent
{
    public string Id => "latencia";
    public string Name => "Medidor de latencia";
    public string Description => "Mide la latencia de DPC/ISR del kernel en vivo y encuentra los drivers que la causan.";
    public string IconGlyph => "\uE9D2"; // Diagnostic
    public ComponentCategory Category => ComponentCategory.Latencia;
    public string Version => "1.0.1";
    public string MinAppVersion => "0.0.0";
    public bool IsCore => false;

    public object CreatePage(IServiceProvider services) => new LatencyMeterPage();
}

/// <summary>Página principal del medidor.</summary>
public sealed class LatencyMeterPage : Page
{
    private readonly DpcIsrTrace _trace = new();

    // ===== Integración por reflexión con la app (I18n / ThemeBrushes) =====
    private static MethodInfo? _i18nT;
    private static MethodInfo? _themeGet;
    private static EventInfo? _langChanged;
    private static bool _integrationResolved;

    private static void ResolveIntegration()
    {
        if (_integrationResolved) return;
        _integrationResolved = true;
        try
        {
            var appAsm = Application.Current.GetType().Assembly;
            var i18n = appAsm.GetType("WHPO_UI.I18n", throwOnError: false);
            _i18nT = i18n?.GetMethod("T", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            var theme = appAsm.GetType("WHPO_UI.ThemeBrushes", throwOnError: false);
            _themeGet = theme?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            _langChanged = i18n?.GetEvent("LanguageChanged", BindingFlags.Public | BindingFlags.Static);
        }
        catch { _i18nT = null; _themeGet = null; _langChanged = null; }
    }

    /// <summary>Traduce con el motor de la app; sin clave/ sin app devuelve el texto fuente.</summary>
    private static string T(string s)
    {
        ResolveIntegration();
        try { return _i18nT?.Invoke(null, new object[] { s }) as string ?? s; }
        catch { return s; }
    }

    /// <summary>Pincel de tema resuelto con el tema EFECTIVO (ThemeBrushes.Get de la app);
    /// fallback: recursos raíz de la app (los de feedback: Success/Error/Warning están ahí).</summary>
    private static Brush Brush(string key)
    {
        ResolveIntegration();
        try
        {
            if (_themeGet?.Invoke(null, new object[] { key }) is Brush b) return b;
        }
        catch { }
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush fb) return fb;
        }
        catch { }
        return new SolidColorBrush(Windows.UI.Color.FromArgb(128, 128, 128, 128));
    }

    // Métricas
    private readonly TextBlock _peakText = new() { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _avgText = new() { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _p99Text = new() { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _dpcRateText = new() { FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _isrRateText = new() { FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _worstDriverText = new() { FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, IsTextSelectionEnabled = true };

    private readonly TextBlock _resultText = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed
    };

    private readonly Button _startStopButton = new()
    {
        MinWidth = 140,
        Padding = new Thickness(16, 8, 16, 8),
        CornerRadius = new CornerRadius(6),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
    };

    private readonly StackPanel _driverList = new() { Spacing = 6 };
    private readonly Canvas _chart = new() { Height = 120 };
    private TextBlock? _chartEmpty;

    // Buffer circular del gráfico: una barra por tick (~90 barras deslizantes).
    private const int BarCount = 90;
    private readonly double[] _bars = new double[BarCount];
    private int _barIndex;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _uiTimer;

    // Acciones para re-aplicar textos al cambiar el idioma (equivalente a
    // I18n.LanguageChanged de las páginas built-in).
    private readonly List<Action> _retranslate = new();
    private bool _langSubscribed;

    public LatencyMeterPage()
    {
        // ===== Header =====
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new FontIcon { Glyph = "\uE9D2", FontSize = 20 });
        var titleText = new TextBlock { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(titleText);

        // ===== Cards =====
        var cards = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < 3; i++) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cards.Children.Add(MakeCard("Pico de DPC", _peakText, 0));
        cards.Children.Add(MakeCard("Promedio", _avgText, 1));
        cards.Children.Add(MakeCard("Percentil 99", _p99Text, 2));

        // ===== Contadores =====
        var counters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 28 };
        counters.Children.Add(Labeled("DPC/s", _dpcRateText));
        counters.Children.Add(Labeled("ISR/s", _isrRateText));
        counters.Children.Add(Labeled("Peor driver", _worstDriverText));

        // ===== Controles =====
        _startStopButton.Click += OnStartStop;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _startStopButton } };

        // ===== Gráfico =====
        var gridLines = new Grid { IsHitTestVisible = false };
        for (int i = 1; i <= 3; i++)
        {
            gridLines.Children.Add(new Rectangle
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 120 * i / 4.0),
                Fill = Brush("CardBorderBrush"),
                Opacity = 0.5
            });
        }
        _chartEmpty = new TextBlock
        {
            Text = T("Iniciá la medición para ver el gráfico en vivo."),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var chartHost = new Grid();
        chartHost.Children.Add(gridLines);
        chartHost.Children.Add(_chart);
        chartHost.Children.Add(_chartEmpty);
        var chartCard = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
            Height = 150,
            Child = chartHost
        };

        // ===== Ranking =====
        var rankingLabel = new TextBlock { FontSize = 12 };
        var rankingCard = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
            Child = _driverList
        };

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(24),
                Children =
                {
                    header,
                    _resultText,
                    cards,
                    counters,
                    controls,
                    chartCard,
                    rankingLabel,
                    rankingCard
                }
            }
        };

        // ===== Retraducción al cambiar de idioma =====
        _retranslate.Add(() =>
        {
            titleText.Text = T("Medidor de latencia");
            ApplyCardTitles();
            rankingLabel.Text = T("Drivers por tiempo total de DPC");
            if (_chartEmpty != null) _chartEmpty.Text = T("Iniciá la medición para ver el gráfico en vivo.");
            _startStopButton.Content = T(_trace.IsRunning ? "Detener" : "Iniciar");
        });
        try
        {
            ResolveIntegration();
            if (_langChanged != null && !_langSubscribed)
            {
                // El evento es Action: se puede suscribir un delegado directo.
                var self = this;
                _langChanged.AddEventHandler(null, new Action(() =>
                {
                    self.DispatcherQueue.TryEnqueue(() => { foreach (var a in self._retranslate) a(); });
                }));
                _langSubscribed = true;
            }
        }
        catch { }

        // ===== Aplicar tema/idioma inicial =====
        ApplyTheme();
        foreach (var a in _retranslate) a();

        _uiTimer = DispatcherQueue.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _uiTimer.Tick += OnTick;
    }

    private readonly TextBlock[] _cardTitles = new TextBlock[3];

    private Border MakeCard(string titleKey, TextBlock value, int col)
    {
        var title = new TextBlock { FontSize = 12 };
        _cardTitles[Math.Min(2, _cardTitleCount++)] = title;
        var b = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1)
        };
        b.Child = new StackPanel { Spacing = 2, Children = { title, value } };
        Grid.SetColumn(b, col);
        return b;
    }

    private int _cardTitleCount;

    private void ApplyCardTitles()
    {
        string[] keys = { "Pico de DPC", "Promedio", "Percentil 99" };
        for (int i = 0; i < _cardTitles.Length && i < 3; i++)
            if (_cardTitles[i] != null) _cardTitles[i].Text = T(keys[i]);
    }

    private StackPanel Labeled(string titleKey, TextBlock value)
    {
        var title = new TextBlock { FontSize = 12, Text = T(titleKey) };
        _retranslate.Add(() => title.Text = T(titleKey));
        return new StackPanel { Spacing = 2, Children = { title, value } };
    }

    /// <summary>Aplica pinceles del tema a todos los elementos (llamado al construir;
    /// los pinceles live de la app repintan solos al cambiar de tema).</summary>
    private void ApplyTheme()
    {
        _startStopButton.Background = Brush("AccentBrush");
        _startStopButton.Foreground = Brush("AccentForegroundBrush");
        _resultText.Foreground = Brush("TextFillColorSecondaryBrush");
        foreach (var title in _cardTitles) if (title != null) title.Foreground = Brush("TextFillColorSecondaryBrush");
        if (_chartEmpty != null) _chartEmpty.Foreground = Brush("TextFillColorSecondaryBrush");
    }

    /// <summary>Mensaje de resultado local (equivalente a Feedback con pinceles raíz
    /// de la app, que no dependen del tema).</summary>
    private void ShowResult(bool ok, string messageKey)
    {
        _resultText.Text = (ok ? "✓ " : "✗ ") + T(messageKey);
        _resultText.Foreground = ok ? Brush("SuccessBrush") : Brush("ErrorBrush");
        _resultText.Visibility = Visibility.Visible;
    }

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_trace.IsRunning)
        {
            _trace.Stop();
            SetRunning(false);
            ShowResult(true, "Medición detenida.");
            return;
        }

        _trace.Reset();
        ClearBars();
        if (!_trace.Start())
        {
            // Mensaje accionable: el motor expone el código exacto de la API que falló.
            ShowResult(false, $"No se pudo iniciar la sesión de tracing. {_trace.LastError}");
            SetRunning(false);
            return;
        }
        SetRunning(true);
        ShowResult(true, "Medición activa. El pico se mantiene hasta que la reinicies.");
    }

    private void SetRunning(bool running)
    {
        _startStopButton.Content = T(running ? "Detener" : "Iniciar");
        _startStopButton.Background = running ? Brush("ErrorBrush") : Brush("AccentBrush");
        _startStopButton.Foreground = running
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
            : Brush("AccentForegroundBrush");
        if (_chartEmpty != null) _chartEmpty.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _uiTimer.Interval = TimeSpan.FromMilliseconds(running ? 250 : 1000);
        if (running) _uiTimer.Start(); else _uiTimer.Stop();
    }

    private void OnTick(object? sender, object e)
    {
        _trace.TickRate();
        var snap = _trace.GetSnapshot();

        _peakText.Text = FmtUs(snap.MaxDpcUs);
        _peakText.Foreground = ColorForUs(snap.MaxDpcUs);
        _avgText.Text = FmtUs(snap.AvgDpcUs);
        _p99Text.Text = FmtUs(snap.P99DpcUs);
        _dpcRateText.Text = $"{snap.DpcCount:N0} ({snap.DpcPerSec}/s)";
        _isrRateText.Text = $"{snap.IsrCount:N0}";
        _worstDriverText.Text = snap.WorstModule.Length > 0 ? snap.WorstModule : "—";
        _worstDriverText.Foreground = ColorForUs(snap.MaxDpcUs);

        _bars[_barIndex % BarCount] = snap.WindowMaxUs;
        _barIndex++;
        DrawChart();
        RebuildDriverList(snap.TopDrivers);
    }

    private void DrawChart()
    {
        _chart.Children.Clear();
        double w = _chart.ActualWidth, h = _chart.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double maxUs = 1000; // escala fija: 1000 µs = umbral rojo
        foreach (var v in _bars) if (v > maxUs) maxUs = v;

        double step = w / BarCount;
        double barW = Math.Max(1.5, step - 1);
        for (int i = 0; i < BarCount; i++)
        {
            double v = _bars[(_barIndex + i) % BarCount];
            if (v <= 0) continue;
            double barH = Math.Max(1, Math.Min(1.0, v / maxUs) * h);
            var rect = new Rectangle { Width = barW, Height = barH, Fill = ColorForUs(v), RadiusX = 1, RadiusY = 1 };
            Canvas.SetLeft(rect, i * step);
            Canvas.SetTop(rect, h - barH);
            _chart.Children.Add(rect);
        }
    }

    private void RebuildDriverList(IReadOnlyList<DpcIsrTrace.DriverRow> top)
    {
        _driverList.Children.Clear();
        if (top.Count == 0)
        {
            _driverList.Children.Add(new TextBlock
            {
                Text = T("Sin datos todavía."),
                FontSize = 12,
                Foreground = Brush("TextFillColorSecondaryBrush")
            });
            return;
        }
        double maxTotal = Math.Max(1e-6, top[0].TotalDpcUs);
        foreach (var s in top)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(At(new TextBlock { Text = s.Module, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 0));
            row.Children.Add(At(new TextBlock { Text = s.DpcCount.ToString("N0"), FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center }, 1));
            row.Children.Add(At(new TextBlock { Text = FmtUs(s.MaxDpcUs), FontSize = 12, Foreground = ColorForUs(s.MaxDpcUs), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 2));

            var barHost = new Grid
            {
                Children =
                {
                    new Border { Background = Brush("CardBorderBrush"), CornerRadius = new CornerRadius(2), Height = 8, VerticalAlignment = VerticalAlignment.Center },
                    new Border { Background = ColorForUs(s.MaxDpcUs), CornerRadius = new CornerRadius(2), Height = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(2, s.TotalDpcUs * 200.0 / maxTotal) }
                }
            };
            Grid.SetColumn(barHost, 3);
            row.Children.Add(barHost);
            _driverList.Children.Add(row);
        }
    }

    private static T At<T>(T el, int col) where T : FrameworkElement { Grid.SetColumn(el, col); return el; }

    private void ClearBars() { Array.Clear(_bars); _barIndex = 0; _chart.Children.Clear(); }

    private static string FmtUs(double us) => us >= 1000 ? $"{us / 1000:F2} ms" : $"{us:F0} µs";

    private static Brush ColorForUs(double us) => us switch
    {
        >= 1000 => Brush("ErrorBrush"),
        >= 500 => Brush("WarningBrush"),
        _ => Brush("SuccessBrush")
    };
}
