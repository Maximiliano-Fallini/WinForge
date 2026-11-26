using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Xml.Linq;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// "Startup impact" como el del Administrador de tareas de Windows.
///
/// Windows mide el costo real de cada app en el arranque con WDI (Windows
/// Diagnostics Infrastructure) y deja el resultado por usuario en
/// %windir%\System32\WDI\LogFiles\StartupInfo\&lt;SID&gt;_StartupInfo&lt;N&gt;.xml.
/// Ese XML trae, por proceso, su línea de comandos, el tiempo de CPU en
/// microsegundos y el disco leído/escrito en bytes. El Administrador de tareas
/// lee justo esos archivos y aplica los cortes oficiales de Microsoft:
///
///   Alto   = más de 1 s de CPU o más de 3 MB de disco
///   Medio  = 300 ms – 1 s de CPU, o 300 KB – 3 MB de disco
///   Bajo   = menos de 300 ms de CPU y menos de 300 KB de disco
///
/// Acá se hace lo mismo: se toman los datos de la última medición, se asocia
/// cada proceso con su entrada (incluidos los procesos hijos que lanza) y se
/// suma CPU y disco antes de clasificar.
/// </summary>
public sealed class StartupImpactService : IStartupImpactService
{
    private const double MediumCpuMs = 300;
    private const double HighCpuMs = 1000;
    private const long MediumDiskBytes = 300_000;
    private const long HighDiskBytes = 3_000_000;

    private readonly ILoggingService _logging;

    public StartupImpactService(ILoggingService logging) => _logging = logging;

    public IReadOnlyDictionary<string, StartupImpact> GetImpacts(IReadOnlyList<StartupEntry> entries)
    {
        var result = new Dictionary<string, StartupImpact>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            result[entry.Id] = entry.IsEnabled ? StartupImpact.Unknown : StartupImpact.Disabled;

        try
        {
            var enabled = entries.Where(e => e.IsEnabled).ToList();
            if (enabled.Count == 0) return result;

            // Ejecutable de cada entrada (o nombre del .exe si el .lnk no se pudo
            // resolver) y carpeta de paquete para las apps MSIX/UWP.
            var byExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byPackageDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in enabled)
            {
                if (entry.Source == StartupSource.PackagedApp)
                {
                    var dir = entry.Command?.TrimEnd('\\');
                    if (!string.IsNullOrEmpty(dir)) byPackageDir[NormalizePath(dir)] = entry.Id;
                    continue;
                }

                var exe = ResolveEntryExecutable(entry);
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    byExe[NormalizePath(exe)] = entry.Id;
                }
                else if (entry.Source == StartupSource.StartupFolder)
                {
                    // Acceso directo sin destino resoluble: el nombre del .lnk
                    // suele coincidir con el del ejecutable.
                    var name = Path.GetFileNameWithoutExtension(entry.Command);
                    if (!string.IsNullOrWhiteSpace(name))
                        byFileName[name + ".exe"] = entry.Id;
                }
            }

            if (byExe.Count == 0 && byFileName.Count == 0 && byPackageDir.Count == 0)
                return result;

            var samples = ReadLatestBootSamples();
            if (samples.Count == 0)
            {
                _logging.LogDebug("StartupImpact: Windows no tiene reporte de inicio (WDI) para este usuario.");
                return result;
            }

            var byPid = new Dictionary<int, BootSample>();
            foreach (var sample in samples)
                if (sample.Pid > 0) byPid[sample.Pid] = sample;

            var totals = new Dictionary<string, (long CpuUs, long Disk)>(StringComparer.OrdinalIgnoreCase);
            foreach (var sample in samples)
            {
                var owner = FindOwner(sample, byPid, byExe, byFileName, byPackageDir);
                if (owner == null) continue;
                totals.TryGetValue(owner, out var acc);
                totals[owner] = (acc.CpuUs + sample.CpuMicroseconds, acc.Disk + sample.DiskBytes);
            }

            foreach (var (id, total) in totals)
                result[id] = Classify(total.CpuUs, total.Disk);

