using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Overclock USB (inspirado en el filtro): lista los
/// dispositivos USB con polling ajustable (mouse, teclados y mandos HID) en una
/// grilla con su controller name, hercios reales y bInterval del descriptor, y
/// deja cambiar la tasa de sondeo por dispositivo — subiendo (overclock) o
/// bajando (downclock, que siempre funciona) — con un popup al hacer doble clic.
///
/// Primera vista (gate): si el componente del sistema (el motor kernel que fuerza
/// el bInterval) no está instalado, se muestra una pantalla profesional para
/// descargarlo e instalarlo automáticamente con un clic — la UI usa siempre
/// lenguaje genérico ("componentes del sistema") y nunca expone el nombre interno
/// del componente. Recién cuando está listo aparece la grilla de dispositivos.
/// El botón "Restaurar de fábrica" saca el filtro y el override y re-arranca.
/// </summary>
public sealed partial class OverclockUsbPage : Page, IBackgroundPausable
{
    private readonly IUsbOverclockService _usb;
    private readonly ILoggingService _logging;

    private List<UsbPollingDevice> _devices = new();
    private bool _busy;
    private bool _loaded;
    private bool _componentReady;
    private const string HowItWorksKey = "Al aplicar una tasa, WinForge instala un filtro en el dispositivo USB y un motor del sistema (kernel) fuerza el nuevo intervalo de sondeo al reconectarse. Es un componente de código abierto que se instala solo en tu PC, sin enviar datos.";

    // ===== Grilla estilo Gestión de Procesos =====
    // Columnas de ancho fijo en píxeles: cada una tiene su ancho natural, sin
    // compresión proporcional. Clic en la cabecera ordena por esa columna (▲/▼);
    // arrastrar la celda mueve la columna a otra posición; el borde derecho de cada
    // celda la redimensiona (esa columna cambia y el resto corre). Si el total supera
    // el ancho visible aparece el scroll horizontal, la cabecera se desplaza en
    // sincronía con las filas y los scrollbars van en canaletas aparte para nunca
    // tapar contenido — el mismo mecanismo de Gestión de Procesos.
    private static readonly (string Header, double Width, bool Right)[] Columns =
    {
    // "Nombre hijo" es la columna principal (con el icono de su tipo al frente);
    // el nombre del producto USB queda en el tooltip y en el popup.
        ("Nombre hijo", 320, false),
        ("Controlador", 360, false),
        ("bInterval", 110, true),
        ("Filtro", 110, false)
    };

    private double[] _colWidths = Columns.Select(c => c.Width).ToArray();
    private int[] _colOrder = Enumerable.Range(0, Columns.Length).ToArray();
    private int _sortColumn = -1;
    private bool _sortDesc;
    private readonly List<TextBlock> _headerTexts = new();
    private readonly List<DeviceRow> _rowUis = new();
    private bool _tableInit;
    private const double RowHeight = 50;
    private const int MaxHz1khzFallback = 1000; // techo conservador si el descriptor es desconocido

    private static readonly InputCursor ResizeCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor DefaultCursor =
        InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    /// <summary>Una fila de la grilla (estructura igual a la de Gestión de Procesos:
    /// Cols = grilla interna con las columnas, Container = borde para el resaltado de
    /// selección, Outer = contenedor del panel que recibe el clic).</summary>
    private sealed class DeviceRow
    {
        public Grid Outer = null!;
        public Grid Cols = null!;
        public Border Container = null!;
        public UsbPollingDevice Device = null!;
    }

    private double Scaled(int col) => _colWidths[col];

    private double TotalWidth
    {
        get
        {
            double sum = 0;
            for (int p = 0; p < Columns.Length; p++)
                sum += Scaled(_colOrder[p]);
            return sum;
        }
    }

