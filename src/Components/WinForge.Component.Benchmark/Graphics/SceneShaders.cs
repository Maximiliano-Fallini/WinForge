namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Código HLSL de la escena, COMPARTIDO por todos los backends (Direct3D 11 y Direct3D 12).
/// Se compila en TIEMPO DE EJECUCIÓN contra el <c>d3dcompiler_47</c> del sistema: así el
/// componente no arrastra binarios de shaders en el zip ni depende de tener el SDK de Windows
/// instalado en la máquina del usuario.
///
/// Es HLSL de shader model 5.0 con un cbuffer en b0 y datos por instancia en el input layout:
/// las dos APIs lo consumen igual, así que una escena nueva se escribe una sola vez.
///
/// Los colores de acá son CONTENIDO de la escena (el mundo que se mira), no UI: por eso no
/// salen de los pinceles del tema de la app.
/// </summary>
internal static class SceneShaders
{
    /// <summary>Perfil del vertex shader de la escena (feature level 11_0 = shader model 5.0).</summary>
    internal const string VertexProfile = "vs_5_0";

    /// <summary>Perfil del pixel shader de la escena.</summary>
    internal const string PixelProfile = "ps_5_0";

    /// <summary>Largo del corredor: debe coincidir con <c>SceneDefinition.CorridorLength</c>.</summary>
    internal const float CorridorLength = 420f;

    /// <summary>Velocidad de vuelo: debe coincidir con <c>SceneDefinition.FlightSpeed</c>.</summary>
    internal const float FlightSpeed = 22f;

    /// <summary>
    /// Constantes por frame. El nombre <c>row_major</c> es lo que permite subir las matrices
    /// de System.Numerics tal cual (que son row-major) y multiplicarlas como
    /// <c>mul(vectorFila, matriz)</c>. Las constantes del corredor se anteponen en
    /// <see cref="Full"/> para que shader y C# no puedan divergir: salen de los mismos campos.
    /// </summary>
    internal static string Common => CorridorConstants + CommonBody;

    /// <summary>Código HLSL completo (constantes del corredor + cbuffer + funciones).</summary>
    internal static string Full => Common;

    /// <summary>
    /// Constantes del corredor declaradas en HLSL. Con cultura INVARIANTE a propósito: en una
    /// máquina con coma decimal (es-AR) un "420,5" en el shader no compila.
    /// </summary>
    private static string CorridorConstants =>
        $"static const float CorridorLength = {CorridorLength.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};\n" +
        $"static const float FlightSpeed = {FlightSpeed.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};\n";

    private const string CommonBody =
        """
        cbuffer FrameConstants : register(b0)
        {
            row_major float4x4 ViewProjection;
            float4 CameraPositionWS;   // xyz = posición de la cámara
            float4 LightDirectionWS;   // xyz = dirección normalizada de la luz
            float4 TimeAndParams;      // x = segundos desde el inicio de la escena
        };

        // Cada campo lleva su semántica: sin ella HLSL no sabe de dónde sacarlo y el
        // compilador tira X3502. Los nombres coinciden con el input layout del backend
        // (INSTANCE0 e INSTANCE1, por instancia).
        struct InstanceData
        {
            float4 PositionScale : INSTANCE0;   // xyz = banda X, altura, posición en el recorrido; w = escala
            float4 Orientation   : INSTANCE1;   // x = giro inicial, y = velocidad de giro
        };

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
        // El piso del terreno lo agrega acá (y no horneado en la instancia) para que terreno,
        // cámara y envoltura salgan siempre de la misma función. La niebla por distancia tapa el
        // reciclado: las rocas entran por el fondo de la ventana, a más de 200 unidades, donde la
        // niebla ya es casi total.
        float3 WrapInstance(float3 local, float4 placement)
        {
            float camZ = CameraPositionWS.z;
            float windowStart = camZ - CorridorLength * 0.5;
            float z = windowStart + fmod(placement.z - windowStart + CorridorLength * 0.5, CorridorLength);

            float2 xz = float2(placement.x, z);
            return local * placement.w + float3(placement.x, placement.y + TerrainHeight(xz), z);
        }

        float3 RotateY(float3 value, float angle)
        {
            float s, c;
            sincos(angle, s, c);
            return float3(c * value.x + s * value.z, value.y, -s * value.x + c * value.z);
        }

        float Hash13(float3 p)
        {
            p = frac(p * 0.1031);
            p += dot(p, p.yzx + 33.33);
            return frac((p.x + p.y) * p.z);
        }
        """;

