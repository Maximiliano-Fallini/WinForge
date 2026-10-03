using System;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// Textura de detalle de la escena: un atlas de 256×256 en RGBA8 generado en el CPU al cargar y
/// subido UNA vez por cada API (ver <c>CreateDetailTexture</c> en los backends). Es lo que le da
/// material al píxel: grano de pasto, piedra, ondas de agua, algodón de nube y hoja de copa.
///
/// Por qué un atlas y no un archivo: el componente no trae assets (no hay pipeline de imágenes ni
/// archivos que instalar), así que la textura se DIBUJA con el mismo ruido determinista que el
/// resto del componente — misma semilla, misma textura en cada corrida, en las cuatro APIs.
///
/// Por qué 4×4 tiles de 64: el muestreo elige la celda con un offset constante y encoge la uv con
/// <c>frac</c>, así que el shader tiene "ocho materiales" con UNA textura atada y sin matrices ni
/// arreglos de texturas (que costarían un descriptor más por material en Vulkan y en Direct3D 12).
///
/// La FILA 3 (celdas 12 a 15) no es material: son los SPRITES de las partículas —polen, hoja,
/// semilla y pétalo— dibujados acá con su forma en el canal ALFA y su color en rgb (ver
/// <see cref="BuildParticleTiles"/>). Van en el mismo atlas porque el aire y el material no se pisan
/// en el shader y una textura más costaría un descriptor por frame en las cuatro APIs.
///
/// El filtrado es LINEAL y con wrap: el detalle se repite (una celda de pasto cubre ~2 unidades de
/// mundo) y el borde de la celda tiene que continuar sin costura.
/// </summary>
internal static class SceneDetailTexture
{
    /// <summary>Lado del atlas, en píxeles.</summary>
    internal const int Size = 256;

    /// <summary>Lado de cada celda (el atlas es una grilla de 4×4).</summary>
    internal const int Tile = Size / 4;

    private static byte[]? pixels;

    /// <summary>
    /// Píxeles en RGBA8, fila por fila (sin padding: 4 bytes por píxel). Se genera una sola vez y
    /// se comparte entre backends: es contenido de la escena, no del dispositivo.
    /// </summary>
    internal static byte[] Pixels
    {
        get
        {
            if (pixels == null) pixels = Build();
            return pixels;
        }
    }

