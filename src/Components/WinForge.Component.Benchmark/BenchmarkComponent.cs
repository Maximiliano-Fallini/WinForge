using System;
using WHPO.Core.Components;

namespace WinForge.Component.Benchmark;

/// <summary>
/// Componente "Benchmark de escenas 3D": corre escenas propias sobre la API gráfica que elija
/// el usuario (D3D11, D3D12, Vulkan, OpenGL a medida que se implementan), muestra las métricas
/// de la máquina en vivo mientras corre y entrega un informe de MÉTRICAS — FPS, percentiles,
/// hitches, ms de GPU y de CPU por frame, temperaturas, frecuencias y potencias — sin puntaje
/// compuesto: cada número se puede explicar y comparar.
///
/// UI construida 100% en código (sin XAML compilado), como el resto de los componentes del
/// Workshop: los assemblies cargados por AssemblyLoadContext no traen su propio XamlTypeInfo.
/// </summary>
public sealed class BenchmarkComponent : IWinForgeComponent
{
    public string Id => "benchmark";
    public string Name => "Benchmark de escenas 3D";
    public string Description => "Escenas 3D propias para medir la placa y el equipo, con las métricas en vivo y un informe comparable (sin puntaje).";
    public string IconGlyph => "\uE9D9";   // Diagnostic / rendimiento
    public ComponentCategory Category => ComponentCategory.Rendimiento;
    public string Version => "0.1.0";
    public string MinAppVersion => "";
    public bool IsCore => false;

    public object CreatePage(IServiceProvider services) => new BenchmarkPage();
}
