using System.Numerics;
using System.Text;
using WinForge.Component.Benchmark.Scenes.Styles;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// Dónde viven los assets de una escena.
///
/// La DLL del componente NO lleva modelos ni texturas (ver README cinematográfico §7): cada escena
/// declara qué set necesita y el set viaja aparte. Se busca en este orden:
///
/// 1. <c>WHPO_BENCHMARK_ASSETS</c>: la carpeta de desarrollo (así se itera sin instalar nada).
/// 2. <c>%LOCALAPPDATA%\WHPO\BenchmarkAssets\&lt;set&gt;</c>: la carpeta de la app, donde el pack
///    queda cacheado al descargarlo (mismo patrón que los packs de idioma y los componentes).
///
/// Si no está en ninguno, la escena NO se puede armar y lo dice con un mensaje que explica cómo
/// conseguirlo (ver <see cref="NeonScene.Build"/>): fallar con "no encontré el modelo" a secas es
/// justo lo que hace perder una hora.
/// </summary>
internal static class SceneAssets
{
    internal const string OverrideVariable = "WHPO_BENCHMARK_ASSETS";

    internal static string Root(string set)
    {
        var candidates = new List<string>(3);

        string? overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            candidates.Add(Path.Combine(overridePath, set));
            candidates.Add(overridePath);
        }

        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WHPO", "BenchmarkAssets", set);
        candidates.Add(local);

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate)) return candidate;
        }

        // OJO: la excepción lleva el camino de recuperación adentro. Fallar con "no encontré el
        // modelo" a secas es lo que hace perder una hora buscando dónde iba.
        throw new DirectoryNotFoundException(
            $"No están los assets del set '{set}'. Se buscó en: {string.Join(" · ", candidates)}. " +
            $"Para bajarlos: ./tools/benchmark-assets/fetch-assets.ps1 -Set {set} " +
            $"(y para la app, copiar la carpeta a {local}).");
    }
}

/// <summary>
/// Escena 3 — Calle nocturna con lluvia ("Neón"), el piloto de la Fase 0 del benchmark
/// cinematográfico: un diorama de cine a nivel de calle, con el auto héroe, faroles, carteles de
/// neón y fachadas de ladrillo, recorrido por un travelling sobre rieles.
///
/// Es la PRIMERA escena con assets REALES (modelos glTF de Poly Haven con sus texturas PBR): lo que
/// la distingue no es la geometría —el motor ya dibujaba mundos— sino que acá cada objeto trae su
/// material con albedo, normales y rugosidad, y la luz de la escena son las fuentes de la calle
/// (faroles y carteles) en vez de un sol compartido.
///
/// Perfil de carga (ver README §4): mide PÍXELES. Materiales con textura y mapa de normales, varias
/// luces puntuales por píxel, partículas de lluvia y el post fijo del estilo: es el trabajo de fill
/// rate y de costo de sombreado, opuesto al perfil de vértices del cañón.
///
/// NOTA de arquitectura (por qué la calle NO es una caja con una textura encima): un edificio de
/// mentira —un prisma con una foto de ladrillo pegada— se delata al primer plano que se acerca,
/// porque no tiene canto, ni sombra propia, ni profundidad. Acá el frente se arma POR FRANJAS
/// alrededor de cada hueco (ver <see cref="AddPerforatedFront"/>): el vano tiene jambas y dintel de
/// ladrillo, la ventana entra 30 cm en la pared, y el marco, el vidrio y el alféizar van adentro. Es
/// más geometría, pero es la diferencia entre una calle y un decorado pintado.
/// </summary>
public static class NeonScene
{
    // ---- Medidas del diorama (metros: los modelos vienen en escala real) ----
    private const float RoadHalfWidth = 5.0f;      // calzada
    private const float KerbHeight = 0.16f;
    private const float SidewalkWidth = 3.4f;
    private const float StreetStart = -18f;
    // 205 y no 170: con la calle corta, el fondo de la fila de edificios caía a 106 m de la cámara del
    // tramo 3 —donde la bruma todavía deja leer la silueta— y se veía el corte seco de la última
    // medianera contra el cielo. Con 35 m más de calle el final queda a 140 m, ya adentro de la bruma.
    private const float StreetLength = 205f;
    private const float FacadeOffset = RoadHalfWidth + SidewalkWidth;   // 8.4: donde arranca el frente
    private const float HeroCarZ = 11.5f;

    // ---- Proporciones de las fachadas ----
    private const float BuildingDepth = 14f;        // fondo de la manzana (hacia adentro de la cuadra)
    private const float PlinthHeight = 0.55f;       // zócalo de piedra
    private const float PlinthProud = 0.11f;
    private const float CorniceHeight = 0.40f;      // cornisa
    private const float CorniceProud = 0.24f;
    private const float ParapetHeight = 0.75f;      // antepecho de azotea

    // ---- Repeticiones de textura (vueltas por metro) ----
    private const float BrickRepeat = 1f / 1.9f;     // el ladrillo vuelve cada 1,90 m
    private const float AsphaltRepeat = 1f / 4f;
    private const float ConcreteRepeat = 1f / 2.2f;
    private const float FittingRepeat = 1f / 1.2f;   // herrajes y unidades chicas

    /// <summary>Lo que la escena aporta al informe cuando algo del set no está (se llena al armar).</summary>
    private static readonly List<string> BuildNotes = new();

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

    /// <summary>Arma la escena. La geometría y las texturas quedan PEREZOSAS (ver
    /// <see cref="LazySceneMeshes"/>): el selector de escenas no paga nada hasta que se corre.</summary>
    public static SceneDefinition Build()
    {
        var meshes = new LazySceneMeshes(BuildMeshes);

        return new SceneDefinition
        {
            Id = "neon",
            Name = "Calle nocturna con lluvia",
            Description =
                "Diorama de calle a nivel de cine: autos estacionados, faroles y carteles de neón sobre " +
                "asfalto mojado. Usa modelos y texturas reales (CC0) y mide el trabajo por píxel: " +
                "materiales con relieve, varias luces puntuales y lluvia.",
            Meshes = meshes,
            // El set que la escena necesita (README §7): el arranque de la corrida lo asegura en disco
            // antes de preparar la escena, así una instalación limpia no falla por assets.
            AssetSet = "neon",
            // ENTORNO IBL: el HDRI del set es la luz del lugar, no un color inventado (una calle de
            // noche: sodio cálido sobre asfalto mojado). La intensidad sale de MEDIR el HDRI —su
            // irradiancia difusa proyectada es ~5× el ambiente que tenía la escena, así que entra a
            // 0.20 para que la calle reciba luz de ciudad sin dejar de ser noche (ver
            // SceneEnvironment: se cambia acá y en ningún otro lado).
            Environment = SceneEnvironment.Lazy(
                () => Path.Combine(SceneAssets.Root("neon"), "hdri", "cobblestone_street_night_1k.hdr"),
                intensity: 0.10f,
                description: "HDRI 'cobblestone_street_night' (Poly Haven, CC0)"),
            // SOMBRAS: no hay nada que declarar. El volumen del shadow map se DEDUCE de la geometría
            // de la escena (ver SceneDefinition.ResolveShadowVolume), así que todos los objetos
            // proyectan solos y no hay que configurar esferas una por una como con las sombras
            // analíticas. La fuerza va en 1: la calle es de noche y la luna es la única luz que
            // proyecta, así que la sombra de los autos y de los faroles se tiene que leer entera.
            // Encenderla acá la enciende en las CUATRO APIs a la vez (el valor es dato de la escena,
            // no del backend).
            ShadowStrength = 1f,
            CameraPath = CameraAt,
            Style = NeonStyle.Default,
            // Luna alta: con una luna baja y rasante las fachadas de 20 m tapaban la calle entera y el
            // diorama quedaba a oscuras. A 62° de altura la sombra de un edificio cae sobre su propia
            // vereda y el centro de la calzada sigue recibiendo la luz del cielo, que es lo que hace
            // visible el asfalto mojado.
            LightDirection = Vector3.Normalize(new Vector3(0.26f, -0.88f, -0.42f)),
            FarPlane = 260f,
            DefaultDurationSeconds = 60,
            WarmupSeconds = 2.5,
            PointLights = NeonLights(),
            ShadowCasters = NeonShadowCasters()
        };
    }

    // =========================================================================================
    // Contenido
    // =========================================================================================

