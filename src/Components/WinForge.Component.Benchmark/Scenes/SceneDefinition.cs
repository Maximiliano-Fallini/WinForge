using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>Vértice de la malla: posición y normal (normales por cara, look facetado).</summary>
/// <summary>
/// Vértice del motor: posición, normal y UV. La UV la usan los materiales con texturas (las mallas
/// de los modelos glTF, el piso y las paredes de los dioramas); las mallas procedurales viejas la
/// dejan en cero y se texturan con el atlas de detalle por posición local (ver
/// <c>SceneDetailTexture</c>), así que no dependen de esta coordenada.
/// </summary>
public readonly record struct SceneVertex(Vector3 Position, Vector3 Normal, Vector2 Uv = default);

/// <summary>
/// Instancia. <see cref="PositionScale"/> lleva posición (xyz) y escala (w). En el corredor
/// infinito x e y son FIJOS (x = banda del corredor, y = altura sobre el terreno) y z es la
/// posición a lo largo del recorrido, que el vertex shader ENVUELVE alrededor de la cámara: por
/// eso el mundo no se termina nunca. En el cañón la posición es un DESPLAZAMIENTO respecto del
/// recorrido (el shader la vuelve mundo con la función del camino, que depende del tiempo).
///
/// <see cref="Orientation"/> lleva el giro inicial (x) y la velocidad de giro (y). Sus dos
/// componentes libres se usan como CONTRATO con los shaders:
/// <list type="bullet">
/// <item><c>z</c> = TIPO de malla (ver <see cref="SceneDefinition.KindCorridorRock"/> y
/// compañía). Los shaders son un solo programa con una rama por tipo: así una escena nueva no
/// obliga a pipeline nuevo en ninguna API (que es la parte que se paga cuatro veces).</item>
/// <item><c>w</c> = parámetro libre del tipo (en los cazas, la variante de pintura).</item>
/// </list>
/// Los dos vectores de 16 bytes alinean con el input layout.
/// </summary>
public readonly record struct SceneInstance(
    Vector4 PositionScale,
    Vector4 Orientation,
    Vector4 MaterialTint = default,
    Vector4 MaterialParams = default)
{
    /// <summary>
    /// Los parámetros del MATERIAL viajan por instancia (y no por malla) a propósito: el motor tiene
    /// un solo pipeline por rol en las cuatro APIs, así que una malla puede compartirse entre objetos
    /// con distinto tinte, escala de UV o fuerza de emisión sin tocar un constante por draw. Los dos
    /// vectores son <c>tint.rgb + uvScale</c> y <c>normalStrength, roughnessScale, metallicScale,
    /// emissiveStrength</c>: exactamente lo que necesita el camino texturado (ver el HLSL).
    /// </summary>
    public static SceneInstance WithMaterial(SceneInstance instance, in SceneMaterial material, float emissiveScale = 1f) =>
        instance with
        {
            MaterialTint = new Vector4(material.Tint, material.UvScale),
            MaterialParams = new Vector4(
                material.NormalStrength,
                material.RoughnessScale,
                material.MetallicScale,
                material.EmissiveStrength * emissiveScale)
        };
}

/// <summary>
/// Una luz puntual de la escena: posición, color, intensidad y radio de alcance. Es lo que hace que
/// una calle nocturna se lea iluminada por SUS fuentes (los faroles y los carteles) y no por un sol
/// compartido con las otras escenas.
///
/// <see cref="Radius"/> no es un corte duro: el shader atenúa con una caída suave hasta el radio,
/// así una luz no deja un borde visible donde se apaga.
/// </summary>
public readonly record struct ScenePointLight(Vector3 Position, Vector3 Color, float Intensity, float Radius);

/// <summary>
/// El volumen de una escena que PROYECTA sombra sobre sí misma (ver <c>README-SOMBRAS.md</c>): el
/// AABB del diorama, en metros, y las perillas de la pasada.
///
/// Por qué un volumen y no "toda la escena": el shadow map es de UNA cascada y con resolución FIJA
/// (2048²), así que la resolución por metro la fija el tamaño del volumen. Un diorama de 60 m sale con
/// 34 téxeles por metro y se ve nítido; el corredor de 420 m tendría 5 y se vería como una mancha. Ese
/// es exactamente el motivo por el que el cañón y el industrial (dioramas) lo van a poder usar y el
/// corredor viejo no.
/// </summary>
/// <param name="Center">Centro del volumen en el mundo.</param>
/// <param name="Extent">Semiextensión (la mitad del tamaño) por eje.</param>
/// <param name="DepthBias">Sesgo en espacio de luz: es lo que evita el "acné" de sombra (una
/// superficie sombreándose a sí misma por el error de cuantización de la profundidad).</param>
/// <param name="Penumbra">Ancho del PCF en téxeles: 1 = sombra dura, más = borde más suave.</param>
public readonly record struct SceneShadowVolume(
    Vector3 Center,
    Vector3 Extent,
    float DepthBias = 0.0016f,
    float Penumbra = 1.6f)
{
    // El tamaño de un téxel del mapa NO vive acá: la resolución del mapa es una perilla por corrida
    // (ver SceneGraphicsOptions.ShadowMapSize) y el PCF tiene que usar el tamaño EFECTIVO, o el borde
    // de la sombra sale con el paso de otra resolución. Lo calcula SceneGraphicsOptions.TexelSize.
}

/// <summary>
/// Una malla de la escena con sus instancias. Una escena tiene VARIAS (terreno, cazas): cada una
/// se sube a su propio vertex buffer y se dibuja con su propio <c>DrawInstanced</c>.
/// </summary>
public sealed class SceneMesh
{
    /// <summary>Nombre para diagnósticos (no se muestra en la UI).</summary>
    public required string Name { get; init; }

    public required SceneVertex[] Vertices { get; init; }

    public required SceneInstance[] Instances { get; init; }

    /// <summary>
    /// Malla de PARTÍCULAS: se dibuja al final, con mezcla y sin escribir profundidad. Es una
    /// propiedad de la malla y no del shader porque el estado lo fija cada API antes de dibujarla
    /// (y el orden importa: lo transparente va después de todo lo opaco).
    /// </summary>
    public bool Blend { get; init; }

    /// <summary>
    /// Material PBR con texturas reales (ver <see cref="SceneMaterial"/>). Cuando está, la malla se
    /// dibuja por el camino de MATERIAL y el shader usa su albedo, su mapa de normales y su ARM: es
    /// lo que hace que un modelo traído de afuera se vea como el objeto real y no como una silueta
    /// de color. Cuando es <c>null</c> la malla usa el camino procedural de siempre (una rama por
    /// tipo de malla, con el atlas de detalle y los colores del shader).
    /// </summary>
    public SceneMaterial? Material { get; init; }

    /// <summary>
    /// Ranura del material en el <see cref="MaterialAtlas"/> de la escena (-1 = sin material). La
    /// asigna <see cref="MaterialAtlas.Build"/> la primera vez que alguien pide el atlas: es el índice
    /// que viaja por instancia para que el shader sepa qué rebanadas muestrear.
    /// </summary>
    public int MaterialSlot { get; internal set; } = -1;

    private SceneInstance[]? _renderInstances;

    /// <summary>
    /// Las instancias tal como se suben a la GPU: a cada una se le dejan puestos los parámetros del
    /// material de la malla (tinta, escala de UV, fuerzas y la RANURA del atlas) en los dos vectores
    /// de material, salvo que la instancia ya traiga los suyos (un cartel de neón con su color).
    ///
    /// Se hace acá y no en cada backend para que los cuatro suban exactamente lo mismo: el formato de
    /// instancia (4 × <c>float4</c>) no cambia, así que el camino de subida tampoco.
    /// </summary>
    public SceneInstance[] RenderInstances
    {
        get
        {
            if (_renderInstances != null) return _renderInstances;
            _renderInstances = Bake(Instances, Material, MaterialSlot);
            return _renderInstances;
        }
    }

    private static SceneInstance[] Bake(SceneInstance[] instances, SceneMaterial? material, int slot)
    {
        if (material == null) return instances;

        // Primera rebanada del material en el atlas: el shader suma 0, 1 o 2 para albedo, normales
        // y ARM (ver MaterialAtlas.MapsPerSlot).
        float slice = (slot >= 0 ? slot : MaterialAtlas.NeutralSlot) * MaterialAtlas.MapsPerSlot;
        var tint = new Vector4(material.Tint, material.UvScale);
        var parameters = new Vector4(
            material.NormalStrength, material.RoughnessScale, material.MetallicScale, material.EmissiveStrength);

        var baked = new SceneInstance[instances.Length];
        for (int i = 0; i < baked.Length; i++)
        {
            var instance = instances[i];
            // Los dos vectores de material son OPCIONALES por instancia: el que los deja en cero usa
            // los del material de la malla (ver SceneInstance.WithMaterial). El índice del tipo se
            // reescribe con la ranura del atlas porque es lo que el camino texturado espera en w.
            var orientation = instance.Orientation;
            baked[i] = instance with
            {
                Orientation = new Vector4(orientation.X, orientation.Y, SceneDefinition.KindTextured, slice),
                MaterialTint = instance.MaterialTint == default ? tint : instance.MaterialTint,
                MaterialParams = instance.MaterialParams == default ? parameters : instance.MaterialParams
            };
        }
        return baked;
    }
}

/// <summary>
/// Una escena descrita como DATOS: mallas, instancias, luz y recorrido de cámara. Deliberadamente
/// no sabe nada de Direct3D ni de Vulkan — así una API nueva no obliga a redefinir la escena,
/// y el mismo mundo se puede renderizar con cualquier backend y comparar de igual a igual.
///
/// Todo lo que cambia con el tiempo es función del TIEMPO (no del frame): dos corridas con la
/// misma duración recorren exactamente el mismo mundo. La cámara AVANZA (como en 3DMark): no es
/// una órbita sobre un campo estático.
/// </summary>
public sealed class SceneDefinition
{
    public required string Id { get; init; }

    /// <summary>Nombre visible, texto fuente en español (se traduce con el motor de la app).</summary>
    public required string Name { get; init; }

    /// <summary>Qué mide la escena, texto fuente en español.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// La escena se puede CORRER (tiene contenido real) o está EN DESARROLLO (aparece en el selector
    /// con el rótulo, pero no se puede correr). Las escenas viejas (corredor, Aero) quedan visibles
    /// como "en desarrollo" hasta que se reemplacen por sus dioramas cinematográficos.
    /// </summary>
    public bool InDevelopment { get; init; }

    /// <summary>Mallas de la escena. La primera es la principal (la que reportan los informes).</summary>
    public required IReadOnlyList<SceneMesh> Meshes { get; init; }

    /// <summary>
    /// Dirección de la luz (hacia dónde VIAJA la luz, normalizada). Cada escena elige su sol: el
    /// corredor lo tiene alto (luz de día, sin sombras dramáticas) y el cañón lo tiene naciente y
    /// de frente sobre el recorrido, que es lo que hace que la escena se vuele hacia el sol.
    /// </summary>
    public Vector3 LightDirection { get; init; } = Vector3.Normalize(new Vector3(-0.45f, -0.8f, 0.4f));

    /// <summary>
    /// Luces puntuales de la escena (faroles, carteles de neón, lámparas del interior industrial).
    /// Son DATO y no una pasada aparte: viajan en el bloque por frame, así que las cuatro APIs
    /// iluminan igual y una escena de interiores no necesita un pipeline nuevo. Hasta
    /// <see cref="MaxPointLights"/>.
    /// </summary>
    public IReadOnlyList<ScenePointLight> PointLights { get; init; } = Array.Empty<ScenePointLight>();

    /// <summary>
    /// Set de assets que necesita la escena, o vacío si se arma sin modelos ni texturas (ver README
    /// cinematográfico §7: la DLL NO lleva assets, cada escena declara su set).
    ///
    /// Lo usa el arranque de la corrida: si la carpeta del set no está en disco, la baja ANTES de
    /// preparar la escena (ver <c>BenchmarkAssetPacks</c>) en vez de tumbar la corrida con "no
    /// encontré el modelo" cuando el usuario recién instaló el componente.
    /// </summary>
    public string AssetSet { get; init; } = "";

    /// <summary>
    /// Recorrido de la cámara: devuelve (ojo, objetivo) para un instante. Función pura del tiempo,
    /// así la corrida es repetible.
    /// </summary>
    public required Func<double, (Vector3 Eye, Vector3 Target)> CameraPath { get; init; }

    /// <summary>
    /// Plano lejano de la cámara, en unidades de mundo. Es de la ESCENA y no una constante del
    /// backend a propósito: el corredor mide 420 unidades de largo y con 400 de plano lejano el
    /// rango de profundidad queda apretado (más precisión), mientras que el cañón necesita ver
    /// kilómetros de terreno y de nubes. Un plano lejano corto no "recorta mal": borra el mundo
    /// lejano entero, y con él las paredes del valle y el horizonte.
    /// </summary>
    public float FarPlane { get; init; } = 400f;

    /// <summary>Presupuesto de frame con el que se cuenta "frames fuera de presupuesto".</summary>
    public double FrameBudgetMs { get; init; } = 1000.0 / 60.0;

    /// <summary>Segundos que se corren antes de empezar a medir (compilado de shaders, caches de driver).</summary>
    public double WarmupSeconds { get; init; } = 2.0;

    /// <summary>Duración medida por defecto de la corrida, en segundos.</summary>
    public int DefaultDurationSeconds { get; init; } = 30;

    /// <summary>Malla principal (compatibilidad: los informes y el probador hablan de "la" malla).</summary>
    public SceneVertex[] Vertices => Meshes[0].Vertices;

    /// <summary>Instancias de la malla principal.</summary>
    public SceneInstance[] Instances => Meshes[0].Instances;

    private MaterialAtlas? _materials;

    /// <summary>
    /// Texturas de material de la escena empaquetadas en un solo arreglo (ver <see cref="MaterialAtlas"/>).
    /// Se arma la primera vez que un backend PREPARA la escena: decodifica las imágenes, las
    /// redimensiona y genera los mips, así que el selector de escenas no lo paga. Las mallas quedan
    /// con su ranura asignada (<see cref="SceneMesh.MaterialSlot"/>).
    /// </summary>
    public MaterialAtlas Materials => _materials ??= MaterialAtlas.Build(this);

