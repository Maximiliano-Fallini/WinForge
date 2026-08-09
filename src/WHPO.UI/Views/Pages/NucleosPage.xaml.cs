using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Página "Núcleos y Plan de energía": gráfico de temperatura del CPU estilo
/// Administrador de tareas (cuadrícula, timeline con scroll horizontal), cards
/// con uso/temperatura/clock (mín/máx mientras la pestaña está abierta) y barras
/// por núcleo con estado de parking real (0% de uso no implica estacionado).
/// </summary>
public sealed partial class NucleosPage : Page
{
    private readonly ISystemInfoService _systemInfoService;
    private readonly ICpuPowerService _cpuPowerService;
    private readonly ILoggingService _loggingService;

    private DispatcherQueueTimer? _samplingTimer;
    private bool _sampling;
    private bool _started;

    // ---- Gráfico de temperatura ----
    private const int ChartMaxSamples = 1200;        // ~20 min a 1 muestra/seg
    private const double ChartPxPerSample = 3.0;     // píxeles por muestra
    private const double PlotTop = 18;
    private const double PlotBottom = 196;           // deja espacio abajo para las horas
    private readonly List<(DateTime Time, double Temp)> _history = new();
    private double _yMax = 100;
    private double _lastYAxisMax = -1;
    private double _chartWidth = 1200;
    private bool _atRightEdge = true;

    // ---- Hover del gráfico (temperatura exacta + hora) ----
    private bool _hoverActive;
    private int _hoverIndex;

    // ---- Pulso de "Cargando…" en la card de temperatura ----
    private Storyboard? _loadingPulse;

    // ---- Última muestra (para re-aplicar al cambiar de tema) ----
    private double[] _lastUsages = Array.Empty<double>();
    private double[] _lastCoreTemps = Array.Empty<double>();
    private bool[]? _lastParked;

    // ---- Min/Max (solo mientras la pestaña está abierta) ----
    private bool _haveStats;
    private double _usageMin, _usageMax, _tempMin, _tempMax, _clockMin, _clockMax;

    // ---- Paleta ----
    private static readonly Color CGreen = Color.FromArgb(255, 0x4C, 0xC2, 0x57);
    private static readonly Color CYellow = Color.FromArgb(255, 0xFF, 0xC9, 0x3C);
    private static readonly Color CRed = Color.FromArgb(255, 0xF0, 0x61, 0x6D);
    private static readonly SolidColorBrush BGreen = new(CGreen);
    private static readonly SolidColorBrush BYellow = new(CYellow);
    private static readonly SolidColorBrush BRed = new(CRed);
    private static readonly SolidColorBrush BGrid = new(Color.FromArgb(255, 0x20, 0x2B, 0x39));
    private static readonly SolidColorBrush BCrosshair = new(Color.FromArgb(255, 0x4A, 0x56, 0x66));
    private static readonly SolidColorBrush BChartBg = new(Color.FromArgb(255, 0x0D, 0x14, 0x1E));
    private static readonly SolidColorBrush BChartBgHot = new(Color.FromArgb(255, 0x1E, 0x0F, 0x13));
    private static readonly SolidColorBrush BTimeLabel = new(Color.FromArgb(255, 0x8A, 0x94, 0xA6));
    private static readonly SolidColorBrush BThermalLine = new(Color.FromArgb(120, 0xFF, 0xC9, 0x3C));
    private static readonly SolidColorBrush BNeutral = new(Color.FromArgb(255, 0x8A, 0x94, 0xA6));

    // ---- Cards de núcleos: fondo y pista según el tema (claro/oscuro) ----
    private static readonly Dictionary<ElementTheme, SolidColorBrush> CoreCardBrushes = new()
    {
        [ElementTheme.Dark] = new(Color.FromArgb(255, 0x20, 0x24, 0x2B)),
        [ElementTheme.Light] = new(Color.FromArgb(255, 0xF4, 0xF6, 0xF8))
    };
    private static readonly Dictionary<ElementTheme, SolidColorBrush> CoreTrackBrushes = new()
    {
        [ElementTheme.Dark] = new(Color.FromArgb(255, 0x15, 0x19, 0x20)),
        [ElementTheme.Light] = new(Color.FromArgb(255, 0xE6, 0xEA, 0xEF))
    };
    private SolidColorBrush CoreCardBrush => CoreCardBrushes[ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark];
    private SolidColorBrush CoreTrackBrush => CoreTrackBrushes[ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark];

