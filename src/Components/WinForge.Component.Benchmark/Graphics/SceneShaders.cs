namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Código HLSL de la escena, COMPARTIDO por todos los backends (Direct3D 11 y Direct3D 12) y
/// fuente del SPIR-V que usa Vulkan.
///
/// Se compila en TIEMPO DE EJECUCIÓN contra el <c>d3dcompiler_47</c> del sistema: así el
/// componente no arrastra binarios de shaders en el zip ni depende de tener el SDK de Windows
/// instalado en la máquina del usuario.
///
/// Es HLSL de shader model 5.0 con un cbuffer en b0 y datos por instancia en el input layout:
/// las CUATRO APIs lo consumen igual, así que una escena nueva se escribe una sola vez. El GLSL del
/// backend OpenGL no es un port: se traduce del mismo SPIR-V en el build (ver <see cref="GlShaders"/>
/// y tools/SpirvGen), así que no hay una segunda fuente que pueda quedar desincronizada.
///
/// Los shaders son UN programa con una rama por TIPO de malla (ver
/// <c>SceneDefinition.KindCorridorRock</c> y compañía), y el tipo viaja en la instancia
/// (<c>Orientation.z</c>). Es a propósito: una escena nueva con varias mallas —el terreno y los
/// modelos del mundo Aero, por ejemplo— no obliga a pipeline nuevo en ninguna API, que es la
/// parte que se paga cuatro veces. El costo es un salto por píxel, que en una escena que se mide no se nota al lado del
/// trabajo real (nubes marcheras, agua, sombreado con ruido).
///
/// Los colores de acá son CONTENIDO de la escena (el mundo que se mira), no UI: por eso no
/// salen de los pinceles del tema de la app. La franja de métricas, en cambio, sale del mismo
/// pixel shader pero SIN tonemapping: sus píxeles ya vienen listos para pantalla.
/// </summary>
internal static class SceneShaders
{
    /// <summary>Perfil del vertex shader de la escena (feature level 11_0 = shader model 5.0).</summary>
    internal const string VertexProfile = "vs_5_0";

    /// <summary>Perfil del pixel shader de la escena.</summary>
    internal const string PixelProfile = "ps_5_0";

    /// <summary>Largo del corredor: debe coincidir con <c>SceneDefinition.CorridorLength</c>.</summary>
    internal const float CorridorLength = 420f;

    /// <summary>Velocidad de vuelo del corredor: debe coincidir con <c>SceneDefinition.FlightSpeed</c>.</summary>
    internal const float FlightSpeed = 22f;

    /// <summary>
    /// Constantes por frame. El nombre <c>row_major</c> es lo que permite subir las matrices
    /// de System.Numerics tal cual (que son row-major) y multiplicarlas como
    /// <c>mul(vectorFila, matriz)</c>. Las constantes de la escena se anteponen en
    /// <see cref="Common"/> para que shader y C# no puedan divergir: salen de los mismos campos.
    /// </summary>
    internal static string Common => SceneConstants + CommonBody;

    /// <summary>Código HLSL completo (constantes de la escena + cbuffer + funciones).</summary>
    internal static string Full => Common;

