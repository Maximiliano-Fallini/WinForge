using System;
using System.IO;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace WHPO_UI;

/// <summary>
/// Herramientas para armar los valores de un tema: colores, pinceles, gradientes,
/// resplandores e imágenes de fondo. Es lo que usan los archivos de <c>Themes\</c>
/// (uno por tema) para describir su configuración.
///
/// Todo acá es puro: no toca diccionarios de recursos ni el estado de la app. Quién
/// aplica lo que estos archivos definen es <see cref="ThemePalettes"/>.
/// </summary>
public static class ThemePaint
{
    /// <summary>
    /// Carpeta de las imágenes de fondo de los temas, al lado del ejecutable. Los
    /// archivos llegan ahí porque el .csproj los copia (Themes\Backgrounds).
    /// </summary>
    public static string BackgroundsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Themes", "Backgrounds");

    /// <summary>Pincel sólido a partir de un hex (#RRGGBB o #AARRGGBB).</summary>
    public static SolidColorBrush Brush(string hex)
        => new(Color(hex));

    /// <summary>Color a partir de un hex (#RRGGBB o #AARRGGBB).</summary>
    public static Windows.UI.Color Color(string hex)
    {
        var (a, r, g, b) = ParseHex(hex);
        return Windows.UI.Color.FromArgb(a, r, g, b);
    }

    /// <summary>Descompone un hex en alfa+RGB (sin alfa, el alfa es 255).</summary>
    public static (byte a, byte r, byte g, byte b) ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        byte a = 255;
        if (h.Length == 8) { a = Convert.ToByte(h[..2], 16); h = h[2..]; }
        return (a, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }

