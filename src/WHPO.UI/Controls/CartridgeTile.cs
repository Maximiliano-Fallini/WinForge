using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System.Diagnostics;
using System.IO;
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
    private readonly Grid _recess;
    private readonly TextBlock _recessLabel;
    private bool _slotVisible;
    private DeviceKind _kind = DeviceKind.Cartridge;
    // Piezas del fallback 2D que cambian según el tipo de dispositivo.
    private UIElement? _fallbackTab, _fallbackArrow;
    private TextBlock? _fallbackCode;

    // El hover persigue al puntero en vez de saltar de golpe al entrar o salir. Se
    // interpola en CompositionTarget.Rendering (el reloj de composición de la app) y el
    // bucle se corta solo al llegar al objetivo: quieto el mouse, no queda consumiendo
    // cuadros. Es independiente del refresh del monitor, así se ve igual a 60/144 Hz.
    private double _hoverTargetX, _hoverTargetY, _hoverTargetLift;
    private bool _hoverAnimating;
    private readonly Stopwatch _hoverWatch = new();
    /// <summary>Multiplicador del offset normalizado del puntero (±0,5): ±7° de inclinación.</summary>
    private const double HoverTiltRange = 14;
    /// <summary>Hover sin elevación: el cartucho solo se inclina hacia el puntero.
    /// Antes se levantaba 4 px; se quitó para que no "salte" al pasar el mouse.</summary>
    private const double HoverLift = 0;
    /// <summary>Respuesta del suavizado (1/s): ~150 ms para llegar al 95 % del objetivo.</summary>
    private const double HoverResponse = 20;

    public bool IsRemoved { get; private set; }
    /// <summary>Ejecutable del juego que representa la card: la biblioteca lo usa para
    /// encontrar la tile al refrescar la carátula o el encabezado personalizados.</summary>
    public string? GameExe { get; set; }
    /// <summary>El cartucho montado en el dock va clavado: con esto en false el hover
    /// no lo inclina (el hover tampoco lo levanta). El clic para inspeccionar y el
    /// arrastre hacia afuera siguen funcionando igual.</summary>
    public bool HoverTiltEnabled { get; set; } = true;
    /// <summary>Manito de agarre al hover, SIN mover la card: solo se usa en el asiento
    /// del dock (que ya no hace tilt). Si es null no se toca el cursor (cards de la grilla).</summary>
    public Func<bool>? GrabHintAllowed { get; set; }
    public Grid Recess => _recess;

    /// <summary>
    /// Muestra el POZO con la silueta del cartucho detrás de la card (el mismo material
    /// de las cards). Lo encienden las cards de la grilla; el asiento del dock no, porque
    /// ahí el cartucho va metido en el puerto y no flota sobre un pozo.
    /// </summary>
    public bool SlotVisible
    {
        get => _slotVisible;
        set { _slotVisible = value; UpdateRecessVisibility(); }
    }

    /// <summary>Tipo de hardware de la pieza (cartucho, consola o teléfono): cambia la
    /// silueta del pozo y del fallback 2D. En la grilla la imagen ya viene pintada con el
    /// tipo por el loader de la página.</summary>
    public DeviceKind Kind
    {
        get => _kind;
        set { _kind = value; ApplyKind(); }
    }

    public UIElement? CoordinateRoot { get; set; }
    public Func<bool>? CanInteract { get; set; }
    /// <summary>La consola y el teléfono no se arrastran: son la máquina, no un cartucho
    /// que se inserta. Un clic sigue abriendo la inspección.</summary>
    public bool Draggable { get; set; } = true;
    /// <summary>Con el control 3D en vivo montado, el puntero lo maneja ÉL (gira la
    /// consola como en la inspección): la tile no captura el gesto.</summary>
    public bool LiveView { get; private set; }

    /// <summary>Muestra el control 3D en vivo sobre el arte quieto (que es un PNG).</summary>
    public void AttachLiveView(FrameworkElement view)
    {
        LiveView = true;
        // El PNG queda a la vista DEBAJO hasta que el control publique su primer cuadro
        // (los dos están en la misma pose, así que el cambio no se nota). Taparlo acá
        // dejaba un hueco de un cuadro al entrar con el mouse.
        view.HorizontalAlignment = HorizontalAlignment.Stretch;
        view.VerticalAlignment = VerticalAlignment.Stretch;
        // El control va HERMANO de la capa visual, encima y fuera de ella: la capa nace
        // en no-hit-test (el arte quieto no recibe el puntero) y además lleva la
        // proyección del tilt, donde el hit-test no es confiable. En la raíz de la tile
        // ocupa la misma área y recibe el arrastre como en la inspección.
        if (Content is Grid root)
        {
            if (!root.Children.Contains(view)) root.Children.Add(view);
        }
        else if (!CartridgeVisual.Children.Contains(view))
        {
            CartridgeVisual.Children.Add(view);
        }
    }

    /// <summary>Tapa el PNG cuando el control en vivo ya dibujó su primer cuadro.</summary>
    public void HideArtworkForLiveView()
    {
        if (LiveView) Artwork.Visibility = Visibility.Collapsed;
    }

    /// <summary>Saca el control en vivo y vuelve al PNG de la biblioteca.</summary>
    public void DetachLiveView(FrameworkElement view)
    {
        LiveView = false;
        if (Content is Grid root) root.Children.Remove(view);
        CartridgeVisual.Children.Remove(view);
        Artwork.Visibility = Visibility.Visible;
    }
    /// <summary>Archivos de las vistas del giro (la 0 es el arte principal, ya cargado).
    /// Se decodifican recién al acercar el puntero y se sueltan al salir, así una grilla
    /// con varias consolas no tiene todas las vistas en memoria.</summary>
    public IReadOnlyList<string>? SpinFrameFiles { get; set; }
    private List<ImageSource>? _spinFrames;
    private ImageSource? _baseSource;
    private bool _spinLoading;
    // Solo la última card apuntada conserva las vistas decodificadas.
    private static CartridgeTile? _spinOwner;
    // Píxeles de arrastre por vista girada.
    private const double SpinStepPx = 14;
    private int _spinIndex;
    private int _spinBase;
    private int _spinStep;
    public Grid CartridgeVisual { get; }
    public Image Artwork { get; }
    public string Title { get; }
    public string Launcher { get; }
    public event Action<Point, Point>? DragStarted;
    public event Action<Point>? DragMoved;
    public event Action<Point?>? DragCompleted;
    public event Action? InspectRequested;

    public CartridgeTile(string title, string launcher, DeviceKind kind = DeviceKind.Cartridge)
    {
        Title = title;
        Launcher = launcher;
        _kind = kind;
        Tag = "no-reveal";
        Background = new SolidColorBrush(Colors.Transparent);
        ManipulationMode = ManipulationModes.None;
        // El dispositivo conserva la proporción de su render (el gabinete arcade es alto
        // y angosto); el cartucho llena el marco como siempre.
        Artwork = new Image
        {
            Stretch = kind == DeviceKind.Cartridge ? Stretch.Fill : Stretch.Uniform,
            IsHitTestVisible = false
        };
        // Giro: se prepara al acercar el puntero y se libera al alejarlo.
        PointerEntered += (_, _) => LoadSpinFramesAsync();
        PointerExited += (_, _) => ReleaseSpinFrames();
        CartridgeVisual = CreateVisual(title, launcher, Artwork, kind);
        // El fallback se construyó con el kind del constructor: sacar las piezas que
        // dependen del tipo (fallback.Children = { shell, sticker, tab, arrow }).
        if (CartridgeVisual.Children.Count > 0 && CartridgeVisual.Children[0] is Grid fallback)
        {
            if (fallback.Children.Count > 3) { _fallbackTab = fallback.Children[2]; _fallbackArrow = fallback.Children[3]; }
            if (fallback.Children.Count > 1 && fallback.Children[1] is Grid sticker && sticker.Children.Count > 2
                && sticker.Children[2] is Border codeBorder && codeBorder.Child is TextBlock codeText)
                _fallbackCode = codeText;
        }
        CartridgeVisual.Projection = _tilt;
        CartridgeVisual.RenderTransform = _lift;
        // POZO con la SILUETA del cartucho: sin marco ni filo propio. El relleno es la
        // superficie viva del tema (misma que las cards), así que responde al ajuste de
        // transparencia y al blur de paneles; lo que separa el pozo de la bandeja es el
        // material, no un borde blanco. Se dibuja SIEMPRE detrás de la card (lo enciende
        // SlotVisible) y cuando el cartucho se levanta al dock queda solo el pozo con el
        // nombre del juego.
        var recessShell = new Polygon
        {
            Fill = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            IsHitTestVisible = false
        };
        var recessLabel = new TextBlock
        {
            Text = title.ToUpperInvariant(), TextWrapping = TextWrapping.Wrap, MaxLines = 3,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"), FontSize = 11,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 14, 22),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _recessLabel = recessLabel;
        _recess = new Grid
        {
            // -4: el pozo llega al borde de la card (el chasis de la grilla tiene 4 px de
            // padding), así la silueta asoma alrededor del cartucho como en Carthage.
            Margin = new Thickness(-4),
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false,
            Children = { recessShell, recessLabel }
        };
        // La silueta se recalcula con el tamaño real del pozo: acompaña cualquier ancho
        // de columna, igual que el cartucho y el skeleton.
        _recess.SizeChanged += (_, e) =>
            recessShell.Points = _kind == DeviceKind.Cartridge
                ? BuildRecessPoints(e.NewSize.Width, e.NewSize.Height)
                : BuildSilhouettePoints(e.NewSize.Width, e.NewSize.Height, kind: _kind);
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
        // El pozo queda; el nombre solo se muestra cuando el cartucho ya no está.
        _recessLabel.Visibility = removed ? Visibility.Visible : Visibility.Collapsed;
        UpdateRecessVisibility();
    }

    /// <summary>El pozo se ve en las cards de la grilla; el asiento del dock no lo lleva.</summary>
    private void UpdateRecessVisibility()
        => _recess.Visibility = _slotVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Consola/teléfono: sin pestaña ni flecha de cartucho y con su propia
    /// etiqueta inferior. El cartucho mantiene la pestaña, la flecha y GAME CARD.</summary>
    private void ApplyKind()
    {
        bool isDevice = _kind != DeviceKind.Cartridge;
        // El PNG del dispositivo conserva SU proporción (el gabinete arcade es alto y
        // angosto): con Fill se estiraba a lo ancho del arte y se veía deformado.
        Artwork.Stretch = isDevice ? Stretch.Uniform : Stretch.Fill;
        if (_fallbackTab != null) _fallbackTab.Visibility = isDevice ? Visibility.Collapsed : Visibility.Visible;
        if (_fallbackArrow != null) _fallbackArrow.Visibility = isDevice ? Visibility.Collapsed : Visibility.Visible;
        if (_fallbackCode != null)
            _fallbackCode.Text = _kind switch
            {
                DeviceKind.Console => "WINFORGE · CONSOLE",
                DeviceKind.Phone => "WINFORGE · PHONE",
                DeviceKind.Arcade => "WINFORGE · ARCADE",
                _ => "WINFORGE · GAME CARD"
            };
    }

    /// <summary>
    /// Construye el polígono exacto de la silueta del cartucho (con esquinas redondeadas y
    /// muescas laterales en y=708..762 sobre 600×878) para el tamaño actual del control.
    /// </summary>
    internal static PointCollection BuildSilhouettePoints(double w, double h, double offsetX = 0, double offsetY = 0, DeviceKind kind = DeviceKind.Cartridge)
    {
        var pts = new PointCollection();
        if (w <= 1 || h <= 1) return pts;

        double sx = w / CartridgeModel.FaceWidth;
        double sy = h / CartridgeModel.FaceHeight;

        if (kind != DeviceKind.Cartridge)
        {
            // Losa redondeada del dispositivo sobre su cuerpo real (teléfono angosto /
            // consola ancha). Las esquinas son las del rectángulo de DeviceBody: la
            // misma pieza que la malla 3D y que el pintado de caras. La referencia es la
            // vista recortada (DeviceViewRect), igual que la imagen de grilla: así la
            // silueta ocupa toda la tile en vez de quedar chica dentro del canvas.
            var (bx, by, bw, bh, br) = CartridgeModel.DeviceBody(kind);
            var (vx, vy, vw, vh) = CartridgeModel.DeviceViewRect(kind);
            double vsx = w / vw, vsy = h / vh;
            const int dSteps = 10;
            void AddDeviceCorner(double cx, double cy, double startDeg, double endDeg)
            {
                for (int i = 0; i <= dSteps; i++)
                {
                    double a = (startDeg + (endDeg - startDeg) * i / dSteps) * Math.PI / 180.0;
                    pts.Add(new Point((cx + br * Math.Cos(a) - vx) * vsx + offsetX, (cy + br * Math.Sin(a) - vy) * vsy + offsetY));
                }
            }
            AddDeviceCorner(bx + br, by + br, 180, 270);
            AddDeviceCorner(bx + bw - br, by + br, 270, 360);
            AddDeviceCorner(bx + bw - br, by + bh - br, 0, 90);
            AddDeviceCorner(bx + br, by + bh - br, 90, 180);
            return pts;
        }

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

    /// <summary>
    /// Filas de la etiqueta con las proporciones del canvas real (cabecera 172, arte 454
    /// y franja de código 88 sobre 714): la franja queda donde la muesca lateral la corta.
    /// </summary>
    internal static void ApplyStickerRows(Grid sticker)
    {
        sticker.RowDefinitions.Clear();
        sticker.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CartridgeModel.HeaderH, GridUnitType.Star) });
        sticker.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CartridgeModel.ArtH, GridUnitType.Star) });
        sticker.RowDefinitions.Add(new RowDefinition { Height = new GridLength(
            CartridgeModel.StickerH - CartridgeModel.HeaderH - CartridgeModel.ArtH, GridUnitType.Star) });
    }

    /// <summary>
    /// Silueta del pozo con el MISMO estilo de muesca que tenía: el contorno del cartucho
    /// agrandado 8 px (4 por lado) y su muesca escalada con el mismo bisel. La única
    /// diferencia es que la muesca se desplaza a la altura de la del cartucho: antes se
    /// escalaba desde el borde superior del pozo y quedaba unos píxeles más abajo.
    /// </summary>
    internal static PointCollection BuildRecessPoints(double w, double h)
    {
        var pts = BuildSilhouettePoints(w, h);
        if (pts.Count < 44) return pts;

        double sy = h / CartridgeModel.FaceHeight;
        double cardTop = 4 + CartridgeModel.NotchTopY * (h - 8) / CartridgeModel.FaceHeight;
        double cardBottom = 4 + (CartridgeModel.NotchTopY + CartridgeModel.NotchHeightPx)
            * (h - 8) / CartridgeModel.FaceHeight;
        double scaledTop = CartridgeModel.NotchTopY * sy;
        double scaledBottom = (CartridgeModel.NotchTopY + CartridgeModel.NotchHeightPx) * sy;
        double deltaTop = cardTop - scaledTop;
        double deltaBottom = cardBottom - scaledBottom;

        // Índices de los cuatro puntos de cada muesca en BuildSilhouettePoints:
        // derecha 18..21 (arriba afuera/adentro, abajo adentro/afuera) e izquierda 40..43.
        var shifted = new PointCollection();
        for (int i = 0; i < pts.Count; i++)
        {
            double dy = i switch
            {
                18 or 19 or 42 or 43 => deltaTop,
                20 or 21 or 40 or 41 => deltaBottom,
                _ => 0
            };
            shifted.Add(new Point(pts[i].X, pts[i].Y + dy));
        }
        return shifted;
    }

    /// <summary>
    /// Lleva la etiqueta y la pestaña de un placeholder a la geometría del canvas 600×878
    /// (etiqueta 44,60 512×714; pestaña 206,34 188×28). El contorno ya usa esa referencia;
    /// con márgenes fijos la etiqueta quedaba más abajo y la muesca parecía alta.
    /// </summary>
    internal static void ApplyPlaceholderChrome(Grid sticker, Border? well, Border? tab, double w, double h)
    {
        if (w <= 1 || h <= 1) return;
        double sx = w / CartridgeModel.FaceWidth;
        double sy = h / CartridgeModel.FaceHeight;
        sticker.Margin = new Thickness(
            CartridgeModel.StickerX * sx,
            CartridgeModel.StickerY * sy,
            (CartridgeModel.FaceWidth - CartridgeModel.StickerX - CartridgeModel.StickerW) * sx,
            (CartridgeModel.FaceHeight - CartridgeModel.StickerY - CartridgeModel.StickerH) * sy);
        if (well != null)
            well.CornerRadius = new CornerRadius(CartridgeModel.StickerR * sx);
        if (tab != null)
        {
            tab.Width = CartridgeModel.TabW * sx;
            tab.Height = CartridgeModel.TabH * sy;
            tab.Margin = new Thickness(0, CartridgeModel.TabY * sy, 0, 0);
            tab.CornerRadius = new CornerRadius(CartridgeModel.TabR * sx);
        }
    }

    // This shell exists immediately and is opaque, even offline or after a failed PNG decode.
    public static Grid CreateVisual(string title, string launcher, Image artwork, DeviceKind kind = DeviceKind.Cartridge)
    {
        var shell = new Polygon
        {
            Fill = Brush(48, 48, 54),
            Stroke = Brush(36, 36, 42),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        var sticker = new Grid { Margin = new Thickness(14, 16, 14, 24) };
        ApplyStickerRows(sticker);
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
                Text = kind switch
                {
                    DeviceKind.Console => "WINFORGE · CONSOLE",
                    DeviceKind.Phone => "WINFORGE · PHONE",
                DeviceKind.Arcade => "WINFORGE · ARCADE",
                    _ => "WINFORGE · GAME CARD"
                },
                FontSize = 8, Foreground = Brush(220, 220, 226),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetRow(label, 1); Grid.SetRow(code, 2);
        sticker.Children.Add(header); sticker.Children.Add(label); sticker.Children.Add(code);
        bool isDevice = kind != DeviceKind.Cartridge;
        var tab = new Border
        {
            Width = 42, Height = 7, CornerRadius = new CornerRadius(3), Background = Brush(41, 41, 46),
            BorderBrush = Brush(32, 32, 36), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = isDevice ? Visibility.Collapsed : Visibility.Visible
        };
        var arrow = new TextBlock
        {
            Text = "▼", FontSize = 11, Foreground = Brush(40, 40, 45),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = isDevice ? Visibility.Collapsed : Visibility.Visible
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
        // veía como un marco gris. La profundidad la da el tilt del hover.
        var visual = new Grid { IsHitTestVisible = false };
        visual.Children.Add(fallback);
        visual.Children.Add(artwork);
        visual.SizeChanged += (_, e) =>
        {
            shell.Points = BuildSilhouettePoints(e.NewSize.Width, e.NewSize.Height, kind: kind);
            if (isDevice)
            {
                // El sticker del fallback debe vivir dentro del cuerpo del
                // dispositivo (teléfono angosto / consola ancha), no en todo el tile.
                // Referencia: la vista recortada, igual que la imagen de grilla.
                var (bx, by, bw, bh, _) = CartridgeModel.DeviceBody(kind);
                var (vx, vy, vw, vh) = CartridgeModel.DeviceViewRect(kind);
                sticker.Margin = new Thickness(
                    (bx - vx) / vw * e.NewSize.Width,
                    (by - vy) / vh * e.NewSize.Height,
                    (vx + vw - bx - bw) / vw * e.NewSize.Width,
                    (vy + vh - by - bh) / vh * e.NewSize.Height);
            }
            else
            {
                ApplyPlaceholderChrome(sticker, null, tab, e.NewSize.Width, e.NewSize.Height);
            }
        };

        return visual;
    }

    private void Press(object sender, PointerRoutedEventArgs e)
    {
        // El control 3D en vivo tiene sus propios gestos (girar / scrollear).
        if (LiveView) return;
        if (_pointerId != null || IsRemoved || CanInteract?.Invoke() == false) return;
        if (!Draggable) LoadSpinFramesAsync();
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
                _spinBase = _spinIndex;
                _spinStep = 0;
                if (Draggable) DragStarted?.Invoke(point, _grab);
            }
            if (_dragging)
            {
                if (Draggable) DragMoved?.Invoke(point);
                else Spin(dx);
            }
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

    /// <summary>Gira la consola con el arrastre horizontal: cada <see cref="SpinStepPx"/> px
    /// pasa a la vista siguiente. El ángulo queda donde se soltó.</summary>
    /// <summary>Decodifica las vistas del giro; solo una card las mantiene a la vez.</summary>
    private async void LoadSpinFramesAsync()
    {
        if (Draggable || IsRemoved || _spinFrames != null || _spinLoading) return;
        // Sin arte principal todavía no hay vista 0: se reintenta al próximo hover.
        if (Artwork.Source == null) return;
        var files = SpinFrameFiles;
        if (files is not { Count: > 0 }) return;
        _spinLoading = true;
        try
        {
            var frames = new List<ImageSource>(files.Count + 1) { Artwork.Source };
            bool ok = true;
            foreach (var path in files)
            {
                if (!File.Exists(path)) { ok = false; break; }
                using var stream = File.OpenRead(path).AsRandomAccessStream();
                var bitmap = new BitmapImage { DecodePixelWidth = 720 };
                await bitmap.SetSourceAsync(stream);
                frames.Add(bitmap);
            }
            if (!ok) return;
            var previous = _spinOwner;
            _spinOwner = this;
            if (previous != null && !ReferenceEquals(previous, this)) previous.ReleaseSpinFrames();
            _baseSource = Artwork.Source;
            _spinFrames = frames;
            _spinIndex = 0;
            _spinBase = 0;
        }
        catch { }
        finally { _spinLoading = false; }
    }

    /// <summary>Suelta las vistas (vuelve al arte principal) al salir el puntero.</summary>
    private void ReleaseSpinFrames()
    {
        if (Draggable || _pointerId != null || _spinLoading) return;
        if (ReferenceEquals(_spinOwner, this)) _spinOwner = null;
        if (_spinFrames == null) return;
        _spinFrames = null;
        _spinIndex = 0;
        if (_baseSource != null) Artwork.Source = _baseSource;
    }

    private void Spin(double dx)
    {
        var frames = _spinFrames;
        if (frames is not { Count: > 1 }) return;
        int step = (int)Math.Round(dx / SpinStepPx);
        if (step == _spinStep) return;
        _spinStep = step;
        int n = frames.Count;
        _spinIndex = ((_spinBase + step) % n + n) % n;
        Artwork.Source = frames[_spinIndex];
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
    /// cortar la inclinación de un cuadro al otro.</summary>
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
