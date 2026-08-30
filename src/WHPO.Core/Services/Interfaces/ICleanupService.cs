using System.Collections.Generic;
using System.Threading;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Elemento de limpieza de la "Limpieza personalizada" (una fila con su check).
/// </summary>
public sealed record CleanupTargetInfo(
    string Id,
    string Name,
    string Description,
    bool DefaultChecked = true,
    bool AnalysisOnly = false,
    bool IsAdvanced = false
);

/// <summary>
/// Categoría de la "Limpieza personalizada": Sistema de Windows, Multimedia,
/// Utilidades, Descargas de Windows y Avanzado.
/// </summary>
public sealed record CleanupCategoryInfo(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<CleanupTargetInfo> Targets
);

/// <summary>
/// Resultado del análisis de un ítem: cuánto ocupa y cuántos archivos encontró.
/// </summary>
public sealed record CleanupItemResult(
    string Id,
    string Name,
    long Bytes,
    int FileCount,
    bool AnalysisOnly,
    string? Warning = null
);

public sealed record CleanupScanResult(
    IReadOnlyList<CleanupItemResult> Items,
    long TotalBytes,
    IReadOnlyList<string> Warnings
);

public sealed record CleanupCleanResult(
    IReadOnlyList<CleanupItemResult> Items,
    long TotalBytes,
    IReadOnlyList<string> Warnings
);

/// <summary>Partes limpiables de un navegador (los 3 checks de la pestaña Chequeo).</summary>
public enum BrowserSubItem
{
    Cache,
    Cookies,
    History
}

/// <summary>
/// Navegador detectado (o no) para la pestaña Chequeo: si está instalado y/o
/// corriendo, su color de marca y los perfiles encontrados.
/// </summary>
public sealed record BrowserCleanupInfo(
    string Id,
    string DisplayName,
    string ProcessName,
    string AccentColor,
    string ExePath,
    bool IsInstalled,
    bool IsRunning,
    IReadOnlyList<string> ProfileDirs
);

/// <summary>
/// Servicio de limpieza del dispositivo: analiza y borra archivos basura del
/// sistema, aplicaciones y navegadores usando rutas seguras y conocidas.
/// </summary>
public interface ICleanupService
{
    /// <summary>Categorías de la "Limpieza personalizada".</summary>
    IReadOnlyList<CleanupCategoryInfo> GetCustomCategories();

    /// <summary>
    /// Navegadores conocidos (Chrome, Edge, Firefox, Opera, Brave) con estado de
    /// instalación/ejecución actual.
    /// </summary>
    IReadOnlyList<BrowserCleanupInfo> GetBrowsers();

    /// <summary>Analiza los targets personalizados seleccionados (id de CleanupTargetInfo).</summary>
    System.Threading.Tasks.Task<CleanupScanResult> ScanCustomAsync(
        IReadOnlyCollection<string> targetIds,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>Analiza partes (caché/cookies/historial) de un navegador.</summary>
    System.Threading.Tasks.Task<CleanupScanResult> ScanBrowserAsync(
        string browserId,
        IReadOnlyCollection<BrowserSubItem> items,
        CancellationToken ct = default);

    /// <summary>Borra los archivos de los ítems personalizados seleccionados.</summary>
    System.Threading.Tasks.Task<CleanupCleanResult> CleanCustomAsync(
        IReadOnlyCollection<string> targetIds,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Borra partes de un navegador. Si <paramref name="closeIfRunning"/> está
    /// activo y el navegador está abierto, se cierra primero (si no, se omite con
    /// una advertencia).
    /// </summary>
    System.Threading.Tasks.Task<CleanupCleanResult> CleanBrowserAsync(
        string browserId,
        IReadOnlyCollection<BrowserSubItem> items,
        bool closeIfRunning,
        CancellationToken ct = default);
}