    /// <summary>
    /// Constantes compartidas con C# (<c>SceneDefinition</c>): se interpolan acá para que no
    /// puedan divergir. Con cultura INVARIANTE a propósito: en una máquina con coma decimal
    /// (es-AR) un "420,5" en el shader no compila.
    /// </summary>
    private static string SceneConstants
    {
        get
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            return
                $"static const float CorridorLength = {CorridorLength.ToString("R", invariant)};\n" +
                $"static const float FlightSpeed = {FlightSpeed.ToString("R", invariant)};\n" +
                $"static const float WaterLevel = {Scenes.SceneDefinition.WaterLevel.ToString("R", invariant)};\n" +

                $"static const float TerrainRadius = {Scenes.SceneDefinition.TerrainRadius.ToString("R", invariant)};\n" +
                $"static const float TerrainLinearShare = {Scenes.SceneDefinition.TerrainLinearShare.ToString("R", invariant)};\n" +
                $"static const float AeroLakeX = {Scenes.SceneDefinition.AeroLakeX.ToString("R", invariant)};\n" +
                $"static const float AeroLakeZ = {Scenes.SceneDefinition.AeroLakeZ.ToString("R", invariant)};\n" +
                $"static const float AeroLakeRadius = {Scenes.SceneDefinition.AeroLakeRadius.ToString("R", invariant)};\n" +
                $"static const float AeroLakeDepth = {Scenes.SceneDefinition.AeroLakeDepth.ToString("R", invariant)};\n" +
                $"static const int MaxShadowCasters = {Scenes.SceneDefinition.MaxShadowCasters};\n" +
                $"static const int MaxPointLights = {Scenes.SceneDefinition.MaxPointLights};\n" +
                $"static const int MaterialMapsPerSlot = {Scenes.SceneDefinition.MaterialMapsPerSlot};\n";
        }
    }

    private const string CommonBody =
        """
        cbuffer FrameConstants : register(b0)
        {
            row_major float4x4 ViewProjection;
            float4 CameraPositionWS;   // xyz = posición de la cámara
            float4 LightDirectionWS;   // xyz = dirección normalizada de la luz (hacia dónde viaja)
            // x = segundos desde el inicio de la escena; y = cantidad de luces puntuales de la
            // escena (0 = el bucle de luces se saltea); z = ALTO del render target en píxeles, y
            // solo cuando la API numera las filas desde ABAJO (OpenGL). En Direct3D y Vulkan va en
            // 0, que es "el origen del fragmento ya viene como el shader lo espera" (ver
            // ScreenPixel); w = sin uso.
            float4 TimeAndParams;
            // Rectángulo de la franja de métricas en NDC (xy = esquina mínima, zw = máxima).
            // Va en el MISMO bloque por frame a propósito: es un dato del frame, no del objeto.
            float4 OverlayRect;
            // Tamaño de la franja en píxeles (xy; zw sin uso): es lo que convierte la Uv en el
            // índice del píxel que hay que leer.
            float4 OverlaySize;
            // Inversa de ViewProjection: la necesita el fondo para reconstruir el RAYO de cada
            // píxel (cielo direccional + nubes marcheras). Se agrega al FINAL del bloque para no
            // mover el offset de nada de lo de arriba (la franja y el sombreado ya contaban con
            // ese orden).
            row_major float4x4 InverseViewProjection;
            // Esferas de sombra del primer plano (xyz = centro, w = radio). Ver
            // SceneDefinition.ShadowCasters: sin pasada de sombras aparte, esto es lo que apoya los
            // objetos grandes en el suelo. Las que están en cero se saltean.
            float4 ShadowCasters[MaxShadowCasters];

            // =================================================================
            // ESTILO de la escena (ver SceneDefinition.Style y Graphics.SceneStyleConstants)
            // =================================================================
            // Cielo, sol, atmósfera, nubes y grading de ESTA escena. Va al final del bloque para no
            // mover el offset de nada de arriba, y son TODOS float4 para que la alineación sea la
            // misma en el cbuffer de HLSL y en el std140 de GLSL.
            float4 StyleSkyHorizon;   // rgb = horizonte, w = exponente del degradado
            float4 StyleSkyMid;       // rgb = color medio, w = inicio de la ventana del cenit
            float4 StyleSkyZenith;    // rgb = cenit, w = fin de la ventana del cenit
            float4 StyleSunGlow;      // rgb = halo, w = fuerza
            float4 StyleSunDisk;      // rgb = disco, w = fuerza
            float4 StyleSunCore;      // rgb = núcleo, w = fuerza
            float4 StyleSunShape;     // x = potencia del halo, y = potencia del disco, z/w = centro y semiancho del núcleo
            float4 StyleSunLight;     // rgb = luz directa del PBR, w = intensidad
            float4 StyleAmbient;      // rgb = ambiente del cielo, w = exposición
            float4 StyleAerial;       // x = densidad de bruma, y/z = altura base y escala, w = resplandor
            float4 StyleCloudBand;    // x/y = base y tope de la banda, z/w = anchos de los bordes
            float4 StyleCloudShape;   // x/y = peso de forma y detalle, z/w = umbral y contraste
            float4 StyleCloudLight;   // x = peso de fase, y = sesgo, z = peso del ambiente, w = absorción
            float4 StyleCloudNoise;   // x/y/z = escala del ruido, del detalle y vertical del detalle
            float4 StyleMisc;         // x = nubes en el reflejo, y = campo de altura del terreno
            float4 StyleGroundTint;   // rgb = tinte del material de roca, w = fuerza

            // =================================================================
            // LUCES PUNTUALES de la escena (ver SceneDefinition.PointLights y
            // Graphics.SceneLightConstants)
            // =================================================================
            // Los faroles y carteles de una calle nocturna: xyz = posición, w = radio de alcance; y
            // rgb = color, w = intensidad. También al final del bloque y también todos float4, por la
            // misma razón que el estilo: no mover offsets y alinear igual en las cuatro APIs. Las que
            // no usa la escena vienen en cero y el bucle las saltea (el contador va en
            // TimeAndParams.y), así que una escena de día no paga el bucle.
            float4 PointLightPositionRadius[MaxPointLights];
            float4 PointLightColorIntensity[MaxPointLights];

            // =================================================================
            // ENTORNO IBL de la escena (ver Scenes.SceneEnvironment y
            // Graphics.SceneEnvironmentConstants)
            // =================================================================
            // La luz que llega de TODO alrededor, medida en el lugar (el HDRI del set) y proyectada a
            // 9 armónicos esféricos de radiancia. El shader los evalúa con el kernel que corresponda:
            // el del coseno para el difuso y uno que se afila con la rugosidad para el reflejo. Son
            // constantes y no un cubemap a propósito: así el IBL viaja por el MISMO bloque por frame
            // que ya comparten las cuatro APIs y no agrega textura, sampler ni descriptor en
            // ninguna (ver la nota del porqué en SceneEnvironment).
            //
            // Van al final del bloque, después del estilo y de las luces, y son todos float4 para que
            // la alineación sea idéntica en el cbuffer de HLSL y en el std140 de GLSL. Sin entorno
            // (EnvParams.x = 0) todo esto se saltea y la escena se queda con su cielo procedural.
            // Mundo → espacio de luz de la pasada de SOMBRAS (ver README-SOMBRAS.md): una proyección
            // ortográfica fija (1 cascada, resolución fija) armada con el volumen que la escena deduce
            // de su propia geometría. El sol no se mueve (es dato del estilo), así que la matriz se
            // calcula una vez y no cambia por frame: el costo por frame queda igual, que es lo que el
            // benchmark necesita para que dos corridas comparen.
            row_major float4x4 LightViewProjection;

            float4 EnvSh[9];     // rgb = coeficiente de radiancia, w = sin uso
            float4 EnvParams;    // x = entorno activo; y/z/w reservados

            // Sombra proyectada: x = fuerza (0 = apagada), y = sesgo en profundidad, z = tamaño del
            // téxel, w = ancho del PCF. Con x = 0 la función sale en la primera línea y NO muestrea el
            // shadow map, así que el camino sin sombras queda intacto (y es idéntico en las cuatro
            // APIs porque el valor viene de la escena, no del backend).
            float4 ShadowParams;
        };

        // (Acá vivían Exposure, CloudBase y CloudTop como constantes de compilación: eran el
        // interruptor global de estilo. Ahora los tres son datos de la escena, en el bloque de
        // arriba, así que una escena no puede cambiar el look de otra.)

        // Franja de métricas: un uint por píxel con el color RGBA8 premultiplicado (fila 0 =
        // arriba). Es un BUFFER y no una textura a propósito: se sube con una copia de memoria
        // desde el hilo de render (sin staging, sin layout de imagen, sin sampler) y en el
        // shader se indexa por píxel, que es exactamente lo que hace falta para un panel que se
        // dibuja 1:1 sobre la pantalla. El "sampling" por índice deja el texto nítido, sin
        // interpolación que lo emborrone.
        //
        // t1 y NO t0: en Vulkan, dxc traduce el número de registro al binding del set 0, y b0 y
        // t0 caerían los dos en el binding 0 — un descriptor set layout con dos tipos distintos
        // en el mismo binding es inválido. Con t1 el cbuffer queda en el binding 0 y la franja en
        // el 1, que son los números con los que el backend arma el layout.
        StructuredBuffer<uint> OverlayPixels : register(t1);

        // ---- Textura de detalle ----
        // Un atlas de 4×4 celdas de 64×64 (ver SceneDetailTexture): UNA textura atada da "ocho
        // materiales" eligiendo la celda con un offset constante y envolviendo la uv con frac. La uv
        // va SIEMPRE fraccionaria (el muestreo repite la celda, que es lo que permite cubrir el
        // mundo sin que la textura se vea estirada) y el nivel se pide EXPLÍCITO: la textura no
        // tiene mips y el derivado de una uv con frac salta en cada celda.
        Texture2D DetailTexture : register(t0);
        SamplerState DetailSampler : register(s0);

        // ---- Texturas de MATERIAL (ver Scenes.MaterialAtlas) ----
        // TODOS los materiales de la escena en UN arreglo de texturas: tres rebanadas por material
        // (albedo, normales y ARM) y la ranura del material viaja por instancia. Se ata una vez por
        // frame —igual que el atlas de detalle— y el material es un índice: así una escena con doce
        // materiales no cuesta doce ataduras por dibujo ni un descriptor por material y por frame en
        // Vulkan, que es exactamente lo que haría una textura por material.
        //
        // t4/s1 y no t2/s2: en Vulkan el número de registro se corre con -fvk-t-shift/-fvk-s-shift y
        // los bindings 0..4 ya están tomados (cbuffer, detalle, franja, sampler del detalle); con t4
        // la imagen cae en el binding 6 y con s1 el sampler en el 5.
        Texture2DArray MaterialMaps : register(t4);
        SamplerState MaterialSampler : register(s1);

        // ---- Shadow map (ver README-SOMBRAS.md) ----
        // La profundidad de la escena vista DESDE EL SOL (1 cascada, 2048² fijo), más un sampler de
        // COMPARACIÓN: el shader no compara a mano, le pasa la profundidad del píxel al sampler y la
        // placa devuelve 0/1 con el filtrado hecho (y en el PCF el filtro lineal promedia 4 téxeles
        // por muestra, que es lo que ablanda el borde sin costar 4 muestras).
        //
        // t6/s3 y no t5/s2: en Vulkan el número de registro se corre con -fvk-t-shift 2 y
        // -fvk-s-shift 4, y los bindings vacantes no se pisan entre imagen y sampler (ver la tabla en
        // README-SOMBRAS.md: es la parte que no se deduce leyendo este archivo).
        Texture2D ShadowMap : register(t6);
        SamplerComparisonState ShadowSampler : register(s3);

        /// <summary>Origen en el atlas de cada celda (en unidades de celda, sobre una grilla de 4×4).</summary>
        float2 DetailUv(float2 surface, float tileScale, float2 tile)
        {
            return (tile + frac(surface * tileScale)) * 0.25;
        }

        /// <summary>Detalle de una celda, como escalar (luminancia). El que llama lo apaga con la distancia.</summary>
        float DetailSample(float2 surface, float tileScale, float2 tile)
        {
            float3 value = DetailTexture.SampleLevel(DetailSampler, DetailUv(surface, tileScale, tile), 0.0).rgb;
            return dot(value, float3(0.299, 0.587, 0.114));
        }

        /// <summary>Detalle de una celda, con color (para modular un albedo).</summary>
        float3 DetailSampleColor(float2 surface, float tileScale, float2 tile)
        {
            return DetailTexture.SampleLevel(DetailSampler, DetailUv(surface, tileScale, tile), 0.0).rgb;
        }

        /// <summary>Celda del atlas: 0 pasto · 1 piedra · 2 agua · 3 nube · 4 hoja · 5 corteza · 6 tierra · 7 chispa.</summary>
        // HLSL clásico (no expresión corporal): dxc lo exige igual que fxc.
        float2 DetailTile(float index) { return float2(fmod(index, 4.0), floor(index * 0.25)); }

        // Cada campo lleva su semántica: sin ella HLSL no sabe de dónde sacarlo y el
        // compilador tira X3502. Los nombres coinciden con el input layout del backend
        // (INSTANCE0 e INSTANCE1, por instancia).
        struct InstanceData
        {
            float4 PositionScale : INSTANCE0;   // xyz = banda/desplazamiento, w = escala
            float4 Orientation   : INSTANCE1;   // x = giro, y = velocidad de giro, z = tipo de malla, w = parámetro
            // Material del objeto, cuando lo tiene (ver SceneMesh.RenderInstances): rgb = tinta,
            // w = repeticiones de UV por unidad de mundo; y xyz = fuerza del mapa de normales,
            // multiplicadores de rugosidad y de metalicidad, w = fuerza de emisión.
            float4 MaterialTint   : INSTANCE2;
            float4 MaterialParams : INSTANCE3;
        };

        // =====================================================================
        // Ruido
        // =====================================================================

        // Hash ENTERO (no senos): con coordenadas grandes —el vuelo llega a decenas de miles de
        // unidades— un hash basado en sin pierde precisión y el terreno se llena de bandas.
        // Mismo camino que SceneDefinition.Lattice: los tres idiomas dan el mismo valor.
        float LatticeNoise(int x, int y)
        {
            uint hash = (uint)(x * 374761393 + y * 668265263);
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            hash ^= hash >> 16;
            return (hash & 0xFFFFFF) / 16777215.0;
        }

        // Valor de ruido suave en [0,1] de la retícula entera.
        float ValueNoise2(float2 p)
        {
            int ix = (int)floor(p.x);
            int iy = (int)floor(p.y);
            float fx = p.x - ix;
            float fy = p.y - iy;
            float ux = fx * fx * (3.0 - 2.0 * fx);
            float uy = fy * fy * (3.0 - 2.0 * fy);

            float a = LatticeNoise(ix, iy);
            float b = LatticeNoise(ix + 1, iy);
            float c = LatticeNoise(ix, iy + 1);
            float d = LatticeNoise(ix + 1, iy + 1);
            float top = a + (b - a) * ux;
            float bottom = c + (d - c) * ux;
            return top + (bottom - top) * uy;
        }

        float Fbm2(float2 p, int octaves)
        {
            float sum = 0.0;
            float amplitude = 0.5;
            float total = 0.0;
            for (int i = 0; i < octaves; i++)
            {
                sum += ValueNoise2(p) * amplitude;
                total += amplitude;
                p *= 2.03;
                amplitude *= 0.5;
            }
            return sum / max(1e-6, total);
        }

        float Hash13(float3 p)
        {
            p = frac(p * 0.1031);
            p += dot(p, p.yzx + 33.33);
            return frac((p.x + p.y) * p.z);
        }

        float3 RotateY(float3 value, float angle)
        {
            float s, c;
            sincos(angle, s, c);
            return float3(c * value.x + s * value.z, value.y, -s * value.x + c * value.z);
        }

        float3 RotateZ(float3 value, float angle)
        {
            float s, c;
            sincos(angle, s, c);
            return float3(c * value.x - s * value.y, s * value.x + c * value.y, value.z);
        }

        // =====================================================================
        // Corredor infinito (tipo 0)
        // =====================================================================

        // Mismo criterio que SceneDefinition.TerrainHeight: si se cambia uno, cambiar el otro.
        float TerrainHeight(float2 xz)
        {
            return 5.5 * sin(xz.x * 0.06) + 4.0 * cos(xz.y * 0.05) + 2.0 * sin((xz.x + xz.y) * 0.021);
        }

        // CORREDOR INFINITO: las instancias viven en una banda fija del recorrido (su PositionScale.z
        // es una posición cualquiera dentro del corredor). El vértice se ENVUELVE en una ventana de
        // CorridorLength de largo que acompaña a la cámara: el terreno se repite pero el CONTENIDO
        // que se ve siempre es nuevo, y el buffer de instancias es chico y fijo.
        //
        // OJO con fmod(): en HLSL devuelve el resto con el signo del DIVIDENDO. Cuando la cámara
        // avanza lo suficiente, (placement.z - windowStart + CorridorLength*0.5) se vuelve NEGATIVO
        // y fmod devuelve negativo: la instancia cae DETRÁS de windowStart (detrás de la cámara) y
        // desaparece. Por eso el wrap usa floor() (módulo matemático, siempre en [0, CorridorLength)).
        // Si se toca esta fórmula, regenerar los dos archivos de shaders con tools/SpirvGen: el GLSL
        // del backend OpenGL sale de este MISMO HLSL, así que se actualiza solo.
        float3 WrapInstance(float3 local, float4 placement)
        {
            float camZ = CameraPositionWS.z;
            float windowStart = camZ - CorridorLength * 0.5;
            float offset = placement.z - windowStart + CorridorLength * 0.5;
            float z = windowStart + (offset - floor(offset / CorridorLength) * CorridorLength);

            float2 xz = float2(placement.x, z);
            return local * placement.w + float3(placement.x, placement.y + TerrainHeight(xz), z);
        }

        // =====================================================================
        // Cañón (tipos 1 y 2)
        // =====================================================================

        // Mismo valor que SceneDefinition.ValleyCenterX: de acá sale el terreno que se dibuja y de
        // allá la cámara del corredor y el pasto del Aero. Si se cambia uno, cambiar el otro (el GLSL
        // sale de este archivo, no hay nada que replicar).
        float ValleyCenterX(float z)
        {
            return 260.0 * sin(z * 0.00060) + 120.0 * sin(z * 0.00170 + 1.7);
        }

        // Semiancho del valle: la pared arranca pasado el 62% de este valor. El valle es ANCHO a
        // propósito (620 a 970 unidades): con las paredes pegadas al recorrido, el cañón tapaba el
        // cielo entero y la escena era un túnel blanco de niebla.
        float ValleyHalfWidth(float z)
        {
            return 480.0 + 220.0 * sin(z * 0.00090) + 80.0 * sin(z * 0.00310 + 2.2);
        }

        // Perfil del valle: vega casi plana con el CAUCE del río en el medio y paredes que suben
        // hasta la cresta. El agua la decide WaterLevel: el cauce queda por debajo y la vega por
        // encima, así el río es una franja y no todo el piso del valle.
        float CanyonHeight(float2 xz)
        {
            float distanceToCenter = abs(xz.x - ValleyCenterX(xz.y));
            float width = max(1.0, ValleyHalfWidth(xz.y));

            // Vega y cauce: el ancho del cauce acompaña el del valle, así el río no queda metido en
            // una zanja de ancho fijo cuando el valle se ensancha.
            float plateau = 4.0 + 7.0 * Fbm2(xz * 0.0026, 3);
            float channel = 1.0 - smoothstep(width * 0.10, width * 0.28, distanceToCenter);
            float bed = WaterLevel - 5.0 - 3.0 * Fbm2(xz * 0.018, 2);
            float floorHeight = lerp(plateau, bed, channel);

            // Paredes: empiezan fuera de la vega y llegan a la cresta en el borde del semiancho.
            float t = saturate((distanceToCenter - width * 0.62) / (width * 0.38));
            t = t * t * (3.0 - 2.0 * t);
            float ridge = 250.0 + 180.0 * Fbm2(xz * 0.0012, 4) + 60.0 * sin(xz.y * 0.0022 + xz.x * 0.0006);
            // Detalle en DOS escalas: el fino (ondulación de la ladera) y el medio (contrafuertes y
            // quebradas que le dan relieve a la pared, que si no queda una duna lisa). El medio va
            // multiplicado por t: en la vega no se ve, en la pared sí.
            float detail = 6.0 * Fbm2(xz * 0.0300, 3) + 70.0 * Fbm2(xz * 0.0095, 3) * t;

            return floorHeight + (ridge + detail - floorHeight) * t;
        }

        // =====================================================================
        // Frutiger Aero (el mundo de postal)
        // =====================================================================

        // Acá vivía WorldIsAero(): un interruptor global que elegía entre dos juegos de constantes
        // de estilo con un lerp. Se fue — el look de cada escena viaja como DATO en el bloque por
        // frame (ver las Style* del cbuffer), así que el shader es uno solo y ninguna escena puede
        // cambiar el look de otra.

        // Eje del arroyo del desagüe y del sendero de tierra: la MISMA fórmula que
        // SceneDefinition.AeroRiverX / AeroPathX. El terreno se talla con el arroyo, el agua de
        // los rápidos y la espuma viven donde él pasa y el pasto deja el sendero en tierra.
        // Van ANTES de AeroHeight: el compilador exige declaración previa.
        float AeroRiverX(float z)
        {
            return 10.0 + 55.0 * sin(z * 0.006);
        }

        float AeroPathX(float z)
        {
            return AeroRiverX(z) + 95.0 + 30.0 * sin(z * 0.011 + 2.0);
        }

        // Mismo campo que SceneDefinition.AeroHeight (lomas suaves + cuenca del lago): el terreno lo
        // dibuja el shader deformando la grilla con esta altura. Si se toca uno, tocar los DOS (C# y
        // HLSL): el GLSL sale de acá.
        float AeroHeight(float2 xz)
        {
            // Cuatro ESCALAS: lomas anchas (el horizonte), medianas (el vaivén que se ve de cerca)
            // y ondulación fina (el relieve del pasto). Sin las dos últimas el campo se ve plano.
            float hills = 12.0 * sin(xz.x * 0.0021 + 0.7)
                        + 10.0 * sin(xz.y * 0.0026 - 0.4)
                        +  6.0 * sin(xz.x * 0.0043 + xz.y * 0.0035 + 1.9)
                        +  3.0 * sin(xz.x * 0.0125 - 1.1) * sin(xz.y * 0.0104 + 0.6)
                        +  1.6 * sin(xz.x * 0.0300 + 0.9) * sin(xz.y * 0.0260 - 0.5)
                        +  0.55 * sin(xz.x * 0.0900 - 0.3) * sin(xz.y * 0.0810 + 1.2);

            float2 delta = xz - float2(AeroLakeX, AeroLakeZ);
            float lakeDistance = length(delta);
            // Costa irregular: idéntico a SceneDefinition.AeroHeight (léase la nota de ahí).
            float angle = atan2(delta.y, delta.x);
            float shore = 0.19 * sin(angle * 5.0 + 0.6)
                        + 0.10 * sin(angle * 11.0 - 1.7)
                        + 0.06 * sin(delta.x * 0.0041 + delta.y * 0.0033 + 2.2);
            float basin = 1.0 - smoothstep(AeroLakeRadius * 0.45, AeroLakeRadius * (1.0 + shore), lakeDistance);
            // Talud sur escalonado y cauce del arroyo: MISMA cuenta que en C# (léase ahí). El escalón
            // vive en un ANILLO pegado a la costa: sin el anillo (creciendo con la distancia) hundía
            // media llanura y el prado entero quedaba bajo el agua.
            float shoreBand = lakeDistance / AeroLakeRadius;
            float ring = smoothstep(0.35, 0.85, shoreBand) * (1.0 - smoothstep(1.05, 1.45, shoreBand));
            float southern = smoothstep(0.10, 0.75, -delta.y / max(1.0, lakeDistance)) * ring;
            basin += southern * (0.85 - basin) * 0.55;
            float riverHalfWidth = 11.0 + 5.0 * sin(xz.y * 0.013);
            float bank = abs(xz.x - AeroRiverX(xz.y)) - riverHalfWidth;
            float river = 1.0 - smoothstep(0.0, 60.0, bank);
            float height = hills - basin * AeroLakeDepth;
            return min(height, height - river * 7.0 * (1.0 - basin * 0.8));
        }

        // =====================================================================
        // Cielo, nubes y color
        // =====================================================================

        float3 SunDirection() { return normalize(-LightDirectionWS.xyz); }

        // Sol, ambiente y cielo de ESTA escena: no hay ninguna variante por mundo acá — los tres
        // salen del bloque de estilo, así que el mismo código dibuja el anochecer azul del corredor
        // y el mediodía de la postal sin saber que son escenas distintas.
        float3 SunColor() { return StyleSunLight.rgb; }

        float3 SkyAmbient() { return StyleAmbient.rgb; }

        // Modelo de cielo analítico: degradado de TRES paradas por elevación (horizonte → medio →
        // cenit) con halo, disco y núcleo del sol. No pretende ser un modelo de dispersión real
        // (Preetham/Hosek) — a esta escala el ojo no distingue, y este se evalúa miles de veces por
        // píxel en las nubes. Todo lo que lo define es dato del estilo: colores, exponente y
        // ventana del cenit, más las tres capas del sol con sus fuerzas y potencias.
        float3 SkyColor(float3 direction)
        {
            float elevation = saturate(direction.y);
            float3 color = lerp(StyleSkyHorizon.rgb, StyleSkyMid.rgb, pow(elevation, StyleSkyHorizon.w));
            color = lerp(color, StyleSkyZenith.rgb, smoothstep(StyleSkyMid.w, StyleSkyZenith.w, elevation));

            float sunDot = saturate(dot(direction, SunDirection()));
            color += StyleSunGlow.rgb * pow(sunDot, StyleSunShape.x) * StyleSunGlow.w;
            color += StyleSunDisk.rgb * pow(sunDot, StyleSunShape.y) * StyleSunDisk.w;
            color += StyleSunCore.rgb
                   * smoothstep(StyleSunShape.z - StyleSunShape.w, StyleSunShape.z + StyleSunShape.w, sunDot)
                   * StyleSunCore.w;
            return color;
        }

        // Densidad de la capa de nubes: perfil vertical por bandas + ruido fractal. TODO lo que
        // distingue los cúmulos de la postal de los jirones del anochecer es dato del estilo (banda,
        // bordes, mezcla de ruido, umbral, contraste y escala): el código es uno.
        float CloudDensity(float3 position)
        {
            float profile = smoothstep(StyleCloudBand.x, StyleCloudBand.x + StyleCloudBand.z, position.y)
                          * (1.0 - smoothstep(StyleCloudBand.y - StyleCloudBand.w, StyleCloudBand.y, position.y));
            if (profile <= 0.0) return 0.0;

            float2 xz = position.xz * StyleCloudNoise.x;
            float shape = Fbm2(xz, 4);
            float detail = Fbm2(xz * StyleCloudNoise.y + position.y * StyleCloudNoise.z, 3);
            // El umbral decide CUÁNTA nube hay y el contraste cómo de definido es su borde: con
            // contraste bajo la marcha acumula densidad en todo el cielo y sale una sábana gris que
            // lava el azul; con contraste alto la densidad es 0 o 1 (cúmulos con borde definido).
            float density = saturate((shape * StyleCloudShape.x + detail * StyleCloudShape.y
                                      - StyleCloudShape.z) * StyleCloudShape.w);
            return density * profile;
        }

        // Marcha dentro de la capa de nubes: dispersión simple con 4 pasos hacia el sol. El
        // resultado se compone sobre el cielo. Es el trabajo por píxel más pesado del componente
        // (y por eso la escena es un buen banco: escala con la resolución y con la placa).
        float4 CloudLayer(float3 origin, float3 direction)
        {
            if (direction.y <= 0.008) return float4(0.0, 0.0, 0.0, 0.0);

            // La banda (base y tope) es del estilo: el cielo del corredor y el del Aero la tienen
            // distinta sin que el marcher sepa de qué escena se trata.
            float bandBase = StyleCloudBand.x;
            float bandTop = StyleCloudBand.y;

            // Cerca del horizonte la banda se comprime en unos pocos píxeles y el ruido de la marcha
            // sale como cortinas verticales sobre el suelo. Se apaga ahí, que es justo donde la
            // neblina por distancia ya la taparía: el cielo bajo queda limpio.
            float horizonFade = smoothstep(0.010, 0.070, direction.y);
            if (horizonFade <= 0.002) return float4(0.0, 0.0, 0.0, 0.0);

            float t0 = (bandBase - origin.y) / direction.y;
            float t1 = (bandTop - origin.y) / direction.y;
            if (t1 <= t0) return float4(0.0, 0.0, 0.0, 0.0);
            t0 = max(t0, 0.0);

            // El espesor marchado se TOPEA. Sin tope, cerca del horizonte la visual atraviesa miles
            // de unidades de capa, la absorbancia satura y el cielo bajo entero queda blanco opaco:
            // una banda de leche de lado a lado, que es exactamente lo que salía y lo que más
            // alejaba el render de la referencia. Con el tope, un cúmulo tiene la misma opacidad
            // arriba y cerca del horizonte.
            const float maxPath = 900.0;
            t1 = min(t1, t0 + maxPath);

            const int steps = 26;
            const int lightSteps = 4;
            float dt = (t1 - t0) / steps;

            // El desplazamiento inicial es por píxel (dither determinista): sin esto, 26 pasos
            // dejan escalones visibles en el degradado de la nube.
            float jitter = Hash13(float3(origin.xz * 0.5, direction.x * 37.0));
            float t = t0 + dt * jitter;

            float3 scatter = float3(0.0, 0.0, 0.0);
            float transmittance = 1.0;
            float3 sun = SunDirection();

            for (int i = 0; i < steps; i++)
            {
                float3 position = origin + direction * t;
                float density = CloudDensity(position);
                if (density > 0.002)
                {
                    // Transmitancia hacia el sol: 4 pasos gruesos alcanzan para el auto-sombreado
                    // (que es lo que da el relieve de la nube).
                    float shadow = 0.0;
                    for (int j = 0; j < lightSteps; j++)
                    {
                        shadow += CloudDensity(position + sun * (28.0 * (j + 1)));
                    }
                    float sunTransmittance = exp(-shadow * 1.15);

                    float cosAngle = dot(direction, sun);
                    // Fase de Henyey-Greenstein con un lóbulo frontal fuerte: el sol "enciende" el
                    // borde de las nubes cuando está adelante (el efecto del amanecer).
                    float g = 0.62;
                    float phase = (1.0 - g * g) / pow(1.0 + g * g - 2.0 * g * cosAngle, 1.5);

                    // La nube se enciende con el sol de ESTA escena: el peso de la fase hacia el sol,
                    // su sesgo y el peso del ambiente salen del estilo (mediodía brillante contra
                    // anochecer apagado).
                    float3 lit = SunColor() * (phase * StyleCloudLight.x + StyleCloudLight.y) * sunTransmittance
                               + SkyAmbient() * StyleCloudLight.z;
                    float stepAlpha = 1.0 - exp(-density * dt * StyleCloudLight.w);
                    scatter += transmittance * lit * stepAlpha;
                    transmittance *= 1.0 - stepAlpha;
                    if (transmittance < 0.02) break;
                }
                t += dt;
            }

            // Algodón: 26 pasos de marcher dan la FORMA de la nube (el ruido de la densidad tiene la
            // escala del cúmulo), pero la superficie queda lisa. El atlas pone el grano con la
            // posición del medio de la capa: ~45 unidades por celda, que visto desde el suelo es el
            // tamaño de un puff.
            float3 cloudSurface = origin + direction * (t0 + (t1 - t0) * 0.5);
            float puff = DetailSample(cloudSurface.xz, 0.022, float2(3.0, 0.0));
            scatter *= 0.78 + 0.46 * puff;

            return float4(scatter, (1.0 - transmittance) * horizonFade);
        }

        // Mezcla de la capa de nubes sobre el cielo. <paramref name="clouds"/> viene PREMULTIPLICADO
        // (scatter ya trae la transmitancia y el paso adentro), así que el compuesto es
        // fondo*(1-alfa) + scatter. Antes se hacía lerp(cielo, scatter, alfa), que trata al scatter
        // como si fuera un color: en los bordes semiopacos eso apaga la nube.
        float3 CompositeClouds(float3 sky, float4 clouds)
        {
            float alpha = saturate(clouds.a);
            return sky * (1.0 - alpha) + clouds.rgb;
        }

        // Resplandor de lente del cielo Aero: halo, destello de cuatro puntas, fantasmas sobre la
        // recta sol→centro y chispas. Es lo que convierte un cielo brillante en el cielo de la
        // postal (y es trabajo por píxel puro, así que también es carga útil para el banco).
        // <paramref name="ndc"/> es la posición del píxel en NDC, que el fondo ya tiene.
        float3 AeroGlare(float2 ndc)
        {
            // El sol proyectado a pantalla: los fantasmas y el halo viven alrededor de ESE punto,
            // no de la dirección (si no, el resplandor no acompaña al sol cuando la cámara gira).
            float4 clip = mul(float4(CameraPositionWS.xyz + SunDirection() * 5000.0, 1.0), ViewProjection);
            float2 sunNdc = clip.xy / max(1e-4, abs(clip.w));
            float visible = (clip.w > 0.0) ? 1.0 : 0.0;

            float2 d = ndc - sunNdc;
            float dist = length(d);

            float3 glare = float3(1.00, 0.98, 0.90) * exp(-dist * 2.1) * 0.16;

            // Destello de cuatro puntas (el de las postales: rayos finos en cruz).
            float cross0 = exp(-abs(d.x) * 8.0) * exp(-dist * 2.0) + exp(-abs(d.y) * 8.0) * exp(-dist * 2.0);
            glare += float3(1.00, 0.99, 0.94) * cross0 * 0.40;

            // Fantasmas: aros de luz sobre la recta que pasa por el sol y el centro del cuadro.
            for (int i = 1; i <= 3; i++)
            {
                float2 ghost = sunNdc + (float2(0.0, 0.0) - sunNdc) * (0.42 * i);
                float ghostDistance = length(ndc - ghost);
                glare += float3(0.60, 0.86, 1.00) * exp(-ghostDistance * 24.0) * (0.16 / i);
            }

            // Acá vivía una "rejilla de chispas" en pantalla (floor(ndc * 44) + un hash por celda).
            // Se fue: cada celda elegida abarcaba 1/44 del cuadro y el halo la rellenaba entera, así
            // que no se leía como un destello sino como papeles blancos flotando — el artefacto más
            // visible de todo el cielo. Si vuelve un parpadeo, tiene que ser sub-pixel.
            return glare * visible;
        }

        // Exposición fija y tonemapping ACES (aproximación de Narkowicz). Todo el mundo de la
        // escena pasa por acá: sin esto la escena escribe luz lineal cruda al back buffer y se ve
        // "de demo técnica" por más geometría que tenga. El panel NO pasa por acá: sus píxeles ya
        // vienen listos para pantalla.
        float3 Tonemap(float3 color)
        {
            color *= StyleAmbient.w;
            const float a = 2.51;
            const float b = 0.03;
            const float c = 2.43;
            const float d = 0.59;
            const float e = 0.14;
            color = saturate((color * (a * color + b)) / (color * (c * color + d) + e));
            // El back buffer es UNORM (no sRGB): la transferencia la hace el shader.
            return pow(color, 1.0 / 2.2);
        }
        """;

    /// <summary>Vertex shader de la geometría: una rama por tipo de malla.</summary>
    internal const string GeometryVertexShader =
        """
        struct VSInput
        {
            float3 Position : POSITION;
            float3 Normal   : NORMAL;
            float2 Uv       : TEXCOORD;   // coordenada de textura del modelo (los procedurales la dejan en 0)
        };

        struct VSOutput
        {
            float4 Position      : SV_POSITION;
            float3 Normal        : NORMAL;
            float3 WorldPosition : TEXCOORD0;
            float3 Local         : TEXCOORD1;   // posición local del modelo (briznas: doblez por el viento)
            float4 Params        : TEXCOORD2;   // x = tipo, y = altura del terreno, z = fase, w = parámetro
            float2 Uv            : TEXCOORD3;   // textura del material
            float4 MaterialTint  : TEXCOORD4;   // tinta y escala de UV (ver InstanceData)
            float4 MaterialParams : TEXCOORD5;  // fuerzas del material (ver InstanceData)
        };

        VSOutput VSMain(VSInput input, InstanceData instance)
        {
            float kind = instance.Orientation.z;
            float3 world;
            float3 normal;
            float3 local = input.Position;
            float height = 0.0;
            // La UV del sprite de partícula GIRA con su cuadro (ver la rama de abajo); el resto de los
            // tipos pasa la del vértice tal cual.
            float2 uv = input.Uv;

            if (kind < 0.5)
            {
                // ---- Rocas del corredor ----
                float angle = instance.Orientation.x + TimeAndParams.x * instance.Orientation.y;
                world = WrapInstance(RotateY(input.Position, angle), instance.PositionScale);
                normal = RotateY(input.Normal, angle);
            }
            else if (kind < 3.5)
            {
                // ---- Pasto del mundo Aero ----
                // La MISMA grilla que el cañón (mismo warp, mismo LOD implícito), pero deformada con
                // el campo de las lomas y el lago: es lo que permite que el mundo nuevo no traiga
                // geometría nueva, solo otra altura y otro sombreado.
                float2 grid = input.Position.xz;
                float2 warped = (grid * TerrainLinearShare + grid * grid * grid * (1.0 - TerrainLinearShare))
                              * TerrainRadius;
                float2 worldXZ = CameraPositionWS.xz + warped;

                float distanceToCamera = length(worldXZ - CameraPositionWS.xz);
                height = AeroHeight(worldXZ);
                float epsilon = max(2.5, distanceToCamera * 0.02);
                float hx = AeroHeight(worldXZ + float2(epsilon, 0.0));
                float hz = AeroHeight(worldXZ + float2(0.0, epsilon));
                normal = normalize(float3(height - hx, epsilon, height - hz));
                world = float3(worldXZ.x, height, worldXZ.y);
            }
            else if (kind < 13.5)
            {
                // ---- Modelos del mundo Aero (hasta la brizna, kind 13) ----
                // Burbujas, torres, árboles, flores, gaviotas, globos, mariposas y briznas: todos
                // se colocan igual (giro en Y, escala y traslación) y cada TIPO le suma su
                // movimiento propio. La animación es función pura del tiempo (con la fase en la
                // instancia), así que la corrida sigue siendo repetible. El polen (kind 14) NO
                // entra acá: su billboard está en la rama de abajo.
                float yaw = instance.Orientation.x;
                float phase = instance.Orientation.y;
                float3 localPosition = input.Position;
                float3 localNormal = input.Normal;

                if (kind < 4.5)
                {
                    // Burbujas: a la deriva LENTA. La escala local es el radio (hasta ~20 unidades),
                    // así que 0,03 de vaivén ya son más de medio metro en la grande: lo que se veía
                    // antes (1,5) las hacía patear como pelotas. La deriva lateral es proporcional
                    // a la fase para que no vayan todas en fila.
                    localPosition.y += 0.030 * sin(TimeAndParams.x * 0.34 + phase);
                    localPosition.x += 0.022 * sin(TimeAndParams.x * 0.21 + phase * 1.7);
                    localPosition.z += 0.016 * cos(TimeAndParams.x * 0.17 + phase);
                }
                else if (kind < 5.5)
                {
                    // Torres: QUIETAS. La ciudad es fondo; el vaivén de gaviotas que las agarraba
                    // antes (la cadena vieja agrupaba 5..9) hacía que todo el skyline se tambaleara.
                }
                else if (kind < 6.5)
                {
                    // Copa del árbol: se mece DESPACIO y proporcional a la altura local (la punta
                    // más que la base, como el viento real). Es el movimiento que hace viva la
                    // línea de árboles sin que parezca que tiemblan.
                    float sway = 0.045 * sin(TimeAndParams.x * 0.9 + phase);
                    localPosition.x += sway * saturate(localPosition.y);
                    localNormal = normalize(localNormal + float3(sway * 0.8, 0.0, 0.0));
                }
                else if (kind < 8.5)
                {
                    // Flores (girasol y rosa): el tallo cede apenas con el viento.
                    float sway = 0.060 * sin(TimeAndParams.x * 1.2 + phase);
                    localPosition.x += sway * saturate(localPosition.y);
                    localNormal = normalize(localNormal + float3(sway, 0.0, 0.0));
                }
                else if (kind < 9.5)
                {
                    // Gaviotas: planeo con un rolido suave.
                    float glide = 0.16 * sin(TimeAndParams.x * 1.6 + phase);
                    localPosition = RotateZ(localPosition, glide);
                    localNormal = RotateZ(localNormal, glide);
                }
                else if (kind < 10.5)
                {
                    // Globos: se mecen.
                    localPosition.x += 1.8 * sin(TimeAndParams.x * 0.24 + phase);
                    localPosition.y += 0.7 * sin(TimeAndParams.x * 0.33 + phase * 1.3);
                }
                else if (kind < 11.5)
                {
                    // Mariposas: cada ala gira alrededor del eje del cuerpo (con el signo de su
                    // propio lado); el cuerpo, sobre el eje, casi no se mueve.
                    float flap = 0.95 * sin(TimeAndParams.x * 8.5 + phase);
                    float side = (localPosition.x >= 0.0) ? 1.0 : -1.0;
                    localPosition = RotateZ(localPosition, flap * side);
                    localNormal = RotateZ(localNormal, flap * side);
                    localPosition.y += 0.5 * sin(TimeAndParams.x * 2.3 + phase);
                }
                else
                {
                    // ---- Briznas de pasto (kind 13, el último modelo opaco) ----
                    // El doblado va con el CUADRADO de la altura local (y²): la base no se mueve y la
                    // punta se lleva todo el viento, que es como se dobla una brizna de verdad. La
                    // fase de la instancia evita que el prado entero se mueva como una manta, y hay
                    // una ráfaga lenta encima del vaivén rápido para que el viento "pase".
                    float wind = sin(TimeAndParams.x * 1.9 + phase * 6.2831
                                   + instance.PositionScale.x * 0.07 + instance.PositionScale.z * 0.05);
                    float gust = 0.55 + 0.45 * sin(TimeAndParams.x * 0.37 + phase * 3.1);
                    float bend = wind * gust * 0.40 * localPosition.y * localPosition.y;
                    localPosition.x += bend;
                    localPosition.z += bend * 0.55;
                    // La normal acompaña al doblado: sin esto la punta se ilumina como si estuviera
                    // derecha y el viento se ve (se mueve) pero no se lee.
                    localNormal = normalize(localNormal + float3(-bend * 1.7, 0.0, -bend * 0.9));
                }

                world = instance.PositionScale.xyz + RotateY(localPosition, yaw) * instance.PositionScale.w;
                normal = normalize(RotateY(localNormal, yaw));
            }
            else if (kind > 17.5)
            {
                // ---- LLUVIA (kind 18): estelas que ACOMPAÑAN a la cámara ----
                // La caja de gotas viaja con el ojo: el travelling se mueve y, si la caja quedara fija
                // en el mundo, la dejaría atrás y el cuadro se secaría a los pocos segundos. Cada gota
                // cae con una función PURA del tiempo y de su fase (un frac que cicla), así que dos
                // corridas de la misma duración ven exactamente la misma lluvia.
                float phase = instance.Orientation.y;
                float fallSpeed = 0.55 + instance.Orientation.w * 0.55;   // vueltas por segundo
                float cycle = frac(phase + TimeAndParams.x * fallSpeed);
                const float rainHeight = 26.0;

                // Ancla: el desplazamiento horizontal de la gota (fijo, relativo al ojo) y su altura,
                // QUE CAE. La caja va de −9 a +17 m sobre el ojo: cubre la calle y el cielo de arriba.
                float3 anchor = CameraPositionWS.xyz
                              + float3(instance.PositionScale.x, cycle * rainHeight - 9.0, instance.PositionScale.z);

                // El quad local es (x = ancho unitario ±1, y = largo 0..1). Se orienta con el ARRIBA
                // DEL MUNDO (la lluvia cae vertical) y la derecha de la vista, así que la estela
                // siempre se ve de canto y nunca de perfil. El viento la inclina apenas.
                float3 toCamera = CameraPositionWS.xyz - anchor;
                float3 right = normalize(cross(float3(0.0, 1.0, 0.0), toCamera));
                float wind = input.Position.y * 0.16 * sin(phase * 6.2831 + TimeAndParams.x * 0.4);
                world = anchor
                      + right * (input.Position.x * 0.032 + wind)
                      + float3(0.0, 1.0, 0.0) * (input.Position.y * instance.PositionScale.w);

                normal = -normalize(toCamera);
                height = 0.0;
            }
            else if (kind > 16.5)
            {
                // ---- Material texturado (modelos glTF y geometría propia de los dioramas) ----
                // Colocación pura: giro en Y, escala y traslación. No hay animación por tipo porque
                // acá el TIPO no es una forma: lo que distingue un objeto de otro es su MATERIAL (las
                // texturas del atlas y los cuatro números de la instancia).
                float yaw = instance.Orientation.x;
                world = instance.PositionScale.xyz + RotateY(input.Position, yaw) * instance.PositionScale.w;
                normal = RotateY(input.Normal, yaw);
                height = 0.0;
            }
            else
            {
                // ---- Partículas (kind 14): polen, hojas, pétalos y semillas ----
                // El cuadro de 1×1 se orienta HACIA LA CÁMARA con la derecha y el arriba de la vista
                // (así la partícula se ve igual desde cualquier ángulo: con el cuadro fijo, de canto
                // desaparecía) y además GIRA sobre su propio eje, que es lo que hace una hoja
                // arrancada. La UV gira CON el cuadro: si no, el dibujo se desliza por la partícula.
                float phase = instance.Orientation.y;
                float sprite = instance.MaterialTint.x;
                float spinAngle = instance.MaterialTint.y * TimeAndParams.x + instance.MaterialTint.z * 6.2831;
                float spinSin, spinCos;
                sincos(spinAngle, spinSin, spinCos);

                float3 toCamera = CameraPositionWS.xyz - instance.PositionScale.xyz;
                float3 right = normalize(cross(float3(0.0, 1.0, 0.0), toCamera));
                float3 up = normalize(cross(toCamera, right));

                // Deriva: se mece de costado y sube o baja según QUÉ partícula sea. El polen flota
                // (vaivén), la semilla SUBE (el penacho es lo que la sostiene) y la hoja CAE despacio
                // mientras gira. Función pura del tiempo y de la fase: la corrida sigue repetible.
                float seed = 1.0 - saturate(abs(sprite - 14.0));
                float leaf = 1.0 - saturate(abs(sprite - 13.0));
                float bob = 0.45 * sin(TimeAndParams.x * 0.83 + phase * 4.10);
                float rise = 0.95 * abs(sin(TimeAndParams.x * 0.21 + phase * 6.2831)) - 0.30;
                float sink = 0.30 * sin(TimeAndParams.x * 0.31 + phase * 3.70) - 0.14;
                float vertical = lerp(lerp(bob, rise, seed), sink, leaf);

                float2 plane = float2(input.Position.x * spinCos - input.Position.y * spinSin,
                                      input.Position.x * spinSin + input.Position.y * spinCos);
                uv = plane * 0.5 + 0.5;

                float3 drift = float3(
                    sin(TimeAndParams.x * 0.61 + phase * 6.2831) * 0.85,
                    vertical,
                    cos(TimeAndParams.x * 0.47 + phase * 5.10) * 0.85);

                world = instance.PositionScale.xyz + drift
                      + (right * plane.x + up * plane.y) * instance.PositionScale.w;
                normal = -normalize(toCamera);
                height = 0.0;
            }

            VSOutput output;
            output.Position = mul(float4(world, 1.0), ViewProjection);
            output.Normal = normal;
            output.WorldPosition = world;
            output.Local = local;
            output.Params = float4(kind, height, instance.Orientation.y, instance.Orientation.w);
            output.Uv = uv;
            output.MaterialTint = instance.MaterialTint;
            output.MaterialParams = instance.MaterialParams;
            return output;
        }
        """;

    /// <summary>
    /// Pixel shader de la geometría, con una rama por tipo de malla: rocas del corredor, pasto,
    /// briznas, copas, polen y el resto del Aero. Todo pasa por <c>Tonemap</c> y por la perspectiva
    /// aérea (que usa el mismo cielo que el fondo, así el horizonte funde en vez de cortar).
    /// </summary>
    internal const string GeometryPixelShader =
        """
        // Bruma por distancia: devuelve CUÁNTO cielo se mezcla, no un color para sumar a lo que ya
        // hay. La niebla aditiva satura cualquier geometría lejana a un blanco plano —con el
        // horizonte amaneciendo, el cañón entero terminaba del color del cielo y no se veían ni las
        // paredes ni el río—; mezclando, lo lejano se acerca al color del cielo sin perder la forma.
        //
        // Se apoya en el suelo (heightFalloff): mirando hacia abajo desde el aire el valle se ve
        // nítido, y mirando al horizonte todo se funde con él, que es como se ve un amanecer.
        float AerialDensity(float3 worldPosition, float distanceToCamera)
        {
            // La bruma es del ESTILO: el aire limpio de la postal y el del anochecer azul tienen
            // densidades y alturas distintas, y ninguna escena toca la de la otra.
            float heightFalloff = exp(-max(0.0, worldPosition.y - StyleAerial.y) / StyleAerial.z);

            // Con mucho cielo mezclado la escena se ve como una duna de yeso: por eso la densidad es
            // baja y el peso lo pone la distancia, no el color.
            return saturate(1.0 - exp(-distanceToCamera * StyleAerial.x * heightFalloff));
        }

        float3 ShadeRock(VSOutput input, float3 n, float3 view, float3 light)
        {
            float diffuse = saturate(dot(n, light));
            float3 half3 = normalize(light + view);
            float specular = pow(saturate(dot(n, half3)), 42.0);
            float rim = pow(1.0 - saturate(dot(n, view)), 3.0);

            float phase = frac(sin(dot(input.Local, float3(12.9898, 78.233, 37.719))) * 43758.5453);
            float3 cool = float3(0.16, 0.55, 0.95);
            float3 warm = float3(0.95, 0.42, 0.66);
            float3 albedo = lerp(cool, warm, phase) * (0.55 + 0.45 * saturate(input.Params.w));

            // El tinte del material es del ESTILO: la misma piedra se lee de amanecer o de noche
            // azul sin que el material (esta función) tenga una rama por escena.
            albedo *= lerp(float3(1.0, 1.0, 1.0), StyleGroundTint.rgb, StyleGroundTint.w);

            float3 q = input.WorldPosition * 1.7 + TimeAndParams.x * 0.35;
            float noise = 0.0;
            // [loop] y NO [unroll]: fxc desenrolla 96 iteraciones de una cadena SERIAL (no gana
            // nada) y con el inlining del PBR el optimizador entra en un acantilado de minutos:
            // medido con [unroll], el PS de geometría no terminaba de compilar en 150 s (el VS:
            // 278 ms). Con forma de bucle la matemática es idéntica y compila en segundos.
            [loop]
            for (int i = 0; i < 96; i++)
            {
                noise += Hash13(q);
                q = q * 1.61 + float3(noise, noise, noise) * 0.37;
            }
            noise /= 96.0;

            float3 color = albedo * (0.09 + 0.91 * diffuse);
            color += specular * 0.55;
            color += rim * 0.35 * float3(0.45, 0.72, 1.0);
            // Grano de piedra: el ruido de arriba (96 iteraciones de hash) es del TERRENO, la textura
            // es de la CARA. Sin esto las rocas del corredor se ven como plástico facetado.
            float gravel = DetailSample(input.WorldPosition.xz + input.Local.xy * 0.6, 0.95, float2(1.0, 0.0));
            return color * (0.78 + 0.44 * noise) * (0.80 + 0.42 * gravel);
        }

        // Normal SIEMPRE del lado del ojo: las chapas (pétalos, alas, paneles del emblema) no tienen
        // espesor y el culling está apagado en las cuatro APIs; sin esto, media cara se sombrea con
        // la normal al revés y sale negra.
        float3 FaceNormal(float3 n, float3 view)
        {
            // Sin ternario (condición escalar + ramas float3): el validador de SPIR-V del dxc
            // rechaza ese OpSelect. lerp/step es exactamente lo mismo y pasa limpio.
            return lerp(n, -n, step(dot(n, view), 0.0));
        }

        // =====================================================================
        // Materiales: PBR (GGX), ambiente del cielo y sombras
        // =====================================================================
        // Un solo motor de sombreado para TODOS los materiales: albedo + metalicidad + rugosidad.
        // Cambiar un material es cambiar tres números, no escribir otro sombreado — y es lo que
        // separa el vidrio del cromo, del agua, de la tela y de la hoja sin fórmulas ad-hoc por
        // objeto (que es lo que hacía que todo se viera "de colores planos").

        float DistributionGgx(float nDotH, float roughness)
        {
            float alpha = max(0.0025, roughness * roughness);
            float alpha2 = alpha * alpha;
            float denominator = nDotH * nDotH * (alpha2 - 1.0) + 1.0;
            return alpha2 / (3.14159265 * denominator * denominator);
        }

        float GeometrySmith(float nDotV, float nDotL, float roughness)
        {
            float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
            float viewTerm = nDotV / (nDotV * (1.0 - k) + k);
            float lightTerm = nDotL / (nDotL * (1.0 - k) + k);
            return viewTerm * lightTerm;
        }

        float3 FresnelSchlick(float cosTheta, float3 f0)
        {
            return f0 + (1.0 - f0) * pow(saturate(1.0 - cosTheta), 5.0);
        }

        // =================================================================
        // Entorno del HDRI (IBL)
        // =================================================================

        /// <summary>
        /// Evalúa los 9 armónicos esféricos del entorno con un kernel por banda: bands.x/y/z pesan las
        /// bandas 0, 1 y 2.
        ///
        /// OJO: es el MISMO polinomio y los MISMOS ejes que usa la proyección en C# (ver
        /// <c>SceneEnvironment.EvaluateBasis</c>): el componente "z" de la base es el ARRIBA del
        /// mundo, o sea que se evalúa con los ejes permutados (x, z, y). Si acá cambia un signo, un
        /// orden o un eje, el entorno entra girado y la luz viene del lado equivocado — sin ningún
        /// error visible más que un ambiente raro.
        /// </summary>
        float3 EnvShEval(float3 direction, float3 bands)
        {
            float ux = direction.x;
            float uy = direction.z;
            float uz = direction.y;
            return bands.x * (EnvSh[0].rgb * 0.282095)
                 + bands.y * (EnvSh[1].rgb * (0.488603 * uy)
                            + EnvSh[2].rgb * (0.488603 * uz)
                            + EnvSh[3].rgb * (0.488603 * ux))
                 + bands.z * (EnvSh[4].rgb * (1.092548 * ux * uy)
                            + EnvSh[5].rgb * (1.092548 * uy * uz)
                            + EnvSh[6].rgb * (0.315392 * (3.0 * uz * uz - 1.0))
                            + EnvSh[7].rgb * (1.092548 * ux * uz)
                            + EnvSh[8].rgb * (0.546274 * (ux * ux - uy * uy)));
        }

        /// <summary>
        /// Irradiancia difusa del entorno en una dirección: el kernel del COSENO, que en armónicos son
        /// los pesos 1, 2/3 y 1/4 (Ramamoorthi &amp; Hanrahan). Es la luz que recibe una superficie que
        /// mira a <paramref name="direction"/>.
        ///
        /// El <c>max(0)</c> no es decorativo: un HDRI es una función MUY picuda (una calle de noche son
        /// unos faroles sobre negro) y la banda 2 de su reconstrucción puede pasar por debajo de cero
        /// en la dirección opuesta a la fuente. Sin el recorte eso daría un ambiente NEGATIVO —una
        /// superficie restando luz— en los planos que miran al lado oscuro.
        /// </summary>
        float3 EnvIrradiance(float3 direction)
        {
            return max(EnvShEval(direction, float3(1.0, 0.6666667, 0.25)), 0.0);
        }

        /// <summary>
        /// Reflejo del entorno con RUGOSIDAD: el mismo polinomio con un kernel que se AFILA con la
        /// rugosidad (exp(-l(l+1)α), con α = rugosidad²). La banda 0 (el promedio del entorno) nunca se
        /// afila, así que un material muy rugoso cae al promedio y no a negro.
        /// </summary>
        float3 EnvReflection(float3 direction, float roughness)
        {
            float alpha = roughness * roughness;
            return max(EnvShEval(direction, float3(1.0, exp(-2.0 * alpha), exp(-6.0 * alpha))), 0.0);
        }

        // Irradiancia del cielo en una dirección: promedio hemisférico del MISMO cielo que se ve de
        // fondo (azul del cenit arriba, claro del horizonte al ras). Es lo que hace que un objeto se
        // vea al aire libre en vez de sombreado con una constante.
        //
        // Con entorno (EnvParams.x) la fuente cambia de "lo que inventó el estilo" a "lo que se midió
        // en el lugar": la misma arquitectura de una sola función, configurada por datos, que el resto
        // del motor. La escena sin HDRI (o con el HDRI ilegible) conserva el camino procedural tal
        // cual, así que esto no cambia NINGUNA escena que no declare su entorno.
        float3 SkyIrradiance(float3 n)
        {
            float3 upColor = SkyColor(float3(0.0, 1.0, 0.0));
            float3 horizonColor = SkyColor(normalize(float3(n.x, 0.10, n.z)));
            float3 procedural = lerp(horizonColor, upColor, saturate(n.y * 0.5 + 0.5)) * 0.60;
            if (EnvParams.x <= 0.0) return procedural;
            return EnvIrradiance(n);
        }

        // Reflejo del entorno con RUGOSIDAD: espejo se lleva el cielo nítido (y las NUBES, con una
        // muestra de la capa), rugoso se cae al promedio del cielo. Es lo que hace que el agua y el
        // vidrio reflejen el mundo en vez de un color.
        float3 SkyReflection(float3 direction, float roughness)
        {
            float3 sharp = SkyColor(direction);
            // Las nubes en el reflejo se pagan solo si la escena las quiere (el vidrio y el agua del
            // Aero): es una marcha DENTRO del specular, así que va con su propia perilla.
            if (StyleMisc.x > 0.0 && direction.y > 0.02)
            {
                float toLayer = (StyleCloudBand.x - CameraPositionWS.y) / direction.y;
                if (toLayer > 0.0)
                {
                    float density = CloudDensity(CameraPositionWS.xyz + direction * toLayer);
                    sharp = lerp(sharp, float3(1.05, 1.05, 1.05), saturate(density * StyleMisc.x));
                }
            }

            float3 blurry = SkyIrradiance(float3(0.0, 1.0, 0.0)) * 1.5;
            float3 procedural = lerp(sharp, blurry, saturate(roughness * 0.9));
            if (EnvParams.x <= 0.0) return procedural;
            return EnvReflection(direction, roughness);
        }

        /// <summary>
        /// Altura del TERRENO del shader para esta escena: es un dato (ver
        /// <c>SceneStyle.TerrainFieldSelector</c>), no una mezcla de estilos. La rama la resuelve el
        /// compilador una vez por draw, como antes, así que el costo no cambia.
        /// </summary>
        float TerrainFieldHeight(float2 xz)
        {
            if (StyleMisc.y > 0.5) return AeroHeight(xz);
            return CanyonHeight(xz);
        }

        /// <summary>
        /// Sombra del TERRENO: marcha del campo de altura hacia el sol. Las lomas se dan sombra entre
        /// sí, que es lo que pone los valles en penumbra y le da profundidad al campo.
        /// </summary>
        float TerrainShadow(float3 worldPosition)
        {
            float3 sun = SunDirection();
            if (sun.y <= 0.03) return 0.0;

            float t = 2.5;
            float shadow = 1.0;
            // [loop]: las copias del campo de altura inlineadas por cada sitio de llamada
            // multiplicaban el trabajo del optimizador sin cambiar el resultado.
            [loop]
            for (int i = 0; i < 12; i++)
            {
                float3 samplePosition = worldPosition + sun * t;
                float ground = TerrainFieldHeight(samplePosition.xz);
                shadow = min(shadow, saturate((samplePosition.y - ground) * 0.30));
                t *= 1.6;
            }
            return shadow;
        }

        /// <summary>
        /// Sombra de los OBJETOS del primer plano: un rayo-esfera por caster contra el sol. Sin
        /// pasada de sombras (que costaría pipeline nuevo en las cuatro APIs), es lo que hace que el
        /// árbol y las flores se APOYEN en el pasto. Con penumbra: el borde se abre con la distancia
        /// porque el sol no es un punto.
        /// </summary>
        float ObjectShadow(float3 worldPosition)
        {
            float3 toSun = SunDirection();
            float shadow = 1.0;
            [loop]
            for (int i = 0; i < MaxShadowCasters; i++)
            {
                float4 caster = ShadowCasters[i];
                if (caster.w <= 0.0) continue;

                float3 toCenter = caster.xyz - worldPosition;
                float along = dot(toCenter, toSun);
                if (along <= 0.0) continue;

                float3 perpendicular = toCenter - toSun * along;
                float radius = caster.w + along * 0.045;
                shadow = min(shadow, smoothstep(radius * 0.5, radius, length(perpendicular)));
            }
            return shadow;
        }

        /// <summary>
        /// Suma de las LUCES PUNTUALES de la escena sobre un punto: faroles, carteles de neón y
        /// lámparas. Cada una es una esfera (posición + radio) con caída suave hasta el radio, así que
        /// una luz no deja un borde visible donde se apaga. Es el MISMO motor PBR que el sol (GGX +
        /// Fresnel): lo único distinto es que la dirección va del punto a la luz y que hay atenuación
        /// por distancia, así que un metal reacciona igual con el sol que con un cartel.
        /// </summary>
        float3 PointLightSum(float3 albedo, float metallic, float roughness, float3 n, float3 view, float3 worldPosition)
        {
            // El contador va en TimeAndParams.y (ver los backends): una escena sin luces puntuales
            // sale del bucle en la primera vuelta y no paga nada.
            int count = (int)TimeAndParams.y;
            if (count <= 0) return float3(0.0, 0.0, 0.0);

            float3 f0 = lerp(float3(0.045, 0.045, 0.045), albedo, metallic);
            float nDotV = saturate(dot(n, view)) + 1e-4;
            float3 sum = float3(0.0, 0.0, 0.0);

            // [loop] y NO [unroll]: el cuerpo es el PBR completo, y desenrollar ocho copias multiplica
            // el trabajo del optimizador sin cambiar el resultado (misma nota que en ShadeRock).
            [loop]
            for (int i = 0; i < MaxPointLights; i++)
            {
                if (i >= count) break;

                float4 positionRadius = PointLightPositionRadius[i];
                if (positionRadius.w <= 0.0) continue;

                float3 toLight = positionRadius.xyz - worldPosition;
                float distance = length(toLight);
                if (distance >= positionRadius.w) continue;

                float3 direction = toLight / max(1e-4, distance);
                float nDotL = saturate(dot(n, direction));
                if (nDotL <= 0.0) continue;

                // Caída: la ventana (1 - d/r)² apaga la luz justo en el radio (sin salto visible) y el
                // 1/(1 + d²·k) es la ley de cuadrado inverso con un piso, que evita el infinito sobre
                // la fuente. La k la elige el estilo de la escena.
                float window = saturate(1.0 - distance / positionRadius.w);
                float attenuation = window * window / (1.0 + distance * distance * 0.22);

                float3 halfway = normalize(direction + view);
                float nDotH = saturate(dot(n, halfway));
                float vDotH = saturate(dot(view, halfway));
                float3 fresnel = FresnelSchlick(vDotH, f0);
                float shape = DistributionGgx(nDotH, roughness) * GeometrySmith(nDotV, nDotL, roughness);
                float3 specular = shape * fresnel / max(1e-4, 4.0 * nDotV * nDotL);
                float3 diffuse = (1.0 - fresnel) * (1.0 - metallic) * albedo * (1.0 / 3.14159265);

                float4 colorIntensity = PointLightColorIntensity[i];
                sum += (diffuse + specular) * colorIntensity.rgb * colorIntensity.w * nDotL * attenuation;
            }
            return sum;
        }

        /// <summary>
        /// Sombra proyectada del SOL en un punto del mundo (ver README-SOMBRAS.md): proyecta el punto
        /// al espacio de luz y compara su profundidad contra el shadow map, con un PCF de 3×3.
        ///
        /// Es la diferencia con las sombras analíticas de esfera (<see cref="ObjectShadow"/>): acá la
        /// geometría proyecta SOLA —no hay que declarar una esfera por objeto— y las paredes, los autos
        /// y las cajas proyectan su forma real y no una mancha redonda.
        ///
        /// Devuelve 1 (sin sombra) fuera del volumen: si el volumen no cubriera todo, es preferible el
        /// borde del mapa a una sombra negra donde el mapa se termina.
        /// </summary>
        float ShadowFactor(float3 worldPosition)
        {
            if (ShadowParams.x <= 0.0) return 1.0;

            float4 lightClip = mul(float4(worldPosition, 1.0), LightViewProjection);
            float3 lightUv = lightClip.xyz / lightClip.w;
            lightUv.xy = lightUv.xy * 0.5 + 0.5;
            if (any(lightUv.xy < 0.0) || any(lightUv.xy > 1.0)) return 1.0;

            // El sesgo va en profundidad y en espacio de luz: es lo que evita el acné de sombra (una
            // superficie sombreándose a sí misma por el error de cuantización del mapa).
            lightUv.z -= ShadowParams.y;

            // 'texelStep' y no 'step': 'step' es una función intrínseca del lenguaje y taparla con una
            // variable es pedirle problemas al compilador (y al que lea el shader después).
            float texelStep = ShadowParams.z * ShadowParams.w;
            float total = 0.0;
            [unroll]
            for (int y = -1; y <= 1; y++)
            {
                [unroll]
                for (int x = -1; x <= 1; x++)
                {
                    total += ShadowMap.SampleCmpLevelZero(
                        ShadowSampler, lightUv.xy + float2(x, y) * texelStep, lightUv.z);
                }
            }

            // La fuerza mezcla contra 1 (sin sombra), así una escena puede pedir sombras suaves.
            return lerp(1.0, total * (1.0 / 9.0), ShadowParams.x);
        }

        /// <summary>
        /// Motor de sombreado. <paramref name="occlusion"/> llega ya calculada desde el sitio que
        /// llama: el terreno recibe también la sombra de los objetos y los objetos no (si no, una
        /// copa se haría sombra a sí misma y quedaría negra).
        /// </summary>
        float3 ShadePbr(float3 albedo, float metallic, float roughness, float3 n, float3 view, float3 light,
                        float3 worldPosition, float occlusion, float3 emissive)
        {
            float3 halfway = normalize(light + view);
            float nDotL = saturate(dot(n, light));
            float nDotV = saturate(dot(n, view)) + 1e-4;
            float nDotH = saturate(dot(n, halfway));
            float vDotH = saturate(dot(view, halfway));

            // f0 = 0,045 en dieléctricos (plástico, agua, hoja) y el ALBEDO en metales (cromo, vidrio
            // espejado): es lo que hace que el metal se vea metálico y no blanco.
            float3 f0 = lerp(float3(0.045, 0.045, 0.045), albedo, metallic);
            float3 fresnel = FresnelSchlick(vDotH, f0);
            float specularShape = DistributionGgx(nDotH, roughness) * GeometrySmith(nDotV, nDotL, roughness);
            float3 specular = specularShape * fresnel / max(1e-4, 4.0 * nDotV * nDotL);

            float3 diffuse = (1.0 - fresnel) * (1.0 - metallic) * albedo * (1.0 / 3.14159265);
            // El término DIRECTO del sol lleva la sombra proyectada; el ambiente y el IBL no (una
            // superficie en sombra sigue recibiendo el cielo, que es justamente lo que la hace leer
            // como sombra y no como un agujero negro).
            float3 color = (diffuse + specular) * SunColor() * StyleSunLight.w * nDotL * occlusion
                         * ShadowFactor(worldPosition);

            // Ambiente: el cielo ilumina el difuso y el reflejo del entorno va con la rugosidad.
            color += albedo * (1.0 - metallic) * SkyIrradiance(n) * lerp(0.70, 1.0, occlusion);
            color += SkyReflection(reflect(-view, n), roughness) * FresnelSchlick(nDotV, f0)
                   * (1.0 - roughness * 0.75);

            // Luces PUNTUALES de la escena (faroles, carteles, lámparas): una calle de noche se lee
            // por sus fuentes, no por el sol. El contador viaja en TimeAndParams.y y una escena sin
            // luces sale del bucle en la primera vuelta (no cuesta). El término se suma acá —y no en
            // cada material— porque es el mismo motor para todos: metal, vidrio, asfalto y tela
            // responden a la misma luz.
            color += PointLightSum(albedo, metallic, roughness, n, view, worldPosition) * occlusion;
            return color + emissive;
        }

        /// <summary>Pasto, lomas y lago del mundo Aero (materiales PBR + sombra del terreno).</summary>
        float3 ShadeAeroGround(VSOutput input, float3 n, float3 view, float3 light, float distanceToCamera)
        {
            float3 world = input.WorldPosition;

            // La altura y la normal se recalculan ACÁ, por píxel, del mismo campo que usa el vertex
            // shader. El VS las deja en cada vértice y el rasterizador las interpola: en la grilla
            // lejana (las celdas del warp cúbico miden decenas de unidades) dos triángulos vecinos
            // llevan normales distintas, y en el agua —que es un espejo— ese borde recto se ve como
            // una cuña clara cruzando el lago. Evaluado por píxel no hay borde que ver.
            float height = AeroHeight(world.xz);
            float gradeEpsilon = max(2.0, distanceToCamera * 0.02);
            float gradeX = AeroHeight(world.xz + float2(gradeEpsilon, 0.0));
            float gradeZ = AeroHeight(world.xz + float2(0.0, gradeEpsilon));
            n = normalize(float3(height - gradeX, gradeEpsilon, height - gradeZ));

            // Verde ESMERALDA con dos manchados (ancho y fino) y las lomas de adelante más claras
            // (pradera soleada). El tono es de pasto cortado, no de oliva: el Aero es verde de
            // juguete y esa saturación es la mitad de la estética.
            // El detalle procedural se APAGA con la distancia: un manchado de 36 unidades no se
            // puede muestrear a 200 (una muestra cae cada varios manchones) y lo único que queda es
            // una moiré de rombos sobre el campo. Es el "mipmap" que el ruido no tiene gratis, y sin
            // él la mitad lejana del pasto se veía dibujada a mano.
            float detailFade = 1.0 - saturate((distanceToCamera - 14.0) / 70.0);
            float coarseFade = 1.0 - saturate((distanceToCamera - 40.0) / 220.0);

            float3 albedo = lerp(float3(0.050, 0.360, 0.050), float3(0.130, 0.600, 0.080), Fbm2(world.xz * 0.0042, 3));
            albedo *= lerp(1.0, 0.80 + 0.40 * Fbm2(world.xz * 0.0280, 3), coarseFade);
            albedo = lerp(albedo, float3(0.330, 0.740, 0.110), 0.28 * saturate((world.z - CameraPositionWS.z) * 0.0018));

            // Relieve FINO del pasto: la normal se perturba con el gradiente del ruido. Dos escalas:
            // los matones (bultos de 3 unidades) y el grano fino de las briznas. Es lo que hace que
            // el campo tenga sombreado propio en vez de ser una chapa verde.
            float2 coarse = world.xz * 0.30;
            float2 fine = world.xz * 1.10;
            float c0 = Fbm2(coarse, 2);
            float cx = Fbm2(coarse + float2(0.35, 0.0), 2);
            float cz = Fbm2(coarse + float2(0.0, 0.35), 2);

            // El grano FINO ya no sale del ruido (que no sabe dibujar una brizna) sino del atlas: una
            // celda de pasto cubre ~1,6 unidades de mundo, así que de cerca se ven las briznas y el
            // gradiente de TRES muestras de la misma celda perturba la normal. Cuesta menos que el
            // ruido de dos octavas que reemplaza (tres muestras contra seis octavas de ALU).
            float blade0 = DetailSample(world.xz, 0.62, float2(0.0, 0.0));
            float bladeX = DetailSample(world.xz + float2(0.16, 0.0), 0.62, float2(0.0, 0.0));
            float bladeZ = DetailSample(world.xz + float2(0.0, 0.16), 0.62, float2(0.0, 0.0));

            // El COLOR del pasto también sale del atlas (verde oscuro a verde claro): es lo que hace
            // que el campo se lea como césped y no como una mancha verde. Lejos cae al punto medio
            // (sin modulación), que es lo que evita el hervido del detalle en el horizonte.
            float bladeColor = blade0 * detailFade + (1.0 - detailFade) * 0.5;
            albedo *= lerp(float3(0.80, 0.88, 0.74), float3(1.14, 1.22, 0.94), bladeColor);

            // El peso va al grano FINO, no a los bultos de 3 unidades: en la referencia el césped es
            // parejo (apenas un microrrelieve). Con los bultos pesando más el campo se veía como una
            // manta ondulada y no como pasto cortado.
            n = normalize(n + float3((c0 - cx) * 1.1 * coarseFade + (blade0 - bladeX) * 2.6 * detailFade, 0.0,
                                     (c0 - cz) * 1.1 * coarseFade + (blade0 - bladeZ) * 2.6 * detailFade));

            float occlusion = TerrainShadow(world) * ObjectShadow(world);
            float3 color = ShadePbr(albedo, 0.0, 0.72, n, view, light, world, occlusion, 0.0);

            // ---- Sendero de tierra (el paseo de la cámara, estilo XP) ----
            // PINTADO, no tallado: una franja que sigue AeroPathX con bordes suaves. Tierra pisada
            // con grano del atlas; desparejo con dos ondas para que no parezca una cinta recta.
            float pathOffset = abs(world.x - AeroPathX(world.z))
                             + 6.0 * sin(world.z * 0.008 + 1.3)
                             + 2.5 * sin(world.x * 0.020);
            float pathMask = 1.0 - smoothstep(7.0, 14.0, pathOffset);
            float3 dirt = lerp(float3(0.360, 0.260, 0.150), float3(0.520, 0.400, 0.240),
                               DetailSample(world.xz * 0.7, 1.1, float2(3.0, 1.0)));
            dirt *= 0.85 + 0.15 * sin(world.z * 0.05);
            color = lerp(color, dirt * saturate(dot(n, light) * 0.5 + 0.5) * occlusion, pathMask * 0.92);

            // ---- Lago ----
            // Debajo del nivel del agua el terreno es agua: la profundidad decide la mezcla, así que
            // la orilla queda mojada en vez de recortada con tijera.
            float depth = WaterLevel - height;

            // ---- Torrente del desagüe (los rápidos, camino a la cascada) ----
            // PINTADO dentro del cauce tallado en el terreno: verde-azulado, corrugado con dos
            // escalas de ruido que CORREN hacia el sur (dirección del desagüe), espuma blanca en
            // el centro del cauce y en los parches rápidos, y brillo especular alto.
            float riverDepth = WaterLevel - height;
            if (riverDepth > 0.1)
            {
                float bank = abs(world.x - AeroRiverX(world.z)) - (11.0 + 5.0 * sin(world.z * 0.013));
                float inRiver = 1.0 - smoothstep(-14.0, -4.0, bank);
                if (inRiver > 0.001)
                {
                    float t = TimeAndParams.x;
                    float chop = Fbm2(world.xz * 0.16 + float2(0.0, -t * 1.7), 2)
                               + 0.5 * Fbm2(world.xz * 0.45 + float2(0.0, -t * 3.1), 2);
                    float3 rapids = lerp(float3(0.16, 0.42, 0.46), float3(0.30, 0.62, 0.62), chop);
                    float foam = smoothstep(0.30, 0.55, chop) * inRiver;
                    rapids = lerp(rapids, float3(0.96, 0.98, 1.00), foam);
                    // El cauce se estrecha hacia la cascada (z negativo): más pendiente, más espuma.
                    rapids = lerp(rapids, float3(0.90, 0.95, 0.98), saturate((-world.z - 60.0) / 220.0) * 0.45);
                    color = lerp(color, rapids * saturate(dot(n, light) * 0.4 + 0.6), inRiver);
                    color += SunColor() * foam * 0.20;
                }
            }

            if (depth > -0.4)
            {
                float blend = saturate(depth * 0.45 + 0.28);

                float2 p = world.xz;
                float time = TimeAndParams.x;
                // Las ondas del lago aguantan mucho más que el detalle del pasto: la longitud de onda
                // de la ola (28 unidades) sigue por encima del paso de muestreo hasta muy lejos, así
                // que apagarlas a 80 unidades dejaba el lago como una chapa.
                float rippleFade = 1.0 - saturate((distanceToCamera - 25.0) / 400.0);
                float waveHeight = Fbm2(p * 0.035 + float2(time * 0.18, time * 0.07), 3)
                                 + 0.50 * rippleFade * Fbm2(p * 0.160 - float2(time * 0.11, time * 0.26), 2)
                                 // Y encima el atlas de agua: el ruido da la ola, la textura da la
                                 // superficie (una celda cada ~0,8 unidades). Se desplaza con el
                                 // tiempo para que la textura no quede clavada al mundo.
                                 + 0.34 * rippleFade * (DetailSample(p + float2(time * 0.22, -time * 0.16), 1.25, float2(2.0, 0.0)) - 0.5);
                float epsilon = 0.7;
                float dx = Fbm2((p + float2(epsilon, 0.0)) * 0.035 + float2(time * 0.18, time * 0.07), 3)
                         - Fbm2((p - float2(epsilon, 0.0)) * 0.035 + float2(time * 0.18, time * 0.07), 3);
                float dz = Fbm2((p + float2(0.0, epsilon)) * 0.035 + float2(time * 0.18, time * 0.07), 3)
                         - Fbm2((p - float2(0.0, epsilon)) * 0.035 + float2(time * 0.18, time * 0.07), 3);

                // El agua es un dieléctrico CASI ESPEJO (rugosidad 0,035): con PBR eso significa
                // reflejo del cielo CON NUBES (SkyReflection las muestrea) y el brillo del sol por
                // microfacetas, en vez de un degradado pintado.
                // Las olas también se aplanan: lejos, el agua tiene que ser un espejo liso del cielo
                // y no una textura de ondas muestreada por debajo de Nyquist.
                float waveFade = 1.0 - saturate((distanceToCamera - 25.0) / 380.0);
                float3 waterNormal = normalize(float3(-dx * 1.2 * waveFade, 1.0, -dz * 1.2 * waveFade));
                float3 water = ShadePbr(float3(0.012, 0.070, 0.120), 0.0, 0.035, waterNormal, view, light, world, occlusion, 0.0);

                // Chispas: donde la ola enfrenta al sol y el ruido lo permite. Van contadas, si no
                // el lago entero se quema a blanco y pierde el azul.
                float sparkle = pow(saturate(Fbm2(p * 0.9 + float2(time * 0.6, -time * 0.5), 2) * 1.7 - 1.05), 2.0);
                water += SunColor() * sparkle * rippleFade * saturate(dot(waterNormal, light)) * 1.2;
                water += (waveHeight - 0.5) * 0.03;

                // Cuerpo del agua: donde el lago es hondo, lo que vuelve no es el espejo del cielo
                // sino la luz que atravesó el agua y volvió filtrada (absorción). Sin esto un lago
                // sin reflejos del entorno (no hay SSR en ninguna de las cuatro APIs) es una lámina
                // de aluminio que refleja el horizonte blanco.
                water = lerp(water, float3(0.010, 0.075, 0.160), saturate(depth / 30.0) * 0.62);

                // Barro del fondo: con poca agua se TIENE que ver el lecho, y el lecho de un lago
                // de pradera no es el mismo pasto de la superficie sino tierra oscura con algas.
                // A más de ~2,5 unidades de profundidad la absorción se come el lecho y el agua
                // queda en su color propio (la mezcla de arriba ya lo hace). Sin esto el lago
                // parecía vidrio verde con el mismo césped de arriba pintado abajo.
                float3 bed = lerp(float3(0.160, 0.120, 0.070), float3(0.070, 0.090, 0.040),
                                  Fbm2(world.xz * 0.11, 3));   // barro con parches de alga
                bed *= 0.55 + 0.45 * DetailSample(world.xz * 0.9, 1.4, float2(3.0, 1.0));
                float bedVisibility = saturate(1.0 - depth / 2.5) * saturate(depth * 2.0);
                water = lerp(water, bed * saturate(SunColor() * 0.5 + 0.5), bedVisibility * 0.80);

                color = lerp(color, water, blend);
            }

            return color;
        }

        /// <summary>
        /// Brizna de pasto: verde con la luz del sol, translúcida hacia el sol y con la sombra de
        /// los objetos encima. La normal se corrige contra la vista porque la brizna NO tiene
        /// espesor: sin eso, media brizna se vería negra desde cada lado.
        /// </summary>
        float3 ShadeGrassBlade(VSOutput input, float3 nRaw, float3 view, float3 light)
        {
            float3 n = FaceNormal(nRaw, view);
            float3 world = input.WorldPosition;

            // Verde algo más claro y más variado que el del campo, y la punta se aclara: es donde
            // pega el sol y donde el pasto de la referencia se ve amarillo.
            float3 albedo = lerp(float3(0.070, 0.400, 0.055), float3(0.190, 0.700, 0.090), input.Params.w);
            albedo *= lerp(0.82, 1.28, saturate(input.Local.y));

            // La brizna recibe la sombra de los objetos (el árbol) pero NO la del terreno: son
            // objetos chicos y la marcha de sombra del terreno costaría más que toda la hierba.
            float occlusion = ObjectShadow(world);
            float3 color = ShadePbr(albedo, 0.0, 0.62, n, view, light, world, occlusion, 0.0);

            // Translucidez: mirando al sol a través del pasto la brizna se enciende (el sol la
            // atraviesa). Sin esto, contra el sol el prado entero se ve casi negro.
            float through = pow(saturate(dot(-view, light)), 2.2) * saturate(1.0 - abs(dot(n, view)));
            color += SunColor() * albedo * through * 1.7 * occlusion;
            return color;
        }

        /// <summary>
        /// Brillo de una partícula: un punto de luz cálida sin sombreado (el cuadro mira a la cámara y
        /// no tiene cara). Devuelve solo la LUZ: el color y la forma los pone el sprite de la celda del
        /// atlas (ver la rama de las partículas en <c>PSMain</c>).
        /// </summary>
        float3 ShadePollen(VSOutput input, float3 view)
        {
            // Se ve porque el sol la ilumina: de espaldas al sol la partícula se apaga.
            float facing = saturate(dot(-view, SunDirection()) * 0.5 + 0.65);
            // La variante sale de la FASE de la instancia (0..1) y no de un dato aparte: así cada
            // partícula tiene su punto de blanco y de cálido sin agregar un parámetro por instancia.
            float variation = frac(input.Params.z * 5.1331 + 0.37);
            float3 tint = lerp(float3(1.00, 0.95, 0.74), float3(0.88, 0.97, 1.00), variation);
            return tint * (0.45 + 1.30 * facing) * 1.05;
        }

        /// <summary>Burbuja de jabón: lo que se ve a través es el cielo, el borde es iridiscente.</summary>
        /// <summary>
        /// Pompa de jabón con transparencia REAL: la película de agua deja pasar casi toda la luz
        /// (alfa mínimo en el centro) y refleja en el borde (el Fresnel sube el alfa y pinta la
        /// iridiscencia). Devuelve el color y el ALFA por separado: la mezcla es la pasada
        /// premultiplicada que ya tiene el motor (la misma del polen), no un truco de "fondo
        /// pintado adentro" que no deja ver la ciudad a través de la burbuja.
        /// </summary>
        float3 ShadeBubble(VSOutput input, float3 nRaw, float3 view, float3 light, out float alpha)
        {
            float3 n = FaceNormal(nRaw, view);
            float facing = saturate(dot(n, view));
            float fresnel = pow(1.0 - facing, 2.4);

            // Iridiscencia: el arcoíris del borde. Es un coseno sobre el Fresnel, y a ojo se lee
            // igual que la interferencia de una pompa de jabón de verdad.
            float3 iridescence = 0.5 + 0.5 * cos(6.2831 * (fresnel * 1.35 + float3(0.00, 0.33, 0.66)));

            // Borde: reflejo del cielo + el sol (el motor PBR con la película como albedo casi
            // negro) y la iridiscencia pintada encima. Centro: alfa de película (3%) — apenas
            // oscurece lo de atrás, como una pompa real.
            float3 mirrored = SkyReflection(reflect(-view, n), 0.04);
            float3 surface = ShadePbr(float3(0.015, 0.020, 0.030), 0.0, 0.05, n, view, light,
                                      input.WorldPosition, TerrainShadow(input.WorldPosition), 0.0);
            float3 color = (surface + mirrored * 0.45 + iridescence * 0.65) * fresnel;
            alpha = 0.03 + fresnel * 0.62;

            // Estallido lejano: las burbujas que pasan a más de ~400 unidades se desvanecen antes
            // de desaparecer de golpe contra la ciudad (el billboard muere de golpe, no hay LOD).
            alpha *= saturate(1.0 - (distance(input.WorldPosition, CameraPositionWS.xyz) - 400.0) / 300.0);
            return color;
        }

        /// <summary>Torre de vidrio y cromo del skyline.</summary>
        float3 ShadeTower(VSOutput input, float3 n, float3 view, float3 light)
        {
            float y = saturate(input.Local.y);
            float variant = input.Params.w;

            // Vidrio azul que se aclara hacia arriba (el cielo pega en la punta) y CROMO en el
            // cinturón y la aguja. El parámetro de la instancia tinte cada torre del skyline.
            float3 baseColor = lerp(float3(0.070, 0.190, 0.430), float3(0.240, 0.440, 0.720), saturate(variant));
            float3 glass = lerp(baseColor, float3(0.700, 0.840, 0.960), pow(y, 2.2));
            if (y > 0.90) glass = float3(0.760, 0.800, 0.870);      // cinturón y aguja: cromo

            // Pisos: bandas de ventanas y travesaños. Las ventanas son vidrio MÁS LISO (menos
            // rugosidad) y más claro: el reflejo del cielo ahí sale nítido y es lo que se ve como
            // "ventanas" a la distancia.
            float bands = frac(y * 26.0);
            float window = smoothstep(0.08, 0.26, bands) * (1.0 - smoothstep(0.70, 0.94, bands));

            // Paños de fachada: columnas verticales (mullions) que rompen el prisma y le dan
            // escala. Cada torre lleva su desfasaje (variant) para que el patrón no sea calcado
            // entre edificios.
            float panes = frac(input.Local.x * 7.0 + variant * 9.0);
            float columns = 1.0 - smoothstep(0.40, 0.50, min(panes, 1.0 - panes));

            // Ventanas encendidas: UNAS POCAS luces cálidas por celda (ruido por celda), que es lo
            // que hace que la ciudad se lea habitada y no como un adhesivo de vidrio.
            float cell = floor(input.Local.x * 7.0 + variant * 9.0) + floor(y * 26.0) * 11.0;
            float lit = step(0.78, Fbm2(float2(cell * 3.1, cell * 7.7), 2));

            float roughness = lerp(0.30, 0.08, window) * (y > 0.90 ? 0.5 : 1.0);
            float3 albedo = glass * (0.60 + 0.70 * window);
            albedo *= 0.84 + 0.16 * columns;   // las columnas son aluminio: apenas más oscuras

            // Luz propia: ventanas encendidas (cálidas) más el rebote del cielo en el vidrio. La
            // baliza de la antena (la punta) parpadea.
            float3 emissive = float3(0.05, 0.09, 0.15) * window;
            emissive += float3(1.00, 0.86, 0.55) * window * lit * 0.85;
            // Sin antena ni baliza en el techo: los edificios terminan en la LOSA con parapeto,
            // como un edificio de oficinas de verdad.
            return ShadePbr(albedo, 0.90, roughness, n, view, light,
                            input.WorldPosition, TerrainShadow(input.WorldPosition), emissive);
        }

        /// <summary>Árbol: tronco con corteza y copa lustrosa (el verde de la postal).</summary>
        float3 ShadeTree(VSOutput input, float3 nRaw, float3 view, float3 light)
        {
            float3 n = FaceNormal(nRaw, view);
            float3 local = input.Local;
            float variant = input.Params.w;

            float occlusion = TerrainShadow(input.WorldPosition);
            if (local.y < 0.56)
            {
                // Corteza: rugosa y mate, con el grano del tronco (la celda de corteza del atlas
                // tiene las fibras verticales dibujadas, que es lo que el ruido no da).
                float3 bark = float3(0.230, 0.160, 0.095);
                bark *= 0.70 + 0.50 * Fbm2(float2(local.y * 9.0, local.x * 14.0), 2);
                bark *= 0.72 + 0.62 * DetailSample(float2(local.x * 2.4, local.y * 1.2), 1.6, float2(1.0, 1.0));
                return ShadePbr(bark, 0.0, 0.88, n, view, light, input.WorldPosition, occlusion, 0.0);
            }

            // Hoja: rugosidad media (tiene brillo, pero no es vidrio) y TRANSLUCIDEZ — la luz que
            // atraviesa la hoja desde atrás es lo que hace que una copa contra el sol se vea viva y
            // no como una bola oscura. Es el término emisivo.
            float3 leaf = lerp(float3(0.045, 0.280, 0.055), float3(0.140, 0.520, 0.120), saturate(variant));
            leaf *= 0.72 + 0.56 * Fbm2(float2(local.x * 6.0 + local.y * 3.0, local.z * 6.0), 3);
            // Hojas: la celda del atlas dibuja los matones de la copa (una hoja de verdad no entra en
            // un píxel a esta distancia, pero la mancha sí, y es lo que le saca el aspecto de bola).
            leaf *= 0.55 + 0.85 * DetailSample(float2(local.x + local.z, local.y * 1.3), 3.1, float2(0.0, 1.0));
            float3 translucent = leaf * pow(saturate(dot(-n, light)), 2.0) * 0.45;
            return ShadePbr(leaf, 0.0, 0.42, n, view, light, input.WorldPosition, occlusion, translucent);
        }

        /// <summary>
        /// Casa urbana con balcones: el retranqueo por piso de la malla marca dónde va cada
        /// material (retranqueo = losa de balcón, pared entre balcones, vidrio en el plano del
        /// balcón) y la planta baja es un local con toldo pintado.
        /// </summary>
        float3 ShadeHouse(VSOutput input, float3 nRaw, float3 view, float3 light)
        {
            float3 n = FaceNormal(nRaw, view);
            float y = saturate(input.Local.y);
            float variant = input.Params.w;

            // Vidrio en el plano del balcón (retrocedido): puertas ventanas de piso a techo.
            float slab = 1.0 - smoothstep(0.015, 0.045, abs(frac(y * 5.0 + 0.5) - 0.5) - 0.155);
            float glassBand = smoothstep(0.08, 0.14, frac(y * 5.0));
            float glass = saturate(slab * 0.8 + glassBand * (1.0 - slab)) * step(0.10, y);

            float3 plaster = lerp(float3(0.88, 0.84, 0.76), float3(0.93, 0.89, 0.80), variant);
            plaster *= 0.90 + 0.10 * frac(variant * 7.31);
            float3 frame = float3(0.42, 0.46, 0.50);   // barandas y marcos: aluminio
            float3 roof = float3(0.30, 0.32, 0.34);    // losa del techo

            float3 albedo = plaster;
            albedo = lerp(albedo, frame, saturate(slab * 1.2));
            albedo = lerp(albedo, float3(0.16, 0.24, 0.34), glass * (1.0 - slab));
            if (y > 0.985) albedo = roof;
            // Planta baja: negocio con toldo (verde del barrio).
            if (y < 0.10) albedo = lerp(float3(0.14, 0.24, 0.32), float3(0.20, 0.48, 0.28), step(0.05, y));

            float roughness = lerp(0.75, 0.12, glass);
            if (y > 0.985) roughness = 0.55;
            float3 emissive = float3(1.00, 0.86, 0.58) * glass * 0.22;   // interior encendido suave
            return ShadePbr(albedo, 0.05, roughness, n, view, light,
                            input.WorldPosition, TerrainShadow(input.WorldPosition), emissive);
        }

        /// <summary>
        /// Local comercial: escaparate de vidrio liso en la planta baja, cartel iluminado en la
        /// banda alta del frente y techo con parapeto.
        /// </summary>
        float3 ShadeShop(VSOutput input, float3 nRaw, float3 view, float3 light)
        {
            float3 n = FaceNormal(nRaw, view);
            float y = saturate(input.Local.y);
            float variant = input.Params.w;

            // Colores del cartel por variante: la calle no repite letrero. Sin operador ternario
            // anidado: el SPIR-V del dxc exige condición y ramas del mismo tamaño de vector.
            float pick = frac(variant * 3.7);
            float3 sign0 = lerp(float3(0.95, 0.30, 0.20), float3(0.98, 0.72, 0.10), step(0.33, pick));
            sign0 = lerp(sign0, float3(0.10, 0.62, 0.85), step(0.66, pick));

            float3 stucco = lerp(float3(0.82, 0.77, 0.68), float3(0.75, 0.72, 0.66), frac(variant * 5.13));
            float3 glass = float3(0.12, 0.20, 0.28);
            float3 albedo = stucco;
            if (y < 0.42) albedo = glass;                          // escaparate
            if (y > 0.52 && y < 0.60) albedo = sign0;              // banda del cartel
            if (y > 0.97) albedo = float3(0.28, 0.30, 0.32);       // parapeto

            float roughness = lerp(0.80, 0.10, step(y, 0.42));
            // Interior del local encendido + cartel retroiluminado (branchless: ver FaceNormal).
            float3 emissive = float3(1.00, 0.90, 0.65) * (0.30 * step(y, 0.42));
            emissive += sign0 * (1.10 * step(0.52, y) * step(y, 0.60));
            return ShadePbr(albedo, 0.0, roughness, n, view, light,
                            input.WorldPosition, TerrainShadow(input.WorldPosition), emissive);
        }

        /// <summary>Girasol: tallo, pétalos amarillos y disco con semillas.</summary>
        float3 ShadeSunflower(VSOutput input, float3 nRaw, float3 view, float3 light)
        {
            float3 n = FaceNormal(nRaw, view);
            float3 local = input.Local;

            float occlusion = TerrainShadow(input.WorldPosition);
            if (local.y < 1.46)
            {
                float3 stem = float3(0.085, 0.320, 0.100);
                return ShadePbr(stem, 0.0, 0.62, n, view, light, input.WorldPosition, occlusion, 0.0);
            }

            float2 head = float2(local.x, local.y - 1.54);
            float radius = length(head);
            if (radius < 0.15)
            {
                // Disco central: marrón rugoso con las semillas.
                float3 disc = float3(0.170, 0.095, 0.030);
                disc *= 0.62 + 0.75 * Fbm2(head * 120.0, 2);
                return ShadePbr(disc, 0.0, 0.78, n, view, light, input.WorldPosition, occlusion, 0.0);
            }

            // Pétalo: fino y traslúcido (la luz lo atraviesa: de ahí el amarillo encendido del
            // borde de la flor contra el sol).
            float t = saturate((radius - 0.12) / 0.30);
            float3 petal = lerp(float3(0.900, 0.430, 0.020), float3(0.990, 0.830, 0.090), t);
            float3 translucent = petal * pow(saturate(dot(-n, light)), 1.6) * 0.42;
            return ShadePbr(petal, 0.0, 0.38, n, view, light, input.WorldPosition, occlusion, translucent);
        }

        /// <summary>Rosa: tallo y capullo rosa (brillo de terciopelo, no de plástico).</summary>
        float3 ShadeRose(VSOutput input, float3 nRaw, float3 view, float3 light)
        {
            float3 n = FaceNormal(nRaw, view);
            float3 local = input.Local;

            float occlusion = TerrainShadow(input.WorldPosition);
            if (local.y < 0.98)
            {
                float3 stem = float3(0.070, 0.270, 0.085);
                return ShadePbr(stem, 0.0, 0.66, n, view, light, input.WorldPosition, occlusion, 0.0);
            }

            // Capullo: terciopelo — rugosidad ALTA (el brillo se abre en un paño suave en vez del
            // punto duro de un plástico) y un rebote translúcido que le da el rosa encendido.
            float3 petal = lerp(float3(0.680, 0.060, 0.190), float3(0.940, 0.330, 0.500), saturate(n.y * 0.5 + 0.5));
            float3 translucent = petal * pow(saturate(dot(-n, light)), 1.8) * 0.35;
            return ShadePbr(petal, 0.0, 0.55, n, view, light, input.WorldPosition, occlusion, translucent);
        }

        /// <summary>Gaviota: blanca con la punta del ala gris.</summary>
        float3 ShadeGull(VSOutput input, float3 nRaw, float3 view, float3 light)
        {
            float3 n = FaceNormal(nRaw, view);
            // Pluma: casi blanca y rugosa (a contraluz el ala se ve encendida: traslúcida).
            float wingTip = smoothstep(0.28, 0.52, abs(input.Local.x));
            float3 albedo = lerp(float3(0.960, 0.965, 0.975), float3(0.560, 0.610, 0.690), wingTip);
            float3 translucent = albedo * pow(saturate(dot(-n, light)), 1.5) * 0.30;
            return ShadePbr(albedo, 0.0, 0.55, n, view, light, input.WorldPosition, 1.0, translucent);
        }

        /// <summary>Globo aerostático: envelope a franjas y canasta de mimbre.</summary>
        float3 ShadeBalloon(VSOutput input, float3 nRaw, float3 view, float3 light)
        {
            float3 n = FaceNormal(nRaw, view);
            float3 local = input.Local;

            if (local.y < 0.27)
            {
                // Canasta de mimbre: muy rugosa, con el tejido.
                float3 wicker = float3(0.290, 0.180, 0.085);
                wicker *= 0.70 + 0.55 * Fbm2(float2(local.x * 40.0, local.y * 60.0), 2);
                return ShadePbr(wicker, 0.0, 0.90, n, view, light, input.WorldPosition, 1.0, 0.0);
            }

            // Tela del envelope: franjas verticales, rugosidad media y traslucidez (la seda del
            // globo deja pasar la luz del sol).
            float angle = atan2(local.x, local.z) / 6.28318 + 0.5;
            float band = frac(angle * 9.0);
            // Ternarios anidados → lerp/step: el SPIR-V del dxc no acepta condición escalar con
            // ramas vectoriales en un Select (falla la validación, ver el cartel del local).
            float3 stripe = lerp(float3(0.820, 0.110, 0.150), float3(0.930, 0.740, 0.120), step(0.34, band));
            stripe = lerp(stripe, float3(0.110, 0.340, 0.760), step(0.67, band));
            float3 fabric = lerp(stripe, float3(0.960, 0.960, 0.970), 0.28);
            float3 translucent = fabric * pow(saturate(dot(-n, light)), 1.6) * 0.45;
            return ShadePbr(fabric, 0.0, 0.62, n, view, light, input.WorldPosition, 1.0, translucent);
        }

        /// <summary>Mariposa: azul iridiscente con el borde oscuro.</summary>
        float3 ShadeButterfly(VSOutput input, float3 nRaw, float3 view, float3 light)
        {
            float3 n = FaceNormal(nRaw, view);
            float3 local = input.Local;

            float body = 1.0 - smoothstep(0.02, 0.05, length(local.xz));
            if (body > 0.55)
            {
                return ShadePbr(float3(0.070, 0.065, 0.090), 0.0, 0.60, n, view, light, input.WorldPosition, 1.0, 0.0);
            }

            // Ala: azul con el borde oscuro, medio metálica (las escamas de la mariposa son
            // iridiscentes) y con el velo azul del borde iluminado.
            float span = saturate(length(local.xz) / 0.36);
            float3 wing = lerp(float3(0.070, 0.290, 0.900), float3(0.015, 0.040, 0.150), smoothstep(0.62, 1.0, span));
            float3 iridescence = float3(0.10, 0.35, 0.75) * pow(1.0 - saturate(dot(n, view)), 2.0);
            return ShadePbr(wing, 0.35, 0.20, n, view, light, input.WorldPosition, 1.0, iridescence * 0.35);
        }

        /// <summary>
        /// Material con TEXTURAS: el camino que usan los modelos glTF y la geometría de los dioramas.
        /// Albedo, mapa de normales y ARM (oclusión, rugosidad, metalicidad) salen del atlas de
        /// materiales (ver <c>Scenes.MaterialAtlas</c>) por la ranura que trae la instancia, y la
        /// tinta, la escala de UV y las fuerzas del material viajan con ella.
        ///
        /// El marco tangente se arma por DERIVADAS en vez de por vértice: las mallas del motor no
        /// traen tangentes, y agregarlas engordaría cada vértice un 40% para todos los modelos —incluso
        /// los que no tienen mapa de normales—. Con el gradiente de la posición de mundo y de la UV
        /// (el marco de Mikkelsen) el relieve sale igual de bien y sobrevive al instanciado.
        /// </summary>
        float3 ShadeTextured(VSOutput input, float3 nRaw, float3 view, float3 light)
        {
            float3 n = FaceNormal(nRaw, view);
            float slot = input.Params.w;
            float2 uv = input.Uv;

            // El albedo se decodifica a mano: el atlas es UN arreglo de texturas para todos los
            // materiales, así que no puede ser sRGB por rebanada (la normal y el ARM van lineales).
            float3 albedo = pow(saturate(MaterialMaps.Sample(MaterialSampler, float3(uv, slot)).rgb), 2.2)
                          * input.MaterialTint.rgb;
            float3 packed = MaterialMaps.Sample(MaterialSampler, float3(uv, slot + 1.0)).rgb;
            float3 arm = MaterialMaps.Sample(MaterialSampler, float3(uv, slot + 2.0)).rgb;

            float3 dpdx = ddx(input.WorldPosition);
            float3 dpdy = ddy(input.WorldPosition);
            float2 duvdx = ddx(uv);
            float2 duvdy = ddy(uv);
            float3 tangent = dpdx * duvdy.y - dpdy * duvdx.y;
            float3 bitangent = dpdy * duvdx.x - dpdx * duvdy.x;
            float inverse = rsqrt(max(1e-12, max(dot(tangent, tangent), dot(bitangent, bitangent))));

            float3 mapped = packed * 2.0 - 1.0;
            mapped.xy *= input.MaterialParams.x;
            n = normalize(mul(float3x3(tangent * inverse, bitangent * inverse, n), mapped));

            float occlusion = arm.r;
            float roughness = saturate(arm.g * input.MaterialParams.y);
            float metallic = saturate(arm.b * input.MaterialParams.z);
            // Emisión: el objeto BRILLA con su propia tinta (carteles de neón, ventanas encendidas,
            // pantallas). Un material con fuerza cero es un objeto común y no paga nada por esto.
            float3 emissive = input.MaterialTint.rgb * input.MaterialParams.w;

            return ShadePbr(albedo, metallic, roughness, n, view, light,
                            input.WorldPosition, occlusion, emissive);
        }

        /// <summary>
        /// Destellos de cuatro puntas sobre el pasto y el agua. Van en PÍXELES y no en NDC: una
        /// rejilla en NDC tiene celdas más anchas que altas (según el aspecto de la ventana) y el
        /// halo las rellenaba enteras, que es el artefacto que se veía como papeles blancos
        /// flotando en el cielo. Acá la celda es cuadrada en pantalla, cada brazo cae con su propia
        /// exponencial (puntas finas, centro brillante) y solo enciende una de cada catorce celdas.
        /// </summary>
        /// <summary>
        /// Posición de un fragmento en PÍXELES contada desde ARRIBA a la izquierda de la imagen, que
        /// es la convención de Direct3D y Vulkan, en las cuatro APIs.
        ///
        /// El rasterizador de OpenGL numera las filas desde ABAJO (gl_FragCoord.y crece hacia
        /// arriba), así que el MISMO píxel de la imagen llega con la Y espejada: una rejilla en
        /// píxeles —el destello de suelo— caería en espejo respecto de las otras tres APIs, que es
        /// exactamente lo que el benchmark no puede permitirse (la misma escena, dos dibujos).
        /// El backend dice la altura (TimeAndParams.z) para poder convertirla; en las APIs que ya
        /// vienen en esta convención va en 0 y acá no se toca nada.
        /// </summary>
        float2 ScreenPixel(float2 fragment)
        {
            return TimeAndParams.z > 0.0
                ? float2(fragment.x, TimeAndParams.z - fragment.y)
                : fragment;
        }

        float3 GroundSparkle(float2 pixel, float time)
        {
            const float cellSize = 44.0;
            float2 cell = floor(pixel / cellSize);
            float h = Hash13(float3(cell, 11.3));
            if (h < 0.93) return float3(0.0, 0.0, 0.0);

            float2 delta = (pixel - (cell + 0.5) * cellSize) / (cellSize * 0.5);
            float twinkle = pow(saturate(sin(time * 1.9 + h * 51.0) * 0.5 + 0.5), 6.0);
            float cross0 = exp(-abs(delta.x) * 7.0) * exp(-abs(delta.y) * 2.2)
                         + exp(-abs(delta.y) * 7.0) * exp(-abs(delta.x) * 2.2);
            float core = exp(-dot(delta, delta) * 4.0) * 0.9;
            return float3(1.0, 1.0, 0.96) * (cross0 + core) * twinkle * 0.85;
        }

        float4 PSMain(VSOutput input) : SV_Target
        {
            float3 n = normalize(input.Normal);
            float3 view = normalize(CameraPositionWS.xyz - input.WorldPosition);
            float3 light = normalize(-LightDirectionWS.xyz);
            float distanceToCamera = length(CameraPositionWS.xyz - input.WorldPosition);

            float kind = input.Params.x;
            float3 color;
            // Alfa: 1 en todo lo opaco (la mezcla está apagada) y la máscara del cuadro en el polen.
            float alpha = 1.0;   // 1 = opaco (todo menos polen y burbujas, que lo bajan)
            if (kind < 0.5)
            {
                color = ShadeRock(input, n, view, light);
            }
            else if (kind < 3.5)
            {
                color = ShadeAeroGround(input, n, view, light, distanceToCamera);
                // Los destellos van encima del pasto y del agua, que es donde están en la referencia.
                color += GroundSparkle(ScreenPixel(input.Position.xy), TimeAndParams.x);

            }
            else if (kind < 4.5)
            {
                // La burbuja DEVUELVE su alfa (película de jabón): es lo que hace que la ciudad se
                // vea a través del centro y el borde quede con el aro brillante.
                color = ShadeBubble(input, n, view, light, alpha);
            }
            else if (kind < 5.5)
            {
                color = ShadeTower(input, n, view, light);
            }
            else if (kind < 6.5)
            {
                color = ShadeTree(input, n, view, light);
            }
            else if (kind < 7.5)
            {
                color = ShadeSunflower(input, n, view, light);
            }
            else if (kind < 8.5)
            {
                color = ShadeRose(input, n, view, light);
            }
            else if (kind < 9.5)
            {
                color = ShadeGull(input, n, view, light);
            }
            else if (kind < 10.5)
            {
                color = ShadeBalloon(input, n, view, light);
            }
            else if (kind < 11.5)
            {
                color = ShadeButterfly(input, n, view, light);
            }
            else if (kind < 13.5)
            {
                color = ShadeGrassBlade(input, n, view, light);
            }
            else if (kind < 14.5)
            {
                // ---- Partículas: polen, hojas, pétalos y semillas (kind 14) ----
                // El DIBUJO lo trae la celda del atlas de detalle que eligió la partícula: color en
                // rgb y FORMA en el alfa (ver SceneDetailTexture.BuildParticleTiles). Antes la mota
                // era un cuadro plano con una máscara redonda y, encima, caía en la rama de las
                // casas (kind 15 arrancaba en 13.5), así que se dibujaba OPACA y del color de una
                // pared. El brillo lo pone el sol (ShadePollen): a contraluz la partícula se apaga
                // como cualquier otra cosa del mundo.
                float4 sprite = DetailTexture.SampleLevel(
                    DetailSampler, DetailUv(input.Uv, 1.0, DetailTile(input.MaterialTint.x)), 0.0);
                alpha = saturate(sprite.a);
                color = ShadePollen(input, view) * sprite.rgb * 1.15;
            }
            else if (kind < 15.5)
            {
                color = ShadeHouse(input, n, view, light);
            }
            else if (kind < 16.5)
            {
                color = ShadeShop(input, n, view, light);
            }
            else if (kind > 17.5)
            {
                // ---- LLUVIA (kind 18): estela fina y translúcida ----
                // La forma la pone la GEOMETRÍA (un quad alargado, ver el VS), así que acá no hace
                // falta un sprite: el alfa baja hacia las puntas y hacia los bordes, y el color es el
                // del agua que ILUMINAN las fuentes de la escena (los faroles y los carteles). Sin esa
                // luz la lluvia sería una rejilla gris: es la luz la que la hace visible de noche.
                float across = saturate(abs(input.Local.x) * 0.5);
                float along = saturate(input.Local.y);
                float edge = 1.0 - across;
                float ends = smoothstep(0.0, 0.22, along) * (1.0 - smoothstep(0.70, 1.0, along));
                alpha = saturate(edge * edge * ends) * 0.46;

                float3 rainAlbedo = float3(0.62, 0.72, 0.86);
                float3 rainLight = SkyAmbient() * 0.9
                                 + PointLightSum(rainAlbedo, 0.0, 0.16, n, view, input.WorldPosition) * 2.4;
                color = rainLight * rainAlbedo;
            }
            else
            {
                color = ShadeTextured(input, n, view, light);
            }

            // Bruma aérea para lo OPACO (los modelos con material también: el asfalto lejano se
            // funde con el aire igual que el terreno). Las mallas con mezcla (burbujas y partículas)
            // se funden por ALFA: mezclarlas además contra el cielo duplicaría el desvanecido (la
            // partícula lejana quedaba doblemente transparente y desaparecía a mitad de cuadro).
            //
            // Las mallas con mezcla son las de kind 4 (burbuja), 14 (partículas) y 18 (lluvia): se dice
            // por KIND y no por rango porque el rango viejo (kind < 13.5 || kind > 16.5) metía a las
            // partículas en el camino opaco y dejaba a la ciudad sin bruma.
            bool blended = (kind > 3.5 && kind < 4.5) || (kind > 13.5 && kind < 14.5) || (kind > 17.5 && kind < 18.5);
            if (!blended)
            {
                color = lerp(color, SkyColor(-view), AerialDensity(input.WorldPosition, distanceToCamera));
            }
            else
            {
                // El fundido a distancia de lo transparente se hace con el ALFA, no con la bruma:
                // es lo que permite que el pasto y las partículas lleguen vivas hasta la ciudad sin
                // que nada se borre antes de tiempo.
                alpha *= saturate(1.0 - (distanceToCamera - 260.0) / 240.0);
            }
            // El polen se entrega PREMULTIPLICADO y en espacio de PANTALLA: la mezcla ocurre DESPUÉS
            // del tonemap, así que el alfa tiene que multiplicar al color ya convertido. En lineal
            // el borde de la mota cambiaría de brillo al desvanecerse.
            float3 display = Tonemap(color);
            return float4(display * alpha, alpha);
        }
        """;

    /// <summary>
    /// Fondo y franja de métricas: los dos son triángulos generados por SV_VertexID (sin buffer de
    /// vértices), con UN entry point cada uno. El fondo es la pasada que se dibuja primero, sin
    /// profundidad; la franja va última, encima de la escena y con blend premultiplicado.
    /// </summary>
    /// <remarks>
    /// El fondo y la franja son DOS entry points y no dos ramas del mismo vertex shader a
    /// propósito. Distinguirlos por "desde qué vértice arranca el dibujo" se rompe en Direct3D:
    /// <c>SV_VertexID</c> NO incluye <c>StartVertexLocation</c> (lo dice el propio HLSL), mientras
    /// que <c>gl_VertexIndex</c> de Vulkan SÍ incluye <c>firstVertex</c> y <c>gl_VertexID</c> de
    /// OpenGL SÍ incluye <c>first</c>. Discriminar por instance id también queda afuera: dibujar
    /// como instancia 1 exige <c>baseInstance</c>, que en Vulkan solo existe en la 1.1 y en
    /// OpenGL 3.3 core no existe (llegó con ARB_base_instance, de 4.2). Dos entry points no
    /// dependen de nada de eso: los cuatro backends dibujan SIEMPRE desde el vértice 0.
    ///
    /// Los dos comparten el MISMO pixel shader (<see cref="SkyPixelShader"/>), que elige la rama
    /// por la Uv del caso: un cambio de formato del panel no se escribe dos veces.
    /// </remarks>
    internal const string SkyVertexShader =
        """
        struct SkyOutput
        {
            float4 Position : SV_POSITION;
            float2 Uv       : TEXCOORD0;
            float  IsOverlay : TEXCOORD1;   // 1 = franja de métricas, 0 = fondo
        };

        SkyOutput VSSky(uint vertexId : SV_VertexID)
        {
            float2 uv = float2((vertexId << 1) & 2, vertexId & 2);

            SkyOutput output;
            output.Position = float4(uv * 2.0 - 1.0, 1.0, 1.0);
            output.Uv = uv;
            output.IsOverlay = 0.0;
            return output;
        }
        """;

    /// <summary>
    /// Vertex shader de la franja (ver <see cref="SkyVertexShader"/> y el comentario de la clase):
    /// 6 vértices que cubren <c>OverlayRect</c>, con la Uv invertida en Y porque la fila 0 de los
    /// píxeles es la de ARRIBA. Las esquinas van en el orden (abajo-izq, abajo-der, arriba-izq /
    /// arriba-izq, abajo-der, arriba-der), que es el de los dos triángulos.
    /// </summary>
    internal const string SkyOverlayVertexShader =
        """
        struct SkyOutput
        {
            float4 Position : SV_POSITION;
            float2 Uv       : TEXCOORD0;
            float  IsOverlay : TEXCOORD1;   // 1 = franja de métricas, 0 = fondo
        };

        SkyOutput VSSkyOverlay(uint vertexId : SV_VertexID)
        {
            float2 corner = float2(
                (vertexId == 1 || vertexId == 4 || vertexId == 5) ? 1.0 : 0.0,
                (vertexId == 2 || vertexId == 3 || vertexId == 5) ? 1.0 : 0.0);

            SkyOutput output;
            output.Position = float4(lerp(OverlayRect.xy, OverlayRect.zw, corner), 0.0, 1.0);
            output.Uv = float2(corner.x, 1.0 - corner.y);
            output.IsOverlay = 1.0;
            return output;
        }
        """;

    internal const string SkyPixelShader =
        """
        float4 PSSky(SkyOutput input) : SV_Target
        {
            // ---- Franja de métricas ----
            // Se indexa el píxel exacto (el panel se dibuja 1:1): sin interpolación, el texto
            // sale nítido. El color ya viene premultiplicado, que es lo que espera el blend, y NO
            // pasa por el tonemapping: es UI terminada, no mundo.
            if (input.IsOverlay > 0.5)
            {
                float2 size = float2(OverlaySize.x, OverlaySize.y);
                int2 pixel = int2(floor(input.Uv * size));
                int index = pixel.y * (int)size.x + pixel.x;
                uint packed = OverlayPixels[index];
                return float4(
                    ((packed >> 0) & 0xFF) / 255.0,
                    ((packed >> 8) & 0xFF) / 255.0,
                    ((packed >> 16) & 0xFF) / 255.0,
                    ((packed >> 24) & 0xFF) / 255.0);
            }

            // ---- Fondo ----
            // Rayo por píxel: se des-proyecta la punta del far clip y se le resta el ojo. Es lo que
            // permite que el cielo sea DIRECCIONAL (sol, halo y nubes siguen a la cámara cuando
            // vira), cosa que un degradado vertical no puede.
            //
            // La Uv del triángulo de pantalla completa vale 0 en el borde de ABAJO y 1 en el de
            // ARRIBA EN LAS CUATRO APIs (los backends de Vulkan y OpenGL ya están "dados vuelta"
            // para que el NDC caiga igual que en Direct3D), así que la Y del NDC sale directa: si
            // se la invierte, el cielo se des-proyecta espejado y se ve el degradado del cenit
            // abajo —y las nubes, que solo existen con el rayo hacia arriba, desaparecen.
            float2 ndc = float2(input.Uv.x * 2.0 - 1.0, input.Uv.y * 2.0 - 1.0);
            float4 farPoint = mul(float4(ndc, 1.0, 1.0), InverseViewProjection);
            float3 direction = normalize(farPoint.xyz / farPoint.w - CameraPositionWS.xyz);

            float3 sky = SkyColor(direction);

            float4 clouds = CloudLayer(CameraPositionWS.xyz, direction);
            sky = CompositeClouds(sky, clouds);

            // El resplandor de lente lo pide el ESTILO (fuerza 0 = escena sin resplandor) y va SOLO
            // en el fondo: es lo que convierte un cielo de mediodía en el cielo de la postal.
            if (StyleAerial.w > 0.001) sky += AeroGlare(ndc) * StyleAerial.w;

            return float4(Tonemap(sky), 1.0);
        }
        """;
}
