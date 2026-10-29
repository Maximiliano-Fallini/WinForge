using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// La fachada de la calle, armada con los MÓDULOS REALES del kit glTF del set
/// (<c>modular_urban_apartments_facade</c>, Poly Haven CC0) en vez de generar la geometría a mano.
///
/// Cómo es el kit: cada pieza es una PARED de 3 × 3 m autorada en su propia celda de plantilla, y
/// las que tienen un hueco vienen acompañadas por la pieza que lo RELLENA (la ventana o la puerta)
/// con el MISMO sufijo de nombre que la pared (<c>wall_window_centered_large_01</c> ↔
/// <c>window_centered_large_01</c>). Por eso el armado es:
/// <list type="number">
/// <item>cargar el modelo y agrupar las primitivas por NODO (cada nodo es una pieza);</item>
/// <item>quedarse con las paredes PLANAS de 3 × 3 —las esquineras y anguladas traen profundidad y
/// sirven para armar volúmenes, no la superficie del frente— y emparejar cada una con su relleno;</item>
/// <item>teselar la cuadra: una celda de 3 × 3 por columna y por piso, eligiendo un módulo distinto
/// por celda.</item>
/// </list>
///
/// Las piezas se PIVOTAN por la esquina mínima de su pared, así que el relleno cae exactamente en el
/// hueco de esa pared sin que haya que saber de antemano dónde está el agujero: se DESCUBRE agrupando
/// nodos, no con una tabla escrita a mano (si el kit cambia de nombres, el emparejado sigue valiendo
/// para todo lo que respete el prefijo <c>wall_</c>).
///
/// El giro es el MISMO que aplica el vertex shader al colocar (<c>RotateY</c>): por eso la pieza
/// autorada en el plano XY termina siendo la pared del frente, mirando a la calle.
/// </summary>
internal static class NeonFacade
{
    /// <summary>Lado de la celda del kit: las paredes planas miden 3 × 3 m.</summary>
    internal const float Cell = 3f;

    /// <summary>Carpeta del modelo dentro del set (ver <see cref="SceneDefinition.AssetSet"/>).</summary>
    private const string Slug = "modular_urban_apartments_facade";

    /// <summary>
    /// Una fila de fachada: uno de los dos frentes de un edificio, entre sus medianiles. El alto sale
    /// de los pisos (cada piso es una celda del kit), así que la pared nunca queda cortada a mitad.
    /// </summary>
    internal readonly record struct FacadeRow(float Side, float Z0, float Z1, int Floors, float Depth)
    {
        internal float Height => Floors * Cell;
    }

    /// <summary>Qué ocupa una celda de planta baja (ver <see cref="Shop"/>).</summary>
    internal enum GroundKind
    {
        /// <summary>Local con vidriera y puerta propia (ver <c>NeonScene.BuildShopfronts</c>).</summary>
        Shop,
        /// <summary>Portal de viviendas: puerta de edificio, escalón y farol (ver <c>BuildPortal</c>).</summary>
        Portal,
        /// <summary>Garaje con persiana metálica (ver <c>BuildGarage</c>).</summary>
        Garage
    }

    /// <summary>
    /// Una pieza de planta baja, en coordenadas de la fila de fachada: qué celdas ocupa, cuál es la de
    /// la puerta y qué estilo tiene. El GENERADOR de fachadas lo necesita para no poner muros del kit
    /// en esas celdas —ahí va la geometría armada a mano (local, portal o garaje)—; el mismo dato lo
    /// usa el armado para saber dónde cae cada celda.
    /// </summary>
    internal readonly record struct Shop(
        int Row, int FirstColumn, int Columns, int DoorColumn, int Style,
        GroundKind Kind = GroundKind.Shop);