            _logging.LogDebug($"StartupImpact: {samples.Count} procesos del último inicio, {totals.Count} entradas medidas.");
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"StartupImpact: no se pudo leer el reporte de Windows: {ex.Message}");
        }

        return result;
    }

    // =====================================================================
    // Asociación proceso → entrada de inicio
    // =====================================================================

    /// <summary>
    /// El proceso pertenece a la entrada más cercana en su cadena de padres:
    /// así los hijos que lanza una app de inicio (updaters, helpers, webviews)
    /// suman a esa app y no a la nada.
    /// </summary>
    private static string? FindOwner(
        BootSample sample,
        Dictionary<int, BootSample> byPid,
        Dictionary<string, string> byExe,
        Dictionary<string, string> byFileName,
        Dictionary<string, string> byPackageDir)
    {
        var current = sample;
        for (int depth = 0; depth < 32 && current != null; depth++)
        {
            var exe = NormalizePath(StartupManagerService.ExtractExePathFromCommand(current.CommandLine));

            // Algunos procesos del reporte usan rutas relativas (".\\bin\\x.exe"):
            // el atributo Name del XML sí trae la ruta completa.
            if (exe.Length == 0 || !Path.IsPathRooted(exe))
            {
                var named = NormalizePath(current.Name);
                if (named.Length > 0) exe = named;
            }

            if (exe.Length > 0)
            {
                if (byExe.TryGetValue(exe, out var id)) return id;

                foreach (var (dir, packageId) in byPackageDir)
                    if (exe.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
                        return packageId;

                if (byFileName.TryGetValue(Path.GetFileName(exe), out var nameId))
                    return nameId;
            }

            current = byPid.TryGetValue(current.ParentPid, out var parent) ? parent : null;
        }
        return null;
    }

    /// <summary>
    /// Ejecutable que lanza una entrada. En la carpeta de inicio el comando es
    /// el .lnk: se resuelve su destino. Null si no se pudo resolver.
    /// </summary>
    private static string? ResolveEntryExecutable(StartupEntry entry)
    {
        if (entry.Source == StartupSource.StartupFolder)
        {
            var path = entry.Command;
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return path;
            return ResolveShortcutTarget(path);
        }

        return StartupManagerService.ExtractExePathFromCommand(entry.Command);
    }

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            if (!File.Exists(lnkPath)) return null;

            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.NameSpace(Path.GetDirectoryName(lnkPath));
            dynamic item = folder.ParseName(Path.GetFileName(lnkPath));
            string path = item.GetLink.Path;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch { return null; }
    }

    // =====================================================================
    // Lectura del reporte de Windows
    // =====================================================================

    private List<BootSample> ReadLatestBootSamples()
    {
        var dir = Path.Combine(Environment.SystemDirectory, "WDI", "LogFiles", "StartupInfo");
        if (!Directory.Exists(dir)) return new List<BootSample>();

        string sid = "";
        try { sid = WindowsIdentity.GetCurrent().User?.Value ?? ""; } catch { }

        var files = new List<string>();
        try
        {
            files.AddRange(Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly));

            // Algunas builds guardan un subdirectorio por usuario con su SID.
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (sid.Length > 0 && !name.Equals(sid, StringComparison.OrdinalIgnoreCase)) continue;
                try { files.AddRange(Directory.EnumerateFiles(sub, "*.xml", SearchOption.TopDirectoryOnly)); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"StartupImpact: sin acceso a {dir}: {ex.Message}");
            return new List<BootSample>();
        }

        // El más reciente es el del último inicio de sesión. Si la carpeta tiene
        // reportes de varios usuarios, quedarse con los del usuario actual:
        // mezclarlos mostraría el impacto de las apps de otra cuenta.
        var candidates = files
            .Where(f => Path.GetFileName(f).Contains("StartupInfo", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sid.Length > 0)
        {
            var mine = candidates
                .Where(f => Path.GetFileName(f).Contains(sid + "_", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (mine.Count > 0) candidates = mine;
        }

        candidates = candidates.OrderByDescending(SafeWriteTime).ToList();

        foreach (var file in candidates)
        {
            var samples = TryRead(file);
            if (samples.Count > 0) return samples;
        }

        return new List<BootSample>();
    }

    private List<BootSample> TryRead(string path)
    {
        var list = new List<BootSample>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var doc = XDocument.Load(stream);
            var root = doc.Root;
            if (root == null) return list;

            foreach (var node in root.Elements().Where(e => e.Name.LocalName == "Process"))
            {
                _ = int.TryParse(node.Attribute("PID")?.Value, out int pid);
                _ = int.TryParse(Child(node, "ParentPID")?.Value, out int parentPid);
                var commandLine = Child(node, "CommandLine")?.Value ?? "";

                list.Add(new BootSample(
                    pid,
                    parentPid,
                    commandLine.Trim(),
                    node.Attribute("Name")?.Value ?? "",
                    ReadCpuMicroseconds(Child(node, "CpuUsage")),
                    ReadBytes(Child(node, "DiskUsage"))));
            }
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"StartupImpact: {Path.GetFileName(path)} ilegible: {ex.Message}");
        }
        return list;
    }

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    /// <summary>CPU en microsegundos (el XML de Windows la publica en "us").</summary>
    private static long ReadCpuMicroseconds(XElement? node)
    {
        if (node == null) return 0;
        if (!long.TryParse(node.Value.Trim(), out long value)) return 0;

        return (node.Attribute("Units")?.Value ?? "us").ToLowerInvariant() switch
        {
            "ns" => value / 1000,
            "100ns" => value / 10,
            "ms" => value * 1000,
            "s" => value * 1_000_000,
            _ => value
        };
    }

    private static long ReadBytes(XElement? node)
    {
        if (node == null) return 0;
        if (!long.TryParse(node.Value.Trim(), out long value)) return 0;

        return (node.Attribute("Units")?.Value ?? "bytes").ToLowerInvariant() switch
        {
            "kb" => value * 1024,
            "mb" => value * 1024 * 1024,
            "gb" => value * 1024 * 1024 * 1024,
            _ => value
        };
    }

    // =====================================================================
    // Clasificación y utilidades
    // =====================================================================

    private static StartupImpact Classify(long cpuMicroseconds, long diskBytes)
    {
        double cpuMs = cpuMicroseconds / 1000.0;

        var level = cpuMs > HighCpuMs || diskBytes > HighDiskBytes
            ? StartupImpactLevel.High
            : cpuMs >= MediumCpuMs || diskBytes >= MediumDiskBytes
                ? StartupImpactLevel.Medium
                : StartupImpactLevel.Low;

        // 1 µs = 10 ticks (100 ns).
        return new StartupImpact(level, TimeSpan.FromTicks(cpuMicroseconds * 10), diskBytes);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        var value = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).Replace('/', '\\');

        // El kernel escribe algunas rutas con prefijo de espacio de nombres.
        if (value.StartsWith(@"\??\", StringComparison.Ordinal) ||
            value.StartsWith(@"\\?\", StringComparison.Ordinal))
            value = value.Substring(4);

        try { value = Path.GetFullPath(value); } catch { }
        return value.TrimEnd('\\');
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    private sealed record BootSample(
        int Pid,
        int ParentPid,
        string CommandLine,
        string Name,
        long CpuMicroseconds,
        long DiskBytes);
}
