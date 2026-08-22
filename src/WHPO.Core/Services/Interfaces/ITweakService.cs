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
    /// Verifica si la aplicación asociada a un tweak está instalada (si el tweak
    /// no depende de una app, devuelve siempre true = aplicable).
    /// </summary>
    bool IsTweakAppInstalled(string tweakId);

    /// <summary>
    /// Aplica un tweak del sistema. Si se pasa <paramref name="progress"/>, reporta
    /// los comandos reales que ejecuta (estilo cmd/winutil).
    /// </summary>
    Task<TweakResult> ApplyTweakAsync(string tweakId, IProgress<string>? progress = null);

    /// <summary>
    /// Revierte un tweak del sistema. Si se pasa <paramref name="progress"/>, reporta
    /// los comandos reales que ejecuta (estilo cmd/winutil).
    /// </summary>
    Task<TweakResult> RevertTweakAsync(string tweakId, IProgress<string>? progress = null);

    /// <summary>
    /// Evento que se dispara cuando cambia el estado de un tweak.
    /// </summary>
    event Action<string, bool>? TweakStateChanged;

    /// <summary>
    /// Invalida las cachés de verificación de desinstalación (Appx / Game Bar)
    /// para que una re-detección consulte el estado real del sistema.
    /// </summary>
    void InvalidateAppxChecks();

    /// <summary>
    /// Consulta el estado de instalación de todos los paquetes Appx de debloat en
    /// un solo Get-AppxPackage y rellena la caché (evita un proceso PowerShell por app).
    /// </summary>
    void WarmUpAppxChecks();
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
    Func<Task<TweakResult>> RevertAction,
    Func<bool>? AppInstalled = null,
    // Nombre alternativo cuando la app asociada NO está instalada (p. ej.
    // "O&O ShutUp10++ - Instalar" vs "- Ejecutar"). Si es null, se usa Name.
    string? NameWhenNotInstalled = null
);

/// <summary>
/// Resultado de aplicar o revertir un tweak.
/// </summary>
public record TweakResult(
    bool Success,
    string Message
);