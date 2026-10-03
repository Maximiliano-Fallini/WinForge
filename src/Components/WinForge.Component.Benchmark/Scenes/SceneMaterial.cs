using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// Una textura de material ya DECODIFICADA: píxeles RGBA8 de 8 bits por canal, en el orden en que
/// los comen las cuatro APIs (Direct3D, Vulkan y OpenGL comparten el mismo layout de bytes, y todas
/// esperan R primero: el formato es <c>R8G8B8A8</c>, no <c>B8G8R8A8</c>).
///
/// OJO con este orden: <c>System.Drawing</c> entrega los píxeles al revés (BGRA) y copiarlos tal cual
/// a una textura <c>R8G8B8A8</c> cambia los canales rojo y azul —una pared de ladrillo sale azul, un
/// asfalto cálido sale frío— sin ningún error visible más que el color equivocado.
///
/// Vive en memoria del CPU y no como recurso de GPU a propósito: la escena es DATO y no sabe de
/// ninguna API (ver <see cref="SceneDefinition"/>). Cada backend la sube a su propia textura una
/// vez, al preparar la escena, y después la ata por material.
///
/// El canal alfa se fuerza a 255: los assets de material (Poly Haven) son opacos y una textura
/// con alfa basura se vería transparente si algún backend habilita la mezcla.
/// </summary>
public sealed class SceneTexture
{
    /// <summary>Nombre para diagnósticos: el archivo de origen, sin la ruta.</summary>
    public required string Name { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Píxeles RGBA8, fila 0 = arriba, sin padding (ancho × alto × 4 bytes).</summary>
    public required byte[] Rgba { get; init; }

    /// <summary>
    /// Si el contenido es color de PANTALLA (albedo, emisivo) y no un dato lineal (normal, rugosidad,
    /// metalicidad, oclusión). Los backends crean la textura con formato sRGB solo cuando lo es: leer
    /// un mapa de rugosidad como sRGB oscurecería el material.
    /// </summary>
    public bool Srgb { get; init; } = true;

    /// <summary>Bytes que ocupa en memoria del CPU (para el informe y los diagnósticos).</summary>
    public int ByteCount => Rgba.Length;

    /// <summary>Textura desde un archivo de imagen (jpg/png) del disco.</summary>
    public static SceneTexture FromFile(string path, bool srgb = true) =>
        FromStream(File.ReadAllBytes(path), Path.GetFileName(path), srgb);

    /// <summary>Textura desde bytes de un archivo de imagen ya leído (los glTF traen las suyas así).</summary>
    public static SceneTexture FromStream(byte[] encoded, string name, bool srgb = true)
    {
        using var stream = new MemoryStream(encoded, writable: false);
        using var bitmap = new Bitmap(stream);
        return FromBitmap(bitmap, name, srgb);
    }

    private static SceneTexture FromBitmap(Bitmap source, string name, bool srgb)
    {
        int width = source.Width;
        int height = source.Height;
        var pixels = new byte[width * height * 4];

        // Se copia a un bitmap propio en 32bpp ARGB: GetPixel() por píxel sobre una textura de
        // 1024×1024 son un millón de llamadas de marshalling (medido: segundos por textura), y
        // LockBits es una copia de memoria.
        using var normalized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        // OJO: 'Graphics' a secas resuelve al namespace Graphics del componente, no a System.Drawing.
        using (var graphics = System.Drawing.Graphics.FromImage(normalized))
        {
            graphics.DrawImage(source, 0, 0, width, height);
        }

        var data = normalized.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* source0 = (byte*)data.Scan0;
                for (int y = 0; y < height; y++)
                {
                    byte* row = source0 + y * data.Stride;
                    int destination = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        byte* pixel = row + x * 4;   // System.Drawing entrega BGRA
                        pixels[destination + 0] = pixel[2];   // R
                        pixels[destination + 1] = pixel[1];   // G
                        pixels[destination + 2] = pixel[0];   // B
                        pixels[destination + 3] = 255;        // A: siempre opaco (ver la nota de la clase)
                        destination += 4;
                    }
                }
            }
        }
        finally
        {
            normalized.UnlockBits(data);
        }

        return new SceneTexture { Name = name, Width = width, Height = height, Rgba = pixels, Srgb = srgb };
    }
}

/// <summary>
/// Material PBR de un objeto: albedo (color base), mapa de normales y un mapa empaquetado
/// <c>ARM</c> (R = oclusión, G = rugosidad, B = metalicidad — la convención de Poly Haven y de glTF).
///
/// Es lo que hace que una escena se lea como un lugar real en vez de como una demo de geometría: la
/// forma la pone la malla y el detalle fino (asfalto granuloso, chapa con rayones, ladrillo con
/// juntas) lo pone el material. Los shaders tienen UNA implementación de material y estos datos la
/// configuran, igual que el estilo de la escena (ver <see cref="SceneStyle"/>).
///
/// Todo es opcional: un material sin texturas es un material de color plano, y una escena sin
/// assets sigue dibujando (con los factores solos).
/// </summary>
public sealed class SceneMaterial
{
    public required string Name { get; init; }

    /// <summary>Color base (albedo). Se multiplica por la textura de albedo si la hay.</summary>
    public Vector3 Tint { get; init; } = Vector3.One;

    /// <summary>Textura de color base (sRGB). Se decodifica al preparar la escena, no al armar el
    /// catálogo: ver <see cref="SceneTextureSource"/>.</summary>
    public SceneTextureSource? Albedo { get; init; }

    /// <summary>Mapa de normales en convención OpenGL (Y+), que es la de Poly Haven. El shader lo
    /// convierte a la de cada API al muestrear (ver la nota en el HLSL).</summary>
    public SceneTextureSource? Normal { get; init; }

    /// <summary>Oclusión (R), rugosidad (G) y metalicidad (B) empaquetadas en una sola textura.</summary>
    public SceneTextureSource? Arm { get; init; }

    /// <summary>Fuerza del mapa de normales (0 = sin detalle de relieve, 1 = tal cual el mapa).</summary>
    public float NormalStrength { get; init; } = 1f;

    /// <summary>Multiplicadores de rugosidad y metalicidad sobre lo que traiga el mapa ARM.</summary>
    public float RoughnessScale { get; init; } = 1f;
    public float MetallicScale { get; init; } = 1f;

    /// <summary>Repeticiones de la textura por unidad de mundo (las paredes y el piso se hacen con
    /// una textura chica que se repite; sin esto el material se estira y se ve como una calcomanía).</summary>
    public float UvScale { get; init; } = 1f;

    /// <summary>Emisión propia del material (carteles de neón, ventanas encendidas, faroles).</summary>
    public Vector3 Emissive { get; init; }
    public float EmissiveStrength { get; init; }

    /// <summary>Si conviene atarlo a la mezcla (vidrio, agua): el material decide, no el shader.</summary>
    public bool Transparent { get; init; }
}