    /// <summary>Suscriptores de la tabla (una sola vez): cabecera sincronizada con el
    /// scroll horizontal, scrollbars propios y rueda del mouse sobre la cabecera.</summary>
    private void InitTable()
    {
        if (_tableInit || HeaderGrid == null) return;
        _tableInit = true;

        HeaderGrid.RenderTransform = new TranslateTransform();
        HeaderHost.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(HeaderHost_PointerWheelChanged), true);
        HeaderHost.SizeChanged += (_, _) => UpdateHeaderClip();
        RowsScroll.SizeChanged += (_, _) => UpdateScrollbarInsets();
        RowsScroll.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, OnScrollableChanged);
        RowsScroll.RegisterPropertyChangedCallback(ScrollViewer.ScrollableWidthProperty, OnScrollableChanged);

        HScrollBar.Scroll += (_, e) =>
        {
            double maxOff = Math.Max(0, TotalWidth - RowsScroll.ViewportWidth);
            RowsScroll.ChangeView(Math.Clamp(e.NewValue, 0, maxOff), null, null, true);
        };
        VScrollBar.Scroll += (_, e) =>
        {
            double maxOff = RowsScroll.ScrollableHeight;
            RowsScroll.ChangeView(null, Math.Clamp(e.NewValue, 0, maxOff), null, true);
        };
        UpdateScrollbarInsets();
    }

    private void OnScrollableChanged(DependencyObject sender, DependencyProperty dp)
    {
        UpdateScrollbarInsets();
    }

    // ===== Cabecera de columnas (ordenable y redimensionable, como Gestión de Procesos) =====

    private void BuildHeader()
    {
        HeaderGrid.Children.Clear();
        HeaderGrid.ColumnDefinitions.Clear();
        for (int p = 0; p < Columns.Length; p++)
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Scaled(_colOrder[p])) });
        HeaderGrid.Width = TotalWidth;
        HeaderGrid.HorizontalAlignment = HorizontalAlignment.Left;
        _headerTexts.Clear();

        for (int pos = 0; pos < Columns.Length; pos++)
        {
            int col = _colOrder[pos];

    // Separador visual entre columnas (borde izquierdo, salvo la primera).
            if (pos > 0)
            {
                var sep = new Rectangle
                {
                    Width = 1,
                    Fill = (Brush)ThemeBrushes.Get("SensorGridLineBrush"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsHitTestVisible = false
                };
                Grid.SetColumn(sep, pos);
                HeaderGrid.Children.Add(sep);
            }

    // Celda de cabecera: clic ordena, arrastrar mueve la columna. La alineación
    // sigue a la columna (Hz y bInterval a la derecha, igual que sus datos).
            var tb = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)ThemeBrushes.Get("SecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = Columns[col].Right ? TextAlignment.Right : TextAlignment.Left,
                Margin = new Thickness(8, 0, 8, 0)
            };
            var cell = new Grid { Tag = pos };
            cell.Children.Add(tb);
            cell.PointerPressed += HeaderCell_PointerPressed;
            cell.PointerMoved += HeaderCell_PointerMoved;
            cell.PointerReleased += HeaderCell_PointerReleased;
            cell.PointerCaptureLost += HeaderCell_PointerCaptureLost;
            Grid.SetColumn(cell, pos);
            HeaderGrid.Children.Add(cell);
            _headerTexts.Add(tb);

    // Manija de redimensionado en el borde derecho de la columna.
            if (pos < Columns.Length - 1)
            {
                var handle = new Rectangle
                {
                    Width = 6,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Tag = pos
                };
                handle.PointerPressed += Handle_PointerPressed;
                handle.PointerMoved += Handle_PointerMoved;
                handle.PointerReleased += Handle_PointerReleased;
                handle.PointerCaptureLost += Handle_PointerCaptureLost;
                handle.PointerEntered += (_, _) => ProtectedCursor = ResizeCursor;
                handle.PointerExited += (_, _) => ProtectedCursor = DefaultCursor;
                Grid.SetColumn(handle, pos);
                HeaderGrid.Children.Add(handle);
            }
        }
        UpdateHeaderIndicators();
    }

    private void UpdateHeaderIndicators()
    {
        for (int pos = 0; pos < _headerTexts.Count; pos++)
        {
            int col = _colOrder[pos];
            string text = Columns[col].Header;
            if (col == _sortColumn)
                text += _sortDesc ? " ▼" : " ▲";
            _headerTexts[pos].Text = text;
        }
    }

    private void SortBy(int col)
    {
        if (_sortColumn == col) _sortDesc = !_sortDesc;
        else { _sortColumn = col; _sortDesc = false; }
        UpdateHeaderIndicators();
        RebuildRows();
    }

    // ===== Mover columnas arrastrando la cabecera =====

    private int _pressPos = -1;
    private bool _dragHappened;
    private double _pressX, _pressY;

    private void HeaderCell_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid cell) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressPos = cell.Tag is int p ? p : -1;
        if (_pressPos < 0) return;
        _pressX = e.GetCurrentPoint(this).Position.X;
        _pressY = e.GetCurrentPoint(this).Position.Y;
        _dragHappened = false;
        cell.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void HeaderCell_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressPos < 0) return;
        var pt = e.GetCurrentPoint(this).Position;
    // Superar el umbral convierte el clic en un arrastre (mover columna).
        if (!_dragHappened && (Math.Abs(pt.X - _pressX) > 8 || Math.Abs(pt.Y - _pressY) > 8))
            _dragHappened = true;
        if (_dragHappened) e.Handled = true;
    }

    private void HeaderCell_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var cell = (Grid)sender;
        int pos = cell.Tag is int p ? p : -1;
    // Mover/ordenar ANTES de soltar la captura: ReleasePointerCapture dispara
    // PointerCaptureLost (que resetea _pressPos).
        if (_pressPos >= 0)
        {
            if (_dragHappened)
            {
                int target = PositionFromX(e.GetCurrentPoint(HeaderGrid).Position.X);
                if (target >= 0 && target != _pressPos)
                    MoveColumn(_pressPos, target);
            }
            else if (pos >= 0)
            {
                SortBy(_colOrder[pos]);
            }
        }
        _pressPos = -1;
        _dragHappened = false;
        cell.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void HeaderCell_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _pressPos = -1;
        _dragHappened = false;
    }

    private int PositionFromX(double x)
    {
        double acc = 0;
        for (int p = 0; p < Columns.Length; p++)
        {
            double w = Scaled(_colOrder[p]);
            if (x < acc + w / 2) return p;   // mitad de la columna = destino
            acc += w;
        }
        return Columns.Length - 1;
    }

    private void MoveColumn(int fromPos, int toPos)
    {
        if (fromPos == toPos) return;
        int col = _colOrder[fromPos];
        if (fromPos < toPos)
        {
            for (int p = fromPos; p < toPos; p++) _colOrder[p] = _colOrder[p + 1];
            _colOrder[toPos] = col;
        }
        else
        {
            for (int p = fromPos; p > toPos; p--) _colOrder[p] = _colOrder[p - 1];
            _colOrder[toPos] = col;
        }
        BuildHeader();
        RebuildRows();
    }

    // ===== Redimensionado de columnas (arrastrar el borde derecho de la cabecera) =====

    // Coalescing igual que Gestión de Procesos: como mucho una aplicación de ancho por
    // frame (~16 ms) y el último ancho pendiente se aplica al soltar.
    private const long ResizeApplyMs = 16;
    private long _lastResizeApply;
    private double _pendingResizeWidth = double.NaN;
    private int _resizeCol = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;

    private void Handle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Rectangle rect) return;
        int pos = rect.Tag is int c ? c : -1;
        if (pos < 0 || pos >= Columns.Length - 1) return;
        _resizeCol = pos;
        _resizeStartX = e.GetCurrentPoint(this).Position.X;
        _resizeStartWidth = _colWidths[_colOrder[pos]];
        _pendingResizeWidth = double.NaN;
        rect.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Handle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeCol < 0) return;
        double delta = e.GetCurrentPoint(this).Position.X - _resizeStartX;
        double w = Math.Clamp(_resizeStartWidth + delta, 60, 900);
        long now = Environment.TickCount64;
        if (now - _lastResizeApply >= ResizeApplyMs)
        {
            _lastResizeApply = now;
            SetColumnWidth(_colOrder[_resizeCol], w);
        }
        else
        {
            _pendingResizeWidth = w;
        }
    }

    private void Handle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Rectangle rect) rect.ReleasePointerCapture(e.Pointer);
        FlushPendingResizeWidth();
        _resizeCol = -1;
        ProtectedCursor = DefaultCursor;
    }

    private void Handle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        FlushPendingResizeWidth();
        _resizeCol = -1;
        ProtectedCursor = DefaultCursor;
    }

    private void FlushPendingResizeWidth()
    {
        if (!double.IsNaN(_pendingResizeWidth) && _resizeCol >= 0)
        {
            SetColumnWidth(_colOrder[_resizeCol], _pendingResizeWidth);
            _pendingResizeWidth = double.NaN;
        }
    }

    private void SetColumnWidth(int col, double width)
    {
        _colWidths[col] = width;
    // Solo cambia esta columna: la izquierda queda estática y la derecha conserva
    // su ancho (si el total desborda el viewport aparece el scroll horizontal).
        ApplyColumnWidths();
        UpdateScrollbarInsets();
    }

    /// <summary>Re-aplica el ancho de cada columna (en el orden visual actual) a la
    /// cabecera y a todas las filas. Con pocas filas no hace falta virtualizar.</summary>
    private void ApplyColumnWidths()
    {
        HeaderGrid.Width = TotalWidth;
        for (int pos = 0; pos < Columns.Length; pos++)
            HeaderGrid.ColumnDefinitions[pos].Width = new GridLength(Scaled(_colOrder[pos]));
        foreach (var ui in _rowUis)
        {
            ui.Cols.Width = TotalWidth;
            for (int pos = 0; pos < Columns.Length; pos++)
                ui.Cols.ColumnDefinitions[pos].Width = new GridLength(Scaled(_colOrder[pos]));
        }
    }

    private void RowsScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
    // Cabecera sincronizada con las filas en cada frame (eventos intermedios
    // incluidos, mientras se arrastra el scrollbar).
        ((TranslateTransform)HeaderGrid.RenderTransform).X = -RowsScroll.HorizontalOffset;
        UpdateHeaderClip();
        UpdateHorizontalBar();
        UpdateVerticalBar();
    }

    // ===== Scrollbars propios en sus canaletas (fuera del viewport, nunca tapan nada) =====

    private void UpdateScrollbarInsets()
    {
        UpdateHeaderClip();
        UpdateHorizontalBar();
        UpdateVerticalBar();
    }

    private void UpdateHorizontalBar()
    {
        double total = TotalWidth;
        double vp = RowsScroll.ViewportWidth;
        if (vp <= 0 || total <= vp)
        {
            HScrollBar.Visibility = Visibility.Collapsed;
            return;
        }
        HScrollBar.Visibility = Visibility.Visible;
        double maxOff = total - vp;
        HScrollBar.Minimum = 0;
        HScrollBar.Maximum = maxOff;
        HScrollBar.ViewportSize = vp;
        HScrollBar.Value = Math.Min(Math.Max(0, RowsScroll.HorizontalOffset), maxOff);
    }

    private void UpdateVerticalBar()
    {
        double vp = RowsScroll.ViewportHeight;
        double maxOff = RowsScroll.ScrollableHeight;
        if (vp <= 0 || maxOff <= 0)
        {
            VScrollBar.Visibility = Visibility.Collapsed;
            return;
        }
        VScrollBar.Visibility = Visibility.Visible;
        VScrollBar.Minimum = 0;
        VScrollBar.Maximum = maxOff;
        VScrollBar.ViewportSize = vp;
        VScrollBar.Value = Math.Min(Math.Max(0, RowsScroll.VerticalOffset), maxOff);
    }

    private void UpdateHeaderClip()
    {
        double w = RowsScroll.ViewportWidth;
        if (w <= 0) w = HeaderHost.ActualWidth;
        if (w < 0) w = 0;
        HeaderHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, w, HeaderHost.ActualHeight) };
    }

    private void HeaderHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
    // La rueda sobre la cabecera se reenvía al área de filas, para que el gesto
    // siga funcionando (la cabecera no es scrolleable por sí sola).
        var props = e.GetCurrentPoint(HeaderHost).Properties;
        e.Handled = true;
        double delta = props.MouseWheelDelta * 0.4;   // ~48 px por muesca
        if (props.IsHorizontalMouseWheel)
            RowsScroll.ChangeView(RowsScroll.HorizontalOffset - delta, null, null, true);
        else
            RowsScroll.ChangeView(null, RowsScroll.VerticalOffset - delta, null, true);
    }

    public OverclockUsbPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _usb = App.Services.GetRequiredService<IUsbOverclockService>();
        _logging = App.Services.GetRequiredService<ILoggingService>();
        _latencyMonitor = App.Services.GetRequiredService<InputLatencyMonitorService>();
        _latencyTest = App.Services.GetRequiredService<InputLatencyTestService>();
        _audioLatency = App.Services.GetRequiredService<AudioLatencyService>();
        _busCadence = App.Services.GetRequiredService<BusCadenceService>();
        _settings = App.Services.GetRequiredService<ISettingsService>();

    // La página vive con caché de navegación: suscripciones una sola vez.
        Loaded += OnLoaded;
        // La cuadrícula del onboarding visual se dibuja al tamaño real del recuadro, así
        // que se rehace cuando cambia (ventana redimensionada, primer layout).
        LatencyDemoBackdrop.SizeChanged += (_, _) => BuildDemoGrid();
        // El desplegable de dispositivos se dimensiona con lo que muestra y con el ancho de la card:
        // hay que rehacerlo cuando ese ancho cambia (ventana), cuando cambia el texto del botón (dice
        // "Iniciar test" / "Medir en el bus" / "Medir audio") o aparece el spinner de la corrida.
        LatencyTestCard.SizeChanged += (_, _) => FitLatencySelectorWidth();
        LatencyTestRunButton.SizeChanged += (_, _) => FitLatencySelectorWidth();
        LatencyTestProgress.SizeChanged += (_, _) => FitLatencySelectorWidth();
        // Mientras corre la prueba, el teclado es DEL TEST: se frena Espacio/Enter antes de que
        // lleguen a cualquier control de la card (ver el comentario del manejador).
        LatencyTestCard.PreviewKeyDown += OnLatencyCardPreviewKeyDown;
        I18n.LanguageChanged += OnLanguageChanged;
    }

    // ===================== Test de latencia (una sola card) =====================
    // Elegís el dispositivo y el tipo, y corrés la prueba. El tipo no es cosmético: cada
    // periférico reporta distinto, y eso decide qué se puede prometer.
    //   • Mouse: emite informes continuos mientras se mueve → mide el polling real.
    //   • Mando: emite continuo mientras se MUEVEN LOS EJES; un botón suelto es UN informe
    //     (por eso el test pide girar el stick: es lo que hace Gamepadla).
    //   • Teclado: solo manda informe cuando cambia de estado → su polling NO se puede medir
    //     desde acá; lo que se mide es cada cuánto manda informe (o sea, tu ritmo).
    // El veredicto detecta ese último caso por la dispersión de la serie, así que sigue
    // siendo honesto aunque el tipo elegido esté mal.

    private readonly InputLatencyMonitorService _latencyMonitor;
    private readonly InputLatencyTestService _latencyTest;
    private readonly AudioLatencyService _audioLatency;
    private readonly BusCadenceService _busCadence;
    private readonly ISettingsService _settings;
    // Endpoints de audio vistos al armar el desplegable, por id: la elección no depende de
    // que una enumeración posterior (al correr el test) devuelva exactamente lo mismo.
    private readonly Dictionary<string, AudioEndpointInfo> _audioEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _latencyTestCts;
    private bool _latencyTestRunning;
    // Tipo del dispositivo elegido al ARRANCAR la corrida. El trazo en vivo lo necesita para
    // desambiguar cuando varias entradas de Raw Input comparten la clave (el receptor de un mouse
    // expone mouse y teclado con el mismo VID/PID); resolverlo en cada tick sería re-enumerar el
    // bus y el PnP 8 veces por segundo.
    private InputDeviceKind? _runningLatencyKind;
    private string? _selectedLatencyKey;          // VID/PID del dispositivo elegido
    private InputLatencyTestResult? _shownResult;
    private AudioLatencyRun? _lastAudioRun;        // última corrida de audio de esta sesión
    private InputLatencyTestResult? _shownPrevious;
    private InputLatencyTestRun? _lastRun;         // última corrida de ESTA sesión

    /// <summary>
    /// VID/PID de un device instance id ("USB\VID_258A&amp;PID_002A\5&amp;…"): es la identidad
    /// que SOBREVIVE al reinicio del periférico, así que es la que une la grilla con lo que ve
    /// Raw Input, la que se guarda como elección del usuario y la que compara dos corridas.
    /// </summary>
    private static string VidPidOf(string instanceId)
    {
        var m = Regex.Match(instanceId ?? "", @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
        return m.Success
            ? $"VID_{m.Groups[1].Value.ToUpperInvariant()}&PID_{m.Groups[2].Value.ToUpperInvariant()}"
            : "";
    }

    /// <summary>Nombre visible de la grilla (mismo criterio que la fila: el hijo funcional
    /// si lo hay, si no el nombre del producto).</summary>
    private static string DisplayNameOf(UsbPollingDevice device) =>
        string.IsNullOrWhiteSpace(device.ChildName) ? device.ControllerName : device.ChildName;

    /// <summary>Tipo que detecta el driver (null = no se pudo: audio, desconocido…).</summary>
    private static InputDeviceKind? KindOf(UsbDeviceKind kind) => kind switch
    {
        UsbDeviceKind.Mouse => InputDeviceKind.Mouse,
        UsbDeviceKind.Keyboard => InputDeviceKind.Keyboard,
        UsbDeviceKind.Controller => InputDeviceKind.Gamepad,
        _ => null
    };

    private sealed record LatencyDeviceOption(string Key, string Name, InputDeviceKind? DetectedKind, bool Reporting,
        InputTransport Transport = InputTransport.Unknown);

    /// <summary>
    /// Dispositivos que puede medir el test: la unión de la grilla USB (siempre poblada, con
    /// el tipo que detecta el driver) y lo que Raw Input haya visto (lo único medible de
    /// verdad). Se unen por VID/PID y se recuerda cuáles reportaron hace poco.
    /// </summary>
    private List<LatencyDeviceOption> CollectLatencyDevices()
    {
        var byKey = new Dictionary<string, LatencyDeviceOption>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in _devices)
        {
            // Los dispositivos de AUDIO no van acá: aparecen abajo, tras el separador, como
            // endpoints WASAPI (que es la vía con la que se miden). Antes un headset USB
            // quedaba listado entre los teclados y los mouse.
            // El ADAPTADOR Bluetooth tampoco: es el transporte, no un dispositivo de entrada,
            // así que no se le puede medir el sondeo (el sondeo es del periférico emparejado,
            // que ya aparece por su cuenta con su transporte Bluetooth).
            if (device.Kind is UsbDeviceKind.Audio or UsbDeviceKind.Bluetooth) continue;
            string key = VidPidOf(device.InstanceId);
            if (key.Length == 0) continue;
            // El transporte se resuelve POR DISPOSITIVO (su instance id en el bus USB, no su
            // VID/PID): así el dato está desde el arranque, sin esperar a que el dispositivo
            // mande un informe, y sin heredar el nombre de otro nodo del mismo VID/PID.
            byKey[key] = new LatencyDeviceOption(key, DisplayNameOf(device), KindOf(device.Kind), false,
                _latencyMonitor.TransportForInstance(device.InstanceId));
        }

        foreach (var presence in _latencyMonitor.GetKnownDevices())
        {
            if (presence.Handle.Length == 0) continue;
            bool reporting = presence.SecondsSinceLastReport < 10;
            byKey[presence.Handle] = byKey.TryGetValue(presence.Handle, out var existing)
                ? existing with { Reporting = reporting, DetectedKind = existing.DetectedKind ?? presence.Kind, Transport = presence.Transport }
                : new LatencyDeviceOption(presence.Handle, presence.Name, presence.Kind, reporting, presence.Transport);
        }

        return byKey.Values.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reconstruye el desplegable conservando la elección (por VID/PID, así que
    /// sobrevive a que el dispositivo se reinicie o a que aparezca uno nuevo). Los periféricos
    /// van primero con su badge de conexión (cable/Bluetooth) y después, tras un separador,
    /// los endpoints de audio (entradas y salidas) que se miden por otra vía (WASAPI).</summary>
    private void RefreshLatencyTestDevices()
    {
        if (_latencyTestRunning) return; // no se cambia de dispositivo en plena prueba

        var options = CollectLatencyDevices();
        string? keep = _selectedLatencyKey;

        LatencyTestDeviceSelector.Items.Clear();
        foreach (var option in options)
        {
            LatencyTestDeviceSelector.Items.Add(MakeLatencyItem(option.Name, option, option.Key));
        }

        // --- Audio: separador + endpoints. La clave "audio:<id>" los aparta del espacio
        // VID/PID de los periféricos: se derivan a la via de medición WASAPI al correr.
        List<AudioEndpointInfo>? endpoints = null;
        try { endpoints = new List<AudioEndpointInfo>(_audioLatency.EnumerateEndpoints()); }
        catch (Exception ex) { _logging.LogWarning($"Test de latencia: no se pudieron listar endpoints de audio: {ex.Message}"); }
        if (endpoints is { Count: > 0 })
        {
            _audioEndpoints.Clear();
            foreach (var ep in endpoints) _audioEndpoints[ep.Id] = ep;
            LatencyTestDeviceSelector.Items.Add(new ComboBoxItem
            {
                IsEnabled = false,
                Content = new TextBlock
                {
                    Text = I18n.T("— Audio —"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)ThemeBrushes.Get("MutedBrush"),
                    Margin = new Thickness(0, 4, 0, 2)
                }
            });
            foreach (var ep in endpoints)
            {
                // El nombre limpio: el rol (Entrada/Salida) y "predeterminado" viajan como
                // badges dentro del ítem, no pegados al texto.
                var opt = new LatencyDeviceOption($"audio:{ep.Id}", ep.Name, null, true, InputTransport.Unknown);
                LatencyTestDeviceSelector.Items.Add(MakeLatencyItem(ep.Name, opt, opt.Key, audio: ep));
            }
        }

        // Elección: la anterior si sigue existiendo; si no, el PRIMER dispositivo REAL (los
        // separadores —ej. "— Audio —"— van deshabilitados y sin Tag: seleccionar uno dejaba
        // el desplegable sin dispositivo y el botón sin nada que medir).
        int index = -1, firstSelectable = -1;
        for (int i = 0; i < LatencyTestDeviceSelector.Items.Count; i++)
        {
            if (LatencyTestDeviceSelector.Items[i] is not ComboBoxItem item || item.Tag is not string tag) continue;
            if (firstSelectable < 0) firstSelectable = i;
            if (tag == keep) { index = i; break; }
        }
        if (index < 0) index = firstSelectable;

        if (index >= 0)
        {
            _selectedLatencyKey = (LatencyTestDeviceSelector.Items[index] as ComboBoxItem)?.Tag as string;
            LatencyTestDeviceSelector.SelectedIndex = index; // dispara el cambio: carga el tipo y muestra la última corrida
            UpdateLatencyHint();
            ShowStoredLatencyResult();
        }
        else
        {
            _selectedLatencyKey = null;
            LatencyTestResultsHost.Children.Clear();
            SetLatencyTestStatus(I18n.T("No hay dispositivos de entrada para medir."));
        }

        // La lista recién armada ya tiene ítems: el ancho de la caja sigue al dispositivo elegido y
        // el del popup queda listo ANTES de abrirlo (si no, se vería crecer en la animación).
        FitLatencySelectorWidth();
        FitLatencyPopupWidth();
    }

    /// <summary>Ítem del desplegable: ícono del tipo en azul + badge de conexión (cable o
    /// Bluetooth) cuando se conoce, y el nombre al lado. Para audio lleva el ícono de
    /// altavoz/auriculares y la etiqueta Entrada/Salida.</summary>
    private ComboBoxItem MakeLatencyItem(string label, LatencyDeviceOption option, string tag, AudioEndpointInfo? audio = null)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(new FontIcon
        {
            Glyph = audio is not null && audio.IsInput ? "\uE720"
                : audio is not null ? "\uE7F6"
                : option.DetectedKind is { } kind ? GlyphFor(kind) : GlyphForKind(UsbDeviceKind.Unknown),
            FontSize = 13,
            Foreground = ThemeBrushes.Get("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        // Rol y conexión como BADGES (pastillas), no como sufijos de texto. El ROL del endpoint
        // (Entrada/Salida) va ANTES del nombre y separado por el punto medio, como estaba antes:
        // "Entrada · Altavoces". El nombre queda en el medio de sus datos: el rol adelante y
        // "predeterminado" atrás, que es el que compara al dispositivo con los demás endpoints.
        // La CONEXIÓN del periférico va después del nombre: es un dato del dispositivo, no parte
        // de cómo se llama.
        if (audio is not null)
        {
            var meta = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            meta.Children.Add(LatencyBadge(I18n.T(audio.IsInput ? "Entrada" : "Salida")));
            meta.Children.Add(new TextBlock
            {
                Text = "·",
                FontSize = 13,
                Foreground = (Brush)ThemeBrushes.Get("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            line.Children.Add(meta);
        }
        line.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (audio is null)
        {
            // Conexión del periférico, sin afirmar más de lo que se sabe:
            //   • Bluetooth: sale de la ruta de Raw Input (dato firme).
            //   • 2,4 GHz: el dispositivo es un receptor inalámbrico (dato firme).
            //   • USB: cuelga del bus USB. NO se dice "cable": un dongle genérico que no se
            //     identifica no se puede distinguir de un periférico por cable, y ahí afirmar
            //     un cable sería mentira para cualquiera.
            // (El chip de "sin informes" se sacó: confundía estar quieto con no funcionar.)
            switch (option.Transport)
            {
                case InputTransport.Usb:
                    line.Children.Add(LatencyBadge("USB", glyph: "\uE88E"));
                    break;
                case InputTransport.Bluetooth:
                    // Los dos badges INALÁMBRICOS comparten el mismo ícono (WirelessGlyph), así
                    // de un vistazo se ve que la medición no va por cable. El texto sigue
                    // diciendo el enlace real.
                    line.Children.Add(LatencyBadge(I18n.T("Bluetooth"), glyph: WirelessGlyph));
                    break;
                case InputTransport.Wireless24:
                    // Nombres que no se traducen: son siglas y una frecuencia.
                    line.Children.Add(LatencyBadge("2,4 GHz", glyph: WirelessGlyph));
                    break;
            }

            // Capacidad del TEST, no del dispositivo: el sondeo se puede medir cuando el aparato
            // reporta POR SU CUENTA mientras lo usás (el mouse al moverse, el mando al mover los
            // ejes). El teclado queda afuera a propósito: solo habla cuando cambiás el estado, así
            // que su intervalo mide tu ritmo y no el suyo. Es indicador de DISPONIBILIDAD (verde),
            // no un veredicto de calidad.
            if (option.DetectedKind is InputDeviceKind.Mouse or InputDeviceKind.Gamepad)
                line.Children.Add(LatencyBadge(I18n.T("Sondeo disponible"), success: true));
        }
        else if (audio.IsDefault)
        {
            // DESPUÉS del nombre: "predeterminado" es lo que lo distingue de los otros
            // endpoints, mientras que el rol (adelante) describe al dispositivo mismo.
            line.Children.Add(LatencyBadge(I18n.T("predeterminado")));
        }
        return new ComboBoxItem { Content = line, Tag = tag };
    }

    /// <summary>
    /// Ícono de los badges INALÁMBRICOS (Bluetooth / 2,4 GHz): \uEB77 = GatewayRouter, el "router
    /// con ondas" de Segoe Fluent Icons — el equivalente monocromo del emoji 🛜, en lugar de las
    /// barras de señal (E1E9/E908), que se confundían con el medidor de carga. Existe en Segoe
    /// Fluent Icons 1.54 y en Segoe MDL2 Assets 1.86 (o sea: no sale caja en Windows 10).
    /// </summary>
    private const string WirelessGlyph = "\uEB77";

    /// <summary>Badge del desplegable (rol, conexión, estado). Pastilla RELLENA: el tinte de
    /// acento del tema como fondo, sin borde, con el texto y el ícono en el acento — el mismo
    /// lenguaje que ya usan las badges de la app (p. ej. los contadores de Limpieza). Antes era un
    /// chip con borde de 1 px sobre el fondo de chip y se leía como una caja más de la fila; el
    /// relleno la separa del texto del dispositivo sin competir con él. El fondo sale de una clave
    /// de paleta (no de un color literal), así que acompaña el tema claro y el oscuro.</summary>
    private static Border LatencyBadge(string text, string? glyph = null, bool success = false)
    {
        var accent = ThemeBrushes.Get(success ? "SuccessBrush" : "AccentBrush");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        // 11 px: las pastillas viajan DENTRO de las filas del desplegable, así que su texto
        // acompaña al del dispositivo. Con 10 quedaban como letra chica al lado del nombre.
        if (glyph is not null)
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = accent });
        return new Border
        {
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrushes.Get(success ? "SuccessTintBrush" : "AccentTintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };
    }

    /// <summary>
    /// OJO: acá NO se rearma la lista. Mutar los Items con el desplegable ABIERTO deja el popup
    /// con la lista vieja: el click cae en otro índice del que muestra el popup (y el Item
    /// elegido puede no tener Tag), así que la selección quedaba "vacía" o el test corría sobre
    /// el dispositivo anterior — justo lo que pasaba al elegir un endpoint de audio y terminar
    /// midiendo el periférico. La lista se rearma cuando los dispositivos se releen (carga de la
    /// página, botón Refrescar, cambio de idioma), no al abrirla.
    /// </summary>
    private void LatencyTestDeviceSelector_DropDownOpened(object sender, object e)
    {
        // La lista ya está armada (no se toca con el popup abierto): solo se le da el ancho que
        // necesita el ítem más largo, para que ningún nombre con sus badges salga recortado.
        FitLatencyPopupWidth(warnIfMissing: true);
    }

    /// <summary>
    /// Piso de la caja del desplegable: ahora que el ancho lo manda el contenido, sin un piso alto la
    /// caja se achicaba al elegir un dispositivo de nombre corto ("Mouse") y quedaba bailando entre un
    /// ítem y otro. Con el piso, la caja se estira cuando hace falta pero nunca baja de acá.
    /// (Antes eran 260 px: el valor del tope FIJO original.)
    /// </summary>
    private const double LatencySelectorMinWidth = 360;

    /// <summary>Borde del popup (1 px por lado) + el scrollbar vertical de la lista.</summary>
    private const double LatencyPopupChrome = 16;

    private bool _fittingLatencySelector;

    /// <summary>
    /// La caja del desplegable crece con lo que MUESTRA: el ítem elegido (nombre + sus badges) fija
    /// su ancho y el tope es el ancho del contenido de la card. Antes el tope era un valor FIJO
    /// (420 px), así que un nombre largo con sus badges se cortaba contra el borde.
    ///
    /// Por qué el recorte era DURO y no unos puntos suspensivos: el contenido de cada ítem es un
    /// StackPanel HORIZONTAL, que mide a sus hijos con ancho INFINITO; su TextBlock nunca recibe un
    /// ancho finito, así que el TextTrimming no llega a activarse y el sobrante se recorta contra el
    /// borde. Por eso el arreglo es el ancho (y no alcanza con pedir elipsis).
    /// </summary>
    private void FitLatencySelectorWidth()
    {
        if (_fittingLatencySelector) return;
        _fittingLatencySelector = true;
        try
        {
            // Sin ancho propio: WinUI mide la caja por contenido y el tope lo pone MaxWidth, que se
            // recalcula en cada ajuste porque la fila no es constante.
            LatencyTestDeviceSelector.Width = double.NaN;
            LatencyTestDeviceSelector.MaxWidth = Math.Max(LatencySelectorMinWidth, LatencySelectorFreeWidth());
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Test de latencia: no se pudo ajustar el ancho del desplegable ({ex.Message}).");
        }
        finally { _fittingLatencySelector = false; }
    }

    /// <summary>
    /// Espacio que le queda al desplegable: el ancho del contenido de la card —que es el límite real,
    /// porque la fila del selector está CENTRADA y mide lo que mide su contenido, así que su ancho no
    /// sirve como tope— menos lo que viaje en esa misma línea (hoy nada: el selector va solo) y las
    /// separaciones. Si todavía no se midió (primer layout) devuelve uno provisorio: el SizeChanged de
    /// la card lo corrige en cuanto hay ancho real.
    /// </summary>
    private double LatencySelectorFreeWidth()
    {
        if (LatencyTestDeviceSelector.Parent is not Panel row) return 520;

        // El tope es la línea de contenido de la card, no la fila del selector.
        double line = (LatencyTestCard.Child as FrameworkElement)?.ActualWidth ?? 0;
        if (line <= 0) line = row.ActualWidth;
        if (line <= 0) return 520;

        double reserved = 0;
        int siblings = 0;
        foreach (var child in row.Children)
        {
            if (ReferenceEquals(child, LatencyTestDeviceSelector)) continue;
            if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible) continue;
            reserved += fe.ActualWidth;
            siblings++;
        }
        // Una separación (Spacing) entre cada par de controles de la línea.
        double spacing = (row as StackPanel)?.Spacing ?? 0;
        double free = line - reserved - spacing * siblings;
        return free > 0 ? free : LatencySelectorMinWidth;
    }

    /// <summary>
    /// El popup: WinUI lo abre con el ANCHO DEL CONTROL (su ScrollViewer interno lleva
    /// MinWidth = TemplateSettings.DropDownContentMinWidth y el scroll horizontal está
    /// deshabilitado, así que nada más ancho que la caja se puede ver). Por eso un ítem más ancho
    /// que el elegido quedaba recortado. Acá se mide cada ítem y se le pone ese ancho como mínimo al
    /// borde del popup, con el ancho de la ventana como techo para no salirse de la pantalla.
    /// </summary>
    /// <param name="warnIfMissing">Avisar si no se encontró el borde del popup: solo tiene sentido
    /// con el popup abierto (antes del primer layout la plantilla todavía no está aplicada).</param>
    private void FitLatencyPopupWidth(bool warnIfMissing = false)
    {
        try
        {
            double widest = 0;
            foreach (var entry in LatencyTestDeviceSelector.Items)
            {
                if (entry is not ComboBoxItem item) continue;
                if (item.Content is FrameworkElement content)
                {
                    // Ancho natural del ítem: su contenido medido sin tope + el relleno del ítem.
                    content.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    widest = Math.Max(widest, content.DesiredSize.Width + item.Padding.Left + item.Padding.Right);
                }
                else
                {
                    item.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    widest = Math.Max(widest, item.DesiredSize.Width);
                }
            }
            if (widest <= 0) return;

            double width = Math.Min(
                Math.Max(widest + LatencyPopupChrome, LatencyTestDeviceSelector.ActualWidth),
                LatencyPopupMaxWidth());

            if (LatencyPopupPane() is not { } pane)
            {
                if (warnIfMissing) _logging.LogWarning("Test de latencia: no se encontró el popup del desplegable para ajustarle el ancho.");
                return;
            }
            pane.MinWidth = width;

            // Diagnóstico (una línea por apertura): con esto se puede confirmar desde el log que el
            // ancho se aplicó, en vez de deducirlo de que "ahora se ve bien".
            _logging.LogDebug($"Test de latencia: popup del desplegable a {width:0} px (ítem más ancho {widest:0} px, caja {LatencyTestDeviceSelector.ActualWidth:0} px).");
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Test de latencia: no se pudo ajustar el ancho del popup ({ex.Message}).");
        }
    }

    /// <summary>El borde del popup del desplegable: vive dentro de la plantilla del ComboBox, así
    /// que no está en el namescope de la página y hay que buscarlo en el árbol visual.</summary>
    private FrameworkElement? LatencyPopupPane()
    {
        var popup = FindDescendant<Microsoft.UI.Xaml.Controls.Primitives.Popup>(LatencyTestDeviceSelector);
        return popup?.Child as FrameworkElement;
    }

    /// <summary>Techo del popup: el ancho de la ventana (menos un margen) o, si todavía no hay
    /// XamlRoot, el de la propia caja más un margen.</summary>
    private double LatencyPopupMaxWidth()
    {
        double window = XamlRoot?.Size.Width ?? 0;
        double fallback = LatencyTestDeviceSelector.ActualWidth + 220;
        return Math.Max(LatencySelectorMinWidth, window > 0 ? window - 48 : fallback);
    }

    /// <summary>Primer descendiente del tipo pedido en el árbol visual.</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void LatencyTestDeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // El ancho de la caja sigue al ítem elegido (nombre + badges), antes de cualquier salida
        // temprana: también en la selección que hace la propia página al armar la lista.
        FitLatencySelectorWidth();
        if (LatencyTestDeviceSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string key) return;
        if (key == _selectedLatencyKey) return;
        _selectedLatencyKey = key;
        UpdateLatencyHint();
        if (!_latencyTestRunning) SetLatencyTestRunningUi(false); // etiqueta: "Medir en el bus" / "Medir audio" / "Iniciar test"
        // Cambiar de dispositivo limpia el onboarding visual: la cuenta 3·2·1 o el gesto
        // que quedó dibujado son del dispositivo anterior.
        HideLatencyDemo();
        ShowStoredLatencyResult();
    }

    /// <summary>True si la clave elegida apunta a un endpoint de audio ("audio:<id>").</summary>
    private bool IsAudioKey(string? key) => key is not null && key.StartsWith("audio:", StringComparison.OrdinalIgnoreCase);

    /// <summary>El endpoint de audio elegido (null si la clave no es de audio o ya no existe).
    /// Se prefiere el que quedó cacheado al armar el desplegable: si WASAPI no lo enumera en
    /// este instante (el dispositivo se está reiniciando, otro proceso lo tomó en exclusivo),
    /// el test igual puede correr con los datos que ya teníamos en vez de decir "no está".</summary>
    private AudioEndpointInfo? SelectedAudioEndpoint()
    {
        if (!IsAudioKey(_selectedLatencyKey)) return null;
        string id = _selectedLatencyKey!["audio:".Length..];
        if (_audioEndpoints.TryGetValue(id, out var cached)) return cached;
        try { return _audioLatency.EnumerateEndpoints().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)); }
        catch { return null; }
    }

    /// <summary>Tipo del dispositivo elegido: lo elegido a mano manda; si está en automático,
    /// lo que detectó el driver (y si no, lo que dice Raw Input).</summary>
    private InputDeviceKind? DetectLatencyKind(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var option in CollectLatencyDevices())
            if (string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase)) return option.DetectedKind;
        return null;
    }

    /// <summary>Tipo del dispositivo elegido: no hay selector manual, lo detecta el driver
    /// (y si no, lo que dice Raw Input), así que la pista y el test apuntan a lo que es.</summary>
    private InputDeviceKind? EffectiveLatencyKind() => DetectLatencyKind(_selectedLatencyKey);

    private static string KindLabelKey(InputDeviceKind? kind) => kind switch
    {
        InputDeviceKind.Mouse => "Mouse",
        InputDeviceKind.Keyboard => "Teclado",
        InputDeviceKind.Gamepad => "Mando",
        _ => "Automático"
    };

    /// <summary>Instrucción según lo que REALMENTE se mide con ese tipo.</summary>
    private void UpdateLatencyHint()
    {
        string text;
        if (IsAudioKey(_selectedLatencyKey))
        {
            text = I18n.T("Se mide el motor de audio (WASAPI) y la ida y vuelta del impulso: no hace falta tocar nada.");
        }
        else if (EffectiveLatencyKind() == InputDeviceKind.Gamepad)
        {
            // El mando no lleva pista: el gesto lo dice el dibujo (el stick girando pegado a su tope),
            // y repetirlo en texto era ruido — el test se mide girando el stick, no leyendo.
            text = "";
        }
        else
        {
            text = I18n.T(EffectiveLatencyKind() switch
            {
                InputDeviceKind.Mouse => "Mové el mouse en círculos durante la prueba.",
                InputDeviceKind.Keyboard => "El teclado no emite informes continuos: se mide el bus (cada cuánto manda un informe), no su polling.",
                _ => "Usá el dispositivo durante la prueba."
            });
        }

        LatencyTestHint.Text = text;
        // Sin pista, el renglón NO se reserva: un hueco vacío entre el dibujo y el botón se ve igual de
        // mal que un texto de más.
        LatencyTestHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Etiqueta del botón según qué se va a medir: en teclado es "Medir en el bus"
    /// (no hay polling que apurar: el test mide la cadencia del bus), en audio "Medir audio"
    /// y en el resto "Iniciar test".</summary>
    private string RunButtonLabelKey(bool running)
    {
        if (running) return "Cancelar";
        if (IsAudioKey(_selectedLatencyKey)) return "Medir audio";
        return EffectiveLatencyKind() == InputDeviceKind.Keyboard ? "Medir en el bus" : "Iniciar test";
    }

    private async void LatencyTestRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latencyTestRunning)
        {
            CancelLatencyTest(); // mientras corre, este botón es "Cancelar"
            return;
        }

        string? key = _selectedLatencyKey;
        if (string.IsNullOrEmpty(key))
        {
            SetLatencyTestStatus(I18n.T("Elegí un dispositivo y corré el test."));
            return;
        }

        _latencyTestRunning = true;
        _runningLatencyKind = EffectiveLatencyKind();
        _latencyTestCts = new CancellationTokenSource();
        SetLatencyTestRunningUi(true);
        LatencyTestResultsHost.Children.Clear();
        _shownResult = null;
        _shownPrevious = null;
        SetLatencyTestStatus("");

        // Vía del BUS (ETW/xHCI): para teclado el test correcto es la cadencia del bus —
        // el teclado no emite informes continuos, así que el "polling" que ve la app no
        // existe: lo que se mide es cada cuánto el endpoint ENTREGA transferencias. Sin demo
        // ni gráfico en vivo (esa UI es del flujo continuo): la ventana es pasiva.
        if (!IsAudioKey(key) && EffectiveLatencyKind() == InputDeviceKind.Keyboard)
        {
            await RunBusCadenceAsync(key);
            return;
        }

        // Onboarding visual ANTES de medir: la ventana arranca cuando la cuenta terminó.
        await StartLatencyDemoAsync(CurrentDemoKind(), _latencyTestCts.Token);
        if (!_latencyTestRunning)
        {
            // Cancelaron durante la cuenta: el onboarding visual se apaga acá (el resto de
            // las salidas lo hacen en su finally).
            HideLatencyDemo();
            return;
        }
        // Cancelaron durante el 3·2·1 (que ahora se muestra en TODOS los tipos de test): se
        // cierra la corrida acá mismo. Sin esto el botón quedaba en "Cancelar" para siempre.
        if (_latencyTestCts.IsCancellationRequested)
        {
            FinishLatencyTest();
            return;
        }

        // Vía de AUDIO: se mide por WASAPI (motor + ida y vuelta), no por informes HID, así
        // que NO se abre el trazo en vivo — ese gráfico dibuja intervalos HID y quedaría
        // vacío (parecia que el test no hacía nada mientras corría).
        if (IsAudioKey(key))
        {
            LatencyLiveArea.Visibility = Visibility.Collapsed;
            SetLatencyTestStatus(I18n.T("Midiendo el motor de audio (WASAPI)…"));
            try
            {
                var endpoint = SelectedAudioEndpoint();
                if (endpoint is null)
                {
                    _logging.LogWarning("Test de latencia (audio): el endpoint elegido ya no está disponible.");
                    SetLatencyTestStatus(I18n.T("Ese endpoint de audio ya no está disponible."));
                    return;
                }
                _logging.LogInfo($"Test de latencia (audio): iniciando sobre {endpoint.Name} ({endpoint.Id}).");

                var progress = new Progress<AudioTestProgress>(p =>
                    SetLatencyTestStatus(I18n.T(p.RoundTripPhase
                        ? "Ida y vuelta: intento {0} de {1}…"
                        : "Motor de audio: {0} muestras…", p.Attempt, p.Attempts)));
                var run = await _audioLatency.RunAsync(endpoint, AudioLatencyService.DefaultDurationSeconds, progress, _latencyTestCts.Token);

                if (run.Stopped)
                {
                    SetLatencyTestStatus("");
                    LatencyLiveArea.Visibility = Visibility.Collapsed;
                    ShowStoredLatencyResult();
                    return;
                }

                _lastAudioRun = run;
                SetLatencyTestStatus($"{I18n.T("Última prueba")}: {run.Timestamp.ToLocalTime():g}");
                RenderAudioLatencyResult(run);
            }
            catch (OperationCanceledException)
            {
                SetLatencyTestStatus("");
                // Cancelar no deja medición: el trazo a medias se retira (antes quedaba
                // dibujado en pantalla hasta la corrida siguiente).
                ClearLatencyLive();
                ShowStoredLatencyResult();
            }
            catch (Exception ex)
            {
                _logging.LogWarning($"Test de latencia (audio): la corrida falló: {ex.Message}");
                SetLatencyTestStatus(I18n.T("No se pudo completar la prueba."));
            }
            finally
            {
                _latencyTestRunning = false;
                _latencyTestCts?.Dispose();
                _latencyTestCts = null;
                ClearLatencyLive();
                HideLatencyDemo();
                SetLatencyTestRunningUi(false);
            }
            return;
        }

        StartLatencyLive();
        SetLatencyTestStatus(I18n.T("Midiendo… {0} s", InputLatencyTestService.DefaultDurationSeconds));

        try
        {
            // El servicio mide una ventana acotada y no reporta avance: el reloj local
            // muestra los segundos que van de esa ventana.
            var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            int elapsed = 0;
            clock.Tick += (_, _) => SetLatencyTestStatus(I18n.T("Midiendo… {0} s", ++elapsed));
            clock.Start();
            InputLatencyTestRun run;
            try
            {
                run = await _latencyTest.RunAsync(key, InputLatencyTestService.DefaultDurationSeconds, _latencyTestCts.Token);
            }
            finally
            {
                clock.Stop();
            }

            if (run.Stopped)
            {
                // Se detuvo a mano (o la ventana se fue a la bandeja): se vuelve a lo último
                // mostrado, como si no hubiera pasado nada. El trazo a medias se retira: no es
                // de ninguna corrida real y dejarlo confunde.
                SetLatencyTestStatus("");
                ClearLatencyLive();
                ShowStoredLatencyResult();
                return;
            }

            _lastRun = run;
            var result = run.Results.FirstOrDefault();
            if (result == null)
            {
                // El dispositivo elegido no mandó ni un informe: es una respuesta, no un error.
                SetLatencyTestStatus(I18n.T("Ese dispositivo no emitió informes durante la prueba."));
            }
            else
            {
                SetLatencyTestStatus($"{I18n.T("Última prueba")}: {run.Timestamp.ToLocalTime():g}");
                RenderLatencyResult(result, null);
            }
        }
        catch (OperationCanceledException)
        {
            SetLatencyTestStatus("");
            ShowStoredLatencyResult();
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Test de latencia: la corrida falló: {ex.Message}");
            SetLatencyTestStatus(I18n.T("No se pudo completar la prueba."));
        }
        finally
        {
            _latencyTestRunning = false;
            _latencyTestCts?.Dispose();
            _latencyTestCts = null;
            StopLatencyLive();
            HideLatencyDemo();
            SetLatencyTestRunningUi(false);
        }
    }

    private void CancelLatencyTest()
    {
        if (!_latencyTestRunning) return;
        _latencyTestCts?.Cancel();
    }

    /// <summary>Cierra una corrida que se canceló ANTES de llegar a medir (durante el
    /// onboarding): deja el botón y el trazo como estaban, sin quedar en estado "midiendo".</summary>
    private void FinishLatencyTest()
    {
        _latencyTestRunning = false;
        _latencyTestCts?.Dispose();
        _latencyTestCts = null;
        ClearLatencyLive();
        HideLatencyDemo();
        SetLatencyTestRunningUi(false);
        SetLatencyTestStatus("");
        ShowStoredLatencyResult();
    }

    // ===== Cadencia de TECLADO medida en el BUS (ETW/xHCI) =====

    /// <summary>
    /// Corrida de la cadencia de bus: modo pasivo (el usuario sigue usando la PC), con techo
    /// de 45 s y corte temprano al juntar 12 informes. Cruza los sellos del kernel con los de
    /// Raw Input para dar además el tramo de host (kernel → app).
    /// </summary>
    private async Task RunBusCadenceAsync(string key)
    {
        try
        {
            // La MISMA cuenta 3·2·1 que el resto de los tests: la ventana del bus arranca
            // cuando la cuenta terminó, así el 3·2·1 no se come el principio de la captura.
            await StartLatencyDemoAsync(LatencyDemoKind.Keyboard, _latencyTestCts!.Token);
            if (!_latencyTestRunning) return;

            // Ventana FIJA de 15 s: dura exactamente lo que dice el reloj de abajo y NO se corta por
            // cantidad de informes. Cada intento de corte temprano trajo su propio problema: sin
            // piso de tiempo, seis teclas juntaban los informes justos y la traza quedaba con cuatro
            // transferencias (el número salía del ritmo de tecleo, no del bus); con piso, seguía
            // dependiendo de cuánto tecleaste. Una duración fija es comparable entre corridas, que es
            // lo que hace útil medir dos veces. Cuando no hay pulsaciones, eso lo dice el resultado.
            const int capSeconds = 15;

            // Captura de sellos de la app durante la MISMA ventana (para cruzar con el kernel):
            // se abre antes y el servicio la cierra al terminar, vía el proveedor.
            _latencyMonitor.BeginSampling();

            // Reloj visible: el servicio no reporta avance, así que sin esto la captura parecía
            // colgada (y en el peor caso se agotaba la ventana sin una sola pulsación). El conteo de
            // informes va al lado para que se vea que SÍ está capturando, sin que corte nada.
            var watch = Stopwatch.StartNew();
            var clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            clock.Tick += (_, _) =>
            {
                int reports = _latencyMonitor.GetSamplingStampCount(key);
                double left = Math.Max(0, capSeconds - watch.Elapsed.TotalSeconds);
                // Agotada la ventana, todavía falta cerrar el ETW y volcar la traza (más de un
                // segundo): decir que quedan 0 s mientras eso pasa era otra forma de mentir el reloj.
                SetLatencyTestStatus(left <= 0.5
                    ? I18n.T("Analizando la traza del bus…")
                    : I18n.T("Capturando en el bus: {0} informes · {1} s restantes. Seguí tecleando con normalidad.", reports, (int)Math.Ceiling(left)));
            };
            clock.Start();
            BusCadenceResult result;
            try
            {
                result = await _busCadence.MeasureAsync(key, capSeconds, _latencyTestCts.Token,
                    appStampsProvider: () => _latencyMonitor.EndSamplingStamps(key));
            }
            finally
            {
                clock.Stop();
                watch.Stop();
            }

            if (_latencyTestCts.IsCancellationRequested)
            {
                SetLatencyTestStatus(I18n.T("Captura cancelada."));
            }
            else if (result.Error == "no-slot")
            {
                // La traza capturó transferencias pero no pudo atribuirlas a este teclado:
                // el mapeo SlotId→VID/PID sale del evento de enumeración, que solo aparece si
                // el teclado se (re)conectó DENTRO de la ventana. Se dice qué hacer.
                SetLatencyTestStatus(I18n.T("La traza vio transferencias pero no pudo identificar este teclado (no hubo evento de enumeración en la ventana). Desconectá y volvé a conectar el teclado, y medí de nuevo: dura 15 segundos."));
            }
            else if (!string.IsNullOrEmpty(result.Error))
            {
                _logging.LogWarning($"Cadencia por bus: no se pudo medir ({result.Error}).");
                SetLatencyTestStatus(I18n.T("No se pudo abrir la traza del bus (ETW) en esta máquina."));
            }
            else if (!result.HasData)
            {
                SetLatencyTestStatus(I18n.T("No hubo ninguna pulsación durante la captura: el teclado solo informa cuando lo tocás. Probá de nuevo (dura 15 segundos) y seguí usando la PC con normalidad."));
            }
            else if (!string.Equals(_selectedLatencyKey, key, StringComparison.OrdinalIgnoreCase))
            {
                // Cambió el dispositivo elegido durante la captura: el resultado es de otro.
                SetLatencyTestStatus(I18n.T("Captura descartada: cambiaste de dispositivo mientras medía."));
            }
            else
            {
                SetLatencyTestStatus("");
                RenderBusCadence(result);
            }
        }
        catch (OperationCanceledException)
        {
            SetLatencyTestStatus(I18n.T("Captura cancelada."));
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Cadencia por bus: la corrida falló: {ex.Message}");
            SetLatencyTestStatus(I18n.T("No se pudo completar la medición del bus."));
        }
        finally
        {
            _latencyTestRunning = false;
            _latencyTestCts?.Dispose();
            _latencyTestCts = null;
            // La corrida del bus también muestra el 3·2·1: sin esto el onboarding visual
            // quedaba dibujado para siempre al terminar "Medir en el bus".
            HideLatencyDemo();
            SetLatencyTestRunningUi(false);
        }
    }

    /// <summary>Fila del resultado del BUS: el piso de cadencia como número principal.</summary>
    private void RenderBusCadence(BusCadenceResult result)
    {
        LatencyTestResultsHost.Children.Clear();
        var (row, refs) = BuildLatencyTestRow();
        LatencyTestResultsHost.Children.Add(row);

        var device = CollectLatencyDevices().FirstOrDefault(o =>
            string.Equals(o.Key, _selectedLatencyKey, StringComparison.OrdinalIgnoreCase));
        int declaredHz = device is { DetectedKind: InputDeviceKind.Keyboard } ? 1000 : 0;
        double declaredPeriodMs = declaredHz > 0 ? 1000.0 / declaredHz : 0;
        double floorHz = result.FloorMs > 0 ? 1000.0 / result.FloorMs : 0;

        refs.Icon.Glyph = "\uE765"; // teclado
        refs.Name.Text = device?.Name ?? _selectedLatencyKey ?? "";
        // Único veredicto que se sigue mostrando: no es una calificación, dice QUÉ se midió
        // (las badges Excelente/Aceptable/Inestable se quitaron del resultado).
        refs.Verdict.Visibility = Visibility.Visible;
        refs.VerdictText.Text = I18n.T("Medido en el bus");
        refs.VerdictText.Foreground = ThemeBrushes.Get("AccentBrush");
        refs.Verdict.BorderBrush = ThemeBrushes.Get("AccentBrush");

        // Columnas propias del bus, con nombres propios: el PISO de cadencia es el número principal
        // (la magnitud análoga a la del mouse), MÍN es la transferencia más rápida vista, HOST P99 es
        // el camino kernel → app y TRANSFERENCIAS es sobre cuántas se calculó todo.
        //
        // Por qué NO se muestran el MÁX y el P99 del test continuo: este test no tiene una serie de
        // intervalos propia. Los huecos entre transferencias dependen de cuánto tardás entre tecla y
        // tecla, así que su máximo y su p99 son TU ritmo, no una latencia — antes esas dos columnas
        // quedaban en "—" fijo. Ahora se llenan con las magnitudes que el test sí mide.
        refs.SetLabels(I18n.T("Piso"), I18n.T("Mín"), I18n.T("Host p99"), I18n.T("Hz"), I18n.T("Transferencias"));
        refs.Avg.Text = $"{result.FloorMs:0.00} ms";
        refs.Max.Text = result.MinMs > 0 ? $"{result.MinMs:0.00} ms" : "—";
        refs.P99.Text = result.HasHostPath ? $"{result.HostP99Ms:0.00} ms" : "—";
        refs.Hz.Text = floorHz > 0 ? $"{floorHz:0} Hz" : "—";
        refs.Gaps.Text = result.Reports.ToString();

        var notes = new List<string>();
        notes.Add(I18n.T("Piso de entrega medido por el kernel (ETW/xHCI) sobre {0} transferencias: cada transferencia del endpoint llega sellada por el controlador, y el piso de esos sellos es la cadencia con la que el teclado ENTREGA informes — la misma magnitud que la cadencia de un mouse. No es la latencia de la tecla (t0 → informe): eso no está en ningún log del sistema.", result.Reports));

        // Camino del host (kernel → app): el tramo del retardo que sí ve el software y el que
        // tocan los tweaks del sistema. Solo se muestra si el cruce dio suficiente confianza.
        if (!result.HasHostPath)
        {
            // Sin cruce no hay número de host, y una columna vacía sin motivo es justo lo que hacía
            // ilegible este resultado: se dice por qué falta.
            notes.Add(I18n.T("Camino del host (kernel → app) sin datos en esta corrida: se muestra con al menos 8 informes emparejados entre la traza del kernel y la llegada a la app. Con pocas pulsaciones no hay con qué cruzar; tecleá más en la próxima medición."));
        }
        else
        {
            notes.Add(I18n.T("Camino del host (kernel → app): mediana {0:0.00} ms · p99 {1:0.00} ms sobre {2} informes emparejados. Es el retardo entre que el kernel sella la transferencia y que Raw Input la entrega a la app — el tramo que tocan los tweaks del sistema. Incluye el sesgo del volcado ETW (~1 ms, fijo): para comparar A/B es válido porque se cancela; para un número absoluto, restalo mentalmente.", result.HostMedianMs, result.HostP99Ms, result.MatchedReports));
        }
        if (declaredHz > 0)
        {
            // Solo el piso que ALCANZA el declarado es afirmativo. Un piso más lento es
            // inconclusivo: con tecleo liviano refleja el ritmo de la mano, no el dispositivo —
            // culpar al firmware sería mentir (el spike lo midió: tecleo suelto da piso ~23 ms).
            notes.Add(declaredPeriodMs > 0 && result.FloorMs > declaredPeriodMs * 1.5
                ? I18n.T("El piso ({0:0.00} ms) quedó más lento que lo declarado ({1:0.###} ms): con tecleo liviano eso refleja tu ritmo, no el dispositivo. Para demostrar su cadencia real, generá transiciones rápidas (aplastá y rodá teclas sin parar) y volvé a medir; si aun así no baja del declarado, ahí sí es firmware.", result.FloorMs, declaredPeriodMs)
                : I18n.T("Coincide con lo declarado ({0:0.###} ms): el endpoint entrega al nivel de su descriptor.", declaredPeriodMs));
        }
        if (result.Reports < 10)
            notes.Add(I18n.T("Pocas transferencias ({0}): la mediana del piso es más frágil con menos de 10. Corrélo de nuevo y tecleá más durante la ventana.", result.Reports));
        refs.Compare.Text = string.Join(" ", notes);
    }

    /// <summary>
    /// Espacio y Enter son las teclas con las que cualquier control enfocado se ACTIVA, y durante el
    /// test la tarea del usuario es justamente teclear: con el foco en el botón de la prueba —que
    /// durante la corrida dice "Cancelar"— la barra espaciadora volvía a disparar su Click y el test
    /// se cortaba solo. El evento PREVIO (túnel) pasa por la card antes que por el control enfocado,
    /// así que marcarlo como usado deja esas dos teclas para el test y nada más.
    ///
    /// No se pierde medición: el medidor recibe las pulsaciones por Raw Input (RIDEV_INPUTSINK), no
    /// por este evento de la UI, así que la barra espaciadora igual enciende su tecla en pantalla.
    /// Fuera de una corrida no se toca nada: Espacio/Enter siguen sirviendo para iniciar el test con
    /// el teclado.
    /// </summary>
    private void OnLatencyCardPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_latencyTestRunning) return;
        if (e.Key == Windows.System.VirtualKey.Space || e.Key == Windows.System.VirtualKey.Enter)
            e.Handled = true;
    }

    private void SetLatencyTestRunningUi(bool running)
    {
        LatencyTestRunButtonLabel.Text = I18n.T(RunButtonLabelKey(running));
        LatencyTestRunButtonIcon.Glyph = running ? "\uE711" : "\uE768";
        LatencyTestProgress.IsActive = running;
        LatencyTestProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== Trazo en vivo y onboarding visual =====

    /// <summary>Barras del trazo: se crean UNA vez y se reutilizan (rehacer 120 rectángulos por
    /// tick sería basura para el GC a 8 ticks por segundo).</summary>
    private const int LiveBars = 120;
    private readonly List<Rectangle> _liveBars = new();
    private DispatcherTimer? _latencyLiveTimer;
    private Stopwatch? _latencyLiveWatch;

    private void StartLatencyLive()
    {
        LatencyLiveArea.Visibility = Visibility.Visible;
        _latencyLiveWatch = Stopwatch.StartNew();
        _latencyLiveTimer ??= CreateLatencyLiveTimer();
        _latencyLiveTimer.Start();
        UpdateLatencyLive(); // primer cuadro sin esperar el tick
    }

    private void StopLatencyLive()
    {
        _latencyLiveTimer?.Stop();
        _latencyLiveWatch = null;
    }

    private DispatcherTimer CreateLatencyLiveTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
        timer.Tick += (_, _) => UpdateLatencyLive();
        return timer;
    }

    /// <summary>Un cuadro del "en vivo": reloj, progreso, números y trazo.</summary>
    private void UpdateLatencyLive()
    {
        if (!_latencyTestRunning) return;

        if (_latencyLiveWatch != null)
        {
            double elapsed = _latencyLiveWatch.Elapsed.TotalSeconds;
            double total = InputLatencyTestService.DefaultDurationSeconds;
            LatencyLiveProgress.IsIndeterminate = false;
            LatencyLiveProgress.Value = Math.Clamp(elapsed / total * 100, 0, 100);
            FormatClock(LatencyLiveTimeText, $"{I18n.T("Restan")} ", Math.Max(0, total - elapsed));
        }

        var live = _latencyMonitor.GetLiveSample(_selectedLatencyKey ?? "", LiveBars * 8, 2500, _runningLatencyKind);
        if (live == null)
        {
            LatencyLiveHzText.Text = "--";
            LatencyLiveAvgText.Text = "--";
            LatencyLiveMinText.Text = "--";
            LatencyLiveMaxText.Text = "--";
            LatencyLiveSamplesText.Text = "0";
            return;
        }

        LatencyLiveHzText.Text = $"{live.Hz:0} Hz";
        LatencyLiveAvgText.Text = $"{live.AvgMs:0.00} ms";
        LatencyLiveMinText.Text = $"{live.MinMs:0.00} ms";
        LatencyLiveMaxText.Text = $"{live.MaxMs:0.00} ms";
        LatencyLiveSamplesText.Text = live.Samples.ToString("N0");
        DrawLiveTrace(live);
    }

    /// <summary>Reloj corto: "00:07.4" abajo del minuto, "01:23" arriba.</summary>
    private static void FormatClock(TextBlock target, string prefix, double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        target.Text = prefix + (span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes:00}:{span.Seconds:00}"
            : $"00:{span.Seconds:00}.{span.Milliseconds / 100}");
    }

    private void EnsureLiveBars()
    {
        if (_liveBars.Count > 0) return;
        var normal = ThemeBrushes.Get("AccentBrush");
        for (int i = 0; i < LiveBars; i++)
        {
            var bar = new Rectangle { Fill = normal, Width = 2, Height = 0 };
            _liveBars.Add(bar);
            LatencyLivePlot.Children.Add(bar);
        }
    }

    /// <summary>
    /// Dibuja el trazo de intervalos. Cada barra se queda con el PEOR intervalo de su tramo
    /// (peak-hold): al agregar no se puede perder el pico, que es justo lo que hay que ver. Las
    /// que se salen de 4x el promedio se pintan en rojo, así un pico se detecta de un vistazo.
    /// </summary>
    private void DrawLiveTrace(InputLatencyMonitorService.InputLatencyLive live)
    {
        EnsureLiveBars();
        double width = LatencyLivePlot.ActualWidth;
        double height = LatencyLivePlot.ActualHeight;
        if (width <= 2 || height <= 2 || live.Intervals.Length == 0) return;

        var alert = ThemeBrushes.Get("ErrorBrush");
        var normal = ThemeBrushes.Get("AccentBrush");

        double max = 0;
        foreach (var value in live.Intervals) if (value > max) max = value;
        if (max <= 0) return;

        double slotWidth = width / LiveBars;
        for (int slot = 0; slot < LiveBars; slot++)
        {
            int from = (int)((long)slot * live.Intervals.Length / LiveBars);
            int to = (int)((long)(slot + 1) * live.Intervals.Length / LiveBars);
            double peak = 0;
            for (int i = from; i < to && i < live.Intervals.Length; i++)
                if (live.Intervals[i] > peak) peak = live.Intervals[i];

            var bar = _liveBars[slot];
            double barHeight = peak <= 0 ? 0 : Math.Clamp(peak / max * (height - 2), 1, height - 2);
            bar.Width = Math.Max(1, slotWidth - 1);
            bar.Height = barHeight;
            bar.Fill = peak > live.AvgMs * 4 ? alert : normal;
            Canvas.SetLeft(bar, slot * slotWidth);
            Canvas.SetTop(bar, height - barHeight);
        }
    }

    // ---- Onboarding visual: cuenta regresiva 3·2·1 y el gesto que pide cada periférico ----

    private enum LatencyDemoKind { None, Mouse, Keyboard, Gamepad, Audio }

    private DispatcherTimer? _latencyDemoTimer;
    private int _latencyDemoRun;      // token anti-carrera: solo la ÚLTIMA demo se dibuja
    private int _latencyDemoTick;
    private FrameworkElement? _demoOrbit;   // lo que recorre la órbita: el ícono del mouse o el punto del mando
    private double _demoOrbitSize;          // lado del orbe, para centrarlo SOBRE la órbita
    private double _demoOrbitRadiusX, _demoOrbitRadiusY;

    /// <summary>Una tecla dibujada del teclado en pantalla. Guarda su estado (encendida o no) para
    /// no repintar en cada tick, y el VKey que la enciende: es el de Windows ('A' = 0x41).</summary>
    private sealed class DemoKeyCap
    {
        public Border Cap = null!;
        public TextBlock Label = null!;
        public int VKey;
        public bool Lit;
        public long LitUntilMs;
    }

    private readonly List<DemoKeyCap> _demoCaps = new();
    private readonly Dictionary<int, DemoKeyCap> _demoCapsByVKey = new();

    // Dispositivos del teclado elegido: el teclado en pantalla solo enciende las teclas del que se
    // está midiendo (varios handles pueden compartir el mismo VID/PID, como las dos mitades de un
    // receptor inalámbrico).
    private HashSet<long> _demoKeyDeviceIds = new();

    // Contadores de diagnóstico del teclado en pantalla (cuentan, no guardan QUÉ tecla): dejan ver en
    // el log si no llega ninguna pulsación, si el filtro por dispositivo las descarta, o si el
    // problema es solo visual.
    private long _demoKeysSeen, _demoKeysLit, _demoKeysFiltered;

    private readonly List<Rectangle> _demoWaves = new();
    private Canvas? _demoStage;

    /// <summary>Qué gesto corresponde al dispositivo elegido: los endpoints de audio también
    /// tienen su gesto (ondas), así TODOS los tipos de test muestran la cuenta 3·2·1.</summary>
    private LatencyDemoKind CurrentDemoKind()
    {
        if (IsAudioKey(_selectedLatencyKey)) return LatencyDemoKind.Audio;
        return EffectiveLatencyKind() switch
        {
            InputDeviceKind.Mouse => LatencyDemoKind.Mouse,
            InputDeviceKind.Keyboard => LatencyDemoKind.Keyboard,
            InputDeviceKind.Gamepad => LatencyDemoKind.Gamepad,
            _ => LatencyDemoKind.None
        };
    }

    /// <summary>
    /// Cuenta regresiva 3·2·1 y después el gesto. La medición arranca cuando esta tarea
    /// termina: así el 3·2·1 no se come los primeros segundos de la ventana de prueba.
    /// </summary>
    private async Task StartLatencyDemoAsync(LatencyDemoKind kind, CancellationToken token)
    {
        if (kind == LatencyDemoKind.None) return;

        // El medidor tiene que estar ESCUCHANDO: su hilo de mensajes y el registro de Raw Input se
        // abren en Start(), y hasta ahora el único que lo llamaba era el test continuo (mouse/mando).
        // Un teclado va por la vía del BUS, que no lo llama: el pump nunca arrancaba, así que no
        // llegaba ni un WM_INPUT y el teclado en pantalla quedaba muerto (y los sellos de la app
        // para cruzar con el bus, vacíos). Idempotente.
        _latencyMonitor.Start();

        // Los dispositivos del teclado elegido se resuelven ANTES de la cuenta regresiva: consultar
        // el bus puede tardar unos milisegundos y no queremos que se note como un tirón en el 3·2·1.
        _demoKeyDeviceIds = DemoSelectedDeviceIds();

        int run = ++_latencyDemoRun;
        try
        {
            var countdown = new TextBlock
            {
                Text = "3",
                FontSize = 34,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeBrushes.Get("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            LatencyDemoHost.Visibility = Visibility.Visible;
            BuildDemoGrid();
            LatencyDemoStage.Content = countdown;

            for (int n = 3; n >= 1; n--)
            {
                if (run != _latencyDemoRun || token.IsCancellationRequested) { HideLatencyDemo(); return; }
                countdown.Text = n.ToString();
                try
                {
                    await Task.Delay(900, token);
                }
                catch (TaskCanceledException)
                {
                    HideLatencyDemo();
                    return;
                }
            }

            if (run != _latencyDemoRun || token.IsCancellationRequested) { HideLatencyDemo(); return; }
            BuildLatencyDemo(kind);
        }
        catch (Exception ex)
        {
            // La demo es cosmética: si algo falla se apaga y la prueba sigue igual.
            _logging.LogWarning($"OverclockUSB: demo de latencia desactivada: {ex.Message}");
            HideLatencyDemo();
        }
    }

    private void HideLatencyDemo()
    {
        _latencyDemoTimer?.Stop();
        _latencyDemoTimer = null;
        // Resumen del teclado en pantalla: se registra aunque no se haya encendido NADA, que es
        // justo el caso que hay que poder diagnosticar desde el log (cuenta, no guarda qué tecla).
        if (_demoCaps.Count > 0)
            _logging.LogDebug($"Test de latencia: teclado en pantalla — pulsaciones leídas {_demoKeysSeen}, encendidas {_demoKeysLit}, descartadas por dispositivo {_demoKeysFiltered} (capturadas por el medidor: {_latencyMonitor.KeysCaptured}).");

        _demoOrbit = null;
        _demoCaps.Clear();
        _demoCapsByVKey.Clear();
        _demoKeyDeviceIds.Clear();
        _demoWaves.Clear();
        _demoStage = null;
        LatencyDemoStage.Content = null;
        LatencyDemoHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>Cuánto queda encendida una tecla después de apretarla (ms).</summary>
    private const int DemoKeyLitMs = 320;

    /// <summary>
    /// Teclado en pantalla del onboarding: la distribución real (números, QWERTY, ASDF, ZXCV y la
    /// barra espaciadora) y cada tecla se ENCIENDE con la que se apretó de verdad durante la prueba.
    /// Las pulsaciones llegan por Raw Input, así que se prenden aunque la ventana no tenga el foco.
    ///
    /// El ancho de tecla sale del alto/ancho real del recuadro, así en ventanas angostas el teclado
    /// se achica en vez de salirse, y las filas van escalonadas como las de un teclado de verdad
    /// (cada una centrada en el escenario).
    /// </summary>
    private void BuildDemoKeyboard(double stageW, double stageH)
    {
        const double gap = 6;

        // VKey de Windows: letras y dígitos usan su ASCII en mayúscula ('A' = 0x41 = VK_A,
        // '1' = 0x31 = VK_1) y la barra espaciadora es 0x20. Prohibida aparte: las teclas que no
        // están dibujadas (Enter, flechas, F1…) no encienden nada, y el dibujo no promete más que
        // lo que muestra.
        static (string Label, int VKey, double Units)[] Keys(string chars) =>
            chars.Select(c => (Label: c.ToString(), VKey: (int)c, Units: 1.0)).ToArray();

        var rows = new List<(string Label, int VKey, double Units)[]>
        {
            Keys("1234567890"),
            Keys("QWERTYUIOP"),
            Keys("ASDFGHJKL"),
            Keys("ZXCVBNM"),
            new[] { (Label: "", VKey: 0x20, Units: 5.0) }, // barra espaciadora: bien ancha
        };

        // La fila más larga son diez teclas: de ahí sale el ancho de UNA tecla.
        double unit = Math.Clamp((stageW - 9 * gap) / 10.0, 22, 46);
        double capH = Math.Round(unit * 0.75);
        double y = (stageH - (rows.Count * capH + (rows.Count - 1) * gap)) / 2;
        // Se rehace desde cero: sin esto las teclas de la corrida anterior quedaban en la lista
        // apuntando a bordes ya retirados (y el mapa podía conservar entradas viejas).
        _demoCaps.Clear();
        _demoCapsByVKey.Clear();
        _demoKeysSeen = 0;
        _demoKeysLit = 0;
        _demoKeysFiltered = 0;

        foreach (var row in rows)
        {
            double rowWidth = row.Sum(k => k.Units) * unit + (row.Length - 1) * gap;
            double x = (stageW - rowWidth) / 2;
            foreach (var key in row)
            {
                double capW = key.Units * unit;
                var label = new TextBlock
                {
                    Text = key.Label,
                    FontSize = Math.Max(10, Math.Round(capH * 0.44)),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var cap = new Border
                {
                    Width = capW,
                    Height = capH,
                    CornerRadius = new CornerRadius(5),
                    BorderThickness = new Thickness(1),
                    BorderBrush = ThemeBrushes.Get("SensorGridLineBrush"),
                    Background = ThemeBrushes.Get("SensorGroupFillBrush"),
                    Child = label
                };
                Canvas.SetLeft(cap, x);
                Canvas.SetTop(cap, y);
                _demoStage!.Children.Add(cap);

                var entry = new DemoKeyCap { Cap = cap, Label = label, VKey = key.VKey };
                _demoCaps.Add(entry);
                _demoCapsByVKey[key.VKey] = entry;
                x += capW + gap;
            }
            y += capH + gap;
        }
    }

    /// <summary>Handles de Raw Input del teclado ELEGIDO (pueden ser varios: un receptor expone más
    /// de una colección HID). Se resuelven una vez por prueba, no en cada tick.</summary>
    private HashSet<long> DemoSelectedDeviceIds()
    {
        var ids = new HashSet<long>();
        try
        {
            string? key = _selectedLatencyKey;
            if (string.IsNullOrEmpty(key) || IsAudioKey(key)) return ids;
            foreach (var device in _latencyMonitor.GetKnownDevices())
                if (string.Equals(device.Handle, key, StringComparison.OrdinalIgnoreCase)) ids.Add(device.DeviceId);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: no se pudieron resolver los dispositivos del onboarding visual: {ex.Message}");
        }
        return ids;
    }

    /// <summary>
    /// Cuadrícula del recuadro del onboarding visual (mismo lenguaje que la grilla del monitor
    /// de sensores): líneas cada 16 px, dibujadas al tamaño REAL del recuadro. Se rehace si
    /// cambia el tamaño; es cosmético, así que nunca debe romper el test.
    /// </summary>
    private void BuildDemoGrid()
    {
        try
        {
            if (LatencyDemoGridCanvas is null) return;
            LatencyDemoGridCanvas.Children.Clear();

            double w = LatencyDemoBackdrop.ActualWidth, h = LatencyDemoBackdrop.ActualHeight;
            if (w < 2 || h < 2) return;

            const double step = 16;
            var line = ThemeBrushes.Get("SensorGridLineBrush");
            for (double x = step; x < w; x += step)
            {
                var vertical = new Rectangle { Width = 1, Height = h, Fill = line, Opacity = 0.5 };
                Canvas.SetLeft(vertical, x);
                LatencyDemoGridCanvas.Children.Add(vertical);
            }
            for (double y = step; y < h; y += step)
            {
                var horizontal = new Rectangle { Width = w, Height = 1, Fill = line, Opacity = 0.5 };
                Canvas.SetTop(horizontal, y);
                LatencyDemoGridCanvas.Children.Add(horizontal);
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: no se pudo dibujar la cuadrícula del onboarding: {ex.Message}");
        }
    }

    /// <summary>Retira el trazo en vivo: apaga el reloj, borra las barras y oculta el área.
    /// Se usa al cancelar (el gráfico a medias no es de ninguna medición) y al cerrar una
    /// corrida que no dejó resultado.</summary>
    private void ClearLatencyLive()
    {
        StopLatencyLive();
        foreach (var bar in _liveBars) bar.Height = 0;
        LatencyLiveArea.Visibility = Visibility.Collapsed;
    }

    /// <summary>Monta el gesto del tipo elegido y lo anima con un timer (la animación es
    /// posicional: un storyboard no puede mover elementos sin depender del hilo de UI).</summary>
    /// <summary>Agrega una forma al escenario del onboarding en una posición absoluta.</summary>
    private void AddDemoShape(FrameworkElement shape, double left, double top)
    {
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
        _demoStage!.Children.Add(shape);
    }

    private void BuildLatencyDemo(LatencyDemoKind kind)
    {
        // El recuadro mide 264 px de alto: el escenario se dibuja en la parte central con margen,
        // así el gesto no queda pegado a los bordes. El teclado en pantalla necesita más ancho (son
        // diez teclas por fila) y lo toma del ancho REAL del recuadro, así en ventanas angostas se
        // achica en vez de salirse.
        bool keyboard = kind == LatencyDemoKind.Keyboard;
        double stageW = keyboard ? Math.Clamp(LatencyDemoBackdrop.ActualWidth - 40, 300, 540) : 380;
        double stageH = keyboard ? 200 : 176;
        _demoStage = new Canvas { Width = stageW, Height = stageH, HorizontalAlignment = HorizontalAlignment.Center };
        _latencyDemoTick = 0;
        // El orbe se rehace por corrida: si quedara el de una corrida anterior, el tick seguiría
        // moviendo un elemento ya retirado del escenario (con los radios viejos).
        _demoOrbit = null;
        _demoWaves.Clear();

        if (kind == LatencyDemoKind.Mouse)
        {
            // MOUSE: un óvalo ancho y punteado marca la zona del gesto, y lo recorre el ÍCONO del
            // mouse —el mismo glifo de la grilla de dispositivos—, no un punto anónimo. La guía va un
            // poco MÁS GRANDE que la órbita, así el ícono viaja por dentro y no le pisa el trazo.
            _demoOrbitRadiusX = 150;
            _demoOrbitRadiusY = 58;
            double guideRx = _demoOrbitRadiusX + 26, guideRy = _demoOrbitRadiusY + 26;
            AddDemoShape(new Ellipse
            {
                Width = guideRx * 2,
                Height = guideRy * 2,
                Stroke = ThemeBrushes.Get("SecondaryTextBrush"),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Opacity = 0.55
            }, (stageW - guideRx * 2) / 2, (stageH - guideRy * 2) / 2);

            _demoOrbitSize = 24;
            _demoOrbit = new FontIcon
            {
                Glyph = GlyphFor(InputDeviceKind.Mouse),
                FontSize = 22,
                Width = _demoOrbitSize,
                Height = _demoOrbitSize,
                Foreground = ThemeBrushes.Get("AccentBrush")
            };
            _demoStage.Children.Add(_demoOrbit);
        }
        else if (kind == LatencyDemoKind.Gamepad)
        {
            // MANDO: el STICK visto desde arriba. El círculo grande es el TOPE del recorrido, el punto
            // del centro es la posición neutra y el cabezal gira pegado al tope — que es exactamente
            // lo que hacés al girar el stick. Antes esto eran dos círculos concéntricos con un punto
            // chico dando vueltas por el medio: no representaba ninguna parte de un mando, y encima
            // hacía falta un texto que explicara el gesto.
            const double gate = 46;
            _demoOrbitRadiusX = 30;
            _demoOrbitRadiusY = 30;
            AddDemoShape(new Ellipse
            {
                Width = gate * 2,
                Height = gate * 2,
                Stroke = ThemeBrushes.Get("SecondaryTextBrush"),
                StrokeThickness = 1,
                Opacity = 0.55
            }, (stageW - gate * 2) / 2, (stageH - gate * 2) / 2);
            AddDemoShape(new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = ThemeBrushes.Get("SecondaryTextBrush"),
                Opacity = 0.7
            }, stageW / 2 - 2, stageH / 2 - 2);

            _demoOrbitSize = 22;
            _demoOrbit = new Ellipse
            {
                Width = _demoOrbitSize,
                Height = _demoOrbitSize,
                Fill = ThemeBrushes.Get("AccentBrush"),
                Opacity = 0.9
            };
            _demoStage.Children.Add(_demoOrbit);
        }
        else if (kind == LatencyDemoKind.Keyboard)
        {
            BuildDemoKeyboard(stageW, stageH);
        }
        else
        {
            // Audio: tres ondas que respiran al ritmo del motor.
            for (int i = 0; i < 3; i++)
            {
                var wave = new Rectangle
                {
                    Width = 6,
                    RadiusX = 3,
                    RadiusY = 3,
                    Height = 18,
                    Fill = ThemeBrushes.Get("AccentBrush")
                };
                Canvas.SetLeft(wave, (stageW - 38) / 2 + i * 16);
                _demoWaves.Add(wave);
                _demoStage.Children.Add(wave);
            }
        }

        LatencyDemoHost.Visibility = Visibility.Visible;
        LatencyDemoStage.Content = _demoStage;
        _latencyDemoTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _latencyDemoTimer.Tick -= LatencyDemoTick;
        _latencyDemoTimer.Tick += LatencyDemoTick;
        _latencyDemoTimer.Start();
    }

    private void LatencyDemoTick(object? sender, object e)
    {
        if (_demoStage == null) { _latencyDemoTimer?.Stop(); return; }
        _latencyDemoTick++;
        double cx = _demoStage.Width / 2, cy = _demoStage.Height / 2;

        if (_demoOrbit != null)
        {
            // Órbita: el orbe recorre la elipse a paso constante (el ángulo es lineal, así que la
            // vuelta dura siempre lo mismo y el gesto no "acelera" en los costados).
            double angle = _latencyDemoTick * 0.09;
            double x = cx + Math.Cos(angle) * _demoOrbitRadiusX;
            double y = cy + Math.Sin(angle) * _demoOrbitRadiusY;
            Canvas.SetLeft(_demoOrbit, x - _demoOrbitSize / 2);
            Canvas.SetTop(_demoOrbit, y - _demoOrbitSize / 2);
        }

        if (_demoCaps.Count > 0)
        {
            // Las teclas se encienden con lo que se APRIETA de verdad (Raw Input), no con un ciclo
            // automático de color: cada una queda prendida un instante y se apaga sola.
            long now = Environment.TickCount64;
            foreach (var (vkey, deviceId) in _latencyMonitor.DrainKeyPresses())
            {
                _demoKeysSeen++;
                if (!_demoCapsByVKey.TryGetValue(vkey, out var pressed)) continue;
                // Si el test mide un teclado concreto, solo encienden SUS pulsaciones (el informe
                // trae el dispositivo). Sin dispositivo identificado todavía se acepta cualquiera,
                // para que el dibujo no quede muerto por un dato que aún no llegó.
                // Sin dispositivo (handle 0) el informe NO se puede atribuir a ningún aparato, así
                // que no puede ser "de otro teclado": descartarlo solo dejaba el dibujo muerto.
                if (deviceId != 0 && _demoKeyDeviceIds.Count > 0 && !_demoKeyDeviceIds.Contains(deviceId))
                {
                    _demoKeysFiltered++;
                    continue;
                }
                _demoKeysLit++;
                pressed.LitUntilMs = now + DemoKeyLitMs;
            }

            foreach (var cap in _demoCaps)
            {
                bool lit = cap.LitUntilMs > now;
                if (lit == cap.Lit) continue; // nada que repintar
                cap.Lit = lit;
                cap.Cap.Background = ThemeBrushes.Get(lit ? "AccentBrush" : "SensorGroupFillBrush");
                cap.Cap.BorderBrush = ThemeBrushes.Get(lit ? "AccentBrush" : "SensorGridLineBrush");
                cap.Label.Foreground = ThemeBrushes.Get(lit ? "AccentForegroundBrush" : "SecondaryTextBrush");
            }
        }

        if (_demoWaves.Count > 0)
        {
            for (int i = 0; i < _demoWaves.Count; i++)
            {
                double phase = _latencyDemoTick * 0.12 + i * 0.9;
                double h = 16 + Math.Abs(Math.Sin(phase)) * 40;
                _demoWaves[i].Height = h;
                Canvas.SetTop(_demoWaves[i], (_demoStage.Height - h) / 2);
            }
        }
    }

    private void SetLatencyTestStatus(string text)
    {
        LatencyTestStatusText.Text = text;
        LatencyTestStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Textos fijos de la card (título y etiquetas) que tienen que seguir el idioma elegido: el
    /// marcado los trae en español como valor de diseño.
    /// </summary>
    private void SetLatencyCardTexts()
    {
        LatencyTestTitle.Text = I18n.T("Test de latencia");
        LatencyLiveCaptionText.Text = I18n.T("En vivo");
        LatencyLiveMinLabel.Text = I18n.T("Mín");
        LatencyLiveSamplesLabel.Text = I18n.T("Muestras");
        SetLatencyTestRunningUi(_latencyTestRunning);
        UpdateLatencyHint();
    }

    /// <summary>Muestra la última prueba GUARDADA del dispositivo elegido (sin comparación:
    /// contra qué compararla es la corrida anterior a esa, y ya no la tenemos).</summary>
    private void ShowStoredLatencyResult()
    {
        LatencyTestResultsHost.Children.Clear();
        _shownResult = null;
        _shownPrevious = null;

        if (string.IsNullOrEmpty(_selectedLatencyKey)) return;

        // La última corrida de audio se muestra aparte: su clave ("audio:<id>") no existe en
        // el espacio VID/PID del test de periféricos.
        if (IsAudioKey(_selectedLatencyKey))
        {
            if (_lastAudioRun is { } audioRun && string.Equals(_selectedLatencyKey["audio:".Length..], audioRun.Endpoint.Id, StringComparison.OrdinalIgnoreCase))
            {
                SetLatencyTestStatus($"{I18n.T("Última prueba")}: {audioRun.Timestamp.ToLocalTime():g}");
                RenderAudioLatencyResult(audioRun);
            }
            else
            {
                SetLatencyTestStatus("");
            }
            return;
        }

        var stored = _lastRun;
        var result = stored?.Results.FirstOrDefault(r => string.Equals(r.Handle, _selectedLatencyKey, StringComparison.OrdinalIgnoreCase));
        if (result == null)
        {
            SetLatencyTestStatus("");
            return;
        }

        SetLatencyTestStatus($"{I18n.T("Última prueba")}: {result.Timestamp.ToLocalTime():g}");
        RenderLatencyResult(result, null);
    }

    private void RenderAudioLatencyResult(AudioLatencyRun run)
    {
        LatencyTestResultsHost.Children.Clear();
        var (row, refs) = BuildLatencyTestRow();
        LatencyTestResultsHost.Children.Add(row);

        refs.Icon.Glyph = run.Endpoint.IsInput ? "\uE720" : "\uE7F6";
        refs.Name.Text = run.Endpoint.Name;
        var (vText, vBrush) = run.RoundTrip is { } rt
            ? rt.Verdict switch
            {
                AudioRoundTripVerdict.Excellent => (I18n.T("Excelente"), ThemeBrushes.Get("MetricTempBrush")),
                AudioRoundTripVerdict.Good => (I18n.T("Aceptable"), ThemeBrushes.Get("MetricPowerBrush")),
                AudioRoundTripVerdict.High => (I18n.T("Latencia alta"), ThemeBrushes.Get("ErrorBrush")),
                AudioRoundTripVerdict.InsufficientData => (I18n.T("Datos insuficientes"), ThemeBrushes.Get("MutedBrush")),
                _ => (I18n.T("Sin retorno"), ThemeBrushes.Get("MutedBrush"))
            }
            : (I18n.T("Datos insuficientes"), ThemeBrushes.Get("MutedBrush"));
        refs.VerdictText.Text = vText;
        refs.VerdictText.Foreground = vBrush;
        refs.Verdict.BorderBrush = vBrush;

        // Columnas del audio, con nombres propios: la IDA Y VUELTA es el número de la latencia y su
        // etiqueta dice en qué MODO se midió. Un loopback interno NO es la latencia del dispositivo, y
        // sin decirlo parecía que los parlantes de un monitor y unos auriculares inalámbricos medían
        // lo mismo — que es exactamente lo que miden: el motor compartido de Windows.
        bool rtSoftware = run.RoundTrip?.Mode == AudioRoundTripMode.Software;
        refs.SetLabels(rtSoftware ? I18n.T("Ida y vuelta (software)") : I18n.T("Ida y vuelta (física)"),
            I18n.T("Mejor"), I18n.T("Peor"), I18n.T("Motor"), I18n.T("Muestras"));
        refs.Avg.Text = run.RoundTrip is { } rtr ? $"{rtr.MedianMs:0.00} ms" : "—";
        // Mejor y Peor son los extremos de la serie de intentos: antes el MÍNIMO viajaba bajo la
        // etiqueta "P99", que nombra lo peor, no lo mejor.
        refs.Max.Text = run.RoundTrip is { } rtm ? $"{rtm.MinMs:0.00} ms" : "—";
        refs.P99.Text = run.RoundTrip is { } rtp ? $"{rtp.MaxMs:0.00} ms" : "—";
        refs.Hz.Text = run.Engine is { } eng ? $"{1000.0 / Math.Max(eng.PeriodMs, 0.001):0} Hz" : "—";
        refs.Gaps.Text = run.Engine?.Samples.ToString() ?? "—";

        var notes = new List<string>();
        if (run.RoundTrip is { } rtd)
        {
            // El MODO decide el texto: en loopback interno no hay convertidores del dispositivo en el
            // medio, así que afirmar "es la latencia que sentís" sería falso — y es justo el caso en el
            // que dos dispositivos distintos dan el mismo número.
            notes.Add(rtd.Mode == AudioRoundTripMode.Software
                ? I18n.T("Ida y vuelta MEDIDA POR LOOPBACK INTERNO: {0:0.00} ms de mediana sobre {1} intentos (el primer impulso se descarta). Se captura el mismo endpoint de salida, así que es el camino de SOFTWARE (motor + búferes) y NO incluye el DAC, el amplificador, el inalámbrico ni el parlante: no es la latencia que sentís. Para medirla de verdad hace falta una vuelta física: cable de salida a entrada, o un micrófono frente al parlante.", rtd.MedianMs, rtd.DetectedAttempts)
                : I18n.T("Ida y vuelta FÍSICA: {0:0.00} ms de mediana sobre {1} intentos (el primer impulso se descarta). El sonido salió por la salida y volvió por una entrada, así que incluye DAC, amplificador, aire y ADC: es la latencia que sentís al jugar o en una llamada.", rtd.MedianMs, rtd.DetectedAttempts));
            if (rtd.Verdict == AudioRoundTripVerdict.Good) notes.Add(I18n.T("La ida y vuelta está en el rango típico del hardware de audio: no hay nada que apurar acá."));
        }
        else
        {
            notes.Add(I18n.T("La ida y vuelta no pudo detectar el retorno: subí el volumen del dispositivo y volvé a medir."));
        }
        if (run.Engine is { } en)
        {
            notes.Add(I18n.T("Motor (WASAPI): mediana {0:0.00} ms · período {1:0.00} ms · búfer {2:0.0} ms{3}.", en.MedianMs, en.PeriodMs, en.BufferMs, en.EstimatedFromBuffer ? " (estimada del búfer: el driver no reporta latencia)" : ""));
        }
        refs.Compare.Text = string.Join(" ", notes);
    }

    private void RenderLatencyResult(InputLatencyTestResult result, InputLatencyTestResult? previous)
    {
        _shownResult = result;
        _shownPrevious = previous;
        LatencyTestResultsHost.Children.Clear();
        var (row, refs) = BuildLatencyTestRow();
        LatencyTestResultsHost.Children.Add(row);
        UpdateLatencyTestRow(refs, result, previous);
    }

    /// <summary>Glifo del tipo de dispositivo.</summary>
    private static string GlyphFor(InputDeviceKind kind) => kind switch
    {
        // Los MISMOS glyphs que la grilla de dispositivos: si acá se usa otro, el mouse
        // queda sin ícono (el E7BF no existe en Segoe Fluent).
        InputDeviceKind.Mouse => "\uE962",
        InputDeviceKind.Keyboard => "\uE765",
        _ => "\uE7FC"
    };

    private sealed class LatencyTestRowRefs
    {
        public FontIcon Icon = null!;
        public TextBlock Name = null!;
        public Border Verdict = null!;
        public TextBlock VerdictText = null!;
        public TextBlock Hz = null!;
        public TextBlock Avg = null!;
        public TextBlock Max = null!;
        public TextBlock P99 = null!;
        public TextBlock Gaps = null!;
        public TextBlock Compare = null!;

        // Etiquetas de las columnas: el resultado del BUS mide otras magnitudes y necesita nombrarlas
        // (Piso, Mín, Host p99, Transferencias). Con la etiqueta prestada, una columna que no aplica
        // queda mentirosa o muerta.
        public TextBlock LabelAvg = null!;
        public TextBlock LabelMax = null!;
        public TextBlock LabelP99 = null!;
        public TextBlock LabelHz = null!;
        public TextBlock LabelGaps = null!;

        /// <summary>Renombra las columnas de la fila: cada medición tiene sus propias magnitudes.</summary>
        public void SetLabels(string avg, string max, string p99, string hz, string gaps)
        {
            LabelAvg.Text = avg.ToUpperInvariant();
            LabelMax.Text = max.ToUpperInvariant();
            LabelP99.Text = p99.ToUpperInvariant();
            LabelHz.Text = hz.ToUpperInvariant();
            LabelGaps.Text = gaps.ToUpperInvariant();
        }
    }

    private static (StackPanel Row, LatencyTestRowRefs Refs) BuildLatencyTestRow()
    {
        var refs = new LatencyTestRowRefs();
        var row = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Background = (Brush)Application.Current.Resources["SensorGroupFillBrush"],
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(6)
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        refs.Icon = new FontIcon { FontSize = 13, Foreground = (Brush)Application.Current.Resources["AccentBrush"] };
        refs.Name = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        refs.VerdictText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
        refs.Verdict = new Border
        {
            Padding = new Thickness(7, 2, 7, 2),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["ChipBackgroundBrush"],
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = refs.VerdictText,
            // Oculto por defecto: el resultado ya no muestra la calificación
            // (Excelente/Aceptable/Inestable). El test del bus sí lo enciende.
            Visibility = Visibility.Collapsed
        };
        header.Children.Add(refs.Icon);
        header.Children.Add(refs.Name);
        header.Children.Add(refs.Verdict);
        row.Children.Add(header);

        var metrics = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        (string Label, TextBlock Caption, TextBlock Target)[] cols =
        {
            ("AVG", refs.LabelAvg = new TextBlock(), refs.Avg = new TextBlock()),
            ("MAX", refs.LabelMax = new TextBlock(), refs.Max = new TextBlock()),
            ("P99", refs.LabelP99 = new TextBlock(), refs.P99 = new TextBlock()),
            ("HZ",  refs.LabelHz  = new TextBlock(), refs.Hz  = new TextBlock()),
            (I18n.T("Huecos"), refs.LabelGaps = new TextBlock(), refs.Gaps = new TextBlock()),
        };
        foreach (var (label, caption, target) in cols)
        {
            var col = new StackPanel { Spacing = 1 };
            caption.FontSize = 9;
            caption.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            caption.Text = label.ToUpperInvariant();
            col.Children.Add(caption);
            target.FontSize = 13;
            target.FontWeight = FontWeights.SemiBold;
            col.Children.Add(target);
            metrics.Children.Add(col);
        }
        row.Children.Add(metrics);

        refs.Compare = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["MutedBrush"],
            TextWrapping = TextWrapping.Wrap
        };
        row.Children.Add(refs.Compare);

        return (row, refs);
    }

    private static void UpdateLatencyTestRow(LatencyTestRowRefs refs, InputLatencyTestResult result, InputLatencyTestResult? previous)
    {
        refs.Icon.Glyph = GlyphFor(result.Kind);
        refs.Name.Text = result.Name;

        var (verdictText, verdictBrush) = VerdictOf(result.Verdict);
        refs.VerdictText.Text = verdictText;
        refs.VerdictText.Foreground = verdictBrush;
        refs.Verdict.BorderBrush = verdictBrush;

        // La flecha compara con la corrida anterior del mismo periférico (más Hz = mejor).
        string arrow = previous == null ? "" : result.Hz >= previous.Hz ? " ▲" : " ▼";
        refs.Hz.Text = $"{result.Hz:0} Hz{arrow}";
        refs.Avg.Text = $"{result.AvgMs:0.00} ms";
        refs.Max.Text = $"{result.MaxMs:0.00} ms";
        refs.P99.Text = $"{result.P99Ms:0.00} ms";
        refs.Gaps.Text = $"{result.GapPercent:0.##} %";

        refs.Compare.Text = previous == null
            ? I18n.T("Sin corridas previas para comparar.")
            : $"{I18n.T("Corrida anterior")}: {previous.Hz:0} Hz · {previous.AvgMs:0.00} ms";
    }

    /// <summary>Texto y color del veredicto (semáforo: verde, ámbar, rojo, gris).</summary>
    private static (string Text, Brush Brush) VerdictOf(InputLatencyVerdict verdict) => verdict switch
    {
        InputLatencyVerdict.Excellent => (I18n.T("Excelente"), (Brush)Application.Current.Resources["MetricTempBrush"]),
        InputLatencyVerdict.Acceptable => (I18n.T("Aceptable"), (Brush)Application.Current.Resources["MetricPowerBrush"]),
        InputLatencyVerdict.Unstable => (I18n.T("Inestable"), (Brush)Application.Current.Resources["ErrorBrush"]),
        InputLatencyVerdict.NoContinuousStream => (I18n.T("Sin flujo continuo"), (Brush)Application.Current.Resources["MutedBrush"]),
        _ => (I18n.T("Datos insuficientes"), (Brush)Application.Current.Resources["MutedBrush"])
    };

    /// <summary>
    /// Pausa de bandeja (IBackgroundPausable): una prueba en curso se cancela — mide con el
    /// usuario delante — y el monitor de Raw Input queda como está: es pasivo (solo recibe
    /// copias de los informes) y es lo que permite poblar la lista de dispositivos.
    /// </summary>
    public void PauseBackgroundTimers() => CancelLatencyTest();

    /// <summary>Reanudación desde bandeja: nada que reanudar (la prueba la pide el usuario).</summary>
    public void ResumeBackgroundTimers() { }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        // El pump de Raw Input alimenta la actividad de la lista de dispositivos y es lo que captura
        // las teclas del onboarding visual: se abre al entrar a la página, no en el primer test.
        _latencyMonitor.Start();
        InitTable();
        BuildHeader();
        SetLatencyCardTexts();
        UpdateUsbGuardBanner();
        RefreshComponentUi(refreshDevices: true);
    }

    /// <summary>
    /// Aviso de Integridad de memoria (HVCI) en la propia sección donde importa.
    ///
    /// Por qué acá y no solo en Sistema: el overclock real depende de que el filtro pueda parchear
    /// la pila USB, y con esa protección activa Windows solo carga la variante NoPatch. Es el
    /// propio autor del driver quien lo exige (README de hidusbf, "Warning 2": "you SHALL disable
    /// Memory Integrity to load patching versions of HIDUSBF driver"), y desde 2024 los binarios
    /// están firmados por atestación de Microsoft, así que el arranque seguro y la firma de
    /// pruebas ya no hacen falta. El aviso no se muestra si la protección no está puesta.
    /// </summary>
    private void UpdateUsbGuardBanner()
    {
        try
        {
            var guard = _usb.GetGuardStatus();
            if (!guard.HvciRunning && !guard.HvciConfigured)
            {
                GuardBanner.Visibility = Visibility.Collapsed;
                return;
            }

            // Configurada pero todavía no corriendo = se aplica al reiniciar: se aclara en el título
            // con la etiqueta de estado que ya existe ("Configurado"), sin claves nuevas.
            GuardTitleText.Text = guard.HvciRunning
                ? I18n.T("Integridad de memoria activa")
                : $"{I18n.T("Integridad de memoria activa")} · {I18n.T("Configurado")}";
    // Lo que la protección CUESTA depende de la variante instalada, y son dos casos distintos:
    //   NoPatch → carga (es la variante que respeta HVCI): solo se pierde el overclock >1000 Hz
    //             en dispositivos no high-speed, porque ahí hace falta parchear la pila USB.
    //   patching → no carga en absoluto: sin filtro no hay overclock, y el arreglo es que la app
    //             instale NoPatch (reinstalar el componente o volver a aplicar la tasa).
            // TEXTO PARA EL USUARIO FINAL: sin "filtro", sin "variante", sin "pila USB", sin firmas.
            // El detalle técnico (Warning 2 del README, variantes, firma por atestación) vive en los
            // comentarios de arriba y en el log; acá sólo qué le pasa a sus dispositivos y qué hacer.
            bool patchingInstalled = guard.InstalledVariant is "1khz" or "2khz-4khz" or "4khz-8khz";
            GuardBodyText.Text = I18n.T(patchingInstalled
                    ? "Windows está bloqueando el driver que ajusta la frecuencia, así que tus dispositivos están funcionando a su velocidad normal. Se arregla desde acá: instalá el componente otra vez o aplicá la frecuencia de nuevo, y la app pone la versión que Windows sí acepta."
                    : "Windows tiene activada la Integridad de memoria, una protección contra programas peligrosos, y con ella el ajuste queda limitado: podés bajar la frecuencia, pero no subirla por encima de 1000 Hz.")
                + " "
                + I18n.T("¿Querés pasar de 1000 Hz? Usá el botón de al lado para abrir Seguridad de Windows, desactivá Integridad de memoria en Seguridad del dispositivo → Aislamiento del núcleo y reiniciá la PC. Es una protección real: sirve para que ningún programa desconocido pueda meterse dentro del sistema. Desactivarla es tu decisión.");

            GuardVariantText.Text = VariantLabel(guard);
            GuardVariantChip.Visibility = GuardVariantText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            GuardVariantText.Foreground = patchingInstalled ? Feedback.WarningBrush : Feedback.AccentBrush;
            GuardOpenButtonText.Text = I18n.T("Abrir Seguridad de Windows");
            GuardBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: no se pudo actualizar el aviso de Integridad de memoria: {ex.Message}");
            GuardBanner.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Etiqueta de la variante del filtro instalada + techo que puede forzar ("" si no se pudo
    /// identificar). Los nombres son del paquete oficial y los Hz una unidad: no se traducen.
    /// </summary>
    private static string VariantLabel(UsbOverclockGuardStatus guard)
    {
        string name = guard.InstalledVariant switch
        {
            "nopatch" => "NoPatch",
            "1khz" => "1 kHz",
            "2khz-4khz" => "2-4 kHz",
            "4khz-8khz" => "4-8 kHz",
            _ => ""
        };
        return name.Length == 0 || guard.CeilingHz <= 0 ? "" : $"{name} · {guard.CeilingHz} Hz";
    }

    /// <summary>Abre la página de Integridad de memoria de Seguridad de Windows (deep link oficial).</summary>
    private void GuardOpenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("windowsdefender://coreisolation") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: no se pudo abrir Seguridad de Windows: {ex.Message}");
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

    // La página está cacheada (NavigationCacheMode.Enabled): Loaded solo dispara
    // una vez, así que el chequeo de "¿está instalado el componente?" se repite
    // acá, en cada navegación. Si mientras se estaba en otra sección se borró el
    // caché o se desinstaló el componente, al volver aparece la puerta de entrada
    // en lugar de una grilla huérfana.
        if (_loaded)
            RefreshComponentUi(refreshDevices: true);
    }

    private void OnLanguageChanged()
    {
        if (!_loaded) return;

        // Card del test: título, etiquetas, pista y estado siguen el idioma elegido.
        SetLatencyCardTexts();
        UpdateUsbGuardBanner();
        RefreshLatencyTestDevices();

        if (_componentReady)
        {
    // Las filas se construyen en código con textos traducidos: re-render desde
    // la caché al cambiar de idioma (sin re-escannear el bus).
            RebuildRows();
        }
        else
        {
    // Gate: limpiar los textos dinámicos (los estáticos los re-traduce el walker).
            SetGateIdle();
            GateErrorText.Visibility = Visibility.Collapsed;
            if (HowItWorksDetail.Visibility == Visibility.Visible)
                HowItWorksDetail.Text = I18n.T(HowItWorksKey);
        }
    }

    // ===================== Componente: gate vs grilla =====================

    /// <summary>
    /// Decide qué se muestra: la puerta de entrada (descargar componentes) si el
    /// motor aún no está listo, o la grilla de dispositivos si ya lo está.
    /// </summary>
    private void RefreshComponentUi(bool refreshDevices)
    {
        _componentReady = _usb.IsComponentReady();

        MainPanel.Visibility = _componentReady ? Visibility.Visible : Visibility.Collapsed;
        GatePanel.Visibility = _componentReady ? Visibility.Collapsed : Visibility.Visible;

        if (_componentReady)
        {
            if (refreshDevices && !_busy)
                _ = RefreshDevicesAsync(initial: true);
        }
    }

    /// <summary>Deshabilita/habilita los controles interactivos de ambos paneles.</summary>
    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
        RowsScroll.IsEnabled = !busy;
        DownloadComponentsButton.IsEnabled = !busy;
    }

    // ===================== Gate: estado del progreso =====================

    private void SetGateBusy(string message)
    {
        GateErrorText.Visibility = Visibility.Collapsed;
        GateStatusText.Text = message;
        GateProgressArea.Visibility = Visibility.Visible;
        GateProgressRing.IsActive = true;
    }

    private void SetGateIdle()
    {
        GateProgressRing.IsActive = false;
        GateProgressArea.Visibility = Visibility.Collapsed;
    }

    private void SetGateFailure(string message)
    {
        SetGateIdle();
        GateErrorText.Text = message;
        GateErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>Despliega/contrae la explicación de cómo funciona la sección (transparencia, sin jerga interna).</summary>
    private void HowItWorksLink_Click(object sender, RoutedEventArgs e)
    {
        bool expanded = HowItWorksDetail.Visibility == Visibility.Visible;
        if (!expanded)
        {
            HowItWorksDetail.Text = I18n.T(HowItWorksKey);
            HowItWorksDetail.Visibility = Visibility.Visible;
        }
        else
        {
            HowItWorksDetail.Visibility = Visibility.Collapsed;
        }
    }

    // ===================== Descarga / instalación automática =====================

    private async void DownloadComponentsButton_Click(object sender, RoutedEventArgs e)
        => await RunAutoInstallAsync();

    /// <summary>
    /// Flujo de un clic (solo desde la puerta de entrada): descarga el paquete
    /// oficial del componente desde su repositorio (HTTPS), extrae el driver,
    /// instala el servicio del sistema y recién entonces muestra la grilla. Si la
    /// descarga falla, ofrece la opción de elegir el archivo manualmente.
    /// </summary>
    private async Task RunAutoInstallAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            SetBusy(true);
            SetGateBusy(I18n.T("Descargando el componente del sistema..."));

            string? sys = await _usb.DownloadComponentAsync();
            if (string.IsNullOrEmpty(sys) || !File.Exists(sys))
            {
                SetGateFailure(I18n.T("No se pudo descargar el componente. Verificá tu conexión a internet e intentá de nuevo."));
                return;
            }

            SetGateBusy(I18n.T("Instalando el componente del sistema..."));
            bool ok = await _usb.InstallFilterServiceAsync(sys);
            if (!ok)
            {
                SetGateFailure(I18n.T("No se pudo instalar el componente. Ejecutá WinForge como administrador e intentá de nuevo."));
                return;
            }

            SetGateBusy(I18n.T("Componente instalado. Abriendo la sección..."));
            await Task.Delay(300);
        }
        catch (Exception ex)
        {
            _logging.LogError("OverclockUSB: error preparando los componentes", ex);
            SetGateFailure(I18n.T("Ocurrió un error al preparar los componentes. Intentalo de nuevo."));
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }

        if (_componentReady) return;
        RefreshComponentUi(refreshDevices: true);
        if (!_componentReady) SetGateIdle();
    }

    // ===================== Listado =====================

    private async Task RefreshDevicesAsync(bool initial = false)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            SetBusy(true);
            Feedback.Running(StatusText, I18n.T(initial ? "Buscando dispositivos USB..." : "Refrescando dispositivos..."), persistent: true);
            var devices = await _usb.GetDevicesAsync();
            _devices = devices;
            RebuildRows();
            // El desplegable del test de latencia se rearma junto con los dispositivos (y nunca
            // con el popup abierto): así está listo antes de que el usuario lo abra.
            RefreshLatencyTestDevices();

    // Si el motor dejó de estar operativo (p. ej. se borró el binario del filtro del
    // sistema o se desinstaló el servicio), volver a la puerta de entrada en
    // lugar de dejar una grilla que no puede aplicar cambios.
            if (!_usb.IsComponentReady())
            {
                RefreshComponentUi(refreshDevices: false);
                return;
            }

    // Diagnóstico visible: si hay filas pero el detalle (hijo/controladora)
    // llegó vacío, avisar — así distinguimos un problema real de datos de una
    // versión vieja del binario en ejecución.
            int childFilled = _devices.Count(d => !string.IsNullOrEmpty(d.ChildName));
            int hostFilled = _devices.Count(d => !string.IsNullOrEmpty(d.HostControllerName));
            if (_devices.Count > 0 && childFilled == 0 && hostFilled == 0)
            {
                string sample = _devices.First().InstanceId;
                _logging.LogWarning($"OverclockUSB: {_devices.Count} dispositivos sin hijo/controladora (ej: {sample})");
                Feedback.Error(StatusText,
                    I18n.T("Se listaron {0} dispositivos pero sin nombre hijo ni controladora (parece una versión vieja del programa).", _devices.Count),
                    persistent: true);
            }
            else if (_devices.Count > 0)
            {
                _logging.LogInfo($"OverclockUSB: {_devices.Count} dispositivos (hijos con nombre: {childFilled}, controladoras: {hostFilled})");
                Feedback.Set(StatusText, null);
            }
            else
            {
                Feedback.Set(StatusText, null);
            }
        }
        catch (Exception ex)
        {
            _logging.LogError("OverclockUSB: error listando dispositivos", ex);
            Feedback.Error(StatusText, I18n.T("No se pudieron listar los dispositivos USB."));
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        => await RefreshDevicesAsync();

    /// <summary>
    /// Función que muestra la fila: el mejor hijo del producto (prioridad clásica
    /// mouse &gt; teclado &gt; audio), con su icono al frente. Siempre un solo icono por
    /// fila — el del tipo detectado por clase de driver (mouhid/kbdhid, invariante al
    /// idioma del SO); las demás funciones del combo quedan en el tooltip.
    /// </summary>
    private (IReadOnlyList<string> Glyphs, string Name) MainFunctionFor(UsbPollingDevice device)
    {
        string name = !string.IsNullOrEmpty(device.ChildName)
            ? device.ChildName
            : !string.IsNullOrEmpty(device.ControllerName) ? device.ControllerName : "—";
        return (new[] { GlyphForKind(device.Kind) }, name);
    }

    /// <summary>Reconstruye las filas de la grilla desde la caché, con el orden elegido
    /// (columna de la cabecera). Conserva la selección previa (por instance id).</summary>
    private void RebuildRows()
    {
        string? keepId = _selectedDevice?.InstanceId;

        RowsPanel.Children.Clear();
        _rowUis.Clear();
        _selectedDevice = null;
        UpdateSelectedDevicePanel(null);

        var visible = new List<UsbPollingDevice>(_devices);

    // Orden por la columna elegida (clic en la cabecera), como en Procesos.
        if (_sortColumn >= 0 && visible.Count > 1)
        {
            visible.Sort((a, b) => CompareByColumn(a, b, _sortColumn));
            if (_sortDesc) visible.Reverse();
        }

        foreach (var device in visible)
        {
            var row = CreateDeviceRow(device);
            _rowUis.Add(row);
            RowsPanel.Children.Add(row.Outer);
        }

        EmptyStatePanel.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Text = I18n.T("No hay dispositivos USB compatibles (mouse, teclado, mando HID o adaptador Bluetooth).");

    // Restaurar la selección previa (si sigue visible).
        if (keepId != null) SetSelectedRow(keepId);
        UpdateScrollbarInsets();
    }

    /// <summary>Compara por columna. La columna principal (0) agrupa por tipo
    /// (mouse < teclado < audio < mando) y luego por nombre del mejor hijo.</summary>
    private int CompareByColumn(UsbPollingDevice a, UsbPollingDevice b, int col) => col switch
    {
        0 => CompareFunctionName(a, b),
        1 => string.Compare(a.HostControllerName, b.HostControllerName, StringComparison.OrdinalIgnoreCase),
        2 => a.DescriptorBInterval.CompareTo(b.DescriptorBInterval),
        _ => a.FilterOn.CompareTo(b.FilterOn)
    };

    private int CompareFunctionName(UsbPollingDevice a, UsbPollingDevice b)
    {
    // Se agrupa por tipo primero (mouse < teclado < mando < audio), así los
    // dispositivos del mismo tipo quedan juntos y el AUDIO queda al final de la
    // grilla, separado de los periféricos de entrada; dentro del mismo tipo, por nombre.
        int rank = KindRank(a.Kind).CompareTo(KindRank(b.Kind));
        if (rank != 0) return rank;
        return string.Compare(MainFunctionFor(a).Name, MainFunctionFor(b).Name, StringComparison.OrdinalIgnoreCase);
    }

    private static int KindRank(UsbDeviceKind kind) => kind switch
    {
        UsbDeviceKind.Mouse => 0,
        UsbDeviceKind.Keyboard => 1,
        UsbDeviceKind.Controller => 2,
        // El adaptador Bluetooth va después de los periféricos de entrada y antes del
        // audio: es la radio del equipo (el transporte), no un periférico. Con el valor
        // explícito se separa del 3 de los desconocidos y el orden deja de depender del azar.
        UsbDeviceKind.Bluetooth => 3,
        // El audio va último: son endpoints de sonido, no periféricos de entrada.
        UsbDeviceKind.Audio => 4,
        _ => 3
    };

    /// <summary>Glyph Segoe Fluent (monocromo, estilo del resto de la app) del tipo de
    /// dispositivo detectado: Mouse E962, Teclado E765, Audio E7F6 (Headphone),
    /// Mando E7FC (Game), adaptador Bluetooth EB77 (el "router con ondas" de los badges
    /// inalámbricos, para que un inalámbrico se lea igual en toda la página) y USB
    /// genérico E88E (el mismo de la cabecera de la página).</summary>
    private static string GlyphForKind(UsbDeviceKind kind) => kind switch
    {
        UsbDeviceKind.Mouse => "\uE962",
        UsbDeviceKind.Keyboard => "\uE765",
        UsbDeviceKind.Audio => "\uE7F6",
        UsbDeviceKind.Controller => "\uE7FC",
        UsbDeviceKind.Bluetooth => WirelessGlyph,
        _ => "\uE88E"
    };

    /// <summary>
    /// Celda principal de la fila: uno o dos FontIcons del tipo (monocromo, como el
    /// resto de la app) + nombre de la función con recorte. Los iconos quedan fijos a
    /// la izquierda y el nombre ocupa el resto de la columna (Grid Auto+*), así el
    /// texto largo termina en "…" sin empujar a los iconos. Con dos iconos (🖱+⌨) se
    /// marca que el producto es combo.
    /// </summary>
    private static Grid MainNameCell(IReadOnlyList<string> glyphs, string text, string? subtitle = null)
    {
        var cell = new Grid { ColumnSpacing = 8 };
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var glyph in glyphs)
        {
            icons.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 15,
                Foreground = ThemeBrushes.Get("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
    // Nombre de la función (línea principal) + nombre del producto USB debajo:
    // así un receptor combo (ej. el dongle Logitech con interfaz de teclado) se
    // distingue a simple vista de un teclado físico propio.
        var textCol = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        var textTb = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Left
        };
        textCol.Children.Add(textTb);
        if (!string.IsNullOrEmpty(subtitle) && !string.Equals(subtitle, text, StringComparison.OrdinalIgnoreCase))
        {
            textCol.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Foreground = (Brush)ThemeBrushes.Get("SecondaryTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Left,
                MaxLines = 1
            });
        }
        Grid.SetColumn(textCol, 1);
        cell.Children.Add(icons);
        cell.Children.Add(textCol);
        // Sin badge de conexión en la grilla: el estado cable/Bluetooth del dispositivo
        // es dato del desplegable del test de latencia, no una columna de esta tabla.
        return cell;
    }

    // ===================== Selección y acciones del dispositivo =====================

    private UsbPollingDevice? _selectedDevice;

    /// <summary>Selecciona la fila para mostrar su estado visual.</summary>
    private void SetSelectedRow(string? instanceId)
    {
        _selectedDevice = _devices.FirstOrDefault(d => d.InstanceId == instanceId);
        foreach (var ui in _rowUis)
            ApplySelectionVisual(ui, ui.Device.InstanceId == instanceId);
        UpdateSelectedDevicePanel(_selectedDevice);
    }

    private void UpdateSelectedDevicePanel(UsbPollingDevice? device)
    {
        if (device == null)
        {
            SelectedDevicePanel.Visibility = Visibility.Collapsed;
            return;
        }

        SelectedDevicePanel.Visibility = Visibility.Visible;
        SelectedDeviceName.Text = string.IsNullOrWhiteSpace(device.ControllerName)
            ? device.ChildName
            : device.ControllerName;
        SelectedDeviceType.Text = string.IsNullOrWhiteSpace(device.ChildName)
            ? device.Kind.ToString()
            : device.ChildName;
        SelectedDeviceNativeRate.Text = device.NativeHz > 0 ? $"{device.NativeHz} Hz" : "—";
        SelectedDeviceActiveRate.Text = device.ActiveHz.HasValue
            ? $"{device.ActiveHz.Value} Hz"
            : "Sin override";
        // El par "Filtro activo"/"Filtro inactivo" no existe como clave y quedaba en
        // español en todos los idiomas: se arma con claves que sí están ("Filtro" +
        // "Activo"/"Inactivo").
        SelectedDeviceFilterState.Text = $"{I18n.T("Filtro")}: {I18n.T(device.FilterOn ? "Activo" : "Inactivo")}";
        SelectedDeviceBus.Text = device.IsHighSpeed ? "High-Speed" : "Full/Low-Speed";
        SelectedDeviceInterval.Text = device.DescriptorBInterval > 0
            ? $"bInterval {device.DescriptorBInterval}"
            : "bInterval no disponible";
    }

    private void ApplySelectionVisual(DeviceRow ui, bool selected)
    {
        Brush brush = selected
            ? (ThemeBrushes.Get("AccentTintBrush") ?? Feedback.AccentBrush)
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ui.Container.Background = brush;
    }

    /// <summary>Clic simple: selecciona la fila sin abrir acciones destructivas.</summary>
    private void DeviceRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Grid { Tag: UsbPollingDevice device })
            SetSelectedRow(device.InstanceId);
    }

    /// <summary>Doble clic: abre el popup de tasa, aplicación y restauración.</summary>
    private async void DeviceRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is Grid { Tag: UsbPollingDevice device })
        {
            SetSelectedRow(device.InstanceId);
            await ShowRateDialogAsync(device);
        }
    }

    /// <summary>
    /// Construye la fila con la misma estructura que Gestión de Procesos: grilla
    /// interna «Cols» de ancho fijo (TotalWidth) con las celdas ubicadas según el
    /// orden visual actual (_colOrder) y separadores de 1 px entre columnas, envuelta
    /// en un Border con línea inferior (container) y en un contenedor «Outer» que
    /// recibe el clic (selección + popup de Hz).
    /// </summary>
    private DeviceRow CreateDeviceRow(UsbPollingDevice device)
    {
        var cols = new Grid { Width = TotalWidth, HorizontalAlignment = HorizontalAlignment.Left, Height = RowHeight };
        for (int p = 0; p < Columns.Length; p++)
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Scaled(_colOrder[p])) });

    // Textos por columna semántica (los datos de cada columna no cambian al moverla).

    // Columna principal: el mejor hijo del producto, con su icono al frente.
        var (glyphs, mainText) = MainFunctionFor(device);
    // Subtítulo solo en productos combo (receptores con varias funciones, ej.
    // dongle mouse+teclado): aclara a qué producto pertenece la función sin
    // lógica por fabricante. En dispositivos de función única es ruido.
        string? subtitle = device.Children.Count > 1 ? device.ControllerName : null;
        var main = MainNameCell(glyphs, mainText, subtitle);
        main.Margin = new Thickness(8, 0, 8, 0);
        string tooltip = device.Children.Count > 1
            ? I18n.T("Funciones del producto: {0}", string.Join(" · ", device.Children.Select(c => c.Name)))
            : device.ChildName;
        ToolTipService.SetToolTip(main, tooltip);

        string controllerText = string.IsNullOrEmpty(device.HostControllerName) ? "—" : device.HostControllerName;
        var controller = NewCell(controllerText);
        if (string.IsNullOrEmpty(device.HostControllerName))
            controller.Foreground = Feedback.MutedBrush;
        ToolTipService.SetToolTip(controller, string.IsNullOrEmpty(device.HostControllerName)
            ? I18n.T("No se pudo determinar la controladora de host USB.")
            : $"{device.HostControllerName}\n{device.HostControllerInstance}");

    // bInterval crudo del descriptor, igual que la columna bInterval de el filtro
    // (sin convertir a Hz; la conversión queda en el popup, según la velocidad).
        int rawInterval = device.DescriptorBInterval;
        var interval = NewCell(rawInterval > 0 ? rawInterval.ToString() : "—", right: true);
        interval.FontSize = 13;
        interval.FontWeight = FontWeights.SemiBold;
        if (device.ActiveHz != null)
            interval.Foreground = Feedback.AccentBrush; // solo acento cuando hay override
        ToolTipService.SetToolTip(interval, rawInterval > 0
            ? device.IsHighSpeed
                ? I18n.T("bInterval {0} en high-speed: 2^({0}-1) microframes (≈{1} Hz).", rawInterval, device.CurrentHz)
                : I18n.T("bInterval {0}: {0} ms por frame (≈{1} Hz).", rawInterval, device.CurrentHz)
            : I18n.T("No se pudo leer el bInterval del dispositivo."));

        var filter = NewCell(device.FilterOn ? I18n.T("Activo") : I18n.T("Inactivo"));
        filter.FontWeight = FontWeights.SemiBold;
        filter.Foreground = device.FilterOn ? Feedback.SuccessBrush : Feedback.MutedBrush;

        var cells = new FrameworkElement[Columns.Length];
        cells[0] = main;
        cells[1] = controller;
        cells[2] = interval;
        cells[3] = filter;

        for (int pos = 0; pos < Columns.Length; pos++)
        {
    // Separador visual entre columnas (borde izquierdo, salvo la primera).
            if (pos > 0)
            {
                var sep = new Rectangle
                {
                    Width = 1,
                    Fill = (Brush)ThemeBrushes.Get("SensorGridLineBrush"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsHitTestVisible = false
                };
                Grid.SetColumn(sep, pos);
                cols.Children.Add(sep);
            }

    // La celda se ubica según el orden actual de columnas (arrastrable).
            var el = cells[_colOrder[pos]];
            Grid.SetColumn(el, pos);
            cols.Children.Add(el);
        }

    // Fila plana estilo monitor de sensores: sin card, solo una línea inferior que
    // separa las filas (la selección la pinta ApplySelectionVisual).
        var container = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = (Brush)ThemeBrushes.Get("SensorGridLineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = cols,
            Padding = new Thickness(0),
            // La fila es plana (fondo transparente y sin esquinas), así que el reveal
            // global no la reconocería como card: se marca a mano para que la luz
            // también siga al mouse por la grilla de dispositivos.
            Tag = "reveal"
        };
        var outer = new Grid { Tag = device };
        outer.Tapped += DeviceRow_Tapped;
        outer.DoubleTapped += DeviceRow_DoubleTapped;
        outer.Children.Add(container);
        ToolTipService.SetToolTip(outer, I18n.T("Doble clic para cambiar la tasa de sondeo (Hz)"));

        return new DeviceRow { Outer = outer, Cols = cols, Container = container, Device = device };
    }

    private static TextBlock NewCell(string text, bool right = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
            Margin = new Thickness(8, 0, 8, 0)
        };
        ToolTipService.SetToolTip(tb, text);
        return tb;
    }

    // ===================== Popup: cambiar hercios =====================


    /// <summary>
    /// Popup de un dispositivo: muestra el estado actual y deja elegir la nueva tasa
    /// en Hz (presets + personalizado). "Aplicar y reiniciar" escribe el override y
    /// re-arranca el dispositivo; "Restaurar de fábrica" saca filtro + override y
    /// re-arranca (vuelve a la tasa nativa).
    /// </summary>
    private async Task ShowRateDialogAsync(UsbPollingDevice device)
    {
        if (XamlRoot == null || _busy) return;

        var content = new StackPanel { Spacing = 12, MinWidth = 400, MaxWidth = 480 };

    // Subtítulo: el hijo que representa al producto (como la columna Child de el filtro).
        if (!string.IsNullOrEmpty(device.ChildName))
        {
            content.Children.Add(new TextBlock
            {
                Text = device.ChildName,
                FontSize = 12,
                Foreground = Feedback.MutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

    // Estado actual: dos pares (etiqueta | valor) lado a lado, alineados.
        var details = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
    // Cuatro columnas reales: etiqueta/valor/etiqueta/valor (antes el par 2
    // caía en columnas inexistentes y el texto se superponía).
        for (int i = 0; i < 4; i++)
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddDetail(int col, int rowIdx, string label, string value, SolidColorBrush? valueBrush = null)
        {
            var lbl = new TextBlock { Text = label, FontSize = 12, Foreground = Feedback.MutedBrush, VerticalAlignment = VerticalAlignment.Center };
            var val = new TextBlock
            {
                Text = value,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (valueBrush != null)
                val.Foreground = valueBrush; // null explícito dejaría el texto invisible
            Grid.SetColumn(lbl, col);
            Grid.SetRow(lbl, rowIdx);
            Grid.SetColumn(val, col + 1);
            Grid.SetRow(val, rowIdx);
            details.Children.Add(lbl);
            details.Children.Add(val);
        }

        AddDetail(0, 0, I18n.T("Hz actuales"), device.CurrentHz > 0 ? $"{device.CurrentHz} Hz" : "—");
    // bInterval crudo del descriptor: en Low/Full es ms; en High es un exponente
    // (intervalo = 2^(N-1) microframes) — se muestra tal cual la spec, sin "ms" inventados.
        string bIntervalText = device.DescriptorBInterval > 0
            ? (device.IsHighSpeed
                ? $"N={device.DescriptorBInterval} (2^{device.DescriptorBInterval - 1} µf)"
                : $"{device.DescriptorBInterval} ms")
            : "—";
        AddDetail(0, 1, I18n.T("bInterval del descriptor"), bIntervalText);
        AddDetail(2, 0, I18n.T("Override guardado"), device.ActiveHz != null ? $"{device.ActiveHz} Hz" : I18n.T("Sin override"), device.ActiveHz != null ? Feedback.AccentBrush : null);
        AddDetail(2, 1, I18n.T("Filtro del dispositivo"), device.FilterOn ? I18n.T("Activo") : I18n.T("Inactivo"), device.FilterOn ? Feedback.SuccessBrush : null);
        content.Children.Add(details);

    // Separador sutil entre el estado y la edición.
        content.Children.Add(new Rectangle
        {
            Height = 1,
            Fill = ThemeBrushes.Get("SensorGridLineBrush"),
            IsHitTestVisible = false,
            Margin = new Thickness(0, 2, 0, 0)
        });

    // Selector estilo HIDUSBF: solo tasas que el MECANISMO puede dar en esta
    // máquina/dispositivo (variante del driver + HVCI + bus + descriptor). Nunca se
    // ofrece un hercio que después no se va a aplicar.
        UsbPollingCapability cap;
        try { cap = await _usb.GetPollingCapabilityAsync(device.InstanceId); }
        catch { cap = new UsbPollingCapability(1000, null); }

    // Escalera de tasas: se ofrece hasta el techo del BUS del dispositivo (1000 Hz en
    // Low/Full Speed, 8000 en High Speed). Es el mismo criterio que muestra hidusbf, y
    // evita ofrecer números que el dispositivo no puede entregar. Antes la lista se cortaba
    // en la tasa NATIVA del descriptor y todo lo de arriba quedaba detrás de una casilla:
    // eso escondía justo el caso normal de hidusbf (subir un mouse de 125 a 1000 Hz). La
    // nativa sigue visible, marcada dentro de la lista. High-speed llega a 8000 por el
    // exponente del descriptor; en Low/Full Speed el byte nativo y los códigos de bajada.
        var allRates = new[] { 8000, 4000, 2000, 1000, 500, 250, 125, 62, 31 };

        int native = device.NativeHz > 0 ? device.NativeHz : MaxHz1khzFallback;
        int selectedRate = device.ActiveHz ?? (device.CurrentHz > 0 ? device.CurrentHz : Math.Min(125, cap.MaxOverclockHz));

        var rateCombo = new ComboBox
        {
            Header = I18n.T("Nueva tasa de sondeo (Hz)"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 13
        };

        void FillRates()
        {
            rateCombo.Items.Clear();
            // La escalera + la nativa, de mayor a menor: así el valor real del dispositivo
            // aparece en su lugar y no colgado al final de la lista.
            var list = allRates
                .Concat(new[] { native })
                .Where(r => r > 0 && r <= cap.MaxOverclockHz)
                .Distinct()
                .OrderByDescending(r => r)
                .ToArray();
            foreach (var rate in list)
            {
                // "(nativa)" solo si el descriptor se pudo leer de verdad: llamar nativa a la
                // tasa de reserva (descriptor desconocido) sería afirmar algo que no se sabe.
                string label = device.NativeHz > 0 && rate == device.NativeHz
                    ? I18n.T("{0} Hz (nativa)", rate)
                    : $"{rate} Hz";
                rateCombo.Items.Add(new ComboBoxItem { Content = label, Tag = rate });
            }
            int idx = Array.IndexOf(list, selectedRate);
            rateCombo.SelectedIndex = idx < 0 ? 0 : idx;
        }

        FillRates();
        content.Children.Add(rateCombo);

    // Motivo del techo, solo cuando hay recorte real (HVCI activa o controladora
    // sin driver xHCI de Microsoft: en ambos casos el techo es 1000 Hz).
        if (cap.Reason != null && cap.MaxOverclockHz < 8000)
        {
            content.Children.Add(new TextBlock
            {
                Text = I18n.T(cap.Reason),
                FontSize = 11,
                Foreground = Feedback.WarningBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (device.Kind == UsbDeviceKind.Bluetooth)
        {
            // El filtro y el intervalo se aplican sobre la RADIO, no sobre el periférico
            // emparejado: es un ajuste del transporte y alcanza a todo lo que se conecte por
            // ella. Decirlo acá evita que se lea como si fuera un ajuste del mando o del mouse.
            content.Children.Add(new TextBlock
            {
                Text = I18n.T("Este es el adaptador Bluetooth: el ajuste es de la radio, no del dispositivo emparejado, así que alcanza a todo lo que se conecte por ella."),
                FontSize = 11,
                Foreground = Feedback.MutedBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var hint = new TextBlock
        {
            Text = I18n.T("Subir los hercios (overclock) puede no tener efecto en algunos dispositivos; bajarlos (downclock) siempre funciona. El cambio se aplica al reiniciar el dispositivo."),
            FontSize = 11,
            Foreground = Feedback.MutedBrush,
            TextWrapping = TextWrapping.Wrap
        };
        content.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = device.ControllerName,
            Content = content,
            PrimaryButtonText = I18n.T("Aplicar y reiniciar"),
            SecondaryButtonText = I18n.T("Restaurar de fábrica"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary && result != ContentDialogResult.Secondary)
            return;

        if (result == ContentDialogResult.Secondary)
        {
            await RestoreDeviceAsync(device);
            return;
        }

        int rateHz = rateCombo.SelectedItem is ComboBoxItem { Tag: int value } ? value : 0;
        if (rateHz <= 0) return;
        await ApplyRateAsync(device, rateHz);
    }

    /// <summary>
    /// Aplica la tasa elegida, reinicia el dispositivo y VERIFICA el resultado real
    /// (código de problema del devnode + hercios efectivos leídos del descriptor).
    /// Si el dispositivo quedó con problema, hace rollback (quita filtro + override) en
    /// vez de anunciar un éxito falso: el peor desenlace es dejar al usuario sin mouse.
    /// </summary>
    private async Task ApplyRateAsync(UsbPollingDevice device, int rateHz)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            SetBusy(true);
            Feedback.Running(StatusText, I18n.T("Aplicando {0} Hz...", rateHz), persistent: true);

            bool ok = await _usb.SetRateAsync(device.InstanceId, rateHz);
            if (!ok)
            {
                Feedback.Error(StatusText, I18n.T("No se pudo aplicar la tasa. Asegurate de que los componentes estén instalados."));
                return;
            }

    // Re-arrancar el dispositivo: el filtro entra con el nuevo bInterval.
            Feedback.Running(StatusText, I18n.T("Reiniciando el dispositivo para aplicar el cambio..."), persistent: true);
            bool restarted = await _usb.RestartDeviceAsync(device.InstanceId);

    // Verificación honesta post-restart: problema del devnode + tasa real leída.
            var verify = await _usb.VerifyDeviceAsync(device.InstanceId);
            if (verify.DeviceProblemCode is int prob && prob != 0)
            {
        // El filtro dejó el dispositivo en mal estado: rollback automático.
                Feedback.Running(StatusText, I18n.T("El dispositivo quedó con un problema tras aplicar la tasa; restaurando..."), persistent: true);
                await _usb.RestoreDeviceAsync(device.InstanceId);
                await _usb.RestartDeviceAsync(device.InstanceId);
                Feedback.Error(StatusText, I18n.T("La tasa {0} Hz dejó el dispositivo sin funcionar y se restauró a su estado anterior.", rateHz), persistent: true);
                return;
            }

            if (!restarted)
            {
                Feedback.Warning(StatusText, I18n.T("Tasa guardada: {0} Hz. El dispositivo no se pudo reiniciar automáticamente: desconectalo y volvé a conectarlo.", rateHz), persistent: true);
                return;
            }


            // La tasa efectiva de polling NO se puede medir desde modo usuario leyendo el
            // descriptor: el filtro parchea los IRPs en vivo y el descriptor crudo sigue
            // mostrando la tasa nativa (el propio autor sugiere verificar con un
            // "Mouse Rate Checker"). La verificación honesta acá es de SALUD: el driver
            // cargó y el dispositivo quedó operativo. Si el descriptor coincide con lo
            // pedido, ya era la tasa nativa del dispositivo.
            int descriptorHz = verify.EffectiveHz;
            if (descriptorHz == rateHz)
                Feedback.Success(StatusText, I18n.T("Tasa aplicada: {0} Hz. El dispositivo se reinició.", rateHz), persistent: true);
            else
                Feedback.Success(StatusText, I18n.T("Tasa aplicada: {0} Hz. El dispositivo se reinició. (El descriptor sigue reportando {1} Hz: el filtro fuerza el sondeo en vivo; verificalo con un medidor de polling.)", rateHz, descriptorHz > 0 ? descriptorHz : rateHz), persistent: true);
        }
        catch (Exception ex)
        {
            _logging.LogError($"OverclockUSB: error aplicando tasa a {device.InstanceId}", ex);
            Feedback.Error(StatusText, I18n.T("No se pudo aplicar la tasa."));
        }
        finally
        {
            _busy = false;
            await RefreshDevicesAsync();
        }
    }

    /// <summary>Restaura de fábrica: saca el filtro y el override, y reinicia (vuelve a la tasa nativa).</summary>
    private async Task RestoreDeviceAsync(UsbPollingDevice device)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            SetBusy(true);

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = I18n.T("Restaurar de fábrica"),
                Content = I18n.T("Se quita el filtro del dispositivo y el override de bInterval de «{0}», y el dispositivo vuelve a su tasa de fábrica.", device.ControllerName),
                PrimaryButtonText = I18n.T("Restaurar"),
                CloseButtonText = I18n.T("Cancelar"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            Feedback.Running(StatusText, I18n.T("Restaurando el dispositivo a fábrica..."), persistent: true);
            bool ok = await _usb.RestoreDeviceAsync(device.InstanceId);
            if (!ok)
            {
                Feedback.Error(StatusText, I18n.T("No se pudo restaurar el dispositivo."), persistent: true);
                return;
            }

            Feedback.Running(StatusText, I18n.T("Reiniciando el dispositivo..."), persistent: true);
            bool restarted = await _usb.RestartDeviceAsync(device.InstanceId);
            Feedback.Success(StatusText, restarted
                ? I18n.T("Dispositivo restaurado a fábrica y reiniciado.")
                : I18n.T("Dispositivo restaurado a fábrica. Reinicialo (desconectar/conectar) para terminar."),
                persistent: true);
        }
        catch (Exception ex)
        {
            _logging.LogError($"OverclockUSB: error restaurando {device.InstanceId}", ex);
            Feedback.Error(StatusText, I18n.T("No se pudo restaurar el dispositivo."));
        }
        finally
        {
            _busy = false;
            await RefreshDevicesAsync();
        }
    }

}
