using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes.Styles;

/// <summary>
/// Estilo de FRUTIGER AERO: el día de postal (azul saturado, sol de mediodía, cúmulos blancos y
/// resplandor de lente).
///
/// ESTE ARCHIVO ES EL ÚNICO LUGAR donde se define el cielo, el sol, la bruma y las nubes del mundo
/// Aero. Los números son EXACTAMENTE los que el shader tenía fijos para este mundo cuando existía el
/// interruptor global: lo que cambió es dónde viven, no cómo se ve.
/// </summary>
internal static class AeroStyle
{
    /// <summary>Día claro de postal: cielo azul profundo hasta el horizonte y cúmulos definidos.</summary>
    internal static SceneStyle Default { get; } = new()
    {
        // Azul PROFUNDO, no celeste: ACES comprime los azules claros hacia el blanco, así que el
        // celeste "de cielo real" salía gris lavado. El azul llega hasta muy cerca del horizonte.
        SkyHorizon = new Vector3(0.075f, 0.330f, 0.880f),
        SkyMid = new Vector3(0.030f, 0.190f, 0.800f),
        SkyZenith = new Vector3(0.005f, 0.065f, 0.540f),
        SkyMidExponent = 0.30f,
        SkyZenithStart = 0.22f,
        SkyZenithEnd = 0.95f,

        // Sol de mediodía: halo contenido, disco a plena potencia y núcleo blanco.
        SunGlowColor = new Vector3(1.00f, 0.99f, 0.94f),
        SunGlowStrength = 0.05f,
        SunGlowPower = 4.0f,
        SunDiskColor = new Vector3(1.00f, 0.99f, 0.96f),
        SunDiskStrength = 1.10f,
        SunDiskPower = 90.0f,
        SunCoreColor = new Vector3(1.70f, 1.68f, 1.60f),
        SunCoreStrength = 20.0f,
        SunCoreCenter = 0.99880f,
        SunCoreHalfWidth = 0.00060f,

        // Luz de mediodía: blanca y fuerte — es lo que hace que el verde y el vidrio se vean
        // saturados y limpios.
        SunLightColor = new Vector3(1.45f, 1.42f, 1.30f),
        SunLightStrength = 2.4f,

        // Ambiente de cielo claro.
        SkyAmbient = new Vector3(0.36f, 0.50f, 0.74f),

        // Bruma mucho más tenue y más alta que en el corredor: si no, el verde y el vidrio se lavan.
        AerialDensity = 0.000030f,
        AerialHeightBase = 260f,
        AerialHeightScale = 2600f,

        Exposure = 1.15f,

        // El resplandor de lente es la firma del cielo de la postal.
        GlareStrength = 1f,

        // Cúmulos: banda más baja y profunda que la del corredor, contraste alto (densidad 0 o 1) y
        // absorción baja, que es lo que da bolas de algodón blancas con azul limpio entre medio.
        CloudBandBase = 780f,
        CloudBandTop = 1900f,
        CloudFadeIn = 150f,
        CloudFadeOut = 280f,
        CloudShapeWeight = 0.70f,
        CloudDetailWeight = 0.30f,
        CloudThreshold = 0.625f,
        CloudContrast = 14.0f,
        CloudPhaseWeight = 0.72f,
        CloudPhaseBias = 0.55f,
        CloudAmbientWeight = 0.55f,
        CloudAbsorption = 0.0036f,
        CloudNoiseScale = 0.00042f,
        CloudDetailScale = 2.6f,
        CloudDetailYScale = 0.0009f,

        // El agua y el vidrio reflejan los cúmulos: es lo que los hace parecer espejos del cielo.
        CloudReflectionStrength = 0.9f,

        // Lomas y lago del mundo Aero (ver SceneDefinition.AeroHeight).
        TerrainFieldSelector = 1f,

        // El material de roca no se usa en este mundo; sin tinte.
        GroundTint = Vector3.One,
        GroundTintStrength = 0f
    };
}
