using System.Numerics;

namespace WinForge.Component.Benchmark.Scenes.Styles;

/// <summary>
/// Estilo de la CALLE NOCTURNA CON LLUVIA (el piloto de la Fase 0).
///
/// ESTE ARCHIVO ES EL ÚNICO LUGAR donde se define el cielo, la bruma, las nubes y la exposición de
/// esta escena (ver <see cref="SceneStyle"/>): los shaders son uno solo y se configuran con esto.
///
/// Dirección de arte: noche de ciudad con lluvia fina. El cielo NO es negro —en una ciudad llueve
/// sobre un cielo que devuelve la luz de las calles—, así que el horizonte tiene un resplandor
/// cálido de sodio que sube a un azul casi negro; las nubes son una sábana baja y sucia que atrapa
/// ese resplandor; la bruma es densa y baja, que es lo que hace que los faroles tengan halo.
/// </summary>
internal static class NeonStyle
{
    /// <summary>Noche urbana con lluvia: horizonte de sodio, cenit azul profundo, bruma densa.</summary>
    internal static SceneStyle Default { get; } = new()
    {
        // El resplandor de la ciudad vive en el horizonte (naranja sucio, no amarillo puro: el sodio
        // tira a ámbar) y a los pocos grados se va a un azul nocturno saturado.
        SkyHorizon = new Vector3(0.165f, 0.115f, 0.090f),
        SkyMid = new Vector3(0.038f, 0.052f, 0.098f),
        SkyZenith = new Vector3(0.012f, 0.018f, 0.040f),
        SkyMidExponent = 0.34f,
        SkyZenithStart = 0.16f,
        SkyZenithEnd = 0.62f,

        // No hay luna visible: la luz que llega es la de la ciudad rebotada en las nubes. Un halo
        // amplio y muy tenue en el cenit evita que el cielo quede en un plano sin lectura.
        SunGlowColor = new Vector3(0.30f, 0.34f, 0.46f),
        SunGlowStrength = 0.025f,
        SunGlowPower = 3.0f,
        SunDiskColor = new Vector3(0.42f, 0.48f, 0.60f),
        SunDiskStrength = 0.04f,
        SunDiskPower = 60.0f,
        SunCoreColor = new Vector3(0.50f, 0.56f, 0.70f),
        SunCoreStrength = 0.6f,
        SunCoreCenter = 0.99940f,
        SunCoreHalfWidth = 0.00040f,

        // Luz directa FRÍA y débil: es la que "entra" de arriba (cielo de lluvia), y todo lo demás lo
        // ponen las luces puntuales de la escena (faroles y carteles).
        SunLightColor = new Vector3(0.30f, 0.36f, 0.48f),
        SunLightStrength = 0.75f,

        // Ambiente de ciudad: sin esto las sombras quedan en negro puro y la calle pierde el detalle
        // de los materiales (que es justo lo que la escena quiere mostrar). Es el número que más se
        // nota en una escena nocturna de interiores/exteriores: con el de una escena de día, la calle
        // sale a un plano negro donde no se ve ni el ladrillo ni el asfalto.
        SkyAmbient = new Vector3(0.115f, 0.135f, 0.175f),

        // Bruma densa y baja: es lo que hace que los faroles del fondo tengan halo y que la calle se
        // pierda en la lluvia en vez de cortarse contra el cielo.
        //
        // El número tiene que ser DENSO de verdad, que es lo que dice la línea de arriba: con 0,00042
        // —que era el valor que tenía, cien veces menos que esto— la bruma no llegaba a 20 m y la
        // punta de la calle se veía como un corte seco contra el cielo (las fachadas terminan de golpe
        // y no hay nada detrás). Con 0,0095 el fondo se disuelve en el resplandor de sodio del
        // horizonte a partir de los 60 m, que es como se ve una calle de noche con lluvia.
        AerialDensity = 0.0095f,
        AerialHeightBase = 18f,
        AerialHeightScale = 420f,

        // Noche: hace falta exposición para que la escena no quede en un plano oscuro, pero no tanta
        // como para que el cielo deje de ser noche.
        Exposure = 1.30f,

        // Sin resplandor de lente de sol: el de esta escena son los halos de los carteles, que los
        // hace el material emisivo y la bruma, no el destello de cámara.
        GlareStrength = 0f,

        // Nubes: sábana baja, con el contraste medio de una noche lluviosa (ni cúmulos definidos ni
        // un cielo limpio).
        CloudBandBase = 620f,
        CloudBandTop = 1600f,
        CloudFadeIn = 260f,
        CloudFadeOut = 420f,
        CloudShapeWeight = 0.72f,
        CloudDetailWeight = 0.28f,
        CloudThreshold = 0.52f,
        CloudContrast = 4.4f,
        // La nube NO se enciende con el ambiente del cielo (que es de noche) sino apenas con el
        // resplandor de la ciudad: con los pesos altos que tenía —pensados para un cielo de día— la
        // sábana salía a 200 de luminancia y el cielo nocturno quedaba más claro que la calle.
        CloudPhaseWeight = 0.10f,
        CloudPhaseBias = 0.06f,
        CloudAmbientWeight = 0.20f,
        CloudAbsorption = 0.0078f,
        CloudNoiseScale = 0.00048f,
        CloudDetailScale = 3.4f,
        CloudDetailYScale = 0.0013f,

        // El asfalto mojado refleja el cielo con nubes: es la mitad del look de una calle bajo lluvia.
        CloudReflectionStrength = 0.55f,

        // El terreno de esta escena es una calle plana: no hay campo de altura que marchar (el
        // shader no hace sombra de terreno acá), pero el selector usa el valle del cañón para que
        // el resto de la geometría (si la hubiera) caiga en un campo conocido.
        TerrainFieldSelector = 0f,

        // Materiales propios: el piso y las paredes traen sus texturas, así que acá no se tiñe nada.
        GroundTint = Vector3.One,
        GroundTintStrength = 0f
    };
}
