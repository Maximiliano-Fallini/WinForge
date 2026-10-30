using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;

namespace WHPO_UI.Controls;

/// <summary>A fixed hit target owns the gesture; only its plastic visual tilts.</summary>
public sealed class CartridgeTile : UserControl
{
    private uint? _pointerId;
    private Point _press;
    private Point _grab;
    private bool _dragging;
    private readonly PlaneProjection _tilt = new();
    private readonly TranslateTransform _lift = new();
    private readonly Border _recess;

    // El hover persigue al puntero en vez de saltar de golpe al entrar o salir. Se
    // interpola en CompositionTarget.Rendering (el reloj de composición de la app) y el
    // bucle se corta solo al llegar al objetivo: quieto el mouse, no queda consumiendo
    // cuadros. Es independiente del refresh del monitor, así se ve igual a 60/144 Hz.
    private double _hoverTargetX, _hoverTargetY, _hoverTargetLift;
    private bool _hoverAnimating;
    private readonly Stopwatch _hoverWatch = new();
    /// <summary>Multiplicador del offset normalizado del puntero (±0,5): ±7° de inclinación.</summary>
    private const double HoverTiltRange = 14;
    /// <summary>Cuánto se levanta el cartucho al hover (px).</summary>
    private const double HoverLift = -4;
    /// <summary>Respuesta del suavizado (1/s): ~150 ms para llegar al 95 % del objetivo.</summary>
    private const double HoverResponse = 20;

    public bool IsRemoved { get; private set; }
    /// <summary>Ejecutable del juego que representa la card: la biblioteca lo usa para
    /// encontrar la tile al refrescar la carátula o el encabezado personalizados.</summary>
    public string? GameExe { get; set; }
    /// <summary>El cartucho montado en el dock va clavado: con esto en false el hover
    /// no lo inclina ni lo levanta (el clic para inspeccionar y el arrastre hacia
    /// afuera siguen funcionando igual).</summary>
    public bool HoverTiltEnabled { get; set; } = true;
    /// <summary>Manito de agarre al hover, SIN mover la card: solo se usa en el asiento
    /// del dock (que ya no hace tilt). Si es null no se toca el cursor (cards de la grilla).</summary>
    public Func<bool>? GrabHintAllowed { get; set; }
    public Border Recess => _recess;

    public UIElement? CoordinateRoot { get; set; }
    public Func<bool>? CanInteract { get; set; }
    public Grid CartridgeVisual { get; }
    public Image Artwork { get; }
    public string Title { get; }
    public string Launcher { get; }
    public event Action<Point, Point>? DragStarted;
    public event Action<Point>? DragMoved;
    public event Action<Point?>? DragCompleted;
    public event Action? InspectRequested;

