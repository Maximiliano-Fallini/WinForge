using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// Assets REALES del mundo Frutiger Aero (set <c>aero</c>, Poly Haven CC0): la vegetación y el
/// mobiliario de fotogrametría del prado, la orilla del lago y el paseo.
///
/// Por qué existe este archivo: la escena aero nació 100% procedural (torres, casas, árboles y
/// flores facetados armados acá a mano) y el terreno se veía "de maqueta". Acá vive lo que la
/// vuelve un mundo con contenido real: se descargan los modelos y las texturas con
/// <c>tools/benchmark-assets/fetch-assets.ps1 -Set aero</c> y la escena los coloca por instancias
/// sobre el campo de altura, igual que la calle nocturna coloca sus modelos.
///
/// A diferencia de la escena neón, acá el set es OPCIONAL: si falta, la escena se arma con el
/// contenido procedural de siempre y lo informa en <see cref="Notes"/> en vez de tumbarse. La ciudad
/// (torres, casas, locales) sigue siendo geometría propia: Poly Haven no tiene edificios.
/// </summary>
internal static class AeroAssets
{
    /// <summary>Nombre de la carpeta del set (ver <c>files/benchmark-assets/fetch-assets.ps1</c>).</summary>
    internal const string SetName = "aero";

    private static readonly List<string> BuildNotes = new();
    private static string? root;
    private static bool rootResolved;

    /// <summary>Lo que la escena aporta al informe cuando falta el set o un modelo (se llena al armar).</summary>
    internal static IReadOnlyList<string> Notes
    {
        get { lock (BuildNotes) return BuildNotes.ToArray(); }
    }

    private static void Note(string message)
    {
        lock (BuildNotes)
        {
            if (!BuildNotes.Contains(message)) BuildNotes.Add(message);
        }
    }

    /// <summary>
    /// Carpeta del set, o <c>null</c> si no está. Se resuelve UNA vez: buscarla en cada modelo sería
    /// recorrer las mismas carpetas decenas de veces.
    /// </summary>
    internal static string? Root
    {
        get
        {
            lock (BuildNotes)
            {
                if (rootResolved) return root;
                rootResolved = true;
                try
                {
                    root = SceneAssets.Root(SetName);
                }
                catch (DirectoryNotFoundException exception)
                {
                    // Sin set: la escena sigue con lo procedural. El mensaje ya trae cómo bajarlo.
                    Note(exception.Message);
                    root = null;
                }
                return root;
            }
        }
    }

    /// <summary>
    /// Una textura PBR del set (<paramref name="channel"/> = <c>diff</c>, <c>nor_gl</c> o <c>arm</c>).
    /// El nombre del archivo lo pone Poly Haven con el canal adentro, así que se busca por el canal
    /// en vez de adivinar el sufijo. Es la MISMA convención que usa la escena neón.
    /// </summary>
    internal static SceneTextureSource? Texture(string root, string slug, string channel)
    {
        string directory = Path.Combine(root, "textures", slug);
        if (!Directory.Exists(directory)) return null;

        foreach (var file in Directory.EnumerateFiles(directory, "*.jpg"))
        {
            string name = Path.GetFileName(file).ToLowerInvariant();
            bool matches = channel switch
            {
                "diff" => name.Contains("diff"),
                "nor_gl" => name.Contains("nor"),
                "arm" => name.Contains("arm"),
                _ => name.Contains(channel)
            };
            if (matches) return SceneTextureSource.FromFile(file, srgb: channel == "diff");
        }

        Note($"Falta la textura '{slug}' ({channel}) en el set de assets.");
        return null;
    }

    /// <summary>
    /// Carga un modelo glTF del set (una sola vez, ver <see cref="Load"/>) y lo agrega como una malla
    /// POR PRIMITIVA, todas con las mismas
    /// instancias (cada primitiva trae su material, y el motor dibuja una malla por material).
    ///
    /// <paramref name="sinkToGround"/> corrige los modelos que no traen el origen en la base: la caja
    /// del modelo ya viene medida por <see cref="GltfModel"/>, así que la bajada se resuelve con la
    /// instancia (traslación pura + giro en Y + escala uniforme) sin tocar un solo vértice.
    /// </summary>
    internal static void AddModel(
        List<SceneMesh> meshes,
        string root,
        string slug,
        string label,
        IReadOnlyList<SceneInstance> instances,
        bool sinkToGround = true)
    {
        var model = Load(root, slug);
        if (model == null) return;

        float drop = sinkToGround ? model.Min.Y : 0f;
        SceneInstance[] placed = drop == 0f ? instances.ToArray() : Sink(instances, drop);

        foreach (var part in model.Parts)
        {
            meshes.Add(new SceneMesh
            {
                Name = $"{label} · {part.Name}",
                Vertices = part.Vertices,
                Instances = placed,
                Material = part.Material
            });
        }
    }

    /// <summary>
    /// Modelos ya LEÍDOS, por nombre. Cargar un glTF de fotogrametría es leer el archivo y expandir
    /// cientos de miles de triángulos, así que se hace UNA vez: la altura que despeja la escala, la
    /// bajada al piso y las primitivas salen del mismo objeto.
    /// </summary>
    private static readonly Dictionary<string, GltfModel?> Cache = new(StringComparer.Ordinal);

    private static GltfModel? Load(string root, string slug)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(slug, out var cached)) return cached;

            try
            {
                string path = Path.Combine(root, "models", slug, slug + "_1k.gltf");
                if (!File.Exists(path))
                {
                    Note($"Falta el modelo '{slug}': la escena corre sin él.");
                    cached = null;
                }
                else
                {
                    cached = GltfModel.Load(path);
                }
            }
            catch (Exception exception)
            {
                Note($"No se pudo cargar '{slug}' ({exception.GetType().Name}): la escena corre sin él.");
                cached = null;
            }

            Cache[slug] = cached;
            return cached;
        }
    }

    /// <summary>
    /// Alto de un modelo en metros (0 si no está). Lo usa la escala de la escena: cada objeto pide
    /// cuánto tiene que medir y el factor sale de acá, en vez de un ×N a ojo.
    /// </summary>
    internal static float HeightOf(string root, string slug) => Load(root, slug)?.Size.Y ?? 0f;

    /// <summary>
    /// Factor de escala para que el modelo mida <paramref name="targetHeight"/> metros de alto. Es el
    /// ÚNICO camino por el que la escena escala modelos: pedir METROS hace imposible el error que ya
    /// pasó (una planta rastrera de 0,17 m multiplicada por 8 se convierte en una alfombra de cuatro
    /// metros que tapa el cuadro y se lee como una chapa, no como una planta).
    /// </summary>
    internal static float ScaleFor(string root, string slug, float targetHeight)
    {
        float height = HeightOf(root, slug);
        return height > 0.01f ? targetHeight / height : 1f;
    }

    /// <summary>Baja cada instancia lo que sobresale el modelo por debajo del origen (ver AddModel).</summary>
    private static SceneInstance[] Sink(IReadOnlyList<SceneInstance> instances, float drop)
    {
        var sunk = new SceneInstance[instances.Count];
        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            var placement = instance.PositionScale;
            sunk[i] = instance with
            {
                PositionScale = new Vector4(
                    placement.X,
                    placement.Y - drop * placement.W,
                    placement.Z,
                    placement.W)
            };
        }
        return sunk;
    }

    /// <summary>Descripción del set para el informe.</summary>
    internal static string DescribeAssets() =>
        $"assets: set '{SetName}' (Poly Haven, CC0) - modelos de fotogrametría en el prado, la orilla y el paseo";
}
