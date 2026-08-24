using System.Collections.Generic;
using System.Threading;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Un archivo encontrado por el buscador de duplicados.
/// </summary>
public sealed record DuplicateFileInfo(
    string FullPath,
    long Length,
    string Hash
);

/// <summary>
/// Grupo de archivos duplicados (mismo hash = mismo contenido).
/// </summary>
public sealed record DuplicateGroup(
    string Hash,
    long Length,
    IReadOnlyList<DuplicateFileInfo> Files
);

public sealed record DuplicateScanResult(
    IReadOnlyList<DuplicateGroup> Groups,
    long TotalDuplicateBytes,
    int TotalDuplicateFiles,
    long FilesScanned
);

/// <summary>
/// Buscador de archivos duplicados del dispositivo.
/// Escanea carpetas recursivamente, agrupa por hash (MD5) y permite borrar duplicados.
/// </summary>
public interface IDuplicateFinderService
{
    /// <summary>Escanea las carpetas indicadas. progress recibe (0.0..1.0, \"carpeta actual\").</summary>
    System.Threading.Tasks.Task<DuplicateScanResult> ScanAsync(
        IReadOnlyList<string> directories,
        IProgress<(double Percent, string Path)>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Borra los archivos indicados. Cada archivo se borra individualmente;
    /// si uno falla (en uso, permisos), se omite y sigue con el resto.
    /// Con toRecycleBin=true los archivos van a la Papelera de reciclaje
    /// (recuperables) en vez de borrarse permanentemente.
    /// Devuelve las rutas efectivamente borradas, para que la UI pueda
    /// quitar solo esas del resultado sin re-escanear.
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<string>> DeleteAsync(
        IReadOnlyList<string> files,
        bool toRecycleBin = false,
        CancellationToken ct = default);
}