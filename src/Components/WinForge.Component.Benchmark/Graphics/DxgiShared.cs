using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.Direct3D;
using Vortice.DXGI;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>Adaptador de video tal como lo reporta DXGI (lo comparten Direct3D 11 y 12).</summary>
public sealed record AdapterInfo(int Index, string Name, long DedicatedVideoMemory, bool IsSoftware);

/// <summary>
/// Lo que DXGI responde igual para Direct3D 11 y Direct3D 12: qué adaptadores hay, cuál elegir
/// y cómo se llama el feature level. Vive acá para que agregar un backend no duplique la
/// enumeración (y para que los dos elijan la MISMA placa por defecto: si cada uno elegía la
/// suya, una corrida en D3D11 y otra en D3D12 podían caer en placas distintas —chip integrado
/// contra dedicada— y la comparación entre APIs mediría otra cosa).
/// </summary>
internal static class DxgiShared
{
    /// <summary>Adaptadores disponibles: índice, nombre, VRAM dedicada y si es software.</summary>
    internal static IReadOnlyList<AdapterInfo> ListAdapters()
    {
        try
        {
            var adapters = Enumerate();
            var result = new List<AdapterInfo>(adapters.Count);
            foreach (var adapter in adapters)
            {
                var description = adapter.Description1;
                result.Add(new AdapterInfo(
                    result.Count,
                    description.Description,
                    (long)(ulong)description.DedicatedVideoMemory,
                    (description.Flags & AdapterFlags.Software) != 0));
                SafeDispose(adapter);
            }
            return result;
        }
        catch
        {
            return Array.Empty<AdapterInfo>();
        }
    }

    /// <summary>
    /// Enumera los adaptadores. Se usa una fábrica aparte de la del swapchain: enumerar es una
    /// operación de DXGI 1.0/1.1 y la del swapchain es la 1.2 (CreateSwapChainForHwnd).
    /// El llamador libera los adaptadores que no use.
    /// </summary>
    internal static List<IDXGIAdapter1> Enumerate()
    {
        var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        try
        {
            var adapters = new List<IDXGIAdapter1>();
            for (uint index = 0; index < 32; index++)
            {
                if (factory.EnumAdapters1(index, out var adapter).Failure || adapter == null) break;
                adapters.Add(adapter);
            }
            return adapters;
        }
        finally
        {
            SafeDispose(factory);
        }
    }

    /// <summary>Adaptador pedido, o el mejor (la placa dedicada con más VRAM) si no se pidió ninguno.</summary>
    internal static IDXGIAdapter1 PickAdapter(List<IDXGIAdapter1> adapters, int requestedIndex)
    {
        if (requestedIndex >= 0 && requestedIndex < adapters.Count) return adapters[requestedIndex];

        IDXGIAdapter1 best = adapters[0];
        long bestMemory = -1;
        foreach (var adapter in adapters)
        {
            var description = adapter.Description1;
            bool isSoftware = (description.Flags & AdapterFlags.Software) != 0;
            long memory = isSoftware ? -1 : (long)(ulong)description.DedicatedVideoMemory;
            if (memory > bestMemory)
            {
                bestMemory = memory;
                best = adapter;
            }
        }
        return best;
    }

    internal static string FeatureLevelName(FeatureLevel level) => level switch
    {
        FeatureLevel.Level_11_1 => "11_1",
        FeatureLevel.Level_11_0 => "11_0",
        FeatureLevel.Level_10_1 => "10_1",
        FeatureLevel.Level_10_0 => "10_0",
        _ => level.ToString()
    };

    internal static void SafeDispose(IDisposable? resource)
    {
        try { resource?.Dispose(); } catch { }
    }

    /// <summary>
    /// ¿Algún adaptador soporta lo que se pregunta? Devuelve el nombre del primero que sí, para
    /// poder decir en el selector de API QUÉ placa lo soporta (no una opción gris sin motivo).
    /// </summary>
    internal static bool TryFindSupporting(Func<IDXGIAdapter1, bool> supported, out string adapterName)
    {
        adapterName = "";
        foreach (var adapter in Enumerate())
        {
            bool ok = false;
            try { ok = supported(adapter); }
            catch { /* un adaptador que no responde no invalida a los demás */ }
            if (ok)
            {
                adapterName = adapter.Description1.Description;
                SafeDispose(adapter);
                return true;
            }
            SafeDispose(adapter);
        }
        return false;
    }
}