    /// <summary>Vértices totales de la escena (suma de sus mallas).</summary>
    public long VertexCount => Meshes.Sum(m => (long)m.Vertices.Length);

    /// <summary>Instancias totales de la escena (suma de sus mallas).</summary>
    public long InstanceCount => Meshes.Sum(m => (long)m.Instances.Length);

    /// <summary>Cámara en el instante indicado (ver <see cref="CameraPath"/>).</summary>
    public (Vector3 Eye, Vector3 Target) CameraAt(double seconds) => CameraPath(seconds);

    // =====================================================================
    // Tipos de malla (el contrato con los shaders)
    // =====================================================================

    /// <summary>Rocas facetadas del corredor: se envuelven alrededor de la cámara.</summary>
    public const float KindCorridorRock = 0f;

    // ---- Frutiger Aero (mundos 3 a 12): modelos facetados del mundo "de postal" ----

    /// <summary>Pasto del mundo Aero: la MISMA grilla del valle, con el campo de altura de las lomas.</summary>
    public const float KindAeroGround = 3f;

    /// <summary>Burbuja de jabón: esfera con Fresnel e iridiscencia, flotando a la deriva.</summary>
    public const float KindBubble = 4f;

    /// <summary>Torre de vidrio y cromo de la ciudad del horizonte.</summary>
    public const float KindTower = 5f;

    /// <summary>Árbol: tronco y copa frondosa (la línea de árboles del fondo y el árbol del primer plano).</summary>
    public const float KindTree = 6f;

    /// <summary>Girasol: tallo, disco central y pétalos.</summary>
    public const float KindSunflower = 7f;

    /// <summary>Rosa: tallo y capullo.</summary>
    public const float KindRose = 8f;

    /// <summary>Gaviota: dos alas en V.</summary>
    public const float KindGull = 9f;

    /// <summary>Globo aerostático: envelope a franjas y canasta.</summary>
    public const float KindBalloon = 10f;

    /// <summary>Mariposa: dos alas que aletean (el parámetro elige el color).</summary>
    public const float KindButterfly = 11f;

    /// <summary>Emblema de vidrio de cuatro paneles flotando en el cielo.</summary>
    public const float KindEmblem = 12f;

    /// <summary>Casa con balcones (verticales de ciudad baja): 4 pisos con retículo de balcones.</summary>
    public const float KindHouse = 15f;

    /// <summary>Local comercial: negocio a nivel de calle con toldo y cartel.</summary>
    public const float KindShop = 16f;

    /// <summary>Brizna de pasto: dos cuadriláteros cruzados que se doblan con el viento.</summary>
    /// <summary>
    /// Malla con MATERIAL de verdad: se dibuja por el camino texturado (albedo, normales, ARM y
    /// luces puntuales) y su colocación es posición + giro en Y + escala. Es el tipo que usan los
    /// modelos glTF y la geometría propia de los dioramas (piso, paredes, carteles): a diferencia de
    /// los tipos procedurales, estos no llevan una rama de código por forma — el shader es el mismo
    /// y lo que cambia es el dato del material.
    /// </summary>
    public const float KindTextured = 17f;

    /// <summary>
    /// Estela de lluvia: un quad ALARGADO que el vertex shader orienta hacia la cámara y hace caer
    /// dentro de una caja que ACOMPAÑA al ojo (si la caja quedara fija en el mundo, el travelling la
    /// dejaría atrás y el cuadro se secaría a los pocos segundos). Se dibuja con MEZCLA y después de
    /// todo lo opaco, y no proyecta sombra.
    /// </summary>
    public const float KindRain = 18f;

    public const float KindGrassBlade = 13f;

    /// <summary>Mota de polen: el shader la orienta hacia la cámara (se dibuja con mezcla).</summary>
    public const float KindPollen = 14f;

    /// <summary>
    /// ESTILO de la escena (cielo, sol, atmósfera, nubes, exposición, tinte del suelo). Es el único
    /// lugar del motor donde vive el look de cada escena: los shaders son uno solo y se configuran
    /// con esto, así que dos escenas no comparten ni un número de estilo y tocar una no puede
    /// cambiar la otra. Cada escena arma el suyo en su archivo (ver <c>Scenes/Styles</c>).
    /// </summary>
    public required SceneStyle Style { get; init; }

    /// <summary>
    /// ENTORNO IBL de la escena (ver <see cref="SceneEnvironment"/>): la luz que llega de todo
    /// alrededor, medida con un HDRI del lugar y proyectada a 9 armónicos esféricos, y no el ambiente
    /// que el estilo tenga que inventar.
    ///
    /// Null (lo normal en una escena que todavía no declara su HDRI) deja el ambiente EXACTAMENTE
    /// como estaba: el cielo procedural del estilo. Es decir que declarar el entorno es lo único que
    /// hace falta para que una escena tenga IBL, y ninguna escena cambia por el hecho de que otra lo
    /// declare.
    /// </summary>
    public SceneEnvironment? Environment { get; init; }

    /// <summary>
    /// Volumen que proyecta sombra (ver <see cref="SceneShadowVolume"/>), <b>solo si hace falta
    /// ajustarlo a mano</b>. Es una perilla de dirección de arte: achicarlo da sombras más nítidas a
    /// costa de recortar lo que queda afuera.
    ///
    /// Dejarlo en <c>null</c> (lo normal) es lo que hace que esto sea SOMBRAS DINÁMICAS y sin
    /// configurar nada por objeto ni por escena: el volumen se deduce de la geometría de la propia
    /// escena (ver <see cref="ResolveShadowVolume"/>).
    /// </summary>
    public SceneShadowVolume? ShadowVolume { get; init; }

    /// <summary>
    /// Cuánto pesa la sombra proyectada (1 = negra del todo, 0 = sin sombra). Es la perilla que
    /// enciende la función en las cuatro APIs a la vez: por eso vive en la ESCENA y no en un backend
    /// (una API con sombras y otra sin ellas daría dos imágenes distintas del mismo mundo).
    /// </summary>
    public float ShadowStrength { get; init; }

    private SceneShadowVolume? _resolvedVolume;
    private bool _volumeResolved;

    /// <summary>
    /// El volumen de sombra REAL de la escena: el declarado a mano si lo hay, y si no el que se
    /// DEDUCE de la geometría (el AABB de todas las mallas colocadas), así que no hay que configurar
    /// nada por objeto ni por escena.
    ///
    /// Cómo se deduce, y por qué así:
    /// <list type="number">
    /// <item>Se calcula la caja LOCAL de cada malla UNA vez (una pasada sobre sus vértices) y después
    /// se transforman solo sus 8 esquinas por instancia. Recorrer todos los vértices de todas las
    /// instancias costaría miles de millones de operaciones en una escena con vegetación instanciada;
    /// con la caja local son 8 por instancia, o sea nada.</item>
    /// <item>Se saltean las mallas que DEFORMA el vertex shader (los tipos del corredor y de la grilla
    /// del terreno, ver los <c>Kind*</c>): su posición en el CPU no es la del mundo —el shader las
    /// envuelve alrededor de la cámara o las levanta con el campo de altura—, así que meterlas en la
    /// caja daría un volumen que no tiene nada que ver con lo que se ve. Esas escenas (corredor, Aero)
    /// se quedan con las sombras analíticas de siempre, que es lo que su perfil necesita.</item>
    /// <item>El resultado se centra y se le suma la extensión mínima para que el volumen nunca sea
    /// plano, y si no quedó ninguna malla útil se devuelve <c>null</c> (sin pasada de sombras).</item>
    /// </list>
    /// </summary>
    public SceneShadowVolume? ResolveShadowVolume()
    {
        if (_volumeResolved) return _resolvedVolume;
        _volumeResolved = true;

        if (ShadowVolume is { } declared)
        {
            _resolvedVolume = declared;
            return _resolvedVolume;
        }
        if (ShadowStrength <= 0f) return null;   // apagada: no hay nada que calcular

        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        bool any = false;

        // Fuera del bucle: un stackalloc adentro se acumularía en cada vuelta (CA2014).
        Span<Vector3> corners = stackalloc Vector3[8];

        foreach (var mesh in Meshes)
        {
            // Las mallas con MEZCLA no definen el volumen: una partícula o una gota de lluvia no
            // proyecta sombra (tampoco lo hace en la vida real) y su colocación en el CPU —la caja que
            // ACOMPAÑA a la cámara, en el caso de la lluvia— no describe nada del mundo.
            if (mesh.Blend) continue;
            if (!TryLocalBounds(mesh, out var localMin, out var localMax)) continue;

            for (int i = 0; i < 8; i++)
            {
                corners[i] = new Vector3(
                    (i & 1) == 0 ? localMin.X : localMax.X,
                    (i & 2) == 0 ? localMin.Y : localMax.Y,
                    (i & 4) == 0 ? localMin.Z : localMax.Z);
            }

            foreach (var instance in mesh.Instances)
            {
                if (IsShaderDisplaced(instance.Orientation.Z)) continue;

                var origin = new Vector3(
                    instance.PositionScale.X,
                    instance.PositionScale.Y,
                    instance.PositionScale.Z);
                float scale = instance.PositionScale.W;
                float yaw = instance.Orientation.X;

                foreach (var corner in corners)
                {
                    var world = origin + RotateY(corner * scale, yaw);
                    minimum = Vector3.Min(minimum, world);
                    maximum = Vector3.Max(maximum, world);
                }
                any = true;
            }
        }

        // Sin geometría colocada no hay pasada de sombras: mejor las analíticas de siempre que una
        // matriz inventada.
        if (!any) return null;

        var center = (minimum + maximum) * 0.5f;
        var extent = Vector3.Max((maximum - minimum) * 0.5f, new Vector3(1f));
        _resolvedVolume = new SceneShadowVolume(center, extent);
        return _resolvedVolume;
    }

    /// <summary>
    /// ¿Esta malla entra en la pasada de profundidad del shadow map? (ver <c>README-SOMBRAS.md</c>)
    ///
    /// Sí para toda la geometría que el vertex shader coloca con una transformación PURA (la de los
    /// dioramas y los modelos glTF): esa misma transformación, con la matriz de luz en lugar de la de
    /// la cámara, da la profundidad vista desde el sol. Las DOS excepciones son las mismas que saltea
    /// <see cref="ResolveShadowVolume"/> y por la misma razón:
    /// <list type="bullet">
    /// <item>Las mallas de PARTÍCULAS (<see cref="SceneMesh.Blend">): son transparentes y el shader
    /// las orienta hacia la CÁMARA, así que en espacio de luz quedarían de canto proyectando manchas
    /// que no corresponden a nada. El polen y las hojas no proyectan sombra (tampoco lo hace una mota
    /// de polvo en la vida real).</item>
    /// <item>Los tipos que el shader DESPLAZA (la grilla del terreno y las rocas del corredor): su
    /// posición en el CPU no es la del mundo, así que entrarían al mapa en un lugar equivocado. Esas
    /// escenas se quedan con las sombras analíticas de <c>ShadowCasters</c>, que es lo que su perfil
    /// necesita.</item>
    /// </list>
    /// Se mira la instancia 0 y no todas: el tipo es del TIPO de malla (todas las instancias de una
    /// malla son de la misma clase; lo que cambia entre ellas es la colocación, no la rama del shader).
    /// </summary>
    public bool MeshCastsShadow(SceneMesh mesh)
    {
        if (mesh.Blend) return false;
        var instances = mesh.RenderInstances;
        if (instances.Length == 0) return false;
        return !IsShaderDisplaced(instances[0].Orientation.Z);
    }

    /// <summary>Caja LOCAL de una malla (una pasada sobre sus vértices), o <c>false</c> si no tiene.</summary>
    private static bool TryLocalBounds(SceneMesh mesh, out Vector3 minimum, out Vector3 maximum)
    {
        minimum = new Vector3(float.MaxValue);
        maximum = new Vector3(float.MinValue);
        if (mesh.Vertices.Length == 0) return false;

        foreach (var vertex in mesh.Vertices)
        {
            minimum = Vector3.Min(minimum, vertex.Position);
            maximum = Vector3.Max(maximum, vertex.Position);
        }
        return true;
    }

    /// <summary>Giro en Y alrededor del origen, el MISMO que aplica el vertex shader al colocar.
    /// Para una caja envolvente el signo del giro no cambia el resultado útil (la caja ya se toma
    /// como esfera envolvente al armar la matriz de luz), así que no hace falta más precisión.</summary>
    private static Vector3 RotateY(Vector3 value, float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        return new Vector3(value.X * cos + value.Z * sin, value.Y, -value.X * sin + value.Z * cos);
    }

    /// <summary>True para los tipos de malla cuya posición de mundo la decide el vertex shader
    /// (el corredor que envuelve alrededor de la cámara y la grilla del terreno): su caja en el CPU
    /// no describe nada de lo que se ve.</summary>
    private static bool IsShaderDisplaced(float kind) => kind < 3.5f;

    /// <summary>
    /// Cuántas esferas de sombra entran en el bloque por frame. Es un límite DURO del layout (tiene
    /// que dar lo mismo en C#, HLSL y GLSL): con una pasada de sombras de verdad cada objeto
    /// cercano proyecta la suya, así que además de los árboles y las flores del primer plano entran
    /// las rosas del prado y las copas de la línea de árboles.
    /// </summary>
    public const int MaxShadowCasters = 16;

    /// <summary>
    /// Resolución POR DEFECTO del shadow map, en téxeles por lado. Es fija en el sentido de que no se
    /// adapta sola a la escena ni al hardware —el benchmark mide trabajo por frame, y una resolución
    /// que cambiara sin que nadie la pida haría que dos corridas del mismo equipo no midieran lo
    /// mismo—, pero la configuración de la corrida la puede cambiar a propósito y a la vista
    /// (ver <see cref="Graphics.SceneGraphicsOptions.ShadowMapSize"/>): este número es el valor con el
    /// que corren las escenas cuando nadie toca nada.
    /// </summary>
    public const int ShadowMapSize = 2048;

    /// <summary>
    /// Cuántas luces puntuales entran en el bloque por frame. Es un límite DURO del layout (tiene
    /// que dar lo mismo en C#, HLSL y GLSL) y también el presupuesto de la escena: cada luz se evalúa
    /// por píxel, así que subirlo mucho convierte el benchmark en una prueba de luces y no de escena.
    /// </summary>
    public const int MaxPointLights = 8;

