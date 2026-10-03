using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes.Styles;

/// <summary>
/// Estilo del CORREDOR INFINITO: noche azul profundo.
///
/// ESTE ARCHIVO ES EL ÚNICO LUGAR donde se define el cielo, el sol, la bruma y las nubes del
/// corredor. Cambiar el corredor a otro cielo es editar estos números: Frutiger Aero tiene su
/// propio archivo (<see cref="AeroStyle"/>) con los suyos, y ningún shader mezcla los dos.
///
/// Dirección de arte: es un anochecer azul — la piedra del pedregal queda en penumbra fría, el
/// "sol" es una luna baja y fría que apenas enciende un halo, la bruma es tenue y azulada, y las
/// nubes son escasas y apagadas para que el azul del cielo se lea limpio detrás del vuelo.
/// </summary>
internal static class CorridorStyle
{
    /// <summary>Cielo nocturno: azul profundo saturado en el horizonte que cae a un azul casi negro.</summary>
    internal static SceneStyle Default { get; } = new()
    {
        // Azul OSCURO, no celeste: todo el cuadro pasa por ACES, que lava los azules claros.
        // El horizonte queda azul de verdad y el cenit casi negro, que es la lectura de "anochecer
        // azul" que se pidió para el corredor.
        SkyHorizon = new Vector3(0.012f, 0.038f, 0.150f),
        SkyMid = new Vector3(0.006f, 0.020f, 0.090f),
        SkyZenith = new Vector3(0.002f, 0.008f, 0.040f),
        SkyMidExponent = 0.42f,
        SkyZenithStart = 0.25f,
        SkyZenithEnd = 0.95f,

        // Luna: halo frío y contenido, disco chico y muy brillante, núcleo apenas marcado.
        SunGlowColor = new Vector3(0.36f, 0.48f, 0.74f),
        SunGlowStrength = 0.06f,
        SunGlowPower = 4.0f,
        SunDiskColor = new Vector3(0.86f, 0.92f, 1.00f),
        SunDiskStrength = 0.60f,
        SunDiskPower = 120.0f,
        SunCoreColor = new Vector3(1.10f, 1.20f, 1.45f),
        SunCoreStrength = 6.0f,
        SunCoreCenter = 0.99920f,
        SunCoreHalfWidth = 0.00045f,

        // Luz directa FRÍA: el pedregal se lee azul acero, nunca ámbar.
        SunLightColor = new Vector3(0.44f, 0.58f, 0.86f),
        SunLightStrength = 2.4f,

        // Ambiente del cielo nocturno: mantiene el detalle en la sombra sin levantar los negros.
        SkyAmbient = new Vector3(0.085f, 0.130f, 0.235f),

        // Bruma tenue y baja: de noche el aire se ve limpio y el azul no se lava.
        AerialDensity = 0.000075f,
        AerialHeightBase = 30f,
        AerialHeightScale = 1100f,

        // Un poco más de exposición que la escena diurna: la noche no puede quedar en negro plano.
        Exposure = 1.45f,

        // Sin resplandor de lente: el del Aero es un artefacto de sol de mediodía.
        GlareStrength = 0f,

        // Nubes escasas, bajas y apagadas (banda ancha, contraste medio): se leen como jirones.
        CloudBandBase = 950f,
        CloudBandTop = 2000f,
        CloudFadeIn = 210f,
        CloudFadeOut = 360f,
        CloudShapeWeight = 0.74f,
        CloudDetailWeight = 0.26f,
        CloudThreshold = 0.545f,
        CloudContrast = 6.0f,
        CloudPhaseWeight = 0.55f,
        CloudPhaseBias = 0.16f,
        CloudAmbientWeight = 0.50f,
        CloudAbsorption = 0.0060f,
        CloudNoiseScale = 0.00055f,
        CloudDetailScale = 2.9f,
        CloudDetailYScale = 0.0010f,

        // El reflejo del pedregal se queda con el cielo limpio: no paga la marcha de nubes.
        CloudReflectionStrength = 0f,

        // Terreno del valle/cauce (ver SceneDefinition.TerrainHeight y CanyonHeight).
        TerrainFieldSelector = 0f,

        // Piedra nocturna: el material de roca es el MISMO del motor, teñido a acero azul.
        GroundTint = new Vector3(0.42f, 0.60f, 1.15f),
        GroundTintStrength = 1f
    };
}
