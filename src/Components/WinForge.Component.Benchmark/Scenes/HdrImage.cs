namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// Lector del formato **Radiance (.hdr)**, que es el que traen los HDRI de Poly Haven: un equirect
/// de radiancia real en punto flotante, con un canal de exponente compartido (RGBE).
///
/// Por qué un lector propio y no una librería: el HDRI es DATO de la escena (ver
/// <see cref="SceneEnvironment"/>) y el componente ya decodifica sus texturas a mano. Una dependencia
/// nueva solo para leer un archivo de 40 líneas de formato sería otro assembly en el zip del
/// componente y otra versión que mantener.
///
/// Formato (lo que hace falta y nada más, que es la parte que el archivo realmente usa):
/// <list type="bullet">
/// <item>Cabecera de líneas <c>clave=valor</c> hasta una línea vacía. El único campo obligatorio es
/// <c>FORMAT=32-bit_rle_rgbe</c>: un .hdr con otro formato no es un mapa de radiancia.</item>
/// <item>Línea de resolución <c>-Y alto +X ancho</c> (arriba→abajo, izquierda→derecha), que es la
/// que escriben todas las herramientas. Las orientaciones rotadas se rechazan: inventar la lectura
/// daría un mapa de luz girado, que es peor que no tener IBL.</item>
/// <item>Píxeles con la codificación RLE por líneas (el caso normal, ancho entre 8 y 32767) o en
/// crudo. El caso RLE es un byte de cuenta por componente: mayor que 128 = repetición, menor =
/// literales.</item>
/// </list>
///
/// La radiancia sale lineal, sin ningún gamma: es luz, no color de pantalla. El valor de un píxel es
/// <c>(canal + 0.5) * 2^(exponente - 136)</c> — el +0.5 es el punto medio del rango del canal, y
/// saltearlo oscurece todo el mapa un octavo de paso.
/// </summary>
internal static class HdrImage
{
    /// <summary>
    /// Decodifica un .hdr a radiancia lineal, con los canales intercalados (rgb, rgb, …), fila 0 =
    /// arriba. Devuelve <c>null</c> si el archivo no está o no es un Radiance válido: un HDRI que no
    /// se puede leer deja la escena con el cielo procedural, no la tumba.
    /// </summary>
    internal static float[]? Load(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            bool radiance = false;
            string? resolution = null;
            while (true)
            {
                string? line = ReadLine(reader);
                if (line == null) return null;                 // cabecera truncada
                if (line.Length == 0) break;                   // fin de la cabecera
                if (line.StartsWith("FORMAT=", StringComparison.OrdinalIgnoreCase))
                {
                    radiance = line.Contains("32-bit_rle_rgbe", StringComparison.OrdinalIgnoreCase);
                }
                else if (!line.StartsWith("#", StringComparison.Ordinal) && !line.Contains('='))
                {
                    resolution = line;                         // la línea de resolución ("-Y h +X w")
                }
            }

            if (!radiance) return null;
            resolution ??= ReadLine(reader);
            if (resolution == null) return null;

            if (!TryParseResolution(resolution, out height, out width) || width <= 0 || height <= 0) return null;

            var pixels = new float[width * height * 3];
            var scanline = new byte[width * 4];
            var rgbe = new byte[width * 4];

            for (int y = 0; y < height; y++)
            {
                if (width < 8 || width > 32767)
                {
                    // Sin RLE por líneas: la fila viene cruda, 4 bytes por píxel.
                    if (!ReadExact(reader, rgbe, width * 4)) return null;
                }
                else
                {
                    int b0 = reader.ReadByte();
                    int b1 = reader.ReadByte();
                    int b2 = reader.ReadByte();
                    int b3 = reader.ReadByte();
                    int lineWidth = (b2 << 8) | b3;
                    if (b0 != 2 || b1 != 2 || lineWidth != width)
                    {
                        // Es una línea en crudo (no un encabezado RLE): se reusa lo leído.
                        rgbe[0] = (byte)b0;
                        rgbe[1] = (byte)b1;
                        rgbe[2] = (byte)b2;
                        rgbe[3] = (byte)b3;
                        if (!ReadExact(reader, rgbe.AsSpan(4), width * 4 - 4)) return null;
                    }
                    else
                    {
                        for (int channel = 0; channel < 4; channel++)
                        {
                            int x = 0;
                            while (x < width)
                            {
                                int count = reader.ReadByte();
                                if (count > 128)
                                {
                                    byte value = (byte)reader.ReadByte();
                                    int run = Math.Min(count - 128, width - x);
                                    for (int i = 0; i < run; i++) scanline[(x + i) * 4 + channel] = value;
                                    x += run;
                                }
                                else
                                {
                                    int run = Math.Min(count, width - x);
                                    for (int i = 0; i < run; i++)
                                        scanline[(x + i) * 4 + channel] = (byte)reader.ReadByte();
                                    x += run;
                                }
                            }
                        }
                        Array.Copy(scanline, rgbe, scanline.Length);
                    }
                }

                int row = y * width * 3;
                for (int x = 0; x < width; x++)
                {
                    byte r = rgbe[x * 4 + 0];
                    byte g = rgbe[x * 4 + 1];
                    byte b = rgbe[x * 4 + 2];
                    byte e = rgbe[x * 4 + 3];
                    // Exponente 0 = negro exacto: la fórmula daría un subnormal casi cero.
                    if (e == 0)
                    {
                        pixels[row + x * 3 + 0] = 0f;
                        pixels[row + x * 3 + 1] = 0f;
                        pixels[row + x * 3 + 2] = 0f;
                    }
                    else
                    {
                        float scale = MathF.ScaleB(1f, e - 136);
                        pixels[row + x * 3 + 0] = (r + 0.5f) * scale;
                        pixels[row + x * 3 + 1] = (g + 0.5f) * scale;
                        pixels[row + x * 3 + 2] = (b + 0.5f) * scale;
                    }
                }
            }

            return pixels;
        }
        catch (Exception)
        {
            // Un .hdr cortado o con una cabecera que no esperábamos: la escena sigue sin IBL.
            return null;
        }
    }

    private static bool TryParseResolution(string line, out int height, out int width)
    {
        height = 0;
        width = 0;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;
        if (!parts[0].StartsWith("-Y", StringComparison.Ordinal)) return false;
        if (parts[2] != "+X") return false;
        return int.TryParse(parts[1], out height) && int.TryParse(parts[3], out width);
    }

    private static string? ReadLine(BinaryReader reader)
    {
        var builder = new System.Text.StringBuilder(64);
        while (true)
        {
            int value = reader.BaseStream.ReadByte();
            if (value < 0) return builder.Length > 0 ? builder.ToString() : null;
            if (value == '\n') return builder.ToString();
            if (value != '\r') builder.Append((char)value);
        }
    }

    private static bool ReadExact(BinaryReader reader, Span<byte> destination, int count)
    {
        int read = 0;
        while (read < count)
        {
            int got = reader.Read(destination[read..count]);
            if (got <= 0) return false;
            read += got;
        }
        return true;
    }
}
