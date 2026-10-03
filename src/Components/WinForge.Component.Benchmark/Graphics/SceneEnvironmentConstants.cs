using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinForge.Component.Benchmark.Scenes;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Los 9 coeficientes del entorno, como arreglo de <c>float4</c> (rgb = coeficiente, w libre). Igual
/// que las esferas de sombra y las luces puntuales, es un <c>InlineArray</c> para que el bloque siga
/// siendo UNA estructura copiable con <c>MemoryMarshal</c> y para que el tamaño salga del mismo número
/// que usa el layout del cbuffer.
/// </summary>
[InlineArray(SceneEnvironment.CoefficientCount)]
internal struct EnvironmentShArray
{
    private Vector4 _element;
}

/// <summary>
/// La luz que rodea la escena tal como viaja al shader: la forma de GPU de
/// <see cref="SceneEnvironment"/> (IBL) más la matriz de la sombra proyectada, hermana de
/// <see cref="SceneStyleConstants"/> y de <see cref="SceneLightConstants"/>.
///
/// Son 9 <c>float4</c> de entorno + la matriz de luz + dos vectores de parámetros (240 bytes) y ni una
/// textura en el bloque: el HDRI entra al render como CONSTANTES, así que las cuatro APIs reciben el
/// mismo entorno por el mismo camino que ya compartían. La textura del shadow map sí es un recurso
/// aparte, y la ata cada backend (ver <c>README-SOMBRAS.md</c>).
///
/// Va al FINAL del bloque por frame, después del estilo y de las luces, para no mover el offset de
/// nada de lo anterior.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SceneEnvironmentConstants
{
    /// <summary>Bytes del bloque: es lo que suman los backends al tamaño del cbuffer.</summary>
    internal const int SizeInBytes = 64 + 16 * SceneEnvironment.CoefficientCount + 16 + 16;

    /// <summary>
    /// Mundo → espacio de luz para la pasada de sombras: una proyección ORTOGRÁFICA fija (1 cascada,
    /// resolución fija) armada una vez con el volumen del diorama y la dirección del sol de la escena.
    /// El sol no se mueve (es dato del estilo) y el volumen es de la escena, así que la matriz no
    /// cambia por frame — el costo por frame queda igual, que es lo que el benchmark necesita.
    /// </summary>
    public Matrix4x4 LightViewProjection;

    /// <summary>rgb = coeficiente de armónicos esféricos del entorno, w = sin uso.</summary>
    public EnvironmentShArray Sh;

    /// <summary>
    /// x = el entorno está activo (1) o la escena se queda con su cielo procedural (0).
    /// y/z/w = reservados.
    /// </summary>
    public Vector4 Params;

    /// <summary>
    /// Sombra proyectada: x = fuerza (0 = apagada, y es el valor por defecto), y = sesgo en
    /// profundidad en espacio de luz, z = tamaño del téxel (para el PCF), w = penumbra.
    ///
    /// Con x = 0 el shader sale de la función en la primera línea y NO muestrea el shadow map, así que
    /// el camino de hoy queda intacto. El valor sale de la ESCENA, y la configuración gráfica de la
    /// corrida (<see cref="SceneGraphicsOptions.Shadows"/>) puede APAGARLA. En los dos casos el número
    /// es el mismo en las cuatro APIs —las sombras están encendidas en las cuatro o apagadas en las
    /// cuatro—: dos APIs con sombras y dos sin ellas darían imágenes distintas del mismo mundo, que es
    /// justo lo que el benchmark no puede permitirse.
    /// </summary>
    public Vector4 Shadows;

    /// <summary>Sin entorno y sin sombra: el shader no toca ni los armónicos ni el shadow map.</summary>
    internal static SceneEnvironmentConstants Disabled
    {
        get
        {
            var constants = new SceneEnvironmentConstants();
            constants.Params = Vector4.Zero;
            constants.Shadows = Vector4.Zero;
            return constants;
        }
    }

    /// <summary>
    /// Arma el bloque con lo que declara la escena: el entorno (si tiene) y su volumen de sombra (si
    /// tiene). Una escena sin ninguna de las dos cosas deja el bloque apagado y se renderiza
    /// exactamente como antes. La configuración gráfica de la corrida puede apagar la sombra desde
    /// afuera (ver <see cref="SceneGraphicsOptions.Shadows"/>), y ese apagado entra por ACÁ: es el
    /// único camino por el que el valor llega al shader, así que no hay forma de que una API lo vea
    /// encendido y otra apagado.
    ///
    /// El pedido de los coeficientes de entorno es lo que dispara, la PRIMERA vez, la lectura y
    /// proyección del .hdr (ver <see cref="SceneEnvironment"/>, carga perezosa); después es una
    /// lectura de memoria. La matriz de luz también se arma una sola vez: los backends la piden al
    /// preparar la escena y la guardan.
    /// </summary>
    internal static SceneEnvironmentConstants From(SceneDefinition? scene, SceneGraphicsOptions graphics)
    {
        var constants = new SceneEnvironmentConstants();

        // ---- Entorno (IBL) ----
        // Pedir los coeficientes es lo que dispara la lectura y proyección del .hdr (carga perezosa):
        // con el entorno apagado no se pide y el archivo no se lee.
        var coefficients = graphics.Environment ? scene?.Environment?.Coefficients : null;
        if (coefficients != null)
        {
            float intensity = scene!.Environment!.Intensity;
            int count = Math.Min(coefficients.Length, SceneEnvironment.CoefficientCount);
            for (int i = 0; i < count; i++)
            {
                constants.Sh[i] = new Vector4(coefficients[i] * intensity, 0f);
            }
            constants.Params = new Vector4(1f, 0f, 0f, 0f);
        }

        // ---- Sombra proyectada ----
        // El volumen sale de la GEOMETRÍA de la escena (o del ajuste a mano, si la escena lo declara),
        // así que una escena NO tiene que configurar nada para tener sombras dinámicas: ver
        // SceneDefinition.ResolveShadowVolume.
        if (graphics.Shadows && scene is { ShadowStrength: > 0f } && scene.ResolveShadowVolume() is { } volume)
        {
            constants.LightViewProjection = BuildLightViewProjection(scene.LightDirection, volume);
            // El téxel sale del tamaño EFECTIVO del mapa (la configuración gráfica puede cambiarlo) y
            // no del que trae el volumen: si no, el PCF muestrearía con un paso que no es el del
            // recurso que crean los backends.
            constants.Shadows = new Vector4(
                scene.ShadowStrength,
                volume.DepthBias,
                graphics.TexelSize,
                volume.Penumbra);
        }

        return constants;
    }

    /// <summary>
    /// Cómo traduce cada API una posición de CLIP al TÉXEL de la textura en la que escribe. Es la única
    /// diferencia real entre las cuatro y la razón de que la PASADA de profundidad reciba una matriz
    /// distinta en cada grupo (el shader, en cambio, ve la MISMA en todas: <see cref="From"/>).
    ///
    /// La comparación de la sombra pide que el téxel que ESCRIBE la pasada y el que LEE el sombreado
    /// sean el mismo para el mismo punto del mundo. El shader calcula su coordenada con la fórmula
    /// clásica <c>uv = ndc.xy * 0.5 + 0.5</c>, así que la pasada tiene que escribir con ESA convención;
    /// lo que cambia entre APIs es a qué téxel manda la placa cada posición de clip:
    /// <list type="bullet">
    /// <item><b>Direct3D y Vulkan</b> (<c>CullNone</c>+viewport invertido en Vulkan): el NDC tiene la Y
    /// hacia ABAJO, así que <c>ndc.y = +1</c> cae en la fila 0 — que es <c>v = 0</c>, el opuesto de la
    /// fórmula. La pasada tiene que negar la Y para que el mapa quede como el shader lo espera. (Es el
    /// mismo motivo por el que todo muestreo de shadow map en Direct3D usa <c>float2(0.5, -0.5)</c>.)</item>
    /// <item><b>OpenGL</b>: el NDC tiene la Y hacia ARRIBA y el téxel de <c>ndc.y = +1</c> es
    /// <c>v = 1</c>, que es justo lo que dice la fórmula. Acá lo que cambia es la PROFUNDIDAD: el búfer
    /// de OpenGL mapea [-1,1] a [0,1] (Direct3D y Vulkan ya usan [0,1]) y la matriz de System.Numerics
    /// produce la convención de Direct3D, así que sin convertir, el valor escrito caería en [0,5 .. 1] y
    /// la comparación del sombreado no encontraría ninguna sombra.</item>
    /// </list>
    /// </summary>
    internal enum ShadowMapAxis
    {
        /// <summary>Direct3D 11, Direct3D 12 y Vulkan: la pasada niega la Y de clip.</summary>
        FlippedY,

        /// <summary>OpenGL: la pasada convierte la profundidad de clip a [-1,1].</summary>
        OpenGl
    }

    /// <summary>
    /// La matriz de luz con la que cada backend RENDERIZA el shadow map: es la que reemplaza a
    /// <c>ViewProjection</c> en la pasada de profundidad, con el ajuste que le corresponde a su API
    /// (ver <see cref="ShadowMapAxis"/>). El bloque que ve el SHADER no lleva ningún ajuste, así que las
    /// cuatro APIs comparan exactamente el mismo número.
    /// </summary>
    internal static Matrix4x4 LightViewProjectionFor(SceneDefinition? scene, ShadowMapAxis axis)
    {
        if (scene is not { ShadowStrength: > 0f } || scene.ResolveShadowVolume() is not { } volume)
        {
            return Matrix4x4.Identity;
        }

        var matrix = BuildLightViewProjection(scene.LightDirection, volume);
        return axis == ShadowMapAxis.OpenGl
            ? Matrix4x4.Multiply(matrix, GlDepthConvention)
            : Matrix4x4.Multiply(matrix, Direct3DYFlip);
    }

    /// <summary>
    /// z' = 2·z − w: lleva una profundidad de clip de la convención de Direct3D/Vulkan ([0,1]) a la
    /// de OpenGL ([-1,1]). Con el convenio de filas de System.Numerics (<c>v·M</c>), eso es "dos
    /// veces la columna z menos la columna w", que es lo que dice esta matriz.
    /// </summary>
    private static readonly Matrix4x4 GlDepthConvention = new(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 2f, 0f,
        0f, 0f, -1f, 1f);

    /// <summary>y' = −y: pone la Y de clip en la convención de la TEXTURA de Direct3D y Vulkan (ver
    /// <see cref="ShadowMapAxis"/>), donde la fila 0 —o sea <c>v = 0</c>— es la de arriba.</summary>
    private static readonly Matrix4x4 Direct3DYFlip = new(
        1f, 0f, 0f, 0f,
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 0f, 0f, 1f);

    /// <summary>
    /// Matriz de luz: una cámara puesta "detrás" del volumen contra la dirección de la luz, con una
    /// proyección ortográfica que abarca el volumen entero.
    ///
    /// Se usa el RADIO de la esfera envolvente (el largo de la extensión) para las dos dimensiones
    /// laterales: un volumen no tiene por qué estar alineado con la luz, y encajarlo con la esfera
    /// envolvente cuesta unos téxeles de resolución pero no deja NADA afuera. El rango de profundidad
    /// va de 0,1 a cuatro radios: el volumen entra completo por construcción.
    /// </summary>
    private static Matrix4x4 BuildLightViewProjection(Vector3 lightDirection, SceneShadowVolume volume)
    {
        var direction = lightDirection.LengthSquared() > 0f
            ? Vector3.Normalize(lightDirection)
            : new Vector3(0f, -1f, 0f);
        float radius = MathF.Max(1f, volume.Extent.Length());

        var eye = volume.Center - direction * (radius * 2f);
        var up = MathF.Abs(direction.Y) > 0.99f ? new Vector3(0f, 0f, 1f) : new Vector3(0f, 1f, 0f);

        return Matrix4x4.CreateLookAt(eye, volume.Center, up)
             * Matrix4x4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, 0.1f, radius * 4f);
    }
}
