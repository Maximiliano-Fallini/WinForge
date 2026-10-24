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
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Controls;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Página "Núcleos": gráfico de temperatura del CPU estilo
/// Administrador de tareas (cuadrícula, timeline con scroll horizontal), cards
/// con uso/temperatura/clock (mín/máx mientras la pestaña está abierta) y barras
/// por núcleo con estado de parking real (0% de uso no implica estacionado).
/// </summary>
public sealed partial class NucleosPage : Page, IBackgroundPausable
{
    private readonly ISystemInfoService _systemInfoService;
    private readonly ICpuPowerService _cpuPowerService;
    private readonly IWinUtilService _winUtilService;
    private readonly ILoggingService _loggingService;

    private DispatcherQueueTimer? _samplingTimer;
    private bool _sampling;
    private bool _started;
    private bool _contentShown;

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

    // El ÁREA del gráfico va como superficie (el mismo relleno que los canvas de Estabilidad, que
    // el XAML resuelve solo): es un panel, así que le tocan el alfa del ajuste y el vidrio. Los dos
    // colores de arriba —planos— quedan para lo que NO es una superficie: el anillo de 2 px del
    // punto de hover, que existe justamente para separar el punto de lo que tiene detrás y se vería
    // mal desenfocado.
    private static Brush BChartFill => ThemeBrushes.GetSurface("ChartBackgroundBrush");
    private static Brush BChartFillHot => ThemeBrushes.GetSurface("ChartBackgroundHotBrush");
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
    private static Brush CoreCardBrush => ThemeBrushes.GetSurface("CoreCardBackgroundBrush");

 /// <summary>
 /// Fondo de las cards de plan (un tono distinto al de la card grande, para que se lean como tabla):
 /// es una card, así que lleva el pincel de superficie —vidrio del motor incluido— como cualquier
 /// otra. En claro el tono sale de otra clave de gráfico, que también se pide como superficie para que
 /// las dos variantes de tema se vean con el mismo tipo de relleno.
 /// </summary>
    private static Brush RowBackgroundBrush =>
        ThemeBrushes.GetSurface(ThemeBrushes.ActiveThemeKey() == "Light" ? "ChartHoverBadgeBgBrush" : "ChartBackgroundBrush");
    private static SolidColorBrush CoreTrackBrush => ThemeBrushes.Get("CoreTrackBackgroundBrush");

 /// <summary>
 /// Superficie de card del tema: es la que el ajuste de transparencia atenúa y la que el
 /// desenfoque reconoce como panel (ver PanelBlurLayer). Las superficies que se arman en código
 /// —los resultados de la comparación de planes— la usan igual que las cards del XAML; con
 /// cualquier otro relleno quedaban fuera de los dos deslizadores.
 /// </summary>
    private static Brush CardBrush => ThemeBrushes.GetSurface("CardBackgroundBrush");

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
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
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
        I18n.LanguageChanged += OnLanguageChanged;

        Unloaded += (s, e) =>
        {
            StopSampling();
            I18n.LanguageChanged -= OnLanguageChanged;
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
        // El techo de turbo se aplica al plan activo: al abrir la pestaña se relee,
        // así no muestra el estado de un plan que quedó atrás.
        TurboCeiling?.Refresh();
        _ = ForceShowContentAfterTimeoutAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        PauseBackgroundTimers();
    }

    /// <summary>
    /// Pausa de bandeja (IBackgroundPausable): Stop real del sampling de 1 s.
    /// </summary>
    public void PauseBackgroundTimers() => StopSampling();