    // ---- Barras por núcleo ----
    private readonly List<CoreBar> _coreBars = new();

    private sealed class CoreBar
    {
        public Border Fill = null!;
        public TextBlock Temp = null!;
        public TextBlock Status = null!;
        public double TrackHeight;
    }

    public NucleosPage()
    {
        InitializeComponent();
        _systemInfoService = App.Services.GetRequiredService<ISystemInfoService>();
        _cpuPowerService = App.Services.GetRequiredService<ICpuPowerService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();

        ChartScroll.ViewChanged += ChartScroll_ViewChanged;
        ChartScroll.SizeChanged += (s, e) =>
        {
            // Tras el primer layout el viewport real puede ser mayor que el ancho
            // inicial del lienzo: re-anclar el gráfico al borde derecho.
            if (_chartWidth < ChartScroll.ViewportWidth - 1)
                RedrawChart();
            if (_atRightEdge) ScrollToLatest();
        };
        ChartCanvas.PointerMoved += ChartCanvas_PointerMoved;
        ChartCanvas.PointerExited += ChartCanvas_PointerExited;

        // Al cambiar el tema, re-crear las cards de núcleos con los colores nuevos.
        ActualThemeChanged += (s, e) =>
        {
            if (_coreBars.Count > 0)
            {
                RebuildCoreBars(_coreBars.Count);
                UpdateCoreBars(_lastUsages, _lastCoreTemps, _lastParked);
            }
        };

        Unloaded += (s, e) => StopSampling();
    }

    // ===================== Ciclo de vida =====================

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Cada vez que se abre la pestaña: estadísticas desde cero
        ResetStats();
        StartSampling();

