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
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private readonly IWinUtilService _winUtilService;
    private readonly ILoggingService _loggingService;

    private DispatcherQueueTimer? _samplingTimer;
    private bool _sampling;
    private bool _started;
    private bool _contentShown;

    // ---- Navbar interno (Núcleos / Gestionar / Comparar) ----
    private int _selectedTabIndex;
    private bool _manageLoaded;
    private bool _compareLoaded;

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

    // ---- Texto secundario según el tema (claro/oscuro) ----
    private static readonly Dictionary<ElementTheme, SolidColorBrush> SecondaryBrushes = new()
    {
        [ElementTheme.Dark] = new(Color.FromArgb(255, 0x9A, 0xA0, 0xA6)),
        [ElementTheme.Light] = new(Color.FromArgb(255, 0x5D, 0x63, 0x6B))
    };
    private SolidColorBrush SecondaryBrush => SecondaryBrushes[ActualTheme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark];

    // ---- Paleta: los colores viven en los recursos de tema de la app (claro/oscuro).
    // Se resuelven con el tema EFECTIVO (ThemeBrushes), no con el del sistema. ----
    private static SolidColorBrush ThemeBrush(string key) => ThemeBrushes.Get(key);

    private static Color CGreen => ThemeBrush("MetricTempBrush").Color;
    private static Color CYellow => ThemeBrush("MetricPowerBrush").Color;
    private static Color CRed => ThemeBrush("ErrorBrush").Color;
    private static SolidColorBrush BGreen => ThemeBrush("MetricTempBrush");
    private static SolidColorBrush BYellow => ThemeBrush("MetricPowerBrush");
    private static SolidColorBrush BRed => ThemeBrush("ErrorBrush");
    private static SolidColorBrush BGrid => ThemeBrush("ChartGridBrush");
    private static SolidColorBrush BCrosshair => ThemeBrush("ChartCrosshairBrush");
    private static SolidColorBrush BChartBg => ThemeBrush("ChartBackgroundBrush");
    private static SolidColorBrush BChartBgHot => ThemeBrush("ChartBackgroundHotBrush");
    private static SolidColorBrush BTimeLabel => ThemeBrush("ChartAxisTextBrush");
    private static SolidColorBrush BNeutral => ThemeBrush("ChartAxisTextBrush");
    private static SolidColorBrush BChartHoverText => ThemeBrush("ChartHoverTextBrush");
    private static SolidColorBrush BChartHoverBadgeBg => ThemeBrush("ChartHoverBadgeBgBrush");
    private static SolidColorBrush BChartHoverBadgeBorder => ThemeBrush("ChartHoverBadgeBorderBrush");
    private static SolidColorBrush BThermalLine
    {
        get
        {
            var c = ThemeBrush("MetricPowerBrush").Color;
            return new SolidColorBrush(Color.FromArgb(120, c.R, c.G, c.B));
        }
    }

    // ---- Cards de núcleos: fondo y pista desde los recursos de tema (en el tema base
    // coinciden con los valores originales; las variantes los redefinen). ----
    private static SolidColorBrush CoreCardBrush => ThemeBrushes.Get("CoreCardBackgroundBrush");
    private static SolidColorBrush CoreTrackBrush => ThemeBrushes.Get("CoreTrackBackgroundBrush");

    // ---- Barras por núcleo ----
    private readonly List<CoreBar> _coreBars = new();

    private sealed class CoreBar
    {
        public Border Fill = null!;
        public TextBlock Usage = null!;
        public TextBlock Temp = null!;
        public TextBlock Status = null!;
        public double TrackHeight;
    }

    public NucleosPage()
    {
        InitializeComponent();
        _systemInfoService = App.Services.GetRequiredService<ISystemInfoService>();
        _cpuPowerService = App.Services.GetRequiredService<ICpuPowerService>();
        _winUtilService = App.Services.GetRequiredService<IWinUtilService>();
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

        // Al cambiar el tema o el idioma, re-crear las cards de núcleos.
        ActualThemeChanged += (s, e) =>
        {
            if (_coreBars.Count > 0)
            {
                RebuildCoreBars(_coreBars.Count);
                UpdateCoreBars(_lastUsages, _lastCoreTemps, _lastParked);
            }
        };
        // La página no se cachea: desuscribirse al desmontar para no filtrar memoria
        // (el evento de idioma es estático y vive toda la app).
        I18n.LanguageChanged += ReapplyLanguageToCores;

        Unloaded += (s, e) =>
        {
            StopSampling();
            I18n.LanguageChanged -= ReapplyLanguageToCores;
        };
    }

    // ===================== Ciclo de vida =====================

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Cada vez que se abre la pestaña: estadísticas desde cero
        ResetStats();
        ShowSkeleton();
        StartSampling();
        _ = ForceShowContentAfterTimeoutAsync();

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

    private void ReapplyLanguageToCores()
    {
        if (_coreBars.Count > 0)
        {
            RebuildCoreBars(_coreBars.Count);
            UpdateCoreBars(_lastUsages, _lastCoreTemps, _lastParked);
        }
    }

    // ===================== Skeleton de carga =====================

    private void ShowSkeleton()
    {
        _contentShown = false;
        if (PageSkeleton == null || PageContent == null || GestionarTab == null || CompararTab == null) return;
        PageSkeleton.Visibility = Visibility.Visible;
        PageContent.Visibility = Visibility.Collapsed;
        GestionarTab.Visibility = Visibility.Collapsed;
        CompararTab.Visibility = Visibility.Collapsed;
    }

    private void ShowContent()
    {
        if (_contentShown) return;
        _contentShown = true;
        if (PageSkeleton == null) return;
        PageSkeleton.Visibility = Visibility.Collapsed;
        ApplyTabVisibility();
        LazyLoadTab(_selectedTabIndex);
    }

    // Seguridad: si el primer sample tarda más de 4s (sensor lento), el skeleton
    // se reemplaza igual para no dejar la página en "cargando" para siempre.
    private async Task ForceShowContentAfterTimeoutAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (!_contentShown)
            DispatcherQueue.TryEnqueue(ShowContent);
    }

    // ===================== Navbar interno =====================

    private void PlanTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (PlanTabs == null || PlanTabs.Items.Count == 0) return;
        int idx = PlanTabs.SelectedItem != null ? PlanTabs.Items.IndexOf(PlanTabs.SelectedItem) : 0;
        _selectedTabIndex = idx < 0 ? 0 : idx;

        // Mientras el skeleton está visible no se revela contenido de ninguna pestaña.
        if (!_contentShown) return;
        ApplyTabVisibility();
        LazyLoadTab(_selectedTabIndex);
    }

    private void ApplyTabVisibility()
    {
        if (PageContent == null || GestionarTab == null || CompararTab == null) return;
        PageContent.Visibility = _selectedTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        GestionarTab.Visibility = _selectedTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        CompararTab.Visibility = _selectedTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LazyLoadTab(int index)
    {
        if (index == 1 && !_manageLoaded)
        {
            _manageLoaded = true;
            _ = LoadManagePlansAsync();
            _ = LoadInstallPresetsAsync();
        }
        if (index == 2 && !_compareLoaded)
        {
            _compareLoaded = true;
            LoadCompareCombos();
        }
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
                double temp = _systemInfoService.GetCpuTemperatureFresh();
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
            ShowContent();
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
                CpuTempValueText.Text = I18n.T("Cargando…");
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
                ChartCanvas.Children.Add(vl);
                lastFive = five;
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
            ChartCanvas.Children.Add(tb);
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
                Foreground = BChartHoverText
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
            ThermalWarningText.Text = I18n.T("Thermal throttling inminente");
        }
        else if (t >= 85)
        {
            ThermalWarningBadge.Visibility = Visibility.Visible;
            ThermalWarningBadge.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xC9, 0x3C));
            ThermalWarningBadge.BorderBrush = BYellow;
            ThermalWarningIcon.Foreground = BYellow;
            ThermalWarningText.Foreground = BYellow;
            ThermalWarningText.Text = I18n.T("Posible Thermal throttling");
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
            Stroke = BChartBg,
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
            Foreground = BChartHoverText
        };
        var badge = new Border
        {
            Background = BChartHoverBadgeBg,
            BorderBrush = BChartHoverBadgeBorder,
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
                Text = $"{I18n.T("Núcleo")} {i}",
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

            bar.Usage = new TextBlock
            {
                Text = "--%",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BGreen
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
            sp.Children.Add(bar.Usage);
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
            bar.Usage.Text = $"{u:F0}%";

            // Estado de parking REAL (el contador distingue estacionado de 0% de uso)
            bool? isParked = parked != null && i < parked.Length ? parked[i] : null;
            if (isParked == true)
            {
                bar.Fill.Background = BRed;
                bar.Usage.Foreground = BRed;
                bar.Status.Text = I18n.T("Estacionado");
                bar.Status.Foreground = BRed;
            }
            else if (isParked == false)
            {
                bar.Fill.Background = BGreen;
                bar.Usage.Foreground = BGreen;
                bar.Status.Text = I18n.T("Activo");
                bar.Status.Foreground = BGreen;
            }
            else
            {
                bar.Fill.Background = BNeutral;
                bar.Usage.Foreground = BNeutral;
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

    // El popup del ComboBox abre con un VerticalOffset que WinUI 3 calcula mal cuando
    // el combo está dentro del Frame/ScrollViewer: queda desplazado hacia arriba. En
    // cada apertura alineamos el tope del popup con el tope del combo (igual que el de
    // "Tipo de test"), una vez que el popup ya está medido.
    private async void PowerPlanCombo_DropDownOpened(object sender, object e)
    {
        var combo = (ComboBox)sender;
        try
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(16);
                var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(combo.XamlRoot);
                if (popups.Count == 0 || popups[0].Child is not FrameworkElement fe || fe.ActualHeight <= 0)
                    continue;

                var popup = popups[0];
                var popupPos = fe.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                var comboPos = combo.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                double delta = comboPos.Y - popupPos.Y;
                if (Math.Abs(delta) < 0.5) break;
                popup.VerticalOffset += delta;
                break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"POWERPLAN reposition: {ex.Message}");
        }
    }

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
                    Content = plan.IsActive ? I18n.T("{0}  (activo)", plan.Name) : plan.Name,
                    Tag = plan.Guid
                });
                if (plan.IsActive) activeIndex = i;
            }

            PowerPlanCombo.SelectedIndex = activeIndex >= 0 ? activeIndex : (plans.Count > 0 ? 0 : -1);
            ApplyPowerPlanButton.IsEnabled = PowerPlanCombo.SelectedIndex >= 0;
            // No pisar el mensaje de confirmación/error cuando la carga es exitosa.
            if (plans.Count == 0)
                Feedback.Warning(PowerPlanStatusText, "No se detectaron planes de energía.");
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error cargando planes de energía: {ex.Message}");
            Feedback.Warning(PowerPlanStatusText, "No se pudieron cargar los planes de energía.");
        }
    }

    private async void ApplyPowerPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (PowerPlanCombo.SelectedItem is not ComboBoxItem { Tag: string planGuid })
            return;
        var planName = (PowerPlanCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? planGuid;

        ApplyPowerPlanButton.IsEnabled = false;            Feedback.Running(PowerPlanStatusText, I18n.T("Aplicando plan..."));
        try
        {
            var result = await _cpuPowerService.SetActivePowerPlanAsync(planGuid);

            // Recargar primero (refresca el marcador "(activo)") y mostrar el resultado
            // DESPUÉS, para que el mensaje de confirmación no se borre con la recarga.
            if (result.Success)
                await LoadPowerPlansAsync();

            if (result.Success)
                Feedback.Success(PowerPlanStatusText, I18n.T("Plan de energía establecido: {0}", planName));
            else
                Feedback.Result(PowerPlanStatusText, result);
        }
        catch (Exception ex)
        {
            Feedback.Error(PowerPlanStatusText, ex.Message);
            _loggingService.LogWarning($"NucleosPage: error aplicando plan de energía: {ex.Message}");
        }
        finally
        {
            ApplyPowerPlanButton.IsEnabled = PowerPlanCombo.SelectedIndex >= 0;
        }
    }

    // ===================== Gestionar planes de energía =====================

    private sealed class PlanListItem
    {
        public string Guid { get; }
        public string Name { get; }
        public string Description { get; }
        public bool IsActive { get; }

        public PlanListItem(string guid, string name, string description, bool isActive)
        {
            Guid = guid;
            Name = name;
            Description = description;
            IsActive = isActive;
        }
    }

    private async Task LoadManagePlansAsync()
    {
        try
        {
            var plans = await Task.Run(() => _cpuPowerService.GetPowerPlans());

            var items = new List<PlanListItem>();
            foreach (var plan in plans)
            {
                var desc = await Task.Run(() => _cpuPowerService.GetPowerPlanDescription(plan.Guid));
                items.Add(new PlanListItem(plan.Guid, plan.Name, desc, plan.IsActive));
            }

            PlansList.Items.Clear();
            foreach (var item in items)
                PlansList.Items.Add(BuildManageRow(item));

            if (items.Count == 0)
            {
                ManagePlanStatusText.Visibility = Visibility.Visible;
                Feedback.Warning(ManagePlanStatusText, "No se detectaron planes de energía.");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error cargando gestión de planes: {ex.Message}");
            ManagePlanStatusText.Visibility = Visibility.Visible;
            Feedback.Warning(ManagePlanStatusText, "No se pudieron cargar los planes de energía.");
        }
    }

    private static ListViewItem BuildManageRow(PlanListItem item)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var guid = new TextBlock
        {
            Text = item.Guid,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(guid, 1);
        row.Children.Add(guid);

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Description) ? "—" : item.Description,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(desc, 2);
        row.Children.Add(desc);

        var active = new TextBlock
        {
            Text = item.IsActive ? I18n.T("✓ Activo") : "",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = item.IsActive ? BGreen : BTimeLabel
        };
        Grid.SetColumn(active, 3);
        row.Children.Add(active);

        return new ListViewItem { Content = row, Tag = item, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private void PlansList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = (PlansList.SelectedItem as ListViewItem)?.Tag as PlanListItem;
        bool has = item != null;
        RenamePlanButton.IsEnabled = has;
        ActivatePlanButton.IsEnabled = has && !item!.IsActive;
        DeletePlanButton.IsEnabled = has && !item!.IsActive;
    }

    private void SetManageStatus(bool success, string message, string? template = null, object?[]? args = null)
    {
        var text = template != null
            ? I18n.T(template, args ?? Array.Empty<object?>())
            : I18n.T(message);
        if (success)
            Feedback.Success(ManagePlanStatusText, text);
        else
            Feedback.Error(ManagePlanStatusText, text);
    }

    private async void RenamePlanButton_Click(object sender, RoutedEventArgs e)
    {
        var item = (PlansList.SelectedItem as ListViewItem)?.Tag as PlanListItem;
        if (item == null || XamlRoot == null) return;

        var nameBox = new TextBox
        {
            Text = item.Name,
            Header = I18n.T("Nuevo nombre"),
            MaxLength = 255
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Renombrar plan"),
            Content = nameBox,
            PrimaryButtonText = I18n.T("Renombrar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var newName = nameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            SetManageStatus(false, "El nombre no puede estar vacío.");
            return;
        }

        var result = await _cpuPowerService.RenamePowerPlanAsync(item.Guid, newName);
        SetManageStatus(result.Success, result.Output, result.MessageTemplate, result.MessageArgs);
        if (result.Success)
        {
            await LoadManagePlansAsync();
            _ = LoadPowerPlansAsync(); // refrescar el selector de la pestaña Núcleos
        }
    }

    private async void ActivatePlanButton_Click(object sender, RoutedEventArgs e)
    {
        var item = (PlansList.SelectedItem as ListViewItem)?.Tag as PlanListItem;
        if (item == null) return;

        ActivatePlanButton.IsEnabled = false;
        Feedback.Running(ManagePlanStatusText, "Activando plan...");
        var result = await _cpuPowerService.SetActivePowerPlanAsync(item.Guid);
        SetManageStatus(result.Success, result.Output, result.MessageTemplate, result.MessageArgs);
        if (result.Success)
        {
            await LoadManagePlansAsync();
            _ = LoadPowerPlansAsync(); // refrescar el selector de la pestaña Núcleos
        }
    }

    private async void OpenPowerOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _winUtilService.LaunchPanelAsync("power");
            if (!result.Success) SetManageStatus(false, result.Output, result.MessageTemplate, result.MessageArgs);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error abriendo planes de energía: {ex.Message}");
            SetManageStatus(false, ex.Message);
        }
    }

    private async void DeletePlanButton_Click(object sender, RoutedEventArgs e)
    {
        var item = (PlansList.SelectedItem as ListViewItem)?.Tag as PlanListItem;
        if (item == null || XamlRoot == null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Borrar plan de energía"),
            Content = $"{I18n.T("¿Seguro que querés borrar ")}\"{item.Name}\"{I18n.T("? Esta acción no se puede deshacer.")}",
            PrimaryButtonText = I18n.T("Borrar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        DeletePlanButton.IsEnabled = false;
        var result = await _cpuPowerService.DeletePowerPlanAsync(item.Guid);
        SetManageStatus(result.Success, result.Output, result.MessageTemplate, result.MessageArgs);
        if (result.Success)
        {
            await LoadManagePlansAsync();
            _ = LoadPowerPlansAsync(); // refrescar el selector de la pestaña Núcleos
        }
    }

    // ===================== Instalar planes de energía =====================

    // GUIDs de subgrupos/settings de energía (documentados por Microsoft).
    private const string SubProcessorGuid = "54533251-82be-4824-96c1-47b60b740d00";
    private const string ProcThrottleMinGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string ProcThrottleMaxGuid = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    private const string PerfBoostModeGuid = "be337238-0d82-4146-a960-4f3749d470c7";
    private const string PerfAutonomousGuid = "8baa4a8a-14c6-4451-8e8b-14bdbd197537";

    // Esquemas base de Windows.
    private const string BalancedSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerfSchemeGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string PowerSaverSchemeGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
    private const string UltimatePerfSchemeGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private sealed class InstallPreset
    {
        public string Name { get; }
        public string Description { get; }
        public string Category { get; }
        public string? BuiltInGuid { get; }
        public string? BaseGuid { get; }
        public PowerPlanTuning[]? Tunings { get; }

        public InstallPreset(string name, string description, string category, string? builtInGuid = null, string? baseGuid = null, PowerPlanTuning[]? tunings = null)
        {
            Name = name;
            Description = description;
            Category = category;
            BuiltInGuid = builtInGuid;
            BaseGuid = baseGuid;
            Tunings = tunings;
        }
    }

    private sealed class InstallRowItem
    {
        public InstallPreset Preset { get; }
        public bool Installed { get; }

        public InstallRowItem(InstallPreset preset, bool installed)
        {
            Preset = preset;
            Installed = installed;
        }
    }

    private static List<InstallPreset> BuildInstallPresets() => new()
    {
        new InstallPreset(I18n.T("Máximo rendimiento"), I18n.T("Plan oculto de Windows para máximo desempeño (Ultimate Performance)."), I18n.T("Oficial"), builtInGuid: UltimatePerfSchemeGuid),
        new InstallPreset(I18n.T("Alto rendimiento"), I18n.T("Plan oficial de Windows para alto desempeño."), I18n.T("Oficial"), builtInGuid: HighPerfSchemeGuid),
        new InstallPreset(I18n.T("Ahorro de energía"), I18n.T("Plan oficial de Windows para reducir el consumo."), I18n.T("Oficial"), builtInGuid: PowerSaverSchemeGuid),
        new InstallPreset(I18n.T("Equilibrado"), I18n.T("Plan oficial de Windows: balance entre rendimiento y consumo."), I18n.T("Oficial"), builtInGuid: BalancedSchemeGuid),
        new InstallPreset(I18n.T("Ryzen Balanced"), I18n.T("Preset estilo 1usmus para CPU AMD Ryzen: mínimo 99%, boost eficiente agresivo y modo autónomo desactivado."), I18n.T("Ryzen"), baseGuid: BalancedSchemeGuid, tunings: new[]
        {
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMinGuid, 99, 99),
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMaxGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, PerfBoostModeGuid, 4, 4),
            new PowerPlanTuning(SubProcessorGuid, PerfAutonomousGuid, 0, 0)
        }),
        new InstallPreset(I18n.T("Ryzen High Performance"), I18n.T("Preset estilo 1usmus para CPU AMD Ryzen: mínimo 100%, boost agresivo y modo autónomo desactivado."), I18n.T("Ryzen"), baseGuid: HighPerfSchemeGuid, tunings: new[]
        {
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMinGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMaxGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, PerfBoostModeGuid, 2, 2),
            new PowerPlanTuning(SubProcessorGuid, PerfAutonomousGuid, 0, 0)
        }),
        new InstallPreset(I18n.T("Bitsum Highest Performance"), I18n.T("Preset de Bitsum (Process Lasso/ParkControl): mínimo 100% para evitar el parking de núcleos y boost agresivo."), I18n.T("Comunidad"), baseGuid: HighPerfSchemeGuid, tunings: new[]
        {
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMinGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMaxGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, PerfBoostModeGuid, 2, 2)
        }),
        new InstallPreset(I18n.T("Bitsum Balanced"), I18n.T("Preset de Bitsum para uso general: mínimo 5% y boost eficiente habilitado."), I18n.T("Comunidad"), baseGuid: BalancedSchemeGuid, tunings: new[]
        {
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMinGuid, 5, 5),
            new PowerPlanTuning(SubProcessorGuid, ProcThrottleMaxGuid, 100, 100),
            new PowerPlanTuning(SubProcessorGuid, PerfBoostModeGuid, 3, 3)
        })
    };

    private async Task LoadInstallPresetsAsync()
    {
        try
        {
            var plans = await Task.Run(() => _cpuPowerService.GetPowerPlans());
            var presets = BuildInstallPresets();

            InstallPlansList.Items.Clear();
            foreach (var preset in presets)
            {
                bool installed = preset.BuiltInGuid != null
                    ? plans.Any(p => string.Equals(p.Guid, preset.BuiltInGuid, StringComparison.OrdinalIgnoreCase))
                    : plans.Any(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
                InstallPlansList.Items.Add(BuildInstallRow(preset, installed));
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error cargando presets de instalación: {ex.Message}");
            InstallPlanStatusText.Visibility = Visibility.Visible;
            Feedback.Warning(InstallPlanStatusText, "No se pudieron cargar los planes instalables.");
        }
    }

    private static ListViewItem BuildInstallRow(InstallPreset preset, bool installed)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = preset.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var desc = new TextBlock
        {
            Text = preset.Description,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = BTimeLabel
        };
        Grid.SetColumn(desc, 1);
        row.Children.Add(desc);

        var category = new TextBlock
        {
            Text = preset.Category,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = BNeutral
        };
        Grid.SetColumn(category, 2);
        row.Children.Add(category);

        var status = new TextBlock
        {
            Text = installed ? I18n.T("Instalado") : "",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = installed ? BGreen : BTimeLabel
        };
        Grid.SetColumn(status, 3);
        row.Children.Add(status);

        return new ListViewItem
        {
            Content = row,
            Tag = new InstallRowItem(preset, installed),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private void InstallPlansList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = (InstallPlansList.SelectedItem as ListViewItem)?.Tag as InstallRowItem;
        InstallPlanButton.IsEnabled = item != null && !item.Installed;
    }

    private async void InstallPlanButton_Click(object sender, RoutedEventArgs e)
    {
        var item = (InstallPlansList.SelectedItem as ListViewItem)?.Tag as InstallRowItem;
        if (item == null || item.Installed) return;

        InstallPlanButton.IsEnabled = false;
        Feedback.Running(InstallPlanStatusText, "Instalando plan...");

        var preset = item.Preset;
        var result = preset.BuiltInGuid != null
            ? await _cpuPowerService.InstallBuiltInSchemeAsync(preset.BuiltInGuid)
            : await _cpuPowerService.CreateCustomPowerPlanAsync(preset.Name, preset.BaseGuid!, preset.Tunings!);

        SetInstallStatus(result);
        if (result.Success)
        {
            await LoadManagePlansAsync();
            await LoadInstallPresetsAsync();
            _ = LoadPowerPlansAsync(); // refrescar el selector de la pestaña Núcleos
        }
    }

    private void SetInstallStatus(CommandResult result)
    {
        var text = result.MessageTemplate != null
            ? I18n.T(result.MessageTemplate, result.MessageArgs ?? Array.Empty<object?>())
            : I18n.T(result.Output);
        if (result.Success)
            Feedback.Success(InstallPlanStatusText, text);
        else
            Feedback.Error(InstallPlanStatusText, text);
    }

    // ===================== Comparar planes de energía =====================

    // Colores por defecto de las curvas de comparación: desde los recursos de tema.
    private static Color DefaultColorA => ThemeBrushes.Get("MetricTempBrush").Color;
    private static Color DefaultColorB => ThemeBrushes.Get("MetricPowerBrush").Color;

    private Color _colorA = DefaultColorA;
    private Color _colorB = DefaultColorB;
    private PowerPlanDetail? _detailA;
    private PowerPlanDetail? _detailB;

    private void LoadCompareCombos()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var plans = _cpuPowerService.GetPowerPlans();
                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var plan in plans)
                    {
                        ComparePlanACombo.Items.Add(new ComboBoxItem { Content = plan.Name, Tag = plan.Guid });
                        ComparePlanBCombo.Items.Add(new ComboBoxItem { Content = plan.Name, Tag = plan.Guid });
                    }
                    UpdateColorButtons();
                });
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"NucleosPage: error cargando planes para comparar: {ex.Message}");
            }
        });
    }

    private static string? SelectedPlanGuid(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void ComparePlanCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var guidA = SelectedPlanGuid(ComparePlanACombo);
        var guidB = SelectedPlanGuid(ComparePlanBCombo);

        if (guidA != null && guidB != null && guidA == guidB)
        {
            // No permitir el mismo plan en ambos desplegables
            if (ReferenceEquals(sender, ComparePlanACombo)) ComparePlanACombo.SelectedItem = null;
            else ComparePlanBCombo.SelectedItem = null;
            Feedback.Warning(CompareStatusText, "Los dos planes deben ser diferentes.");
        }
        else
        {
            Feedback.Set(CompareStatusText, null);
        }

        CompareButton.IsEnabled = guidA != null && guidB != null && guidA != guidB;
    }

    private void ColorAButton_Click(object sender, RoutedEventArgs e) => ShowColorPickerFor(planA: true);

    private void ColorBButton_Click(object sender, RoutedEventArgs e) => ShowColorPickerFor(planA: false);

    private void ShowColorPickerFor(bool planA)
    {
        if (XamlRoot == null) return;
        var target = planA ? ColorAButton : ColorBButton;
        var picker = new ColorPicker
        {
            Color = planA ? _colorA : _colorB,
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorSpectrumVisible = true,
            IsColorSliderVisible = true,
            IsHexInputVisible = true,
            Width = 300
        };
        var flyout = new Flyout { Content = picker, Placement = FlyoutPlacementMode.Bottom };
        flyout.Closed += (_, _) =>
        {
            var c = picker.Color;
            if (planA) _colorA = c; else _colorB = c;
            UpdateColorButtons();
            if (CompareScroll.Visibility == Visibility.Visible)
                BuildComparison();
        };
        flyout.ShowAt(target);
    }

    private void UpdateColorButtons()
    {
        SetColorButtonContent(ColorAButton, _colorA, "Color A");
        SetColorButtonContent(ColorBButton, _colorB, "Color B");
    }

    private static void SetColorButtonContent(Button button, Color color, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
    }

    private async void CompareButton_Click(object sender, RoutedEventArgs e)
    {
        var guidA = SelectedPlanGuid(ComparePlanACombo);
        var guidB = SelectedPlanGuid(ComparePlanBCombo);
        if (guidA == null || guidB == null || guidA == guidB) return;

        CompareButton.IsEnabled = false;
        Feedback.Running(CompareStatusText, "Comparando planes...");

        try
        {
            var (da, db) = await Task.Run(() => (_cpuPowerService.GetPowerPlanDetails(guidA), _cpuPowerService.GetPowerPlanDetails(guidB)));
            if (da == null || db == null)
            {
                Feedback.Error(CompareStatusText, "No se pudo leer el detalle de uno de los planes.");
                return;
            }

            _detailA = da;
            _detailB = db;
            BuildComparison();

            // Sin feedback de "comparación lista": la cuadrícula ya muestra las diferencias.
            Feedback.Set(CompareStatusText, null);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"NucleosPage: error comparando planes: {ex.Message}");
            Feedback.Error(CompareStatusText, I18n.T("No se pudo comparar: {0}", ex.Message));
        }
        finally
        {
            CompareButton.IsEnabled = SelectedPlanGuid(ComparePlanACombo) != null && SelectedPlanGuid(ComparePlanBCombo) != null;
        }
    }

    private void BuildComparison()
    {
        if (_detailA == null || _detailB == null) return;

        ComparePlanAContainer.Children.Clear();
        ComparePlanBContainer.Children.Clear();

        ComparePlanAContainer.Children.Add(BuildPlanHeader(_detailA.Name, _colorA));
        ComparePlanBContainer.Children.Add(BuildPlanHeader(_detailB.Name, _colorB));

        int n = Math.Max(_detailA.Subgroups.Count, _detailB.Subgroups.Count);
        for (int i = 0; i < n; i++)
        {
            var sgA = i < _detailA.Subgroups.Count ? _detailA.Subgroups[i] : null;
            var sgB = i < _detailB.Subgroups.Count ? _detailB.Subgroups[i] : null;

            var expA = sgA != null ? BuildSubgroupExpander(sgA, _detailB, _colorA, isPlanA: true) : null;
            var expB = sgB != null ? BuildSubgroupExpander(sgB, _detailA, _colorB, isPlanA: false) : null;

            // Sincronizar el estado de colapso: si se abre/cierra uno, el otro lo sigue,
            // así las filas quedan alineadas entre las dos cuadrículas.
            if (expA != null && expB != null)
            {
                expA.Expanding += (_, _) => expB.IsExpanded = true;
                expA.Collapsed += (_, _) => expB.IsExpanded = false;
            }

            if (expA != null) ComparePlanAContainer.Children.Add(expA);
            if (expB != null) ComparePlanBContainer.Children.Add(expB);
        }

        CompareScroll.Visibility = Visibility.Visible;
        CompareActionsPanel.Visibility = Visibility.Visible;
    }

    private void ExpandAllButton_Click(object sender, RoutedEventArgs e) => SetAllExpandersExpanded(true);

    private void CollapseAllButton_Click(object sender, RoutedEventArgs e) => SetAllExpandersExpanded(false);

    private void SetAllExpandersExpanded(bool expanded)
    {
        foreach (var child in ComparePlanAContainer.Children)
            if (child is Expander expA) expA.IsExpanded = expanded;
        foreach (var child in ComparePlanBContainer.Children)
            if (child is Expander expB) expB.IsExpanded = expanded;
    }

    private static Border BuildPlanHeader(string name, Color color)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stack.Children.Add(new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        });
        var planName = new TextBlock
        {
            Text = name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(planName, name);
        stack.Children.Add(planName);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack
        };
    }

    private Expander BuildSubgroupExpander(PowerSubgroupInfo sg, PowerPlanDetail? other, Color color, bool isPlanA)
    {
        int diffCount = 0;
        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(BuildSettingHeaderRow());

        foreach (var setting in sg.Settings)
        {
            var otherSetting = FindSetting(other, sg.Guid, setting.Guid);
            bool acDiff = otherSetting != null && !string.Equals(setting.AcValue, otherSetting.AcValue, StringComparison.OrdinalIgnoreCase);
            bool dcDiff = otherSetting != null && !string.Equals(setting.DcValue, otherSetting.DcValue, StringComparison.OrdinalIgnoreCase);
            if (acDiff || dcDiff) diffCount++;
            body.Children.Add(BuildSettingRow(setting, acDiff, dcDiff, color));
        }

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var subgroupName = new TextBlock
        {
            Text = sg.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(subgroupName, sg.Name);
        header.Children.Add(subgroupName);
        if (diffCount > 0)
        {
            header.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 2),
                Background = new SolidColorBrush(Color.FromArgb(0x28, color.R, color.G, color.B)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"{diffCount} diff",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }

        return new Expander
        {
            Header = header,
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = true
        };
    }

    private static PowerSettingInfo? FindSetting(PowerPlanDetail? other, string subgroupGuid, string settingGuid)
    {
        if (other == null) return null;
        var sg = other.Subgroups.FirstOrDefault(x => string.Equals(x.Guid, subgroupGuid, StringComparison.OrdinalIgnoreCase));
        return sg?.Settings.FirstOrDefault(x => string.Equals(x.Guid, settingGuid, StringComparison.OrdinalIgnoreCase));
    }

    private static Grid BuildSettingHeaderRow()
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
        AddSettingColumns(grid);

        grid.Children.Add(new TextBlock { Text = I18n.T("ID de configuración"), FontSize = 11, FontWeight = FontWeights.SemiBold });
        var nombre = new TextBlock { Text = I18n.T("Nombre"), FontSize = 11, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(nombre, 1);
        grid.Children.Add(nombre);
        var ac = new TextBlock { Text = I18n.T("Valor AC"), FontSize = 11, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(ac, 2);
        grid.Children.Add(ac);
        var dc = new TextBlock { Text = I18n.T("Valor DC"), FontSize = 11, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(dc, 3);
        grid.Children.Add(dc);
        return grid;
    }

    private static void AddSettingColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
    }

    private Border BuildSettingRow(PowerSettingInfo setting, bool acDiff, bool dcDiff, Color color)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 5, 4, 5) };
        AddSettingColumns(row);

        if (acDiff || dcDiff)
            row.Background = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B));

        var highlight = new SolidColorBrush(color);
        var normal = SecondaryBrush;

        var guidTb = new TextBlock
        {
            Text = setting.Guid,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(guidTb, setting.Guid);
        row.Children.Add(guidTb);

        var nombre = new TextBlock
        {
            Text = setting.Name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        ToolTipService.SetToolTip(nombre, setting.Name);
        Grid.SetColumn(nombre, 1);
        row.Children.Add(nombre);

        var ac = new TextBlock
        {
            Text = FormatDisplayValue(setting.AcValue),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = acDiff ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = acDiff ? highlight : normal
        };
        ToolTipService.SetToolTip(ac, ac.Text);
        Grid.SetColumn(ac, 2);
        row.Children.Add(ac);

        var dc = new TextBlock
        {
            Text = FormatDisplayValue(setting.DcValue),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = dcDiff ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = dcDiff ? highlight : normal
        };
        ToolTipService.SetToolTip(dc, dc.Text);
        Grid.SetColumn(dc, 3);
        row.Children.Add(dc);

        return new Border { Child = row };
    }

    private static string FormatDisplayValue(string value)
    {
        // Los valores ya llegan traducidos por el servicio (nombre localizado,
        // porcentaje, tiempo legible o decimal); solo se rellena el vacío.
        return string.IsNullOrEmpty(value) ? "--" : value;
    }

    // ===================== Helpers =====================

    private static string FormatFreq(double mhz)
    {
        if (mhz <= 0) return "--";
        return mhz >= 1000 ? $"{mhz / 1000.0:F2} GHz" : $"{mhz:F0} MHz";
    }
}
