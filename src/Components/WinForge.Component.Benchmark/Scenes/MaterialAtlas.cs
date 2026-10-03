using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// TODAS las texturas de material de una escena empaquetadas en UN arreglo de texturas
/// (<c>Texture2DArray</c>): por cada ranura de material hay tres rebanadas —albedo, normales y ARM—
/// y el shader elige el material con un ÍNDICE que viaja por instancia (ver
/// <see cref="SceneMesh.RenderInstances"/>).
///
/// Por qué un arreglo y no una textura por material: atar una textura por dibujo obliga a partir los
/// dibujos por material en las cuatro APIs (y en Vulkan a un descriptor set por material y por frame).
/// Un arreglo se ata UNA vez por frame —igual que el atlas de detalle— y el material pasa a ser un
/// número. El costo es que todas las rebanadas comparten tamaño y formato: por eso el paquete
/// redimensiona en vez de llevar cada textura en su resolución original, y elige el lado según cuántas
/// ranuras tenga la escena (una escena de dos materiales se lleva 512²; una de doce, 384², que es lo
/// que mantiene el arreglo en decenas de MB y no en cientos).
///
/// Los mips se generan ACÁ, en el CPU, y no con el generador de la API: el atlas se sube por niveles
/// en los cuatro backends por el mismo camino (una copia por subrecurso) y así una rebanada lejana no
/// hierve.
///
/// OJO: el orden de las rebanadas es parte del contrato con el shader
/// (<c>rebanada = ranura * MapsPerSlot + mapa</c>), junto con el índice de la ranura neutra.
/// </summary>
public sealed class MaterialAtlas
{
    /// <summary>Rebanadas por material: albedo, normales y ARM (oclusión/rugosidad/metalicidad).</summary>
    public const int MapsPerSlot = 3;

    /// <summary>
    /// Ranuras de material de una escena. Es un límite DURO del layout (el índice viaja por instancia
    /// como el índice de rebanada del arreglo, ver <c>SceneMesh.RenderInstances</c>) y también del
    /// presupuesto de memoria: el lado de cada rebanada BAJA a medida que la escena pide más ranuras
    /// (ver <see cref="Build"/>), así que más materiales no es más memoria sin control.
    ///
    /// Por qué 64: una escena con veinte modelos de fotogrametría tiene un material DISTINTO por
    /// modelo (cada uno trae sus propias texturas, y son objetos distintos aunque el nombre coincida),
    /// y con el tope viejo de 16 la mitad caía a la ranura neutra: los modelos se dibujaban con
    /// blanco plano, sin sus texturas. El índice sigue siendo un número chico (la ranura × 3 mapas
    /// entra en 192 rebanadas, dentro del tope de 2048 que tienen los arreglos en las cuatro APIs).
    /// </summary>
    public const int MaxSlots = 64;

    /// <summary>Ranura NEUTRA: albedo blanco, normal plana y ARM rugoso-dieléctrico. La usan los
    /// materiales sin texturas (carteles, ventanas) y cualquier malla que se pase de ranuras.</summary>
    public const int NeutralSlot = 0;

    private MaterialAtlas(int size, int sliceCount, int mipCount, byte[] pixels, int[] mipOffsets, bool overflowed)
    {
        Size = size;
        SliceCount = sliceCount;
        MipCount = mipCount;
        Pixels = pixels;
        MipOffsets = mipOffsets;
        SliceBytes = mipOffsets[mipCount - 1] + MipSize(mipCount - 1) * MipSize(mipCount - 1) * 4;
        Overflowed = overflowed;
    }

    /// <summary>Si la escena pidió más ranuras que <see cref="MaxSlots"/>: las que sobran se dibujan
    /// con la neutra (color del material, sin sus texturas). Es un dato de DIAGNÓSTICO: una escena
    /// nueva que lo encienda se ve sin texturas y hay que subir el tope o bajar la cantidad de
    /// materiales distintos.</summary>
    public bool Overflowed { get; }

    /// <summary>Lado de cada rebanada, en texeles (todas las rebanadas miden lo mismo).</summary>
    public int Size { get; }

    /// <summary>Cantidad de rebanadas del arreglo (múltiplo de <see cref="MapsPerSlot"/>).</summary>
    public int SliceCount { get; }

    /// <summary>Niveles de mip del arreglo (todos hasta 1×1).</summary>
    public int MipCount { get; }