        // Planes de energía (powercfg, no requiere admin)
        _ = LoadPowerPlansAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopSampling();
    }

    private void StartSampling()
    {
        if (_started) return;
        _started = true;

        // Calentamiento: la PRIMERA lectura de los contadores de uso por núcleo
        // devuelve ~0% (necesita un intervalo entre lecturas para calcular el delta).
        // Se hace un pase previo en segundo plano y se espera ~400ms antes de la
        // primera muestra visible, para que las cards no arranquen en "0%" / "--".
        _ = WarmupAndStartAsync();
    }

    private async Task WarmupAndStartAsync()
    {
        await Task.Run(() =>
        {
            try { _systemInfoService.GetCpuCoreUsages(); } catch { }
            try { _systemInfoService.GetCpuCoreParkedStatus(); } catch { }
            Thread.Sleep(400);
        });
        if (!_started) return;

        if (_samplingTimer == null)
        {
            _samplingTimer = DispatcherQueue.CreateTimer();
            _samplingTimer.Interval = TimeSpan.FromSeconds(1);
            _samplingTimer.Tick += OnSamplingTick;
        }
        _samplingTimer.Start();
        _ = SampleAsync(); // primera muestra inmediata
    }

    private void StopSampling()
    {
        _started = false;
        _samplingTimer?.Stop();
    }

    private void OnSamplingTick(DispatcherQueueTimer sender, object args)
    {
        if (_sampling) return;
        // Ventana en bandeja: no gastar CPU/recursos en segundo plano
        if (App.MainWindowInstance is { } w && !w.IsWindowVisible) return;
        _ = SampleAsync();
    }

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var data = await Task.Run(() =>
            {
                double[] usages = _systemInfoService.GetCpuCoreUsages();
                double[] coreTemps = _systemInfoService.GetCpuCoreTemperatures();
                bool[]? parked = _systemInfoService.GetCpuCoreParkedStatus();
                double temp = _systemInfoService.GetCpuTemperature();
                double clock = _systemInfoService.GetCpuFrequency();
                return (usages, coreTemps, parked, temp, clock);
            });

            if (DispatcherQueue.HasThreadAccess)
                ApplySample(data.usages, data.coreTemps, data.parked, data.temp, data.clock);
            else
                DispatcherQueue.TryEnqueue(() => ApplySample(data.usages, data.coreTemps, data.parked, data.temp, data.clock));
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error muestreando CPU: {ex.Message}");
        }
        finally
        {
            _sampling = false;
        }
    }

    private void ApplySample(double[] usages, double[] coreTemps, bool[]? parked, double temp, double clock)
    {
        try
        {
            _lastUsages = usages;
            _lastCoreTemps = coreTemps;
            _lastParked = parked;

            // ---- Cards: uso, temperatura y clock (con mín/máx) ----
            double usage = usages.Length > 0 ? usages.Average() : 0;
            UpdateStats(usage, temp, clock);

            // Hasta que haya un sample real, no mostrar un "0%" engañoso
            CpuUsageValueText.Text = _haveStats ? $"{usage:F0}%" : "--%";
            CpuUsageBar.Value = _haveStats ? Math.Clamp(usage, 0, 100) : 0;
            CpuUsageMinText.Text = _haveStats ? $"{_usageMin:F0}%" : "--%";
            CpuUsageMaxText.Text = _haveStats ? $"{_usageMax:F0}%" : "--%";

            if (temp > 0)
            {
                StopTempLoadingPulse();
                CpuTempValueText.Text = $"{temp:F0}°C";
                CpuTempValueText.Foreground = PickThermalBrush(temp);
            }
            else
            {
                // Sensor aún cargando (driver de LHM): estado visible y pulsante, no un "--" confuso
                CpuTempValueText.Text = "Cargando…";
                CpuTempValueText.Foreground = BTimeLabel;
                StartTempLoadingPulse();
            }
            CpuTempMinText.Text = _haveStats && _tempMin > 0 ? $"{_tempMin:F0}°C" : "--°C";
            CpuTempMaxText.Text = _haveStats && _tempMax > 0 ? $"{_tempMax:F0}°C" : "--°C";

            CpuClockValueText.Text = FormatFreq(clock);
            CpuClockMinText.Text = _haveStats && _clockMin > 0 ? FormatFreq(_clockMin) : "--";
            CpuClockMaxText.Text = _haveStats && _clockMax > 0 ? FormatFreq(_clockMax) : "--";

            // ---- Gráfico de temperatura ----
            if (temp > 0)
            {
                _history.Add((DateTime.Now, temp));
                while (_history.Count > ChartMaxSamples)
                    _history.RemoveAt(0);
                RedrawChart();
            }

            // ---- Núcleos ----
            if (usages.Length > 0)
            {
                if (_coreBars.Count != usages.Length)
                    RebuildCoreBars(usages.Length);
                UpdateCoreBars(usages, coreTemps, parked);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error aplicando muestra: {ex.Message}");
        }
    }

    // ===================== Pulso de carga =====================

    private void StartTempLoadingPulse()
    {
        if (_loadingPulse != null) return;
        var anim = new DoubleAnimation
        {
            From = 0.35,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(700)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(anim, CpuTempValueText);
        Storyboard.SetTargetProperty(anim, "Opacity");
        _loadingPulse = new Storyboard();
        _loadingPulse.Children.Add(anim);
        _loadingPulse.Begin();
    }

    private void StopTempLoadingPulse()
    {
        if (_loadingPulse == null) return;
        _loadingPulse.Stop();
        _loadingPulse = null;
        CpuTempValueText.Opacity = 1.0;
    }

    // ===================== Min/Max =====================

    private void UpdateStats(double usage, double temp, double clock)
    {
        if (!_haveStats)
        {
            _haveStats = true;
            _usageMin = _usageMax = usage;
            _tempMin = _tempMax = temp;
            _clockMin = _clockMax = clock;
            return;
        }
        if (usage < _usageMin) _usageMin = usage;
        if (usage > _usageMax) _usageMax = usage;
        if (temp > 0)
        {
            if (_tempMin <= 0 || temp < _tempMin) _tempMin = temp;
            if (temp > _tempMax) _tempMax = temp;
        }
        if (clock > 0)
        {
            if (_clockMin <= 0 || clock < _clockMin) _clockMin = clock;
            if (clock > _clockMax) _clockMax = clock;
        }
    }

    private void ResetStats()
    {
        _haveStats = false;
        _usageMin = _usageMax = _tempMin = _tempMax = _clockMin = _clockMax = 0;
        StopTempLoadingPulse();
        _history.Clear();
        _coreBars.Clear();
        CoresHost.Items.Clear();
        _lastYAxisMax = -1;
        YAxisCanvas.Children.Clear();
        ChartCanvas.Children.Clear();
        ChartCanvas.Background = BChartBg;
        ThermalWarningBadge.Visibility = Visibility.Collapsed;
        RedrawChart();
    }

    // ===================== Gráfico =====================

    private double CurrentTemp()
        => _history.Count > 0 ? _history[_history.Count - 1].Temp : 0;

    private double MapY(double value)
        => PlotBottom - (value / Math.Max(1, _yMax)) * (PlotBottom - PlotTop);

    private static Color PickThermalColor(double temp)
    {
        if (temp >= 90) return CRed;
        if (temp >= 85) return CYellow;
        return CGreen;
    }

    private static SolidColorBrush PickThermalBrush(double temp)
    {
        if (temp >= 90) return BRed;
        if (temp >= 85) return BYellow;
        return BGreen;
    }

    private void RedrawChart()
    {
        double maxTemp = _history.Count > 0 ? _history.Max(h => h.Temp) : 0;
        // Escala fija mínima de 95°C (por debajo del umbral térmico) para que el eje
        // Y siempre muestre 0/25/50/75/95 y la línea no quede pegada arriba.
        _yMax = Math.Max(95, Math.Ceiling((maxTemp + 10) / 25.0) * 25.0);

        // Ancho del lienzo: el gráfico crece de izquierda a derecha desde el
        // viewport; cuando la línea llega al límite, el lienzo sigue creciendo y
        // aparece el scroll horizontal para corroborar temperaturas anteriores.
        double viewport = ChartScroll.ViewportWidth > 10 ? ChartScroll.ViewportWidth : 600;
        _chartWidth = Math.Min(ChartMaxSamples * ChartPxPerSample,
                               Math.Max(viewport, _history.Count * ChartPxPerSample));

        ChartCanvas.Width = _chartWidth;
        ChartInner.Width = _chartWidth;
        HoverCanvas.Width = _chartWidth;

        ChartCanvas.Background = CurrentTemp() >= 90 ? BChartBgHot : BChartBg;

        ChartCanvas.Children.Clear();

        DrawGrid();
        DrawLine();
        DrawTimeLabels();
        DrawYAxis();
        UpdateThermalBadge();

        if (_hoverActive) UpdateHoverVisuals();
        if (_atRightEdge) ScrollToLatest();
    }

    /// <summary>
    /// Posición X de la muestra i dentro del lienzo: el gráfico crece de izquierda
    /// a derecha (la primera muestra arranca a la izquierda). Cuando el lienzo crece
    /// más allá del viewport, el auto-scroll deja la muestra más reciente a la derecha
    /// y el scroll permite volver a las temperaturas anteriores.
    /// </summary>
    private double XOf(int i)
        => i * ChartPxPerSample;

    /// <summary>
    /// Valores del eje Y: cada 25°C (0/25/50/75…) y la marca superior (95°C)
    /// cuando la escala no es múltiplo de 25.
    /// </summary>
    private IEnumerable<double> YTicks()
    {
        for (double v = 0; v <= _yMax + 0.5; v += 25)
            yield return v;
        if (Math.Abs(_yMax % 25) > 0.5)
            yield return _yMax;
    }

    private void DrawGrid()
    {
        // Líneas horizontales (cuadrícula) cada 25°C
        foreach (double v in YTicks())
        {
            var line = new Rectangle { Width = _chartWidth, Height = 1, Fill = BGrid };
            Canvas.SetTop(line, MapY(v));
            ChartCanvas.Children.Add(line);
        }

        // Línea de referencia a 85°C (umbral de warning)
        if (_yMax >= 85)
        {
            var threshold = new Line
            {
                X1 = 0, X2 = _chartWidth, Y1 = MapY(85), Y2 = MapY(85),
                Stroke = BThermalLine, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 5, 5 }
            };
            ChartCanvas.Children.Add(threshold);
        }

        // Líneas verticales cada minuto
        int? lastMinute = null;
        for (int i = 0; i < _history.Count; i++)
        {
            int minute = _history[i].Time.Minute;
            if (lastMinute != minute)
            {
                double x = XOf(i);
                var vl = new Rectangle { Width = 1, Height = PlotBottom - PlotTop, Fill = BGrid };
                Canvas.SetLeft(vl, x);
                Canvas.SetTop(vl, PlotTop);
                ChartCanvas.Children.Add(vl);
                lastMinute = minute;
            }
        }
    }

    private void DrawLine()
    {
        if (_history.Count == 0) return;

        if (_history.Count == 1)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = PickThermalBrush(CurrentTemp()) };
            Canvas.SetLeft(dot, XOf(0) - 3);
            Canvas.SetTop(dot, MapY(_history[0].Temp) - 3);
            ChartCanvas.Children.Add(dot);
            return;
        }

        var brush = PickThermalBrush(CurrentTemp());
        var color = PickThermalColor(CurrentTemp());

        // Relleno translúcido bajo la curva (se agrega antes que la línea)
        var fillPts = new PointCollection();
        fillPts.Add(new Point(XOf(0), PlotBottom));
        for (int i = 0; i < _history.Count; i++)
            fillPts.Add(new Point(XOf(i), MapY(_history[i].Temp)));
        fillPts.Add(new Point(XOf(_history.Count - 1), PlotBottom));
        var fillColor = Color.FromArgb(0x2E, color.R, color.G, color.B);
        ChartCanvas.Children.Add(new Polygon { Points = fillPts, Fill = new SolidColorBrush(fillColor) });

        // Línea de temperatura
        var pts = new PointCollection();
        for (int i = 0; i < _history.Count; i++)
            pts.Add(new Point(XOf(i), MapY(_history[i].Temp)));
        ChartCanvas.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = brush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    private void DrawTimeLabels()
    {
        int? lastMinute = null;
        for (int i = 0; i < _history.Count; i++)
        {
            var t = _history[i].Time;
            int minute = t.Minute;
            if (lastMinute != minute)
            {
                lastMinute = minute;
                double x = XOf(i);

                // No dibujar la etiqueta de minuto si se superpone con el marcador
                // "ahora" del extremo derecho (en el arranque de cada minuto coinciden
                // y el tiempo se veía "bugueado" por la superposición).
                if (x > _chartWidth - 80) continue;

                var tb = new TextBlock
                {
                    Text = t.ToString("HH:mm:ss"),
                    FontSize = 10,
                    Foreground = BTimeLabel
                };
                Canvas.SetLeft(tb, Math.Max(0, x - 24));
                Canvas.SetTop(tb, PlotBottom + 8);
                ChartCanvas.Children.Add(tb);
            }
        }

        // Hora actual en el extremo inferior derecho (marcador "ahora")
        // OJO: no va arriba a la derecha — ahí se superpone el aviso térmico.
        if (_history.Count > 0)
        {
            var now = new TextBlock
            {
                Text = _history[_history.Count - 1].Time.ToString("HH:mm:ss"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0xD5, 0xDC, 0xE5))
            };
            Canvas.SetLeft(now, _chartWidth - 52);
            Canvas.SetTop(now, PlotBottom + 8);
            ChartCanvas.Children.Add(now);
        }
    }

    private void DrawYAxis()
    {
        // Solo se reconstruye cuando cambia la escala del eje Y
        if (_yMax == _lastYAxisMax) return;
        _lastYAxisMax = _yMax;

        YAxisCanvas.Children.Clear();
        foreach (double v in YTicks())
        {
            // Números centrados en el ancho del eje (40px) y alineados a su línea de cuadrícula.
            var tb = new TextBlock
            {
                Text = $"{v:F0}°",
                FontSize = 10,
                Foreground = BTimeLabel,
                Width = 40,
                TextAlignment = TextAlignment.Center
            };
            double y = MapY(v);
            Canvas.SetLeft(tb, 0);
            Canvas.SetTop(tb, Math.Max(0, y - 7));
            YAxisCanvas.Children.Add(tb);
        }
    }

    private void UpdateThermalBadge()
    {
        double t = CurrentTemp();
        if (t >= 90)
        {
            ThermalWarningBadge.Visibility = Visibility.Visible;
            ThermalWarningBadge.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xF0, 0x61, 0x6D));
            ThermalWarningBadge.BorderBrush = BRed;
            ThermalWarningIcon.Foreground = BRed;
            ThermalWarningText.Foreground = BRed;
            ThermalWarningText.Text = "Thermal throttling inminente";
        }
        else if (t >= 85)
        {
            ThermalWarningBadge.Visibility = Visibility.Visible;
            ThermalWarningBadge.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xC9, 0x3C));
            ThermalWarningBadge.BorderBrush = BYellow;
            ThermalWarningIcon.Foreground = BYellow;
            ThermalWarningText.Foreground = BYellow;
            ThermalWarningText.Text = "Posible Thermal throttling";
        }
        else
        {
            ThermalWarningBadge.Visibility = Visibility.Collapsed;
        }
    }

    // ===================== Hover del gráfico =====================

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

    private void ChartCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_history.Count == 0) { HideHover(); return; }

        var pos = e.GetCurrentPoint(ChartCanvas).Position;
        int idx = FindNearestIndex(pos.X);

        // Solo mostrar el hover dentro del tramo dibujado
        if (pos.X < XOf(0) - ChartPxPerSample || pos.X > XOf(_history.Count - 1) + ChartPxPerSample)
        {
            HideHover();
            return;
        }

        _hoverActive = true;
        _hoverIndex = idx;
        UpdateHoverVisuals();
    }

    private void ChartCanvas_PointerExited(object sender, PointerRoutedEventArgs e) => HideHover();

    private void HideHover()
    {
        if (!_hoverActive) return;
        _hoverActive = false;
        HoverCanvas.Children.Clear();
    }

    private void UpdateHoverVisuals()
    {
        HoverCanvas.Children.Clear();
        if (!_hoverActive || _history.Count == 0) return;
        if (_hoverIndex < 0 || _hoverIndex >= _history.Count) return;

        var (time, temp) = _history[_hoverIndex];
        double x = XOf(_hoverIndex);
        double y = MapY(temp);

        // Línea de cruce vertical
        var cross = new Rectangle { Width = 1, Height = PlotBottom - PlotTop, Fill = BCrosshair };
        Canvas.SetLeft(cross, x);
        Canvas.SetTop(cross, PlotTop);
        HoverCanvas.Children.Add(cross);

        // Punto sobre la curva
        var dot = new Ellipse
        {
            Width = 9, Height = 9,
            Fill = PickThermalBrush(temp),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 0x0D, 0x14, 0x1E)),
            StrokeThickness = 2
        };
        Canvas.SetLeft(dot, x - 4.5);
        Canvas.SetTop(dot, y - 4.5);
        HoverCanvas.Children.Add(dot);

        // Etiqueta: temperatura exacta + hora en la que se tomó
        var tb = new TextBlock
        {
            Text = $"{temp:F0}°C · {time:HH:mm:ss}",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0xF2, 0xF4, 0xF8))
        };
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 0x1C, 0x27, 0x35)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x3A, 0x4A, 0x5E)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Child = tb
        };
        double bx = x + 12;
        if (bx + 150 > _chartWidth) bx = x - 150; // no salirse del borde derecho
        Canvas.SetLeft(badge, Math.Max(4, bx));
        Canvas.SetTop(badge, Math.Max(PlotTop, y - 34));
        HoverCanvas.Children.Add(badge);
    }

    private void ChartScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        _atRightEdge = ChartScroll.HorizontalOffset >= ChartScroll.ScrollableWidth - 6;
    }

    private void ScrollToLatest()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try { ChartScroll.ChangeView(ChartScroll.ScrollableWidth, null, null, true); }
            catch { }
        });
    }

    // ===================== Núcleos =====================

    private void RebuildCoreBars(int count)
    {
        CoresHost.Items.Clear();
        _coreBars.Clear();

        for (int i = 0; i < count; i++)
        {
            var bar = new CoreBar();
            const double trackH = 104;

            var name = new TextBlock
            {
                Text = $"Núcleo {i}",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            bar.Fill = new Border
            {
                Width = 34,
                Height = 0,
                CornerRadius = new CornerRadius(6),
                Background = BGreen,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            var track = new Border
            {
                Width = 34,
                Height = trackH,
                CornerRadius = new CornerRadius(6),
                Background = CoreTrackBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = bar.Fill
            };

            bar.Temp = new TextBlock
            {
                Text = "--",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BTimeLabel
            };

            bar.Status = new TextBlock
            {
                Text = "—",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BNeutral
            };

            var sp = new StackPanel { Spacing = 8 };
            sp.Children.Add(name);
            sp.Children.Add(track);
            sp.Children.Add(bar.Temp);
            sp.Children.Add(bar.Status);

            var root = new Border
            {
                Background = CoreCardBrush,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Width = 116,
                Margin = new Thickness(0, 0, 10, 10),
                Child = sp
            };

            bar.TrackHeight = trackH;
            _coreBars.Add(bar);
            CoresHost.Items.Add(root);
        }
    }

    private void UpdateCoreBars(double[] usages, double[] coreTemps, bool[]? parked)
    {
        for (int i = 0; i < _coreBars.Count && i < usages.Length; i++)
        {
            var bar = _coreBars[i];

            double u = Math.Clamp(usages[i], 0, 100);
            bar.Fill.Height = Math.Max(3, u / 100.0 * bar.TrackHeight);

            // Estado de parking REAL (el contador distingue estacionado de 0% de uso)
            bool? isParked = parked != null && i < parked.Length ? parked[i] : null;
            if (isParked == true)
            {
                bar.Fill.Background = BRed;
                bar.Status.Text = "Estacionado";
                bar.Status.Foreground = BRed;
            }
            else if (isParked == false)
            {
                bar.Fill.Background = BGreen;
                bar.Status.Text = "Activo";
                bar.Status.Foreground = BGreen;
            }
            else
            {
                bar.Fill.Background = BNeutral;
                bar.Status.Text = "—";
                bar.Status.Foreground = BNeutral;
            }

            // Temperatura del núcleo físico (con SMT varios hilos comparten sensor).
            // Si el hardware no expone sensores por núcleo (p. ej. este Ryzen 5 7600
            // solo expone Tctl/Tdie), el campo de temperatura se oculta directamente.
            if (coreTemps.Length > 0)
            {
                bar.Temp.Visibility = Visibility.Visible;
                double t = coreTemps[i % coreTemps.Length];
                bar.Temp.Text = t > 0 ? $"{t:F0}°C" : "--";
            }
            else
            {
                bar.Temp.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ===================== Plan de energía =====================

    private async Task LoadPowerPlansAsync()
    {
        try
        {
            // GetPowerPlans ya maneja sus errores internamente y devuelve lista vacía.
            var plans = await Task.Run(() => _cpuPowerService.GetPowerPlans());

            int activeIndex = -1;
            PowerPlanCombo.Items.Clear();
            for (int i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                PowerPlanCombo.Items.Add(new ComboBoxItem
                {
                    Content = plan.IsActive ? $"{plan.Name}  (activo)" : plan.Name,
                    Tag = plan.Guid
                });
                if (plan.IsActive) activeIndex = i;
            }

            PowerPlanCombo.SelectedIndex = activeIndex >= 0 ? activeIndex : (plans.Count > 0 ? 0 : -1);
            ApplyPowerPlanButton.IsEnabled = PowerPlanCombo.SelectedIndex >= 0;
            // No pisar el mensaje de confirmación/error cuando la carga es exitosa.
            if (plans.Count == 0)
                PowerPlanStatusText.Text = "No se detectaron planes de energía.";
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error cargando planes de energía: {ex.Message}");
            PowerPlanStatusText.Text = "No se pudieron cargar los planes de energía.";
        }
    }

    private void PowerPlanCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Solo habilita el botón: no limpiar el estado aquí, porque la recarga
        // programática de items (Items.Clear) borraría el mensaje de confirmación.
        ApplyPowerPlanButton.IsEnabled = PowerPlanCombo.SelectedItem != null;
    }

    private async void ApplyPowerPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (PowerPlanCombo.SelectedItem is not ComboBoxItem { Tag: string planGuid })
            return;
        var planName = (PowerPlanCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? planGuid;

        ApplyPowerPlanButton.IsEnabled = false;
        PowerPlanStatusText.Text = "Aplicando plan...";
        try
        {
            var result = await _cpuPowerService.SetActivePowerPlanAsync(planGuid);

            // Recargar primero (refresca el marcador "(activo)") y mostrar el resultado
            // DESPUÉS, para que el mensaje de confirmación no se borre con la recarga.
            if (result.Success)
                await LoadPowerPlansAsync();

            PowerPlanStatusText.Foreground = result.Success ? BGreen : BRed;
            PowerPlanStatusText.Text = result.Success
                ? $"✓ Plan de energía establecido: {planName}"
                : $"✗ {result.Output}";
        }
        catch (Exception ex)
        {
            PowerPlanStatusText.Foreground = BRed;
            PowerPlanStatusText.Text = $"✗ {ex.Message}";
            _loggingService.LogWarning($"NucleosPage: error aplicando plan de energía: {ex.Message}");
        }
        finally
        {
            ApplyPowerPlanButton.IsEnabled = PowerPlanCombo.SelectedIndex >= 0;
        }
    }

    // ===================== Helpers =====================

    private static string FormatFreq(double mhz)
    {
        if (mhz <= 0) return "--";
        return mhz >= 1000 ? $"{mhz / 1000.0:F2} GHz" : $"{mhz:F0} MHz";
    }
}