    /// <summary>Vertex shader de la geometría: corredor + instancias rotadas + color por instancia.</summary>
    internal const string GeometryVertexShader =
        """
        struct VSInput
        {
            float3 Position : POSITION;
            float3 Normal   : NORMAL;
        };

        struct VSOutput
        {
            float4 Position      : SV_POSITION;
            float3 Normal        : NORMAL;
            float3 WorldPosition : TEXCOORD0;
            float3 Albedo        : COLOR0;
            float  Fog           : TEXCOORD1;
        };

        VSOutput VSMain(VSInput input, InstanceData instance)
        {
            // Giro propio de cada instancia: parte del estado inicial y avanza con el tiempo
            // (no con el número de frame), así la corrida es repetible.
            float angle = instance.Orientation.x + TimeAndParams.x * instance.Orientation.y;
            float3 world = WrapInstance(RotateY(input.Position, angle), instance.PositionScale);
            float3 normal = RotateY(input.Normal, angle);

            float phase = frac(sin(dot(instance.PositionScale.xyz, float3(12.9898, 78.233, 37.719))) * 43758.5453);
            float3 cool = float3(0.16, 0.55, 0.95);
            float3 warm = float3(0.95, 0.42, 0.66);

            // Niebla por distancia a la cámara: funde el mundo con el cielo y tapa el pop de
            // las instancias que entran por el fondo de la ventana.
            float distance = length(world - CameraPositionWS.xyz);
            float fog = saturate((distance - 90.0) / 160.0);

            VSOutput output;
            output.Position = mul(float4(world, 1.0), ViewProjection);
            output.Normal = normal;
            output.WorldPosition = world;
            output.Albedo = lerp(cool, warm, phase) * (0.55 + 0.45 * saturate(instance.PositionScale.w));
            output.Fog = fog;
            return output;
        }
        """;

    /// <summary>
    /// Pixel shader de la geometría: lambert + especular + rim + niebla, y una carga de ruido
    /// con cadena de dependencia. El bucle tiene un número FIJO de iteraciones a propósito: el
    /// costo por píxel es constante, no depende de la escena ni del hardware, y el resultado
    /// entra en el color final (el compilador no lo puede eliminar).
    ///
    /// 96 iteraciones no son un detalle: con 24 la escena quedaba tan liviana que el costo de
    /// enviar el frame (CPU) pesaba más que el trabajo de la placa, y eso mide otra cosa.
    /// </summary>
    internal const string GeometryPixelShader =
        """
        float4 PSMain(VSOutput input) : SV_Target
        {
            float3 n = normalize(input.Normal);
            float3 l = normalize(-LightDirectionWS.xyz);
            float3 v = normalize(CameraPositionWS.xyz - input.WorldPosition);
            float3 h = normalize(l + v);

            float diffuse = saturate(dot(n, l));
            float specular = pow(saturate(dot(n, h)), 42.0);
            float rim = pow(1.0 - saturate(dot(n, v)), 3.0);

            float3 q = input.WorldPosition * 1.7 + TimeAndParams.x * 0.35;
            float noise = 0.0;
            [unroll]
            for (int i = 0; i < 96; i++)
            {
                noise += Hash13(q);
                q = q * 1.61 + float3(noise, noise, noise) * 0.37;
            }
            noise /= 96.0;

            float3 color = input.Albedo * (0.09 + 0.91 * diffuse);
            color += specular * 0.55;
            color += rim * 0.35 * float3(0.45, 0.72, 1.0);
            color *= 0.78 + 0.44 * noise;

            // La niebla usa el color del cielo en la altura del píxel: el horizonte funde la
            // geometría con el fondo en vez de cortarla en seco.
            float3 fogColor = lerp(float3(0.100, 0.120, 0.215), float3(0.015, 0.025, 0.060),
                                   saturate(input.WorldPosition.y / 60.0));
            color = lerp(color, fogColor, input.Fog);
            return float4(color, 1.0);
        }
        """;

    /// <summary>
    /// Fondo: triángulo de pantalla completa generado por SV_VertexID (sin buffer de
    /// vértices) con degradado y viñeta. Se dibuja primero, sin profundidad.
    /// </summary>
    internal const string SkyVertexShader =
        """
        struct SkyOutput
        {
            float4 Position : SV_POSITION;
            float2 Uv       : TEXCOORD0;
        };

        SkyOutput VSSky(uint vertexId : SV_VertexID)
        {
            float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
            SkyOutput output;
            output.Position = float4(uv * 2.0 - 1.0, 1.0, 1.0);
            output.Uv = uv;
            return output;
        }
        """;

    internal const string SkyPixelShader =
        """
        float4 PSSky(SkyOutput input) : SV_Target
        {
            float3 top = float3(0.015, 0.025, 0.060);
            float3 bottom = float3(0.100, 0.120, 0.215);
            float3 color = lerp(bottom, top, saturate(input.Uv.y));
            float2 centered = input.Uv * 2.0 - 1.0;
            float vignette = saturate(1.0 - dot(centered, centered) * 0.35);
            return float4(color * vignette, 1.0);
        }
        """;
}
