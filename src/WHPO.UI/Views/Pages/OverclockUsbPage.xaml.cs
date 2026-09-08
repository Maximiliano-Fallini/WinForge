using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Overclock USB (inspirado en el filtro): lista los
/// dispositivos USB con polling ajustable (mouse, teclados y mandos HID) en una
/// grilla con su controller name, hercios reales y bInterval del descriptor, y
/// deja cambiar la tasa de sondeo por dispositivo — subiendo (overclock) o
/// bajando (downclock, que siempre funciona) — con un popup al hacer clic.
///
/// Primera vista (gate): si el componente del sistema (el motor kernel que fuerza
/// el bInterval) no está instalado, se muestra una pantalla profesional para
/// descargarlo e instalarlo automáticamente con un clic — la UI usa siempre
/// lenguaje genérico ("componentes del sistema") y nunca expone el nombre interno
/// del componente. Recién cuando está listo aparece la grilla de dispositivos.
/// El botón "Restaurar de fábrica" saca el filtro y el override y re-arranca.
/// </summary>
public sealed partial class OverclockUsbPage : Page
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

    // La página vive con caché de navegación: suscripciones una sola vez.
        Loaded += OnLoaded;
        I18n.LanguageChanged += OnLanguageChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        InitTable();
        BuildHeader();
        RefreshComponentUi(refreshDevices: true);
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

        if (_componentReady)
        {
    // Las filas se construyen en código con textos traducidos: re-render desde
    // la caché al cambiar de idioma (sin re-escannear el bus).
            UpdateServiceState();
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
            EnsureRateCombo();
            UpdateServiceState();
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
        RateCombo.IsEnabled = !busy;
        ServiceButton.IsEnabled = !busy;
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
        EmptyStateText.Text = I18n.T("No hay dispositivos USB compatibles (mouse, teclado o mando HID).");

    // Restaurar la selección previa (si sigue visible).
        if (keepId != null) SetSelectedRow(keepId);
        UpdateActionButtons();
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
    // Se agrupa por tipo primero (mouse &lt; teclado &lt; audio &lt; mando), así los
    // dispositivos del mismo tipo quedan juntos; dentro del mismo tipo, por nombre.
        int rank = KindRank(a.Kind).CompareTo(KindRank(b.Kind));
        if (rank != 0) return rank;
        return string.Compare(MainFunctionFor(a).Name, MainFunctionFor(b).Name, StringComparison.OrdinalIgnoreCase);
    }

    private static int KindRank(UsbDeviceKind kind) => kind switch
    {
        UsbDeviceKind.Mouse => 0,
        UsbDeviceKind.Keyboard => 1,
        UsbDeviceKind.Audio => 2,
        _ => 3
    };

    /// <summary>Glyph Segoe Fluent (monocromo, estilo del resto de la app) del tipo de
    /// dispositivo detectado: Mouse E962, Teclado E765, Audio E7F6 (Headphone),
    /// Mando E7FC (Game) y USB genérico E88E (el mismo de la cabecera de la página).</summary>
    private static string GlyphForKind(UsbDeviceKind kind) => kind switch
    {
        UsbDeviceKind.Mouse => "\uE962",
        UsbDeviceKind.Keyboard => "\uE765",
        UsbDeviceKind.Audio => "\uE7F6",
        UsbDeviceKind.Controller => "\uE7FC",
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
                Foreground = (Brush)ThemeBrushes.Get("SecondaryTextBrush"),
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
        return cell;
    }

    // ===================== Selección y acciones del dispositivo =====================

    private UsbPollingDevice? _selectedDevice;
    private static readonly int[] RatePresets = { 1000, 500, 250, 125, 62, 31 };

    /// <summary>Selecciona la fila (fondo acentuado) y actualiza las acciones de abajo.</summary>
    private void SetSelectedRow(string? instanceId)
    {
        _selectedDevice = _devices.FirstOrDefault(d => d.InstanceId == instanceId);
        foreach (var ui in _rowUis)
            ApplySelectionVisual(ui, ui.Device.InstanceId == instanceId);
        UpdateActionButtons();
    }

    private void ApplySelectionVisual(DeviceRow ui, bool selected)
    {
        Brush brush = selected
            ? (ThemeBrushes.Get("AccentTintBrush") ?? Feedback.AccentBrush)
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ui.Container.Background = brush;
    }

    /// <summary>Clic en una fila: la selecciona y abre el popup para cambiar los hercios.</summary>
    private async void DeviceRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Grid { Tag: UsbPollingDevice device })
        {
            SetSelectedRow(device.InstanceId);
            await ShowRateDialogAsync(device);
        }
    }

    /// <summary>Habilita/deshabilita los botones de acción según haya dispositivo seleccionado.</summary>
    private void UpdateActionButtons()
    {
        bool has = _selectedDevice != null;
        ApplyRateButton.IsEnabled = has;
        RestartButton.IsEnabled = has;
        RestoreButton.IsEnabled = has;
        if (has)
        {
            var d = _selectedDevice!;
            string label = string.IsNullOrEmpty(d.ChildName) ? d.ControllerName : d.ChildName;
            int selHz = d.ActiveHz ?? d.CurrentHz;
            SelectedDeviceHint.Text = selHz > 0 ? $"{label} · {selHz} Hz" : label;
        }
        else
        {
            SelectedDeviceHint.Text = I18n.T("Seleccioná un dispositivo de la lista para usar estas acciones.");
        }

    // Sincroniza el selector de hercios con la tasa del dispositivo elegido.
        if (has)
        {
            int target = _selectedDevice!.ActiveHz ?? _selectedDevice!.CurrentHz;
            if (target > 0) SelectRatePreset(target);
        }
    }

    /// <summary>Llena el desplegable de hercios (una sola vez) y lo deja en el preset por defecto.</summary>
    private void EnsureRateCombo()
    {
        if (RateCombo.Items.Count > 0) return;
        foreach (var rate in RatePresets)
            RateCombo.Items.Add(new ComboBoxItem { Content = $"{rate} Hz", Tag = rate });
        RateCombo.SelectedIndex = 0;
    }

    /// <summary>
    /// Selecciona la tasa exacta en el desplegable. Si el valor no coincide con ningún
    /// preset (ej. un dispositivo a 800 Hz), se agrega un ítem con el número real en
    /// lugar de ajustar al preset más cercano: mostrar "1000 Hz" para un dispositivo a
    /// 800 Hz invita a aplicar un cambio no deseado al confirmar.
    /// </summary>
    private void SelectRatePreset(int hz)
    {
    // Limpiar ítems dinámicos de selecciones anteriores.
        for (int i = RateCombo.Items.Count - 1; i >= 0; i--)
            if (RateCombo.Items[i] is ComboBoxItem { Tag: int v } && !RatePresets.Contains(v))
                RateCombo.Items.RemoveAt(i);

        for (int i = 0; i < RateCombo.Items.Count; i++)
        {
            if (RateCombo.Items[i] is ComboBoxItem { Tag: int v } && v == hz)
            {
                RateCombo.SelectedIndex = i;
                return;
            }
        }

    // Valor personalizado (ej. 800 Hz): ítem con el número real, no el preset vecino.
        RateCombo.Items.Add(new ComboBoxItem { Content = $"{hz} Hz", Tag = hz });
        RateCombo.SelectedIndex = RateCombo.Items.Count - 1;
    }

    private int SelectedRateHz()
        => RateCombo.SelectedItem is ComboBoxItem { Tag: int v } ? v : 0;

    private async void ApplyRateButton_Click(object sender, RoutedEventArgs e)
    {
        var device = _selectedDevice;
        if (device == null) return;
        int rate = SelectedRateHz();
        if (rate <= 0) return;
        await ApplyRateAsync(device, rate);
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        var device = _selectedDevice;
        if (device == null || _busy) return;
        _busy = true;
        try
        {
            SetBusy(true);
            Feedback.Running(StatusText, I18n.T("Reiniciando el dispositivo..."), persistent: true);
            bool ok = await _usb.RestartDeviceAsync(device.InstanceId);
            Feedback.Success(StatusText, ok
                ? I18n.T("Dispositivo reiniciado.")
                : I18n.T("No se pudo reiniciar el dispositivo."), persistent: true);
        }
        catch (Exception ex)
        {
            _logging.LogError($"OverclockUSB: error reiniciando {device.InstanceId}", ex);
            Feedback.Error(StatusText, I18n.T("No se pudo reiniciar el dispositivo."));
        }
        finally
        {
            _busy = false;
            SetBusy(false);
            await RefreshDevicesAsync();
        }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var device = _selectedDevice;
        if (device == null) return;
        await RestoreDeviceAsync(device);
    }

    /// <summary>Estado del servicio en la barra de acciones (botón instalar/reinstalar + texto).</summary>
    private void UpdateServiceState()
    {
        bool installed = _usb.IsFilterServiceInstalled();
        bool ready = installed && _usb.IsFilterServiceRunnable();
        ServiceButton.Content = ready
            ? I18n.T("Reinstalar servicio")
            : installed ? I18n.T("Reparar componentes") : I18n.T("Instalar servicio");
        ServiceStateText.Text = ready ? I18n.T("Motor de polling listo.") : "";
    }

    private async void ServiceButton_Click(object sender, RoutedEventArgs e)
        => await RunServiceInstallAsync();

    /// <summary>Descarga (si hace falta) e instala/repara el servicio desde la grilla.</summary>
    private async Task RunServiceInstallAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            SetBusy(true);
            Feedback.Running(StatusText, I18n.T("Instalando el componente del sistema..."), persistent: true);

            string? sys = await _usb.DownloadComponentAsync();
            if (string.IsNullOrEmpty(sys) || !File.Exists(sys))
            {
                Feedback.Error(StatusText, I18n.T("No se pudo descargar el componente. Verificá tu conexión a internet e intentá de nuevo."), persistent: true);
                return;
            }

            bool ok = await _usb.InstallFilterServiceAsync(sys);
            if (!ok)
            {
                Feedback.Error(StatusText, I18n.T("No se pudo instalar el componente. Ejecutá WinForge como administrador e intentá de nuevo."), persistent: true);
                return;
            }

            Feedback.Success(StatusText, I18n.T("Componente instalado. Ahora podés ajustar la tasa de cualquier dispositivo."), persistent: true);
        }
        catch (Exception ex)
        {
            _logging.LogError("OverclockUSB: error instalando el servicio desde la grilla", ex);
            Feedback.Error(StatusText, I18n.T("Ocurrió un error al preparar los componentes. Intentalo de nuevo."), persistent: true);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }

        UpdateServiceState();
        await RefreshDevicesAsync();
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
            Padding = new Thickness(0)
        };
        var outer = new Grid { Tag = device };
        outer.Tapped += DeviceRow_Tapped;
        outer.Children.Add(container);
        ToolTipService.SetToolTip(outer, I18n.T("Clic para cambiar la tasa de sondeo (Hz)"));

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

    // Selector de la nueva tasa. Rango representable según la velocidad del
    // dispositivo: Low/Full → 1000/255 ≈ 4 Hz mínimo y 1000 máximo; High → hasta
    // 8000 Hz (exponente N=1). Así el usuario no pide valores que el bus ignora.
        double minHz = device.IsHighSpeed ? 1 : 4;
        double maxHz = device.IsHighSpeed ? 8000 : 1000;
        var number = new NumberBox
        {
            Header = I18n.T("Nueva tasa de sondeo (Hz)"),
            Minimum = minHz,
            Maximum = maxHz,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = Math.Clamp((double)(device.ActiveHz ?? (device.CurrentHz > 0 ? device.CurrentHz : 125)), minHz, maxHz),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.Children.Add(number);

    // Presets rápidos (como la lista de el filtro): overclock y downclock. En
    // high-speed el bus permite más de 1000 Hz (2000/4000/8000).
        var presets = device.IsHighSpeed
            ? new[] { 8000, 4000, 2000, 1000, 500, 250, 125, 62, 31 }
            : new[] { 1000, 500, 250, 125, 62, 31 };
        var presetRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        foreach (var preset in presets)
        {
            var btn = new Button
            {
                Content = $"{preset} Hz",
                FontSize = 12,
                MinWidth = 58,
                Padding = new Thickness(10, 5, 10, 5),
                CornerRadius = new CornerRadius(6),
                Background = ThemeBrushes.Get("CardBackgroundBrush")
            };
            int hz = preset;
            btn.Click += (s, e) => number.Value = hz;
            presetRow.Children.Add(btn);
        }
        content.Children.Add(presetRow);

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

        int rateHz = (int)Math.Round(number.Value);
        await ApplyRateAsync(device, rateHz);
    }

    /// <summary>Aplica la tasa elegida y reinicia el dispositivo para que el filtro la tome.</summary>
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
            Feedback.Success(StatusText, restarted
                ? I18n.T("Tasa aplicada: {0} Hz. El dispositivo se reinició.", rateHz)
                : I18n.T("Tasa guardada: {0} Hz. El dispositivo no se pudo reiniciar automáticamente: desconectalo y volvé a conectarlo.", rateHz),
                persistent: true);
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
