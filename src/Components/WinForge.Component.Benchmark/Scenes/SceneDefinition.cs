using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>Vértice de la malla: posición y normal (normales por cara, look facetado).</summary>
public readonly record struct SceneVertex(Vector3 Position, Vector3 Normal);

/// <summary>
/// Instancia: <see cref="PositionScale"/> lleva posición (xyz) y escala (w). En el corredor
/// infinito x e y son FIJOS (x = banda del corredor, y = altura sobre el terreno) y z es la
/// posición a lo largo del recorrido, que el vertex shader ENVUELVE alrededor de la cámara:
/// por eso el mundo no se termina nunca. <see cref="Orientation"/> lleva el giro inicial (x)
/// y la velocidad de giro (y). Los dos vectores de 16 bytes alinean con el input layout.
/// </summary>
public readonly record struct SceneInstance(Vector4 PositionScale, Vector4 Orientation);

/// <summary>
/// Una escena descrita como DATOS: malla, instancias y recorrido de cámara. Deliberadamente
/// no sabe nada de Direct3D ni de Vulkan — así una API nueva no obliga a redefinir la escena,
/// y el mismo mundo se puede renderizar con cualquier backend y comparar de igual a igual.
///
/// Todo lo que cambia con el tiempo es función del TIEMPO (no del frame): dos corridas con la
/// misma duración recorren exactamente el mismo mundo. La cámara AVANZA por el corredor (como
/// en 3DMark): no es una órbita sobre un campo estático.
/// </summary>
public sealed class SceneDefinition
{
    public required string Id { get; init; }

    /// <summary>Nombre visible, texto fuente en español (se traduce con el motor de la app).</summary>
    public required string Name { get; init; }

    /// <summary>Qué mide la escena, texto fuente en español.</summary>
    public required string Description { get; init; }

    public required SceneVertex[] Vertices { get; init; }
    public required SceneInstance[] Instances { get; init; }

    /// <summary>Presupuesto de frame con el que se cuenta "frames fuera de presupuesto".</summary>
    public double FrameBudgetMs { get; init; } = 1000.0 / 60.0;

    /// <summary>Segundos que se corren antes de empezar a medir (compilado de shaders, caches de driver).</summary>
    public double WarmupSeconds { get; init; } = 2.0;

    /// <summary>Duración medida por defecto de la corrida, en segundos.</summary>
    public int DefaultDurationSeconds { get; init; } = 30;

    /// <summary>
    /// Largo del corredor (unidades de mundo). El vertex shader envuelve las instancias en una
    /// ventana de este largo alrededor de la cámara: el terreno se repite pero el CONTENIDO que
    /// se ve siempre es nuevo. Debe coincidir con <c>CorridorLength</c> en <c>SceneShaders</c>.
    /// </summary>
    public const float CorridorLength = 420f;

    /// <summary>Velocidad de avance de la cámara, en unidades por segundo.</summary>
    public const float FlightSpeed = 22f;

    /// <summary>
    /// Terreno del corredor. VIVE DUPLICADO a propósito: acá para la cámara y en
    /// <c>SceneShaders.Common</c> para envolver las instancias. Si se cambia uno, cambiar el otro —
    /// si no, las rocas quedan flotando o enterradas.
    /// </summary>
    public static float TerrainHeight(float x, float z) =>
        5.5f * MathF.Sin(x * 0.06f) + 4.0f * MathF.Cos(z * 0.05f) + 2.0f * MathF.Sin((x + z) * 0.021f);

    /// <summary>
    /// Cámara en el instante <paramref name="seconds"/>: vuelo hacia adelante a
    /// <see cref="FlightSpeed"/> con una curva suave en X, siempre a una altura fija sobre el
    /// terreno, mirando un punto del recorrido que está adelante. Determinista: la misma corrida
    /// de la misma duración recorre exactamente el mismo camino.
    /// </summary>
    public (Vector3 Eye, Vector3 Target) CameraAt(double seconds)
    {
        const double lookaheadSeconds = 2.4;   // ~53 unidades por delante: la curva se ve venir

        return (PathPoint(seconds), PathPoint(seconds + lookaheadSeconds));
    }

    private static Vector3 PathPoint(double seconds)
    {
        float z = (float)(seconds * FlightSpeed);
        float x = 26f * MathF.Sin((float)seconds * 0.11f);
        float y = TerrainHeight(x, z) + 9f;
        return new Vector3(x, y, z);
    }

    public long VertexCount => Vertices.LongLength;
    public long InstanceCount => Instances.LongLength;
}

/// <summary>Catálogo de escenas del componente. La primera es la que trae esta entrega.</summary>
public static class SceneCatalog
{
    public static IReadOnlyList<SceneDefinition> All { get; } = new[] { BuildCorridorScene() };

    public static SceneDefinition Default => All[0];

