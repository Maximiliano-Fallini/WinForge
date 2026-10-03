using System.Numerics;
using SharpGLTF.Schema2;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// Una primitiva del modelo ya convertida al formato del motor: vértices con posición, normal y UV,
/// más el material con sus texturas ya decodificadas.
/// </summary>
public sealed class GltfPart
{
    public required string Name { get; init; }

    public required SceneVertex[] Vertices { get; init; }

    public required SceneMaterial Material { get; init; }

    /// <summary>Cuántas caras tiene (para el informe y los diagnósticos).</summary>
    public int TriangleCount => Vertices.Length / 3;
}

/// <summary>
/// Modelo glTF real (los assets CC0 de Poly Haven) cargado al formato del motor.
///
/// Por qué glTF: es el formato del catálogo elegido (ver README cinematográfico §5) y trae TODO
/// junto —geometría con UV, materiales PBR y sus texturas—, así que el motor no necesita un formato
/// propio ni un conversor aparte. Acá se usa <c>SharpGLTF</c> (MIT): resuelve el grafo de nodos con
/// sus transformaciones, los accessors con o sin <c>byteStride</c> y las imágenes externas, que es
/// justo la parte que uno escribe mal a mano.
///
/// Lo que sale de acá es DATO: las cuatro APIs dibujan estas mismas mallas (ver
/// <see cref="SceneDefinition"/>), así que el modelo se ve igual en las cuatro por construcción.
/// </summary>
public sealed class GltfModel
{
    public required string Name { get; init; }

    public required IReadOnlyList<GltfPart> Parts { get; init; }

