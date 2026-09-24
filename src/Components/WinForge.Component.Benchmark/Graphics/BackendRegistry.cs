using System;
using System.Collections.Generic;
using System.Linq;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Registro de las APIs gráficas: sondea cuáles existen en ESTA máquina y crea la que el
/// usuario eligió. El selector de la página se arma con esto, así que nunca se puede elegir
/// una API que después falle sin explicación.
///
/// Direct3D 11 y Direct3D 12 están implementadas; Vulkan y OpenGL están DECLARADAS con su
/// motivo ("todavía no implementada"), que es más honesto que esconderlas o, peor, ofrecerlas
/// y caerse al arrancar: el día que se implementa su backend, aparece sola en el selector.
/// </summary>
public static class BackendRegistry
{
    /// <summary>APIs que el plan contempla, en el orden en que se muestran.</summary>
    public static readonly GraphicsApi[] SupportedApis =
    {
        GraphicsApi.D3D11,
        GraphicsApi.D3D12,
        GraphicsApi.Vulkan,
        GraphicsApi.OpenGL
    };

    /// <summary>Nombre visible de cada API (texto fuente en español).</summary>
    public static string NameOf(GraphicsApi api) => api switch
    {
        GraphicsApi.D3D11 => "Direct3D 11",
        GraphicsApi.D3D12 => "Direct3D 12",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => api.ToString()
    };

    /// <summary>Sondea todas las APIs: disponibilidad y motivo, listas para el selector.</summary>
    public static IReadOnlyList<BackendAvailability> ProbeAll()
    {
        var result = new List<BackendAvailability>(SupportedApis.Length);
        foreach (var api in SupportedApis) result.Add(Probe(api));
        return result;
    }

    public static BackendAvailability Probe(GraphicsApi api)
    {
        try
        {
            return api switch
            {
                GraphicsApi.D3D11 => D3D11Backend.Probe(),
                GraphicsApi.D3D12 => D3D12Backend.Probe(),
                _ => new BackendAvailability(api, NameOf(api), false,
                    $"{NameOf(api)} todavía no está implementada en este componente.")
            };
        }
        catch (Exception ex)
        {
            return new BackendAvailability(api, NameOf(api), false, ex.Message);
        }
    }

    /// <summary>Crea el backend de la API pedida. Tira si la API no está implementada.</summary>
    public static IGraphicsBackend Create(GraphicsApi api) => api switch
    {
        GraphicsApi.D3D11 => new D3D11Backend(),
        GraphicsApi.D3D12 => new D3D12Backend(),
        _ => throw new NotSupportedException($"{NameOf(api)} todavía no está implementada en este componente.")
    };

    /// <summary>
    /// Adaptadores que reporta DXGI (para el selector de placa). Es una sola lista para todas
    /// las APIs: DXGI es el denominador común, así que cambiar de API no cambia la placa medida.
    /// </summary>
    public static IReadOnlyList<AdapterInfo> ListAdapters(GraphicsApi api) => DxgiShared.ListAdapters();

    /// <summary>Primera API disponible, o null si ninguna lo está.</summary>
    public static GraphicsApi? FirstAvailable() =>
        ProbeAll().Where(a => a.Available).Select(a => a.Api).Cast<GraphicsApi?>().FirstOrDefault();
}
