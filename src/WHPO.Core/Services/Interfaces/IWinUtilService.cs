using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Estado detectado de una característica opcional de Windows (DISM).
/// </summary>
public enum FeatureState
{
    Enabled,
    Disabled,
    Pending,
    Unknown
}

/// <summary>
/// Característica opcional de Windows activable/desactivable (Features de winutil).
/// </summary>
public record WinFeatureInfo(
    string Id,
    string Name,
    string Description,
    string[] FeatureNames,
    bool NeedsRestart = false);

/// <summary>
/// Utilidad de un solo uso (Fixes de winutil).
/// </summary>
public record WinFixInfo(
    string Id,
    string Name,
    string Description,
    bool IsLongRunning = false,
    bool RequiresRestart = false,
    bool SupportsRevert = false);

/// <summary>
/// Panel clásico de Windows que se abre con un botón (Legacy Windows Panels de winutil).
/// </summary>
public record WindowsPanelInfo(
    string Id,
    string Name,
    string Description,
    string LaunchCommand);

/// <summary>
/// Servicio con las secciones "Features", "Fixes" y "Legacy Windows Panels" de winutil
/// (Chris Titus Tech), adaptadas a WinForge.
/// </summary>
public interface IWinUtilService
{
    /// <summary>Características opcionales de Windows (Features).</summary>
    List<WinFeatureInfo> GetFeatures();

    /// <summary>Estado actual de una característica (DISM / Get-FeatureInfo).</summary>
    Task<FeatureState> GetFeatureStateAsync(string featureId);

    /// <summary>Activa una característica (DISM /Enable-Feature).</summary>
    Task<CommandResult> EnableFeatureAsync(string featureId, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Desactiva una característica (DISM /Disable-Feature).</summary>
    Task<CommandResult> DisableFeatureAsync(string featureId, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Utilidades de un solo uso (Fixes).</summary>
    List<WinFixInfo> GetFixes();

    /// <summary>Ejecuta una utilidad (Fix).</summary>
    Task<CommandResult> RunFixAsync(string fixId, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Revierte una utilidad (Fix) con estado reversible (ej. volver al NTP de Windows).</summary>
    Task<CommandResult> RevertFixAsync(string fixId, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Configura el inicio de sesión automático (AutoLogon).</summary>
    Task<CommandResult> SetAutoLogonAsync(string username, string password, string? domain = null);

    /// <summary>Quita la configuración de inicio de sesión automático.</summary>
    Task<CommandResult> RemoveAutoLogonAsync();

    /// <summary>Paneles clásicos de Windows disponibles.</summary>
    List<WindowsPanelInfo> GetPanels();

    /// <summary>Abre un panel clásico de Windows.</summary>
    Task<CommandResult> LaunchPanelAsync(string panelId);
}
