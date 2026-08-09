using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio para aplicar y revertir tweaks del sistema.
/// </summary>
public interface ITweakService
{
    /// <summary>
    /// Obtiene todos los tweaks disponibles con su estado actual.
    /// </summary>
    List<TweakDefinition> GetAllTweaks();

    /// <summary>
    /// Verifica si un tweak está aplicado actualmente.
    /// </summary>
    bool IsTweakApplied(string tweakId);

    /// <summary>
    /// Aplica un tweak del sistema.
    /// </summary>
    Task<TweakResult> ApplyTweakAsync(string tweakId);

    /// <summary>
    /// Revierte un tweak del sistema.
    /// </summary>
    Task<TweakResult> RevertTweakAsync(string tweakId);

    /// <summary>
    /// Evento que se dispara cuando cambia el estado de un tweak.
    /// </summary>
    event Action<string, bool>? TweakStateChanged;
}

/// <summary>
/// Definición completa de un tweak con su acción de aplicar y revertir.
/// </summary>
public record TweakDefinition(
    string Id,
    string Name,
    string Description,
    string Compatibility,
    bool IsReversible,
    string Category,
    bool RequiresAdmin,
    Func<bool> CheckApplied,
    Func<Task<TweakResult>> ApplyAction,
    Func<Task<TweakResult>> RevertAction
);

/// <summary>
/// Resultado de aplicar o revertir un tweak.
/// </summary>
public record TweakResult(
    bool Success,
    string Message
);