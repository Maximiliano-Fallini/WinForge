using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes;

/// <summary>
/// Estilo visual de UNA escena: cielo, sol, atmósfera, nubes, exposición y gradación.
///
/// Es DATO, no código: los shaders tienen UNA sola implementación de cielo, sol, bruma y nubes, y
/// estos números la configuran. Por eso cambiar el cielo de una escena NO puede afectar a otra: no
/// hay ninguna constante compartida entre escenas que mezclar — cada escena trae la suya (ver
/// <c>Scenes/Styles</c>, un archivo por escena).
///
/// Antes esto era un interruptor global en los shaders (<c>WorldIsAero()</c>) que elegía entre dos
/// juegos de constantes fijas con un <c>lerp</c>: tocar el cielo del corredor significaba tocar una
/// función que también dibujaba el de Frutiger Aero. Ahora el estilo viaja como uniforme por frame
/// (ver <c>Graphics.SceneStyleConstants</c>) y ninguna escena comparte ni un número con otra.
/// </summary>
public sealed class SceneStyle
{
    // =====================================================================
    // Cielo
    // =====================================================================

    /// <summary>Color del cielo en el horizonte (donde la cúpula toca el suelo).</summary>
    public Vector3 SkyHorizon { get; init; } = new(1.00f, 0.54f, 0.27f);

    /// <summary>Color del cielo a media altura: el que da el carácter del degradado.</summary>
    public Vector3 SkyMid { get; init; } = new(0.06f, 0.15f, 0.42f);

    /// <summary>Color del cenit (arriba de todo).</summary>
    public Vector3 SkyZenith { get; init; } = new(0.06f, 0.15f, 0.42f);

    /// <summary>Exponente del degradado horizonte→medio: menos de 1 adelanta el color de arriba.</summary>
    public float SkyMidExponent { get; init; } = 0.50f;

    /// <summary>
    /// Ventana de elevación (0 = horizonte, 1 = cenit) en la que el degradado pasa al color del
    /// cenit. Con el inicio en 1 o más, el cielo queda en dos colores (horizonte y medio).
    /// </summary>
    public float SkyZenithStart { get; init; } = 1f;
    public float SkyZenithEnd { get; init; } = 2f;

    // =====================================================================
    // Sol (disco, halo y luz que ilumina la escena)
    // =====================================================================

    /// <summary>Halo ancho alrededor del sol.</summary>
    public Vector3 SunGlowColor { get; init; } = new(1.0f, 0.62f, 0.32f);
    public float SunGlowStrength { get; init; } = 0.30f;
    public float SunGlowPower { get; init; } = 5.0f;

    /// <summary>Disco del sol (el punto brillante propiamente dicho).</summary>
    public Vector3 SunDiskColor { get; init; } = new(1.0f, 0.78f, 0.52f);
    public float SunDiskStrength { get; init; } = 3.0f;
    public float SunDiskPower { get; init; } = 220.0f;

    /// <summary>Núcleo quemado del disco, con el borde que lo define (centro y semiancho).</summary>
    public Vector3 SunCoreColor { get; init; } = new(1.5f, 1.30f, 1.05f);
    public float SunCoreStrength { get; init; } = 14.0f;
    public float SunCoreCenter { get; init; } = 0.99970f;
    public float SunCoreHalfWidth { get; init; } = 0.00015f;

    /// <summary>
    /// Color e intensidad de la luz DIRECTA (la que ilumina los materiales PBR). No es el color del
    /// disco: una luna azulada tiene un disco casi blanco y una luz mucho más fría.
    /// </summary>
    public Vector3 SunLightColor { get; init; } = new(1.35f, 0.86f, 0.52f);
    public float SunLightStrength { get; init; } = 2.4f;

    /// <summary>Luz ambiente del cielo (rebote): es lo que evita los negros planos en la sombra.</summary>
    public Vector3 SkyAmbient { get; init; } = new(0.16f, 0.22f, 0.34f);

    // =====================================================================
    // Atmósfera (bruma por distancia) y gradación
    // =====================================================================

    /// <summary>Cuánto cielo se mezcla por unidad de distancia recorrida.</summary>
    public float AerialDensity { get; init; } = 0.00011f;

    /// <summary>Altura desde la que la bruma empieza a caer, y a qué ritmo (más alto = más limpio).</summary>
    public float AerialHeightBase { get; init; } = 40f;
    public float AerialHeightScale { get; init; } = 1400f;

    /// <summary>Exposición previa al tonemapping ACES: es la perilla de "hora del día" del cuadro.</summary>
    public float Exposure { get; init; } = 1.15f;

    /// <summary>Fuerza del resplandor de lente (0 = escena sin resplandor).</summary>
    public float GlareStrength { get; init; }

    // =====================================================================
    // Nubes (capa volumétrica marchada)
    // =====================================================================

    /// <summary>Banda vertical de la capa: dónde empieza y dónde termina.</summary>
    public float CloudBandBase { get; init; } = 900f;
    public float CloudBandTop { get; init; } = 1750f;

    /// <summary>Anchos de los bordes de la banda (entrada por abajo, salida por arriba).</summary>
    public float CloudFadeIn { get; init; } = 170f;
    public float CloudFadeOut { get; init; } = 320f;

    /// <summary>Mezcla de forma gruesa y detalle fino del ruido de la nube, y su umbral/contraste.</summary>
    public float CloudShapeWeight { get; init; } = 0.78f;
    public float CloudDetailWeight { get; init; } = 0.22f;
    public float CloudThreshold { get; init; } = 0.475f;
    public float CloudContrast { get; init; } = 3.1f;

    /// <summary>Cómo se enciende la nube: peso de la fase hacia el sol, sesgo y peso del ambiente.</summary>
    public float CloudPhaseWeight { get; init; } = 0.45f;
    public float CloudPhaseBias { get; init; } = 0.10f;
    public float CloudAmbientWeight { get; init; } = 0.45f;

    /// <summary>Absorción por unidad de densidad marchada: define la opacidad de la nube.</summary>
    public float CloudAbsorption { get; init; } = 0.0075f;

    /// <summary>Escalas del ruido de la nube (horizontal, detalle y vertical del detalle).</summary>
    public float CloudNoiseScale { get; init; } = 0.00062f;
    public float CloudDetailScale { get; init; } = 3.1f;
    public float CloudDetailYScale { get; init; } = 0.0011f;

    /// <summary>
    /// Cuánto se ven las nubes en el REFLEJO del entorno (agua, vidrio). 0 = el reflejo se queda con
    /// el cielo limpio y no paga la marcha extra dentro del specular.
    /// </summary>
    public float CloudReflectionStrength { get; init; }

    // =====================================================================
    // Suelo (geometría del shader) y tinte de material
    // =====================================================================

    /// <summary>
    /// Qué campo de altura usa el SHADER para el terreno de esta escena: 0 = el valle/cauce del
    /// cañón, 1 = las lomas y el lago del mundo Aero. Es un dato de la escena (su terreno), no una
    /// mezcla de estilos: la rama la resuelve el compilador una vez por draw.
    /// </summary>
    public float TerrainFieldSelector { get; init; }

    /// <summary>
    /// Tinte multiplicativo del material de roca/terreno (el que usa el corredor). Es lo que permite
    /// que el MISMO material se lea como piedra de amanecer o como pedregal nocturno sin duplicar
    /// código de sombreado.
    /// </summary>
    public Vector3 GroundTint { get; init; } = Vector3.One;
    public float GroundTintStrength { get; init; }
}
