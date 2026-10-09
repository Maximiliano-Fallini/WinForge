using System;

namespace WHPO_UI;

/// <summary>
/// El algoritmo del relleno desenfocado de los paneles: desenfoque de caja separable + teñido,
/// sobre un búfer BGRA de 8 bits por canal. Vive aparte de <see cref="PanelBlur"/> —que es quien
/// habla con el decodificador y con XAML— para que el cálculo se pueda ejercitar y medir sin
/// levantar la interfaz (ver tools/_blur-check).
///
/// El desenfoque tiene que ser SEPARABLE (una pasada horizontal y otra vertical por cada
/// repetición) y no un cuadrado por píxel: con el radio de trabajo, la versión ingenua costaría
/// cientos de millones de operaciones por imagen y acá se hace en unos pocos millones.
/// </summary>
internal static class PanelBlurAlgorithm
{
    /// <summary>Ancho de trabajo: el desenfoque se come el detalle, no hace falta más resolución.</summary>
    public const int WorkingWidth = 320;

    /// <summary>Repeticiones de la pasada de caja: con 3 se lee como un gaussiano.</summary>
    public const int BlurPasses = 3;

    /// <summary>
    /// Radio MÁXIMO del desenfoque, en píxeles del ancho de trabajo. Lo que gradúa la intensidad es
    /// este radio: el deslizador de blur pide un porcentaje y <see cref="RadiusForPercent"/> lo
    /// traduce a un radio de 1 a 16, así que el ajuste es una intensidad gradual y no un
    /// interruptor (antes había un único radio fijo y el número era solo encendido/apagado).
    ///
    /// POR QUÉ 16: la copia mide 320 px de ancho y se estira al ancho de la ventana, así que en una
    /// ventana de 1920 px cada píxel de acá son 6 de pantalla — el radio máximo equivale a unos
    /// 96 px de desenfoque real, que es el techo de un vidrio esmerilado (más que eso ya no se
    /// distingue de un color plano). El costo no depende del radio: el desenfoque de caja usa una
    /// media móvil, así que el radio grande cuesta lo mismo que el chico.
    /// </summary>
    public const int MaxBlurRadius = 16;

    /// <summary>
    /// Radio que le toca a un porcentaje del deslizador: de 1 px (1 %) a
    /// <see cref="MaxBlurRadius"/> (100 %). Nunca devuelve 0: con el desenfoque encendido el parche
    /// tiene que estar desenfocado, aunque sea apenas — el 0 % no llega acá, apaga la capa entera
    /// (ver <see cref="PanelAppearance.SetBlurPercent"/>).
    /// </summary>
    public static int RadiusForPercent(double percent)
    {
        if (!double.IsFinite(percent)) percent = 0.0;
        double fraction = Math.Clamp(percent, 0.0, 100.0) / 100.0;
        return Math.Max(1, (int)Math.Round(fraction * MaxBlurRadius));
    }

    /// <summary>Desenfoca el búfer con la intensidad pedida (la copia se pinta tal cual detrás de los paneles, sin teñir).</summary>
    public static void Apply(byte[] bgra, int width, int height, double percent)
    {
        Blur(bgra, width, height, RadiusForPercent(percent), BlurPasses);
    }

    /// <summary>Desenfoque de caja separable repetido (3 cajas seguidas ya son un gaussiano decente).</summary>
    public static void Blur(byte[] data, int width, int height, int radius, int passes)
    {
        var scratch = new byte[data.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            BlurHorizontal(data, scratch, width, height, radius);
            BlurVertical(scratch, data, width, height, radius);
        }
    }

    /// <summary>Media móvil horizontal: cada píxel sale de la suma de la ventana, que se corre de a un paso.</summary>
    private static void BlurHorizontal(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int window = radius * 2 + 1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            int sumB = 0, sumG = 0, sumR = 0;

            // Los bordes se rellenan tomando el píxel más cercano (extiende el borde en vez de
            // oscurecerlo): sin esto, el marco de la imagen queda con un halo hacia el negro.
            for (int k = -radius; k <= radius; k++)
            {
                int x = Math.Clamp(k, 0, width - 1) * 4;
                sumB += src[row + x];
                sumG += src[row + x + 1];
                sumR += src[row + x + 2];
            }

            for (int x = 0; x < width; x++)
            {
                int o = row + x * 4;
                dst[o] = (byte)(sumB / window);
                dst[o + 1] = (byte)(sumG / window);
                dst[o + 2] = (byte)(sumR / window);
                dst[o + 3] = src[o + 3];

                int leaving = Math.Clamp(x - radius, 0, width - 1) * 4;
                int entering = Math.Clamp(x + radius + 1, 0, width - 1) * 4;
                sumB += src[row + entering] - src[row + leaving];
                sumG += src[row + entering + 1] - src[row + leaving + 1];
                sumR += src[row + entering + 2] - src[row + leaving + 2];
            }
        }
    }

    /// <summary>Media móvil vertical (misma cuenta que la horizontal, con el alto como recorrido).</summary>
    private static void BlurVertical(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int window = radius * 2 + 1;
        int stride = width * 4;
        for (int x = 0; x < width; x++)
        {
            int column = x * 4;
            int sumB = 0, sumG = 0, sumR = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int y = Math.Clamp(k, 0, height - 1) * stride;
                sumB += src[y + column];
                sumG += src[y + column + 1];
                sumR += src[y + column + 2];
            }

            for (int y = 0; y < height; y++)
            {
                int o = y * stride + column;
                dst[o] = (byte)(sumB / window);
                dst[o + 1] = (byte)(sumG / window);
                dst[o + 2] = (byte)(sumR / window);
                dst[o + 3] = src[o + 3];

                int leaving = Math.Clamp(y - radius, 0, height - 1) * stride;
                int entering = Math.Clamp(y + radius + 1, 0, height - 1) * stride;
                sumB += src[entering + column] - src[leaving + column];
                sumG += src[entering + column + 1] - src[leaving + column + 1];
                sumR += src[entering + column + 2] - src[leaving + column + 2];
            }
        }
    }

    /// <summary>Fija el alfa en opaco: la copia se dibuja sobre la foto y debajo de los paneles.</summary>
    public static void Opaque(byte[] data)
    {
        for (int i = 3; i < data.Length; i += 4) data[i] = 255;
    }
}
