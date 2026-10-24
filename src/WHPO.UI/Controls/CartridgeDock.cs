using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private static readonly Dictionary<(int W, int H, int Port), BitmapImage> ShellCache = new();

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
            var key = (w, h, port);
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
        return new Border
        {
            Width = width, Height = 26, CornerRadius = new CornerRadius(5),
            Background = Gradient((0, "#FF0B0D12"), (0.45, "#FF262A34"), (1, "#FF484E5B")),
            BorderBrush = Brush("#FF090B10"), BorderThickness = new Thickness(1), Child = well,
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
                    Background = Gradient((0, "#00000000"), (1, connected ? "#D8000000" : "#70000000"))
                },
                // Pared frontal inferior del puerto (tapa el corte inferior del cartucho)
                new Border
                {
                    Height = 8,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(0, 0, 5, 5),
                    Background = Gradient((0, "#FF525866"), (0.28, "#FF323743"), (1, "#FF181C24")),
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
                    Background = Gradient((0, "#FF262B35"), (0.65, "#FF323743"), (1, "#FF181C24")),
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
                    Background = Gradient((0, "#FF262B35"), (0.65, "#FF323743"), (1, "#FF181C24")),
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
        return BuildLed(core, shade);
    }

    /// <summary>
    /// Luz del estado de la regla del juego. Es un LED distinto del de la consola:
    /// la consola dice "hay un juego montado"; esta dice qué pasó con su regla.
    /// Amarillo mientras se espera el proceso real (verde solo cuando se aplicó),
    /// ámbar cuando se aplicó con limitaciones y rojo si venció la espera.
    /// </summary>
    public static Grid CreateRuleLed(GameRuleState state, bool limited, double size = 16)
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
    /// LED biselado del dock. <paramref name="size"/> permite la segunda luz de la
    /// regla, un poco más chica que la del encendido, manteniendo las proporciones.
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

    public static void StyleButton(Button button, bool eject = false)
    {
        button.CornerRadius = new CornerRadius(5);
        button.Background = Gradient((0, "#FF414651"), (0.15, "#FF303540"), (1, "#FF1B1F27"));
        button.BorderBrush = Gradient((0, "#FF69717D"), (0.3, "#FF383E48"), (1, "#FF0D1015"));
        button.BorderThickness = new Thickness(1, 1, 1, 2);
        button.Foreground = Brush(eject ? "#FFE9C2AC" : "#FFDCE2EA");
        button.Resources["ButtonBackgroundPointerOver"] = Gradient((0, "#FF4C5461"), (1, "#FF252B35"));
        button.Resources["ButtonBackgroundPressed"] = Gradient((0, "#FF10141B"), (1, "#FF292F39"));
        button.Resources["ButtonBorderBrushPointerOver"] = Brush("#FF8A94A3");
        button.Resources["ButtonBorderBrushPressed"] = Brush("#FF13171F");
        button.Resources["ButtonForegroundPointerOver"] = Brush("#FFF2F5FA");
        button.Resources["ButtonForegroundPressed"] = Brush("#FFC5CDDA");
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

    public static SolidColorBrush TextBrush(bool secondary = false)
        => Brush(secondary ? "#FF9DA5B1" : "#FFE1E6EE");

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

    private static Bitmap PaintShell(int pixelWidth, int pixelHeight, double portWidth)
    {
        // Paint in 66-DIP reference coordinates, rasterized at the window's DPI.
        var bmp = new Bitmap(pixelWidth, pixelHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        float scale = pixelHeight / 66f;
        float width = pixelWidth / scale;
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var silhouette = TopRounded(new RectangleF(0.5f, 1, width - 1, 65), 11);
        using (var body = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(0, 0, width, 66),
            Color.FromArgb(59, 64, 74), Color.FromArgb(22, 25, 32), 90f))
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
            Color.FromArgb(37, 41, 50), Color.FromArgb(12, 15, 20), 90f))
            g.FillRectangle(face, 0, 49, width, 18);
        using (var seam = new Pen(Color.FromArgb(190, 8, 10, 14), 1)) g.DrawLine(seam, 7, 49, width - 7, 49);
        using (var faceLight = new Pen(Color.FromArgb(50, 164, 175, 193), 1)) g.DrawLine(faceLight, 10, 50, width - 10, 50);
        // Recess around the port, wider than the actual XAML mouth. Se deriva del ancho
        // del conector (el de la card 3D) para que el marco grabado lo siga siempre.
        float recessW = (float)Math.Max(96, portWidth) + 22f;
        using var portPath = Rounded(new RectangleF(width * 0.5f - recessW * 0.5f, 5, recessW, 31), 6);
        using (var bevel = new System.Drawing.Drawing2D.LinearGradientBrush(new RectangleF(0, 5, 1, 31),
            Color.FromArgb(14, 17, 23), Color.FromArgb(91, 97, 109), 90f))
            g.FillPath(bevel, portPath);
        using (var groove = new Pen(Color.FromArgb(205, 9, 11, 16), 1)) g.DrawPath(groove, portPath);
        float insetW = (float)Math.Max(88, portWidth) - 4f;
        using (var inset = Rounded(new RectangleF(width * 0.5f - insetW * 0.5f, 7, insetW, 26), 4))
        using (var plastic = new SolidBrush(Color.FromArgb(32, 36, 44))) g.FillPath(plastic, inset);
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
        using (var topLight = new Pen(Color.FromArgb(100, 170, 181, 199), 1)) g.DrawLine(topLight, 15, 2, width - 15, 2);
        // A second shoulder bevel avoids the appearance of a flat rounded rectangle.
        using (var bevelLight = new Pen(Color.FromArgb(38, 216, 226, 244), 1))
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