    private static IReadOnlyList<SceneMesh> BuildMeshes()
    {
        string root = SceneAssets.Root("neon");
        var materials = BuildMaterials(root);
        var street = new StreetBuilder();
        var rng = new Random(20260927);

        BuildRoadSurface(street);
        BuildGroundDetail(street, rng);
        var rows = LayoutBuildings(rng);
        foreach (var row in rows) AddMassing(street, row);
        BuildSigns(street);
        BuildBackdrop(street);

        var meshes = new List<SceneMesh>(64)
        {
            new() { Name = "calzada", Vertices = street.Road.ToArray(), Instances = One(), Material = materials.Asphalt }
        };

        // ---- FACHADAS: los MÓDULOS REALES del kit glTF del set (ver NeonFacade) ----
        // Esto es lo que reemplaza a la pared generada a mano: el frente se TESELA con las paredes de
        // 3 × 3 m del kit y sus ventanas/puertas, con su albedo, su mapa de normales y su ARM.
        //
        // Si el kit NO está (una instalación que todavía no bajó el pack de assets), el frente quedaría
        // ABIERTO —la masa no lleva esa cara, la pone el kit— y cada manzana se vería hueca por dentro:
        // por eso el camino de emergencia cierra el frente con un plano de ladrillo.
        var facades = NeonFacade.Build(root, rows, FacadeOffset, rng, Note);
        if (facades.Count == 0)
        {
            AddFlatFronts(street, rows);
            Add(meshes, "frentes de emergencia", street.FlatFronts, materials.Brick1);
        }
        foreach (var mesh in facades) meshes.Add(mesh);

        // ---- Superficie ----
        Add(meshes, "veredas", street.Walk, materials.Cement);
        Add(meshes, "charcos", street.Puddles, materials.Wet);
        Add(meshes, "pintura de calle", street.Paint, materials.Paint);
        Add(meshes, "herrajes", street.Metal, materials.Metal);

        // Los MUROS DE MANZANA (medianiles, fondo y azotea) van en UNA malla oscura: son la masa que
        // sostiene la fachada, no la superficie que se mira —el frente lo ponen los módulos glTF de
        // NeonFacade— y la tinta oscura es lo que hace que el hueco de un vano se lea hundido.
        Add(meshes, "muros de manzana", street.Walls[3], materials.Recess);

        // ---- Carteles: una malla local y N colocaciones (un draw call por forma). No hay modelos de
        // cartel de neón en el catálogo de assets, así que siguen siendo geometría propia: son quads
        // emisivos con su cuerpo de chapa y su ménsula, no "bloques" — ver SignLayout/BuildSigns. ----
        Add(meshes, "paneles de cartel", SignPanelVertices(horizontal: true), street.Signs, materials.Sign);
        Add(meshes, "paneles de cartel verticales", SignPanelVertices(horizontal: false), street.SignsVertical, materials.Sign);
        Add(meshes, "cuerpos de cartel", SignBodyVertices(horizontal: true), street.SignBodies, materials.Metal);
        Add(meshes, "cuerpos de cartel verticales", SignBodyVertices(horizontal: false), street.SignBodiesVertical, materials.Metal);

        AddModel(meshes, root, "covered_car", "auto", CarPlacements());
        AddModel(meshes, root, "street_lamp_01", "farol", PostLampPlacements());
        AddModel(meshes, root, "street_lamp_02", "farol de pared", WallLampPlacements());
        AddModel(meshes, root, "cardboard_box_01", "cajas", BoxPlacements());

        // ---- LLUVIA: la ÚLTIMA y con MEZCLA, después de todo lo opaco ----
        meshes.Add(new SceneMesh
        {
            Name = "lluvia",
            Vertices = RainVertices(),
            Instances = RainPlacements().ToArray(),
            Blend = true
        });

        return meshes;
    }

    // ---- Lluvia ----

    /// <summary>Cuántas estelas caen dentro de la caja que acompaña al ojo.</summary>
    private const int RainDropCount = 2600;

    /// <summary>Radio (m) de la caja de lluvia alrededor del ojo. Más ancho = se ve caer más lejos.</summary>
    private const float RainBoxRadius = 19f;