    /// <summary>Reanudación desde bandeja: solo con ventana visible.</summary>
    public void ResumeBackgroundTimers()
    {
        if (App.MainWindowInstance?.IsWindowVisible == true)
            StartSampling();
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

    private void OnLanguageChanged()
    {
        if (_coreBars.Count > 0)
        {
            RebuildCoreBars(_coreBars.Count);
            UpdateCoreBars(_lastUsages, _lastCoreTemps, _lastParked);
        }

        ApplyLanguageAfterLayout();
    }

    private void ApplyLanguageAfterLayout()
    {
        ApplyLanguageToPage();
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (PageContent.Visibility == Visibility.Visible) PageContent.UpdateLayout();
            }
            catch { }

            ApplyLanguageToPage();
            DispatcherQueue.TryEnqueue(ApplyLanguageToPage);
        });
    }

    private void ApplyLanguageToPage()
    {
        I18n.ApplyToVisualTree(this);
    }

    // ===================== Skeleton de carga =====================

    private void ShowSkeleton()
    {
        _contentShown = false;
        if (PageSkeleton == null || PageContent == null) return;
        PageSkeleton.Visibility = Visibility.Visible;
        PageContent.Visibility = Visibility.Collapsed;
    }

    private void ShowContent()
    {
        if (_contentShown) return;
        _contentShown = true;
        if (PageSkeleton == null) return;
        PageSkeleton.Visibility = Visibility.Collapsed;

        // El contenido arranca (y vuelve a quedar, en cada entrada a la pestaña) en
        // Collapsed: lo oculta el skeleton de ShowSkeleton. Sin esta línea la página
        // se quedaba en blanco para siempre —el skeleton desaparecía y nadie volvía
        // a mostrar PageContent—.
        PageContent.Visibility = Visibility.Visible;

        // Forzar layout del contenedor visible para que WinUI construya sus árboles
        // visuales internos — sin esto, ApplyToVisualTree recorre paneles vacíos.
        if (PageContent.Visibility == Visibility.Visible) PageContent.UpdateLayout();

        ApplyLanguageAfterLayout();
    }

    // Seguridad: si el primer sample tarda más de 4s (sensor lento), el skeleton
    // se reemplaza igual para no dejar la página en "cargando" para siempre.
    private async Task ForceShowContentAfterTimeoutAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (!_contentShown)
            DispatcherQueue.TryEnqueue(ShowContent);
    }

    private void OnSamplingTick(DispatcherQueueTimer sender, object args)
    {
        if (_sampling) return;
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
        ChartCanvas.Background = BChartFill;
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

        ChartCanvas.Background = CurrentTemp() >= 90 ? BChartFillHot : BChartFill;

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
 // La tolerancia debe superar el avance por muestra (3 px): si una muestra
 // agranda el lienzo entre nuestro ChangeView y este evento, el offset queda
 // por detrás del nuevo máximo y con poca tolerancia el seguimiento se cortaba.
        _atRightEdge = ChartScroll.HorizontalOffset >= ChartScroll.ScrollableWidth - (ChartPxPerSample + 2);
    }

    private void ScrollToLatest()
    {
 // Objetivo determinista: el máximo desplazable DESPUÉS de que el layout
 // aplique el ancho recién asignado al lienzo. Leer ChartScroll.ScrollableWidth
 // aquí devuelve el valor viejo (el layout aún no procesó el nuevo ancho) y el
 // ChangeView quedaba apuntando a la posición actual: el gráfico crecía fuera
 // de pantalla sin moverse. Se difiere al próximo pase de layout (one-shot) y
 // recién ahí se empuja la vista al borde derecho.
        double target = Math.Max(0, _chartWidth - ChartScroll.ViewportWidth);
        EventHandler<object>? onLayout = null;
        onLayout = (s, e) =>
        {
            ChartScroll.LayoutUpdated -= onLayout;
            try { ChartScroll.ChangeView(target, null, null, true); }
            catch { }
        };
        ChartScroll.LayoutUpdated += onLayout;
    }

    // ===================== Núcleos =====================

    private void RebuildCoreBars(int count)
    {
        CoresHost.Items.Clear();
        _coreBars.Clear();

        for (int i = 0; i < count; i++)
        {
            var bar = new CoreBar();
            const double trackW = 26;
            const double trackH = 96;

 // Cabecera: nombre del núcleo a la izquierda como etiqueta chica y
 // espaciada, temperatura a la derecha. El dato que manda es el %, así
 // que el resto queda en segundo plano (jerarquía limpia).
            var name = new TextBlock
            {
                Text = $"{I18n.T("Núcleo")} {i}".ToUpperInvariant(),
                FontSize = 8.5,
                CharacterSpacing = 40,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("SecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            bar.Temp = new TextBlock
            {
                Text = "--",
                FontSize = 9.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = BTimeLabel
            };

            var header = new Grid { ColumnSpacing = 6 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(name, 0);
            Grid.SetColumn(bar.Temp, 1);
            header.Children.Add(name);
            header.Children.Add(bar.Temp);

 // Pista con extremos redondeados (pastilla): el relleno crece desde
 // abajo con la misma forma, sin bordes ni biseles que agreguen ruido.
            bar.Fill = new Border
            {
                Width = trackW,
                Height = 0,
                CornerRadius = new CornerRadius(trackW / 2),
                Background = BGreen,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            var track = new Border
            {
                Width = trackW,
                Height = trackH,
                CornerRadius = new CornerRadius(trackW / 2),
                Background = CoreTrackBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = bar.Fill
            };

            bar.Usage = new TextBlock
            {
                Text = "--%",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BNeutral
            };

 // Estado como texto plano (sin píldora de fondo): el color de estado lo
 // lleva la barra y acá queda solo la palabra. UpdateCoreBars sigue
 // cambiando Text/Foreground igual que antes.
            bar.Status = new TextBlock
            {
                Text = "—",
                FontSize = 9,
                CharacterSpacing = 30,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = BNeutral
            };

            var sp = new StackPanel { Spacing = 6 };
            sp.Children.Add(header);
            sp.Children.Add(track);
            sp.Children.Add(bar.Usage);
            sp.Children.Add(bar.Status);

 // Card más angosta y compacta: padding parejo y un solo borde hairline.
            var root = new Border
            {
                Background = CoreCardBrush,
                BorderBrush = ThemeBrush("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 10, 10, 9),
                Width = 112,
                Margin = new Thickness(0, 0, 8, 8),
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
                // El estado activo va atenuado: el color ya lo dice la barra y el %.
                // Así solo lo que importa (estacionado) resalta en rojo.
                bar.Status.Foreground = BNeutral;
            }
            else
            {
                bar.Fill.Background = BNeutral;
                bar.Usage.Foreground = BNeutral;
                bar.Status.Text = "—";
                bar.Status.Foreground = BNeutral;
            }

            // Temperatura del núcleo físico (con SMT varios hilos comparten sensor).
            // Si el hardware no expone sensores por núcleo (p. ej. este
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

    // ===================== Helpers =====================

    private static string FormatFreq(double mhz)
    {
        if (mhz <= 0) return "--";
        return mhz >= 1000 ? $"{mhz / 1000.0:F2} GHz" : $"{mhz:F0} MHz";
    }
}