    /// <summary>Caja del modelo en SU espacio local (antes de instanciarlo). Sirve para apoyarlo
    /// en el piso y para ubicar la cámara sin adivinar.</summary>
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }

    public Vector3 Size => Max - Min;

    public int TriangleCount
    {
        get
        {
            int total = 0;
            foreach (var part in Parts) total += part.TriangleCount;
            return total;
        }
    }

    /// <summary>
    /// Carga un .gltf (o .glb) y devuelve sus primitivas ya listas para instanciar.
    /// <paramref name="uvScale"/> multiplica las UVs: para materiales que se repiten (piso, paredes)
    /// el modelo trae la UV 0..1 y el que repite es el material.
    /// </summary>
    /// <exception cref="FileNotFoundException">Si el archivo no está (el set de assets no se bajó).</exception>
    public static GltfModel Load(string path, float uvScale = 1f)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No está el modelo '{Path.GetFileName(path)}'", path);
        }

        var root = ModelRoot.Load(path);
        var parts = new List<GltfPart>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        var materials = new Dictionary<string, SceneMaterial>(StringComparer.Ordinal);
        string modelDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";

        foreach (var node in root.LogicalNodes)
        {
            if (node.Mesh == null) continue;

            // La transformación del NODO (posición, giro, escala del árbol) se APLICA acá: el motor
            // instancia con posición + giro en Y + escala (ver SceneInstance), así que lo que no sea
            // eso hay que hornearlo en los vértices.
            var world = node.WorldMatrix;

            foreach (var primitive in node.Mesh.Primitives)
            {
                var positions = primitive.GetVertexAccessor("POSITION");
                if (positions == null) continue;

                var positionArray = positions.AsVector3Array();
                var normalArray = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var uvArray = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();

                var material = ResolveMaterial(primitive.Material, materials, modelDirectory);

                // El motor dibuja SIN índice (ver SceneMesh): se expanden los triángulos del glTF a
                // una lista de vértices. Un modelo indexado ocupa menos en disco y se sube una vez por
                // escena, así que el costo de expandir es del arranque, no del frame.
                var indices = primitive.GetIndexAccessor()?.AsIndicesArray();
                var vertices = new List<SceneVertex>(indices?.Count ?? positionArray.Count);

                for (int i = 0; i < (indices?.Count ?? positionArray.Count); i++)
                {
                    int source = indices != null ? (int)indices[i] : i;
                    if ((uint)source >= (uint)positionArray.Count) continue;

                    // Posición al mundo del nodo. Las normales van con la parte rotacional (la escala
                    // de estos modelos es uniforme, así que normalizar alcanza).
                    Vector3 position = Vector3.Transform(positionArray[source], world);
                    Vector3 normal = normalArray != null && source < normalArray.Count
                        ? Vector3.Normalize(Vector3.TransformNormal(normalArray[source], world))
                        : Vector3.UnitY;
                    Vector2 uv = uvArray != null && source < uvArray.Count
                        ? uvArray[source] * uvScale
                        : Vector2.Zero;

                    vertices.Add(new SceneVertex(position, normal) { Uv = uv });
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                }

                parts.Add(new GltfPart
                {
                    Name = $"{node.Name ?? node.Mesh.Name ?? "malla"}/{primitive.LogicalIndex}",
                    Vertices = vertices.ToArray(),
                    Material = material
                });
            }
        }

        if (parts.Count == 0)
        {
            throw new InvalidDataException($"El modelo '{Path.GetFileName(path)}' no tiene ninguna primitiva con POSITION");
        }

        return new GltfModel
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Parts = parts,
            Min = min,
            Max = max
        };
    }

    /// <summary>
    /// Material del glTF, con sus texturas decodificadas una sola vez (los materiales se repiten
    /// entre primitivas del mismo modelo: dos ruedas y la carrocería comparten el suyo).
    /// </summary>
    private static SceneMaterial ResolveMaterial(
        Material? material, Dictionary<string, SceneMaterial> cache, string modelDirectory)
    {
        string key = material?.LogicalIndex.ToString() ?? "sin-material";
        if (cache.TryGetValue(key, out var cached)) return cached;

        var tint = new Vector3(0.82f, 0.82f, 0.82f);
        var albedo = LoadChannel(material, "BaseColor", modelDirectory, srgb: true);
        var normal = LoadChannel(material, "Normal", modelDirectory, srgb: false);
        var arm = LoadChannel(material, "MetallicRoughness", modelDirectory, srgb: false);

        // Los BYTES de la imagen se copian acá (ya están en memoria: los leyó SharpGLTF al abrir el
        // .gltf) y la DECODIFICACIÓN de JPEG queda para el primer pedido: es lo caro (ver
        // SceneTextureSource) y el catálogo de escenas no tiene por qué pagarlo al abrir la página.

        if (material != null)
        {
            var baseColor = material.FindChannel("BaseColor");
            if (baseColor.HasValue)
            {
                var color = baseColor.Value.Color;
                tint = new Vector3(color.X, color.Y, color.Z);
            }
        }

        var resolved = new SceneMaterial
        {
            Name = material?.Name ?? "sin-material",
            Tint = tint,
            Albedo = albedo,
            Normal = normal,
            Arm = arm,
            // Estos modelos vienen en metros y su UV está pensada para verse tal cual: si se repite
            // se convierte en una calcomanía.
            UvScale = 1f
        };

        cache[key] = resolved;
        return resolved;
    }

    /// <summary>
    /// Textura de un canal del material. El motor espera píxeles ya decodificados: si la imagen
    /// viene embebida (glb) se usa tal cual y si vive al lado del .gltf se lee del disco.
    /// </summary>
    private static SceneTextureSource? LoadChannel(Material? material, string channelName, string modelDirectory, bool srgb)
    {
        var channel = material?.FindChannel(channelName);
        if (!channel.HasValue) return null;

        var image = channel.Value.Texture?.PrimaryImage;
        if (image == null) return null;

        string name = image.Name ?? channelName;

        // SharpGLTF ya trae los bytes de la imagen (del .glb embebido o del archivo de al lado:
        // SourcePath viene resuelto contra el .gltf). Si no los trae, quedan para el primer pedido.
        var content = image.Content;
        byte[]? eager = (!content.IsEmpty && content.Content.Length > 0) ? content.Content.ToArray() : null;
        string? sourcePath = content.SourcePath;

        return SceneTextureSource.FromBytes(
            () =>
            {
                if (eager != null) return eager;
                if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath)) return File.ReadAllBytes(sourcePath);
                return null;
            },
            name,
            srgb);
    }
}
