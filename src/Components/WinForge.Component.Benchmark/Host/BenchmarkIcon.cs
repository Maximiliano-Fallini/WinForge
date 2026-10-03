using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WinForge.Component.Benchmark.Host;

/// <summary>
/// El icono de las ventanas del benchmark (la de la escena y la franja de métricas).
///
/// El dibujo es UN archivo PNG —el que preparó el equipo para el componente, con el mismo lenguaje
/// visual que el resto de la app— y viaja EMBEBIDO en este assembly (ver el <c>EmbeddedResource</c> del
/// .csproj), así que no hay ningún archivo suelto al lado del ejecutable que se pueda no copiar: el
/// zip del componente y el probador lo llevan adentro.
///
/// Win32 no acepta un PNG: hay que darle un <c>HICON</c>. Se arma con GDI+ (que ya se usa para el HUD y
/// para decodificar las texturas del atlas) redimensionando el original a los dos tamaños que Windows
/// pide: 32×32 para la barra de tareas y Alt+Tab, y 16×16 para el título de la ventana.
///
/// El PNG es un LOGOTIPO sobre un lienzo cuadrado grande —el contenido opaco ocupa 1078×571 de un
/// lienzo de 1254×1254, o sea que tiene cientos de píxeles transparentes arriba y abajo—. Escalarlo
/// entero al tamaño del icono deja el dibujo en la mitad de su tamaño útil, así que se recorta a la
/// CAJA del contenido y recién ahí se encaja en el cuadro conservando la proporción: a 16×16 el
/// logotipo se lee, que es lo único que importa en un título de ventana.
///
/// Todo es best-effort: si algo falla, se devuelve 0 y la ventana se abre SIN icono, que es como estaba
/// antes — un componente no puede dejar de dibujar la escena porque no pudo pintar una ventanita.
/// </summary>
internal static class BenchmarkIcon
{
    /// <summary>Icono grande (barra de tareas y Alt+Tab).</summary>
    internal static nint Large { get; } = Load(32);

    /// <summary>Icono chico (la esquina del título de la ventana).</summary>
    internal static nint Small { get; } = Load(16);

    /// <summary>Alfa por encima del cual un píxel cuenta como parte del dibujo. Bajo a propósito: el
    /// antialias del logotipo tiene un borde ancho y recortar por el filo lo dejaría sin halo.</summary>
    private const int OpaqueThreshold = 24;

    private static nint Load(int size)
    {
        try
        {
            var assembly = typeof(BenchmarkIcon).Assembly;
            string? resource = null;
            foreach (string candidate in assembly.GetManifestResourceNames())
            {
                if (candidate.EndsWith("WinForgeBenchmarkIcono.png", StringComparison.OrdinalIgnoreCase))
                {
                    resource = candidate;
                    break;
                }
            }
            if (resource == null) return 0;

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream == null) return 0;
            using var decoded = new Bitmap(stream);
            // Copia en 32bppArgb: el PNG puede venir en cualquier formato (paleta, 24bpp) y el recorte
            // necesita leer el alfa y el color sin adivinar el formato de origen.
            using var source = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
            using (var flatten = System.Drawing.Graphics.FromImage(source))
            {
                flatten.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);
            }

            var content = ContentBounds(source);
            using var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;

                // Encaje CONSERVANDO la proporción: el logotipo es más ancho que alto y estirarlo a un
                // cuadrado lo deforma. Se centra en el cuadro y el resto queda transparente.
                float ratio = Math.Min((float)size / content.Width, (float)size / content.Height);
                int width = Math.Max(1, (int)MathF.Round(content.Width * ratio));
                int height = Math.Max(1, (int)MathF.Round(content.Height * ratio));
                graphics.DrawImage(source, new Rectangle((size - width) / 2, (size - height) / 2, width, height),
                    content, GraphicsUnit.Pixel);
            }
            return scaled.GetHicon();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// La caja de los píxeles que NO son transparentes: es el recorte que saca los márgenes vacíos del
    /// lienzo. Si el dibujo viniera entero transparente (un archivo vacío), se devuelve el lienzo
    /// completo y el escalado sale como salía antes.
    /// </summary>
    private static Rectangle ContentBounds(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
            unsafe
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    var row = (byte*)(nint)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        if (row[x * 4 + 3] <= OpaqueThreshold) continue;   // BGRA: el alfa va en +3
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }
            }
            return right < 0
                ? new Rectangle(0, 0, bitmap.Width, bitmap.Height)
                : new Rectangle(left, top, right - left + 1, bottom - top + 1);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
