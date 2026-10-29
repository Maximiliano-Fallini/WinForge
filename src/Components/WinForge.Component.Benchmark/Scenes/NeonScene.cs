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
                "Diorama de calle a nivel de cine: autos estacionados, escaleras de incendio, azoteas " +
                "con tanques de agua y guirnaldas sobre asfalto mojado. Usa modelos y texturas reales " +
                "(CC0) y mide el trabajo por píxel: materiales con relieve, varias luces puntuales y lluvia.",
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
        // La planta baja comercial se decide ANTES de las fachadas: el generador de fachadas tiene que
        // saber qué celdas no lleva el kit porque las ocupa un local.
        var bays = LayoutGround(rows);
        foreach (var row in rows) AddMassing(street, row);
        BuildUrbanDetails(street, rows);
        BuildRooftops(street, rows);
        BuildOverheadWires(street);
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
        var facades = NeonFacade.Build(root, rows, FacadeOffset, bays, rng, Note);
        if (facades.Count == 0)
        {
            AddFlatFronts(street, rows);
            Add(meshes, "frentes de emergencia", street.FlatFronts, materials.Brick1);
        }
        else
        {
            // Los locales son geometría propia: van solo si el kit está, porque el frente de
            // emergencia es un plano ciego en el mismo plano y taparía la vidriera.
            BuildShopfronts(street, rows, bays);
        }
        foreach (var mesh in facades) meshes.Add(mesh);

        // ---- Superficie ----
        Add(meshes, "veredas", street.Walk, materials.Cement);
        Add(meshes, "herrajes", street.Metal, materials.Metal);
        Add(meshes, "juntas de vereda", street.SidewalkSeams, materials.Recess);
        Add(meshes, "mobiliario urbano", street.Furniture, materials.Metal);
        Add(meshes, "toldos de comercios", street.ShopAwnings, materials.Fabric);
        Add(meshes, "hidrantes", street.Hydrants, materials.Hydrant);
        Add(meshes, "zócalo de fachada", street.FacadeBase, materials.Brick2);
        Add(meshes, "ladrillo de locales", street.ShopBrick, materials.Brick1);
        Add(meshes, "marcos de local", street.ShopFrame, materials.ShopFrame);
        Add(meshes, "vidrieras cálidas", street.ShopGlassWarm, materials.ShopWarm);
        Add(meshes, "vidrieras frías", street.ShopGlassCool, materials.ShopCool);
        Add(meshes, "maderas de local y azotea", street.ShopWood, materials.Wood);

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

        // Guirnaldas: las bombillas son la única parte emisiva de los cables, así que van en su
        // propia malla instanciada (una por todas) con el material de neón.
        Add(meshes, "luces colgantes", BulbVertices(), street.Bulbs, materials.Sign);

        AddModel(meshes, root, "covered_car", "auto", CarPlacements(), groundOnRoad: true);
        AddModel(meshes, root, "street_lamp_01", "farol", PostLampPlacements());
        AddModel(meshes, root, "street_lamp_02", "farol de pared", WallLampPlacements());
        // La escalera de incendio es del kit y estaba sin usar: es la pieza que le faltaba a la
        // fachada para leerse como una cuadra de departamentos y no como una pared con ventanas.
        AddModel(meshes, root, "modular_fire_escape", "escalera de incendio", FireEscapePlacements(rows), groundOnRoad: true);
        AddModel(meshes, root, "cardboard_box_01", "cajas", CardboardPlacements(), groundOnRoad: true);
        AddModel(meshes, root, "old_tyre", "neumáticos", TyrePlacements(), groundOnRoad: true);
        // No dejar cajas o neumáticos delante de entradas elegidas por el generador de fachadas.

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
            float length = 0.28f + (float)rng.NextDouble() * 0.55f;

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
        SceneMaterial Sign,
        SceneMaterial Hydrant,
        SceneMaterial ShopWarm,
        SceneMaterial ShopCool,
        SceneMaterial ShopFrame);

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
            RoughnessScale = 0.58f,
            MetallicScale = 0.0f,
            NormalStrength = 0.8f
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

        var sign = new SceneMaterial
        {
            Name = "neón",
            Tint = Vector3.One,
            RoughnessScale = 0.35f,
            Emissive = new Vector3(1f, 1f, 1f),
            EmissiveStrength = SignGlow
        };

        var hydrant = new SceneMaterial
        {
            Name = "hidrante pintado",
            Tint = new Vector3(0.58f, 0.13f, 0.075f),
            RoughnessScale = 0.58f
        };

        // Vidrieras de local: vidrio con luz adentro. El vidrio de la escena es OPACO (no hay pase de
        // transparencias), así que una vidriera "vacía" sería un espejo negro; lo que la hace leer como
        // un negocio abierto a la noche es la emisión cálida detrás del marco. Dos temperaturas —cálida
        // para la mayoría, fría para los locales de electrónica/bar— evitan que la vereda parezca una
        // hilera de luces clonadas.
        SceneMaterial Shopwindow(string name, Vector3 tint, float glow) => new()
        {
            Name = name,
            Tint = tint,
            // Rugoso y dieléctrico: un vidrio pulido devuelve el cielo nocturno y la vidriera se lee
            // AZUL, no como un local encendido (fue el primer intento). Con la rugosidad alta manda
            // el color propio y la emisión hace el resto. La emisión la toma el shader de la tinta
            // por instancia (`MaterialTint * glow`), no del material.
            RoughnessScale = 0.78f,
            MetallicScale = 0f,
            Emissive = Vector3.One,
            EmissiveStrength = glow
        };
        var shopWarm = Shopwindow("vidriera cálida", new Vector3(1.00f, 0.50f, 0.15f), 0.44f);
        var shopCool = Shopwindow("vidriera fría", new Vector3(0.32f, 0.55f, 0.88f), 0.30f);

        // Marco de local: acero pintado casi negro. El herraje de la calle es gris claro y con la luz
        // cálida se leía del mismo color que el ladrillo, así que las vidrieras no tenían marco.
        var shopFrame = new SceneMaterial
        {
            Name = "marco de local",
            Albedo = concreteAlbedo,
            Normal = concreteNormal,
            Arm = concreteArm,
            Tint = new Vector3(0.045f, 0.048f, 0.056f),
            // Sin metalicidad: con 0,35 el marco devolvía el HDRI cálido y se veía tan claro como el
            // ladrillo, así que las vidrieras parecían no tener marco.
            RoughnessScale = 0.52f,
            MetallicScale = 0f
        };

        return new MaterialSet(
            asphalt, cement, metal,
            brick1, brick2, brick3, new[] { brick1, brick2, brick3 },
            recess, glass, wood, fabric, sign, hydrant, shopWarm, shopCool, shopFrame);
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
        internal readonly List<SceneVertex> SidewalkSeams = new();
        internal readonly List<SceneVertex> Furniture = new();
        internal readonly List<SceneVertex> ShopAwnings = new();
        internal readonly List<SceneVertex> Hydrants = new();

        // ---- Locales ----
        internal readonly List<SceneVertex> ShopBrick = new();      // pilastras y dinteles de ladrillo
        internal readonly List<SceneVertex> ShopFrame = new();      // marcos de acero de las vidrieras
        internal readonly List<SceneVertex> ShopWood = new();       // zócalos y hojas de puerta
        internal readonly List<SceneVertex> ShopGlassWarm = new();  // vidrieras encendidas
        internal readonly List<SceneVertex> ShopGlassCool = new();
        internal readonly List<SceneVertex> FacadeBase = new();     // zócalo que cierra el pie de fachada

        /// <summary>Tres tintas de ladrillo (0-2) y los cantos de los vanos (3).</summary>
        internal readonly List<SceneVertex>[] Walls = { new(), new(), new(), new() };

        internal readonly List<SceneInstance> Signs = new();
        internal readonly List<SceneInstance> SignsVertical = new();
        /// <summary>Bombillas de las guirnaldas: instancias de una malla chica, material emisivo.</summary>
        internal readonly List<SceneInstance> Bulbs = new();
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

            // Vereda CONTINUA: sin sendas peatonales no hay aberturas de cordón ni rampas. Las
            // rampas eran dos losas inclinadas que, vistas desde la cámara, devolvían otra luz que
            // el hormigón de al lado y se leían como un polígono gris pegado sobre la vereda.
            AddSidewalkSection(street, side, StreetStart, StreetStart + StreetLength);

            // Juntas finas entre paños de hormigón: a ras de vereda y regulares en metros.
            for (float z = StreetStart + 3f; z < StreetStart + StreetLength - 2f; z += 3f)
            {
                AddQuad(street.SidewalkSeams,
                    new Vector3(inner, KerbHeight + 0.002f, z), new Vector3(outer, KerbHeight + 0.002f, z),
                    new Vector3(outer, KerbHeight + 0.002f, z + 0.018f), new Vector3(inner, KerbHeight + 0.002f, z + 0.018f),
                    Vector3.UnitY, ConcreteRepeat);
            }
        }

        // Sin pintura de calle: las franjas del paso peatonal y la línea de eje eran quads planos
        // que, sin desgaste ni relieve, se leían como cinta blanca pegada sobre el asfalto. La
        // calzada queda desnuda y la escala la dan el cordón, las bocas de tormenta y los autos.
    }

    private static void AddSidewalkSection(StreetBuilder street, float side, float start, float end)
    {
        float inner = side * RoadHalfWidth;
        float outer = side * (RoadHalfWidth + SidewalkWidth);
        AddQuad(street.Walk, new Vector3(inner, KerbHeight, start), new Vector3(outer, KerbHeight, start),
            new Vector3(outer, KerbHeight, end), new Vector3(inner, KerbHeight, end), Vector3.UnitY, ConcreteRepeat);
        AddQuad(street.Walk, new Vector3(inner, 0f, start), new Vector3(inner, 0f, end),
            new Vector3(inner, KerbHeight, end), new Vector3(inner, KerbHeight, start),
            new Vector3(-side, 0f, 0f), ConcreteRepeat);
    }

    /// <summary>
    /// El detalle que hace que la calle sea una calle: bocas de tormenta contra el cordón, tapas de
    /// inspección y bolardos. Todo geometría propia (una malla por material) y ningún asset nuevo.
    /// </summary>
    private static void BuildGroundDetail(StreetBuilder street, Random rng)
    {
        for (float z = StreetStart + 4f; z < StreetStart + StreetLength - 4f; z += 27f)
        {
            foreach (float side in new[] { -1f, 1f })
            {
                // Boca de tormenta: una reja hundida contra el cordón (y el hueco que se ve debajo).
                float x = side * (RoadHalfWidth - 0.42f);
                AddQuad(street.SidewalkSeams,
                    new Vector3(x - 0.34f, 0.002f, z - 0.60f), new Vector3(x + 0.34f, 0.002f, z - 0.60f),
                    new Vector3(x + 0.34f, 0.002f, z + 0.60f), new Vector3(x - 0.34f, 0.002f, z + 0.60f), Vector3.UnitY);
                for (int bar = 0; bar < 5; bar++)
                {
                    float bz = z - 0.44f + bar * 0.22f;
                    AddSlab(street.Metal,
                        new Vector3(x - 0.30f, 0.002f, bz - 0.035f), new Vector3(x + 0.30f, 0.008f, bz + 0.035f), FittingRepeat);
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
            if (MathF.Abs(z - (HeroCarZ + 18f)) < 1.8f || MathF.Abs(z - (HeroCarZ + 52f)) < 1.8f) continue;
            foreach (float side in new[] { -1f, 1f })
            {
                float x = side * (RoadHalfWidth + 0.55f);
                AddTube(street.Metal, new Vector3(x, KerbHeight, z), new Vector3(x, KerbHeight + 0.78f, z), 0.075f);
                AddTube(street.Metal, new Vector3(x, KerbHeight + 0.74f, z), new Vector3(x, KerbHeight + 0.90f, z), 0.055f);
            }
        }

    }

    /// <summary>Elementos urbanos deterministas para vestir las fachadas y las veredas del recorrido.</summary>
    private static void BuildUrbanDetails(StreetBuilder street, IReadOnlyList<NeonFacade.FacadeRow> rows)
    {
        var rng = new Random(20261028);
        foreach (var row in rows)
        {
            if (row.Z1 < HeroCarZ - 12f || row.Z0 > HeroCarZ + 70f) continue;
            float width = row.Z1 - row.Z0;
            float x = row.Side * (FacadeOffset - 0.18f);

            // Los gabinetes van junto al medianil, fuera del paso del local.
            float serviceZ = row.Z0 + 0.62f;
            float serviceX = row.Side * (FacadeOffset - 0.30f);
            // Contra el medianil y chico: pegado a la vidriera y en tamaño grande tapaba el local
            // entero y se leía como un pilar sin terminar.
            AddBox(street.Furniture, new Vector3(serviceX, KerbHeight + 0.46f, serviceZ),
                new Vector3(0.14f, 0.46f, 0.22f), FittingRepeat);
            AddTube(street.Furniture, new Vector3(serviceX, KerbHeight + 0.92f, serviceZ),
                new Vector3(serviceX, KerbHeight + 2.4f, serviceZ), 0.032f);

            // Los toldos NO se cuelgan por edificio: van por local y los arma BuildShopfronts. Un toldo
            // sobre una ventana de departamento era justamente el negocio sin puerta que se veía.

            // Equipos de aire sobre soportes; su distribución es fija para que dos corridas coincidan.
            for (int floor = 1; floor < row.Floors; floor++)
            {
                if (rng.NextDouble() >= 0.42) continue;
                float z = row.Z0 + 1.5f + (float)rng.NextDouble() * Math.Max(0f, width - 3f);
                float y = floor * NeonFacade.Cell + 0.65f;
                AddBox(street.Furniture, new Vector3(x, y, z), new Vector3(0.18f, 0.24f, 0.42f), FittingRepeat);
                for (int slat = 0; slat < 4; slat++)
                {
                    float sy = y - 0.15f + slat * 0.10f;
                    AddTube(street.Metal, new Vector3(x - row.Side * 0.19f, sy, z - 0.32f),
                        new Vector3(x - row.Side * 0.19f, sy, z + 0.32f), 0.012f);
                }
                AddTube(street.Furniture, new Vector3(x, y - 0.24f, z - 0.3f),
                    new Vector3(row.Side * FacadeOffset, y - 0.42f, z - 0.3f), 0.025f);
            }
        }

        // Hidrantes de varias secciones y bocas laterales, junto al cordón.
        foreach (var point in new[] { (Side: -1f, Z: HeroCarZ + 2f), (Side: 1f, Z: HeroCarZ + 39f) })
        {
            var basePoint = new Vector3(point.Side * (RoadHalfWidth + 0.42f), KerbHeight, point.Z);
            AddRoundTube(street.Hydrants, basePoint, basePoint + Vector3.UnitY * 0.78f, 0.12f, 12);
            AddRoundTube(street.Hydrants, basePoint + Vector3.UnitY * 0.72f, basePoint + Vector3.UnitY * 0.86f, 0.16f, 12);
            AddRoundTube(street.Hydrants, basePoint + Vector3.UnitY * 0.50f - Vector3.UnitZ * 0.22f,
                basePoint + Vector3.UnitY * 0.50f + Vector3.UnitZ * 0.22f, 0.075f, 10);
        }

    }

    // =========================================================================================
    // Azoteas y aire: lo que se lee en los planos altos y en la silueta contra el cielo
    // =========================================================================================

    /// <summary>
    /// Viste las azoteas del tramo que recorre la cámara: caseta de escalera, tanque de agua sobre
    /// patas, antenas, respiraderos y equipos de aire. No es decorado suelto: son las formas que
    /// hacen que la línea del techo deje de ser un antepecho repetido y pase a leerse como una
    /// cuadra habitada. Todo va a las mallas que ya existen (masa oscura y herrajes), así que no
    /// agrega ni un draw call.
    /// </summary>
    private static void BuildRooftops(StreetBuilder street, IReadOnlyList<NeonFacade.FacadeRow> rows)
    {
        var rng = new Random(20261103);

        foreach (var row in rows)
        {
            if (row.Z1 < HeroCarZ - 16f || row.Z0 > HeroCarZ + 86f) continue;

            float face = row.Side * FacadeOffset;
            float roof = row.Height + 0.06f;           // sobre la losa de alquitrán, no dentro
            float zMin = row.Z0 + 1.3f;
            float zMax = row.Z1 - 1.3f;
            if (zMax - zMin < 2f) continue;

            // Caseta de escalera: una masa al fondo que corta la línea del techo.
            if (rng.NextDouble() < 0.52)
            {
                float cz = zMin + 1.2f + (float)rng.NextDouble() * MathF.Max(0.4f, zMax - zMin - 2.4f);
                float cx = face + row.Side * (row.Depth - 3.1f);
                AddBox(street.Walls[3], new Vector3(cx, roof + 1.05f, cz),
                    new Vector3(1.25f, 1.05f, 1.45f), BrickRepeat);
            }

            // Tanque de agua: el remate más neoyorquino de la silueta. Solo en edificios altos.
            if (row.Floors >= 4 && rng.NextDouble() < 0.5)
            {
                float radius = 0.85f + (float)rng.NextDouble() * 0.30f;
                float tx = face + row.Side * (2.2f + radius + (float)rng.NextDouble() * 1.2f);
                float tz = zMin + 1.2f + (float)rng.NextDouble() * MathF.Max(0.4f, zMax - zMin - 2.4f);
                AddWaterTank(street, new Vector3(tx, roof, tz), radius, 2.4f + (float)rng.NextDouble() * 0.5f);
            }

            // Antenas: tres travesaños y un mástil, apenas más finos que un poste.
            int antennas = rng.Next(0, 3);
            for (int i = 0; i < antennas; i++)
            {
                float ax = face + row.Side * (1.8f + (float)rng.NextDouble() * MathF.Max(0.5f, row.Depth - 3.6f));
                float az = zMin + (float)rng.NextDouble() * (zMax - zMin);
                float height = 1.4f + (float)rng.NextDouble() * 2.6f;
                var foot = new Vector3(ax, roof, az);
                AddTube(street.Metal, foot, foot + Vector3.UnitY * height, 0.024f);
                for (int bar = 0; bar < 3; bar++)
                {
                    float y = roof + height * (0.62f + bar * 0.12f);
                    AddTube(street.Metal, new Vector3(ax - 0.30f, y, az), new Vector3(ax + 0.30f, y, az), 0.012f);
                }
            }

            // Respiraderos: caño con sombrerete, el detalle que se ve desde el plano alto.
            if (rng.NextDouble() < 0.55)
            {
                float vx = face + row.Side * (2f + (float)rng.NextDouble() * MathF.Max(0.5f, row.Depth - 4f));
                float vz = zMin + (float)rng.NextDouble() * (zMax - zMin);
                var foot = new Vector3(vx, roof, vz);
                AddRoundTube(street.Metal, foot, foot + Vector3.UnitY * 0.70f, 0.14f, 8);
                AddRoundTube(street.Metal, foot + Vector3.UnitY * 0.70f, foot + Vector3.UnitY * 0.80f, 0.19f, 8);
            }

            // Equipo de aire acondicionado de azotea.
            if (rng.NextDouble() < 0.42)
            {
                float ux = face + row.Side * (2.4f + (float)rng.NextDouble() * MathF.Max(0.5f, row.Depth - 4.8f));
                float uz = zMin + (float)rng.NextDouble() * (zMax - zMin);
                AddBox(street.Metal, new Vector3(ux, roof + 0.30f, uz),
                    new Vector3(0.50f, 0.30f, 0.68f), FittingRepeat);
            }
        }
    }

    /// <summary>Tanque de madera sobre cuatro patas, con zunchos y tapa cónica.</summary>
    private static void AddWaterTank(StreetBuilder street, Vector3 anchor, float radius, float height)
    {
        float legHeight = height * 0.34f;
        float bodyHeight = height * 0.55f;
        float inset = radius * 0.62f;
        var feet = new[]
        {
            new Vector3(-inset, 0f, -inset), new Vector3(inset, 0f, -inset),
            new Vector3(-inset, 0f, inset), new Vector3(inset, 0f, inset)
        };

        foreach (var foot in feet)
        {
            AddTube(street.Metal, anchor + foot, anchor + foot + Vector3.UnitY * legHeight, 0.055f);
        }
        // Arriostramiento en las dos caras que dan a la calle.
        AddTube(street.Metal, anchor + feet[0] + Vector3.UnitY * 0.10f,
            anchor + feet[1] + Vector3.UnitY * (legHeight * 0.78f), 0.024f);
        AddTube(street.Metal, anchor + feet[2] + Vector3.UnitY * (legHeight * 0.78f),
            anchor + feet[3] + Vector3.UnitY * 0.10f, 0.024f);

        var bodyBase = anchor + Vector3.UnitY * legHeight;
        AddRoundTube(street.ShopWood, bodyBase, bodyBase + Vector3.UnitY * bodyHeight, radius, 14);
        for (int band = 0; band < 2; band++)
        {
            float y = bodyHeight * (0.28f + band * 0.44f);
            AddRoundTube(street.Metal, bodyBase + Vector3.UnitY * y,
                bodyBase + Vector3.UnitY * (y + 0.06f), radius + 0.022f, 14);
        }
        AddCone(street.Metal, bodyBase + Vector3.UnitY * bodyHeight, radius + 0.045f, 0.30f, 14);

        // Caño de venteo pegado al tanque, para que la silueta no sea un cilindro solo.
        var vent = anchor + new Vector3(radius * 0.55f, 0f, -radius * 0.55f);
        AddTube(street.Metal, vent, vent + Vector3.UnitY * (legHeight + bodyHeight + 0.55f), 0.034f);
    }

    /// <summary>Cono de caras triangulares (tapa del tanque): no hay primitiva de cono en el set.</summary>
    private static void AddCone(List<SceneVertex> vertices, Vector3 baseCenter, float radius, float height, int segments)
    {
        var apex = baseCenter + Vector3.UnitY * height;
        for (int i = 0; i < segments; i++)
        {
            float a = MathF.Tau * i / segments;
            float b = MathF.Tau * (i + 1) / segments;
            var p0 = baseCenter + new Vector3(MathF.Cos(a) * radius, 0f, MathF.Sin(a) * radius);
            var p1 = baseCenter + new Vector3(MathF.Cos(b) * radius, 0f, MathF.Sin(b) * radius);
            var normal = Vector3.Normalize(Vector3.Cross(apex - p0, p1 - p0));
            vertices.Add(new SceneVertex(p0, normal));
            vertices.Add(new SceneVertex(p1, normal));
            vertices.Add(new SceneVertex(apex, normal));
        }
    }

    /// <summary>
    /// Cables cruzando la calle con guirnaldas de bombillas cálidas. Son el gesto más barato de
    /// inmersión que le faltaba al diorama: una calle nocturna no es solo fachadas iluminadas, es
    /// también el cableado aéreo y la luz que cuelga por encima de la cabeza. La catenaria es una
    /// parábola (no una recta) y las bombillas van instanciadas en una sola malla.
    /// </summary>
    private static void BuildOverheadWires(StreetBuilder street)
    {
        var spans = new (float Z, float LeftY, float RightY, bool Lights)[]
        {
            (10f, 7.10f, 6.85f, true),
            (16f, 6.90f, 7.20f, false),
            (27f, 7.25f, 6.95f, true),
            (44f, 6.85f, 7.15f, true),
            (57f, 7.15f, 6.80f, false),
            (70f, 6.95f, 7.20f, true)
        };

        const int segments = 12;
        const float sag = 0.50f;

        foreach (var span in spans)
        {
            var left = new Vector3(-(FacadeOffset - 0.10f), span.LeftY, span.Z);
            var right = new Vector3(FacadeOffset - 0.10f, span.RightY, span.Z);

            var previous = left;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                var point = Vector3.Lerp(left, right, t);
                point.Y -= sag * 4f * t * (1f - t);
                AddTube(street.Metal, previous, point, 0.017f);
                previous = point;
            }

            // Placas de anclaje contra las dos fachadas. El centro va MEDIO ESPESOR hacia la pared
            // respecto del extremo del cable, así la placa apoya en el ladrillo y no queda flotando.
            AddBox(street.Metal, left - new Vector3(0.05f, 0f, 0f), new Vector3(0.05f, 0.15f, 0.09f), FittingRepeat);
            AddBox(street.Metal, right + new Vector3(0.05f, 0f, 0f), new Vector3(0.05f, 0.15f, 0.09f), FittingRepeat);

            if (!span.Lights) continue;

            float length = Vector3.Distance(left, right);
            int bulbs = Math.Max(3, (int)(length * 0.82f / 0.62f));
            for (int i = 0; i <= bulbs; i++)
            {
                float t = 0.09f + 0.82f * i / bulbs;
                var point = Vector3.Lerp(left, right, t);
                point.Y -= sag * 4f * t * (1f - t) + 0.085f;
                street.Bulbs.Add(Tinted(
                    Placed(point, 0f, 1f),
                    new Vector3(1.00f, 0.66f, 0.34f), roughness: 0.35f, glow: 1.35f));
            }
        }
    }

    /// <summary>Bombilla: un cilindro corto de seis caras; a la distancia de la calle se lee redonda.</summary>
    private static SceneVertex[] BulbVertices()
    {
        var vertices = new List<SceneVertex>(36);
        AddRoundTube(vertices, new Vector3(0f, -0.045f, 0f), new Vector3(0f, 0.045f, 0f), 0.038f, 6);
        return vertices.ToArray();
    }

    private static void AddRoundTube(List<SceneVertex> vertices, Vector3 from, Vector3 to, float radius, int segments)
    {
        Vector3 axis = to - from;
        if (axis.LengthSquared() < 1e-9f) return;
        axis = Vector3.Normalize(axis);
        Vector3 helper = MathF.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        Vector3 u = Vector3.Normalize(Vector3.Cross(axis, helper));
        Vector3 v = Vector3.Normalize(Vector3.Cross(axis, u));
        for (int i = 0; i < segments; i++)
        {
            float a = MathF.Tau * i / segments, b = MathF.Tau * (i + 1) / segments;
            Vector3 na = u * MathF.Cos(a) + v * MathF.Sin(a);
            Vector3 nb = u * MathF.Cos(b) + v * MathF.Sin(b);
            AddQuad(vertices, from + na * radius, to + na * radius, to + nb * radius, from + nb * radius,
                Vector3.Normalize(na + nb), FittingRepeat);
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
    /// Reparte la PLANTA BAJA de cada edificio. Todo edificio tiene un PORTAL de viviendas; según el
    /// edificio se suman un garaje y/o locales. Antes cada fila con locales se llenaba entera de
    /// vidrieras, así que la calle quedaba sin una sola entrada normal ni un garaje: una calle de
    /// centros comerciales, no una calle. Ahora la mezcla es explícita y la semilla es fija.
    /// </summary>
    private static List<NeonFacade.Shop> LayoutGround(IReadOnlyList<NeonFacade.FacadeRow> rows)
    {
        var rng = new Random(20261029);
        var bays = new List<NeonFacade.Shop>(96);

        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            int columns = Math.Max(1, (int)MathF.Round((row.Z1 - row.Z0) / NeonFacade.Cell));
            if (columns < 2) continue;

            var taken = new bool[columns];

            // Portal: SIEMPRE hay uno. Es la entrada de viviendas del edificio y lo que hace que la
            // planta baja no sea una hilera de vidrieras.
            int portal = rng.Next(columns);
            taken[portal] = true;
            bays.Add(new NeonFacade.Shop(rowIndex, portal, 1, 0, 0, NeonFacade.GroundKind.Portal));

            // Garaje: en frentes anchos y cada tanto. La persiana metálica es una lectura distinta de
            // la vidriera aun de lejos, que es justo lo que faltaba.
            if (columns >= 4 && rng.NextDouble() < 0.30)
            {
                int garage;
                do { garage = rng.Next(columns); } while (taken[garage]);
                taken[garage] = true;
                bays.Add(new NeonFacade.Shop(rowIndex, garage, 1, 0, 0, NeonFacade.GroundKind.Garage));
            }

            // Una de cada cuatro plantas bajas es solo viviendas (portal + ventanas).
            if (rng.NextDouble() < 0.26) continue;

            // Locales en los tramos LIBRES de al menos dos celdas. Los tramos de una sola celda se
            // quedan como ventana del kit: un local de 3 m con puerta no entra.
            int column = 0;
            while (column < columns)
            {
                if (taken[column]) { column++; continue; }
                int run = 0;
                while (column + run < columns && !taken[column + run]) run++;
                if (run < 2) { column++; continue; }

                int width = Math.Min(run, rng.NextDouble() < 0.52 ? 2 : 3);
                int door = rng.Next(width);
                // Dos tercios de los locales con luz cálida: la calle tiene que verse de sodio, con
                // algún local frío suelto (electrónica, farmacia) que rompa la hilera.
                bays.Add(new NeonFacade.Shop(rowIndex, column, width, door, rng.Next(3) == 0 ? 1 : 0));
                column += width;
            }
        }
        return bays;
    }

    // ---- Medidas del local (metros desde el plano del frente del kit) ----
    private const float ShopProud = 0.05f;        // lo que el marco sobresale hacia la vereda
    private const float ShopDepth = 0.30f;         // fondo del marco: cierra el canto del hueco
    private const float ShopRecess = 0.16f;        // dónde queda la vidriera respecto del frente
    private const float ShopBulkhead = 0.66f;      // remate del zócalo bajo la vidriera
    private const float ShopGlassTop = 2.52f;      // dintel: de acá al techo de la celda
    private const float ShopCellTop = NeonFacade.Cell + KerbHeight;

    /// <summary>
    /// Los locales de planta baja: puerta propia, vidriera encendida, marco de metal, pilastras de
    /// ladrillo y cartel. Sin esto la planta baja era una fila de ventanas de departamento con un
    /// toldo colgado encima: el "negocio" no tenía puerta ni vidriera.
    ///
    /// Todo se arma con la MISMA convención de la fachada (celda de 3 m, +Z local hacia la calle),
    /// así el local encaja en las celdas que <see cref="NeonFacade.Build"/> dejó libres. El vidrio es
    /// opaco en este motor (no hay pase de transparencias): la vidriera se lee por su EMISIÓN cálida
    /// y por las siluetas de mercadería delante, no por lo que se ve detrás.
    /// </summary>
    private static void BuildShopfronts(
        StreetBuilder street, IReadOnlyList<NeonFacade.FacadeRow> rows, IReadOnlyList<NeonFacade.Shop> bays)
    {
        var rng = new Random(20261031);

        // Zócalo continuo al pie de TODAS las fachadas: la celda del kit arranca a 0,16 m (la vereda) y
        // el frente de la manzana no tiene cara a esa altura, así que sin este listón se veía una
        // ranura oscura al pie de cada edificio —el hueco por el que se miraba el interior vacío—.
        foreach (var row in rows)
        {
            float face = row.Side * FacadeOffset;
            AddSpan(street.FacadeBase, face - row.Side * 0.05f, face + row.Side * 0.30f,
                0f, KerbHeight, row.Z0, row.Z1, BrickRepeat);
        }

        foreach (var shop in bays)
        {
            var row = rows[shop.Row];
            float side = row.Side;
            float zA = side > 0f
                ? row.Z0 + shop.FirstColumn * NeonFacade.Cell
                : row.Z1 - shop.FirstColumn * NeonFacade.Cell;
            float zB = side > 0f ? zA + shop.Columns * NeonFacade.Cell : zA - shop.Columns * NeonFacade.Cell;
            float zMin = MathF.Min(zA, zB), zMax = MathF.Max(zA, zB);

            float face = side * FacadeOffset;
            float xOut = face - side * ShopProud;               // cara del local
            float xBack = face + side * ShopDepth;              // fondo del marco
            float xGlass = face + side * ShopRecess;            // vidriera
            float zLeft = zMin + 0.14f, zRight = zMax - 0.14f;

            // Pilastras: el medianil del local, de ladrillo, del frente al fondo del marco.
            AddSpan(street.ShopBrick, xOut, xBack, KerbHeight, ShopCellTop, zMin, zLeft, BrickRepeat);
            AddSpan(street.ShopBrick, xOut, xBack, KerbHeight, ShopCellTop, zRight, zMax, BrickRepeat);

            // Portal y garaje tienen su propia puerta, su propio dintel y no llevan cartel de local.
            if (shop.Kind == NeonFacade.GroundKind.Portal)
            {
                BuildPortal(street, side, xOut, xGlass, xBack, zMin, zMax);
                continue;
            }
            if (shop.Kind == NeonFacade.GroundKind.Garage)
            {
                BuildGarage(street, side, xOut, xBack, zMin, zMax);
                continue;
            }

            // Dintel de pared a pared: cierra el frente por encima de la vidriera y es el fondo del
            // cartel. Va en el MISMO ladrillo de la fachada: el marco oscuro de la vidriera termina
            // en el vidrio, y el cartel se monta sobre la franja de ladrillo, como en un local real.
            AddSpan(street.ShopBrick, xOut, xBack, ShopGlassTop, ShopCellTop, zLeft, zRight, BrickRepeat);

            // Divisiones entre celdas del mismo local: tapan el canto de la celda y dan el ritmo de
            // montantes de una vidriera de verdad.
            for (int cell = 1; cell < shop.Columns; cell++)
            {
                float z = zMin + cell * NeonFacade.Cell;
                AddSpan(street.ShopFrame, xOut, xBack, KerbHeight, ShopCellTop, z - 0.07f, z + 0.07f, FittingRepeat);
            }

            for (int cell = 0; cell < shop.Columns; cell++)
            {
                float czA = side > 0f
                    ? row.Z0 + (shop.FirstColumn + cell) * NeonFacade.Cell
                    : row.Z1 - (shop.FirstColumn + cell) * NeonFacade.Cell;
                float czB = side > 0f ? czA + NeonFacade.Cell : czA - NeonFacade.Cell;
                float czMin = MathF.Min(czA, czB), czMax = MathF.Max(czA, czB);

                if (cell == shop.DoorColumn)
                    BuildShopDoor(street, shop.Style, side, xOut, xGlass, xBack, czMin, czMax, rng);
                else
                    BuildShopWindow(street, shop.Style, side, xOut, xGlass, czMin, czMax, rng);
            }

            // Cartel del local: paneles de neón sobre el dintel, uno por cada 2,4 m de frente. Reusan
            // el mismo panel de 1,20 × 0,38 m de los carteles grandes, así el local pertenece a la
            // misma calle y no a un kit distinto.
            int color = rng.Next(SignColors.Length);
            var colorVector = SignColors[color];
            float yaw = side > 0f ? -MathF.PI * 0.5f : MathF.PI * 0.5f;
            int panels = Math.Max(1, (int)MathF.Round((zMax - zMin - 0.7f) / 2.4f));
            float span = zMax - zMin - 0.7f;
            float signX = side * (FacadeOffset - 0.10f);
            for (int panel = 0; panel < panels; panel++)
            {
                float z = zMin + 0.35f + span * (panel + 0.5f) / panels;
                var position = new Vector3(signX, 2.86f, z);
                var bodyTint = new Vector3(0.050f, 0.053f, 0.062f) + colorVector * 0.14f;
                street.SignBodies.Add(Tinted(Placed(position, yaw, 1f), bodyTint, roughness: 0.55f, metallic: 0.35f));
                street.Signs.Add(Tinted(Placed(position, yaw, 1f), colorVector, roughness: 0.35f, glow: SignGlow));
            }
        }
    }

    /// <summary>Una celda de VIDRIERA: zócalo, vidrio encendido, marco y mercadería en silueta.</summary>
    private static void BuildShopWindow(
        StreetBuilder street, int style, float side, float xOut, float xGlass,
        float zMin, float zMax, Random rng)
    {
        float z0 = zMin + 0.09f, z1 = zMax - 0.09f;

        // Zócalo: del frente a la vidriera, cierra el hueco por abajo y es donde se apoya la mercadería.
        AddSpan(street.ShopWood, xOut, xGlass, KerbHeight, ShopBulkhead, z0, z1, FittingRepeat);

        // Vidrio encendido. Dos temperaturas por estilo: la mayoría cálida, los locales pares fríos.
        var glass = (style & 1) == 0 ? street.ShopGlassWarm : street.ShopGlassCool;
        AddSpan(glass, xGlass, xGlass + side * 0.02f, ShopBulkhead, ShopGlassTop, z0, z1);

        // Marco: travesaño inferior y superior, jambas y dos montantes.
        AddSpan(street.ShopFrame, xGlass - side * 0.04f, xGlass + side * 0.04f,
            ShopBulkhead, ShopBulkhead + 0.07f, z0, z1, FittingRepeat);
        AddSpan(street.ShopFrame, xGlass - side * 0.04f, xGlass + side * 0.04f,
            ShopGlassTop - 0.07f, ShopGlassTop, z0, z1, FittingRepeat);
        foreach (float z in new[] { z0, z1 - 0.07f })
        {
            AddSpan(street.ShopFrame, xGlass - side * 0.04f, xGlass + side * 0.04f,
                ShopBulkhead, ShopGlassTop, z, z + 0.07f, FittingRepeat);
        }
        for (int mullion = 1; mullion <= 2; mullion++)
        {
            float z = z0 + (z1 - z0) * mullion / 3f;
            AddSpan(street.ShopFrame, xGlass - side * 0.05f, xGlass + side * 0.02f,
                ShopBulkhead + 0.07f, ShopGlassTop - 0.07f, z - 0.025f, z + 0.025f, FittingRepeat);
        }

        // Estantes: dos líneas horizontales oscuras por delante del vidrio. Sin ellas la vidriera es
        // un rectángulo de color plano; con ellas se lee como un local con mercadería adentro.
        foreach (float shelf in new[] { 1.38f, 1.96f })
        {
            AddSpan(street.ShopFrame, xGlass - side * 0.03f, xGlass + side * 0.02f,
                shelf, shelf + 0.035f, z0, z1, FittingRepeat);
        }

        // Bultos en silueta sobre el zócalo, DELANTE del vidrio. Van en el marco casi negro (no en el
        // metal claro del mobiliario) y bajos: contra la vidriera encendida se leen como mercadería,
        // no como cajas claras tapando el local.
        int items = 1 + rng.Next(2);
        float usable = z1 - z0 - 0.30f;
        for (int item = 0; item < items; item++)
        {
            float width = 0.14f + (float)rng.NextDouble() * 0.16f;
            float height = 0.12f + (float)rng.NextDouble() * 0.22f;
            float z = z0 + 0.15f + usable * (item + 0.5f) / items;
            float depth = 0.08f + (float)rng.NextDouble() * 0.08f;
            AddSpan(street.ShopFrame, xGlass - side * depth, xGlass - side * 0.03f,
                ShopBulkhead, ShopBulkhead + height, z - width * 0.5f, z + width * 0.5f, FittingRepeat);
        }
    }

    /// <summary>La celda de la PUERTA: hoja de madera con vidrio, marco, umbral y picaporte.</summary>
    private static void BuildShopDoor(
        StreetBuilder street, int style, float side, float xOut, float xGlass, float xBack,
        float zMin, float zMax, Random rng)
    {
        float zCenter = (zMin + zMax) * 0.5f;
        float zDoorA = zCenter - 0.55f, zDoorB = zCenter + 0.55f;
        float z0 = zMin + 0.09f, z1 = zMax - 0.09f;

        // Zócalos a los lados de la puerta, y vidrieras angostas arriba de cada uno.
        foreach (var (a, b) in new[] { (z0, zDoorA - 0.07f), (zDoorB + 0.07f, z1) })
        {
            if (b - a < 0.10f) continue;
            AddSpan(street.ShopWood, xOut, xGlass, KerbHeight, ShopBulkhead, a, b, FittingRepeat);
            var glass = (style & 1) == 0 ? street.ShopGlassWarm : street.ShopGlassCool;
            AddSpan(glass, xGlass, xGlass + side * 0.02f, ShopBulkhead, ShopGlassTop, a, b);
            AddSpan(street.ShopFrame, xGlass - side * 0.04f, xGlass + side * 0.04f,
                ShopBulkhead, ShopBulkhead + 0.07f, a, b, FittingRepeat);
            AddSpan(street.ShopFrame, xGlass - side * 0.04f, xGlass + side * 0.04f,
                ShopGlassTop - 0.07f, ShopGlassTop, a, b, FittingRepeat);
        }

        // Marco de la puerta: dos jambas y el dintel, del piso al dintel del local.
        AddSpan(street.ShopFrame, xOut, xBack, KerbHeight, ShopGlassTop, zDoorA - 0.09f, zDoorA, FittingRepeat);
        AddSpan(street.ShopFrame, xOut, xBack, KerbHeight, ShopGlassTop, zDoorB, zDoorB + 0.09f, FittingRepeat);
        AddSpan(street.ShopFrame, xOut, xBack, ShopGlassTop - 0.07f, ShopGlassTop,
            zDoorA - 0.09f, zDoorB + 0.09f, FittingRepeat);

        // Hoja: madera oscura con un paño de vidrio en la mitad de arriba. El vidrio de la puerta va
        // del mismo lado que el de la vidriera para que la fachada se lea de una sola pieza.
        AddSpan(street.ShopWood, xGlass - side * 0.03f, xGlass + side * 0.05f,
            KerbHeight, ShopGlassTop - 0.10f, zDoorA + 0.04f, zDoorB - 0.04f, FittingRepeat);
        var doorGlass = (style & 1) == 0 ? street.ShopGlassWarm : street.ShopGlassCool;
        AddSpan(doorGlass, xGlass - side * 0.06f, xGlass - side * 0.02f,
            1.18f, 2.32f, zDoorA + 0.14f, zDoorB - 0.14f);

        // Patada de la puerta y picaporte.
        AddSpan(street.ShopFrame, xGlass - side * 0.065f, xGlass + side * 0.02f,
            KerbHeight, KerbHeight + 0.34f, zDoorA + 0.08f, zDoorB - 0.08f, FittingRepeat);
        AddSpan(street.ShopFrame, xGlass - side * 0.13f, xGlass - side * 0.03f,
            1.04f, 1.12f, zDoorB - 0.20f, zDoorB - 0.12f, FittingRepeat);

        // Umbral de piedra, un escalón por encima de la vereda.
        AddSpan(street.Walk, xOut - side * 0.14f, xGlass, KerbHeight, KerbHeight + 0.05f,
            zDoorA - 0.10f, zDoorB + 0.10f, ConcreteRepeat);
    }

    /// <summary>
    /// PORTAL de viviendas: la entrada normal de un edificio, entre los locales. Una puerta de madera
    /// con paño de vidrio, jambas y dintel de ladrillo, dos escalones, un pequeño refugio con luz y
    /// buzón. Es deliberadamente MÁS BAJO y MÁS ANGOSTO que una vidriera —y de una sola hoja, no de
    /// dos—: se lee como acceso desde lejos, no como otro negocio.
    /// </summary>
    private static void BuildPortal(
        StreetBuilder street, float side, float xOut, float xGlass, float xBack, float zMin, float zMax)
    {
        float zCenter = (zMin + zMax) * 0.5f;
        float zDoorA = zCenter - 0.58f, zDoorB = zCenter + 0.58f;
        float z0 = zMin + 0.14f, z1 = zMax - 0.14f;
        float xDoor = xGlass + side * 0.10f;

        // Dintel de ladrillo sobre el portal (lo común con el local son las pilastras laterales).
        AddSpan(street.ShopBrick, xOut, xBack, 2.80f, ShopCellTop, z0, z1, BrickRepeat);

        // Jambas y dintel de ladrillo alrededor del hueco: el mismo ladrillo del edificio le da el
        // marco de obra al portal y evita que un material claro devuelva el cielo y se vea verde.
        AddSpan(street.ShopBrick, xOut, xDoor + side * 0.02f, KerbHeight, 2.62f, zDoorA - 0.17f, zDoorA, BrickRepeat);
        AddSpan(street.ShopBrick, xOut, xDoor + side * 0.02f, KerbHeight, 2.62f, zDoorB, zDoorB + 0.17f, BrickRepeat);
        AddSpan(street.ShopBrick, xOut, xDoor + side * 0.02f, 2.62f, 2.80f, zDoorA - 0.17f, zDoorB + 0.17f, BrickRepeat);

        // La puerta va 0.26 m adentro: el escalón y la profundidad son lo que la distinguen de una
        // vidriera pegada al plano de la calle.
        AddSpan(street.ShopFrame, xDoor - side * 0.02f, xDoor + side * 0.06f, KerbHeight, 2.40f,
            zDoorA - 0.05f, zDoorA, FittingRepeat);
        AddSpan(street.ShopFrame, xDoor - side * 0.02f, xDoor + side * 0.06f, KerbHeight, 2.40f,
            zDoorB, zDoorB + 0.05f, FittingRepeat);
        AddSpan(street.ShopFrame, xDoor - side * 0.02f, xDoor + side * 0.06f, 2.34f, 2.40f,
            zDoorA, zDoorB, FittingRepeat);

        // Una sola hoja de madera con paño de vidrio arriba y picaporte: es la lectura de "entrada
        // de edificio" que la doble puerta ancha no daba (parecía otro local).
        float leafA = zDoorA + 0.04f, leafB = zDoorB - 0.04f;
        AddSpan(street.ShopWood, xDoor, xDoor + side * 0.05f, KerbHeight, 2.34f, leafA, leafB, FittingRepeat);
        AddSpan(street.ShopGlassWarm, xDoor - side * 0.02f, xDoor, 1.16f, 2.16f, leafA + 0.12f, leafB - 0.12f);
        AddSpan(street.ShopFrame, xDoor - side * 0.05f, xDoor - side * 0.01f, 1.02f, 1.10f,
            leafB - 0.22f, leafB - 0.12f, FittingRepeat);
        // Travesaño bajo el vidrio, para que la hoja no sea un panel liso.
        AddSpan(street.ShopFrame, xDoor - side * 0.03f, xDoor + side * 0.05f, 1.10f, 1.16f,
            leafA, leafB, FittingRepeat);

        // Vidrio de abanico sobre la puerta, debajo del dintel de ladrillo.
        AddSpan(street.ShopGlassWarm, xDoor, xDoor + side * 0.02f, 2.42f, 2.62f, zDoorA + 0.08f, zDoorB - 0.08f);

        // Escalones: dos, anchos, con el umbral por encima de la vereda.
        AddSpan(street.Walk, xOut - side * 0.55f, xDoor, KerbHeight, 0.19f, zDoorA - 0.22f, zDoorB + 0.22f, ConcreteRepeat);
        AddSpan(street.Walk, xOut - side * 0.30f, xDoor, 0.19f, 0.23f, zDoorA - 0.14f, zDoorB + 0.14f, ConcreteRepeat);

        // Refugio: losa sobre la puerta, dos tirantes y una luz cálida abajo. Es lo que hace que el
        // portal se lea desde el otro lado de la calle.
        AddSpan(street.ShopFrame, xOut - side * 0.62f, xOut, 2.80f, 2.86f, zDoorA - 0.28f, zDoorB + 0.28f, FittingRepeat);
        foreach (float z in new[] { zDoorA - 0.24f, zDoorB + 0.24f })
        {
            AddTube(street.Furniture, new Vector3(xOut - side * 0.58f, 2.86f, z),
                new Vector3(xOut, 3.14f, z), 0.022f);
        }
        AddSpan(street.ShopFrame, xOut - side * 0.36f, xOut - side * 0.12f, 2.76f, 2.80f,
            zCenter - 0.22f, zCenter + 0.22f, FittingRepeat);
        AddSpan(street.ShopGlassWarm, xOut - side * 0.36f, xOut - side * 0.12f, 2.73f, 2.76f,
            zCenter - 0.22f, zCenter + 0.22f);

        // Buzón y placa junto a la jamba.
        AddSpan(street.ShopFrame, xOut - side * 0.06f, xOut + side * 0.02f, 1.30f, 1.62f,
            zDoorB + 0.20f, zDoorB + 0.52f, FittingRepeat);
        AddSpan(street.ShopFrame, xOut - side * 0.05f, xOut + side * 0.01f, 1.38f, 1.46f,
            zDoorB + 0.26f, zDoorB + 0.46f, FittingRepeat);
    }

    /// <summary>
    /// GARAJE: persiana metálica de duelas, guías, dintel de ladrillo y un cartel sobre la boca. La
    /// persiana cerrada es la lectura más distinta de una vidriera, que es lo que la calle necesitaba
    /// además del portal: no todo local está abierto a la noche.
    /// </summary>
    private static void BuildGarage(
        StreetBuilder street, float side, float xOut, float xBack, float zMin, float zMax)
    {
        float z0 = zMin + 0.14f, z1 = zMax - 0.14f;
        float zCenter = (zMin + zMax) * 0.5f;

        // Dintel de ladrillo sobre la boca de 2,50 m.
        AddSpan(street.ShopBrick, xOut, xBack, 2.50f, ShopCellTop, z0, z1, BrickRepeat);

        // Guías laterales.
        AddSpan(street.ShopFrame, xOut, xBack, KerbHeight, 2.50f, z0, z0 + 0.08f, FittingRepeat);
        AddSpan(street.ShopFrame, xOut, xBack, KerbHeight, 2.50f, z1 - 0.08f, z1, FittingRepeat);

        // Duelas de la persiana: doce fajas horizontales con una junta oscura entre ellas.
        const int slats = 12;
        const float bottom = 0.22f, top = 2.42f;
        float pitch = (top - bottom) / slats;
        for (int slat = 0; slat < slats; slat++)
        {
            float y = bottom + slat * pitch;
            AddSpan(street.ShopFrame, xOut - side * 0.02f, xOut + side * 0.02f,
                y, y + pitch - 0.025f, z0 + 0.10f, z1 - 0.10f, FittingRepeat);
        }
        // Remate superior: cierra la luz entre la última duela y el dintel de ladrillo.
        AddSpan(street.ShopFrame, xOut - side * 0.02f, xOut + side * 0.03f, top, 2.50f,
            z0 + 0.08f, z1 - 0.08f, FittingRepeat);

        // Remate inferior, umbral y una franja de hormigón que hace de rampa sobre la vereda.
        AddSpan(street.ShopFrame, xOut - side * 0.04f, xOut + side * 0.02f, bottom - 0.06f, bottom,
            z0 + 0.09f, z1 - 0.09f, FittingRepeat);
        AddSpan(street.Walk, xOut - side * 0.55f, xOut, KerbHeight, KerbHeight + 0.02f,
            z0 + 0.08f, z1 - 0.08f, ConcreteRepeat);

        // Cartel de garaje, un solo panel ámbar sobre la boca, y una luz de aviso junto a la guía.
        var amber = SignColors[2];
        float yaw = side > 0f ? -MathF.PI * 0.5f : MathF.PI * 0.5f;
        var position = new Vector3(side * (FacadeOffset - 0.10f), 2.78f, zCenter);
        street.SignBodies.Add(Tinted(Placed(position, yaw, 1f), new Vector3(0.05f, 0.053f, 0.062f) + amber * 0.14f,
            roughness: 0.55f, metallic: 0.35f));
        street.Signs.Add(Tinted(Placed(position, yaw, 1f), amber, roughness: 0.35f, glow: SignGlow));
        AddSpan(street.ShopGlassWarm, xOut - side * 0.06f, xOut + side * 0.01f, 2.56f, 2.66f,
            z0 + 0.12f, z0 + 0.24f, FittingRepeat);
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
        List<SceneMesh> meshes, string root, string slug, string label, IReadOnlyList<SceneInstance> instances,
        bool groundOnRoad = false)
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
            // El glTF ya tiene horneadas las transformaciones de sus nodos. Ajustar cada
            // instancia por su mínimo real y su escala, no por una altura supuesta del eje.
            var placements = groundOnRoad
                ? instances.Select(instance => instance with
                {
                    PositionScale = instance.PositionScale with
                    {
                        Y = instance.PositionScale.Y - model.Min.Y * instance.PositionScale.W
                    }
                }).ToArray()
                : instances.ToArray();
            foreach (var part in model.Parts)
            {
                meshes.Add(new SceneMesh
                {
                    Name = $"{label} · {part.Name}",
                    Vertices = part.Vertices,
                    Instances = placements,
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

    private static IReadOnlyList<SceneInstance> CarPlacements() => new[]
    {
        // El auto héroe: estacionado contra la vereda derecha, apenas abierto hacia la calle. Está a
        // la DERECHA del recorrido del tramo 1 a propósito: la cámara pasa por el carril izquierdo y
        // el auto entra en cuadro de costado, sin que el travelling se le meta adentro (ver CameraAt).
        Placed(new Vector3(3.2f, 0f, HeroCarZ), 0.30f, 1f),
        Placed(new Vector3(-3.1f, 0f, HeroCarZ + 24f), -0.22f, 0.96f),
        Placed(new Vector3(3.3f, 0f, HeroCarZ + 46f), 0.16f, 1.02f),
        Placed(new Vector3(-3.2f, 0f, HeroCarZ + 71f), -0.30f, 0.94f)
    };

    /// <summary>
    /// Escaleras de incendio del kit, montadas sobre cuatro frentes del recorrido. Se eligen filas de
    /// cuatro pisos o más para que la escalera (9,2 m con el escalado) no asome por encima del
    /// antepecho, y posiciones Z lejos de los carteles, los faroles de pared y los cables: una
    /// escalera atravesando un cartel de neón se lee como un error de armado, no como una calle.
    /// </summary>
    private static IReadOnlyList<SceneInstance> FireEscapePlacements(IReadOnlyList<NeonFacade.FacadeRow> rows)
    {
        // Posiciones elegidas contra el plano real de la cuadra (ver los centros de fila en
        // LayoutBuildings): lejos de los carteles, los faroles de pared y los cables que cruzan.
        var targets = new (float Side, float Z)[] { (-1f, 6f), (1f, 33.5f), (-1f, 47f), (1f, 67.5f) };
        var placements = new List<SceneInstance>(targets.Length);
        const float scale = 0.94f;

        foreach (var target in targets)
        {
            var row = rows
                .Where(r => r.Side == target.Side && r.Floors >= 4 &&
                            target.Z > r.Z0 + 2.3f && target.Z < r.Z1 - 2.3f)
                .OrderBy(r => MathF.Abs((r.Z0 + r.Z1) * 0.5f - target.Z))
                .FirstOrDefault();
            if (row.Floors < 4) continue;

            float yaw = row.Side > 0f ? -MathF.PI * 0.5f : MathF.PI * 0.5f;
            // El fondo de la escalera (z local −0,78) queda apenas dentro del plano del frente; el
            // resto vuela sobre la vereda, como una escalera de verdad.
            float x = row.Side * (FacadeOffset - 0.78f * scale);
            placements.Add(Placed(new Vector3(x, 2.35f, target.Z), yaw, scale));
        }
        return placements;
    }

    /// <summary>Cajas de cartón: un envío recién bajado y unas cajas sueltas contra el cordón.</summary>
    private static IReadOnlyList<SceneInstance> CardboardPlacements() => new[]
    {
        Placed(new Vector3(6.15f, KerbHeight, 6.30f), 0.25f, 1.05f),
        Placed(new Vector3(6.38f, KerbHeight, 6.62f), -0.55f, 0.92f),
        Placed(new Vector3(6.20f, KerbHeight + 0.31f, 6.44f), 0.85f, 0.78f),
        Placed(new Vector3(-6.45f, KerbHeight, 24.10f), 1.35f, 1.00f),
        Placed(new Vector3(-6.20f, KerbHeight, 37.55f), 0.35f, 0.88f),
        Placed(new Vector3(6.55f, KerbHeight, 56.05f), -0.70f, 1.10f)
    };

    /// <summary>Neumáticos viejos de canto contra la pared, en los huecos entre dos frentes.</summary>
    private static IReadOnlyList<SceneInstance> TyrePlacements() => new[]
    {
        Placed(new Vector3(8.12f, KerbHeight, 31.45f), MathF.PI * 0.5f, 1.0f),
        Placed(new Vector3(-8.10f, KerbHeight, 60.35f), -MathF.PI * 0.5f, 0.95f)
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
            new Vector3(1.0f, 0.82f, 0.62f), 17f, 23f));
        lights.Add(new ScenePointLight(new Vector3(-(RoadHalfWidth + PostLampInset), PostLampLanternHeight, 28f),
            new Vector3(1.0f, 0.82f, 0.62f), 16f, 23f));
        lights.Add(new ScenePointLight(new Vector3(RoadHalfWidth + PostLampInset, PostLampLanternHeight, 51f),
            new Vector3(1.0f, 0.82f, 0.62f), 15f, 23f));

        // Los carteles tiran su color sobre el frente: son la mitad de la identidad de la calle.
        for (int i = 0; i < SignLayout.Length && lights.Count < SceneDefinition.MaxPointLights; i++)
        {
            var sign = SignLayout[i];
            if (sign.Z > HeroCarZ + 30f) continue;      // los del fondo no entran en el presupuesto
            lights.Add(new ScenePointLight(
                new Vector3(sign.Side * (FacadeOffset - 0.85f), sign.Y, sign.Z),
                SignColors[sign.Color], 11f - i * 0.9f, 14f));
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
    private const float SignGlow = 0.68f;

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

    /// <summary>Igual que <see cref="AddSlab"/> pero por CARAS: evita armar el mínimo/máximo a mano en
    /// los locales, donde el eje X cambia de signo según la vereda y es fácil equivocarse.</summary>
    private static void AddSpan(
        List<SceneVertex> vertices, float xA, float xB, float y0, float y1, float z0, float z1,
        float uvPerMeter = 0f) =>
        AddSlab(vertices,
            new Vector3(MathF.Min(xA, xB), y0, MathF.Min(z0, z1)),
            new Vector3(MathF.Max(xA, xB), y1, MathF.Max(z0, z1)), uvPerMeter);

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