    /// <summary>Píxeles RGBA8 (como los quieren las cuatro APIs): rebanada mayor —la rebanada 0 completa
    /// con todos sus mips, después la 1…—.</summary>
    public byte[] Pixels { get; }

    /// <summary>Offset de cada nivel DENTRO de una rebanada, en bytes.</summary>
    public int[] MipOffsets { get; }

    /// <summary>Bytes de UNA rebanada con todos sus mips.</summary>
    public int SliceBytes { get; }

    /// <summary>Bytes totales del arreglo (para el informe y los diagnósticos).</summary>
    public long ByteCount => Pixels.LongLength;

    /// <summary>Lado del nivel indicado, en texeles.</summary>
    public int MipSize(int level) => Math.Max(1, Size >> level);

    /// <summary>
    /// Arma el atlas de una escena y ASIGNA la ranura de cada malla
    /// (<see cref="SceneMesh.MaterialSlot"/>). Es idempotente: una malla sin material queda en la
    /// ranura neutra, y dos materiales que comparten exactamente las mismas texturas comparten ranura
    /// (el auto tiene varias primitivas y es común que compartan la chapa).
    /// </summary>
    internal static MaterialAtlas Build(SceneDefinition scene)
    {
        var slots = new List<SceneTextureSource?[]>(MaxSlots) { new SceneTextureSource?[MapsPerSlot] };
        bool overflow = false;

        foreach (var mesh in scene.Meshes)
        {
            mesh.MaterialSlot = -1;
            var material = mesh.Material;
            if (material == null) continue;

            var maps = new SceneTextureSource?[] { material.Albedo, material.Normal, material.Arm };
            if (maps[0] == null && maps[1] == null && maps[2] == null)
            {
                // Sin texturas: la ranura neutra ya es exactamente eso (blanco + normal plana + ARM
                // rugoso). El color y la emisión del material siguen viajando por instancia.
                mesh.MaterialSlot = NeutralSlot;
                continue;
            }

            int found = -1;
            for (int slot = 1; slot < slots.Count && found < 0; slot++)
            {
                if (Same(slots[slot], maps)) found = slot;
            }

            if (found < 0)
            {
                if (slots.Count >= MaxSlots)
                {
                    // Se acabaron las ranuras: la malla cae a la neutra (se ve el color del material
                    // sin sus texturas) y la escena lo informa en vez de dibujar cualquier cosa.
                    mesh.MaterialSlot = NeutralSlot;
                    overflow = true;
                    continue;
                }
                slots.Add(maps);
                found = slots.Count - 1;
            }

            mesh.MaterialSlot = found;
        }

        // El lado de la rebanada sale de CUÁNTAS ranuras pidió la escena: pocas ranuras se llevan una
        // textura nítida, muchas se reparten el mismo presupuesto. Con los escalones viejos (512/384/
        // 256) una escena de veinte modelos caía en 256² con 60 rebanadas: veinte y pico de MB de
        // atlas para un detalle que no se ve a la distancia de los modelos.
        int size = slots.Count <= 6 ? 512
                 : slots.Count <= 12 ? 384
                 : slots.Count <= 24 ? 256
                 : 192;
        int mipCount = 1;
        while (Math.Max(1, size >> (mipCount - 1)) > 1) mipCount++;

        var mipOffsets = new int[mipCount];
        int offset = 0;
        for (int level = 0; level < mipCount; level++)
        {
            mipOffsets[level] = offset;
            int side = Math.Max(1, size >> level);
            offset += side * side * 4;
        }

        int sliceBytes = offset;
        int sliceCount = slots.Count * MapsPerSlot;
        var pixels = new byte[(long)sliceBytes * sliceCount];

        for (int slot = 0; slot < slots.Count; slot++)
        {
            for (int map = 0; map < MapsPerSlot; map++)
            {
                var destination = pixels.AsSpan((slot * MapsPerSlot + map) * sliceBytes, sliceBytes);
                var texture = slots[slot][map]?.Value;
                if (texture == null) FillNeutral(destination, mipOffsets, size, map);
                else Resize(texture.Rgba, texture.Width, texture.Height, size, destination[..(size * size * 4)]);
                BuildMips(destination, mipOffsets, size);
            }
        }

        return new MaterialAtlas(size, sliceCount, mipCount, pixels, mipOffsets, overflow);
    }

