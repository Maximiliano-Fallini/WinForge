using System.Numerics;
using System.Runtime.InteropServices;
using WinForge.Component.Benchmark.Scenes;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Bloque de estilo de la escena tal como viaja al shader: la forma de GPU de
/// <see cref="SceneStyle"/>, y la ÚNICA pieza que comparten HLSL, GLSL y C#.
///
/// Son 16 <c>float4</c> (256 bytes) y ni un escalar suelto: con todos los campos de 16 bytes la
/// alineación es idéntica en el cbuffer de HLSL y en un bloque <c>std140</c> de GLSL, así que el
/// mismo bloque subido desde C# lo leen las cuatro APIs sin reglas de padding distintas. Ese es el
/// motivo de empaquetar grupos de escalares en el <c>.w</c>/<c>.xyz</c> de un vector (por ejemplo
/// <c>SkyHorizon.w</c> = exponente del degradado) en vez de declararlos uno por uno.
///
/// Va al FINAL del bloque por frame (ver <c>FrameConstants</c> en los backends) justamente para no
/// mover el offset de nada de lo que ya estaba: el estilo se puede cambiar sin tocar el layout
/// existente.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SceneStyleConstants
{
    /// <summary>Bytes del bloque: es lo que suman los backends al tamaño del cbuffer.</summary>
    internal const int SizeInBytes = 16 * 16;

    /// <summary>Cielo: rgb = color del horizonte, w = exponente del degradado hacia el medio.</summary>
    public Vector4 SkyHorizon;

    /// <summary>Cielo: rgb = color medio, w = inicio de la ventana del cenit.</summary>
    public Vector4 SkyMid;

    /// <summary>Cielo: rgb = color del cenit, w = fin de la ventana del cenit.</summary>
    public Vector4 SkyZenith;

    /// <summary>Sol: rgb = color del halo, w = fuerza del halo.</summary>
    public Vector4 SunGlow;

    /// <summary>Sol: rgb = color del disco, w = fuerza del disco.</summary>
    public Vector4 SunDisk;

    /// <summary>Sol: rgb = color del núcleo, w = fuerza del núcleo.</summary>
    public Vector4 SunCore;

    /// <summary>Sol: x = potencia del halo, y = potencia del disco, z = centro del núcleo, w = semiancho.</summary>
    public Vector4 SunShape;

    /// <summary>Luz directa del PBR: rgb = color, w = intensidad.</summary>
    public Vector4 SunLight;

    /// <summary>Ambiente del cielo (rgb) y exposición previa al tonemapping (w).</summary>
    public Vector4 Ambient;

    /// <summary>Bruma: x = densidad, y = altura base, z = escala de altura, w = fuerza del resplandor.</summary>
    public Vector4 Aerial;

    /// <summary>Nubes: x = base de la banda, y = tope, z = ancho de entrada, w = ancho de salida.</summary>
    public Vector4 CloudBand;

    /// <summary>Nubes: x = peso de la forma, y = peso del detalle, z = umbral, w = contraste.</summary>
    public Vector4 CloudShape;

    /// <summary>Nubes: x = peso de la fase, y = sesgo de fase, z = peso del ambiente, w = absorción.</summary>
    public Vector4 CloudLight;

    /// <summary>Nubes: x = escala del ruido, y = escala del detalle, z = escala vertical del detalle.</summary>
    public Vector4 CloudNoise;

    /// <summary>x = nubes en el reflejo del entorno, y = campo de altura del terreno (0 cañón, 1 Aero).</summary>
    public Vector4 Misc;

    /// <summary>Material de roca/terreno: rgb = tinte, w = fuerza del tinte.</summary>
    public Vector4 GroundTint;

    /// <summary>Arma el bloque de GPU con el estilo de una escena.</summary>
    internal static SceneStyleConstants From(SceneStyle style) => new()
    {
        SkyHorizon = new Vector4(style.SkyHorizon, style.SkyMidExponent),
        SkyMid = new Vector4(style.SkyMid, style.SkyZenithStart),
        SkyZenith = new Vector4(style.SkyZenith, style.SkyZenithEnd),

        SunGlow = new Vector4(style.SunGlowColor, style.SunGlowStrength),
        SunDisk = new Vector4(style.SunDiskColor, style.SunDiskStrength),
        SunCore = new Vector4(style.SunCoreColor, style.SunCoreStrength),
        SunShape = new Vector4(style.SunGlowPower, style.SunDiskPower, style.SunCoreCenter, style.SunCoreHalfWidth),
        SunLight = new Vector4(style.SunLightColor, style.SunLightStrength),

        Ambient = new Vector4(style.SkyAmbient, style.Exposure),

        Aerial = new Vector4(style.AerialDensity, style.AerialHeightBase, style.AerialHeightScale, style.GlareStrength),

        CloudBand = new Vector4(style.CloudBandBase, style.CloudBandTop, style.CloudFadeIn, style.CloudFadeOut),
        CloudShape = new Vector4(style.CloudShapeWeight, style.CloudDetailWeight, style.CloudThreshold, style.CloudContrast),
        CloudLight = new Vector4(style.CloudPhaseWeight, style.CloudPhaseBias, style.CloudAmbientWeight, style.CloudAbsorption),
        CloudNoise = new Vector4(style.CloudNoiseScale, style.CloudDetailScale, style.CloudDetailYScale, 0f),

        Misc = new Vector4(style.CloudReflectionStrength, style.TerrainFieldSelector, 0f, 0f),

        GroundTint = new Vector4(style.GroundTint, style.GroundTintStrength)
    };
}