    public CartridgeTile(string title, string launcher)
    {
        Title = title;
        Launcher = launcher;
        Tag = "no-reveal";
        Background = new SolidColorBrush(Colors.Transparent);
        ManipulationMode = ManipulationModes.None;
        Artwork = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        CartridgeVisual = CreateVisual(title, launcher, Artwork);
        CartridgeVisual.Projection = _tilt;
        CartridgeVisual.RenderTransform = _lift;
        _recess = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = Brush(17, 19, 24), CornerRadius = new CornerRadius(12),
            BorderBrush = Brush(55, 59, 69), BorderThickness = new Thickness(1), Margin = new Thickness(2),
            Child = new TextBlock
            {
                Text = title.ToUpperInvariant(), TextWrapping = TextWrapping.Wrap, MaxLines = 3,
                Foreground = Brush(100, 106, 120), FontSize = 11, TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 14, 22)
            }
        };
        _recess.IsHitTestVisible = false;
        // Keep the transparent hit surface enabled; only the artwork is non-interactive.
        Content = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Children = { _recess, CartridgeVisual }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, title);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, "CartridgeTile");

        // Cursor de agarre solo donde corresponde (asiento del dock): no mueve nada,
        // solo avisa que se puede agarrar. Se evalúa en vivo porque el permiso cambia
        // (juego en ejecución = clavado, lanzando = ocupado).
        PointerEntered += (_, _) =>
        {
            if (GrabHintAllowed == null) return;
            try { ProtectedCursor = GrabHintAllowed() ? InputSystemCursor.Create(InputSystemCursorShape.Hand) : null; }
            catch { }
        };
        PointerPressed += Press;
        PointerMoved += Move;
        PointerReleased += Release;
        PointerCaptureLost += Lost;
        PointerCanceled += Lost;
        PointerExited += (_, _) =>
        {
            // Con un gesto en curso el reset es inmediato: el arrastre manda.
            if (_pointerId != null) ResetTilt();
            else EndHover();
        };
        Unloaded += (_, _) => CancelGesture();
    }

    // The recess is a vacancy marker, not a permanent background or hover effect.
    public void SetRemoved(bool removed)
    {
        IsRemoved = removed;
        ResetTilt();
        CartridgeVisual.Opacity = removed ? 0 : 1;
        _recess.Visibility = removed ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Construye el polígono exacto de la silueta del cartucho (con esquinas redondeadas y
    /// muescas laterales en y=708..762 sobre 600×878) para el tamaño actual del control.
    /// </summary>
    internal static PointCollection BuildSilhouettePoints(double w, double h, double offsetX = 0, double offsetY = 0)
    {
        var pts = new PointCollection();
        if (w <= 1 || h <= 1) return pts;

        double sx = w / CartridgeModel.FaceWidth;
        double sy = h / CartridgeModel.FaceHeight;
        const double r = 36.0;
        const double ny0 = CartridgeModel.NotchTopY;
        const double ny1 = CartridgeModel.NotchTopY + CartridgeModel.NotchHeightPx;
        const double nd = CartridgeModel.NotchDepthPx;
        const int arcSteps = 8;

        void AddPt(double x, double y) => pts.Add(new Point(x * sx + offsetX, y * sy + offsetY));
        void AddCornerArc(double cx, double cy, double startDeg, double endDeg)
        {
            for (int i = 0; i <= arcSteps; i++)
            {
                double a = (startDeg + (endDeg - startDeg) * i / arcSteps) * Math.PI / 180.0;
                AddPt(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
            }
        }

        // Esquina superior izquierda (180° → 270°) y superior derecha (270° → 360°)
        AddCornerArc(r, r, 180, 270);
        AddCornerArc(CartridgeModel.FaceWidth - r, r, 270, 360);

        // Lado derecho con la muesca biselada
        AddPt(CartridgeModel.FaceWidth, ny0);
        AddPt(CartridgeModel.FaceWidth - nd, ny0 + nd);
        AddPt(CartridgeModel.FaceWidth - nd, ny1 - nd);
        AddPt(CartridgeModel.FaceWidth, ny1);

        // Esquina inferior derecha (0° → 90°) e inferior izquierda (90° → 180°)
        AddCornerArc(CartridgeModel.FaceWidth - r, CartridgeModel.FaceHeight - r, 0, 90);
        AddCornerArc(r, CartridgeModel.FaceHeight - r, 90, 180);

        // Lado izquierdo con la muesca biselada
        AddPt(0, ny1);
        AddPt(nd, ny1 - nd);
        AddPt(nd, ny0 + nd);
        AddPt(0, ny0);

        return pts;
    }

    // This shell exists immediately and is opaque, even offline or after a failed PNG decode.
    public static Grid CreateVisual(string title, string launcher, Image artwork)
    {
        var shell = new Polygon
        {
            Fill = Brush(48, 48, 54),
            Stroke = Brush(36, 36, 42),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        var sticker = new Grid { Margin = new Thickness(14, 16, 14, 24) };
        sticker.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.22, GridUnitType.Star) });
        sticker.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sticker.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.16, GridUnitType.Star) });
        var header = new Border
        {
            Background = Brush(28, 28, 32), CornerRadius = new CornerRadius(6, 6, 0, 0),
            Child = new TextBlock
            {
                Text = launcher.ToUpperInvariant(), FontSize = 10, Foreground = Brush(240, 240, 244),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var label = new Border
        {
            Background = Brush(38, 40, 48), Padding = new Thickness(8),
            Child = new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center, Foreground = Brush(244, 244, 248),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var code = new Border
        {
            Background = Brush(11, 11, 13), CornerRadius = new CornerRadius(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = "WINFORGE · GAME CARD", FontSize = 8, Foreground = Brush(220, 220, 226),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetRow(label, 1); Grid.SetRow(code, 2);
        sticker.Children.Add(header); sticker.Children.Add(label); sticker.Children.Add(code);
        var tab = new Border
        {
            Width = 42, Height = 7, CornerRadius = new CornerRadius(3), Background = Brush(41, 41, 46),
            BorderBrush = Brush(32, 32, 36), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var arrow = new TextBlock
        {
            Text = "▼", FontSize = 11, Foreground = Brush(40, 40, 45),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var fallback = new Grid
        {
            Children = { shell, sticker, tab, arrow },
            Opacity = 0,
            IsHitTestVisible = false
        };
        artwork.ImageOpened += (_, _) => fallback.Opacity = 0;
        artwork.ImageFailed += (_, _) => fallback.Opacity = 1;

        // Sin sombra: se probó una imagen de silueta difusa como hijo y el halo quedaba
        // recortado por el layout de la card; una ThemeShadow alrededor del plástico se
        // veía como un marco gris. La profundidad la dan el tilt y el lift del hover.
        var visual = new Grid { IsHitTestVisible = false };
        visual.Children.Add(fallback);
        visual.Children.Add(artwork);
        visual.SizeChanged += (_, e) =>
            shell.Points = BuildSilhouettePoints(e.NewSize.Width, e.NewSize.Height);

        return visual;
    }

    private void Press(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != null || IsRemoved || CanInteract?.Invoke() == false) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        // Capture BEFORE the threshold: otherwise the ScrollViewer/another card takes the gesture.
        if (!CapturePointer(e.Pointer)) return;
        _pointerId = e.Pointer.PointerId;
        _press = e.GetCurrentPoint(CoordinateRoot ?? this).Position;
        _grab = new Point(
            Math.Clamp(point.Position.X / Math.Max(1, ActualWidth), 0, 1),
            Math.Clamp(point.Position.Y / Math.Max(1, ActualHeight), 0, 1));
        e.Handled = true;
    }

    private void Move(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId == e.Pointer.PointerId)
        {
            var point = e.GetCurrentPoint(CoordinateRoot ?? this).Position;
            double dx = point.X - _press.X, dy = point.Y - _press.Y;
            if (!_dragging && dx * dx + dy * dy >= 64)
            {
                _dragging = true;
                ResetTilt();
                DragStarted?.Invoke(point, _grab);
            }
            if (_dragging) DragMoved?.Invoke(point);
            e.Handled = true;
            return;
        }
        if (!HoverTiltEnabled || _pointerId != null || IsRemoved || CanInteract?.Invoke() == false) return;
        var p = e.GetCurrentPoint(this).Position;
        // Solo se fijan los OBJETIVOS: el cuadro de composición interpola los valores
        // actuales hacia ellos, así entrar o salir no produce un salto instantáneo.
        _hoverTargetY = -Math.Clamp(p.X / Math.Max(1, ActualWidth) - 0.5, -0.5, 0.5) * HoverTiltRange;
        _hoverTargetX = Math.Clamp(p.Y / Math.Max(1, ActualHeight) - 0.5, -0.5, 0.5) * HoverTiltRange;
        _hoverTargetLift = HoverLift;
        EnsureHoverAnimation();
    }

    private void Release(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId != e.Pointer.PointerId) return;
        bool wasDragging = _dragging;
        var point = e.GetCurrentPoint(CoordinateRoot ?? this).Position;
        _pointerId = null;
        _dragging = false;
        // Clear gesture state before ReleasePointerCapture raises PointerCaptureLost.
        ReleasePointerCapture(e.Pointer);
        ResetTilt();
        if (wasDragging) DragCompleted?.Invoke(point);
        else InspectRequested?.Invoke();
        e.Handled = true;
    }

    private void Lost(object sender, PointerRoutedEventArgs e)
    {
        if (_pointerId == e.Pointer.PointerId) CancelGesture();
    }

    public void CancelGesture()
    {
        bool wasDragging = _dragging;
        _pointerId = null;
        _dragging = false;
        ReleasePointerCaptures();
        ResetTilt();
        if (wasDragging) DragCompleted?.Invoke(null);
    }

    /// <summary>Vuelve al reposo (y corta el suavizado del hover) sin animar: lo usan los
    /// gestos, el retiro del cartucho y la descarga del control, donde el estado tiene que
    /// quedar limpio ya mismo.</summary>
    private void ResetTilt()
    {
        StopHoverAnimation();
        _hoverTargetX = _hoverTargetY = _hoverTargetLift = 0;
        _tilt.RotationX = _tilt.RotationY = 0;
        _lift.Y = 0;
    }

    /// <summary>El puntero salió sin arrastrar: vuelve al reposo suavemente, en vez de
    /// cortar la inclinación y el levante de un cuadro al otro.</summary>
    private void EndHover()
    {
        _hoverTargetX = _hoverTargetY = _hoverTargetLift = 0;
        if (Math.Abs(_tilt.RotationX) < 0.01 &&
            Math.Abs(_tilt.RotationY) < 0.01 &&
            Math.Abs(_lift.Y) < 0.01)
        {
            ResetTilt();
            return;
        }
        EnsureHoverAnimation();
    }

    private void EnsureHoverAnimation()
    {
        if (_hoverAnimating) return;
        _hoverAnimating = true;
        _hoverWatch.Restart();
        CompositionTarget.Rendering += OnHoverRendering;
    }

    private void StopHoverAnimation()
    {
        if (!_hoverAnimating) return;
        _hoverAnimating = false;
        CompositionTarget.Rendering -= OnHoverRendering;
        _hoverWatch.Stop();
    }

    private void OnHoverRendering(object? sender, object e)
    {
        double dt = _hoverWatch.Elapsed.TotalSeconds;
        _hoverWatch.Restart();
        // Un salto de tiempo (ventana minimizada, equipo dormido) no debe teletransportar
        // el cartucho: se trata como un cuadro normal.
        if (dt <= 0 || dt > 0.25) dt = 1.0 / 60.0;

        double k = 1.0 - Math.Exp(-HoverResponse * dt);
        _tilt.RotationX += (_hoverTargetX - _tilt.RotationX) * k;
        _tilt.RotationY += (_hoverTargetY - _tilt.RotationY) * k;
        _lift.Y += (_hoverTargetLift - _lift.Y) * k;

        if (Math.Abs(_hoverTargetX - _tilt.RotationX) < 0.02 &&
            Math.Abs(_hoverTargetY - _tilt.RotationY) < 0.02 &&
            Math.Abs(_hoverTargetLift - _lift.Y) < 0.02)
        {
            _tilt.RotationX = _hoverTargetX;
            _tilt.RotationY = _hoverTargetY;
            _lift.Y = _hoverTargetLift;
            StopHoverAnimation();
        }
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
        => new(Windows.UI.Color.FromArgb(255, r, g, b));
}