    private static byte[] Build()
    {
        var pixels = new byte[Size * Size * 4];

        // ---- Receta de cada celda: frecuencia horizontal/vertical, octavas, contraste y paleta ----
        // La frecuencia anisotrópica es lo que hace que una celda se lea como un material: el pasto
        // y la corteza se estiran en vertical, la piedra y el agua son isótropas.
        Span<TileRecipe> recipes = stackalloc TileRecipe[16];
        recipes[0] = new(4f, 22f, 4, 1.05f, new(0.06f, 0.22f, 0.05f), new(0.42f, 0.86f, 0.16f));    // pasto
        recipes[1] = new(14f, 14f, 3, 1.15f, new(0.13f, 0.13f, 0.13f), new(0.62f, 0.62f, 0.60f));    // piedra
        recipes[2] = new(6f, 6f, 2, 0.85f, new(0.20f, 0.42f, 0.52f), new(0.95f, 0.99f, 1.00f));     // agua
        recipes[3] = new(3f, 3f, 4, 0.75f, new(0.55f, 0.60f, 0.70f), new(1.00f, 1.00f, 1.00f));     // nube
        recipes[4] = new(9f, 12f, 3, 1.10f, new(0.05f, 0.20f, 0.05f), new(0.36f, 0.72f, 0.14f));    // hoja
        recipes[5] = new(3f, 26f, 3, 1.00f, new(0.16f, 0.11f, 0.07f), new(0.70f, 0.55f, 0.36f));    // corteza
        recipes[6] = new(18f, 18f, 3, 1.10f, new(0.24f, 0.17f, 0.10f), new(0.78f, 0.64f, 0.42f));    // tierra
        recipes[7] = new(26f, 26f, 2, 1.60f, new(0.02f, 0.02f, 0.02f), new(1.00f, 1.00f, 0.94f));    // chispa
        // Cuatro repiten las recetas con menos contraste: el muestreo puede pedir una variante
        // (celdas 8..11) sin que haga falta otro dibujo. La FILA 3 (12..15) queda libre: ahí van los
        // sprites de partícula, que se pintan aparte (ver BuildParticleTiles).
        for (int i = 8; i < 12; i++) recipes[i] = recipes[i - 8] with { Contrast = recipes[i - 8].Contrast * 0.85f };

        for (int ty = 0; ty < 4; ty++)
        {
            for (int tx = 0; tx < 4; tx++)
            {
                var recipe = recipes[ty * 4 + tx];
                int originX = tx * Tile;
                int originY = ty * Tile;
                for (int y = 0; y < Tile; y++)
                {
                    for (int x = 0; x < Tile; x++)
                    {
                        // La uv se muestrea en el CENTRO del píxel y con las frecuencias de la celda:
                        // el ruido es continuo dentro de la celda y el wrap del muestreo se encarga
                        // de la costura con la celda vecina.
                        float u = (x + 0.5f) / Tile;
                        float v = (y + 0.5f) / Tile;
                        float value = Fractal(u * recipe.FrequencyX, v * recipe.FrequencyY, recipe.Octaves);
                        value = Math.Clamp((value - 0.5f) * recipe.Contrast + 0.5f, 0f, 1f);

                        float red = recipe.Low.R + (recipe.High.R - recipe.Low.R) * value;
                        float green = recipe.Low.G + (recipe.High.G - recipe.Low.G) * value;
                        float blue = recipe.Low.B + (recipe.High.B - recipe.Low.B) * value;

                        int index = ((originY + y) * Size + (originX + x)) * 4;
                        pixels[index + 0] = ToByte(red);
                        pixels[index + 1] = ToByte(green);
                        pixels[index + 2] = ToByte(blue);
                        pixels[index + 3] = 255;
                    }
                }
            }
        }

        BuildParticleTiles(pixels);
        return pixels;
    }

    // =====================================================================
    // Sprites de partículas (fila 3 del atlas: celdas 12 a 15)
    // =====================================================================

    /// <summary>
    /// Dibuja los cuatro sprites de las partículas. A diferencia de las celdas de material, que se
    /// muestrean como un escalar, estas traen RGB (el color del sprite) y ALFA (su forma): el shader
    /// del polen multiplica el color por el brillo del sol y usa el alfa como máscara, así que la
    /// partícula se ve como una mota, una hoja o un penacho de verdad en vez de un cuadro de color.
    ///
    /// Todas las siluetas se apagan ANTES del borde de la celda (radio ≈0,9): el filtrado es lineal y
    /// la celda vecina tiene alfa 1, así que un sprite que llegue al borde se ve como un cuadrado.
    /// </summary>
    private static void BuildParticleTiles(byte[] pixels)
    {
        Paint(pixels, 12, PollenSprite);
        Paint(pixels, 13, LeafSprite);
        Paint(pixels, 14, SeedSprite);
        Paint(pixels, 15, PetalSprite);
    }

    /// <summary>Pinta una celda del atlas con el dibujo indicado (u, v en [0,1] del centro de la celda).</summary>
    private static void Paint(byte[] pixels, int tileIndex, SpriteArtist artist)
    {
        int originX = (tileIndex % 4) * Tile;
        int originY = (tileIndex / 4) * Tile;
        for (int y = 0; y < Tile; y++)
        {
            for (int x = 0; x < Tile; x++)
            {
                var sprite = artist((x + 0.5f) / Tile, (y + 0.5f) / Tile);
                int index = ((originY + y) * Size + (originX + x)) * 4;
                pixels[index + 0] = ToByte(sprite.Color.R);
                pixels[index + 1] = ToByte(sprite.Color.G);
                pixels[index + 2] = ToByte(sprite.Color.B);
                pixels[index + 3] = ToByte(sprite.Alpha);
            }
        }
    }

