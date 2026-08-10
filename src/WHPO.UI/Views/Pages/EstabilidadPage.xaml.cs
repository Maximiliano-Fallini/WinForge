using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Página "Test de estabilidad": los tres gráficos (uso, temperatura y potencia
/// del CPU) muestran métricas en vivo desde que se abre la página, aunque no se
/// haya iniciado ningún test. La card de configuración arranca el test, que pone
/// todos los núcleos al 100% y muestra el tiempo restante.
/// </summary>
public sealed partial class EstabilidadPage : Page
{
    private readonly IStabilityService _stabilityService;
    private readonly ISystemInfoService _systemInfoService;
    private readonly ILoggingService _loggingService;

    private DispatcherQueueTimer? _samplingTimer;
    private bool _sampling;
    private bool _running;
    private bool _subscribed;
    private bool _loaded;
    private System.Diagnostics.PerformanceCounter? _cpuUsageCounter;
    private DispatcherQueueTimer? _countdownTimer;

    // ---- Paleta (misma que la pestaña Núcleos) ----
    private static readonly Color CGreen = Color.FromArgb(255, 0x4C, 0xC2, 0x57);
    private static readonly Color CYellow = Color.FromArgb(255, 0xFF, 0xC9, 0x3C);
    private static readonly Color CRed = Color.FromArgb(255, 0xF0, 0x61, 0x6D);
    private static readonly SolidColorBrush BGreen = new(CGreen);
    private static readonly SolidColorBrush BYellow = new(CYellow);
    private static readonly SolidColorBrush BRed = new(CRed);
    private static readonly SolidColorBrush BGrid = new(Color.FromArgb(255, 0x20, 0x2B, 0x39));
    private static readonly SolidColorBrush BTimeLabel = new(Color.FromArgb(255, 0x8A, 0x94, 0xA6));
    private static readonly SolidColorBrush BCrosshair = new(Color.FromArgb(255, 0x4A, 0x56, 0x66));
    private static readonly SolidColorBrush BHoverBadgeBg = new(Color.FromArgb(255, 0x1C, 0x27, 0x35));
    private static readonly SolidColorBrush BHoverBadgeBorder = new(Color.FromArgb(255, 0x3A, 0x4A, 0x5E));
    private static readonly SolidColorBrush BHoverText = new(Color.FromArgb(255, 0xF2, 0xF4, 0xF8));

    // ---- Gráficos ----
    private MiniChart? _usageChart;
    private MiniChart? _tempChart;
    private MiniChart? _powerChart;

    public EstabilidadPage()
    {
        InitializeComponent();
        _stabilityService = App.Services.GetRequiredService<IStabilityService>();
        _systemInfoService = App.Services.GetRequiredService<ISystemInfoService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();

        Loaded += (s, e) => OnPageLoaded();
        Unloaded += (s, e) => OnPageUnloaded();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadTestTypes();
    }

    private void OnPageLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        // Los gráficos arrancan con su casillero marcado (el compilador XAML
        // clásico de este proyecto no admite IsChecked="True" en el XAML).
        UsageChartCheck.IsChecked = true;
        TempChartCheck.IsChecked = true;
        PowerChartCheck.IsChecked = true;

        _usageChart = new MiniChart(UsagePlot, UsageHover, UsageYAxis, UsageScroll, UsageInner, Color.FromArgb(255, 0x4C, 0xC2, 0xC9), fixedMax: 100, unit: "%");
        _tempChart = new MiniChart(TempPlot, TempHover, TempYAxis, TempScroll, TempInner, Color.FromArgb(255, 0x4C, 0xC2, 0x57), fixedMax: 100, unit: "°C");
        _powerChart = new MiniChart(PowerPlot, PowerHover, PowerYAxis, PowerScroll, PowerInner, Color.FromArgb(255, 0xFF, 0xC9, 0x3C), fixedMax: 0, unit: "W");

        RestoreRunningState();
        Subscribe();
        StartSampling();
    }

    private void OnPageUnloaded()
    {
        if (!_loaded) return;
        _loaded = false;
        Unsubscribe();
        StopSampling();
        StopCountdown();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        _stabilityService.TestCompleted += OnTestCompleted;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        _stabilityService.TestCompleted -= OnTestCompleted;
    }

    // ===================== Muestreo en vivo (siempre activo) =====================

    /// <summary>
    /// La página muestrea las métricas cada segundo desde que se abre, aunque no
    /// haya un test corriendo: los gráficos muestran uso/temperatura/potencia en
    /// vivo siempre. Cuando el test corre, el tiempo restante se toma del servicio.
    /// </summary>
    private void StartSampling()
    {
        if (_samplingTimer == null)
        {
            _samplingTimer = DispatcherQueue.CreateTimer();
            _samplingTimer.Interval = TimeSpan.FromSeconds(1);
            _samplingTimer.Tick += (s, e) =>
            {
                if (_sampling) return;
                if (App.MainWindowInstance is { } w && !w.IsWindowVisible) return;
                _ = SampleAsync();
            };
        }
        _samplingTimer.Start();
        _ = SampleAsync(); // primera muestra inmediata
    }

    private void StopSampling()
    {
        _samplingTimer?.Stop();
    }

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var data = await Task.Run(() =>
            {
                double usage = 0;
                try
                {
                    if (_cpuUsageCounter == null)
                    {
                        _cpuUsageCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                        _cpuUsageCounter.NextValue(); // primera lectura: inicializa el delta
                    }
                    usage = Math.Max(0, _cpuUsageCounter.NextValue());
                }
                catch { }

                double temp = 0, power = 0, freq = 0;
                try { temp = _systemInfoService.GetCpuTemperature(); } catch { }
                try { power = _systemInfoService.GetCpuPower(); } catch { }
                try { freq = _systemInfoService.GetCpuFrequency(); } catch { }
                return (usage, temp, power, freq);
            });

            var now = DateTime.Now;
            _usageChart?.AddSample(now, Math.Clamp(data.usage, 0, 100));
            if (data.temp > 0) _tempChart?.AddSample(now, Math.Min(data.temp, 100));
            if (data.power > 0) _powerChart?.AddSample(now, data.power);

            UsageValueText.Text = $"{data.usage:F0}%";
            TempValueText.Text = data.temp > 0 ? $"{data.temp:F0}°C" : "--°C";
            TempValueText.Foreground = data.temp >= 90 ? BRed : data.temp >= 85 ? BYellow : BGreen;
            PowerValueText.Text = data.power > 0 ? $"{data.power:F0} W" : "-- W";
            LiveValuesText.Text = $"Uso {data.usage:F0}% · {data.temp:F0}°C · {data.power:F0} W · {data.freq:F0} MHz";

            if (_running)
            {
                var last = _stabilityService.LastSample;
                if (last != null)
                    RemainingTimeText.Text = FormatTime(last.Remaining);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Error muestreando métricas en estabilidad: {ex.Message}");
        }
        finally
        {
            _sampling = false;
        }
    }

    // ===================== Configuración =====================

    private void LoadTestTypes()
    {
        TestTypeCombo.Items.Clear();
        foreach (var (type, label) in _stabilityService.GetAvailableTestTypes())
        {
            TestTypeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = type });
        }
        if (TestTypeCombo.Items.Count > 0)
            TestTypeCombo.SelectedIndex = 0;
    }

    private void NumericTextBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        // Solo permitir números, punto decimal y los dos puntos del formato min:seg
        args.Cancel = args.NewText.Any(c => c != '.' && c != ':' && !char.IsDigit(c));
    }

    /// <summary>
    /// Parsea la duración ingresada: minutos simples ("10" o "0.5"), formato
    /// "min:seg" ("10:30"), con milisegundos en los segundos ("10:30.5") e
    /// incluso "h:min:seg" ("1:15:00.25"). Devuelve false si el texto no es una
    /// duración válida dentro de 100 ms y 1440 min.
    /// </summary>
    private static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        if (text.Contains(':'))
        {
            var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts.Length > 3) return false;
            if (parts.Any(p => !double.TryParse(p, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))) return false;

            var nums = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            if (nums.Any(n => n < 0)) return false;

            if (parts.Length == 2)
            {
                if (nums[1] >= 60) return false;
                duration = TimeSpan.FromMinutes(nums[0]) + TimeSpan.FromSeconds(nums[1]);
            }
            else
            {
                if (nums[1] >= 60 || nums[2] >= 60) return false;
                duration = TimeSpan.FromHours(nums[0]) + TimeSpan.FromMinutes(nums[1]) + TimeSpan.FromSeconds(nums[2]);
            }
        }
        else
        {
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double minutes) || minutes < 0)
                return false;
            duration = TimeSpan.FromMinutes(minutes);
        }

        return duration >= TimeSpan.FromMilliseconds(100) && duration <= TimeSpan.FromMinutes(1440);
    }

    // ===================== Botones =====================

    private void StartTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        if (TestTypeCombo.SelectedItem is not ComboBoxItem { Tag: StabilityTestType type })
        {
            TestStatusText.Text = "Elegí un tipo de test.";
            return;
        }

        if (!TryParseDuration(DurationTextBox.Text, out var duration))
        {
            TestStatusText.Text = "Duración inválida: usá min:seg.mmm (0:30 · 10:30.5 · 1:15:00.25), entre 0.1 s y 1440 min.";
            return;
        }

        _usageChart?.Clear();
        _tempChart?.Clear();
        _powerChart?.Clear();

        RemainingTimeText.Text = FormatTime(duration);
        TestStatusText.Text = "Iniciando...";

        SetRunningUi(true);
        _stabilityService.Start(type, duration);
        StartCountdown();
    }

    private void StopTestButton_Click(object sender, RoutedEventArgs e)
    {
        _stabilityService.Stop();
    }

    private void ChartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, UsageChartCheck))
            UsageChartArea.Visibility = UsageChartCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        else if (ReferenceEquals(sender, TempChartCheck))
            TempChartArea.Visibility = TempChartCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        else if (ReferenceEquals(sender, PowerChartCheck))
            PowerChartArea.Visibility = PowerChartCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== Estado / eventos del servicio =====================

    private void SetRunningUi(bool running)
    {
        _running = running;
        StartTestButton.IsEnabled = !running;
        StopTestButton.IsEnabled = running;
        TestTypeCombo.IsEnabled = !running;
        DurationTextBox.IsEnabled = !running;
    }

    /// <summary>
    /// Si el usuario navegó a otra pestaña mientras el test corría, el servicio
    /// siguió trabajando: al volver se restaura el estado y la UI retoma las
    /// muestras (los gráficos arrancan desde cero, el tiempo restante sigue).
    /// </summary>
    private void RestoreRunningState()
    {
        if (_stabilityService.IsRunning)
        {
            SetRunningUi(true);
            StartCountdown();
            var last = _stabilityService.LastSample;
            if (last != null)
            {
                TestStatusText.Text = "Test en ejecución...";
            }
        }
        else
        {
            SetRunningUi(false);
            RemainingTimeText.Text = FormatTime(ParseDurationOrDefault());
            TestStatusText.Text = "Listo para iniciar";
        }
    }

    private TimeSpan ParseDurationOrDefault()
    {
        if (TryParseDuration(DurationTextBox.Text, out var duration))
            return duration;
        return TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Timer fino (50 ms) del contador: mientras el test corre, el tiempo restante
    /// se calcula localmente desde StartTime para que los milisegundos bajen en
    /// vivo (el servicio muestra cada 1 s, no alcanza para verlos).
    /// </summary>
    private void StartCountdown()
    {
        if (_countdownTimer == null)
        {
            _countdownTimer = DispatcherQueue.CreateTimer();
            _countdownTimer.Interval = TimeSpan.FromMilliseconds(50);
            _countdownTimer.Tick += (s, e) =>
            {
                if (!_running) return;
                var remaining = _stabilityService.Duration - (DateTime.Now - _stabilityService.StartTime);
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                RemainingTimeText.Text = FormatTime(remaining);
            };
        }
        _countdownTimer.Start();
    }

    private void StopCountdown()
    {
        _countdownTimer?.Stop();
    }

    private void OnTestCompleted(StabilityTestResult result)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_running && !result.Completed) return;
            SetRunningUi(false);
            StopCountdown();
            RemainingTimeText.Text = "00:00.000";

            if (result.Completed)
            {
                TestStatusText.Text = $"Test completado · uso máx {result.MaxUsagePercent:F0}% · temp máx {result.MaxTempCelsius:F0}°C · potencia máx {result.MaxPowerWatts:F0} W";
            }
            else
            {
                TestStatusText.Text = "Test detenido manualmente.";
            }
        });
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }

    // ===================== Gráfico reutilizable =====================

    /// <summary>
    /// Mini gráfico de línea estilo Administrador de tareas: fondo oscuro,
    /// cuadrícula, eje Y propio (40px), auto-scroll horizontal y etiquetas de
    /// hora. Comparte la estética del gráfico de la pestaña Núcleos.
    /// </summary>
    private sealed class MiniChart
    {
        private const int MaxSamples = 1800;         // 30 min a 1 muestra/seg
        // 11 px por muestra: una marca de 5 s ocupa ~55 px, el espacio justo para
        // que la etiqueta de hora (HH:mm:ss) entre sin superponerse con la siguiente.
        private const double PxPerSample = 11.0;
        private const double PlotTop = 14;
        private const double PlotBottom = 130;        // deja espacio para las horas
        private const double CanvasHeight = 160;

        private readonly Canvas _plot;
        private readonly Canvas _hover;
        private readonly Canvas _yAxis;
        private readonly ScrollViewer _scroll;
        private readonly Grid _inner;
        private readonly Color _color;
        private readonly SolidColorBrush _brush;
        private readonly double _fixedMax;             // 0 = escala automática
        private readonly string _unit;

        private readonly List<(DateTime Time, double Value)> _history = new();
        private double _yMax = 100;
        private double _lastYAxisMax = -1;
        private double _chartWidth = 1200;
        private bool _atRightEdge = true;
        private bool _hoverActive;
        private int _hoverIndex;

        public MiniChart(Canvas plot, Canvas hover, Canvas yAxis, ScrollViewer scroll, Grid inner, Color color, double fixedMax, string unit)
        {
            _plot = plot;
            _hover = hover;
            _yAxis = yAxis;
            _scroll = scroll;
            _inner = inner;
            _color = color;
            _brush = new SolidColorBrush(color);
            _fixedMax = fixedMax;
            _unit = unit;

            _plot.PointerMoved += OnPointerMoved;
            _plot.PointerExited += (s, e) => HideHover();

            _scroll.ViewChanged += (s, e) =>
            {
                _atRightEdge = _scroll.HorizontalOffset >= _scroll.ScrollableWidth - 4;
            };
            _scroll.SizeChanged += (s, e) =>
            {
                if (_chartWidth < _scroll.ViewportWidth - 1) Redraw();
                if (_atRightEdge) ScrollToLatest();
            };
        }

        public void Clear()
        {
            _history.Clear();
            _yMax = _fixedMax > 0 ? _fixedMax : 100;
            _lastYAxisMax = -1;
            _chartWidth = 1200;
            _plot.Children.Clear();
            _hover.Children.Clear();
            _yAxis.Children.Clear();
            _plot.Width = _chartWidth;
            _hover.Width = _chartWidth;
            _inner.Width = _chartWidth;
            _hoverActive = false;
        }

        public void AddSample(DateTime time, double value)
        {
            // Descartar lecturas inválidas del sensor (NaN/Infinito/negativas):
            // un punto malo en la polilínea rompía el render del gráfico.
            if (!double.IsFinite(value) || value < 0) return;
            _history.Add((time, value));
            if (_history.Count > MaxSamples) _history.RemoveAt(0);
            Redraw();
        }

        private double MapY(double v)
        {
            double y = PlotBottom - (v / Math.Max(1, _yMax)) * (PlotBottom - PlotTop);
            return Math.Clamp(y, PlotTop - 40, PlotBottom + 40);
        }

        private double XOf(int i) => i * PxPerSample;

        private IEnumerable<double> YTicks()
        {
            for (double v = 0; v <= _yMax + 0.5; v += 25)
                yield return v;
            if (Math.Abs(_yMax % 25) > 0.5)
                yield return _yMax;
        }

        private void Redraw()
        {
            double maxVal = _history.Count > 0 ? _history.Max(h => h.Value) : 0;
            if (_fixedMax > 0)
            {
                _yMax = _fixedMax;
            }
            else
            {
                // Escala automática con paso de 25 (0/25/50/75...), calculada sobre
                // una ventana reciente (~2 min) y no sobre todo el historial: así un
                // pico transitorio del sensor (o del consumo) no deforma el eje
                // durante todo el gráfico y la escala vuelve sola a la normalidad.
                int windowStart = Math.Max(0, _history.Count - 120);
                for (int i = windowStart; i < _history.Count; i++)
                    maxVal = Math.Max(maxVal, _history[i].Value);
                _yMax = Math.Max(25, Math.Ceiling((maxVal + 10) / 25.0) * 25.0);
            }

            double viewport = _scroll.ViewportWidth > 10 ? _scroll.ViewportWidth : 600;
            _chartWidth = Math.Min(MaxSamples * PxPerSample, Math.Max(viewport, _history.Count * PxPerSample));

            _plot.Width = _chartWidth;
            _hover.Width = _chartWidth;
            _inner.Width = _chartWidth;

            _plot.Children.Clear();
            DrawGrid();
            DrawLine();
            DrawTimeLabels();
            DrawYAxis();

            if (_hoverActive) UpdateHoverVisuals();
            if (_atRightEdge) ScrollToLatest();
        }

        private void DrawGrid()
        {
            foreach (double v in YTicks())
            {
                var line = new Rectangle { Width = _chartWidth, Height = 1, Fill = BGrid };
                Canvas.SetTop(line, MapY(v));
                _plot.Children.Add(line);
            }

            // Líneas verticales cada 5 segundos
            int? lastFive = null;
            for (int i = 0; i < _history.Count; i++)
            {
                int five = _history[i].Time.Second / 5;
                if (lastFive != five)
                {
                    double x = XOf(i);
                    var vl = new Rectangle { Width = 1, Height = PlotBottom - PlotTop, Fill = BGrid };
                    Canvas.SetLeft(vl, x);
                    Canvas.SetTop(vl, PlotTop);
                    _plot.Children.Add(vl);
                    lastFive = five;
                }
            }
        }

        private void DrawLine()
        {
            if (_history.Count == 0) return;

            if (_history.Count == 1)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Fill = _brush };
                Canvas.SetLeft(dot, XOf(0) - 3);
                Canvas.SetTop(dot, MapY(_history[0].Value) - 3);
                _plot.Children.Add(dot);
                return;
            }

            var fillPts = new PointCollection();
            fillPts.Add(new Point(XOf(0), PlotBottom));
            for (int i = 0; i < _history.Count; i++)
                fillPts.Add(new Point(XOf(i), MapY(_history[i].Value)));
            fillPts.Add(new Point(XOf(_history.Count - 1), PlotBottom));
            var fillColor = Color.FromArgb(0x2E, _color.R, _color.G, _color.B);
            _plot.Children.Add(new Polygon { Points = fillPts, Fill = new SolidColorBrush(fillColor) });

            var pts = new PointCollection();
            for (int i = 0; i < _history.Count; i++)
                pts.Add(new Point(XOf(i), MapY(_history[i].Value)));
            _plot.Children.Add(new Polyline
            {
                Points = pts,
                Stroke = _brush,
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            });
        }

        // ===================== Hover (mismo comportamiento que Núcleos) =====================

        private int FindNearestIndex(double x)
        {
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < _history.Count; i++)
            {
                double d = Math.Abs(XOf(i) - x);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_history.Count == 0) { HideHover(); return; }

            var pos = e.GetCurrentPoint(_plot).Position;
            int idx = FindNearestIndex(pos.X);

            // Solo mostrar el hover dentro del tramo dibujado
            if (pos.X < XOf(0) - PxPerSample || pos.X > XOf(_history.Count - 1) + PxPerSample)
            {
                HideHover();
                return;
            }

            _hoverActive = true;
            _hoverIndex = idx;
            UpdateHoverVisuals();
        }

        private void HideHover()
        {
            if (!_hoverActive) return;
            _hoverActive = false;
            _hover.Children.Clear();
        }

        private void UpdateHoverVisuals()
        {
            _hover.Children.Clear();
            if (!_hoverActive || _history.Count == 0) return;
            if (_hoverIndex < 0 || _hoverIndex >= _history.Count) return;

            var (time, value) = _history[_hoverIndex];
            double x = XOf(_hoverIndex);
            double y = MapY(value);

            // Línea de cruce vertical
            var cross = new Rectangle { Width = 1, Height = PlotBottom - PlotTop, Fill = BCrosshair };
            Canvas.SetLeft(cross, x);
            Canvas.SetTop(cross, PlotTop);
            _hover.Children.Add(cross);

            // Punto sobre la curva
            var dot = new Ellipse
            {
                Width = 9, Height = 9,
                Fill = _brush,
                Stroke = new SolidColorBrush(Color.FromArgb(255, 0x0D, 0x14, 0x1E)),
                StrokeThickness = 2
            };
            Canvas.SetLeft(dot, x - 4.5);
            Canvas.SetTop(dot, y - 4.5);
            _hover.Children.Add(dot);

            // Etiqueta: valor exacto + hora en la que se tomó
            var tb = new TextBlock
            {
                Text = $"{value:F0}{_unit} · {time:HH:mm:ss}",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = BHoverText
            };
            var badge = new Border
            {
                Background = BHoverBadgeBg,
                BorderBrush = BHoverBadgeBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 4, 8, 4),
                Child = tb
            };
            double bx = x + 12;
            if (bx + 150 > _chartWidth) bx = x - 150; // no salirse del borde derecho
            Canvas.SetLeft(badge, Math.Max(4, bx));
            Canvas.SetTop(badge, Math.Max(PlotTop, y - 34));
            _hover.Children.Add(badge);
        }

        private void DrawTimeLabels()
        {
            if (_history.Count == 0) return;

            // Etiqueta cada 5 segundos, sin superponerse: se dibuja la marca de 5 s
            // solo si hay suficiente espacio desde la etiqueta anterior (y sin pisar
            // el marcador "ahora" del extremo derecho).
            // OJO: lastFive arranca en el bucket de la PRIMERA muestra para que la
            // etiqueta aparezca recién en el primer cambio de bucket (x>0), no en
            // x=0 cortada contra el borde izquierdo.
            int? lastFive = _history[0].Time.Second / 5;
            double lastLabelX = double.NegativeInfinity;
            for (int i = 0; i < _history.Count; i++)
            {
                var t = _history[i].Time;
                int five = t.Second / 5;
                if (lastFive == five) continue;
                lastFive = five;
                double x = XOf(i);
                if (x < 28) continue;
                if (x > _chartWidth - 80) continue;
                if (x - lastLabelX < 55) continue;
                lastLabelX = x;
                var tb = new TextBlock
                {
                    Text = t.ToString("HH:mm:ss"),
                    FontSize = 10,
                    Foreground = BTimeLabel
                };
                Canvas.SetLeft(tb, Math.Max(0, x - 24));
                Canvas.SetTop(tb, PlotBottom + 8);
                _plot.Children.Add(tb);
            }

            if (_history.Count > 0)
            {
                var now = new TextBlock
                {
                    Text = _history[_history.Count - 1].Time.ToString("HH:mm:ss"),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BTimeLabel
                };
                Canvas.SetLeft(now, _chartWidth - 52);
                Canvas.SetTop(now, PlotBottom + 8);
                _plot.Children.Add(now);
            }
        }

        private void DrawYAxis()
        {
            if (_yMax == _lastYAxisMax) return;
            _lastYAxisMax = _yMax;

            _yAxis.Children.Clear();
            foreach (double v in YTicks())
            {
                var tb = new TextBlock
                {
                    Text = $"{v:F0}{_unit}",
                    FontSize = 10,
                    Foreground = BTimeLabel,
                    Width = 40,
                    TextAlignment = TextAlignment.Center
                };
                double y = MapY(v);
                Canvas.SetLeft(tb, 0);
                Canvas.SetTop(tb, Math.Max(0, y - 7));
                _yAxis.Children.Add(tb);
            }
        }

        private void ScrollToLatest()
        {
            try { _scroll.ChangeView(_scroll.ScrollableWidth, null, null, true); }
            catch { }
        }
    }
}