    /// <summary>
    /// Cuántos mapas tiene cada material en el atlas de texturas de la escena (ver
    /// <see cref="MaterialAtlas"/>): albedo, normales y ARM, en ese orden. Es un contrato con el
    /// SHADER (<c>rebanada = ranura * mapas + mapa</c>), así que vive acá y no en el atlas: las
    /// constantes que el HLSL interpola salen de este archivo (ver <c>SceneShaders.SceneConstants</c>).
    /// </summary>
    public const int MaterialMapsPerSlot = MaterialAtlas.MapsPerSlot;

    /// <summary>
    /// Casters de sombra de la escena (xyz = centro, w = radio, en unidades de mundo). Vacío = la
    /// escena no proyecta sombras de objeto (el corredor y el cañón se apoyan solo en la niebla).
    /// </summary>
    public IReadOnlyList<Vector4> ShadowCasters { get; init; } = Array.Empty<Vector4>();

    // =====================================================================
    // Corredor infinito
    // =====================================================================

    /// <summary>
    /// Largo del corredor (unidades de mundo). El vertex shader envuelve las instancias en una
    /// ventana de este largo alrededor de la cámara: el terreno se repite pero el CONTENIDO que
    /// se ve siempre es nuevo. Debe coincidir con <c>CorridorLength</c> en <c>SceneShaders</c>.
    /// </summary>
    public const float CorridorLength = 420f;

    /// <summary>Velocidad de avance de la cámara del corredor, en unidades por segundo.</summary>
    public const float FlightSpeed = 22f;

    /// <summary>
    /// Terreno del corredor. VIVE DUPLICADO a propósito: acá para la cámara y en
    /// <c>SceneShaders.Common</c> para envolver las instancias. Si se cambia uno, cambiar el otro —
    /// si no, las rocas quedan flotando o enterradas.
    /// </summary>
    public static float TerrainHeight(float x, float z) =>
        5.5f * MathF.Sin(x * 0.06f) + 4.0f * MathF.Cos(z * 0.05f) + 2.0f * MathF.Sin((x + z) * 0.021f);

    // =====================================================================
    // Terreno compartido (corredor y Frutiger Aero)
    // =====================================================================

    /// <summary>Nivel del agua de la vega (unidades de mundo). Debajo de acá el terreno es agua.</summary>
    public const float WaterLevel = 0f;

    /// <summary>Radio de la grilla del terreno: se ven 5 km a la redonda, con celdas que crecen con la distancia.</summary>
    public const float TerrainRadius = 5200f;

    /// <summary>Lado de la grilla del terreno (celdas por eje, la malla es (lado × lado × 2) triángulos).</summary>
    public const int TerrainGridSide = 256;

    /// <summary>
    /// Cuánto del radio se reparte LINEALMENTE en la grilla; el resto va con el cubo:
    /// <c>x = u·TerrainLinearShare·R + u³·(1−TerrainLinearShare)·R</c>. La parte lineal mantiene
    /// celdas usables cerca de la cámara (con el warp puramente cuadrático/cúbico las primeras
    /// celdas quedaban de centímetros y se gastaba toda la malla en un parche diminuto), y el
    /// término cúbico estira las del fondo hasta el horizonte. Con 0,10 y 256 celdas, la celda del
    /// centro mide ~4 unidades y la del borde ~60.
    /// </summary>
    public const float TerrainLinearShare = 0.10f;

    /// <summary>
    /// Centro del valle en la coordenada z: el río serpentea. Debe coincidir EXACTAMENTE con
    /// <c>ValleyCenterX</c> de <c>SceneShaders</c> y <c>GlShaders</c>: de allá sale el terreno que
    /// se dibuja (el corredor vuela por este valle).
    /// </summary>
    public static float ValleyCenterX(float z) =>
        260f * MathF.Sin(z * 0.00060f) + 120f * MathF.Sin(z * 0.00170f + 1.7f);

    // La FORMA del terreno (semiancho del valle, cauce del río, crestas) vive SOLO en los shaders:
    // la malla del terreno la genera el vertex shader a partir del campo de altura y el C# no
    // necesita esa altura para nada más que la cámara del corredor. Tenerla además acá era
    // duplicarla en tres idiomas sin que nadie la use.


    // =====================================================================
    // Frutiger Aero (el mundo "de postal")
    // =====================================================================

    /// <summary>Velocidad de la deriva de la cámara, en unidades por segundo.</summary>
    public const float AeroSpeed = 5.5f;

    /// <summary>
    /// Altura del ojo sobre el pasto, en unidades. Doce metros es lo que hace que el prado LLENE el
    /// cuadro: a 16 la mirada pasaba por encima del contenido (la hierba, las matas y los modelos se
    /// veían como una franja fina al pie de la imagen) y la escena se leía como un plano de agua.
    /// </summary>
    public const float AeroEyeHeight = 12.0f;

    /// <summary>Altura a la que apunta la cámara: alto y lejos, para que el cielo se lleve el cuadro.</summary>
    public const float AeroLookHeight = 58f;

    /// <summary>z del centro de la ciudad del horizonte.</summary>
    public const float AeroCityZ = 470f;

    /// <summary>z donde arranca la deriva (la ciudad queda 700 unidades adelante).</summary>
    public const float AeroStartZ = -150f;

    /// <summary>
    /// Centro del lago y radio de su cuenca. El lago es una LAGUNA de 240 unidades de ancho en el
    /// medio del prado, no un mar: con 250 de radio la cuenca se comía la banda entera de siembra
    /// (todo el prado quedaba bajo el agua o en el borde del cuadro) y la cámara cruzaba el agua
    /// durante casi todo el recorrido, así que el mundo se veía vacío. Cerca de la ciudad queda como
    /// el espejo de agua de la postal, que es lo que era.
    /// </summary>
    public const float AeroLakeX = 0f;
    public const float AeroLakeZ = 210f;
    public const float AeroLakeRadius = 120f;

    /// <summary>Cuánto hunde la cuenca del lago por debajo del pasto. Hondo: a 30+ unidades la
    /// absorción se come el lecho y el centro se ve AZUL de lago, no verde de laguna.</summary>
    public const float AeroLakeDepth = 36f;

    /// <summary>
    /// Briznas de pasto del prado. El número es CARGA, no adorno: cada brizna son 4 triángulos
    /// (12 vértices) y el vertex shader las dobla con el viento, así que el prado es también una
    /// medición de vértices instanciados — la mitad del presupuesto de relleno del primer plano.
    /// </summary>
    public const int GrassTufts = 60000;

    /// <summary>Motas de polen en el aire (malla transparente: va con mezcla y sin escribir profundidad).</summary>
    public const int PollenMotes = 1400;

    /// <summary>
    /// Hojas y pétalos en el aire: las partículas GRANDES del primer plano. Poco número y mucha
    /// presencia —cada una es un sprite de media unidad de mundo—, que es lo que le da escala al
    /// prado; el polen sigue siendo el relleno fino de fondo.
    /// </summary>
    public const int LeafMotes = 240;

    /// <summary>Semillas con penacho flotando (diente de león): las que se ven contra el verde.</summary>
    public const int SeedMotes = 130;

    // ---- Sprites de partícula -------------------------------------------------------------
    // Cada partícula elige su DIBUJO con la celda del atlas de detalle que trae en la tinta del
    // material (ver SceneDetailTexture.BuildParticleTiles): la celda guarda el color del sprite en
    // rgb y su FORMA en el alfa, que es lo que el shader usa como máscara. Son las mismas cuatro
    // celdas en las cuatro APIs porque viajan como un número.

    /// <summary>Mota de polen: núcleo claro y borde difuso.</summary>
    public const float ParticlePollen = 12f;

    /// <summary>Hoja: silueta con nervaduras, verde de prado.</summary>
    public const float ParticleLeaf = 13f;

    /// <summary>Semilla con penacho (diente de león): el tallo y los pelos claros.</summary>
    public const float ParticleSeed = 14f;

    /// <summary>Pétalo: la misma silueta de hoja pero redonda y cálida.</summary>
    public const float ParticlePetal = 15f;

    /// <summary>
    /// Campo de altura del mundo Aero: LOMAS suaves —nada de cañones— con la cuenca del lago en el
    /// medio del recorrido. Son cuatro senos a propósito: la misma fórmula se escribe en C# (para
    /// apoyar árboles, flores y cámara), en HLSL y en GLSL, y con senos los tres idiomas dan el
    /// mismo número sin tener que replicar el ruido de la retícula entera.
    /// </summary>
    public static float AeroHeight(float x, float z)
    {
        // Cuatro ESCALAS: las lomas anchas (dan el horizonte), las medianas (el vaivén que se ve
        // de cerca) y la ondulación fina (el relieve que hace que el pasto no sea una chapa verde).
        // Sin las dos últimas el campo se ve plano: la loma grande tiene tan poca pendiente en 100
        // unidades que el sombreado sale uniforme.
        float hills = 12f * MathF.Sin(x * 0.0021f + 0.7f)
                    + 10f * MathF.Sin(z * 0.0026f - 0.4f)
                    + 6f * MathF.Sin(x * 0.0043f + z * 0.0035f + 1.9f)
                    + 3.0f * MathF.Sin(x * 0.0125f - 1.1f) * MathF.Sin(z * 0.0104f + 0.6f)
                    + 1.6f * MathF.Sin(x * 0.0300f + 0.9f) * MathF.Sin(z * 0.0260f - 0.5f)
                    + 0.55f * MathF.Sin(x * 0.0900f - 0.3f) * MathF.Sin(z * 0.0810f + 1.2f);

        float dx = x - AeroLakeX;
        float dz = z - AeroLakeZ;
        float lakeDistance = MathF.Sqrt(dx * dx + dz * dz);

        // La costa NO es un círculo: el radio del cuenco respira con dos ondas angulares y una
        // espacial. Con el borde perfectamente redondo el lago se leía como una bañera, que es
        // justo lo que no pasa en ningún lago de verdad. La MISMA cuenta está en el HLSL y en el
        // GLSL (el sombreado marcha el campo de altura, así que si divergen la sombra sale de un
        // cráter que no existe).
        float angle = MathF.Atan2(dz, dx);
        float shore = 0.19f * MathF.Sin(angle * 5f + 0.6f)
                    + 0.10f * MathF.Sin(angle * 11f - 1.7f)
                    + 0.06f * MathF.Sin(dx * 0.0041f + dz * 0.0033f + 2.2f);
        float basin = 1f - Smoothstep(AeroLakeRadius * 0.45f, AeroLakeRadius * (1f + shore), lakeDistance);

        // Talud sur: la orilla sur se talla en ESCALONES (el norte queda de playa suave). La
        // cascada necesita un borde desde el que el arroyo se despeñe, y el lago un vaso hondo
        // del lado del desagüe — es lo que evita el fondo de plato sopa que se leía laguna.
        //
        // OJO CON EL ALCANCE (era el defecto más caro de la escena): el escalón vive en un ANILLO
        // pegado a la costa (0,35 R a 1,45 R, con el pico en la orilla). Escrito sin anillo —como
        // estaba, con un smoothstep que CRECÍA con la distancia— se aplicaba a media llanura: todo
        // el prado al norte del lago quedaba 20 unidades BAJO el agua, y el pasto, las flores y los
        // modelos (que se apoyan con LandX, y LandX corre la semilla hasta pisar tierra) terminaban
        // empujados al borde del cuadro, en un pantano de 2 unidades de profundidad. El campo se veía
        // vacío por esto, no por falta de contenido.
        float shoreBand = lakeDistance / AeroLakeRadius;
        float ring = Smoothstep(0.35f, 0.85f, shoreBand) * (1f - Smoothstep(1.05f, 1.45f, shoreBand));
        float southern = Smoothstep(0.10f, 0.75f, -dz / MathF.Max(1f, lakeDistance)) * ring;
        basin += southern * (0.85f - basin) * 0.55f;

        // Cauce del arroyo: una zanja suave que sale del lago por el sur y baja hacia la cascada.
        // El pasto no la siembra (LandX lo respeta) y el agua dibuja los rápidos dentro.
        float riverHalfWidth = 11f + 5f * MathF.Sin(z * 0.013f);
        float bank = MathF.Abs(x - AeroRiverX(z)) - riverHalfWidth;
        float river = 1f - Smoothstep(0f, 60f, bank);

        float height = hills - basin * AeroLakeDepth;
        // El cauce NO puede cortar el vaso del lago: dentro de la cuenca manda el lago (el cauce
        // ya está bajo el agua ahí, que es lo correcto: el arroyo ES el desagüe).
        return MathF.Min(height, height - river * 7f * (1f - basin * 0.8f));
    }

    /// <summary>
    /// X del eje del arroyo que desagua el lago hacia el sur (la cascada). La MISMA fórmula vive
    /// en el HLSL y en el GLSL: el terreno se talla con ella y el agua de los rápidos se pinta
    /// donde ella pasa. Si se cambia acá, cambiar allá.
    /// </summary>
    public static float AeroRiverX(float z) => 10f + 55f * MathF.Sin(z * 0.006f);

    /// <summary>
    /// X del sendero de tierra que recorre la vega este del arroyo (por donde pasea la cámara).
    /// También vive en los shaders: el pasto lo deja en tierra pisada.
    /// </summary>
    public static float AeroPathX(float z) => AeroRiverX(z) + 95f + 30f * MathF.Sin(z * 0.011f + 2.0f);

    /// <summary>
    /// X del eje del recorrido con el que la cámara cruza el prado a la altura <paramref name="z"/>.
    /// Es la MISMA cuenta que <see cref="AeroCamera"/> con el tiempo despejado de z, y existe para que
    /// el contenido se siembre ALREDEDOR del recorrido: a 100 unidades el cuadro abarca unos 115, así
    /// que lo sembrado por la banda entera (520 de ancho) no se ve nunca y el prado queda vacío con el
    /// mismo presupuesto de instancias. Es función de z y no de t para poder sembrar en el armado.
    /// </summary>
    public static float AeroFlightX(float z) =>
        AeroRiverX(z) + 120f + 34f * MathF.Sin((z - AeroStartZ) / AeroSpeed * 0.05f + 0.8f);

