using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WHPO.Core.Services.Interfaces;
using Windows.Foundation;
using Color = System.Drawing.Color;
using Point = Windows.Foundation.Point;
using Size = System.Drawing.Size;

namespace WHPO_UI.Controls;

/// <summary>
/// Hardware chrome only. Hit testing, insertion coordinates and launching remain
/// owned by the library page. No theme transparency applies to this plastic.
/// </summary>
public static class CartridgeDock
{
    // The shell is static: reuse it across running-state/hover updates, not per frame.
    private static readonly Dictionary<(int W, int H, int Port, string Skin), BitmapImage> ShellCache = new();

    // Luz del dock: apagada mantiene el tamaño de siempre; encendida crece un poco
    // (LED completo, bisel incluido) para que el ojo la registre de reojo sin que
    // deje de ser un LED de consola.
    /// <summary>
    /// Tamaño ÚNICO del LED. Antes el apagado medía 16 y el encendido 20, así que la luz
    /// crecía al encenderse y empujaba el texto del pie 4 px. Lo que cambia es el color,
    /// no el tamaño.
    /// </summary>
    private const double LitLedSize = 20;

    /// <summary>
    /// Chasis del dock. <paramref name="portWidth"/> es el ancho de la boca/ranura
    /// (la misma medida que el cartucho que entra): el chasis se pinta alrededor para
    /// que el pozo grabado siempre enmarque a la ranura, sin importar su tamaño.
    /// </summary>
    public static Grid CreateShell(Grid controls, double height, double portWidth)
    {
        var image = new Microsoft.UI.Xaml.Controls.Image
        {
            Stretch = Stretch.Fill, IsHitTestVisible = false
        };
        var shell = new Grid
        {
            Height = height, VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                image,
                // Padding superior 7 (no 10): la boca XAML queda a y=7..33, clavada con el
                // pozo pintado (inset y=7..33). Con 10 la boca caía 3 px por debajo del receso.
                new Border
                {
                    Tag = "no-reveal", Padding = new Thickness(28, 7, 28, 10),
                    BorderThickness = new Thickness(1, 1, 1, 0), Child = controls
                }
            }
        };
        void Refresh()
        {
            if (shell.ActualWidth <= 0) return;
            double scale = shell.XamlRoot?.RasterizationScale ?? 1;
            int w = Math.Max(1, (int)Math.Ceiling(shell.ActualWidth * scale));
            int h = Math.Max(1, (int)Math.Ceiling(height * scale));
            int port = Math.Max(1, (int)Math.Round(portWidth * scale));
            // La apariencia 3D (CardSkin) también forma parte de la clave: el chasis se
            // repinta con el plástico elegido en Configuración → Apariencia.
            var key = (w, h, port, CardSkin.Current.Key);
            if (!ShellCache.TryGetValue(key, out var bitmap))
            {
                using var body = PaintShell(w, h, portWidth);
                using var png = new MemoryStream();
                body.Save(png, ImageFormat.Png);
                png.Position = 0;
                using var stream = png.AsRandomAccessStream();
                bitmap = new BitmapImage();
                bitmap.SetSource(stream);
                if (ShellCache.Count >= 4) ShellCache.Clear();
                ShellCache[key] = bitmap;
            }
            image.Source = bitmap;
        }
        shell.Loaded += (_, _) => Refresh();
        shell.SizeChanged += (_, _) => Refresh();
        return shell;
    }

    /// <summary>
    /// Pozo del conector a <paramref name="width"/> de ancho (el de la card 3D):
    /// los contactos se reparten para cubrir la boca y no quedar amontonados en el
    /// centro cuando la ranura es ancha.
    /// </summary>
    public static Border CreatePort(bool connected, double width)
    {
        var contacts = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5),
            Opacity = connected ? 0 : 0.85
        };
        int cantidadContactos = Math.Clamp((int)Math.Round((width * 0.55 + 3) / 6), 12, 40);
        for (int i = 0; i < cantidadContactos; i++)
            contacts.Children.Add(new Border
            {
                Width = 3, Height = 5, CornerRadius = new CornerRadius(0.5),
                Background = Gradient((0, "#FF443521"), (0.4, "#FFB39356"), (1, "#FF5E492B"))
            });
        var well = new Grid
        {
            Children =
            {
                // Pozo profundo donde entra la base del cartucho (detrás del cartucho)
                new Border
                {
                    Margin = new Thickness(3, 2, 3, 2), CornerRadius = new CornerRadius(3),
                    Background = Gradient((0, "#FF020305"), (0.55, "#FF07090D"), (1, "#FF11141B"))
                },
                contacts,
                new Border
                {
                    Width = 3, Margin = new Thickness(3, 2, 0, 3), HorizontalAlignment = HorizontalAlignment.Left,
                    Background = Brush("#FF272B33")
                },
                new Border
                {
                    Width = 3, Margin = new Thickness(0, 2, 3, 3), HorizontalAlignment = HorizontalAlignment.Right,
                    Background = Brush("#FF272B33")
                }
            },
            IsHitTestVisible = false
        };
        var skin = CardSkin.Current;
        return new Border
        {
            Width = width, Height = 26, CornerRadius = new CornerRadius(5),
            Background = Gradient((0, skin.Dark(0.78f)), (0.45, skin.Dark(0.22f)), (1, skin.Light(Sheen(skin) * 0.9f))),
            BorderBrush = Brush("#FF090B10"),
            BorderThickness = new Thickness(1), Child = well,
            VerticalAlignment = VerticalAlignment.Top
        };
    }

    /// <summary>
    /// Labio frontal del conector que se dibuja POR DELANTE del cartucho insertado:
    /// abraza la base y los costados de la ranura para que el cartucho se vea clavado
    /// ADENTRO del puerto (y nunca flotando ni cortado por detrás del borde superior).
    /// </summary>
    public static Grid CreatePortFrontLip(bool connected, double width)
    {
        var skin = CardSkin.Current;
        return new Grid
        {
            Width = width,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Children =
            {
                // Sombra de contacto justo donde el cartucho entra al labio inferior
                new Border
                {
                    Height = 3,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(8, 0, 8, 7),
                    Background = Gradient((0, "#00000000"), (1, connected
                        ? "#D8000000" : "#70000000"))
                },
                // Pared frontal inferior del puerto (tapa el corte inferior del cartucho)
                new Border
                {
                    Height = 8,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(0, 0, 5, 5),
                    Background = Gradient((0, skin.Light(Sheen(skin) * 1.1f)), (0.28, skin.Dark(0.02f)), (1, skin.Dark(0.55f))),
                    BorderBrush = Brush("#FF090B10"),
                    BorderThickness = new Thickness(1, 1, 1, 1),
                    Child = new Border
                    {
                        Height = 1,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(4, 0, 4, 0),
                        Background = Brush("#66DCE6F7")
                    }
                },
                // Guías laterales del puerto que abrazan los costados del cartucho
                new Border
                {
                    Width = 9,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Margin = new Thickness(0, 4, 0, 0),
                    CornerRadius = new CornerRadius(2, 0, 0, 5),
                    Background = Gradient((0, skin.Dark(0.20f)), (0.65, skin.Dark(0.02f)), (1, skin.Dark(0.55f))),
                    BorderBrush = Brush("#FF090B10"),
                    BorderThickness = new Thickness(1, 0, 1, 1)
                },
                new Border
                {
                    Width = 9,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Margin = new Thickness(0, 4, 0, 0),
                    CornerRadius = new CornerRadius(0, 2, 5, 0),
                    Background = Gradient((0, skin.Dark(0.20f)), (0.65, skin.Dark(0.02f)), (1, skin.Dark(0.55f))),
                    BorderBrush = Brush("#FF090B10"),
                    BorderThickness = new Thickness(1, 0, 1, 1)
                }
            }
        };
    }

    public static Grid CreateLed(bool connected, bool running, bool failed)
    {
        // Verde en cuanto hay un cartucho puesto (insertado o ya corriendo): es la señal de
        // "la consola tiene un juego". Gris en espera y rojo si el último lanzamiento falló.
        string core = failed ? "#FFE07059" : running || connected ? "#FF83E8AE" : "#FF62746B";
        string shade = failed ? "#FF4B211C" : running || connected ? "#FF1E4A38" : "#FF213C32";
        return BuildLed(core, shade, LitLedSize);
    }

    /// <summary>
    /// Luz del estado de la regla del juego. Es un LED distinto del de la consola:
    /// la consola dice "hay un juego montado"; esta dice qué pasó con su regla.
    /// Amarillo mientras se espera el proceso real (verde solo cuando se aplicó),
    /// ámbar cuando se aplicó con limitaciones y rojo si venció la espera.
    /// </summary>
    public static Grid CreateRuleLed(GameRuleState state, bool limited, double size = LitLedSize)
    {
        string core = state switch
        {
            GameRuleState.Applied when limited => "#FFE0A85C",
            GameRuleState.Applied => "#FF83E8AE",
            GameRuleState.Waiting => "#FFF2C230",
            GameRuleState.Failed => "#FFE07059",
            _ => "#FF62746B"
        };
        string shade = state switch
        {
            GameRuleState.Applied when limited => "#FF4A3A1B",
            GameRuleState.Applied => "#FF1E4A38",
            GameRuleState.Waiting => "#FF4A3A1B",
            GameRuleState.Failed => "#FF4B211C",
            _ => "#FF213C32"
        };
        return BuildLed(core, shade, size);
    }

    /// <summary>
    /// LED biselado del dock. <paramref name="size"/> permite ajustar la luz de la
    /// regla manteniendo las proporciones; las luces encendidas usan el tamaño grande
    /// para no encogerse cuando el dock pasa del estado de consola al de la regla.
    /// </summary>
    private static Grid BuildLed(string core, string shade, double size = 16)
    {
        double s = size / 16.0;
        return new Grid
        {
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    CornerRadius = new CornerRadius(size / 2),
                    Background = Gradient((0, "#FF101319"), (1, "#FF636A76")),
                    Padding = new Thickness(3 * s),
                    Child = new Border
                    {
                        CornerRadius = new CornerRadius(5 * s), Background = Gradient((0, core), (1, shade)),
                        BorderBrush = Brush("#FF080A0D"), BorderThickness = new Thickness(0.5)
                    }
                },
                new Border
                {
                    Width = 4 * s, Height = 2 * s, CornerRadius = new CornerRadius(1 * s),
                    Margin = new Thickness(5 * s, 4 * s, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Background = Brush("#A0FFFFFF"), IsHitTestVisible = false
                }
            }
        };
    }

    /// <summary>
    /// Botón del pie: una pieza moldeada del MISMO plástico del chasis, un escalón más
    /// oscura que la cara donde apoya para leerse como tecla empotrada. Antes era un
    /// degradé grafito fijo, así que en cualquier apariencia que no fuera la Oscura el
    /// botón quedaba como un chip de otro material encima de la consola.
    /// </summary>
    public static void StyleButton(Button button, bool eject = false)
    {
        var skin = CardSkin.Current;
        float sheen = Sheen(skin);
        bool light = skin.IsLight;

        SolidColorBrush Solid(Color color) => new(CardSkin.ToUi(color));

        button.CornerRadius = new CornerRadius(5);
        // Cara de la tecla: arriba el plástico casi puro (brillo de canto), abajo hundido.
        button.Background = Gradient(
            (0, light ? skin.Dark(0.05f) : skin.Light(sheen * 1.15f)),
            (0.55, light ? skin.Dark(0.12f) : skin.Dark(0.20f)),
            (1, light ? skin.Dark(0.20f) : skin.Dark(0.46f)));
        // Filo: aclarado arriba, sombra abajo (el borde inferior de 2 px es el que da el relieve).
        button.BorderBrush = Gradient(
            (0, light ? skin.Dark(0.28f) : skin.Light(sheen * 1.9f)),
            (1, light ? skin.Dark(0.46f) : skin.Dark(0.66f)));
        button.BorderThickness = new Thickness(1, 1, 1, 2);
        button.Foreground = TextBrush(secondary: eject);
        button.Resources["ButtonBackgroundPointerOver"] = Gradient(
            (0, light ? skin.Dark(0.03f) : skin.Light(sheen * 2.1f)),
            (1, light ? skin.Dark(0.16f) : skin.Dark(0.32f)));
        button.Resources["ButtonBackgroundPressed"] = Gradient(
            (0, light ? skin.Dark(0.24f) : skin.Dark(0.62f)),
            (1, light ? skin.Dark(0.15f) : skin.Dark(0.34f)));
        button.Resources["ButtonBorderBrushPointerOver"] = Solid(light ? skin.Dark(0.42f) : skin.Light(sheen * 2.6f));
        button.Resources["ButtonBorderBrushPressed"] = Solid(light ? skin.Dark(0.55f) : skin.Dark(0.75f));
        button.Resources["ButtonForegroundPointerOver"] = TextBrush();
        button.Resources["ButtonForegroundPressed"] = TextBrush();
    }

    /// <summary>
    /// Dial "Modo rendimiento" cuando vive en el pie: la perilla se pinta con el
    /// plástico del chasis (antes era un disco grafito fijo en cualquier apariencia),
    /// el tick pasa al acento del tema y las etiquetas Off/On usan la tinta del dock
    /// en vez de la del tema —necesario con un color exacto claro, donde la tinta
    /// clara del tema quedaba invisible sobre el plástico—.
    /// Los nombres son los del template (PerformanceSwitchStyle en Toggles.xaml).
    /// </summary>
    public static void StyleSwitch(ToggleButton dial)
    {
        // El template puede no estar instanciado si la card se mueve al pie antes del
        // primer layout (arranque directo en modo 3D): ApplyTemplate lo resuelve sin
        // esperar a que la ventana mida.
        dial.ApplyTemplate();
        var skin = CardSkin.Current;
        bool light = skin.IsLight;
        float sheen = Sheen(skin);

        if (FindPart(dial, "DialRing") is Border ring)
            ring.Background = new SolidColorBrush(CardSkin.ToUi(skin.Dark(light ? 0.55f : 0.80f)));
        if (FindPart(dial, "Face") is Border face)
            face.Background = Gradient(
                (0, light ? skin.Dark(0.12f) : skin.Light(sheen * 1.55f)),
                (1, light ? skin.Dark(0.40f) : skin.Dark(0.55f)));
        if (FindPart(dial, "Indicator") is Border tick)
            tick.Background = ThemeBrushes.Get("AccentBrush");
        if (FindPart(dial, "OffLabel") is TextBlock off) off.Foreground = TextBrush();
        if (FindPart(dial, "OnLabel") is TextBlock on) on.Foreground = TextBrush();
    }

    /// <summary>Devuelve el dial a los colores del mock (header): mismo código que el template.</summary>
    public static void ResetSwitch(ToggleButton dial)
    {
        dial.ApplyTemplate();
        if (FindPart(dial, "DialRing") is Border ring)
            ring.Background = Brush("#FF17171B");
        if (FindPart(dial, "Face") is Border face)
            face.Background = Gradient((0, "#FF3A3A3E"), (1, "#FF1D1D20"));
        if (FindPart(dial, "Indicator") is Border tick)
            tick.Background = Brush("#FF3B7FAA");
        if (FindPart(dial, "OffLabel") is TextBlock off) off.Foreground = ThemeBrushes.Get("PrimaryTextBrush");
        if (FindPart(dial, "OnLabel") is TextBlock on) on.Foreground = ThemeBrushes.Get("PrimaryTextBrush");
    }

    /// <summary>Busca una parte del template por x:Name en el árbol visual ya aplicado.</summary>
    private static FrameworkElement? FindPart(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindPart(child, name) is { } nested) return nested;
        }
        return null;
    }

    public static void StyleWell(Border well)
    {
        well.Tag = "no-reveal";
        well.CornerRadius = new CornerRadius(0);
        well.Padding = new Thickness(0);
        well.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        well.BorderThickness = new Thickness(0);
        well.ClearValue(Border.BorderBrushProperty);
    }

    public static void ClearDockResources(FrameworkElement element)
    {
        element.Resources.Clear();
    }

    /// <summary>
    /// Vista previa de la apariencia 3D para Configuración → Apariencia: la card de la
    /// biblioteca (la misma cara de muestra) junto a la consola REAL —chasis, puerto,
    /// labio frontal, LED y textos de <see cref="CartridgeDock"/>— con el plástico vigente
    /// de <see cref="CardSkin"/>. Replica la geometría del dock de la biblioteca (66 DIP
    /// de alto, boca a 8, el cartucho entra 20 DIP y asoma ~15 %) para que la previa no
    /// sea un dibujo aparte: es el mismo material, pintado por el mismo código.
    /// </summary>
    public static FrameworkElement CreateAppearancePreview(ImageSource? card, double previewHeight = 320)
    {
        // Tamaño REAL del dock de la biblioteca, no uno inventado para la previa: el ancho
        // es el de la card 3D en la ventana estándar y el alto del chasis es 66. Todo el
        // escenario se dibuja a esa escala y después se reduce con un Viewbox, así las
        // proporciones del pie, la ranura y el cartucho quedan idénticas a la biblioteca.
        const double cardWidth = 197; // MaxCardWidth3D (205) - 8
        const double stageWidth = 760;
        double cardHeight = Math.Ceiling(cardWidth * CartridgeModel.Aspect);

        // Medidas del dock de la biblioteca (GestionarProcesosPage).
        const double FooterHeight = 66;
        const double MouthY = 8;
        const double SlotDepth = 20;
        const double ApproachGap = 6;
        const double LipWall = 9;
        const double BaySideGap = 12;

        double portWidth = cardWidth + LipWall * 2;
        double bayWidth = portWidth + BaySideGap * 2;
        double bayHeight = cardHeight + ApproachGap;
        double protrusion = Math.Round(cardHeight * 0.15);
        double seatedTop = bayHeight - (protrusion + SlotDepth);

        // Card suelta, como se ve en la grilla de la biblioteca.
        var fullCard = new Grid
        {
            Width = cardWidth, Height = cardHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0)
        };
        if (card != null)
            fullCard.Children.Add(new Microsoft.UI.Xaml.Controls.Image { Source = card, Stretch = Stretch.Fill });

        // Mismo asiento que el dock real: el canvas recortado deja el cartucho dentro de
        // la ranura, con solo la torre asomando por encima de la boca.
        var bay = new Canvas
        {
            Width = bayWidth, Height = bayHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, (MouthY + SlotDepth) - bayHeight, 0, 0),
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, bayWidth, bayHeight) }
        };
        if (card != null)
        {
            var seated = new Microsoft.UI.Xaml.Controls.Image
            {
                Source = card, Width = cardWidth, Height = cardHeight, Stretch = Stretch.Fill
            };
            Canvas.SetLeft(seated, (bayWidth - cardWidth) * 0.5);
            Canvas.SetTop(seated, seatedTop);
            bay.Children.Add(seated);
        }

        // Ala izquierda: los mismos textos y el mismo LED que la consola de la biblioteca.
        var line1 = new TextBlock
        {
            Text = "WINFORGE", FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextBrush(), TextTrimming = TextTrimming.CharacterEllipsis
        };
        var line2 = new TextBlock
        {
            Text = "GAME CARD", FontSize = 9, CharacterSpacing = 60,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TextBrush(secondary: true), TextTrimming = TextTrimming.CharacterEllipsis
        };
        var status = new StackPanel
        {
            Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { line1, line2 }
        };
        var leftWing = new Grid { ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        leftWing.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftWing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftWing.Children.Add(CreateLed(connected: true, running: false, failed: false));
        Grid.SetColumn(status, 1);
        leftWing.Children.Add(status);

        var port = new Grid { Width = portWidth, Height = 44 };
        var portBorder = CreatePort(connected: true, portWidth);
        portBorder.VerticalAlignment = VerticalAlignment.Top;
        port.Children.Add(portBorder);

        // Tres columnas como el dock real (ala / ranura / ala): la ranura queda centrada.
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftWing, 0);
        Grid.SetColumn(port, 1);
        row.Children.Add(leftWing);
        row.Children.Add(port);

        var shell = CreateShell(row, FooterHeight, portWidth);
        var lip = CreatePortFrontLip(connected: true, portWidth);
        lip.Margin = new Thickness(0, MouthY, 0, 0);

        var dock = new Grid { Height = FooterHeight, VerticalAlignment = VerticalAlignment.Bottom };
        dock.Children.Add(shell);
        dock.Children.Add(bay);
        dock.Children.Add(lip);

        var stage = new Grid
        {
            Width = stageWidth,
            Height = cardHeight + FooterHeight + 80 // aire entre la card y la torre que asoma
        };
        stage.Children.Add(fullCard);
        stage.Children.Add(dock);

        return new Viewbox
        {
            Child = stage,
            Stretch = Stretch.Uniform,
            Height = previewHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static SolidColorBrush TextBrush(bool secondary = false)
    {
        // En plástico claro (Blanco) el texto del dock va en oscuro: el gris claro de
        // siempre quedaría casi invisible sobre la consola blanca.
        if (CardSkin.Current.IsLight)
            return Brush(secondary ? "#FF4A5059" : "#FF1A1D22");
        return Brush(secondary ? "#FF9DA5B1" : "#FFE1E6EE");
    }

    private static SolidColorBrush Brush(string hex) => new(ToUiColor(hex));
    private static Windows.UI.Color ToUiColor(string hex)
    {
        uint argb = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }
    private static Microsoft.UI.Xaml.Media.LinearGradientBrush Gradient(params (double Offset, string Color)[] stops)
    {
        var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (offset, color) in stops)
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = ToUiColor(color) });
        return brush;
    }

    private static Microsoft.UI.Xaml.Media.LinearGradientBrush Gradient(params (double Offset, Color Color)[] stops)
    {
        var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (offset, color) in stops)
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = CardSkin.ToUi(color) });
        return brush;
    }

    /// <summary>
    /// Aclarado del canto superior del chasis, proporcional al plástico: un negro
    /// (luminancia ~27) se aclara apenas un 5 %, un blanco (~223) el ~13 % de siempre.
    /// Antes era un 14 % fijo y en la apariencia Oscura la consola salía gris plateada
    /// —ni el negro de las cards ni el del fondo—, como si fuera de otro material:
    /// un negro anodizado tiene brillo de CANTO, no de superficie.
    /// </summary>
    private static float Sheen(CardSkin.Palette skin) => 0.04f + 0.10f * (skin.Luminance / 255f);

    /// <summary>Profundidad del degradé del chasis: cuanto más oscuro el plástico, más negro abajo.</summary>
    private static float Depth(CardSkin.Palette skin) => 0.55f + 0.15f * (1f - skin.Luminance / 255f);

    /// <summary>Opacidad del filo iluminado del borde superior (0..255): un negro solo insinúa el brillo.</summary>
    private static int RimAlpha(CardSkin.Palette skin) => (int)(30 + 70 * (skin.Luminance / 255f));

    /// <summary>Opacidad del reflejo de los cantos laterales (0..255).</summary>
    private static int SideGlowAlpha(CardSkin.Palette skin) => (int)(14 + 24 * (skin.Luminance / 255f));

    private static Bitmap PaintShell(int pixelWidth, int pixelHeight, double portWidth)
    {
        // Paint in 66-DIP reference coordinates, rasterized at the window's DPI.
        var bmp = new Bitmap(pixelWidth, pixelHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        float scale = pixelHeight / 66f;
        float width = pixelWidth / scale;
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Chasis del plástico elegido: claro arriba, casi negro abajo, como una pieza moldeada.
        var skin = CardSkin.Current;
        using var silhouette = TopRounded(new RectangleF(0.5f, 1, width - 1, 65), 11);
        using (var body = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(0, 0, width, 66),
            skin.Light(Sheen(skin)), skin.Dark(Depth(skin)), 90f))
            g.FillPath(body, silhouette);
        var state = g.Save();
        g.SetClip(silhouette);
        // Deterministic matte grain: subtle, static and independent of game state.
        var random = new Random(42);
        using var lightGrain = new SolidBrush(Color.FromArgb(9, 226, 232, 244));
        using var darkGrain = new SolidBrush(Color.FromArgb(16, 0, 0, 0));
        for (int i = 0; i < width * 4; i++)
            g.FillRectangle(i % 2 == 0 ? lightGrain : darkGrain,
                (float)random.NextDouble() * width, (float)random.NextDouble() * 65, 0.6f, 0.6f);
        // Lower moulded face: depth, seam and a narrow reflected bevel anchored to the bottom.
        using (var face = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(0, 48, width, 18),
            skin.Dark(0.08f), skin.Dark(Math.Min(0.9f, Depth(skin) + 0.10f)), 90f))
            g.FillRectangle(face, 0, 49, width, 18);
        using (var seam = new Pen(Color.FromArgb(190, 8, 10, 14), 1)) g.DrawLine(seam, 7, 49, width - 7, 49);
        using (var faceLight = new Pen(Color.FromArgb(50, 164, 175, 193), 1)) g.DrawLine(faceLight, 10, 50, width - 10, 50);
        // Recess around the port, wider than the actual XAML mouth. Se deriva del ancho
        // del conector (el de la card 3D) para que el marco grabado lo siga siempre.
        float recessW = (float)Math.Max(96, portWidth) + 22f;
        using var portPath = Rounded(new RectangleF(width * 0.5f - recessW * 0.5f, 5, recessW, 31), 6);
        using (var bevel = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(0, 5, 1, 31),
            skin.Dark(0.70f), skin.Light(Sheen(skin) * 1.5f), 90f))
            g.FillPath(bevel, portPath);
        using (var groove = new Pen(Color.FromArgb(205, 9, 11, 16), 1)) g.DrawPath(groove, portPath);
        float insetW = (float)Math.Max(88, portWidth) - 4f;
        using (var inset = Rounded(new RectangleF(width * 0.5f - insetW * 0.5f, 7, insetW, 26), 4))
        using (var plastic = new SolidBrush(skin.Dark(0.30f))) g.FillPath(plastic, inset);
        // Side vents carved into the front, away from interactive content.
        // Las rejillas laterales se corren hacia afuera con el pozo: si quedan fijas,
        // una ranura ancha las taparía y el chasis perdería el detalle lateral.
        float ventShift = Math.Max(0, (recessW - 118f) * 0.5f);
        for (int side = -1; side <= 1; side += 2)
        {
            float x0 = side < 0 ? width * 0.5f - (172 + ventShift) : width * 0.5f + 96 + ventShift;
            for (int i = 0; i < 9; i++)
            {
                float x = x0 + i * 8;
                using var vent = Rounded(new RectangleF(x, 54, 4, 5), 1);
                using var hole = new SolidBrush(Color.FromArgb(8, 10, 14));
                g.FillPath(hole, vent);
                using var lip = new Pen(Color.FromArgb(55, 125, 137, 154), 0.6f);
                g.DrawLine(lip, x + 1, 59, x + 3, 59);
            }
        }
        // Countersunk cross-head screws, inset into the corner shoulders.
        foreach (float x in new[] { 12f, width - 12 })
        {
            using var recess = new SolidBrush(Color.FromArgb(13, 16, 22));
            g.FillEllipse(recess, x - 4, 28, 8, 8);
            using var screw = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(x - 3, 29, 6, 6),
                Color.FromArgb(88, 94, 107), Color.FromArgb(29, 33, 41), 90f);
            g.FillEllipse(screw, x - 3, 29, 6, 6);
            using var slot = new Pen(Color.FromArgb(11, 14, 20), 1);
            g.DrawLine(slot, x - 1.6f, 32, x + 1.6f, 32);
            g.DrawLine(slot, x, 30.4f, x, 33.6f);
        }
        g.Restore(state);
        using (var edge = new Pen(Color.FromArgb(205, 10, 12, 17), 1)) g.DrawPath(edge, silhouette);
        using (var topLight = new Pen(Color.FromArgb(RimAlpha(skin), 170, 181, 199), 1)) g.DrawLine(topLight, 15, 2, width - 15, 2);
        // A second shoulder bevel avoids the appearance of a flat rounded rectangle.
        using (var bevelLight = new Pen(Color.FromArgb(SideGlowAlpha(skin), 216, 226, 244), 1))
        {
            g.DrawLine(bevelLight, 3, 12, 3, 65);
            g.DrawLine(bevelLight, width - 3, 12, width - 3, 65);
        }
        return bmp;
    }

    private static GraphicsPath TopRounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddLine(r.Left, r.Bottom, r.Left, r.Top + d * 0.5f);
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddLine(r.Right, r.Top + d * 0.5f, r.Right, r.Bottom);
        p.CloseFigure();
        return p;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
