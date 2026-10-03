using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinForge.Component.Benchmark.Scenes;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Esferas de luz puntuales del bloque por frame: hasta <see cref="SceneDefinition.MaxPointLights"/>
/// luces (xyz = posición, w = radio). Igual que los sombreadores de objeto, es un <c>InlineArray</c>
/// para que el bloque siga siendo UNA estructura copiable con <c>MemoryMarshal</c> y para que el
/// tamaño salga del mismo número que usa el layout del cbuffer.
/// </summary>
[InlineArray(SceneDefinition.MaxPointLights)]
internal struct PointLightArray
{
    private Vector4 _element;
}

/// <summary>
/// Las luces puntuales de la escena tal como viajan al shader: la forma de GPU de
/// <see cref="ScenePointLight"/>, hermana de <see cref="SceneStyleConstants"/>.
///
/// Son 16 <c>float4</c> (256 bytes): 8 posiciones con radio y 8 colores con intensidad, todos de 16
/// bytes para que la alineación sea la misma en el cbuffer de HLSL y en el <c>std140</c> de GLSL.
/// Va al final del bloque por frame, después del estilo, para no mover el offset de nada anterior.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SceneLightConstants
{
    /// <summary>Bytes del bloque: es lo que suman los backends al tamaño del cbuffer.</summary>
    internal const int SizeInBytes = 16 * 16;

    /// <summary>xyz = posición de la luz, w = radio de alcance.</summary>
    public PointLightArray PositionRadius;

    /// <summary>rgb = color de la luz, w = intensidad.</summary>
    public PointLightArray ColorIntensity;

    /// <summary>Arma el bloque de GPU con las luces de una escena (las que sobran quedan en cero y
    /// el shader las saltea).</summary>
    internal static SceneLightConstants From(IReadOnlyList<ScenePointLight> lights)
    {
        var constants = new SceneLightConstants();
        int count = Math.Min(lights.Count, SceneDefinition.MaxPointLights);
        for (int i = 0; i < count; i++)
        {
            var light = lights[i];
            constants.PositionRadius[i] = new Vector4(light.Position, light.Radius);
            constants.ColorIntensity[i] = new Vector4(light.Color * light.Intensity, light.Intensity);
        }
        return constants;
    }
}