    /// <summary>
    /// Cámara del mundo Aero en TRES ACTOS con fundido (sin cortes): primero la cascada —el arroyo
    /// que desagua el lago se despeña por el talud sur, y la cámara lo mira de costado mientras
    /// baja—, después el campo estilo XP (prado, flores y sendero, con la laguna entrando por un
    /// costado) y al final la ciudad. Todo lo que se mueve además es función del tiempo, así que
    /// la corrida sigue siendo repetible.
    /// </summary>
    public static (Vector3 Eye, Vector3 Target) AeroCamera(double seconds)
    {
        float t = (float)seconds;
        float z = AeroStartZ + t * AeroSpeed;
        float x = AeroRiverX(z) + 120f + 34f * MathF.Sin(t * 0.05f + 0.8f);
        // Sobre el lago el ojo vuela a la altura del AGUA (el campo está hundido ahí): sin el
        // máximo, la cámara cruzaba el vado por debajo de la superficie.
        float y = MathF.Max(AeroHeight(x, z), WaterLevel) + AeroEyeHeight + 0.9f * MathF.Sin(t * 0.17f);

        var eye = new Vector3(x, y, z);

        var falls = new Vector3(AeroRiverX(-70f) + 26f, 9f, -70f);
        // El acto del campo mira el PRADO a media altura (y 15, no 26): apuntando tan arriba, el suelo
        // —que es donde está todo el contenido de la escena— caía al borde inferior del cuadro.
        var field = new Vector3(AeroFlightX(z + 150f) + 40f * MathF.Sin(t * 0.075f), 15f, z + 150f);
        var lake = new Vector3(AeroLakeX, 8f, AeroLakeZ);
        var city = new Vector3(6f * MathF.Sin(t * 0.030f), AeroLookHeight, AeroCityZ);

        var target = falls;
        target = Vector3.Lerp(target, field, Smoothstep(10f, 16f, t));
        target = Vector3.Lerp(target, lake, Smoothstep(32f, 38f, t));
        target = Vector3.Lerp(target, city, Smoothstep(50f, 57f, t));
        return (eye, target);
    }

    private static float Smoothstep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / MathF.Max(1e-6f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

}

/// <summary>Catálogo de escenas del componente.</summary>
public static class SceneCatalog
{
    public static IReadOnlyList<SceneDefinition> All { get; } = new[]
    {
        // El piloto cinematográfico primero: es la escena que se muestra por defecto. Su contenido
        // (geometría de los modelos y texturas) es PEREZOSO, así que enumerar el catálogo para
        // llenar el selector no cuesta nada (ver LazySceneMeshes).
        NeonScene.Build(),
        BuildAeroScene(),
        BuildCorridorScene()
    };

    public static SceneDefinition Default => All[0];

    public static SceneDefinition? Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Escena 1 — Frutiger Aero: el mundo de postal. Pasto verde saturado con reflejo, lago de
    /// agua brillante, ciudad de vidrio y cromo en el horizonte, cielo azul profundo con cúmulos
    /// esponjosos, sol con resplandor de lente y burbujas de jabón iridiscentes flotando a la
    /// deriva (más gaviotas, un globo, mariposas, girasoles y rosas).
    ///
    /// Es el perfil de carga OPUESTO al del corredor: acá manda el trabajo por píxel (cielo con
    /// marcha de nubes, agua con Fresnel, vidrio con especular fuerte) y muchos objetos chicos
    /// instanciados, no la malla de rocas de mil doscientas caras.
    /// </summary>
    private static SceneDefinition BuildAeroScene()
    {
        const int seed = 20260926;   // fija: la misma escena, siempre
        var rng = new Random(seed);

        var ground = BuildTerrainGrid();
        var bubbles = BuildSphereMesh(segments: 20, rings: 12);
        var towers = BuildTowerMesh(halfWidth: 0.080f);
        var houses = BuildHouseMesh();
        var shops = BuildShopMesh();
        var trees = BuildTreeMesh();
        var sunflowers = BuildSunflowerMesh();
        var roses = BuildRoseMesh();
        var gulls = BuildGullMesh();
        var balloons = BuildBalloonMesh();
        var butterflies = BuildButterflyMesh();

        // La COMPOSICIÓN se arma con la geometría de la cámara: a distancia d = z - AeroStartZ el
        // cuadro abarca |x| ≲ d (60° de campo vertical con esta relación de aspecto), así que lo
        // grande del primer plano tiene que estar ADELANTE y cerca del eje. Sin eso, las flores y
        // los árboles "del cuadro" quedan fuera de cuadro o pisados por el borde inferior.

        // ---- Burbujas: el sello de la estética. Dos GRANDES cerca del cuadro (como la postal) y
        // el resto repartidas entre la cámara y la ciudad, a distintas alturas.
        var bubbleInstances = new List<SceneInstance>(40)
        {
            Bubble(-58f, 72f, -62f, 20f, 0.4f),
            Bubble(104f, 92f, 62f, 17f, 2.1f),
            Bubble(-42f, 34f, -34f, 8.5f, 1.2f),
            Bubble(52f, 26f, 6f, 6.0f, 3.3f)
        };
        for (int i = 0; i < 32; i++)
        {
            float z = -120f + (float)rng.NextDouble() * 420f;
            float spread = (z + 160f) * 0.95f;      // el ancho visible a esa distancia
            // Centradas en la LÍNEA DE VUELO: una pompa que revienta a 200 unidades del cuadro no
            // adorna la escena, cuesta igual que la que se ve.
            float x = SceneDefinition.AeroFlightX(z) - spread + (float)rng.NextDouble() * spread * 2f;
            float y = 10f + (float)rng.NextDouble() * 80f;
            float scale = 0.9f + (float)rng.NextDouble() * 5.0f;
            bubbleInstances.Add(Bubble(x, y, z, scale, (float)rng.NextDouble() * 6f));
        }

        // ---- Ciudad: TRES tipos de edificio (torres de oficinas, casas con balcones y locales
        // comerciales) en un DAMERO de celdas de 46: el paso es mayor que el ancho máximo (26) más
        // el desnivel del terreno (≈8), así que dos vecinos no se tocan — era el defecto de la
        // grilla de 16 con radios de 16. Los lotes anegados o en el cauce quedan vacíos.
        const float cellSize = 46f;
        const float cellJitter = 7f;
        // Los lotes sobre el cauce (|x - AeroRiverX(z)| < 70) se saltean abajo, con la MISMA fórmula
        // del río: el cauce de la ciudad no se pisa con edificios.
        var officeInstances = new List<SceneInstance>(14);
        var houseInstances = new List<SceneInstance>(22);
        var shopInstances = new List<SceneInstance>(14);
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 9; col++)
            {
                float x = -184f + col * cellSize + Jitter(rng, cellJitter);
                float z = SceneDefinition.AeroCityZ + (row - 1.5f) * cellSize + Jitter(rng, cellJitter);

                // Vacíos: agua (lago/canal) y la avenida del cauce desviado (un 25% queda en
                // patio, como toda ciudad de verdad).
                float g = SceneDefinition.AeroHeight(x, z);
                if (g < SceneDefinition.WaterLevel + 1.5f) continue;
                if (MathF.Abs(x - (10f + 55f * MathF.Sin(SceneDefinition.AeroCityZ * 0.006f))) < 70f) continue;
                if (rng.NextDouble() < 0.25f) continue;

                bool wide = col % 3 == 1 && row < 2;           // las dos hileras de adelante: anchas
                float footprint = wide ? 26f : 16f;
                float yaw = MathF.Round((float)rng.NextDouble() * 4f) * (MathF.PI * 0.5f); // 90°: fachadas alineadas a la cuadrícula
                float roll = (float)rng.NextDouble();

                // Altura por TIPO: torres al fondo (2 filas), casas y locales adelante (primer plano).
                if (row >= 2)
                {
                    float height = 74f + (float)rng.NextDouble() * 96f;
                    officeInstances.Add(Placed(x, z, height, yaw, SceneDefinition.KindTower, (float)rng.NextDouble()));
                }
                else if (roll < 0.55f)
                {
                    float height = 17f + (float)rng.NextDouble() * 12f;
                    houseInstances.Add(Placed(x, z, height, yaw, SceneDefinition.KindHouse, (float)rng.NextDouble()));
                }
                else
                {
                    float height = 8.5f + (float)rng.NextDouble() * 4.5f;
                    shopInstances.Add(Placed(x, z, height, yaw, SceneDefinition.KindShop, (float)rng.NextDouble()));
                }
            }
        }