    /// <summary>Una pieza del kit: la pared (y su relleno, si lo tiene) con su pivote y su medida.</summary>
    private sealed class Module
    {
        public required string Name { get; init; }
        public required Vector3 Pivot { get; init; }
        public required List<GltfPart> Parts { get; init; }
        public required bool IsDoor { get; init; }
        /// <summary>False solo para los muros CIEGOS del kit: en una calle de noche se leen como un
        /// hueco sin terminar, así que no entran al pool de ventanas (ver <see cref="Build"/>).</summary>
        public bool HasOpening { get; init; }
        public float DoorBottom { get; init; }
    }

    /// <summary>
    /// Arma las mallas de las fachadas. Devuelve una lista vacía (y lo anota) si el kit no está: la
    /// escena sigue dibujando el resto en vez de tumbar la corrida por un asset.
    /// </summary>
    internal static IReadOnlyList<SceneMesh> Build(
        string root, IReadOnlyList<FacadeRow> rows, float facadeOffset,
        IReadOnlyList<Shop> shops, Random rng, Action<string> note)
    {
        var meshes = new List<SceneMesh>();
        if (rows.Count == 0) return meshes;

        string path = Path.Combine(root, "models", Slug, Slug + "_1k.gltf");
        if (!File.Exists(path))
        {
            note($"Falta el kit de fachada '{Slug}': la cuadra se arma sin fachadas.");
            return meshes;
        }

        GltfModel model;
        try
        {
            model = GltfModel.Load(path);
        }
        catch (Exception exception)
        {
            note($"No se pudo cargar el kit de fachada '{Slug}' ({exception.GetType().Name}): la cuadra va sin fachadas.");
            return meshes;
        }

        var skipped = new List<string>();
        var modules = MatchModules(GroupByNode(model), skipped);
        if (modules.Count == 0)
        {
            note($"El kit de fachada '{Slug}' no trajo ninguna pared plana de 3 × 3.");
            return meshes;
        }
        if (skipped.Count > 0)
        {
            note($"El kit de fachada trae paredes con hueco y sin relleno ({string.Join(", ", skipped)}): " +
                 $"no entran en la fachada, porque dejarían el interior del edificio a la vista.");
        }

        // Las entradas que trae el kit deben terminar cerca del piso de la celda;
        // algunos módulos llamados door son balcones o tienen el umbral elevado.
        var doors = modules.Where(m => m.IsDoor && MathF.Abs(m.DoorBottom) < 0.20f).ToList();
        // Solo muros CON hueco: el muro ciego (wall_standard_standard_01) deja una pared lisa de 3 × 3
        // en medio de una fachada de ventanas, y a la luz de la calle eso no se lee como un muro a
        // propósito sino como un edificio sin terminar. Si el kit no trajera ninguno con hueco, se
        // cae al pool completo para no quedarse sin fachada.
        var windows = modules.Where(m => !m.IsDoor && m.HasOpening).ToList();
        if (windows.Count == 0) windows = modules.Where(m => m.HasOpening).ToList();
        if (windows.Count == 0) windows = modules;

        // Celdas de planta baja que ocupa un local: no llevan módulo del kit (la vidriera y la puerta
        // las arma NeonScene.BuildShopfronts, en el mismo plano y con el mismo alto de celda).
        var reserved = new HashSet<(int Row, int Column)>();
        var withBays = new bool[rows.Count];
        foreach (var shop in shops)
        {
            if (shop.Row < 0 || shop.Row >= rows.Count) continue;
            withBays[shop.Row] = true;
            for (int column = 0; column < shop.Columns; column++)
                reserved.Add((shop.Row, shop.FirstColumn + column));
        }

        // Una lista de colocaciones por PIEZA: cada primitiva es una malla del motor (dibuja sin índice
        // y con un material por malla) y todas sus celdas viajan como instancias suyas.
        var placements = new Dictionary<GltfPart, List<SceneInstance>>();

        // El kit trae el vidrio con alphaMode BLEND y una textura oscura. Este motor dibuja los
        // materiales opacos (no hay pase de transparencias), así que ese vidrio salía NEGRO y las
        // ventanas se leían como agujeros vacíos. Acá se reemplaza solo para las piezas de vidrio
        // por un material nocturno: refleja el entorno y, en las ventanas encendidas, emite la
        // tinta cálida o fría que le toca a cada celda. Los marcos y las paredes no se tocan.
        var windowGlass = new SceneMaterial
        {
            Name = "vidrio de fachada",
            Tint = Vector3.One,
            RoughnessScale = 0.10f,
            MetallicScale = 0.50f,
            NormalStrength = 0f,
            Emissive = Vector3.One,
            EmissiveStrength = 0f
        };

        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            // El giro deja el +Z local (el frente de la pieza) mirando al centro de la calle.
            float yaw = row.Side > 0f ? -MathF.PI * 0.5f : MathF.PI * 0.5f;
            float x = row.Side * facadeOffset;
            int columns = Math.Max(1, (int)MathF.Round((row.Z1 - row.Z0) / Cell));
            // Cada edificio conserva un lenguaje de ventanas: variación entre edificios,
            // no una pieza distinta al azar en cada piso y columna.
            var upperModules = new Module[columns];
            for (int column = 0; column < columns; column++)
                upperModules[column] = windows[rng.Next(windows.Count)];
            Vector3[] palette =
            {
                new(1.10f, 0.91f, 0.80f), new(0.78f, 0.88f, 0.94f),
                new(0.83f, 0.75f, 0.71f), new(1.05f, 1.01f, 0.91f),
                new(0.78f, 0.83f, 0.76f)
            };
            var facadeTint = palette[rng.Next(palette.Length)];

            for (int floor = 0; floor < row.Floors; floor++)
            {
                float baseY = floor * Cell;

                for (int column = 0; column < columns; column++)
                {
                    // La celda de un local no lleva módulo: la ocupa la vidriera.
                    if (floor == 0 && reserved.Contains((rowIndex, column))) continue;

                    // La planta baja la arma NeonScene (local, portal o garaje) en las celdas
                    // reservadas; las que quedan libres son ventanas, nunca un muro ciego. Solo una
                    // fila sin NINGUNA pieza cae a la puerta del kit como entrada de emergencia.
                    bool ground = floor == 0;
                    var module = ground && !withBays[rowIndex] && doors.Count > 0 && column == columns / 2
                        ? doors[rng.Next(doors.Count)]
                        : upperModules[column];

                    // Ancla de la celda. La vereda de la IZQUIERDA crece hacia −Z (con ese giro su +X
                    // local apunta ahí), así que su celda se ancla desde Z1.
                    float z = row.Side > 0f ? row.Z0 + column * Cell : row.Z1 - column * Cell;
                    var target = new Vector3(x, baseY + 0.16f, z);

                    // Pivote: la pieza se corre por su esquina mínima para que su celda arranque en el
                    // ancla (mismo criterio para la pared y para su relleno, que vive en la misma celda).
                    var origin = target - RotateY(module.Pivot, yaw);
                    var instance = new SceneInstance(
                        new Vector4(origin, 1f),
                        new Vector4(yaw, 0f, SceneDefinition.KindTextured, 0f));

                    // La luz de cada ventana se decide con un hash de la celda, no con el RNG del
                    // armado: agregar una tirada acá cambiaría la elección de módulos y todo el
                    // frente. Un tercio de las ventanas queda encendido, con mayoría cálida.
                    uint windowHash = (uint)(rowIndex * 73856093) ^ (uint)(floor * 19349663) ^
                                      (uint)(column * 83492791) ^ (row.Side > 0f ? 0x9E3779B9u : 0u);
                    // Las puertas del kit son entradas de vivienda: el vidrio de la hoja va SIEMPRE
                    // encendido, aunque la ventana de al lado esté apagada. Si no, la puerta queda
                    // como un rectángulo negro en la planta baja y se lee como un hueco.
                    bool entrance = module.IsDoor;
                    bool litWindow = entrance || windowHash % 100u < 34u;
                    bool coolWindow = !entrance && (windowHash / 100u) % 100u < 30u;
                    var glassTint = litWindow
                        ? (coolWindow ? new Vector3(0.55f, 0.72f, 1.00f) : new Vector3(1.00f, 0.66f, 0.36f))
                        : new Vector3(0.10f, 0.14f, 0.22f);
                    var glassParams = litWindow
                        ? new Vector4(0f, 0.55f, 0f, coolWindow ? 0.18f : 0.24f)
                        : new Vector4(0f, 0.08f, 0.55f, 0f);

                    foreach (var part in module.Parts)
                    {
                        if (!placements.TryGetValue(part, out var list))
                        {
                            list = new List<SceneInstance>();
                            placements[part] = list;
                        }

                        if (part.Name.StartsWith("wall_", StringComparison.Ordinal))
                        {
                            list.Add(instance with
                            {
                                MaterialTint = new Vector4(part.Material.Tint * facadeTint, part.Material.UvScale)
                            });
                        }
                        else if (IsGlass(part))
                        {
                            list.Add(instance with
                            {
                                MaterialTint = new Vector4(glassTint, 1f),
                                MaterialParams = glassParams
                            });
                        }
                        else
                        {
                            // Puertas y marcos conservan su material y su textura.
                            list.Add(instance);
                        }
                    }
                }
            }
        }