    /// <summary>
    /// El quad de una estela: ancho unitario (±1) y largo 0..1 en Y. El vertex shader los lee como
    /// (ancho, largo) y arma la estela con ellos, así que UNA malla local sirve para las 2.600 gotas.
    /// </summary>
    private static SceneVertex[] RainVertices()
    {
        var vertices = new List<SceneVertex>(6);
        AddQuad(vertices,
            new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f),
            new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f), Vector3.UnitZ);
        return vertices.ToArray();
    }

    /// <summary>
    /// Las gotas: desplazamiento horizontal FIJO respecto del ojo (la caja viaja con la cámara), fase
    /// del ciclo de caída, variación de velocidad y largo de la estela. Todo sale de una semilla fija:
    /// dos corridas de la misma duración ven exactamente la misma lluvia.
    /// </summary>
    private static IReadOnlyList<SceneInstance> RainPlacements()
    {
        var rng = new Random(20261002);
        var drops = new List<SceneInstance>(RainDropCount);
        for (int i = 0; i < RainDropCount; i++)
        {
            float dx = (float)(rng.NextDouble() * 2.0 - 1.0) * RainBoxRadius;
            float dz = (float)(rng.NextDouble() * 2.0 - 1.0) * RainBoxRadius;
            float phase = (float)rng.NextDouble();
            float speed = (float)rng.NextDouble();
            float length = 0.55f + (float)rng.NextDouble() * 0.80f;

            drops.Add(new SceneInstance(
                new Vector4(dx, 0f, dz, length),
                new Vector4(0f, phase, SceneDefinition.KindRain, speed)));
        }
        return drops;
    }

    /// <summary>Agrega una malla solo si tiene geometría: una lista vacía es una malla que no dibuja
    /// nada y que igual ocupa un draw call.</summary>
    private static void Add(List<SceneMesh> meshes, string name, List<SceneVertex> vertices, SceneMaterial material)
    {
        if (vertices.Count > 0) meshes.Add(new SceneMesh
        {
            Name = name,
            Vertices = vertices.ToArray(),
            Instances = One(),
            Material = material
        });
    }

    /// <summary>Igual que <see cref="Add(List{SceneMesh}, string, List{SceneVertex}, SceneMaterial)"/>
    /// pero para las unidades instanciadas: la malla local, una vez, con N colocaciones.</summary>
    private static void Add(List<SceneMesh> meshes, string name, SceneVertex[] vertices, List<SceneInstance> instances, SceneMaterial material)
    {
        if (instances.Count > 0) meshes.Add(new SceneMesh
        {
            Name = name,
            Vertices = vertices,
            Instances = instances.ToArray(),
            Material = material
        });
    }

    private static SceneInstance[] One() => new[] { Identity() };

    // =========================================================================================
    // Materiales
    // =========================================================================================

    /// <summary>
    /// Todos los materiales de la calle. Es una clase y no una lista suelta por una razón concreta:
    /// el atlas de materiales ranura por JUEGO DE TEXTURAS, así que los cuatro mapas de abajo se
    /// comparten entre todos los materiales (el ladrillo oscuro de los cantos de vano es el MISMO
    /// ladrillo, solo con otra tinta) y la escena entera entra en cuatro ranuras — que es lo que
    /// mantiene el atlas en 512² en vez de bajarlo a 384².
    /// </summary>
    private sealed record MaterialSet(
        SceneMaterial Asphalt,
        SceneMaterial Wet,
        SceneMaterial Cement,
        SceneMaterial Metal,
        SceneMaterial Brick1,
        SceneMaterial Brick2,
        SceneMaterial Brick3,
        SceneMaterial[] Brick,
        SceneMaterial Recess,
        SceneMaterial Glass,
        SceneMaterial Wood,
        SceneMaterial Fabric,
        SceneMaterial Paint,
        SceneMaterial Sign);

    private static MaterialSet BuildMaterials(string root)
    {
        // Cada archivo se decodifica UNA vez (SceneTexture.FromFile no cachea: pedirlo dos veces son
        // otros 4 MB de CPU y otro JPEG descomprimido por el camino).
        var asphaltAlbedo = Texture(root, "asphalt_01", "diff");
        var asphaltNormal = Texture(root, "asphalt_01", "nor_gl");
        var asphaltArm = Texture(root, "asphalt_01", "arm");
        var concreteAlbedo = Texture(root, "concrete_floor_02", "diff");
        var concreteNormal = Texture(root, "concrete_floor_02", "nor_gl");
        var concreteArm = Texture(root, "concrete_floor_02", "arm");
        var brickAlbedo = Texture(root, "brick_wall_001", "diff");
        var brickNormal = Texture(root, "brick_wall_001", "nor_gl");
        var brickArm = Texture(root, "brick_wall_001", "arm");

        var asphalt = new SceneMaterial
        {
            Name = "asfalto mojado",
            Albedo = asphaltAlbedo,
            Normal = asphaltNormal,
            Arm = asphaltArm,
            // OSCURO de verdad: es la mitad del look de una calle nocturna —el asfalto mojado es casi
            // negro y lo que se ve encima es el reflejo de los faroles y los carteles—, y además es lo
            // que separa la calzada de la vereda. Con la tinta clara que tenía, calle y vereda eran el
            // mismo gris y la calle se leía como una explanada de hormigón.
            Tint = new Vector3(0.30f, 0.305f, 0.33f),
            // Húmedo pero no un espejo: la rugosidad del mapa se baja para que el asfalto devuelva el
            // brillo de los carteles sin convertirse en una pileta de mercurio.
            RoughnessScale = 0.40f,
            MetallicScale = 1.0f,
            NormalStrength = 0.8f
        };

        // Charcos: la misma textura de asfalto, casi negra y pulida. Comparte ranura con la calzada.
        var wet = new SceneMaterial
        {
            Name = "charco",
            Albedo = asphaltAlbedo,
            Normal = asphaltNormal,
            Arm = asphaltArm,
            Tint = new Vector3(0.045f, 0.05f, 0.065f),
            RoughnessScale = 0.04f,
            NormalStrength = 0.25f
        };

        var cement = new SceneMaterial
        {
            Name = "vereda y piedra",
            Albedo = concreteAlbedo,
            Normal = concreteNormal,
            Arm = concreteArm,
            Tint = new Vector3(0.52f, 0.52f, 0.545f),
            RoughnessScale = 0.78f
        };

        // Herrajes: marcos, barandas, bajantes y bolardos. Va con la textura del hormigón en tinta
        // oscura por una razón práctica: el camino perforado por cara del shader no tiene mano, así
        // que un material de color plano deja los marcos como alambre. La textura le da el grano que
        // hace que una baranda se vea de metal pintado y no de plástico.
        var metal = new SceneMaterial
        {
            Name = "herrajes",
            Albedo = concreteAlbedo,
            Normal = concreteNormal,
            Arm = concreteArm,
            Tint = new Vector3(0.26f, 0.27f, 0.30f),
            RoughnessScale = 0.55f
        };

        SceneMaterial Brick(string name, Vector3 tint, float roughness = 0.95f) => new()
        {
            Name = name,
            Albedo = brickAlbedo,
            Normal = brickNormal,
            Arm = brickArm,
            Tint = tint,
            RoughnessScale = roughness,
            NormalStrength = 1.0f
        };

        var brick1 = Brick("ladrillo", new Vector3(0.47f, 0.42f, 0.40f));
        var brick2 = Brick("ladrillo", new Vector3(0.38f, 0.36f, 0.39f));
        var brick3 = Brick("ladrillo", new Vector3(0.52f, 0.43f, 0.35f));

        // Cantos de los vanos: el mismo ladrillo, mucho más oscuro. Es el truco más barato de todos
        // y el que más se ve: la jamba y el dintel en sombra hacen que la ventana sea un AGUJERO.
        var recess = Brick("canto de vano", new Vector3(0.14f, 0.125f, 0.14f), roughness: 1f);

        // Vidrio sin mapa: es un dieléctrico liso. La tinta la pone cada instancia (apagado = el cielo
        // de noche devuelto; encendido = una lámpara cálida detrás del marco).
        var glass = new SceneMaterial
        {
            Name = "vidrio",
            Tint = Vector3.One,
            RoughnessScale = 0.10f
        };

        var wood = new SceneMaterial
        {
            Name = "puerta",
            Tint = new Vector3(0.30f, 0.21f, 0.15f),
            RoughnessScale = 0.72f
        };

        var fabric = new SceneMaterial
        {
            Name = "toldo",
            Tint = Vector3.One,
            RoughnessScale = 0.85f
        };

        var paint = new SceneMaterial
        {
            Name = "pintura de calle",
            Tint = new Vector3(0.72f, 0.71f, 0.66f),
            RoughnessScale = 0.55f
        };

        var sign = new SceneMaterial
        {
            Name = "neón",
            Tint = Vector3.One,
            RoughnessScale = 0.35f,
            Emissive = new Vector3(1f, 1f, 1f),
            EmissiveStrength = SignGlow
        };

        return new MaterialSet(
            asphalt, wet, cement, metal,
            brick1, brick2, brick3, new[] { brick1, brick2, brick3 },
            recess, glass, wood, fabric, paint, sign);
    }

    // =========================================================================================
    // Calle: calzada, veredas y el detalle que la hace una calle y no un plano
    // =========================================================================================

    /// <summary>
    /// Acumuladores del armado. La calle se arma entera en una pasada y después se convierte en
    /// mallas: pasar catorce listas por parámetro es cómo se termina con una escena que nadie puede
    /// leer.
    /// </summary>
    private sealed class StreetBuilder
    {
        internal readonly List<SceneVertex> Road = new();
        internal readonly List<SceneVertex> Walk = new();
        internal readonly List<SceneVertex> Metal = new();
        internal readonly List<SceneVertex> Paint = new();
        internal readonly List<SceneVertex> Puddles = new();

        /// <summary>Tres tintas de ladrillo (0-2) y los cantos de los vanos (3).</summary>
        internal readonly List<SceneVertex>[] Walls = { new(), new(), new(), new() };

        internal readonly List<SceneInstance> Signs = new();
        internal readonly List<SceneInstance> SignsVertical = new();
        // El CUERPO del cartel (el marco oscuro que lo sostiene): el panel de neón va montado adentro.
        internal readonly List<SceneInstance> SignBodies = new();
        internal readonly List<SceneInstance> SignBodiesVertical = new();

        /// <summary>
        /// Camino de emergencia cuando el kit de fachadas no está: los frentes PLANOS de ladrillo que
        /// cierran las manzanas. Acá y no en <c>Walls</c> por el material: lleva el ladrillo CLARO
        /// (se ve de frente, es la superficie que se mira), mientras que <c>Walls[3]</c> es la masa
        /// oscura de medianiles, fondos y azoteas que va detrás.
        /// </summary>
        internal readonly List<SceneVertex> FlatFronts = new();
    }

    private static void BuildRoadSurface(StreetBuilder street)
    {
        // Calzada: un solo quad con la UV ya escalada (4 m por baldosa).
        AddQuad(street.Road,
            new Vector3(-RoadHalfWidth, 0f, StreetStart), new Vector3(RoadHalfWidth, 0f, StreetStart),
            new Vector3(RoadHalfWidth, 0f, StreetStart + StreetLength), new Vector3(-RoadHalfWidth, 0f, StreetStart + StreetLength),
            Vector3.UnitY, AsphaltRepeat);

        foreach (float side in new[] { -1f, 1f })
        {
            float inner = side * RoadHalfWidth;
            float outer = side * (RoadHalfWidth + SidewalkWidth);

            // Vereda.
            AddQuad(street.Walk, new Vector3(inner, KerbHeight, StreetStart), new Vector3(outer, KerbHeight, StreetStart),
                new Vector3(outer, KerbHeight, StreetStart + StreetLength), new Vector3(inner, KerbHeight, StreetStart + StreetLength),
                Vector3.UnitY, ConcreteRepeat);

            // Frente del cordón (la cara vertical que se ve desde la calle) y su remate superior.
            AddQuad(street.Walk, new Vector3(inner, 0f, StreetStart), new Vector3(inner, 0f, StreetStart + StreetLength),
                new Vector3(inner, KerbHeight, StreetStart + StreetLength), new Vector3(inner, KerbHeight, StreetStart),
                new Vector3(-side, 0f, 0f), ConcreteRepeat);

            // Línea de borde pintada sobre el asfalto: sin ella la calzada no tiene ancho, se lee como
            // una explanada.
            AddQuad(street.Paint,
                new Vector3(inner + side * 0.18f, 0.010f, StreetStart + 1f), new Vector3(inner + side * 0.30f, 0.010f, StreetStart + 1f),
                new Vector3(inner + side * 0.30f, 0.010f, StreetStart + StreetLength - 1f), new Vector3(inner + side * 0.18f, 0.010f, StreetStart + StreetLength - 1f),
                Vector3.UnitY);
        }

        // Eje central: línea discontinua de verdad (los tramos pintados y los huecos de la calle).
        for (float z = StreetStart + 6f; z < StreetStart + StreetLength - 6f; z += 9f)
        {
            AddQuad(street.Paint, new Vector3(-0.07f, 0.010f, z), new Vector3(0.07f, 0.010f, z),
                new Vector3(0.07f, 0.010f, z + 3.4f), new Vector3(-0.07f, 0.010f, z + 3.4f), Vector3.UnitY);
        }

        // Dos sendas peatonales (franjas anchas): dan escala humana a la calzada.
        foreach (float center in new[] { HeroCarZ + 18f, HeroCarZ + 52f })
        {
            for (int stripe = 0; stripe < 7; stripe++)
            {
                float x = -3.6f + stripe * 1.2f;
                AddQuad(street.Paint, new Vector3(x, 0.012f, center - 1.9f), new Vector3(x + 0.62f, 0.012f, center - 1.9f),
                    new Vector3(x + 0.62f, 0.012f, center + 1.9f), new Vector3(x, 0.012f, center + 1.9f), Vector3.UnitY);
            }
        }
    }

    /// <summary>
    /// El detalle que hace que la calle sea una calle: bocas de tormenta contra el cordón, tapas de
    /// inspección, bolardos, y los charcos que devuelven los carteles. Todo geometría propia (una
    /// malla por material) y ningún asset nuevo.
    /// </summary>
    private static void BuildGroundDetail(StreetBuilder street, Random rng)
    {
        for (float z = StreetStart + 4f; z < StreetStart + StreetLength - 4f; z += 27f)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                // Boca de tormenta: una reja hundida contra el cordón (y el hueco que se ve debajo).
                float x = side * (RoadHalfWidth - 0.42f);
                AddSlab(street.Metal,
                    new Vector3(x - 0.34f, 0.005f, z - 0.60f), new Vector3(x + 0.34f, 0.030f, z + 0.60f), FittingRepeat);
                for (int bar = 0; bar < 5; bar++)
                {
                    float bz = z - 0.44f + bar * 0.22f;
                    AddSlab(street.Metal,
                        new Vector3(x - 0.30f, 0.030f, bz - 0.05f), new Vector3(x + 0.30f, 0.055f, bz + 0.05f), FittingRepeat);
                }
            }
        }

        // Tapas de inspección sobre la calzada.
        for (float z = StreetStart + 12f; z < StreetStart + StreetLength - 10f; z += 31f)
        {
            float x = (float)(rng.NextDouble() * 6.0 - 3.0);
            AddSlab(street.Metal,
                new Vector3(x - 0.42f, 0.004f, z - 0.42f), new Vector3(x + 0.42f, 0.026f, z + 0.42f), FittingRepeat);
        }

        // Bolardos contra el cordón: marcan la vereda y dan ritmo vertical en primer plano. Solo en el
        // tramo que recorre la cámara, que es donde se ven.
        for (float z = HeroCarZ - 16f; z < HeroCarZ + 52f; z += 4.4f)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                float x = side * (RoadHalfWidth + 0.55f);
                AddTube(street.Metal, new Vector3(x, KerbHeight, z), new Vector3(x, KerbHeight + 0.78f, z), 0.075f);
                AddTube(street.Metal, new Vector3(x, KerbHeight + 0.74f, z), new Vector3(x, KerbHeight + 0.90f, z), 0.055f);
            }
        }

        // Charcos: polígonos irregulares apenas levantados de la calzada. Es lo que convierte el
        // "asfalto brillante" en una calle que acaba de llover.
        for (int i = 0; i < 26; i++)
        {
            float cx = (float)(rng.NextDouble() * 8.4 - 4.2);
            float cz = StreetStart + 4f + (float)rng.NextDouble() * (StreetLength - 10f);
            float radius = 0.5f + (float)rng.NextDouble() * 1.7f;
            float squash = 0.35f + (float)rng.NextDouble() * 0.4f;
            int corners = 6 + rng.Next(4);
            var ring = new Vector3[corners];
            for (int c = 0; c < corners; c++)
            {
                float angle = MathF.Tau * c / corners;
                float wobble = 0.72f + (float)rng.NextDouble() * 0.5f;
                ring[c] = new Vector3(
                    cx + MathF.Cos(angle) * radius * wobble,
                    0.016f,
                    cz + MathF.Sin(angle) * radius * squash * wobble);
            }
            for (int c = 1; c < corners - 1; c++)
            {
                AddQuad(street.Puddles, ring[0], ring[c], ring[c + 1], ring[c + 1], Vector3.UnitY);
            }
        }
    }

    // =========================================================================================
    // Edificios: el reparto de la cuadra y la masa de cada manzana
    // =========================================================================================

    /// <summary>
    /// Reparte los edificios a lo largo de la cuadra. Son CONTIGUOS (medianiles compartidos, como una
    /// manzana de verdad) y el ancho va en MÚLTIPLOS de la celda del kit de fachada: así las paredes
    /// de dos vecinos se tocan y no queda una franja sin pared al final de cada edificio.
    /// </summary>
    private static List<NeonFacade.FacadeRow> LayoutBuildings(Random rng)
    {
        var rows = new List<NeonFacade.FacadeRow>(28);
        const float Cell = NeonFacade.Cell;
        float end = StreetStart + StreetLength - 2f;

        for (float z = StreetStart + 2f; z + Cell < end;)
        {
            int columns = 3 + rng.Next(3);                       // 3 a 5 celdas: 9 a 15 m de frente
            if (z + columns * Cell > end) columns = (int)((end - z) / Cell);
            if (columns < 1) break;
            float width = columns * Cell;

            int floors = 3 + rng.Next(4);                        // 3 a 6 pisos altos
            float depth = BuildingDepth + (float)rng.NextDouble() * 4f;

            foreach (float side in new[] { -1f, 1f })
            {
                // Un piso de diferencia entre las dos veredas: dos filas idénticas delatarían la
                // simetría desde el primer plano.
                int sideFloors = Math.Max(3, floors + (side > 0f ? 0 : rng.Next(-1, 2)));
                rows.Add(new NeonFacade.FacadeRow(side, z, z + width, sideFloors, depth));
            }

            z += width;
        }

        return rows;
    }

    /// <summary>
    /// La MASA del edificio: fondo, medianiles, azotea, zócalo y cornisa. Es lo que hay DETRÁS de la
    /// fachada —la que arma <see cref="NeonFacade"/> con los módulos glTF reales—, así que va en tinta
    /// oscura y sin detalle: se ve como silueta contra el cielo en el plano alto y como sombra en el
    /// hueco de un vano.
    ///
    /// Antes acá se generaba la FACHADA entera, perforando el frente vano por vano con marcos, vidrios,
    /// alféizares, toldos y balcones armados a mano. Eso se fue: el frente son modelos del kit glTF.
    /// </summary>
    private static void AddMassing(StreetBuilder street, NeonFacade.FacadeRow row)
    {
        float side = row.Side;
        float face = side * FacadeOffset;                 // plano del frente
        float back = face + side * row.Depth;             // fondo de la manzana
        var mass = street.Walls[3];                       // muros de manzana: UNA malla oscura

        // ---- Volumen: cinco caras, SIN la del frente (el frente es la fachada de módulos) ----
        float xa = MathF.Min(face, back), xb = MathF.Max(face, back);

        AddQuad(mass, new Vector3(back, 0f, row.Z0), new Vector3(back, 0f, row.Z1),
            new Vector3(back, row.Height, row.Z1), new Vector3(back, row.Height, row.Z0), new Vector3(side, 0f, 0f), BrickRepeat);
        AddQuad(mass, new Vector3(xa, 0f, row.Z0), new Vector3(xb, 0f, row.Z0),
            new Vector3(xb, row.Height, row.Z0), new Vector3(xa, row.Height, row.Z0), -Vector3.UnitZ, BrickRepeat);
        AddQuad(mass, new Vector3(xb, 0f, row.Z1), new Vector3(xa, 0f, row.Z1),
            new Vector3(xa, row.Height, row.Z1), new Vector3(xb, row.Height, row.Z1), Vector3.UnitZ, BrickRepeat);

        // Azotea: una losa oscura (alquitrán), que es lo que se ve desde el plano alto del tramo 3.
        AddSlab(street.Metal, new Vector3(xa, row.Height, row.Z0), new Vector3(xb, row.Height + 0.06f, row.Z1), FittingRepeat);

        // ---- Zócalo: un listón de piedra que separa la planta baja de la vereda ----
        float plinthFace = face - side * PlinthProud;
        AddSlab(street.Walk,
            new Vector3(MathF.Min(plinthFace, back), 0f, row.Z0 - 0.02f),
            new Vector3(MathF.Max(plinthFace, back), PlinthHeight, row.Z1 + 0.02f), ConcreteRepeat);

        // ---- Cornisa y antepecho de azotea: el remate de la línea del techo ----
        float corniceFace = face - side * CorniceProud;
        AddSlab(mass,
            new Vector3(MathF.Min(corniceFace, back), row.Height - CorniceHeight, row.Z0 - 0.02f),
            new Vector3(MathF.Max(corniceFace, back), row.Height, row.Z1 + 0.02f), BrickRepeat);
        AddSlab(mass,
            new Vector3(MathF.Min(corniceFace, back), row.Height, row.Z0 - 0.02f),
            new Vector3(MathF.Max(corniceFace, back), row.Height + ParapetHeight, row.Z1 + 0.02f), BrickRepeat);
    }

    /// <summary>
    /// Camino de emergencia cuando el kit de fachadas glTF no está en disco: cierra cada frente con un
    /// plano de ladrillo en el mismo plano y con el mismo alto que llevaría la fachada de módulos, para
    /// que la manzana no quede hueca por dentro. Es solo eso —un plano ciego— y por eso los carteles y
    /// los faroles de pared igual se colocan encima: la calle sigue iluminada aunque no haya vanos.
    /// </summary>
    private static void AddFlatFronts(StreetBuilder street, IReadOnlyList<NeonFacade.FacadeRow> rows)
    {
        foreach (var row in rows)
        {
            float face = row.Side * FacadeOffset;
            var normal = new Vector3(-row.Side, 0f, 0f);

            AddQuad(street.FlatFronts,
                new Vector3(face, 0f, row.Z0), new Vector3(face, 0f, row.Z1),
                new Vector3(face, row.Height, row.Z1), new Vector3(face, row.Height, row.Z0),
                normal, BrickRepeat);
        }
    }

    /// <summary>
    /// El FONDO que cierra la calle en sus dos extremos: sin él se ve el corte seco de la última
    /// medianera contra el cielo en el plano alto del tramo 3. Son dos manzanas transversales a lo
    /// ancho de la calle (calzada + veredas + frentes) —una antes del arranque y otra pasando el
    /// final— con el MISMO alto de azotea que el vecino más alto, así la silueta continúa en vez de
    /// cortarse. Van en <c>Walls[3]</c>: es masa oscura que se lee contra el cielo, no superficie.
    /// </summary>
    private static void BuildBackdrop(StreetBuilder street)
    {
        float minX = -(FacadeOffset + BuildingDepth) - 4f;
        float maxX = -minX;
        const float depth = 12f;

        // El vecino más alto posible son 6 pisos de la celda del kit: 18 m, más el antepecho de
        // azotea para que la silueta del fondo no quede por debajo de la de la cuadra.
        float maxHeight = NeonFacade.Cell * 6f + ParapetHeight;

        foreach (var end in new[] { StreetStart - depth, StreetStart + StreetLength })
        {
            AddSlab(street.Walls[3],
                new Vector3(minX, 0f, end),
                new Vector3(maxX, maxHeight, end + depth), BrickRepeat);
        }
    }

    // ---- Medidas del cartel (metros). El panel es la cara que enciende; el cuerpo es el marco ----
    private const float SignPanelHalfWidth = 0.60f;    // 1,20 m de ancho
    private const float SignPanelHalfHeight = 0.19f;   // 0,38 m de alto
    private const float SignBodyHalfWidth = 0.68f;
    private const float SignBodyHalfHeight = 0.26f;
    /// <summary>Cuánto sobresale el panel por delante de la cara del cuerpo: el cuerpo tiene 0,075 de
    /// medio fondo, así que el panel —de 0,02 de medio fondo— queda montado y con un reborde visible.</summary>
    private const float SignPanelProud = 0.095f;

    /// <summary>
    /// Panel luminoso del cartel: la cara que enciende. Va MONTADO ADENTRO del cuerpo
    /// (<see cref="SignBodyVertices"/>), un poco adelantado, así el cartel se lee como un aviso
    /// retroiluminado y no como un rectángulo de color pegado a la pared.
    /// </summary>
    /// <remarks>
    /// El tamaño es de CARTAEL de verdad —1,20 × 0,38 m—, y no los 3,9 × 1,16 m que tenía: a ese
    /// tamaño el cartel tapaba media fachada, se leía como un bloque de color plano y por eso la calle
    /// se veía de plástico. Los ejes locales son (ancho a lo largo de la calle, alto, salida de la
    /// pared) porque la colocación gira ±90°.
    /// </remarks>
    private static SceneVertex[] SignPanelVertices(bool horizontal)
    {
        var vertices = new List<SceneVertex>(36);
        AddBox(vertices, new Vector3(0f, 0f, SignPanelProud), horizontal
            ? new Vector3(SignPanelHalfWidth, SignPanelHalfHeight, 0.02f)
            : new Vector3(SignPanelHalfHeight, SignPanelHalfWidth, 0.02f));
        return vertices.ToArray();
    }

    /// <summary>
    /// Cuerpo del cartel: el marco oscuro que lo sostiene, con el panel por delante. Es lo que le da
    /// el borde y el espesor que un cartel necesita para leerse como objeto; sin él el panel flota
    /// sobre el ladrillo como una calcomanía.
    /// </summary>
    private static SceneVertex[] SignBodyVertices(bool horizontal)
    {
        var vertices = new List<SceneVertex>(36);
        AddBox(vertices, Vector3.Zero, horizontal
            ? new Vector3(SignBodyHalfWidth, SignBodyHalfHeight, 0.075f)
            : new Vector3(SignBodyHalfHeight, SignBodyHalfWidth, 0.075f));
        return vertices.ToArray();
    }

    /// <summary>
    /// Carga un modelo glTF del set y lo agrega como una malla POR PRIMITIVA, todas con las mismas
    /// instancias: el motor dibuja sin índice y con un material por malla, así que la carrocería y
    /// las ruedas del auto (que traen materiales distintos) son mallas distintas con la misma
    /// colocación. Un modelo que no está se saltea con una nota en vez de tumbar la escena.
    /// </summary>
    private static void AddModel(
        List<SceneMesh> meshes, string root, string slug, string label, IReadOnlyList<SceneInstance> instances)
    {
        if (instances.Count == 0) return;

        string path = Path.Combine(root, "models", slug, slug + "_1k.gltf");
        if (!File.Exists(path))
        {
            Note($"Falta el modelo '{slug}': la escena corre sin {label}.");
            return;
        }

        try
        {
            var model = GltfModel.Load(path);
            foreach (var part in model.Parts)
            {
                meshes.Add(new SceneMesh
                {
                    Name = $"{label} · {part.Name}",
                    Vertices = part.Vertices,
                    Instances = instances.ToArray(),
                    Material = part.Material
                });
            }
        }
        catch (Exception exception)
        {
            Note($"No se pudo cargar '{slug}' ({exception.GetType().Name}): la escena corre sin {label}.");
        }
    }

    // =========================================================================================
    // Colocación de los objetos de la calle
    // =========================================================================================

    /// <summary>
    /// Altura a la que se APOYA el auto: el origen del modelo <c>covered_car</c> está a la altura del
    /// EJE —sus cuatro ruedas cuelgan 0,30 m—, así que apoyarlo en la calzada es ponerlo en +0,30, no
    /// en la altura del cordón: con <see cref="KerbHeight"/> (0,16) las ruedas quedaban enterradas
    /// 14 cm en el asfalto.
    /// </summary>
    private const float CarRestHeight = 0.30f;

    private static IReadOnlyList<SceneInstance> CarPlacements() => new[]
    {
        // El auto héroe: estacionado contra la vereda derecha, apenas abierto hacia la calle. Está a
        // la DERECHA del recorrido del tramo 1 a propósito: la cámara pasa por el carril izquierdo y
        // el auto entra en cuadro de costado, sin que el travelling se le meta adentro (ver CameraAt).
        Placed(new Vector3(3.2f, CarRestHeight, HeroCarZ), 0.30f, 1f),
        Placed(new Vector3(-3.1f, CarRestHeight, HeroCarZ + 24f), -0.22f, 0.96f),
        Placed(new Vector3(3.3f, CarRestHeight, HeroCarZ + 46f), 0.16f, 1.02f),
        Placed(new Vector3(-3.2f, CarRestHeight, HeroCarZ + 71f), -0.30f, 0.94f)
    };

    // ---- Faroles: poste sobre la vereda y lámpara de pared sobre la fachada ----
    //
    // Son DOS modelos distintos y hasta ahora se colocaban como si fueran el mismo. street_lamp_01 es un
    // poste con su linterna arriba (caja local de 0 a 3,87 m, con su base). street_lamp_02 es un farol
    // DE PARED: su caja cuelga 0,39 m POR DEBAJO del anclaje y su brazo sale 0,81 m hacia su +Z. Plantado
    // a nivel de calle —como estaba, y encima en el mismo z que un poste— el brazo entraba en la vereda y
    // la atravesaba de lado a lado. Acá el poste va en la vereda y la lámpara va montada en el frente.

    /// <summary>Cuánto entra el poste en la vereda (que va de 5,0 a 8,4 m del eje).</summary>
    private const float PostLampInset = 1.15f;
    private const float PostLampSpacing = 17f;

    /// <summary>Altura a la que se ancla el brazo de la lámpara de pared, y cada cuánto se repite.</summary>
    private const float WallLampSpacing = 34f;
    private const float WallLampHeight = 3.30f;

    /// <summary>Anclaje apenas por delante del plano del frente: pegado a la pared la placa del brazo
    /// pelearía z con el ladrillo (las dos superficies en el mismo plano).</summary>
    private const float WallLampOffset = FacadeOffset - 0.04f;

    /// <summary>Altura de la linterna del POSTE en el mundo: el modelo la tiene a 3,32 m sobre su base,
    /// que va apoyada en la vereda.</summary>
    private const float PostLampLanternHeight = KerbHeight + 3.32f;

    /// <summary>
    /// Los postes de la calle. Van sobre la vereda cada 17 m y alternando la vereda (el de la derecha
    /// corre 6 m): dos hileras enfrentadas a la misma altura delatarían la simetría desde el primer plano.
    /// </summary>
    private static IReadOnlyList<SceneInstance> PostLampPlacements()
    {
        var instances = new List<SceneInstance>();
        for (float z = StreetStart + 12f; z < StreetStart + StreetLength - 12f; z += PostLampSpacing)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                float x = side * (RoadHalfWidth + PostLampInset);
                float yaw = side > 0 ? MathF.PI * 0.5f : -MathF.PI * 0.5f;
                instances.Add(Placed(new Vector3(x, KerbHeight, z + (side > 0 ? 6f : 0f)), yaw, 1f));
            }
        }
        return instances;
    }

    /// <summary>
    /// Faroles de PARED montados en el frente, entre los postes y por encima de la cabeza del que pasa.
    /// El giro deja el +Z del modelo (su brazo) apuntando al CENTRO de la calle, así que la linterna
    /// queda colgando sobre la vereda a 2,9 m del piso y no atravesada en el paso.
    /// </summary>
    private static IReadOnlyList<SceneInstance> WallLampPlacements()
    {
        var instances = new List<SceneInstance>();
        for (float z = StreetStart + 12f + 8.5f; z < StreetStart + StreetLength - 12f; z += WallLampSpacing)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                // Para la vereda izquierda el centro de la calle cae hacia +X (giro +90°) y para la
                // derecha hacia −X (giro −90°).
                float yaw = side > 0 ? -MathF.PI * 0.5f : MathF.PI * 0.5f;
                instances.Add(Placed(
                    new Vector3(side * WallLampOffset, WallLampHeight, z + (side > 0 ? 6f : 0f)), yaw, 1f));
            }
        }
        return instances;
    }

    /// <summary>
    /// Las cajas de cartón van APOYADAS contra una fachada, junto a la puerta de un local: ahí es
    /// donde están las cajas en una calle. Sueltas en medio de la calzada —como estaban— son basura
    /// tirada por el decorador.
    /// </summary>
    private static IReadOnlyList<SceneInstance> BoxPlacements() => new[]
    {
        Placed(new Vector3(-7.5f, KerbHeight, HeroCarZ + 5.6f), 0.55f, 1f),
        Placed(new Vector3(-7.1f, KerbHeight, HeroCarZ + 6.3f), 2.35f, 0.85f),
        Placed(new Vector3(-7.6f, KerbHeight + 0.62f, HeroCarZ + 5.9f), 1.15f, 0.78f),
        Placed(new Vector3(7.4f, KerbHeight, HeroCarZ + 33.5f), 3.05f, 1f),
        Placed(new Vector3(7.7f, KerbHeight, HeroCarZ + 34.2f), 1.25f, 0.88f)
    };

    // =========================================================================================
    // Luces y sombras de la escena
    // =========================================================================================

    /// <summary>
    /// Luces puntuales: los faroles (cálidos, colgando sobre la calle) y los carteles de neón
    /// (saturados, pegados al frente de las fachadas). Son DATO de la escena, y el shader las aplica
    /// igual en las cuatro APIs. Máximo <see cref="SceneDefinition.MaxPointLights"/>.
    /// </summary>
    private static IReadOnlyList<ScenePointLight> NeonLights()
    {
        var lights = new List<ScenePointLight>();

        // Los faroles alumbran desde DONDE ESTÁN: cada una de estas tres luces cae en la linterna de un
        // poste de verdad (ver PostLampPlacements: los postes de la izquierda están en z = 11 + 17k y
        // los de la derecha en z = 17 + 17k). Antes estaban a 6,4 m de alto sobre el EJE de la calle
        // —en el aire, a 2,4 m por encima de cualquier linterna— porque se creía que el farol tenía un
        // brazo largo que llegaba hasta el centro; el poste mide 3,87 m y su linterna está en la punta.
        // El radio sigue siendo largo a propósito: la caída es suave y un radio corto apaga la luz antes
        // de tocar la fachada, que deja la calle en un túnel de un solo charco de luz por farol.
        lights.Add(new ScenePointLight(new Vector3(-(RoadHalfWidth + PostLampInset), PostLampLanternHeight, 11f),
            new Vector3(1.0f, 0.74f, 0.44f), 22f, 26f));
        lights.Add(new ScenePointLight(new Vector3(-(RoadHalfWidth + PostLampInset), PostLampLanternHeight, 28f),
            new Vector3(1.0f, 0.74f, 0.44f), 19f, 26f));
        lights.Add(new ScenePointLight(new Vector3(RoadHalfWidth + PostLampInset, PostLampLanternHeight, 51f),
            new Vector3(1.0f, 0.74f, 0.44f), 17f, 26f));

        // Los carteles tiran su color sobre el frente: son la mitad de la identidad de la calle.
        for (int i = 0; i < SignLayout.Length && lights.Count < SceneDefinition.MaxPointLights; i++)
        {
            var sign = SignLayout[i];
            if (sign.Z > HeroCarZ + 30f) continue;      // los del fondo no entran en el presupuesto
            lights.Add(new ScenePointLight(
                new Vector3(sign.Side * (FacadeOffset - 0.85f), sign.Y, sign.Z),
                SignColors[sign.Color], 17f - i * 1.4f, 17f));
        }

        return lights;
    }

    /// <summary>
    /// Esferas de sombra: los objetos grandes que tienen que APOYARSE en el piso. Sin una pasada de
    /// sombras, esto es lo que evita que el auto parezca flotar (ver SceneDefinition.ShadowCasters).
    /// </summary>
    private static IReadOnlyList<Vector4> NeonShadowCasters()
    {
        var casters = new List<Vector4>();
        foreach (var car in CarPlacements())
        {
            casters.Add(new Vector4(car.PositionScale.X, 1.0f, car.PositionScale.Z, 2.6f));
        }
        casters.Add(new Vector4(0f, 6.0f, HeroCarZ + 4f, 0.7f));
        casters.Add(new Vector4(0f, 6.0f, HeroCarZ + 22f, 0.7f));
        casters.Add(new Vector4(0f, 6.0f, HeroCarZ + 44f, 0.7f));
        return casters;
    }

    // ---- Carteles de neón: dónde y de qué color (es la identidad de la calle) ----

    private static readonly (float Z, float Side, float Y, int Color, bool Vertical, bool Projecting)[] SignLayout =
    {
        // La Y los pone a la altura de un cartel DE VERDAD: arriba de la vidriera del local (que llega a
        // 3,05 m) y abajo del primer piso. Los verticales salen un poco más arriba porque van montados
        // contra la fachada y se leen desde lejos. Antes colgaban a media pared, a 5 o 6 m, y a ese
        // tamaño flotaban sobre el ladrillo sin pertenecer a nada.
        // El último campo es "sobresale": el cartel va PERPENDICULAR al frente, colgado sobre la
        // vereda. Son los que leen una calle comercial de verdad desde cualquier punto —de costado
        // se ve el canto, así que no se leen como un rectángulo pintado sobre el ladrillo— y son la
        // razón de que los verticales vayan más arriba: cuelgan por encima de la cabeza del que pasa.
        (-6.5f, -1f, 4.00f, 0, false, false),
        (3.0f, 1f, 3.60f, 1, false, false),
        (9.5f, 1f, 5.60f, 2, true, true),
        (17.0f, -1f, 4.20f, 0, false, false),
        (26.0f, 1f, 4.40f, 1, true, false),
        (33.0f, -1f, 3.80f, 2, false, false),
        (44.0f, 1f, 4.30f, 0, false, false),
        (52.0f, -1f, 5.40f, 1, true, true),
        (62.0f, 1f, 3.90f, 2, false, false),
        (74.0f, -1f, 4.10f, 0, false, false)
    };

    private static readonly Vector3[] SignColors =
    {
        new(1.00f, 0.06f, 0.55f),   // magenta
        new(0.10f, 0.72f, 1.00f),   // cian
        new(1.00f, 0.52f, 0.08f)    // ámbar
    };

    /// <summary>
    /// Cuelga los carteles del frente: el CUERPO oscuro contra la pared y el PANEL encendido montado
    /// adentro, separados lo suficiente como para que su propia sombra los despegue del ladrillo, con
    /// dos tirantes que los sostienen (los tirantes van a la malla de herrajes: un cartel que flota se
    /// lee como un error).
    /// </summary>
    private static void BuildSigns(StreetBuilder street)
    {
        foreach (var sign in SignLayout)
        {
            float yaw = sign.Side > 0f ? -MathF.PI * 0.5f : MathF.PI * 0.5f;
            // Los que sobresalen giran 90° más: el panel pasa a mirar a lo largo de la calle y su ancho
            // sale del frente hacia la vereda.
            if (sign.Projecting) yaw += MathF.PI * 0.5f;
            float x = sign.Side * (FacadeOffset - (sign.Projecting ? 0.80f : 0.28f));
            var color = SignColors[sign.Color];
            var position = new Vector3(x, sign.Y, sign.Z);

            // El cuerpo: chapa oscura que se tiñe apenas con el color del cartel, como si la luz del
            // propio aviso cayera sobre su marco.
            var bodyTint = new Vector3(0.050f, 0.053f, 0.062f) + color * 0.14f;
            (sign.Vertical ? street.SignBodiesVertical : street.SignBodies).Add(
                Tinted(Placed(position, yaw, 1f), bodyTint, roughness: 0.55f, metallic: 0.35f));

            // El panel encendido, adelantado sobre la cara del cuerpo.
            (sign.Vertical ? street.SignsVertical : street.Signs).Add(
                Tinted(Placed(position, yaw, 1f), color, roughness: 0.35f, glow: SignGlow));

            if (sign.Projecting)
            {
                // Sobresale: una ménsula que sale de la pared por arriba del cartel y una varilla
                // diagonal de refuerzo, que es lo que sostiene un cartel colgado de verdad.
                float armY = sign.Y + SignBodyHalfHeight + 0.10f;
                AddTube(street.Metal,
                    new Vector3(sign.Side * FacadeOffset, armY, sign.Z),
                    new Vector3(x + sign.Side * 0.10f, armY, sign.Z), 0.03f);
                AddTube(street.Metal,
                    new Vector3(sign.Side * (FacadeOffset - 0.06f), sign.Y, sign.Z),
                    new Vector3(sign.Side * (FacadeOffset - 0.52f), armY, sign.Z), 0.022f);
            }
            else
            {
                // Tirantes: dos por cartel, del borde de arriba a la pared. Van POR DETRÁS del cuerpo, así
                // que lo que se ve es la ménsula, no el tubo colgado del aire.
                float half = sign.Vertical ? 0.20f : 0.44f;
                foreach (float offset in new[] { -half, half })
                {
                    AddTube(street.Metal,
                        new Vector3(sign.Side * FacadeOffset, sign.Y + 0.22f, sign.Z + offset),
                        new Vector3(x - sign.Side * 0.06f, sign.Y + 0.22f, sign.Z + offset), 0.026f);
                }
            }
        }
    }

    // =========================================================================================
    // Cámara: un plano secuencia en tres planos encadenados
    // =========================================================================================

    /// <summary>
    /// Travelling de 60 s en TRES planos encadenados, función pura del tiempo (dos corridas de la
    /// misma duración recorren exactamente el mismo camino, que es lo que permite comparar):
    ///
    /// 1. 0–17 s · dolly bajo a 1,15 m POR EL CARRIL IZQUIERDO, que pasa al auto héroe por la izquierda
    ///    y lo deja de costado en cuadro (el auto está en x = +3,2 y la cámara no pasa de x = -1,4).
    /// 2. 17–38 s · sube a 5,4 m y se abre hacia las fachadas, que es donde se ve el trabajo de
    ///    ventanas, balcones y cornisas.
    /// 3. 38–60 s · plano general alto y lento calle abajo, con los faroles en fila y las azoteas.
    ///
    /// La trayectoria se mantiene dentro de |x| ≤ 5,6 (fuera de faroles a 6,15 y de fachadas a 8,4) y
    /// arriba de los autos desde el tramo 2: ningún plano puede atravesar geometría, que era el
    /// defecto del recorrido viejo (entraba en el auto a los ~9 s).
    /// </summary>
    private static (Vector3 Eye, Vector3 Target) CameraAt(double seconds)
    {
        float t = (float)Math.Clamp(seconds, 0, 120);

        // Tramo 1: junto al auto, por el carril izquierdo.
        if (t < 17f)
        {
            float k = Ease(t / 17f);
            var eye = new Vector3(Lerp(-2.5f, -1.4f, k), Lerp(1.05f, 1.30f, k), Lerp(-4.5f, 19f, k));
            var target = new Vector3(Lerp(1.7f, 0.8f, k), Lerp(0.95f, 1.20f, k), Lerp(8f, 34f, k));
            return (eye, target);
        }

        // Tramo 2: sube y se abre a las fachadas.
        if (t < 38f)
        {
            float k = Ease((t - 17f) / 21f);
            var eye = new Vector3(Lerp(-1.4f, 1.1f, k), Lerp(1.30f, 5.40f, k), Lerp(19f, 33f, k));
            var target = new Vector3(Lerp(0.8f, -3.4f, k), Lerp(1.20f, 6.30f, k), Lerp(34f, 45f, k));
            return (eye, target);
        }

        // Tramo 3: plano general calle abajo.
        float w = Ease(Math.Min(1f, (t - 38f) / 22f));
        var wide = new Vector3(Lerp(1.1f, 0.3f, w), Lerp(5.40f, 7.20f, w), Lerp(33f, 46f, w));
        var aim = new Vector3(Lerp(-3.4f, -0.9f, w), Lerp(6.30f, 3.60f, w), Lerp(45f, 98f, w));
        return (wide, aim);
    }

    /// <summary>Arranque y frenada suaves (smoothstep): un travelling lineal se siente a máquina.</summary>
    private static float Ease(float k)
    {
        k = Math.Clamp(k, 0f, 1f);
        return k * k * (3f - 2f * k);
    }

    private static float Lerp(float from, float to, float k) => from + (to - from) * k;

    // =========================================================================================
    // Geometría propia (con UV: es lo que permite texturizar con materiales reales)
    // =========================================================================================

    private static SceneInstance Identity() => new(Vector4.Zero with { W = 1f }, Vector4.Zero);

    /// <summary>Instancia en el piso: la altura la pone el que llama (los modelos se apoyan con su
    /// caja local, ver <see cref="GltfModel.Min"/>).</summary>
    private static SceneInstance Placed(Vector3 position, float yaw, float scale, float parameter = 0f) =>
        new(new Vector4(position, scale), new Vector4(yaw, 0f, SceneDefinition.KindTextured, parameter));

    /// <summary>
    /// Fuerza de emisión de los carteles. BAJA a propósito: el tonemapping es ACES, que desatura lo
    /// que le entra saturado —a 3,2 el magenta salía blanco—, así que el neón no se hace más brillante
    /// subiéndole la emisión sino dejándola cerca de 1 y dándole el color puro.
    /// </summary>
    private const float SignGlow = 0.85f;

    /// <summary>
    /// Instancia con tinta propia y fuerzas de material EXPLÍCITAS. Ojo con la trampa: el horneado de
    /// la malla solo completa los vectores que quedaron en cero (ver <c>SceneDefinition.Bake</c>), y
    /// cero es un valor válido — un material con la rugosidad en cero es un espejo.
    /// </summary>
    private static SceneInstance Tinted(
        SceneInstance instance, Vector3 tint, float roughness = 1f, float metallic = 0f, float glow = 0f) =>
        instance with
        {
            MaterialTint = new Vector4(tint, 1f),
            MaterialParams = new Vector4(1f, roughness, metallic, glow)
        };

    /// <summary>Quad con normal y UV: la UV se escala por <paramref name="uvPerMeter"/> repeticiones
    /// por metro (el material repite su textura).</summary>
    private static void AddQuad(
        List<SceneVertex> vertices, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, float uvPerMeter = 0f)
    {
        Vector2 uvA = Vector2.Zero, uvB = Vector2.Zero, uvC = Vector2.Zero, uvD = Vector2.Zero;
        if (uvPerMeter > 0f)
        {
            // La UV se deriva de la posición en el PLANO del quad: así una pared texturada repite la
            // textura cada 1/uvPerMeter metros sin que nadie tenga que numerar los tramos.
            // La V va CONTRA el eje "arriba" del quad a propósito: el bitmap se sube con la fila 0
            // = V 0 = ARRIBA de la imagen, así que con la V hacia arriba la foto de un ladrillo
            // quedaba cabeza abajo. Con la V hacia abajo la textura se lee en el mismo orden en el
            // que se mira la pared (y es la convención de las UV de glTF, que es la que asumen los
            // mapas de normales de los modelos).
            Vector3 right = Vector3.Normalize(SafeDirection(a, b));
            Vector3 up = Vector3.Normalize(SafeDirection(b, c));
            uvA = new Vector2(Vector3.Dot(a, right), -Vector3.Dot(a, up)) * uvPerMeter;
            uvB = new Vector2(Vector3.Dot(b, right), -Vector3.Dot(b, up)) * uvPerMeter;
            uvC = new Vector2(Vector3.Dot(c, right), -Vector3.Dot(c, up)) * uvPerMeter;
            uvD = new Vector2(Vector3.Dot(d, right), -Vector3.Dot(d, up)) * uvPerMeter;
        }

        vertices.Add(new SceneVertex(a, normal) { Uv = uvA });
        vertices.Add(new SceneVertex(b, normal) { Uv = uvB });
        vertices.Add(new SceneVertex(c, normal) { Uv = uvC });
        vertices.Add(new SceneVertex(a, normal) { Uv = uvA });
        vertices.Add(new SceneVertex(c, normal) { Uv = uvC });
        vertices.Add(new SceneVertex(d, normal) { Uv = uvD });
    }

    /// <summary>Caja centrada con las seis caras texturadas (UV por cara, en metros).</summary>
    private static void AddBox(List<SceneVertex> vertices, Vector3 center, Vector3 halfExtents, float uvPerMeter = 0f)
    {
        float x = halfExtents.X, y = halfExtents.Y, z = halfExtents.Z;
        Vector3 c0 = center + new Vector3(-x, -y, -z);
        Vector3 c1 = center + new Vector3(x, -y, -z);
        Vector3 c2 = center + new Vector3(x, y, -z);
        Vector3 c3 = center + new Vector3(-x, y, -z);
        Vector3 c4 = center + new Vector3(-x, -y, z);
        Vector3 c5 = center + new Vector3(x, -y, z);
        Vector3 c6 = center + new Vector3(x, y, z);
        Vector3 c7 = center + new Vector3(-x, y, z);

        AddQuad(vertices, c4, c5, c6, c7, Vector3.UnitZ, uvPerMeter);      // frente (+Z)
        AddQuad(vertices, c1, c0, c3, c2, -Vector3.UnitZ, uvPerMeter);     // atrás (-Z)
        AddQuad(vertices, c0, c4, c7, c3, -Vector3.UnitX, uvPerMeter);     // izquierda (-X)
        AddQuad(vertices, c5, c1, c2, c6, Vector3.UnitX, uvPerMeter);      // derecha (+X)
        AddQuad(vertices, c7, c6, c2, c3, Vector3.UnitY, uvPerMeter);      // arriba (+Y)
        AddQuad(vertices, c0, c1, c5, c4, -Vector3.UnitY, uvPerMeter);     // abajo (-Y)
    }

    /// <summary>Caja entre dos esquinas. Al armar fachadas se piensa en "de acá hasta allá", no en
    /// centro y semiextensión.</summary>
    private static void AddSlab(List<SceneVertex> vertices, Vector3 min, Vector3 max, float uvPerMeter = 0f) =>
        AddBox(vertices, (min + max) * 0.5f, (max - min) * 0.5f, uvPerMeter);

    /// <summary>
    /// Tubo recto de cuatro caras entre dos puntos: bajantes, cables y bolardos. Es lo que da líneas
    /// VERTICALES y horizontales largas, que es de lo que está hecha una calle de verdad.
    /// </summary>
    private static void AddTube(List<SceneVertex> vertices, Vector3 from, Vector3 to, float radius)
    {
        var axis = to - from;
        if (axis.LengthSquared() < 1e-9f) return;
        axis = Vector3.Normalize(axis);

        var helper = MathF.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var u = Vector3.Normalize(Vector3.Cross(axis, helper)) * radius;
        var v = Vector3.Normalize(Vector3.Cross(axis, u)) * radius;

        Span<Vector3> ring = stackalloc Vector3[4];
        ring[0] = u;
        ring[1] = v;
        ring[2] = -u;
        ring[3] = -v;

        for (int i = 0; i < 4; i++)
        {
            var offsetA = ring[i];
            var offsetB = ring[(i + 1) & 3];
            if (offsetA.LengthSquared() < 1e-12f || offsetB.LengthSquared() < 1e-12f) continue;

            var normal = Vector3.Normalize(offsetA + offsetB);
            AddQuad(vertices, from + offsetA, to + offsetA, to + offsetB, from + offsetB, normal, FittingRepeat);
        }
    }

    private static Vector3 SafeDirection(Vector3 from, Vector3 to)
    {
        var delta = to - from;
        return delta.LengthSquared() < 1e-8f ? Vector3.UnitX : delta;
    }

    private static SceneTextureSource? Texture(string root, string slug, string channel)
    {
        string directory = Path.Combine(root, "textures", slug);
        if (!Directory.Exists(directory)) return null;

        // El nombre del archivo lo pone Poly Haven con el canal adentro ("asphalt_01_diff_1k.jpg",
        // "brick_wall_001_diffuse_1k.jpg", ...): se busca por el canal en vez de adivinar el sufijo.
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

    /// <summary>Descripción de la escena para el informe: qué set de assets usa y de dónde salió.</summary>
    internal static string DescribeAssets()
    {
        var text = new StringBuilder();
        text.Append("assets: set 'neon' (Poly Haven, CC0)");
        return text.ToString();
    }
}
