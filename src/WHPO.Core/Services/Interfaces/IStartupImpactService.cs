using System;
using System.Collections.Generic;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Nivel de impacto de una entrada de inicio, con los mismos cortes que usa
/// Windows en el Administrador de tareas.
/// </summary>
public enum StartupImpactLevel
{
    None,        // deshabilitada: no arranca, no tiene impacto
    NotMeasured, // habilitada, pero Windows no tiene una medición de ella
    Low,
    Medium,
    High
}

/// <summary>
/// Medición de una entrada de inicio en el último inicio de sesión: CPU y
/// disco consumidos durante el arranque, según el reporte de WDI de Windows.
/// </summary>
public sealed record StartupImpact(StartupImpactLevel Level, TimeSpan CpuTime, long DiskBytes)
{
    public bool HasMeasurement => Level is StartupImpactLevel.Low
        or StartupImpactLevel.Medium or StartupImpactLevel.High;

    public static StartupImpact Disabled { get; } = new(StartupImpactLevel.None, TimeSpan.Zero, 0);
    public static StartupImpact Unknown { get; } = new(StartupImpactLevel.NotMeasured, TimeSpan.Zero, 0);
}

/// <summary>
/// Impacto de las aplicaciones que arrancan con el sistema.
/// </summary>
public interface IStartupImpactService
{
    /// <summary>
    /// Impacto por Id de entrada. Se calcula con el reporte que Windows genera
    /// en cada inicio de sesión (%windir%\System32\WDI\LogFiles\StartupInfo),
    /// que es la misma fuente que usa el Administrador de tareas.
    /// </summary>
    IReadOnlyDictionary<string, StartupImpact> GetImpacts(IReadOnlyList<StartupEntry> entries);
}