        foreach (var pair in placements)
        {
            meshes.Add(new SceneMesh
            {
                Name = $"fachada · {pair.Key.Name}",
                Vertices = pair.Key.Vertices,
                Instances = pair.Value.ToArray(),
                Material = IsGlass(pair.Key) ? windowGlass : pair.Key.Material
            });
        }

        return meshes;
    }

    /// <summary>Si la primitiva es el VIDRIO del kit (paños de ventana o de puerta).</summary>
    private static bool IsGlass(GltfPart part) =>
        part.Material.Name.Contains("glass", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Agrupa las primitivas del modelo por NODO. <see cref="GltfModel.Load"/> nombra cada primitiva
    /// <c>nodo/índice</c>, así que el nodo se recupera cortando por el último separador.
    /// </summary>
    private static List<(string Name, List<GltfPart> Parts, Vector3 Min, Vector3 Max)> GroupByNode(GltfModel model)
    {
        var order = new List<string>();
        var map = new Dictionary<string, (List<GltfPart> Parts, Vector3 Min, Vector3 Max)>(StringComparer.Ordinal);

        foreach (var part in model.Parts)
        {
            int cut = part.Name.LastIndexOf('/');
            string node = cut >= 0 ? part.Name[..cut] : part.Name;

            if (!map.TryGetValue(node, out var entry))
            {
                entry = (new List<GltfPart>(), new Vector3(float.MaxValue), new Vector3(float.MinValue));
                order.Add(node);
            }

            entry.Parts.Add(part);
            foreach (var vertex in part.Vertices)
            {
                entry.Min = Vector3.Min(entry.Min, vertex.Position);
                entry.Max = Vector3.Max(entry.Max, vertex.Position);
            }
            map[node] = entry;
        }

        var pieces = new List<(string, List<GltfPart>, Vector3, Vector3)>(order.Count);
        foreach (var node in order)
        {
            var entry = map[node];
            pieces.Add((node, entry.Parts, entry.Min, entry.Max));
        }
        return pieces;
    }

    /// <summary>
    /// Empareja cada PARED con el relleno de su hueco. La regla es el nombre: a
    /// <c>wall_&lt;resto&gt;</c> le corresponde <c>&lt;resto&gt;</c> si existe. Las paredes SIN hueco
    /// (una lisa como <c>wall_standard_standard_01</c>) son módulos completos por sí mismas.
    ///
    /// La pared que tiene un hueco y NO tiene relleno en el kit se DESCARTA, y eso que se saltea no
    /// es un detalle: el kit 1k trae <c>wall_door_window_small_013</c> —una puerta y una ventana
    /// recortadas— sin su pieza <c>door_window_small_013</c>. Elegida en una celda dejaba dos AGUJEROS
    /// al descubierto: mirando la fachada se veía el interior vacío de la manzana, que es exactamente
    /// lo que hace que un edificio se lea hueco. El hueco se detecta por GEOMETRÍA (el área proyectada
    /// de sus triángulos contra el área de su caja, ver <see cref="HasOpening"/>) y no por el nombre: si
    /// el kit cambia de nombres, el emparejado sigue valiendo.
    /// </summary>
    private static List<Module> MatchModules(
        List<(string Name, List<GltfPart> Parts, Vector3 Min, Vector3 Max)> pieces, List<string> skipped)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < pieces.Count; i++) index[pieces[i].Name] = i;

        var modules = new List<Module>();
        foreach (var piece in pieces)
        {
            if (!piece.Name.StartsWith("wall_", StringComparison.Ordinal)) continue;

            var size = piece.Max - piece.Min;
            // Solo las paredes PLANAS de 3 × 3: una esquina o una angulada trae profundidad (su caja en
            // Z es grande) y es para armar volúmenes, no la superficie del frente.
            if (MathF.Abs(size.X - Cell) > 0.6f || MathF.Abs(size.Y - Cell) > 0.6f || size.Z > 0.6f) continue;

            var parts = new List<GltfPart>(piece.Parts);
            string insertName = piece.Name["wall_".Length..];
            if (index.TryGetValue(insertName, out int insert))
            {
                parts.AddRange(pieces[insert].Parts);
            }
            else if (HasOpening(piece.Parts, size))
            {
                // Hueco sin relleno: la celda no se puede cerrar, así que el módulo no entra al pool.
                skipped.Add(piece.Name);
                continue;
            }

            modules.Add(new Module
            {
                Name = piece.Name,
                Pivot = piece.Min,
                Parts = parts,
                IsDoor = piece.Name.Contains("door", StringComparison.Ordinal),
                // Con relleno emparejado el hueco existe seguro; sin relleno, la geometría lo confirma
                // (los muros ciegos son los únicos que llegan acá sin hueco).
                HasOpening = index.TryGetValue(insertName, out _) || HasOpening(piece.Parts, size),
                DoorBottom = index.TryGetValue(insertName, out int doorInsert)
                    ? pieces[doorInsert].Min.Y - piece.Min.Y
                    : float.PositiveInfinity
            });
        }
        return modules;
    }

    /// <summary>
    /// ¿La pared tiene un hueco? Se responde con el ÁREA PROYECTADA: una pared lisa cubre toda su caja
    /// (3 × 3 m), y una con una ventana o una puerta recortada cubre menos, porque los triángulos no
    /// pasan por el agujero. Es la única comprobación que no depende de cómo se llame la pieza.
    /// </summary>
    private static bool HasOpening(IReadOnlyList<GltfPart> parts, Vector3 size)
    {
        double covered = 0.0;
        foreach (var part in parts)
        {
            var vertices = part.Vertices;
            // Sin índice (el motor dibuja listas de triángulos): cada tres vértices son un triángulo.
            for (int i = 0; i + 2 < vertices.Length; i += 3)
            {
                var a = vertices[i].Position;
                var b = vertices[i + 1].Position;
                var c = vertices[i + 2].Position;
                covered += Math.Abs((double)(b.X - a.X) * (c.Y - a.Y) - (double)(c.X - a.X) * (b.Y - a.Y)) * 0.5;
            }
        }

        double box = (double)size.X * size.Y;
        return box > 0.01 && covered < box * 0.98;
    }

    /// <summary>
    /// Giro en Y, EXACTAMENTE el mismo que aplica el vertex shader al colocar (<c>RotateY</c> en el
    /// HLSL): <c>(x·cos + z·sen, y, −x·sen + z·cos)</c>. Si acá cambiara un signo, la fachada saldría
    /// girada y las piezas no caerían en su hueco.
    /// </summary>
    private static Vector3 RotateY(Vector3 value, float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        return new Vector3(value.X * cos + value.Z * sin, value.Y, -value.X * sin + value.Z * cos);
    }
}