    public static SceneDefinition? Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Escena 1 — Corredor infinito: vuelo hacia adelante sobre un terreno procedural, con
    /// ~16.000 rocas facetadas de 1.280 caras envueltas alrededor de la cámara (el vertex shader
    /// las recicla al wrapped del corredor, así el mundo no se acaba nunca). Iluminación,
    /// especular, niebla por distancia y ruido procedural por píxel.
    ///
    /// El peso NO es decorativo: se busca que una placa de gama media quede en unos cientos de
    /// FPS y que una integrada todavía dé un número útil.
    /// </summary>
    private static SceneDefinition BuildCorridorScene()
    {
        const int seed = 20260924;   // fija: la misma escena, siempre
        // Tres subdivisiones (1.280 caras, 3.840 vértices por roca). El número sale de la
        // medición: con geometría más liviana el cuello era la ENTREGA del frame y no la placa,
        // es decir se medía otra cosa. Con 3.840 el trabajo de vértices domina.
        var vertices = BuildFacetedIcosphere(subdivisions: 3);

        // Densidad a lo largo del corredor: 40 columnas (banda de ±140 en X) × 60 filas que
        // cubren el largo del corredor, 7 rocas por celda ≈ 16.800 instancias. Se superponen a
        // propósito para que la escena cargue el RELLENO además de los vértices.
        const int columns = 40;
        const int rows = 60;
        const int perCell = 7;
        var instances = new List<SceneInstance>(columns * rows * perCell);
        var rng = new Random(seed);

        float halfSpan = SceneDefinition.CorridorLength * 0.5f;
        for (int cx = 0; cx < columns; cx++)
        {
            for (int cz = 0; cz < rows; cz++)
            {
                for (int i = 0; i < perCell; i++)
                {
                    // x: banda del corredor. z: posición a lo largo del recorrido (el shader la
                    // envuelve alrededor de la cámara, así que acá es una posición cualquiera
                    // dentro del largo del corredor).
                    float x = -140f + (cx + 0.5f) * (280f / columns) + Jitter(rng, 3.2f);
                    float z = -halfSpan + (cz + 0.5f) * (SceneDefinition.CorridorLength / rows) + Jitter(rng, 3.2f);

                    // y = altura sobre el terreno (el terreno lo agrega el shader al envolver):
                    // casi todas cerca del piso, algunas flotando un poco más arriba.
                    float heightOffset = 2.2f + Jitter(rng, 1.8f);

                    float scale = 0.45f + (float)rng.NextDouble() * 0.70f;
                    float yaw = (float)(rng.NextDouble() * Math.PI * 2.0);
                    float spin = (float)(rng.NextDouble() * 0.6 - 0.3);

                    instances.Add(new SceneInstance(
                        new Vector4(x, heightOffset, z, scale),
                        new Vector4(yaw, spin, 0f, 0f)));
                }
            }
        }

        return new SceneDefinition
        {
            Id = "corredor",
            Name = "Corredor infinito",
            Description = "Vuelo hacia adelante sobre un terreno procedural con ~16.000 rocas facetadas de 1.280 caras: mide el trabajo de vértices y de relleno de la placa mientras el escenario avanza.",
            Vertices = vertices,
            Instances = instances.ToArray()
        };
    }

    private static float Jitter(Random rng, float amplitude) =>
        (float)(rng.NextDouble() * 2.0 - 1.0) * amplitude;

    /// <summary>
    /// Icosaedro subdividido con normales PLANAS por cara (look low-poly). Se devuelve sin
    /// índices a propósito: cada cara tiene sus tres vértices propios, así el color y la normal
    /// son de la cara y no hace falta buffer de índices.
    /// </summary>
    private static SceneVertex[] BuildFacetedIcosphere(int subdivisions)
    {
        float t = (1f + MathF.Sqrt(5f)) / 2f;
        var p = new[]
        {
            new Vector3(-1f,  t,  0f), new Vector3( 1f,  t,  0f),
            new Vector3(-1f, -t,  0f), new Vector3( 1f, -t,  0f),
            new Vector3( 0f, -1f,  t), new Vector3( 0f,  1f,  t),
            new Vector3( 0f, -1f, -t), new Vector3( 0f,  1f, -t),
            new Vector3( t,  0f, -1f), new Vector3( t,  0f,  1f),
            new Vector3(-t,  0f, -1f), new Vector3(-t,  0f,  1f)
        };
        for (int i = 0; i < p.Length; i++) p[i] = Vector3.Normalize(p[i]);

        var faces = new (int A, int B, int C)[]
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
            (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
            (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)
        };

        var triangles = faces.Select(f => (A: p[f.A], B: p[f.B], C: p[f.C])).ToList();
        for (int s = 0; s < subdivisions; s++)
        {
            var next = new List<(Vector3 A, Vector3 B, Vector3 C)>(triangles.Count * 4);
            foreach (var (a, b, c) in triangles)
            {
                var ab = Vector3.Normalize((a + b) * 0.5f);
                var bc = Vector3.Normalize((b + c) * 0.5f);
                var ca = Vector3.Normalize((c + a) * 0.5f);
                next.Add((a, ab, ca));
                next.Add((ab, b, bc));
                next.Add((ca, bc, c));
                next.Add((ab, bc, ca));
            }
            triangles = next;
        }

        var vertices = new List<SceneVertex>(triangles.Count * 3);
        foreach (var (a, b, c) in triangles)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            // El signo de la normal se decide por el centro de la cara (que cae del lado de
            // afuera de la esfera): así el sombreado no depende del orden de los vértices.
            var centroid = (a + b + c) / 3f;
            if (Vector3.Dot(normal, centroid) < 0) normal = -normal;
            vertices.Add(new SceneVertex(a, normal));
            vertices.Add(new SceneVertex(b, normal));
            vertices.Add(new SceneVertex(c, normal));
        }
        return vertices.ToArray();
    }
}