    /// <summary>Hex de 8 dígitos (#AARRGGBB) de un color, para mostrar en la UI.</summary>
    public static string Hex(Windows.UI.Color color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Gradiente lineal para el fondo de ventana de un tema (el lienzo sobre el que se
    /// ven sus superficies).
    /// </summary>
    public static LinearGradientBrush Linear(
        double x1, double y1, double x2, double y2, params (double Offset, string Hex)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(x1, y1),
            EndPoint = new Point(x2, y2)
        };
        foreach (var (offset, hex) in stops)
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = Color(hex) });
        return brush;
    }

    /// <summary>
    /// Resplandor radial: el color pleno en el centro, una versión intermedia a media
    /// distancia y transparente en el borde. Es lo que da el aire de marea/brasa
    /// (una mancha suave de color) sin usar imágenes.
    /// </summary>
    public static RadialGradientBrush Glow(string centerHex, string midHex)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color(centerHex) });
        brush.GradientStops.Add(new GradientStop { Offset = 0.55, Color = Color(midHex) });
        brush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color("#00000000") });
        return brush;
    }

    // =====================================================================
    // Superficies derivadas de la imagen de fondo (ver Wallpaper.RefreshDerivedPanels)
    // =====================================================================

    /// <summary>
    /// Compone un color translúcido SOBRE otro (src-over) y devuelve el resultado opaco.
    /// Es cómo "se ve" el velo del tema encima del promedio de la foto: de ese compuesto
    /// salen los colores de las superficies derivadas (ver <see cref="SurfaceFromImage"/>).
    /// </summary>
    public static Windows.UI.Color ComposeOver(Windows.UI.Color top, Windows.UI.Color backdrop)
    {
        if (top.A == 255) return Windows.UI.Color.FromArgb(255, top.R, top.G, top.B);
        if (top.A == 0) return Windows.UI.Color.FromArgb(255, backdrop.R, backdrop.G, backdrop.B);
        double a = top.A / 255.0;
        return Windows.UI.Color.FromArgb(255,
            (byte)Math.Clamp(Math.Round(top.R * a + backdrop.R * (1 - a)), 0, 255),
            (byte)Math.Clamp(Math.Round(top.G * a + backdrop.G * (1 - a)), 0, 255),
            (byte)Math.Clamp(Math.Round(top.B * a + backdrop.B * (1 - a)), 0, 255));
    }

    /// <summary>
    /// Color de una SUPERFICIE (card, menú, chip) derivado del color del fondo que hay
    /// detrás: conserva el tono de la imagen, acota la saturación y fija la luminosidad
    /// en el escalón de superficie oscura. Es lo que hace que los temas con foto de fondo
    /// no necesiten colores escritos a mano: los sacan de la foto misma, y una imagen
    /// elegida por el usuario queda armónica sin tocar nada.
    ///
    /// La fórmula quedó calibrada contra los valores que estaban escritos a mano en los
    /// temas 0.3.1: con el promedio de cada foto compuesto con su velo da, por ejemplo,
    /// #280F0B para Brasa (antes #240F0C) y #0B2428 para Aurora (antes #07242A).
    /// </summary>
    /// <param name="backdrop">Color del fondo (promedio de la imagen compuesto con su velo), opaco.</param>
    /// <param name="lightness">Luminosidad objetivo (0-1): 0.10 cards, 0.09 menú, 0.125 chips.</param>
    public static Windows.UI.Color SurfaceFromImage(Windows.UI.Color backdrop, double lightness)
    {
        var (h, s, _) = RgbToHsl(backdrop.R / 255.0, backdrop.G / 255.0, backdrop.B / 255.0);
        return HslToRgb(h, Math.Min(s, 0.55), Math.Clamp(lightness, 0.0, 1.0));
    }

    private static (double h, double s, double l) RgbToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        if (max - min < 1e-9) return (0.0, 0.0, l);
        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) * 60.0;
        else if (max == g) h = ((b - r) / d + 2.0) * 60.0;
        else h = ((r - g) / d + 4.0) * 60.0;
        return (h, s, l);
    }

    private static Windows.UI.Color HslToRgb(double h, double s, double l)
    {
        if (s <= 0)
        {
            var gray = (byte)Math.Clamp(Math.Round(l * 255), 0, 255);
            return Windows.UI.Color.FromArgb(255, gray, gray, gray);
        }
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double hp = (h % 360.0 + 360.0) % 360.0 / 60.0;
        double x = c * (1.0 - Math.Abs(hp % 2.0 - 1.0));
        double r = 0, g = 0, b = 0;
        switch ((int)hp)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        double m = l - c / 2.0;
        return Windows.UI.Color.FromArgb(255,
            (byte)Math.Clamp(Math.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + m) * 255), 0, 255));
    }

    /// <summary>
    /// Pincel con una imagen REAL de fondo (un archivo de <see cref="BackgroundsDirectory"/>).
    ///
    /// Se arma con la ruta del archivo que viaja al lado del exe —mismo criterio que
    /// Flags.cs, que también crea sus BitmapImage desde una ruta—: ms-appx:/// no es de fiar
    /// en una app sin empaquetar (WindowsPackageType=None). Si el archivo no está se devuelve
    /// un pincel vacío en vez de una imagen apuntando a algo que no existe.
    /// </summary>
    public static ImageBrush Image(string? fileName)
        => ImageFromPath(fileName == null ? null : Path.Combine(BackgroundsDirectory, fileName));

    /// <summary>
    /// Igual que <see cref="Image(string?)"/> pero con una ruta absoluta (imagen elegida por el usuario).
    ///
    /// La imagen SIEMPRE se ve COMPLETA y adaptada al tamaño de la ventana (Fill): el pincel
    /// llena el Border que cubre la ventana y se re-escala solo al redimensionar. Antes era
    /// UniformToFill, que para no deformar RECORTA lo que sobra — con la ventana maximizada
    /// en un monitor de aspecto distinto al de la foto (16:10, ultrawide) se perdía parte de
    /// la imagen y parecía que el fondo estaba mal. Con Fill la deformación solo se nota en
    /// aspectos muy alejados del de la foto (y estos fondos son paisajes, no
    /// retratos), y el velo del tema encima unifica el resultado.
    /// </summary>
    public static ImageBrush ImageFromPath(string? fullPath)
    {
        var brush = new ImageBrush { Stretch = Stretch.Fill };
        try
        {
            if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
                brush.ImageSource = new BitmapImage(new Uri(fullPath));
        }
        catch { }
        return brush;
    }

    /// <summary>
    /// Una clave del tema con su color: los pinceles semánticos llevan el color en el
    /// pincel; los SystemAccentColor* y los de overrides pueden ser Color sueltos.
    /// </summary>
    public sealed class Entry
    {
        public string Key { get; }
        public Brush Brush { get; }

        public Entry(string key, string hex)
            : this(key, Brush(hex))
        {
        }

        /// <summary>
        /// Entrada con un pincel ya armado: lo usan las piezas que no se pueden expresar
        /// con un color (el gradiente del fondo, los resplandores y la imagen del fondo).
        /// </summary>
        public Entry(string key, Brush brush)
        {
            Key = key;
            Brush = brush;
        }
    }
}
