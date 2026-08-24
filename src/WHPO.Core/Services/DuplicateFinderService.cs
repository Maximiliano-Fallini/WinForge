using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Buscador de duplicados: escanea carpetas recursivamente, descarta archivos
/// con tamaño único (no tiene sentido hashearlos) y compara los restantes en
/// dos pasadas EN PARALELO: primero un hash parcial de 64 KB (descarta la gran
/// mayoría sin leer archivos enteros) y después el hash completo solo de los
/// grupos que chocaron en el parcial.
///
/// El borrado es seguro: cada archivo se intenta individualmente; si uno falla
/// (en uso, sin permisos) no tira la operación entera.
/// </summary>
public sealed class DuplicateFinderService : IDuplicateFinderService
{
    private readonly ILoggingService _logging;

    public DuplicateFinderService(ILoggingService logging)
    {
        _logging = logging;
    }

    private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$Recycle.Bin", "$WinREAgent",
        "WindowsApps", "Packages"
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sys", ".dll", ".ocx", ".exe", ".mui", ".cat", ".manifest",
        ".etl", ".evtx", ".pol", ".cpl"
    };

    public Task<DuplicateScanResult> ScanAsync(
        IReadOnlyList<string> directories,
        IProgress<(double Percent, string Path)>? progress = null,
        CancellationToken ct = default)
        => Task.Run(() => ScanSync(directories, progress, ct), ct);

    public Task<IReadOnlyList<string>> DeleteAsync(IReadOnlyList<string> files, bool toRecycleBin = false, CancellationToken ct = default)
        => Task.Run(() => DeleteSync(files, toRecycleBin, ct), ct);

    // =====================================================================
    // Protección de raíces del sistema (estilo CCleaner)
    // =====================================================================

    // Solo C:\Windows queda bloqueada por ubicación: sus archivos son
    // mayoritariamente ocultos/de sistema y además concentran los hardlinks
    // de WinSxS; borrar "duplicados" de ahí rompe componentes de Windows.
    // El resto (incluido Program Files) se escanea como cualquier carpeta:
    // la protección ahí la da el filtro de atributos oculto/sistema.
    private static readonly string[] ProtectedRoots = BuildProtectedRoots();

    private static string[] BuildProtectedRoots()
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.IsNullOrEmpty(windowsDir) ? [] : [NormalizeDir(windowsDir)];
    }

    private static string NormalizeDir(string path) => path.TrimEnd('\\') + '\\';

    /// <summary>
    /// True si la ruta queda dentro de una raíz protegida (la raíz misma incluida).
    /// </summary>
    private static bool IsInProtectedRoot(string path)
    {
        try
        {
            var full = NormalizeDir(Path.GetFullPath(path));
            foreach (var root in ProtectedRoots)
            {
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    // =====================================================================
    // Scan
    // =====================================================================

    private DuplicateScanResult ScanSync(
        IReadOnlyList<string> dirs,
        IProgress<(double Percent, string Path)>? progress,
        CancellationToken ct)
    {
        // ----- Fase 1: enumerar todos los archivos y agrupar por tamaño -----
        var bySize = new Dictionary<long, List<FileSystemInfo>>();
        long totalFiles = 0;

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir)) continue;

            // Raíces del sistema protegidas: avisar y saltar (estilo CCleaner).
            if (IsInProtectedRoot(dir))
            {
                _logging.LogWarning(
                    $"[Duplicados] Raíz del sistema protegida, no se escanea: {dir}");
                continue;
            }

            EnumerateRecursive(dir, bySize, ref totalFiles, progress, ct);
        }

        // Solo archivos de igual tamaño pueden ser duplicados:
        // descartar tamaños con un solo archivo.
        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        long totalCandidates = candidates.Sum(kv => kv.Value.Count);

        if (totalCandidates == 0)
            return new DuplicateScanResult([], 0, 0, totalFiles);

        // ----- Fase 2: hasheado en dos pasadas EN PARALELO (estilo CCleaner) -----
        // 2a) Hash parcial (primeros 64 KB) de TODOS los candidatos a la vez:
        //     casi siempre separa archivos distintos sin leerlos enteros. Con
        //     varios núcleos y solo 64 KB por archivo, esta pasada vuela.
        // ----- Fase 2-pre: colapsar enlaces duros -----
        // Varias rutas pueden apuntar AL MISMO archivo físico (hardlinks; típico
        // del almacén de componentes de Windows: WinSxS ↔ System32/SystemApps).
        // Son "duplicados" de contenido pero NO desperdician espacio: borrar una
        // referencia no libera nada y puede romper el sistema. Cada archivo único
        // (volumen + índice de nodo) entra una sola vez al análisis.
        var flat = candidates.SelectMany(kv => kv.Value).OfType<FileInfo>().ToList();
        var identidades = new HashSet<(uint VolumeSerial, ulong FileIndex)>();
        var sinHardlinks = new List<FileInfo>(flat.Count);
        int copiasHardlink = 0;
        foreach (var fi in flat)
        {
            var id = TryGetLinkIdentity(fi.FullName);
            if (id.HasValue && !identidades.Add(id.Value))
            {
                copiasHardlink++;
                continue;
            }
            sinHardlinks.Add(fi);
        }
        if (copiasHardlink > 0)
            _logging.LogDebug(
                $"[Duplicados] {copiasHardlink} rutas eran enlaces duros a archivos ya contados: excluidas del análisis.");
        flat = sinHardlinks;
        int flatTotal = flat.Count;

        var partialBuckets = new Dictionary<(long Size, string Hash), List<FileInfo>>();
        var bucketLock = new object();
        int partialDone = 0;

        Parallel.For(0, flat.Count,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                var fi = flat[i];
                string ph;
                try { ph = ComputeMd5(fi.FullName, 65_536); }
                catch { return; } // en uso / sin permisos: se omite

                lock (bucketLock)
                {
                    var key = (fi.Length, ph);
                    if (!partialBuckets.TryGetValue(key, out var list))
                        partialBuckets[key] = list = [];
                    list.Add(fi);
                }

                // ~60% del progreso total va para la pasada parcial.
                int done = Interlocked.Increment(ref partialDone);
                if (done % 25 == 0)
                    progress?.Report((0.6 * done / flatTotal, fi.DirectoryName ?? ""));
            });

        // 2b) Solo los grupos que CHOCARON en el hash parcial se hashean completos:
        //     el hash parcial es de 64 KB, dos archivos distintos pueden coincidir
        //     por azar; acá se confirma con el archivo entero.
        var collided = partialBuckets.Where(kv => kv.Value.Count > 1).ToList();
        long fullTotal = collided.Sum(kv => kv.Value.Count);
        if (fullTotal == 0)
            return new DuplicateScanResult([], 0, 0, totalFiles);

        var exactGroups = new ConcurrentQueue<(string Hash, List<FileInfo> Files)>();
        int fullDone = 0;

        Parallel.ForEach(collided,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            kv =>
            {
                var exact = new Dictionary<string, List<FileInfo>>(StringComparer.Ordinal);
                foreach (var fi in kv.Value)
                {
                    ct.ThrowIfCancellationRequested();
                    string fh;
                    try { fh = ComputeMd5(fi.FullName); }
                    catch { continue; }

                    if (!exact.TryGetValue(fh, out var list))
                        exact[fh] = list = [];
                    list.Add(fi);
                }

                foreach (var g in exact.Values)
                    if (g.Count > 1)
                        exactGroups.Enqueue((kv.Key.Hash, g));

                // El 40% restante del progreso va para la pasada completa.
                int done = Interlocked.Add(ref fullDone, kv.Value.Count);
                progress?.Report((0.6 + 0.4 * done / fullTotal, ""));
            });

        // ----- Fase 3: construir grupos -----
        var groups = new List<DuplicateGroup>();
        long totalDupBytes = 0;
        int totalDupFiles = 0;

        // Ordenar por espacio desperdiciado: los duplicados más grandes primero.
        foreach (var (hash, files) in exactGroups.OrderByDescending(g => g.Files[0].Length * (g.Files.Count - 1)))
        {
            var length = files[0].Length;
            var infos = files.Select(f => new DuplicateFileInfo(f.FullName, f.Length, hash)).ToList();

            // Quedarse con la copia más vieja (la "original") al principio.
            infos.Sort((a, b) =>
            {
                try { return File.GetLastWriteTime(a.FullPath).CompareTo(File.GetLastWriteTime(b.FullPath)); }
                catch { return 0; }
            });

            groups.Add(new DuplicateGroup(hash, length, infos));
            totalDupBytes += length * (files.Count - 1);
            totalDupFiles += files.Count - 1;
        }

        return new DuplicateScanResult(groups, totalDupBytes, totalDupFiles, totalFiles);
    }

    private static void EnumerateRecursive(
        string dir,
        Dictionary<long, List<FileSystemInfo>> bySize,
        ref long total,
        IProgress<(double Percent, string Path)>? progress,
        CancellationToken ct)
    {
        try
        {
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                if ((fsi.Attributes & FileAttributes.Directory) != 0)
                {
                    if (IgnoredFolders.Contains(fsi.Name)) continue;
                    if ((fsi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (IsInProtectedRoot(fsi.FullName)) continue;
                    EnumerateRecursive(fsi.FullName, bySize, ref total, progress, ct);
                }
                else
                {
                    // Archivos ocultos o de sistema excluidos (estilo CCleaner):
                    // no son basura del usuario y borrarlos es peligroso.
                    if ((fsi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (IgnoredExtensions.Contains(fsi.Extension)) continue;
                    if (fsi is not FileInfo fi) continue;

                    // Descartar archivos muy chicos o muy grandes para el hashing.
                    long len = fi.Length;
                    if (len < 1024 || len > 500_000_000) continue;

                    if (!bySize.TryGetValue(len, out var list))
                        bySize[len] = list = [];
                    list.Add(fi);
                    total++;

                    if (total % 200 == 0)
                        progress?.Report((0, fi.DirectoryName ?? ""));
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// Identidad física de un archivo: (número de volumen, índice de nodo).
    /// Dos rutas con la misma identidad son enlaces duros AL MISMO archivo:
    /// mismo contenido por definición y un solo ocupado real en disco.
    /// Devuelve null si no se pudo resolver (se trata como archivo normal).
    /// </summary>
    private static (uint VolumeSerial, ulong FileIndex)? TryGetLinkIdentity(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info)) return null;
            ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return (info.VolumeSerialNumber, index);
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    /// <summary>
    /// MD5 del contenido del archivo. Con maxBytes &gt; 0 lee SOLO los primeros
    /// bytes (hash parcial rápido); con -1 hashea el archivo entero.
    /// </summary>
    private static string ComputeMd5(string path, long maxBytes = -1)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            262_144, FileOptions.SequentialScan);
        using var md5 = System.Security.Cryptography.MD5.Create();

        if (maxBytes > 0 && fs.Length > maxBytes)
        {
            var buf = new byte[maxBytes];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return Convert.ToHexStringLower(md5.ComputeHash(buf, 0, read));
        }

        return Convert.ToHexStringLower(md5.ComputeHash(fs));
    }

    // =====================================================================
    // Delete
    // =====================================================================

    private static IReadOnlyList<string> DeleteSync(IReadOnlyList<string> files, bool toRecycleBin, CancellationToken ct)
    {
        var deletedPaths = new List<string>();
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(path)) continue;
                File.SetAttributes(path, FileAttributes.Normal);
                if (toRecycleBin)
                {
                    // A la papelera: recuperable si el usuario se arrepiente.
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    File.Delete(path);
                }
                deletedPaths.Add(path);
            }
            catch { /* archivo en uso */ }
        }
        return deletedPaths;
    }
}