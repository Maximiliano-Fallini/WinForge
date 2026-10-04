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

    /// <summary>Una pieza del kit: la pared (y su relleno, si lo tiene) con su pivote y su medida.</summary>
    private sealed class Module
    {
        public required string Name { get; init; }
        public required Vector3 Pivot { get; init; }
        public required List<GltfPart> Parts { get; init; }
        public required bool IsDoor { get; init; }
    }

    /// <summary>
    /// Arma las mallas de las fachadas. Devuelve una lista vacía (y lo anota) si el kit no está: la
    /// escena sigue dibujando el resto en vez de tumbar la corrida por un asset.
    /// </summary>
    internal static IReadOnlyList<SceneMesh> Build(
        string root, IReadOnlyList<FacadeRow> rows, float facadeOffset, Random rng, Action<string> note)
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

        var doors = modules.Where(m => m.IsDoor).ToList();
        var windows = modules.Where(m => !m.IsDoor).ToList();
        if (windows.Count == 0) windows = modules;

        // Una lista de colocaciones por PIEZA: cada primitiva es una malla del motor (dibuja sin índice
        // y con un material por malla) y todas sus celdas viajan como instancias suyas.
        var placements = new Dictionary<GltfPart, List<SceneInstance>>();

        foreach (var row in rows)
        {
            // El giro deja el +Z local (el frente de la pieza) mirando al centro de la calle.
            float yaw = row.Side > 0f ? -MathF.PI * 0.5f : MathF.PI * 0.5f;
            float x = row.Side * facadeOffset;
            int columns = Math.Max(1, (int)MathF.Round((row.Z1 - row.Z0) / Cell));

            for (int floor = 0; floor < row.Floors; floor++)
            {
                float baseY = floor * Cell;

                for (int column = 0; column < columns; column++)
                {
                    // La planta baja mezcla entradas con ventanas; los pisos altos son todas ventanas.
                    bool ground = floor == 0 && doors.Count > 0;
                    var pool = ground && rng.NextDouble() < 0.45 ? doors : windows;
                    var module = pool[rng.Next(pool.Count)];

                    // Ancla de la celda. La vereda de la IZQUIERDA crece hacia −Z (con ese giro su +X
                    // local apunta ahí), así que su celda se ancla desde Z1.
                    float z = row.Side > 0f ? row.Z0 + column * Cell : row.Z1 - column * Cell;
                    var target = new Vector3(x, baseY, z);

                    // Pivote: la pieza se corre por su esquina mínima para que su celda arranque en el
                    // ancla (mismo criterio para la pared y para su relleno, que vive en la misma celda).
                    var origin = target - RotateY(module.Pivot, yaw);
                    var instance = new SceneInstance(
                        new Vector4(origin, 1f),
                        new Vector4(yaw, 0f, SceneDefinition.KindTextured, 0f));

                    foreach (var part in module.Parts)
                    {
                        if (!placements.TryGetValue(part, out var list))
                        {
                            list = new List<SceneInstance>();
                            placements[part] = list;
                        }
                        list.Add(instance);
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
                Material = pair.Key.Material
            });
        }

        return meshes;
    }

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
                IsDoor = piece.Name.Contains("door", StringComparison.Ordinal)
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