    /// <summary>
    /// Mota de polen: núcleo claro y borde de grumos. El radio del borde RESPIRA con el ángulo (dos
    /// octavas de ruido), que es lo único que separa una mota de polen de un punto dibujado: un
    /// círculo perfecto se lee como geometría, no como aire.
    /// </summary>
    private static Sprite PollenSprite(float u, float v)
    {
        float x = (u - 0.5f) * 2f;
        float y = (v - 0.5f) * 2f;
        float radius = MathF.Sqrt(x * x + y * y);
        float angle = MathF.Atan2(y, x);

        float lumps = 0.70f + 0.17f * Fractal(angle * 1.3f + 4.0f, 3.0f, 3)
                           + 0.10f * Fractal(angle * 3.4f - 1.0f, 7.0f, 2);
        float body = 1f - Smoothstep(lumps * 0.30f, lumps, radius);
        body *= body;
        float core = MathF.Exp(-radius * radius * 9f);
        float grain = Fractal(x * 9f + 3.0f, y * 9f + 7.0f, 3);

        var color = new Rgb(0.86f + 0.14f * core, 0.81f + 0.14f * core, 0.60f + 0.16f * core);
        float alpha = body * (0.45f + 0.55f * core) * (0.86f + 0.14f * grain);
        return new Sprite(color, alpha * (1f - Smoothstep(0.86f, 0.99f, radius)));
    }

    /// <summary>
    /// Hoja: una LENTE con las puntas cerradas y una nervadura central, verde de prado con la veta
    /// clara. Es la partícula grande del primer plano: a media unidad de mundo se le ve la forma, la
    /// vena y el brillo del borde, que es lo que hace que el aire cargado se lea.
    /// </summary>
    private static Sprite LeafSprite(float u, float v)
    {
        float x = (u - 0.5f) * 2f;
        float y = (v - 0.5f) * 2f;
        float axis = 0.13f * y * y;              // la hoja se arquea apenas
        float across = x - axis;
        float along = MathF.Min(1f, MathF.Abs(y) / 0.94f);
        float halfWidth = 0.30f * MathF.Sqrt(MathF.Max(0f, 1f - along * along));

        float shape = Smoothstep(0f, 0.045f, halfWidth - MathF.Abs(across));
        float vein = MathF.Exp(-MathF.Abs(across) * 26f);
        float grain = Fractal(across * 7.0f + 2.0f, y * 5.0f + 1.0f, 3);
        float ribs = 0.5f + 0.5f * MathF.Cos((y * 9.0f + across * 5.0f) * 3.1416f);

        var color = new Rgb(0.16f + 0.20f * grain + 0.10f * ribs - 0.06f * vein,
                            0.38f + 0.24f * grain + 0.08f * ribs - 0.10f * vein,
                            0.10f + 0.12f * grain - 0.04f * vein);
        return new Sprite(color, shape * (1f - Smoothstep(0.88f, 0.99f, MathF.Max(MathF.Abs(x), MathF.Abs(y)))));
    }

    /// <summary>
    /// Semilla con penacho (diente de león): el grano abajo, el tallo fino y once pelos en abanico. El
    /// penacho es lo que hace que una semilla de verdad flote: sin él sería un grano cayendo.
    /// </summary>
    private static Sprite SeedSprite(float u, float v)
    {
        float x = (u - 0.5f) * 2f;
        float y = (v - 0.5f) * 2f;

        // Grano: elipse alargada en la base.
        float sx = x / 0.085f;
        float sy = (y + 0.58f) / 0.24f;
        float seed = 1f - Smoothstep(0.75f, 1.15f, MathF.Sqrt(sx * sx + sy * sy));

        // Tallo: del grano al centro, apenas curvado.
        float stem = MathF.Exp(-MathF.Abs(x - 0.02f) * 42f) * (y > -0.42f && y < -0.10f ? 1f : 0f);

        // Penacho: once pelos que salen del extremo del tallo y se abren hacia arriba.
        float pappus = 0f;
        const int barbs = 11;
        for (int i = 0; i < barbs; i++)
        {
            float lean = -1f + 2f * i / (barbs - 1);
            float length = 0.52f - 0.10f * MathF.Abs(lean);
            float tipX = 0.02f + MathF.Sin(lean) * length;
            float tipY = 0.02f + MathF.Cos(lean) * length;

            // Distancia al segmento (base fija en el tallo, punta según el abanico).
            float ex = tipX - 0.02f;
            float ey = tipY + 0.10f;
            float t = Math.Clamp(((x - 0.02f) * ex + (y + 0.10f) * ey) / MathF.Max(1e-5f, ex * ex + ey * ey), 0f, 1f);
            float dx = x - (0.02f + ex * t);
            float dy = y - (-0.10f + ey * t);
            pappus = MathF.Max(pappus, MathF.Exp(-MathF.Sqrt(dx * dx + dy * dy) * 34f) * Smoothstep(1f, 0.55f, t));
        }

        float alpha = MathF.Max(MathF.Max(seed, stem * 0.85f), pappus * 0.92f);
        var color = new Rgb(0.32f + 0.62f * Smoothstep(0.15f, 0.75f, pappus),
                            0.27f + 0.66f * Smoothstep(0.15f, 0.75f, pappus),
                            0.20f + 0.68f * Smoothstep(0.15f, 0.75f, pappus));
        return new Sprite(color, alpha * (1f - Smoothstep(0.86f, 0.99f, MathF.Sqrt(x * x + y * y))));
    }

