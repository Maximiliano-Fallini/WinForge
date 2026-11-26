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

    // Geometría del pie flotante, tomada del Dock.qml de Carthage: radio 22, la
    // hendidura (saddle) de 11 px donde entra el cartucho y curvas de 22 px.
    private const double FloatingRadius = 22;
    public const double FloatingDip = 11;
    private const double FloatingCurve = 22;

    /// <summary>
    /// Variante flotante del pie, con la MISMA silueta del dock de Carthage: una barra
    /// de vidrio con la parte superior hundida donde entra el cartucho, en vez del
    /// chasis plástico con el conector de la consola. El contenido (estado y Modo
    /// rendimiento) se centra en el cuerpo visible debajo de la hendidura; con cartucho,
    /// este baja hasta la línea del asiento y una sombra lo funde con el vidrio.
    /// </summary>
    public static FrameworkElement CreateFloatingShell(Grid controls, double height, double width, double portWidth, bool connected = false)
    {
        double barWidth = Math.Max(320, width);
        // El hueco de arriba es un poco más ancho que la boca, como en Carthage: la
        // ranura queda dentro de la parte plana y las curvas no la tocan.
        double dipWidth = Math.Max(portWidth + 8, 88);
        var geometry = BuildFloatingGeometry(barWidth, height, dipWidth);
        var back = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = barWidth,
            Height = height,
            Data = geometry,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Fill = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(0xF4, 0x10, 0x15, 0x1D) },
                    new GradientStop { Offset = 0.7, Color = Windows.UI.Color.FromArgb(0xF4, 0x0A, 0x0D, 0x13) },
                    new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0xF4, 0x07, 0x09, 0x0D) }
                }
            },
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1
        };
        // El contenido se centra en la PARTE VISIBLE del vidrio, no en los 66 DIP
        // completos: la hendidura se come los primeros 11 y si no el bloque quedaba
        // 5,5 DIP alto respecto del cuerpo de la barra (Carthage centra debajo del
        // asiento). El margen lateral deja aire contra las curvas.
        controls.Margin = new Thickness(22, FloatingDip, 22, 0);
        var content = new Grid { Width = barWidth, Height = height, Children = { controls } };

        // Brillo superior y sombra de apoyo dentro del vidrio: dos rellenos con la
        // MISMA silueta, así el degradado no invade la hendidura ni la torre del
        // cartucho. Es lo que separa un rectángulo oscuro de una pieza de vidrio.
        var sheen = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = barWidth,
            Height = height,
            Data = geometry,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Fill = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF) },
                    new GradientStop { Offset = 0.45, Color = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF) }
                }
            }
        };
        var bottomShade = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = barWidth,
            Height = height,
            Data = geometry,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Fill = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Offset = 0.62, Color = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00) },
                    new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00) }
                }
            }
        };
        var mouth = CreateFloatingMouth(height, portWidth, connected);
        var host = new Grid { Width = barWidth, Height = height, Children = { back, sheen, bottomShade, content, mouth } };
        // Sombra de contacto: una placa redondeada corrida hacia abajo, detrás del
        // vidrio. No es un blur (WinUI no lo trae acá), pero separa la barra del fondo.
        var shadow = new Border
        {
            Width = Math.Max(1, barWidth - 8),
            Height = Math.Max(1, height - 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            CornerRadius = new CornerRadius(FloatingRadius + 2),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0x00, 0x00, 0x00)),
            IsHitTestVisible = false
        };
        return new Grid { Height = height, Children = { shadow, host } };
    }

    /// <summary>Silueta de la barra flotante: rectángulo redondeado con la hendidura
    /// superior de Carthage. La boca y el labio se dibujan aparte, encima.</summary>
    private static Geometry BuildFloatingGeometry(double w, double h, double dipWidth)
    {
        double r = FloatingRadius, dip = FloatingDip, curve = FloatingCurve;
        double flat = Math.Max(80, Math.Min(dipWidth, w - 2 * (r + curve)));
        double x0 = (w - flat) / 2, x1 = x0 + flat;
        var figure = new PathFigure { StartPoint = new Point(r, 0), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = new Point(x0 - curve, 0) });
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(x0 - curve / 2, 0),
            Point2 = new Point(x0 - curve / 2, dip),
            Point3 = new Point(x0, dip)
        });
        figure.Segments.Add(new LineSegment { Point = new Point(x1, dip) });
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Point(x1 + curve / 2, dip),
            Point2 = new Point(x1 + curve / 2, 0),
            Point3 = new Point(x1 + curve, 0)
        });
        figure.Segments.Add(new LineSegment { Point = new Point(w - r, 0) });
        figure.Segments.Add(new ArcSegment { Point = new Point(w, r), Size = new Windows.Foundation.Size(r, r), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(w, h - r) });
        figure.Segments.Add(new ArcSegment { Point = new Point(w - r, h), Size = new Windows.Foundation.Size(r, r), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(r, h) });
        figure.Segments.Add(new ArcSegment { Point = new Point(0, h - r), Size = new Windows.Foundation.Size(r, r), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = new Point(0, r) });
        figure.Segments.Add(new ArcSegment { Point = new Point(r, 0), Size = new Windows.Foundation.Size(r, r), SweepDirection = SweepDirection.Clockwise });
        return new PathGeometry { Figures = { figure } };
    }

    /// <summary>
    /// Boca del pie flotante. Con cartucho puesto no se dibuja una ranura POR DELANTE
    /// del cartucho: este baja hasta la línea del asiento y una sombra suave lo funde
    /// con el vidrio, como el Dock.qml de Carthage. Sin cartucho sí se ve la cavidad
    /// oscura y el filo iluminado sobre la hendidura.
    /// </summary>
    private static FrameworkElement CreateFloatingMouth(double height, double portWidth, bool connected)
    {
        double width = Math.Max(88, portWidth + 24);
        double slitWidth = Math.Max(80, portWidth - 6);
        var mouth = new Grid
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };

        if (connected)
        {
            var seatShadow = new Border
            {
                Width = Math.Max(80, portWidth - 2),
                Height = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, FloatingDip, 0, 0),
                Background = new Microsoft.UI.Xaml.Media.LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(0xC0, 0x00, 0x00, 0x00) },
                        new GradientStop { Offset = 0.45, Color = Windows.UI.Color.FromArgb(0x58, 0x00, 0x00, 0x00) },
                        new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00) }
                    }
                }
            };
            mouth.Children.Add(seatShadow);
            return mouth;
        }

        // Sin cartucho: la cavidad vive DENTRO de la hendidura (arranca en el borde
        // superior y termina en el asiento), no como una línea suelta por encima.
        var cavity = new Border
        {
            Width = slitWidth,
            Height = 6,
            CornerRadius = new CornerRadius(3, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, FloatingDip - 6, 0, 0),
            Background = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(0xE8, 0x00, 0x00, 0x00) },
                    new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0x80, 0x00, 0x00, 0x00) }
                }
            }
        };
        var lip = new Border
        {
            Width = Math.Max(72, portWidth - 10),
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, FloatingDip, 0, 0),
            Background = new Microsoft.UI.Xaml.Media.LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF) },
                    new GradientStop { Offset = 0.2, Color = Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) },
                    new GradientStop { Offset = 0.8, Color = Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) },
                    new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF) }
                }
            }
        };
        mouth.Children.Add(cavity);
        mouth.Children.Add(lip);
        return mouth;
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
        // "la consola tiene un juego". Negro apagado en espera y rojo si el último lanzamiento falló.
        string core = failed ? "#FFE07059" : running || connected ? "#FF83E8AE" : "#FF0D1014";
        string shade = failed ? "#FF4B211C" : running || connected ? "#FF1E4A38" : "#FF020305";
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
    /// Estado que muestra la luz de la ficha de un emulador en la biblioteca.
    /// </summary>
    public enum DeviceStatus
    {
        /// <summary>No hay proceso: la máquina está apagada.</summary>
        Idle,
        /// <summary>Se lanzó y el proceso todavía no apareció.</summary>
        Starting,
        /// <summary>El juego/emulador está corriendo.</summary>
        Running,
        /// <summary>No se encontró el ejecutable: no se puede iniciar.</summary>
        Missing
    }

    /// <summary>
    /// Luz de estado de la ficha de un emulador, con el mismo bisel que los LED del pie
    /// (apagada gris, ámbar mientras arranca, verde encendida, roja sin ejecutable).
    /// </summary>
    public static Grid CreateDeviceStatusLed(DeviceStatus state, double size = 14)
    {
        (string core, string shade) = state switch
        {
            DeviceStatus.Running => ("#FF83E8AE", "#FF1E4A38"),
            DeviceStatus.Starting => ("#FFF2C230", "#FF4A3A1B"),
            DeviceStatus.Missing => ("#FFE07059", "#FF4B211C"),
            _ => ("#FF62746B", "#FF213C32")
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

    /// <summary>Botón del pie FLOTANTE: no es plástico, es una tecla de vidrio sobre la
    /// barra oscura. Sin esto, en una apariencia clara los botones del Modo rendimiento
    /// quedaban como chips del chasis encima del vidrio.</summary>
    public static void StyleGlassButton(Button button)
    {
        button.CornerRadius = new CornerRadius(5);
        button.Background = Brush("#18FFFFFF");
        button.BorderBrush = Brush("#34FFFFFF");
        button.BorderThickness = new Thickness(1);
        button.Foreground = Brush("#FFE1E6EE");
        button.Resources["ButtonBackgroundPointerOver"] = Brush("#26FFFFFF");
        button.Resources["ButtonBackgroundPressed"] = Brush("#12FFFFFF");
        button.Resources["ButtonBorderBrushPointerOver"] = Brush("#58FFFFFF");
        button.Resources["ButtonBorderBrushPressed"] = Brush("#40FFFFFF");
        button.Resources["ButtonForegroundPointerOver"] = Brush("#FFFFFFFF");
        button.Resources["ButtonForegroundPressed"] = Brush("#FFFFFFFF");
    }

    /// <summary>
    /// Dial "Modo rendimiento" cuando vive en el pie: la perilla se pinta con el
    /// plástico del chasis (antes era un disco grafito fijo en cualquier apariencia),
    /// el tick pasa al acento del tema y las etiquetas Off/On usan la tinta del dock
    /// en vez de la del tema —necesario con un color exacto claro, donde la tinta
    /// clara del tema quedaba invisible sobre el plástico—.
    /// Los nombres son los del template (PerformanceSwitchStyle en Toggles.xaml).
    /// </summary>
    public static void StyleSwitch(ToggleButton dial, bool glass = false)
    {
        // El template puede no estar instanciado si la card se mueve al pie antes del
        // primer layout (arranque directo en modo 3D): ApplyTemplate lo resuelve sin
        // esperar a que la ventana mida.
        dial.ApplyTemplate();
        var skin = CardSkin.Current;
        bool light = skin.IsLight;
        float sheen = Sheen(skin);

        if (FindPart(dial, "DialRing") is Border ring)
            ring.Background = glass
                ? Brush("#CC05070B")
                : new SolidColorBrush(CardSkin.ToUi(skin.Dark(light ? 0.55f : 0.80f)));
        if (FindPart(dial, "Face") is Border face)
            face.Background = glass
                ? Gradient((0, "#FF3B4654"), (1, "#FF161C24"))
                : Gradient(
                    (0, light ? skin.Dark(0.12f) : skin.Light(sheen * 1.55f)),
                    (1, light ? skin.Dark(0.40f) : skin.Dark(0.55f)));
        if (FindPart(dial, "Indicator") is Border tick)
            tick.Background = ThemeBrushes.Get("AccentBrush");
        if (FindPart(dial, "OffLabel") is TextBlock off)
            off.Foreground = glass ? Brush("#FF9DA5B1") : TextBrush();
        if (FindPart(dial, "OnLabel") is TextBlock on)
            on.Foreground = glass ? Brush("#FFE1E6EE") : TextBrush();
        LocalizeSwitch(dial);
    }

    /// <summary>
    /// Escribe las etiquetas Off/On del dial en el idioma activo. Son literales del
    /// template (<c>PerformanceSwitchStyle</c>): nacen escritos en el XAML y el recorrido
    /// de I18n no los re-traduce al cambiar de idioma, así que quedaban clavados en el
    /// idioma con el que se armó el control. Se llaman desde los dos estilos del dial y
    /// al cambiar de idioma (ver la página de la biblioteca).
    /// </summary>
    public static void LocalizeSwitch(ToggleButton dial)
    {
        dial.ApplyTemplate();
        if (FindPart(dial, "OffLabel") is TextBlock off) off.Text = I18n.T("Off");
        if (FindPart(dial, "OnLabel") is TextBlock on) on.Text = I18n.T("On");
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
        LocalizeSwitch(dial);
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
    public static FrameworkElement CreateAppearancePreview(ImageSource? card, double previewHeight = 320, bool floatingFooter = false)
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
        // En la barra flotante el vidrio es oscuro en todos los temas: el texto del
        // chasis (que sigue al plástico y puede ser oscuro) quedaría ilegible.
        var previewText = floatingFooter
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE1, 0xE6, 0xEE))
            : TextBrush();
        var previewTextSecondary = floatingFooter
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x9D, 0xA5, 0xB1))
            : TextBrush(secondary: true);
        var line1 = new TextBlock
        {
            Text = "WINFORGE", FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = previewText, TextTrimming = TextTrimming.CharacterEllipsis
        };
        var line2 = new TextBlock
        {
            Text = "GAME CARD", FontSize = 9, CharacterSpacing = 60,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = previewTextSecondary, TextTrimming = TextTrimming.CharacterEllipsis
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

        var port = new Grid { Width = floatingFooter ? portWidth + 24 : portWidth, Height = 44 };
        if (!floatingFooter)
        {
            var portBorder = CreatePort(connected: true, portWidth);
            portBorder.VerticalAlignment = VerticalAlignment.Top;
            port.Children.Add(portBorder);
        }

        // Tres columnas como el dock real (ala / ranura / ala): la ranura queda centrada.
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftWing, 0);
        Grid.SetColumn(port, 1);
        row.Children.Add(leftWing);
        row.Children.Add(port);

        if (floatingFooter)
        {
            const double barWidth = 520;
            const double stageHeight = 250;
            double barTop = stageHeight - 14 - FooterHeight;
            var shell = CreateFloatingShell(row, FooterHeight, barWidth, portWidth, card != null);

            // La torre del cartucho asoma por la boca: la card se recorta en la línea
            // del asiento (barTop + FloatingDip), así no queda un hueco entre la card y
            // la sombra del vidrio.
            var seatedClip = new Grid
            {
                Width = cardWidth,
                Height = 50 + FloatingDip,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, barTop - 50, 0, 0),
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, cardWidth, 50 + FloatingDip) }
            };
            if (card != null)
                seatedClip.Children.Add(new Microsoft.UI.Xaml.Controls.Image
                {
                    Source = card, Width = cardWidth, Height = cardHeight,
                    Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Top
                });

            var stage = new Grid { Width = stageWidth, Height = stageHeight };
            stage.Children.Add(seatedClip);
            var dock = new Grid
            {
                Width = barWidth,
                Height = FooterHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 14),
                Children = { shell }
            };
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

        var consoleShell = CreateShell(row, FooterHeight, portWidth);
        var lip = CreatePortFrontLip(connected: true, portWidth);
        lip.Margin = new Thickness(0, MouthY, 0, 0);
        var consoleDock = new Grid { Height = FooterHeight, VerticalAlignment = VerticalAlignment.Bottom };
        consoleDock.Children.Add(consoleShell);
        consoleDock.Children.Add(bay);
        consoleDock.Children.Add(lip);

        var consoleStage = new Grid
        {
            Width = stageWidth,
            Height = cardHeight + FooterHeight + 80 // aire entre la card y la torre que asoma
        };
        consoleStage.Children.Add(fullCard);
        consoleStage.Children.Add(consoleDock);

        return new Viewbox
        {
            Child = consoleStage,
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
        // Sin contorno sobre la silueta: el chasis lo define el degradé del plástico y el
        // filo iluminado del canto superior. El trazo casi negro que había acá dibujaba un
        // reborde alrededor de todo el pie, que se leía como un marco pegado encima.
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