        // ---- Árboles: la línea oscura del fondo (separá el pasto del lago y de la ciudad, como en
        // la postal) y unos pocos en primer plano, uno grande a la izquierda del cuadro.
        var treeInstances = new List<SceneInstance>(64);
        for (int i = 0; i < 60; i++)
        {
            float x = -470f + i * 16f + Jitter(rng, 5f);
            float z = 390f + Jitter(rng, 30f);
            float scale = 4.0f + (float)rng.NextDouble() * 4.2f;
            treeInstances.Add(Grounded(x, z, scale, (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindTree, (float)rng.NextDouble()));
        }
        // Los cuatro que enmarcan el cuadro se apoyan en la LÍNEA DE VUELO, a los costados: para
        // enmarcar hay que estar donde pasa la cámara. Van por FUERA de la siembra de modelos para que
        // la copa entre por el borde superior, como en la postal.
        treeInstances.Add(Grounded(SceneDefinition.AeroFlightX(-66f) - 62f, -66f, 8.0f, 0.6f, SceneDefinition.KindTree, 0.2f));
        treeInstances.Add(Grounded(SceneDefinition.AeroFlightX(-122f) + 52f, -122f, 5.6f, 2.4f, SceneDefinition.KindTree, 0.1f));
        treeInstances.Add(Grounded(SceneDefinition.AeroFlightX(-98f) + 74f, -98f, 6.2f, 4.1f, SceneDefinition.KindTree, 0.9f));
        treeInstances.Add(Grounded(SceneDefinition.AeroFlightX(-30f) - 58f, -30f, 5.0f, 5.5f, SceneDefinition.KindTree, 0.6f));

        // ---- Flores: el girasol y la rosa son el primer plano de las postales (abajo, grandes) y
        // además hay un manto de flores chicas sobre el pasto para que el verde no quede pelado.
        // Las flores NO crecen dentro del lago: LandX corre la semilla en X hasta pisar tierra.
        var sunflowerInstances = new List<SceneInstance>
        {
            Grounded(LandX(SceneDefinition.AeroFlightX(-110f) + 24f, -110f), -110f, 3.0f, 0.2f, SceneDefinition.KindSunflower, 0f),
            Grounded(LandX(SceneDefinition.AeroFlightX(-92f) + 44f, -92f), -92f, 2.4f, 5.1f, SceneDefinition.KindSunflower, 0.4f),
            Grounded(LandX(SceneDefinition.AeroFlightX(-100f) - 34f, -100f), -100f, 2.2f, 2.6f, SceneDefinition.KindSunflower, 0.8f),
            Grounded(LandX(SceneDefinition.AeroFlightX(-88f) - 58f, -88f), -88f, 2.0f, 3.4f, SceneDefinition.KindSunflower, 0.2f),
            Grounded(LandX(SceneDefinition.AeroFlightX(-74f) + 62f, -74f), -74f, 1.8f, 1.1f, SceneDefinition.KindSunflower, 0.6f)
        };
        var roseInstances = new List<SceneInstance>
        {
            Grounded(LandX(SceneDefinition.AeroFlightX(-118f) - 26f, -118f), -118f, 2.6f, 1.4f, SceneDefinition.KindRose, 0f),
            Grounded(LandX(SceneDefinition.AeroFlightX(-92f) - 44f, -92f), -92f, 2.2f, 4.6f, SceneDefinition.KindRose, 0.7f),
            Grounded(LandX(SceneDefinition.AeroFlightX(-74f) - 16f, -74f), -74f, 1.9f, 2.9f, SceneDefinition.KindRose, 0.3f),
            Grounded(LandX(SceneDefinition.AeroFlightX(-108f) + 38f, -108f), -108f, 2.1f, 0.7f, SceneDefinition.KindRose, 0.9f),
            Grounded(LandX(SceneDefinition.AeroFlightX(-130f) + 78f, -130f), -130f, 1.8f, 5.8f, SceneDefinition.KindRose, 0.5f)
        };
        var meadowInstances = new List<SceneInstance>(64);
        for (int i = 0; i < 58; i++)
        {
            // El manto de flores chicas sigue el ancho VISIBLE a su distancia: así"hay flores hasta
            // el horizonte" en vez de un sembrado al azar que cae casi todo fuera de cuadro.
            float z = -140f + (float)rng.NextDouble() * 580f;
            float spread = MathF.Min(200f, (z + 160f) * 0.95f);
            float x = LandX(SceneDefinition.AeroFlightX(z) - spread + (float)rng.NextDouble() * spread * 2f, z);
            float scale = 0.9f + (float)rng.NextDouble() * 0.9f;
            // El manto es mitad girasoles y mitad rosas: un prado monótono se ve artificial.
            bool sunflower = rng.NextDouble() < 0.55;
            float kind = sunflower ? SceneDefinition.KindSunflower : SceneDefinition.KindRose;
            meadowInstances.Add(Grounded(x, z, scale, (float)(rng.NextDouble() * MathF.Tau), kind, (float)rng.NextDouble()));
        }

        // ---- Vida: gaviotas, globos y mariposas (poco, pero es lo que le pone escala al cielo).
        var gullInstances = new List<SceneInstance>
        {
            Sky(-64f, 42f, -120f, 2.6f, 0.3f, SceneDefinition.KindGull),
            Sky(46f, 58f, -186f, 2.2f, 1.7f, SceneDefinition.KindGull),
            Sky(-14f, 68f, -60f, 1.9f, 2.9f, SceneDefinition.KindGull),
            Sky(104f, 50f, -240f, 2.0f, 4.4f, SceneDefinition.KindGull)
        };
        var balloonInstances = new List<SceneInstance>
        {
            Sky(-92f, 66f, -34f, 4.2f, 0.2f, SceneDefinition.KindBalloon),
            Sky(126f, 78f, 220f, 3.0f, 2.2f, SceneDefinition.KindBalloon)
        };
        var butterflyInstances = new List<SceneInstance>
        {
            GroundedOffset(SceneDefinition.AeroFlightX(-110f) + 18f, -110f, 4.2f, 1.5f, 0f),
            GroundedOffset(SceneDefinition.AeroFlightX(-84f) + 26f, -84f, 3.6f, 1.3f, 0.6f),
            GroundedOffset(SceneDefinition.AeroFlightX(-100f) - 22f, -100f, 3.0f, 1.2f, 0.3f)
        };

        // ---- Hierba: briznas de verdad en el pasto, que es lo que separa "un campo verde" de
        // "un prado". Van alrededor del recorrido de la cámara (que solo avanza ~165 unidades por
        // corrida) y la densidad cae hacia los costados: el centro del cuadro se ve brizna por
        // brizna y el borde no gasta vértices en pasto que ya es una mancha de color. La y la pone
        // el campo de altura —igual que las flores— y NO se siembra dentro del lago.
        var grass = BuildGrassBladeMesh();
        var grassInstances = new List<SceneInstance>(SceneDefinition.GrassTufts);
        int grassDrawn = 0;
        for (int i = 0; i < SceneDefinition.GrassTufts * 3 && grassDrawn < SceneDefinition.GrassTufts; i++)
        {
            // Triangular en X (±260, denso en el eje) y uniforme en Z: la banda llega HASTA LA
            // CIUDAD (z≈460), porque el pasto que se corta en seco a mitad del cuadro se ve como
            // una alfombra con fin de tela. La orilla del lago se resuelve corriendo la semilla
            // en X hasta tierra (LandX), no descartándola: descartar dejaba un borde pelado
            // alrededor del agua.
            float z = -190f + (float)rng.NextDouble() * 650f;
            // El cauce del arroyo TAMBIÉN es agua: se corre la semilla fuera antes de buscar tierra.
            // La banda va centrada en la LÍNEA DE VUELO (ver AeroFlightX): así la mitad de las briznas
            // no se gasta detrás del cuadro.
            float x = SceneDefinition.AeroFlightX(z) + (float)(rng.NextDouble() + rng.NextDouble() - 1.0) * 250f;
            if (MathF.Abs(x - SceneDefinition.AeroRiverX(z)) < 20f) x += (x >= SceneDefinition.AeroRiverX(z) ? 1f : -1f) * 24f;
            x = LandX(x, z);

            float scale = 0.55f + (float)rng.NextDouble() * 0.75f;
            grassInstances.Add(Grass(x, z, scale, (float)rng.NextDouble() * MathF.Tau, (float)rng.NextDouble(), (float)rng.NextDouble()));
            grassDrawn++;
        }

        // ---- Partículas: polen, hojas, pétalos y semillas en el aire de la postal ----
        // Son SPRITES: el cuadro lo orienta el vertex shader hacia la cámara y la forma —con su ALFA—
        // la trae una celda del atlas de detalle (ver SceneDetailTexture). Una mota dibujada como
        // cuadro de color plano se lee como un error de dibujo, no como aire; con el degradado del
        // sprite, y con cada partícula con SU tamaño, SU giro y SU deriva, el aire se ve cargado. El
        // dibujo lo trae el sprite y el movimiento es función pura del tiempo y de la fase, así que la
        // corrida sigue siendo repetible.
        var pollen = BuildParticleQuadMesh();
        var pollenInstances = new List<SceneInstance>(
            SceneDefinition.PollenMotes + SceneDefinition.LeafMotes + SceneDefinition.SeedMotes);

        // Polen: la niebla fina de las postales, a media altura y ancha sobre el recorrido.
        for (int i = 0; i < SceneDefinition.PollenMotes; i++)
        {
            float z = -170f + (float)rng.NextDouble() * 610f;
            float x = SceneDefinition.AeroFlightX(z) + (float)(rng.NextDouble() + rng.NextDouble() - 1.0) * 140f;
            float height = 0.5f + (float)rng.NextDouble() * 15f;
            float size = 0.10f + (float)rng.NextDouble() * 0.16f;
            pollenInstances.Add(Particle(x, SceneDefinition.AeroHeight(x, z) + height, z, size,
                (float)rng.NextDouble(), SceneDefinition.ParticlePollen, 0.35f));
        }

        // Hojas y pétalos arrancados: los GRANDES del primer plano. Van pegados al recorrido de la
        // cámara (la misma curva del cauce que usan los faroles) y a la altura de la mirada, que es
        // donde se leen: son los que le dan escala al prado cuando la cámara pasa entre los árboles.
        for (int i = 0; i < SceneDefinition.LeafMotes; i++)
        {
            float z = -190f + (float)rng.NextDouble() * 660f;
            float x = SceneDefinition.AeroFlightX(z) + (float)(rng.NextDouble() * 2.0 - 1.0) * 78f;
            float height = 0.7f + (float)rng.NextDouble() * 9f;
            float size = 0.32f + (float)rng.NextDouble() * 0.45f;
            bool petal = rng.NextDouble() < 0.35;
            // La base es el SUELO o la superficie del lago: una hoja sobre el agua tiene que quedar
            // arriba del agua, no colgada del lecho (que está 30 unidades más abajo).
            pollenInstances.Add(Particle(x, MathF.Max(SceneDefinition.AeroHeight(x, z), SceneDefinition.WaterLevel) + height, z, size,
                (float)rng.NextDouble(),
                petal ? SceneDefinition.ParticlePetal : SceneDefinition.ParticleLeaf,
                0.6f + (float)rng.NextDouble() * 1.6f));
        }

        // Semillas con penacho (diente de león): suben despacio, que es lo que hace el aire quieto
        // de la postal, y se ven de LEJOS porque el penacho claro corta contra el verde.
        for (int i = 0; i < SceneDefinition.SeedMotes; i++)
        {
            float z = -190f + (float)rng.NextDouble() * 660f;
            float x = SceneDefinition.AeroFlightX(z) + (float)(rng.NextDouble() * 2.0 - 1.0) * 120f;
            float height = 2.0f + (float)rng.NextDouble() * 16f;
            float size = 0.22f + (float)rng.NextDouble() * 0.30f;
            pollenInstances.Add(Particle(x, MathF.Max(SceneDefinition.AeroHeight(x, z), SceneDefinition.WaterLevel) + height, z, size,
                (float)rng.NextDouble(), SceneDefinition.ParticleSeed, 0.18f + (float)rng.NextDouble() * 0.5f));
        }

        // ---- Contenido REAL (set 'aero'): vegetación, rocas y mobiliario de fotogrametría ----
        // Lo procedural de arriba se queda (los girasoles, las rosas y las briznas son la identidad de
        // la postal) pero lo que le da escala de MUNDO es esto: árboles, matas, pasto, flores, rocas y
        // bancos de fotogrametría real (Poly Haven CC0), apoyados en el MISMO campo de altura —por eso
        // no flotan en las lomas—. El set es OPCIONAL: si falta, la escena se arma como siempre y el
        // motivo viaja en el informe (ver AeroAssets.Notes).
        string? assetRoot = AeroAssets.Root;
        var modeled = new List<SceneMesh>(64);
        var heroTrees = new List<SceneInstance>(6);

        if (assetRoot != null)
        {
            // Muestreo del prado: la MISMA banda que el pasto procedural (triangular en X, uniforme en
            // Z, corrida a tierra con LandX para no sembrar dentro del lago) descartando el cauce.
            (float X, float Z) Meadow()
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    float z = -190f + (float)rng.NextDouble() * 430f;
                    float x = SceneDefinition.AeroFlightX(z) + (float)(rng.NextDouble() + rng.NextDouble() - 1.0) * 150f;
                    if (MathF.Abs(x - SceneDefinition.AeroRiverX(z)) < 16f) continue;
                    x = LandX(x, z);
                    if (SceneDefinition.AeroHeight(x, z) < SceneDefinition.WaterLevel + 0.35f) continue;
                    return (x, z);
                }
                return (0f, -60f);
            }