    private static bool Same(SceneTextureSource?[] left, SceneTextureSource?[] right)
    {
        for (int i = 0; i < MapsPerSlot; i++)
        {
            if (!ReferenceEquals(left[i], right[i])) return false;
        }
        return true;
    }

    /// <summary>Rellena una rebanada sin textura con su valor neutro (y sus mips, que son lo mismo).</summary>
    private static void FillNeutral(Span<byte> slice, int[] mipOffsets, int size, int map)
    {
        // albedo: blanco · normal: plana (0,0,1) · ARM: oclusión 1, rugosidad 1, metalicidad 0.
        //
        // OJO con el orden de escritura: el atlas es R8G8B8A8 (byte 0 = ROJO, ver SceneTexture), y el
        // shader lee arm.r = oclusión, arm.g = rugosidad, arm.b = metalicidad. Con los nombres
        // cambiados —como estaban— la rebanada neutra salía con la oclusión en 0 y la metalicidad en 1:
        // un material de color plano SIN luz ambiente y totalmente metálico, o sea un espejo negro. Se
        // notaba poco porque la usaban los carteles y las ventanas, que son emisivos y no sombrean;
        // los marcos, barandas y toldos de la calle sí la sombrean.
        byte red = map == 1 ? (byte)128 : (byte)255;
        byte green = map == 1 ? (byte)128 : (byte)255;
        byte blue = map == 2 ? (byte)0 : (byte)255;

        for (int level = 0; level < mipOffsets.Length; level++)
        {
            int side = Math.Max(1, size >> level);
            var span = slice[mipOffsets[level]..];
            for (int i = 0; i < side * side; i++)
            {
                span[i * 4 + 0] = red;
                span[i * 4 + 1] = green;
                span[i * 4 + 2] = blue;
                span[i * 4 + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Redimensiona al lado pedido promediando el área de origen de cada texel destino: es un filtro de
    /// caja, que en una reducción a la mitad es exactamente el promedio 2×2 y en un aumento degenera en
    /// vecino más cercano (nunca se ve peor que un estirado).
    /// </summary>
    private static void Resize(byte[] source, int sourceWidth, int sourceHeight, int size, Span<byte> destination)
    {
        for (int y = 0; y < size; y++)
        {
            int y0 = (int)((long)y * sourceHeight / size);
            int y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sourceHeight / size));
            for (int x = 0; x < size; x++)
            {
                int x0 = (int)((long)x * sourceWidth / size);
                int x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sourceWidth / size));

                int blue = 0, green = 0, red = 0, count = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * sourceWidth;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int index = (row + sx) * 4;
                        blue += source[index + 0];
                        green += source[index + 1];
                        red += source[index + 2];
                        count++;
                    }
                }

                int output = (y * size + x) * 4;
                destination[output + 0] = (byte)(blue / count);
                destination[output + 1] = (byte)(green / count);
                destination[output + 2] = (byte)(red / count);
                destination[output + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Genera los mips promediando 2×2 el nivel anterior. El promedio es del valor ALMACENADO (no del
    /// lineal): para el albedo la diferencia con el filtrado en lineal de la placa aparece solo en los
    /// bordes de mucho contraste, y para los mapas de datos (normal y ARM) es exactamente lo que
    /// corresponde.
    /// </summary>
    private static void BuildMips(Span<byte> slice, int[] mipOffsets, int size)
    {
        for (int level = 1; level < mipOffsets.Length; level++)
        {
            int side = Math.Max(1, size >> level);
            int previous = Math.Max(1, size >> (level - 1));
            var source = slice[mipOffsets[level - 1]..];
            var destination = slice[mipOffsets[level]..];

            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    int x0 = Math.Min(previous - 1, x * 2);
                    int y0 = Math.Min(previous - 1, y * 2);
                    int x1 = Math.Min(previous - 1, x0 + 1);
                    int y1 = Math.Min(previous - 1, y0 + 1);

                    int i00 = (y0 * previous + x0) * 4;
                    int i10 = (y0 * previous + x1) * 4;
                    int i01 = (y1 * previous + x0) * 4;
                    int i11 = (y1 * previous + x1) * 4;

                    int output = (y * side + x) * 4;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int sum = source[i00 + channel] + source[i10 + channel]
                                + source[i01 + channel] + source[i11 + channel];
                        destination[output + channel] = (byte)(sum / 4);
                    }
                    destination[output + 3] = 255;
                }
            }
        }
    }
}