    /// <summary>Pétalo: la silueta de la hoja pero redonda y cálida (los pétalos que arranca el viento).</summary>
    private static Sprite PetalSprite(float u, float v)
    {
        float x = (u - 0.5f) * 2f;
        float y = (v - 0.5f) * 2f;
        float across = x + 0.06f * (1f - y * y);
        float along = MathF.Min(1f, MathF.Abs(y) / 0.90f);
        float halfWidth = 0.34f * MathF.Pow(MathF.Max(0f, 1f - along * along), 0.34f);

        float shape = Smoothstep(0f, 0.05f, halfWidth - MathF.Abs(across));
        float gloss = MathF.Exp(-((across / 0.16f) * (across / 0.16f)));
        float grain = Fractal(across * 8.0f + 5.0f, y * 6.0f + 3.0f, 3);

        var color = new Rgb(0.86f + 0.14f * gloss, 0.56f + 0.20f * gloss + 0.08f * grain, 0.22f + 0.18f * grain);
        return new Sprite(color, shape * (1f - Smoothstep(0.88f, 0.99f, MathF.Max(MathF.Abs(x), MathF.Abs(y)))));
    }

    /// <summary>Color y alfa de un texel de sprite (ver <see cref="Paint"/>).</summary>
    private readonly record struct Sprite(Rgb Color, float Alpha);

    /// <summary>Dibujo de una celda de partícula: recibe la uv del centro del texel y devuelve el texel.</summary>
    private delegate Sprite SpriteArtist(float u, float v);

    /// <summary>Interpolación suave entre dos bordes (los sprites la usan para los degradados del borde).</summary>
    private static float Smoothstep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / MathF.Max(1e-6f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Suma de octavas de ruido de valor con interpolación suave (mismo criterio que el ruido del shader).</summary>
    private static float Fractal(float x, float y, int octaves)
    {
        float sum = 0f, amplitude = 1f, total = 0f;
        for (int octave = 0; octave < octaves; octave++)
        {
            sum += Value(x, y) * amplitude;
            total += amplitude;
            x *= 2f;
            y *= 2f;
            amplitude *= 0.55f;
        }
        return total > 0f ? sum / total : 0f;
    }

    /// <summary>Ruido de valor con la retícula interpolada (suave, sin costuras en los enteros).</summary>
    private static float Value(float x, float y)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        float n00 = Hash(x0, y0), n10 = Hash(x0 + 1, y0);
        float n01 = Hash(x0, y0 + 1), n11 = Hash(x0 + 1, y0 + 1);
        return Lerp(Lerp(n00, n10, fx), Lerp(n01, n11, fx), fy);
    }

    /// <summary>
    /// Hash entero (xorshift, sin senos): con senos el patrón se repite en bandas diagonales y la
    /// textura sale con un damero visible. Es el mismo tipo de hash que usa el shader.
    /// </summary>
    private static float Hash(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    /// <summary>Receta de una celda del atlas (ver <see cref="Build"/>).</summary>
    private readonly record struct TileRecipe(
        float FrequencyX, float FrequencyY, int Octaves, float Contrast, Rgb Low, Rgb High);

    private readonly record struct Rgb(float R, float G, float B);
}