            // Orilla: se busca en X el punto que queda a un paso del nivel del agua. Así las rocas
            // caen en la costa y no repartidas por el medio del campo.
            (float X, float Z) Shore()
            {
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    float z = -170f + (float)rng.NextDouble() * 560f;
                    float best = 0f;
                    float bestScore = float.MaxValue;
                    for (int step = 0; step < 26; step++)
                    {
                        float x = -230f + step * 18f;
                        float score = MathF.Abs(SceneDefinition.AeroHeight(x, z) - (SceneDefinition.WaterLevel + 0.25f));
                        if (score < bestScore) { bestScore = score; best = x; }
                    }
                    if (bestScore < 0.7f) return (best + Jitter(rng, 1.4f), z);
                }
                return (0f, -60f);
            }

            // Escala por ALTURA DESEADA, en metros (ver AeroAssets.ScaleFor): el factor sale de la caja
            // REAL del modelo. Escalar "×8" a ojo fue el defecto que convertía una planta rastrera de
            // 0,17 m en una alfombra de cuatro metros que tapaba el cuadro.
            float Scale(string slug, float meters) => AeroAssets.ScaleFor(assetRoot, slug, meters);

            // Los árboles del set miden 3,41 m: "Tree(1)" es un árbol de 6,2 m, que es la altura de un
            // árbol de verdad al lado del paseo.
            const float treeHeight = 6.20f;
            float Tree(float relative) => Scale("island_tree_02", treeHeight * relative);

            // ---- Árboles: cuatro que enmarcan el cuadro (los mismos lugares que la versión
            // procedural) y dos a media distancia. El modelo mide 3,4 m, así que la escala los lleva a
            // 5-8 m: son árboles de verdad. Cada uno pesa 874k vértices, por eso son pocos y por eso el
            // resto del verde va con matas (ver abajo).
            heroTrees.AddRange(new[]
            {
                Grounded(SceneDefinition.AeroFlightX(-66f) - 34f, -66f, Tree(1.00f), 0.6f, SceneDefinition.KindTree, 0.2f),
                Grounded(SceneDefinition.AeroFlightX(-122f) + 30f, -122f, Tree(0.92f), 2.4f, SceneDefinition.KindTree, 0.1f),
                Grounded(SceneDefinition.AeroFlightX(-98f) + 52f, -98f, Tree(1.05f), 4.1f, SceneDefinition.KindTree, 0.9f),
                Grounded(SceneDefinition.AeroFlightX(-30f) - 44f, -30f, Tree(0.88f), 5.5f, SceneDefinition.KindTree, 0.6f),
                Grounded(SceneDefinition.AeroFlightX(66f) + 38f, 66f, Tree(0.95f), 1.3f, SceneDefinition.KindTree, 0.4f),
                Grounded(SceneDefinition.AeroFlightX(180f) - 30f, 180f, Tree(0.90f), 3.0f, SceneDefinition.KindTree, 0.8f)
            });
            AeroAssets.AddModel(modeled, assetRoot, "island_tree_02", "árbol", heroTrees);

            // ---- Arboleda del horizonte: mata densa ESCALADA (0,54 m × 9-15). A 400 metros lee como
            // línea de árboles y cuesta 30 veces menos que repetir la fotogrametría del árbol.
            var grove = new List<SceneInstance>(44);
            for (int i = 0; i < 40; i++)
            {
                float x = -470f + i * 24f + Jitter(rng, 7f);
                float z = 388f + Jitter(rng, 26f);
                grove.Add(Grounded(x, z, Scale("wild_rooibos_bush", 6.50f) * (0.85f + (float)rng.NextDouble() * 0.4f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindTree, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "wild_rooibos_bush", "arboleda", grove);

            // ---- Pasto real: matas de dos tamaños sobre la banda del prado. Van por delante de las
            // briznas procedurales: de cerca se ve la mata texturada y de lejos manda la alfombra.
            var coarseTufts = new List<SceneInstance>(160);
            for (int i = 0; i < 160; i++)
            {
                var (x, z) = Meadow();
                coarseTufts.Add(Grounded(x, z, Scale("grass_medium_02", 0.80f) * (0.8f + (float)rng.NextDouble() * 0.5f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindGrassBlade, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "grass_medium_02", "pasto", coarseTufts);

            var fineTufts = new List<SceneInstance>(320);
            for (int i = 0; i < 320; i++)
            {
                var (x, z) = Meadow();
                fineTufts.Add(Grounded(x, z, Scale("grass_bermuda_01", 0.42f) * (0.9f + (float)rng.NextDouble() * 0.7f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindGrassBlade, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "grass_bermuda_01", "pasto fino", fineTufts);

            // ---- Flores reales: los mismos colores fuertes de la postal (naranja y amarillo) pero con
            // geometría de planta de verdad. Las chicas (0,17-0,27 m) van escaladas: sin eso no se ven
            // desde la distancia a la que pasa la cámara.
            var gazanias = new List<SceneInstance>(20);
            for (int i = 0; i < 20; i++)
            {
                var (x, z) = Meadow();
                gazanias.Add(Grounded(x, z, Scale("flower_gazania", 1.10f) * (0.75f + (float)rng.NextDouble() * 0.5f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindSunflower, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "flower_gazania", "gazania", gazanias);

            var ursinias = new List<SceneInstance>(20);
            for (int i = 0; i < 20; i++)
            {
                var (x, z) = Meadow();
                ursinias.Add(Grounded(x, z, Scale("flower_ursinia", 0.34f) * (0.8f + (float)rng.NextDouble() * 0.6f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindSunflower, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "flower_ursinia", "ursinia", ursinias);

            var empodiums = new List<SceneInstance>(60);
            for (int i = 0; i < 60; i++)
            {
                var (x, z) = Meadow();
                empodiums.Add(Grounded(x, z, Scale("flower_empodium", 0.30f) * (0.8f + (float)rng.NextDouble() * 0.6f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindRose, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "flower_empodium", "empodium", empodiums);

            // ---- Matas y arbustos: el cuerpo medio del prado (lo que el césped no llena) y la
            // transición hacia la orilla.
            var bigShrubs = new List<SceneInstance>(10);
            for (int i = 0; i < 10; i++)
            {
                var (x, z) = Meadow();
                bigShrubs.Add(Grounded(x, z, Scale("shrub_02", 1.60f) * (0.8f + (float)rng.NextDouble() * 0.5f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindTree, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "shrub_02", "arbusto", bigShrubs);

            var midShrubs = new List<SceneInstance>(40);
            for (int i = 0; i < 24; i++)
            {
                var (x, z) = Meadow();
                midShrubs.Add(Grounded(x, z, Scale("shrub_03", 0.80f) * (0.8f + (float)rng.NextDouble() * 0.6f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindTree, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "shrub_03", "mata", midShrubs);

            var lowShrubs = new List<SceneInstance>(16);
            for (int i = 0; i < 16; i++)
            {
                var (x, z) = Meadow();
                lowShrubs.Add(Grounded(x, z, Scale("shrub_04", 0.50f) * (0.8f + (float)rng.NextDouble() * 0.6f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindTree, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "shrub_04", "mata baja", lowShrubs);

            var nettles = new List<SceneInstance>(10);
            for (int i = 0; i < 8; i++)
            {
                var (x, z) = Meadow();
                nettles.Add(Grounded(x, z, Scale("nettle_plant", 0.72f) * (0.8f + (float)rng.NextDouble() * 0.6f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindRose, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "nettle_plant", "ortiga", nettles);

            var periwinkles = new List<SceneInstance>(10);
            for (int i = 0; i < 8; i++)
            {
                var (x, z) = Meadow();
                periwinkles.Add(Grounded(x, z, Scale("periwinkle_plant", 0.80f) * (0.8f + (float)rng.NextDouble() * 0.6f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindRose, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "periwinkle_plant", "vinca", periwinkles);

            // ---- Rocas: la orilla del lago y el cauce. Es lo que le da el borde de piedra al agua en
            // vez del corte limpio del campo de altura.
            var boulders = new List<SceneInstance>(12);
            for (int i = 0; i < 8; i++)
            {
                var (x, z) = Shore();
                boulders.Add(Grounded(x, z, Scale("boulder_01", 1.30f) * (0.8f + (float)rng.NextDouble() * 1.1f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindTextured, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "boulder_01", "peñasco", boulders);

            var mossyRocks = new List<SceneInstance>(10);
            for (int i = 0; i < 6; i++)
            {
                var (x, z) = Shore();
                mossyRocks.Add(Grounded(x, z, Scale("rock_moss_set_01", 1.00f) * (0.7f + (float)rng.NextDouble() * 0.8f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindTextured, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "rock_moss_set_01", "roca con musgo", mossyRocks);

            var smallRocks = new List<SceneInstance>(14);
            for (int i = 0; i < 12; i++)
            {
                var (x, z) = i % 2 == 0 ? Shore() : Meadow();
                smallRocks.Add(Grounded(x, z, Scale("rock_07", 0.55f) * (0.8f + (float)rng.NextDouble() * 1.5f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindTextured, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "rock_07", "piedra", smallRocks);

            // ---- Mobiliario del paseo: la fila de faroles acompaña a la cámara en el tramo del prado
            // (misma curva del cauce que usa la cámara para no quedar dentro del agua) y los bancos y
            // macetas caen en el prado, que es donde se pasea.
            var lamps = new List<SceneInstance>(10);
            for (int i = 0; i < 8; i++)
            {
                float z = -148f + i * 26f;
                float x = LandX(SceneDefinition.AeroFlightX(z) + 34f + 5f * MathF.Sin(i * 1.7f), z);
                lamps.Add(Grounded(x, z, Scale("street_lamp_01", 4.10f), 1.4f + i * 0.35f, SceneDefinition.KindTree, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "street_lamp_01", "farol del paseo", lamps);

            var benches = new List<SceneInstance>(8);
            for (int i = 0; i < 6; i++)
            {
                var (x, z) = Meadow();
                benches.Add(Grounded(x, z, Scale("painted_wooden_bench", 0.90f), (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindHouse, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "painted_wooden_bench", "banco", benches);

            var planterBoxes = new List<SceneInstance>(8);
            for (int i = 0; i < 8; i++)
            {
                var (x, z) = Meadow();
                planterBoxes.Add(Grounded(x, z, Scale("planter_box_03", 0.85f), (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindHouse, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "planter_box_03", "jardinera", planterBoxes);

            var pots = new List<SceneInstance>(10);
            for (int i = 0; i < 10; i++)
            {
                var (x, z) = Meadow();
                pots.Add(Grounded(x, z, Scale("planter_pot_clay", 0.42f) * (0.9f + (float)rng.NextDouble() * 0.6f),
                    (float)(rng.NextDouble() * MathF.Tau), SceneDefinition.KindHouse, (float)rng.NextDouble()));
            }
            AeroAssets.AddModel(modeled, assetRoot, "planter_pot_clay", "maceta", pots);
        }

        // ---- Casters de sombra: los objetos GRANDES y CERCANOS, que son los que dejan una sombra
        // que se ve. El shader hace UN rayo-esfera por caster contra el sol (ver ObjectShadow), así
        // que la lista se arma por tamaño y distancia a la cámara, no por orden de creación: los
        // árboles que enmarcan el cuadro van primero (los de la línea del fondo no proyectan nada
        // útil sobre el pasto que se mira).
        var casters = new List<Vector4>(SceneDefinition.MaxShadowCasters);
        // Los árboles del primer plano son los MODELOS del set cuando está (el de fotogrametría es más
        // alto y más ancho que el facetado, así que la esfera del caster tiene que medir lo suyo); sin
        // set, los procedurales de siempre.
        var foregroundTrees = heroTrees.Count > 0 ? heroTrees : treeInstances.Skip(treeInstances.Count - 4).ToList();
        foreach (var tree in foregroundTrees)
        {
            float treeScale = tree.PositionScale.W;
            casters.Add(new Vector4(
                tree.PositionScale.X,
                tree.PositionScale.Y + 1.05f * treeScale,
                tree.PositionScale.Z,
                0.85f * treeScale));
        }
        foreach (var flower in sunflowerInstances)
        {
            float flowerScale = flower.PositionScale.W;
            casters.Add(new Vector4(
                flower.PositionScale.X,
                flower.PositionScale.Y + 1.30f * flowerScale,
                flower.PositionScale.Z,
                0.45f * flowerScale));
        }
        foreach (var flower in roseInstances)
        {
            float flowerScale = flower.PositionScale.W;
            casters.Add(new Vector4(
                flower.PositionScale.X,
                flower.PositionScale.Y + 1.20f * flowerScale,
                flower.PositionScale.Z,
                0.42f * flowerScale));
        }

        return new SceneDefinition
        {
            Id = "aero",
            Name = "Frutiger Aero (en desarrollo)",
            Description = "El paseo de la postal: la cámara gira por la cascada y el arroyo del desagüe, cruza el campo estilo XP con su sendero de tierra, sobrevuela el lago hondo y termina frente a la ciudad —torres de vidrio con losa, casas con balcones y locales con toldo—. El prado, la orilla y el paseo están poblados con MODELOS REALES de fotogrametría (árboles, matas, pasto, flores, rocas, bancos y faroles, cada uno con su material PBR) y el aire lleva partículas con sprite: polen fino, hojas y pétalos girando y semillas con penacho. Cielo, agua y rápidos, burbujas transparentes a la deriva y sombras de primer plano. Mide relleno por píxel, material por material y muchos objetos instanciados.",
            InDevelopment = true,
            Meshes = new[]
            {
                new SceneMesh { Name = "pasto", Vertices = ground, Instances = new[] { GroundInstance() } },
                new SceneMesh { Name = "briznas", Vertices = grass, Instances = grassInstances.ToArray() },
                new SceneMesh { Name = "torres", Vertices = towers, Instances = officeInstances.ToArray() },
                new SceneMesh { Name = "casas", Vertices = houses, Instances = houseInstances.ToArray() },
                new SceneMesh { Name = "locales", Vertices = shops, Instances = shopInstances.ToArray() },
                new SceneMesh { Name = "árboles", Vertices = trees, Instances = treeInstances.ToArray() },
                new SceneMesh { Name = "girasoles", Vertices = sunflowers, Instances = sunflowerInstances.ToArray() },
                new SceneMesh { Name = "rosas", Vertices = roses, Instances = roseInstances.ToArray() },
                new SceneMesh { Name = "flores del prado", Vertices = sunflowers, Instances = meadowInstances.ToArray() },
                new SceneMesh { Name = "gaviotas", Vertices = gulls, Instances = gullInstances.ToArray() },
                new SceneMesh { Name = "globos", Vertices = balloons, Instances = balloonInstances.ToArray() },
                new SceneMesh { Name = "mariposas", Vertices = butterflies, Instances = butterflyInstances.ToArray() },
            }
            // Los modelos REALES de fotogrametría del set 'aero' (ver AeroAssets): árboles, matas,
            // pasto, flores, rocas, bancos y faroles, cada uno con su material PBR. Entran DESPUÉS de
            // lo procedural —la postal mantiene sus girasoles, sus rosas y sus briznas— y ANTES de lo
            // transparente, que es el orden obligatorio del camino con mezcla. Si el set falta, la
            // lista viene vacía y la escena es la de siempre (el motivo viaja en AeroAssets.Notes).
            .Concat(modeled)
            // ÚLTIMAS y con mezcla: las únicas mallas transparentes van después de todo lo opaco.
            .Concat(new[]
            {
                new SceneMesh { Name = "burbujas", Vertices = bubbles, Instances = bubbleInstances.ToArray(), Blend = true },
                new SceneMesh { Name = "partículas", Vertices = pollen, Instances = pollenInstances.ToArray(), Blend = true }
            })
            .ToArray(),
            // El sol va ALTO y adelante, a un costado (como en la postal): azul arriba, resplandor
            // adentro del cuadro arriba a la derecha y el reflejo del agua apuntando al ojo.
            LightDirection = Vector3.Normalize(new Vector3(-0.30f, -0.36f, -0.88f)),
            CameraPath = SceneDefinition.AeroCamera,
            // El look del mundo Aero vive entero en su archivo: nada de esto lo comparte con el corredor.
            Style = Styles.AeroStyle.Default,
            ShadowCasters = casters,
            FarPlane = 14000f,
            WarmupSeconds = 2.5
        };
    }

    // ---- Ayudantes de colocación (dejan las listas de instancias legibles) ----

    /// <summary>Instancia del suelo: posición y orientación no se usan, el shader la sigue sola.</summary>
    private static SceneInstance GroundInstance() =>
        new(new Vector4(0f, 0f, 0f, 1f), new Vector4(0f, 0f, SceneDefinition.KindAeroGround, 0f));

    /// <summary>Burbuja a la deriva: <paramref name="phase"/> desfasa el vaivén vertical.</summary>
    private static SceneInstance Bubble(float x, float y, float z, float scale, float phase) =>
        new(new Vector4(x, y, z, scale), new Vector4(0f, phase, SceneDefinition.KindBubble, 0f));

    /// <summary>
    /// Brizna de pasto apoyada en el suelo. <paramref name="phase"/> desfasa el viento (si todas
    /// soplaran juntas el prado se movería como una manta) y la base se hunde un poco para que el
    /// borde no se despegue en las lomas.
    /// </summary>
    private static SceneInstance Grass(float x, float z, float scale, float yaw, float phase, float variant) =>
        new(new Vector4(x, SceneDefinition.AeroHeight(x, z) - 0.18f, z, scale),
            new Vector4(yaw, phase, SceneDefinition.KindGrassBlade, variant));

    /// <summary>
    /// Partícula de sprite (polen, hoja, semilla o pétalo): flota en el aire, el shader la orienta
    /// hacia la cámara y la hace girar sobre su propio eje. El DIBUJO viaja en la tinta del material
    /// —x la celda del atlas, y la velocidad del giro— porque la rama de partículas no tiene material
    /// de verdad: el tipo de malla (polen) ya dice lo que hay que hacer.
    /// </summary>
    private static SceneInstance Particle(float x, float y, float z, float scale, float phase, float sprite, float spin) =>
        new(new Vector4(x, y, z, scale),
            new Vector4(0f, phase, SceneDefinition.KindPollen, 0f),
            new Vector4(sprite, spin, phase, 0f));

    /// <summary>Modelo apoyado EN EL SUELO: el shader no conoce el campo de altura, así que la y la
    /// resuelve el C# (por eso <c>AeroHeight</c> vive también acá). El TIPO va explícito: árboles,
    /// girasoles y rosas comparten el mismo ayudante.</summary>
    private static SceneInstance Grounded(float x, float z, float scale, float yaw, float kind, float variant) =>
        new(new Vector4(x, SceneDefinition.AeroHeight(x, z), z, scale),
            new Vector4(yaw, 0f, kind, variant));

    /// <summary>Modelo apoyado en el suelo con un desplazamiento de altura (mariposas, que vuelan bajas).</summary>
    private static SceneInstance GroundedOffset(float x, float z, float lift, float scale, float yaw) =>
        new(new Vector4(x, SceneDefinition.AeroHeight(x, z) + lift, z, scale),
            new Vector4(yaw, 0f, SceneDefinition.KindButterfly, 0f));

    /// <summary>Modelo en el aire: la y es absoluta (gaviotas, globos, emblema).</summary>
    private static SceneInstance Sky(float x, float y, float z, float scale, float yaw, float kind) =>
        new(new Vector4(x, y, z, scale), new Vector4(yaw, 0f, kind, 0f));

    /// <summary>Modelo colocado genérico (torres): yaw y tipo explícitos.</summary>
    private static SceneInstance Placed(float x, float z, float height, float yaw, float kind, float variant) =>
        new(new Vector4(x, SceneDefinition.AeroHeight(x, z) - 2f, z, height),
            new Vector4(yaw, 0f, kind, variant));

    // =====================================================================
    // Modelos facetados del mundo Aero
    // =====================================================================
    //
    // Todos son mallas SIN ÍNDICES con la normal PLANAR por cara, como el resto del componente: el
    // look facetado es del motor (y el culling está apagado en las cuatro APIs, así que el orden de
    // los vértices no decide qué se ve; las normales se orientan hacia afuera del centro que se
    // indique). La escala la pone la INSTANCIA y es uniforme: la proporción (torre alta y angosta,
    // girasol con tallo) tiene que venir en la malla.
    //
    // Los modelos se apoyan en y = 0 (el suelo) o se centran en el origen (burbujas, gaviotas): el
    // vertex shader solo aplica giro en Y, escala y traslación.

    /// <summary>Esfera facetada de radio 1 (burbujas). <paramref name="squashY"/> la achata.</summary>
    private static SceneVertex[] BuildSphereMesh(int segments, int rings, float squashY = 1f, bool smooth = true)
    {
        var vertices = new List<SceneVertex>(segments * rings * 6);
        AppendSphere(vertices, Vector3.Zero, 1f, squashY, segments, rings, smooth);
        return vertices.ToArray();
    }

    /// <summary>
    /// Torre de vidrio: prisma de 6 caras con cinturón de remate, corona y aguja. Es ALTA y ANGOSTA
    /// a propósito (la escala de la instancia es uniforme: con un cubo, escalar daría torres
    /// cuadradas). El parámetro de la instancia elige el tinte, así que el skyline no es un peine
    /// de torres idénticas.
    /// </summary>
    private static SceneVertex[] BuildTowerMesh(float halfWidth)
    {
        // Anillos de abajo hacia arriba: fuste y CORONA PLANA (losa de techo con parapeto, como
        // un edificio de oficinas de verdad). Ni aguja que afina ni punta: eso era lo que hacía
        // que todas terminaran igual en un pico irreal.
        (float Y, float Width)[] rings =
        {
            (0.00f, 1.00f), (0.55f, 0.96f), (0.92f, 0.96f), (0.92f, 1.02f), (1.00f, 1.02f), (1.00f, 0.86f)
        };
        const int sides = 6;
        var vertices = new List<SceneVertex>(sides * (rings.Length - 1) * 6 + sides * 6);

        for (int r = 0; r < rings.Length - 1; r++)
        {
            var (y0, w0) = rings[r];
            var (y1, w1) = rings[r + 1];
            for (int i = 0; i < sides; i++)
            {
                float a0 = MathF.Tau * i / sides;
                float a1 = MathF.Tau * (i + 1) / sides;
                var p0 = new Vector3(MathF.Sin(a0) * halfWidth * w0, y0, MathF.Cos(a0) * halfWidth * w0);
                var p1 = new Vector3(MathF.Sin(a1) * halfWidth * w0, y0, MathF.Cos(a1) * halfWidth * w0);
                var q0 = new Vector3(MathF.Sin(a0) * halfWidth * w1, y1, MathF.Cos(a0) * halfWidth * w1);
                var q1 = new Vector3(MathF.Sin(a1) * halfWidth * w1, y1, MathF.Cos(a1) * halfWidth * w1);
                // El "centro" del que sale la normal es el eje de la torre a la altura de la cara.
                AddQuad(vertices, p0, p1, q1, q0, new Vector3(0f, (y0 + y1) * 0.5f, 0f));
            }
        }

        AppendDisc(vertices, new Vector3(0f, rings[^1].Y, 0f), halfWidth * rings[^1].Width, sides, Vector3.UnitY);
        AppendDisc(vertices, Vector3.Zero, halfWidth, sides, -Vector3.UnitY);
        return vertices.ToArray();
    }

    /// <summary>
    /// Brizna de pasto: DOS cuadriláteros cruzados, con la punta más fina que la base. Un solo
    /// cuadro desaparece cuando el ojo lo mira de canto; el cruce se ve desde cualquier lado y
    /// cuesta 12 vértices (4 triángulos). La y local va de 0 (base) a 1 (punta): el shader la usa
    /// para doblar la brizna con el viento y la escala de la instancia pone la altura real.
    /// </summary>
    private static SceneVertex[] BuildGrassBladeMesh()
    {
        var vertices = new List<SceneVertex>(12);
        foreach (float angle in new[] { 0f, MathF.PI * 0.5f })
        {
            float dx = MathF.Cos(angle) * 0.17f;
            float dz = MathF.Sin(angle) * 0.17f;
            // El "centro" del que sale la normal es la BASE de la brizna: así la cara que mira al
            // sol se ilumina y la de atrás queda en sombra (el pixel shader además corrige la
            // normal contra la vista, porque la brizna no tiene espesor).
            AddQuad(vertices,
                new Vector3(-dx, 0f, -dz),
                new Vector3(dx, 0f, dz),
                new Vector3(dx * 0.20f, 1f, dz * 0.20f),
                new Vector3(-dx * 0.20f, 1f, -dz * 0.20f),
                Vector3.Zero);
        }

        return vertices.ToArray();
    }

    /// <summary>
    /// Mota de polen: un cuadro de 1×1 centrado en el origen, en el plano XY. El vertex shader lo
    /// orienta hacia la cámara y lo escala con la instancia; el pixel shader le da el borde redondo
    /// y lo entrega PREMULTIPLICADO (la malla se dibuja con mezcla).
    /// </summary>
    /// <summary>
    /// Cuadro de partícula: 1×1 en [-1,1]² y con la UV 0..1, que es lo que permite muestrear el
    /// SPRITE de la celda del atlas. La normal mira a -z solo como referencia: el vertex shader la
    /// reescribe hacia la cámara (el cuadro se arma en el shader, no en la malla).
    /// </summary>
    private static SceneVertex[] BuildParticleQuadMesh()
    {
        var normal = new Vector3(0f, 0f, -1f);
        var corners = new (Vector3 Position, Vector2 Uv)[]
        {
            (new Vector3(-1f, -1f, 0f), new Vector2(0f, 0f)),
            (new Vector3(1f, -1f, 0f), new Vector2(1f, 0f)),
            (new Vector3(1f, 1f, 0f), new Vector2(1f, 1f)),
            (new Vector3(-1f, 1f, 0f), new Vector2(0f, 1f))
        };
        int[] order = { 0, 1, 2, 0, 2, 3 };

        var vertices = new SceneVertex[6];
        for (int i = 0; i < order.Length; i++)
        {
            var corner = corners[order[i]];
            vertices[i] = new SceneVertex(corner.Position, normal, corner.Uv);
        }
        return vertices;
    }

    /// <summary>Árbol: tronco y copa de varias bolas (la línea del fondo y el árbol del primer plano).</summary>
    private static SceneVertex[] BuildTreeMesh()
    {
        var vertices = new List<SceneVertex>(256);
        AppendPrism(vertices, Vector3.Zero, halfWidth: 0.075f, height: 0.52f, sides: 5);
        // Anillos de más y normal radial: la copa tiene que leerse como follaje redondo, no como una
        // bola de espejos.
        AppendSphere(vertices, new Vector3(0f, 1.00f, 0f), 0.62f, 0.80f, 20, 14, smooth: true);
        AppendSphere(vertices, new Vector3(0.20f, 1.38f, -0.12f), 0.40f, 0.84f, 16, 10, smooth: true);
        AppendSphere(vertices, new Vector3(-0.22f, 1.30f, 0.14f), 0.37f, 0.82f, 16, 10, smooth: true);
        return vertices.ToArray();
    }

    /// <summary>Girasol: tallo, pétalos y disco central. El disco mira hacia -z (al ojo de la cámara).</summary>
    private static SceneVertex[] BuildSunflowerMesh()
    {
        var vertices = new List<SceneVertex>(256);
        AppendPrism(vertices, Vector3.Zero, halfWidth: 0.026f, height: 1.50f, sides: 5);
        var head = new Vector3(0f, 1.54f, 0f);
        const int petals = 14;
        for (int i = 0; i < petals; i++)
        {
            float a0 = MathF.Tau * i / petals;
            float a1 = MathF.Tau * (i + 1) / petals;
            float am = (a0 + a1) * 0.5f;
            var innerA = head + new Vector3(MathF.Cos(a0) * 0.12f, MathF.Sin(a0) * 0.12f, -0.03f);
            var innerB = head + new Vector3(MathF.Cos(a1) * 0.12f, MathF.Sin(a1) * 0.12f, -0.03f);
            var tip = head + new Vector3(MathF.Cos(am) * 0.40f, MathF.Sin(am) * 0.40f, -0.05f);
            AddTriangleWithNormal(vertices, innerA, innerB, tip, -Vector3.UnitZ);
        }
        AppendDisc(vertices, head + new Vector3(0f, 0f, -0.045f), 0.155f, 12, -Vector3.UnitZ);
        return vertices.ToArray();
    }

    /// <summary>Rosa: tallo y capullo envuelto en pétalos.</summary>
    private static SceneVertex[] BuildRoseMesh()
    {
        var vertices = new List<SceneVertex>(256);
        AppendPrism(vertices, Vector3.Zero, halfWidth: 0.030f, height: 1.00f, sides: 5);
        var bud = new Vector3(0f, 1.12f, 0f);
        AppendSphere(vertices, bud, 0.19f, 1.26f, 16, 12, smooth: true);
        const int petals = 9;
        for (int i = 0; i < petals; i++)
        {
            float a0 = MathF.Tau * i / petals;
            float a1 = MathF.Tau * (i + 1) / petals;
            float am = (a0 + a1) * 0.5f;
            var innerA = bud + new Vector3(MathF.Cos(a0) * 0.14f, 0.05f, MathF.Sin(a0) * 0.14f);
            var innerB = bud + new Vector3(MathF.Cos(a1) * 0.14f, 0.05f, MathF.Sin(a1) * 0.14f);
            var tip = bud + new Vector3(MathF.Cos(am) * 0.30f, 0.17f, MathF.Sin(am) * 0.30f);
            AddTriangleWithNormal(vertices, innerA, innerB, tip, -Vector3.UnitZ);
        }
        return vertices.ToArray();
    }

    /// <summary>Gaviota: cuerpo chato y dos alas en V (dos triángulos por ala).</summary>
    private static SceneVertex[] BuildGullMesh()
    {
        var vertices = new List<SceneVertex>(96);
        AppendBox(vertices, Vector3.Zero, new Vector3(0.045f, 0.032f, 0.24f));
        AppendBox(vertices, new Vector3(0f, 0.03f, 0.26f), new Vector3(0.035f, 0.035f, 0.045f));
        foreach (float sign in new[] { -1f, 1f })
        {
            var rootFront = new Vector3(sign * 0.04f, 0.02f, 0.12f);
            var rootBack = new Vector3(sign * 0.04f, 0.01f, -0.18f);
            var tipFront = new Vector3(sign * 0.54f, 0.11f, -0.02f);
            var tipBack = new Vector3(sign * 0.54f, 0.10f, -0.16f);
            AddTriangleWithNormal(vertices, rootFront, tipFront, tipBack, Vector3.UnitY);
            AddTriangleWithNormal(vertices, rootFront, tipBack, rootBack, Vector3.UnitY);
        }
        AddTriangleWithNormal(vertices,
            new Vector3(-0.10f, 0.02f, -0.22f),
            new Vector3(0.10f, 0.02f, -0.22f),
            new Vector3(0f, 0.03f, -0.42f), Vector3.UnitY);
        return vertices.ToArray();
    }

    /// <summary>Globo aerostático: envelope esférico y canasta colgando.</summary>
    private static SceneVertex[] BuildBalloonMesh()
    {
        var vertices = new List<SceneVertex>(512);
        AppendSphere(vertices, new Vector3(0f, 1.42f, 0f), 0.48f, 1.22f, 24, 16, smooth: true);
        AppendBox(vertices, new Vector3(0f, 0.12f, 0f), new Vector3(0.13f, 0.12f, 0.13f));
        return vertices.ToArray();
    }

    /// <summary>Mariposa: cuerpo fino y cuatro alas (el vertex shader las hace aletear).</summary>
    private static SceneVertex[] BuildButterflyMesh()
    {
        var vertices = new List<SceneVertex>(96);
        AppendBox(vertices, Vector3.Zero, new Vector3(0.022f, 0.022f, 0.16f));
        foreach (float sign in new[] { -1f, 1f })
        {
            var frontInner = new Vector3(sign * 0.02f, 0.01f, 0.06f);
            var frontOuter = new Vector3(sign * 0.34f, 0.07f, 0.16f);
            var frontBack = new Vector3(sign * 0.30f, 0.05f, -0.02f);
            AddTriangleWithNormal(vertices, frontInner, frontOuter, frontBack, Vector3.UnitY);

            var rearInner = new Vector3(sign * 0.02f, 0.00f, -0.04f);
            var rearOuter = new Vector3(sign * 0.24f, 0.05f, -0.18f);
            var rearTip = new Vector3(sign * 0.09f, 0.02f, -0.24f);
            AddTriangleWithNormal(vertices, rearInner, rearOuter, rearTip, Vector3.UnitY);
        }
        return vertices.ToArray();
    }

    // ---- Ladrillos de malla (todos con normal plana por cara) ----

    private static void AddTriangle(List<SceneVertex> vertices, Vector3 a, Vector3 b, Vector3 c, Vector3 outwardFrom)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        var centroid = (a + b + c) / 3f;
        if (Vector3.Dot(normal, centroid - outwardFrom) < 0f) normal = -normal;
        AddTriangleWithNormal(vertices, a, b, c, normal);
    }

    private static void AddTriangleWithNormal(List<SceneVertex> vertices, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        vertices.Add(new SceneVertex(a, normal));
        vertices.Add(new SceneVertex(b, normal));
        vertices.Add(new SceneVertex(c, normal));
    }

    /// <summary>
    /// Casa urbana con BALCONES: un prisma con retranqueos por piso (la moldedura le da el balcón
    /// real: los pisos sobresalen y el vidrio entra) y losa plana con parapeto. El PS distingue
    /// pared, vidrio de balcón, baranda y techo por la altura local.
    /// </summary>
    private static SceneVertex[] BuildHouseMesh()
    {
        const int sides = 4;
        (float Y, float Width)[] rings =
        {
            (0.00f, 1.00f), (0.18f, 1.06f), (0.20f, 0.94f),   // planta: zócalo + balcón saliente
            (0.38f, 0.94f), (0.40f, 1.06f), (0.58f, 0.94f),   // piso 2
            (0.60f, 1.06f), (0.78f, 0.94f), (0.80f, 1.06f),   // piso 3
            (0.98f, 0.94f), (1.00f, 1.02f), (1.00f, 0.80f)    // coronamiento + losa
        };
        var vertices = new List<SceneVertex>(sides * (rings.Length - 1) * 6 + sides * 6);
        const float halfWidth = 0.11f;

        for (int r = 0; r < rings.Length - 1; r++)
        {
            var (y0, w0) = rings[r];
            var (y1, w1) = rings[r + 1];
            for (int i = 0; i < sides; i++)
            {
                float a0 = MathF.Tau * i / sides;
                float a1 = MathF.Tau * (i + 1) / sides;
                var p0 = new Vector3(MathF.Sin(a0) * halfWidth * w0, y0, MathF.Cos(a0) * halfWidth * w0);
                var p1 = new Vector3(MathF.Sin(a1) * halfWidth * w0, y0, MathF.Cos(a1) * halfWidth * w0);
                var q0 = new Vector3(MathF.Sin(a0) * halfWidth * w1, y1, MathF.Cos(a0) * halfWidth * w1);
                var q1 = new Vector3(MathF.Sin(a1) * halfWidth * w1, y1, MathF.Cos(a1) * halfWidth * w1);
                AddQuad(vertices, p0, p1, q1, q0, new Vector3(0f, (y0 + y1) * 0.5f, 0f));
            }
        }

        AppendDisc(vertices, new Vector3(0f, rings[^1].Y, 0f), halfWidth * rings[^1].Width, sides, Vector3.UnitY);
        AppendDisc(vertices, Vector3.Zero, halfWidth, sides, -Vector3.UnitY);
        return vertices.ToArray();
    }

    /// <summary>
    /// Local comercial: caja baja de 2 pisos con CARTOUCHE (banda alta de vidrio en la planta
    /// baja —el escaparate—) y toldo (marquesina inclinada) sobre la vereda. El PS pinta la
    /// banda superior como cartel y el vidrio de abajo como escaparate.
    /// </summary>
    private static SceneVertex[] BuildShopMesh()
    {
        const int sides = 4;
        var vertices = new List<SceneVertex>(sides * 5 * 6 + sides * 6 + 24);
        const float halfWidth = 0.13f;

        // Caja principal: dos alturas (el PS pinta la planta baja como vidrio de local).
        (float Y, float Width)[] rings =
        {
            (0.00f, 1.00f), (0.52f, 1.00f), (0.56f, 0.96f), (1.00f, 0.96f), (1.00f, 0.84f)
        };
        for (int r = 0; r < rings.Length - 1; r++)
        {
            var (y0, w0) = rings[r];
            var (y1, w1) = rings[r + 1];
            for (int i = 0; i < sides; i++)
            {
                float a0 = MathF.Tau * i / sides;
                float a1 = MathF.Tau * (i + 1) / sides;
                var p0 = new Vector3(MathF.Sin(a0) * halfWidth * w0, y0, MathF.Cos(a0) * halfWidth * w0);
                var p1 = new Vector3(MathF.Sin(a1) * halfWidth * w0, y0, MathF.Cos(a1) * halfWidth * w0);
                var q0 = new Vector3(MathF.Sin(a0) * halfWidth * w1, y1, MathF.Cos(a0) * halfWidth * w1);
                var q1 = new Vector3(MathF.Sin(a1) * halfWidth * w1, y1, MathF.Cos(a1) * halfWidth * w1);
                AddQuad(vertices, p0, p1, q1, q0, new Vector3(0f, (y0 + y1) * 0.5f, 0f));
            }
        }
        AppendDisc(vertices, new Vector3(0f, rings[^1].Y, 0f), halfWidth * rings[^1].Width, sides, Vector3.UnitY);
        AppendDisc(vertices, Vector3.Zero, halfWidth, sides, -Vector3.UnitY);

        // Toldo: marquesina inclinada que sale 0,35 hacia afuera sobre la planta baja (y local
        // 0.30→0.42), un cuadro por lado con la normal hacia arriba-afuera.
        foreach (var (dir, side) in new[]
        {
            (new Vector3(1f, 0f, 0f), 0), (new Vector3(-1f, 0f, 0f), 1),
            (new Vector3(0f, 0f, 1f), 2), (new Vector3(0f, 0f, -1f), 3)
        })
        {
            var sideDir = side % 2 == 0 ? new Vector3(0f, 0f, 1f) : new Vector3(1f, 0f, 0f);
            var inner0 = dir * halfWidth * 1.02f + new Vector3(0f, 0.30f, 0f) - sideDir * halfWidth;
            var inner1 = dir * halfWidth * 1.02f + new Vector3(0f, 0.30f, 0f) + sideDir * halfWidth;
            var outer0 = (dir + sideDir * 0f) * (halfWidth * 1.02f + 0.35f) + new Vector3(0f, 0.42f, 0f) - sideDir * halfWidth * 0.9f;
            var outer1 = (dir + sideDir * 0f) * (halfWidth * 1.02f + 0.35f) + new Vector3(0f, 0.42f, 0f) + sideDir * halfWidth * 0.9f;
            AddQuad(vertices, inner0, inner1, outer1, outer0, new Vector3(dir.X, 0.8f, dir.Z));
        }

        return vertices.ToArray();
    }

    private static void AddQuad(List<SceneVertex> vertices, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outwardFrom)
    {
        AddTriangle(vertices, a, b, c, outwardFrom);
        AddTriangle(vertices, a, c, d, outwardFrom);
    }

    /// <summary>Normal radial de una esfera (la de la superficie, no la de la cara).</summary>
    private static Vector3 SphereNormal(Vector3 offset, float squashY)
    {
        // Al achatado en Y le corresponde dividir por squashY al cuadrado: es el gradiente de
        // x² + (y/squashY)² + z², o sea la normal de la elipsoide.
        float scale = MathF.Max(0.05f, squashY * squashY);
        return Vector3.Normalize(new Vector3(offset.X, offset.Y / scale, offset.Z));
    }

    private static void AddTriangleSmooth(List<SceneVertex> vertices, Vector3 a, Vector3 b, Vector3 c, Vector3 center, float squashY)
    {
        vertices.Add(new SceneVertex(a, SphereNormal(a - center, squashY)));
        vertices.Add(new SceneVertex(b, SphereNormal(b - center, squashY)));
        vertices.Add(new SceneVertex(c, SphereNormal(c - center, squashY)));
    }

    /// <summary>
    /// Esfera centrada en <paramref name="center"/> (radio con achatado en Y). Con <paramref name="smooth"/>
    /// la normal es la RADIAL y no la de la cara: una esfera de 20×12 caras con normal plana se lee
    /// como un dado facetado —en una pompa de jabón o en una copa de árbol eso es lo que delata que
    /// es una malla— y con la radial el contorno sigue siendo el mismo pero el sombreado es curvo.
    /// </summary>
    private static void AppendSphere(List<SceneVertex> vertices, Vector3 center, float radius, float squashY, int segments, int rings, bool smooth = false)
    {
        for (int r = 0; r < rings; r++)
        {
            float phi0 = MathF.PI * r / rings;
            float phi1 = MathF.PI * (r + 1) / rings;
            for (int s = 0; s < segments; s++)
            {
                float theta0 = MathF.Tau * s / segments;
                float theta1 = MathF.Tau * (s + 1) / segments;
                var p00 = SpherePoint(theta0, phi0, squashY) * radius + center;
                var p10 = SpherePoint(theta1, phi0, squashY) * radius + center;
                var p11 = SpherePoint(theta1, phi1, squashY) * radius + center;
                var p01 = SpherePoint(theta0, phi1, squashY) * radius + center;
                if (smooth)
                {
                    AddTriangleSmooth(vertices, p00, p10, p11, center, squashY);
                    AddTriangleSmooth(vertices, p00, p11, p01, center, squashY);
                }
                else
                {
                    AddTriangle(vertices, p00, p10, p11, center);
                    AddTriangle(vertices, p00, p11, p01, center);
                }
            }
        }
    }

    private static Vector3 SpherePoint(float theta, float phi, float squashY) =>
        new(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi) * squashY, MathF.Sin(phi) * MathF.Sin(theta));

    /// <summary>Prisma vertical (tallos y troncos) con la punta afinada y la tapa cerrada.</summary>
    private static void AppendPrism(List<SceneVertex> vertices, Vector3 baseCenter, float halfWidth, float height, int sides)
    {
        float top = height;
        float topWidth = halfWidth * 0.72f;
        for (int i = 0; i < sides; i++)
        {
            float a0 = MathF.Tau * i / sides;
            float a1 = MathF.Tau * (i + 1) / sides;
            var p0 = baseCenter + new Vector3(MathF.Sin(a0) * halfWidth, 0f, MathF.Cos(a0) * halfWidth);
            var p1 = baseCenter + new Vector3(MathF.Sin(a1) * halfWidth, 0f, MathF.Cos(a1) * halfWidth);
            var q0 = baseCenter + new Vector3(MathF.Sin(a0) * topWidth, top, MathF.Cos(a0) * topWidth);
            var q1 = baseCenter + new Vector3(MathF.Sin(a1) * topWidth, top, MathF.Cos(a1) * topWidth);
            AddQuad(vertices, p0, p1, q1, q0, baseCenter + new Vector3(0f, top * 0.5f, 0f));
        }
        AppendDisc(vertices, baseCenter + new Vector3(0f, top, 0f), topWidth, sides, Vector3.UnitY);
    }

    /// <summary>Disco (abanico) orientado según <paramref name="normal"/>.</summary>
    private static void AppendDisc(List<SceneVertex> vertices, Vector3 center, float radius, int sides, Vector3 normal)
    {
        // Dos ejes perpendiculares a la normal, para armar el anillo en su plano.
        var axis = normal.LengthSquared() > 0.5f ? Vector3.Normalize(normal) : Vector3.UnitY;
        var helper = MathF.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var u = Vector3.Normalize(Vector3.Cross(helper, axis));
        var v = Vector3.Normalize(Vector3.Cross(axis, u));

        for (int i = 0; i < sides; i++)
        {
            float a0 = MathF.Tau * i / sides;
            float a1 = MathF.Tau * (i + 1) / sides;
            var p0 = center + (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * radius;
            var p1 = center + (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * radius;
            AddTriangleWithNormal(vertices, center, p0, p1, axis);
        }
    }

    /// <summary>Caja (caras planas, normales hacia afuera) para cuerpos, canastas y alas de mariposa.</summary>
    private static void AppendBox(List<SceneVertex> vertices, Vector3 center, Vector3 halfExtents)
    {
        float x0 = center.X - halfExtents.X, x1 = center.X + halfExtents.X;
        float y0 = center.Y - halfExtents.Y, y1 = center.Y + halfExtents.Y;
        float z0 = center.Z - halfExtents.Z, z1 = center.Z + halfExtents.Z;

        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => AddQuad(vertices, a, b, c, d, center);
        Face(new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1));
        Face(new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0));
        Face(new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1));
        Face(new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0));
        Face(new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0));
        Face(new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1));
    }

    /// del plano [-1,1]²): detalle fino cerca de la cámara y cobertura de kilómetros al fondo,
    /// que es lo que hace que el terreno se estire hasta el horizonte sin pop.
    /// </summary>
    private static SceneVertex[] BuildTerrainGrid()
    {
        const int side = SceneDefinition.TerrainGridSide;
        var vertices = new List<SceneVertex>(side * side * 6);

        for (int ix = 0; ix < side; ix++)
        {
            float x0 = -1f + 2f * ix / side;
            float x1 = -1f + 2f * (ix + 1) / side;
            for (int iz = 0; iz < side; iz++)
            {
                float z0 = -1f + 2f * iz / side;
                float z1 = -1f + 2f * (iz + 1) / side;

                var p00 = new Vector3(x0, 0f, z0);
                var p10 = new Vector3(x1, 0f, z0);
                var p11 = new Vector3(x1, 0f, z1);
                var p01 = new Vector3(x0, 0f, z1);

                // Sin índices: cada cara tiene sus tres vértices. La normal la calcula el shader
                // con el campo de altura (la malla acá es un plano, la forma la da el terreno).
                vertices.Add(new SceneVertex(p00, Vector3.UnitY));
                vertices.Add(new SceneVertex(p10, Vector3.UnitY));
                vertices.Add(new SceneVertex(p11, Vector3.UnitY));
                vertices.Add(new SceneVertex(p00, Vector3.UnitY));
                vertices.Add(new SceneVertex(p11, Vector3.UnitY));
                vertices.Add(new SceneVertex(p01, Vector3.UnitY));
            }
        }

        return vertices.ToArray();
    }

    /// Escena 2 — Corredor infinito: vuelo hacia adelante sobre un terreno procedural, con
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
                        new Vector4(yaw, spin, SceneDefinition.KindCorridorRock, 0f)));
                }
            }
        }

        return new SceneDefinition
        {
            Id = "corredor",
            Name = "Corredor infinito (en desarrollo)",
            Description = "Vuelo hacia adelante sobre un terreno procedural con ~16.000 rocas facetadas de 1.280 caras: mide el trabajo de vértices y de relleno de la placa mientras el escenario avanza.",
            InDevelopment = true,
            Meshes = new[]
            {
                new SceneMesh { Name = "rocas", Vertices = vertices, Instances = instances.ToArray() }
            },
            CameraPath = CorridorCamera,
            // El look del corredor vive entero en su archivo (noche azul): es el ÚNICO lugar donde se
            // cambia su cielo, y Frutiger Aero tiene el suyo aparte.
            Style = Styles.CorridorStyle.Default
        };
    }

    /// <summary>
    /// Cámara del corredor: vuelo hacia adelante a <see cref="FlightSpeed"/> con una curva suave
    /// en X, siempre a una altura fija sobre el terreno, mirando un punto del recorrido que está
    /// adelante. Determinista: la misma corrida de la misma duración recorre el mismo camino.
    /// </summary>
    private static (Vector3 Eye, Vector3 Target) CorridorCamera(double seconds)
    {
        const double lookaheadSeconds = 2.4;   // ~53 unidades por delante: la curva se ve venir

        return (CorridorPath(seconds), CorridorPath(seconds + lookaheadSeconds));
    }

    private static Vector3 CorridorPath(double seconds)
    {
        float z = (float)(seconds * SceneDefinition.FlightSpeed);
        float x = 26f * MathF.Sin((float)seconds * 0.11f);
        float y = SceneDefinition.TerrainHeight(x, z) + 9f;
        return new Vector3(x, y, z);
    }

    private static float Jitter(Random rng, float amplitude) =>
        (float)(rng.NextDouble() * 2.0 - 1.0) * amplitude;

    /// <summary>
    /// Corre una semilla HACIA AFUERA del agua (radial, desde el centro del lago): si camina en X
    /// fija puede recortar una cuerda del vaso y terminar MÁS ADENTRO — el defecto que dejaba
    /// briznas y flores en el fondo. Pasos chicos para respetar la curva de la orilla.
    /// </summary>
    private static float LandX(float x, float z)
    {
        float dx = x - SceneDefinition.AeroLakeX;
        float dz = z - SceneDefinition.AeroLakeZ;
        float length = MathF.Sqrt(dx * dx + dz * dz);
        float stepX = length > 0.001f ? dx / length * 4f : 4f;
        for (int i = 0; i < 40 && SceneDefinition.AeroHeight(x, z) < SceneDefinition.WaterLevel + 0.6f; i++)
        {
            x += stepX;
        }
        return x;
    }

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
