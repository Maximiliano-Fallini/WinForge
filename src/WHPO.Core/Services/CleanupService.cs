using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Limpieza del dispositivo estilo CCleaner: analiza y borra archivos basura del
/// sistema, aplicaciones y navegadores. Usa las rutas canónicas de CCleaner
/// (TEMP de Windows y del usuario, SoftwareDistribution\Download, INetCache,
/// WER, CrashDumps, thumbcache/iconcache, MRUs del registro, Prefetch, etc.).
///
/// Reglas de seguridad:
///  - Cada ítem borra el CONTENIDO de su carpeta pero conserva la raíz.
///  - Los archivos en uso (bloqueados) se omiten y se reportan como advertencia,
///    nunca se reintenta furiosamente ni se tira la operación completa.
///  - Los ítems de "solo análisis" (ruta de entorno PATH) no borran nada.
///  - Para limpiar un navegador abierto hay que cerrarlo explícitamente.
/// </summary>
public sealed class CleanupService : ICleanupService
{
    private readonly ILoggingService _logging;

    public CleanupService(ILoggingService loggingService)
    {
        _logging = loggingService;
    }

    // =====================================================================
    // Catálogo de "Limpieza personalizada"
    // =====================================================================

    private enum TargetKind { Files, RegistryValues, Analysis }

    private sealed class Target
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public bool DefaultChecked { get; init; } = true;
        public bool IsAdvanced { get; init; }
        public TargetKind Kind { get; init; } = TargetKind.Files;

        /// <summary>Para Files: rutas absolutas (carpetas a vaciar o archivos a borrar).</summary>
        public Func<IEnumerable<string>>? GetPaths { get; init; }

        /// <summary>Para Files: patrón opcional ("*.log"); null = todo el contenido.</summary>
        public string? Pattern { get; init; }

        /// <summary>Para RegistryValues: clave completa de HKCU.</summary>
        public string? RegistryPath { get; init; }

        /// <summary>Para RegistryValues: nombres de valor a borrar ("*" = todos).</summary>
        public string[]? RegistryPatterns { get; init; }
    }

    private static readonly Target[] CustomTargets =
    [
        // ===== Sistema de Windows =====
        new()
        {
            Id = "sys_temp", Name = "Temp de Windows", Kind = TargetKind.Files,
            Description = "Archivos temporales del sistema (%WINDIR%\\Temp). Se regeneran solos.",
            GetPaths = () => [WinDir("Temp")]
        },
        new()
        {
            Id = "sys_usertemp", Name = "Temporal del usuario", Kind = TargetKind.Files,
            Description = "Archivos temporales de tu sesión (%TEMP%). Se regeneran solos.",
            GetPaths = () => [Path.GetTempPath()]
        },
        new()
        {
            Id = "sys_inetcache", Name = "Internet Explorer (Temporary Internet Files)", Kind = TargetKind.Files,
            Description = "Caché web legada del sistema (INetCache), usada también por aplicaciones del sistema.",
            GetPaths = () => [Path.Combine(LocalAppData, "Microsoft", "Windows", "INetCache")]
        },
        new()
        {
            Id = "sys_crashdumps", Name = "Volcados de aplicaciones (CrashDumps)", Kind = TargetKind.Files,
            Description = "Minidumps de aplicaciones que fallaron. Útiles solo para depurar.",
            GetPaths = () => [Path.Combine(LocalAppData, "CrashDumps")]
        },
        new()
        {
            Id = "sys_wer", Name = "Reportes de errores de Windows (WER)", Kind = TargetKind.Files,
            Description = "Cola de reportes de errores de Windows. Útil solo para depurar.",
            GetPaths = () => [Path.Combine(ProgramData, "Microsoft", "Windows", "WER")]
        },

        new()
        {
            Id = "sys_recyclebin", Name = "Papelera de reciclaje", Kind = TargetKind.Files,
            DefaultChecked = true,
            Description = "Archivos en la papelera de reciclaje de todas las unidades (C:, D:, USB, etc.).",
            GetPaths = () =>
            {
                var paths = new List<string>();
                try
                {
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        if (!drive.IsReady) continue;
                        var rb = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                        if (Directory.Exists(rb)) paths.Add(rb);
                    }
                }
                catch { }
                return paths;
            }
        },

        // ---------- Multimedia ----------
        new()
        {
            Id = "mm_thumbs", Name = "Miniaturas (thumbcache)", Kind = TargetKind.Files,
            Description = "Miniaturas de imágenes y videos del Explorador. Se regeneran al ver las carpetas.",
            Pattern = "thumbcache_*.db",
            GetPaths = () => [Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer")]
        },
        new()
        {
            Id = "mm_iconcache", Name = "Caché de íconos (iconcache)", Kind = TargetKind.Files,
            Description = "Íconos cacheados del Explorador. Se regeneran al usarlos.",
            Pattern = "iconcache_*.db",
            GetPaths = () => [Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer")]
        },
        new()
        {
            Id = "mm_wmp", Name = "Caché de Windows Media Player", Kind = TargetKind.Files,
            Description = "Copias temporales de reproducción (transcodes). Se regeneran al reproducir.",
            GetPaths = () =>
            {
                var wmp = Path.Combine(LocalAppData, "Microsoft", "Media Player");
                return
                [
                    Path.Combine(wmp, "Transcoded Files Cache"),
                    Path.Combine(wmp, "Transcoded Files")
                ];
            }
        },

        // ---------- Utilidades ----------
        new()
        {
            Id = "ut_recent", Name = "Documentos recientes", Kind = TargetKind.Files,
            Description = "Atajos de archivos recientes del menú Inicio (Recent).",
            GetPaths = () => [Path.Combine(AppDataRoaming, "Microsoft", "Windows", "Recent")]
        },
        new()
        {
            Id = "ut_jumplists", Name = "Listas de salto (Jump Lists)", Kind = TargetKind.Files,
            Description = "Recientes del clic derecho sobre íconos de la barra de tareas.",
            GetPaths = () =>
            [
                Path.Combine(AppDataRoaming, "Microsoft", "Windows", "Recent", "AutomaticDestinations"),
                Path.Combine(AppDataRoaming, "Microsoft", "Windows", "Recent", "CustomDestinations")
            ]
        },
        new()
        {
            Id = "ut_runmru", Name = "Autocompletado de Ejecutar (RunMRU)", Kind = TargetKind.RegistryValues,
            Description = "Historial de comandos del diálogo Ejecutar (Win+R).",
            RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
            RegistryPatterns = ["*"]
        },
        new()
        {
            Id = "ut_wordwheel", Name = "Búsquedas recientes (WordWheelQuery)", Kind = TargetKind.RegistryValues,
            Description = "Términos de búsqueda recientes del Explorador y el menú Inicio.",
            RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery",
            RegistryPatterns = ["*"]
        },

        // ---------- Descargas de Windows ----------
        new()
        {
            Id = "dl_wsus", Name = "Actualizaciones de Windows", Kind = TargetKind.Files,
            Description = "Descargas ya instaladas de Windows Update (SoftwareDistribution\\Download).",
            GetPaths = () => [WinDir("SoftwareDistribution", "Download")]
        },
        new()
        {
            Id = "dl_programfiles", Name = "Archivos de programa descargados", Kind = TargetKind.Files,
            Description = "Carpeta legada Downloaded Program Files (ActiveX/Java).",
            GetPaths = () => [WinDir("Downloaded Program Files")]
        },

        // ---------- Avanzado ----------
        new()
        {
            Id = "adv_path", Name = "Ruta de entorno (PATH)", Kind = TargetKind.Analysis,
            DefaultChecked = false, IsAdvanced = true,
            Description = "Diagnóstico: entradas inválidas en la variable PATH (rutas que ya no existen). No se borra automáticamente."
        },
        new()
        {
            Id = "adv_tray", Name = "Caché de notificaciones de la bandeja", Kind = TargetKind.RegistryValues,
            DefaultChecked = false, IsAdvanced = true,
            Description = "Iconos y config de la zona de notificaciones (TrayNotify). Aplica al reiniciar el Explorador.",
            RegistryPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify",
            RegistryPatterns = ["IconStreams", "PastIconsStream"]
        },
        new()
        {
            Id = "adv_prefetch", Name = "Precarga (Prefetch)", Kind = TargetKind.Files,
            DefaultChecked = false, IsAdvanced = true,
            Description = "Datos de precarga de programas. Windows los reconstruye al arrancar.",
            GetPaths = () => [WinDir("Prefetch")]
        },
        new()
        {
            Id = "adv_fontcache", Name = "Caché de fuentes", Kind = TargetKind.Files,
            DefaultChecked = false, IsAdvanced = true,
            Description = "Índice de fuentes del sistema. Se regenera al reiniciar.",
            Pattern = "FontCache*",
            GetPaths = () => [WinDir("ServiceProfiles", "LocalService", "AppData", "Local")]
        },
        new()
        {
            Id = "adv_livekernel", Name = "Informes del kernel en vivo", Kind = TargetKind.Files,
            DefaultChecked = false, IsAdvanced = true,
            Description = "Capturas de kernel para reportar fallos (LiveKernelReports).",
            GetPaths = () => [WinDir("LiveKernelReports")]
        },
        new()
        {
            Id = "adv_memdump", Name = "Volcados de memoria (minidumps)", Kind = TargetKind.Files,
            DefaultChecked = false, IsAdvanced = true,
            Description = "Minidumps de pantallas azules. Ocupan mucho; útiles solo para depurar.",
            GetPaths = () =>
            {
                var list = new List<string> { WinDir("Minidump") };
                var big = WinDir("MEMORY.DMP");
                if (File.Exists(big)) list.Add(big);
                return list;
            }
        },
        new()
        {
            Id = "adv_logs", Name = "Registros de Windows", Kind = TargetKind.Files,
            DefaultChecked = false, IsAdvanced = true,
            Description = "Archivos .log de la carpeta de registros del sistema (WINDIR\\Logs).",
            Pattern = "*.log",
            GetPaths = () => [WinDir("Logs")]
        }
    ];

    private static readonly CleanupCategoryInfo[] CustomCategories =
    [
        new("sistema", "Sistema de Windows",
            "Archivos temporales y de error del sistema operativo.",
        CustomTargets.Where(t => t.Id.StartsWith("sys_", StringComparison.Ordinal)).Select(ToInfo).ToList()),
        new("multimedia", "Multimedia",
            "Miniaturas, caché de íconos y transcodes de reproducción.",
            CustomTargets.Where(t => t.Id.StartsWith("mm_", StringComparison.Ordinal)).Select(ToInfo).ToList()),
        new("utilidades", "Utilidades",
            "Recientes de la sesión y autocompletados del historial (Windows).",
            CustomTargets.Where(t => t.Id.StartsWith("ut_", StringComparison.Ordinal)).Select(ToInfo).ToList()),
        new("descargas", "Descargas de Windows",
            "Descargas de actualizaciones y componentes ya instalados.",
            CustomTargets.Where(t => t.Id.StartsWith("dl_", StringComparison.Ordinal)).Select(ToInfo).ToList()),
        new("avanzado", "Avanzado",
            "Limpieza de bajo nivel, recomendada solo para usuarios que saben lo que borran.",
            CustomTargets.Where(t => t.Id.StartsWith("adv_", StringComparison.Ordinal)).Select(ToInfo).ToList())
    ];

    private static CleanupTargetInfo ToInfo(Target t) => new(t.Id, t.Name, t.Description, t.DefaultChecked, t.Kind == TargetKind.Analysis, t.IsAdvanced);

    // =====================================================================
    // Navegadores (pestaña Chequeo)
    // =====================================================================

    private sealed class BrowserDef
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required string ProcessName { get; init; }
        public required string Accent { get; init; }

        /// <summary>Rutas de perfiles (se resuelven si existen): Chromium → User Data\*, Firefox → Profiles\*.</summary>
        public required Func<List<string>> Profiles { get; init; }

        /// <summary>Rutas de caché por perfil.</summary>
        public required Func<string, List<string>> CachePaths { get; init; }

        /// <summary>Rutas candidatas del EXE: la primera que existe se usa para extraer el ícono real.</summary>
        public required Func<string?> ExePath { get; init; }

        public required string[] CookieFiles { get; init; }
        public required string[] HistoryFiles { get; init; }
        public bool IsMozilla { get; init; }
    }

    private static readonly BrowserDef[] Browsers =
    [
        new()
        {
            Id = "chrome", DisplayName = "Google Chrome", ProcessName = "chrome", Accent = "#4285F4",
            Profiles = () => ChromiumProfiles(Path.Combine(LocalAppData, "Google", "Chrome", "User Data")),
            CachePaths = ChromiumCachePaths,
            ExePath = () => FindFirstExe(
                Path.Combine(ProgFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(ProgFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(LocalAppData, "Google", "Chrome", "Application", "chrome.exe")),
            CookieFiles = ["Network\\Cookies", "Cookies"],
            HistoryFiles = ["History"]
        },
        new()
        {
            Id = "edge", DisplayName = "Microsoft Edge", ProcessName = "msedge", Accent = "#0078D7",
            Profiles = () => ChromiumProfiles(Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data")),
            CachePaths = ChromiumCachePaths,
            ExePath = () => FindFirstExe(
                Path.Combine(ProgFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(ProgFiles, "Microsoft", "Edge", "Application", "msedge.exe")),
            CookieFiles = ["Network\\Cookies", "Cookies"],
            HistoryFiles = ["History"]
        },
        new()
        {
            Id = "brave", DisplayName = "Brave", ProcessName = "brave", Accent = "#FB542B",
            Profiles = () => ChromiumProfiles(Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data")),
            CachePaths = ChromiumCachePaths,
            ExePath = () => FindFirstExe(
                Path.Combine(ProgFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")),
            CookieFiles = ["Network\\Cookies", "Cookies"],
            HistoryFiles = ["History"]
        },
        new()
        {
            Id = "opera", DisplayName = "Opera", ProcessName = "opera", Accent = "#FF1B2D",
            Profiles = () =>
            {
                var list = new List<string>();
                var stable = Path.Combine(AppDataRoaming, "Opera Software", "Opera Stable");
                var gx = Path.Combine(AppDataRoaming, "Opera Software", "Opera GX Stable");
                if (Directory.Exists(stable)) list.Add(stable);
                if (Directory.Exists(gx)) list.Add(gx);
                return list;
            },
            ExePath = () => FindFirstExe(
                Path.Combine(LocalAppData, "Programs", "Opera", "opera.exe"),
                Path.Combine(ProgFiles, "Opera", "opera.exe")),
            CachePaths = p =>
            [
                Path.Combine(p, "Cache"),
                Path.Combine(p, "Code Cache"),
                Path.Combine(p, "GPUCache"),
                Path.Combine(p, "Media Cache")
            ],
            CookieFiles = ["Network\\Cookies", "Cookies"],
            HistoryFiles = ["History"]
        },
        new()
        {
            Id = "firefox", DisplayName = "Mozilla Firefox", ProcessName = "firefox", Accent = "#FF7139", IsMozilla = true,
            Profiles = () =>
            {
                var list = new List<string>();
                AddProfiles(list, Path.Combine(AppDataRoaming, "Mozilla", "Firefox", "Profiles"));
                AddProfiles(list, Path.Combine(LocalAppData, "Mozilla", "Firefox", "Profiles"));
                return list;
            },
            ExePath = () => FindFirstExe(
                Path.Combine(ProgFiles, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(ProgFilesX86, "Mozilla Firefox", "firefox.exe")),
            CachePaths = p =>
            [
                Path.Combine(p, "cache2"),
                Path.Combine(p, "startupCache"),
                Path.Combine(p, "cache")
            ],
            CookieFiles = ["cookies.sqlite"],
            HistoryFiles = ["places.sqlite"]
        }
    ];

    /// <summary>
    /// Devuelve la primera ruta que existe. Útil para buscar el EXE de un navegador
    /// entre varios lugares de instalación posibles (Program Files, x86, LocalAppData).
    /// </summary>
    private static string? FindFirstExe(params string[] candidates)
    {
        foreach (var path in candidates)
            if (File.Exists(path))
                return path;
        return null;
    }

    private static void AddProfiles(List<string> list, string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            // Solo perfiles con datos reales de navegación (excluye carpetas de respaldo).
            if (HasBrowserData(dir))
                list.Add(dir);
        }
    }

    private static List<string> ChromiumProfiles(string userData)
    {
        var list = new List<string>();
        if (!Directory.Exists(userData)) return list;
        foreach (var dir in Directory.EnumerateDirectories(userData))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".", StringComparison.Ordinal) ||
                name.Equals("System Profile", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Crashes", StringComparison.OrdinalIgnoreCase))
                continue;
            if (HasBrowserData(dir))
                list.Add(dir);
        }
        return list;
    }

    private static List<string> ChromiumCachePaths(string profile) =>
    [
        Path.Combine(profile, "Cache"),
        Path.Combine(profile, "Code Cache"),
        Path.Combine(profile, "GPUCache"),
        Path.Combine(profile, "Media Cache"),
        Path.Combine(profile, "Service Worker", "CacheStorage"),
        Path.Combine(profile, "Service Worker", "ScriptCache")
    ];

    private static bool HasBrowserData(string profileDir)
    {
        try
        {
            return Directory.Exists(Path.Combine(profileDir, "Cache")) ||
                   Directory.Exists(Path.Combine(profileDir, "cache2")) ||
                   File.Exists(Path.Combine(profileDir, "Cookies")) ||
                   File.Exists(Path.Combine(profileDir, "cookies.sqlite")) ||
                   File.Exists(Path.Combine(profileDir, "History"));
        }
        catch
        {
            return false;
        }
    }

    // =====================================================================
    // ICleanupService
    // =====================================================================

    public IReadOnlyList<CleanupCategoryInfo> GetCustomCategories() => CustomCategories;

    public IReadOnlyList<BrowserCleanupInfo> GetBrowsers()
    {
        var result = new List<BrowserCleanupInfo>(Browsers.Length);
        foreach (var b in Browsers)
        {
            List<string> profiles;
            try { profiles = b.Profiles(); }
            catch { profiles = []; }
            string? exePath = null;
            try { exePath = b.ExePath(); } catch { }

            // Está instalado si: (a) encontró perfiles con datos reales, o (b) el
            // EXE existe en alguna ruta de instalación. Con solo la carpeta User Data
            // no alcanza: después de un debloat queda vacía y no queremos mostrar el
            // navegador.
            bool hasExe = !string.IsNullOrEmpty(exePath) && File.Exists(exePath);
            bool installed = profiles.Count > 0 || hasExe;

            bool running;
            try { running = Process.GetProcessesByName(b.ProcessName).Length > 0; }
            catch { running = false; }
            result.Add(new BrowserCleanupInfo(b.Id, b.DisplayName, b.ProcessName, b.Accent, exePath ?? "", installed, running, profiles));
        }
        return result;
    }

    public Task<CleanupScanResult> ScanCustomAsync(
        IReadOnlyCollection<string> targetIds, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => ScanCustomSync(targetIds, progress, ct), ct);

    public Task<CleanupScanResult> ScanBrowserAsync(
        string browserId, IReadOnlyCollection<BrowserSubItem> items, CancellationToken ct = default)
        => Task.Run(() => ScanBrowserSync(browserId, items, ct), ct);

    public Task<CleanupCleanResult> CleanCustomAsync(
        IReadOnlyCollection<string> targetIds, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => CleanCustomSync(targetIds, progress, ct), ct);

    public Task<CleanupCleanResult> CleanBrowserAsync(
        string browserId, IReadOnlyCollection<BrowserSubItem> items, bool closeIfRunning, CancellationToken ct = default)
        => Task.Run(() => CleanBrowserSync(browserId, items, closeIfRunning, ct), ct);

    // =====================================================================
    // Análisis
    // =====================================================================

    private CleanupScanResult ScanCustomSync(IReadOnlyCollection<string> ids, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<CleanupItemResult>(ids.Count);
        var warnings = new List<string>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var target = CustomTargets.FirstOrDefault(t => t.Id == id);
            if (target == null) continue;
            progress?.Report(target.Name);
            results.Add(ScanTarget(target, warnings, ct));
        }
        return new CleanupScanResult(results, results.Sum(r => r.Bytes), warnings);
    }

    private CleanupItemResult ScanTarget(Target t, List<string> warnings, CancellationToken ct)
    {
        switch (t.Kind)
        {
            case TargetKind.Analysis:
            {
                return new CleanupItemResult(t.Id, t.Name, 0, AnalyzePathEntries().Count, AnalysisOnly: true);
            }
            case TargetKind.RegistryValues:
            {
                int count = CountRegistryValues(t);
                return new CleanupItemResult(t.Id, t.Name, 0, count, AnalysisOnly: false);
            }
            default:
            {
                long bytes = 0; int files = 0;
                var paths = ExpandPaths(t.GetPaths);
                foreach (var path in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    ScanPath(path, t.Pattern, ref bytes, ref files, warnings);
                }
                return new CleanupItemResult(t.Id, t.Name, bytes, files, AnalysisOnly: false);
            }
        }
    }

    private static void ScanPath(string path, string? pattern, ref long bytes, ref int files, List<string> warnings)
    {
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                bytes += fi.Length; files++;
                return;
            }
            if (!Directory.Exists(path)) return;
            foreach (var f in Directory.EnumerateFiles(path, pattern ?? "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; files++; }
                catch { /* archivo en uso: solo se omite del total */ }
            }
        }
        catch (UnauthorizedAccessException)
        {
            warnings.Add($"Sin permisos para leer: {path}");
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException)
        {
            // Carpeta desapareció a mitad del análisis: se omite.
        }
    }

    private static List<string> AnalyzePathEntries()
    {
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            string? path;
            try { path = Environment.GetEnvironmentVariable("PATH", scope); }
            catch { continue; }
            if (string.IsNullOrWhiteSpace(path)) continue;
            foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = raw.Trim().Trim('"');
                if (entry.Length == 0) continue;
                if (entry.StartsWith("%", StringComparison.Ordinal))
                {
                    // Expandir variables %VAR% para validar la ruta real.
                    entry = Environment.ExpandEnvironmentVariables(entry);
                }
                if (seen.Add(entry) && !Directory.Exists(entry))
                    invalid.Add(raw.Trim());
            }
        }
        return invalid;
    }

    private static int CountRegistryValues(Target t)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(t.RegistryPath!);
            if (key == null) return 0;
            return key.GetValueNames().Count(n => MatchesAny(n, t.RegistryPatterns!));
        }
        catch { return 0; }
    }

    private CleanupScanResult ScanBrowserSync(string browserId, IReadOnlyCollection<BrowserSubItem> items, CancellationToken ct)
    {
        var browser = Browsers.FirstOrDefault(b => b.Id == browserId);
        if (browser == null) return new CleanupScanResult([], 0, []);
        var results = new List<CleanupItemResult>();
        var warnings = new List<string>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            long bytes = 0; int files = 0;
            foreach (var profile in SafeProfiles(browser))
            {
                foreach (var path in BrowserSubPaths(browser, item, profile))
                    ScanPath(path, null, ref bytes, ref files, warnings);
            }
            results.Add(new CleanupItemResult(BrowserItemId(browser.Id, item), BrowserItemName(item), bytes, files, false));
        }
        return new CleanupScanResult(results, results.Sum(r => r.Bytes), warnings);
    }

    // =====================================================================
    // Limpieza
    // =====================================================================

    private CleanupCleanResult CleanCustomSync(IReadOnlyCollection<string> ids, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<CleanupItemResult>(ids.Count);
        var warnings = new List<string>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var target = CustomTargets.FirstOrDefault(t => t.Id == id);
            if (target == null) continue;
            progress?.Report(target.Name);
            switch (target.Kind)
            {
                case TargetKind.Analysis:
                    results.Add(new CleanupItemResult(target.Id, target.Name, 0, 0, true,
                        "Esta entrada es solo de análisis: no se borra automáticamente."));
                    break;
                case TargetKind.RegistryValues:
                {
                    int count = DeleteRegistryValues(target, warnings);
                    results.Add(new CleanupItemResult(target.Id, target.Name, 0, count, false));
                    break;
                }
                default:
                {
                    long freed = 0; int deleted = 0;
                    foreach (var path in ExpandPaths(target.GetPaths))
                        DeletePath(path, target.Pattern, ref freed, ref deleted, warnings, ct);
                    results.Add(new CleanupItemResult(target.Id, target.Name, freed, deleted, false));
                    break;
                }
            }
        }
        return new CleanupCleanResult(results, results.Sum(r => r.Bytes), warnings);
    }

    private CleanupCleanResult CleanBrowserSync(string browserId, IReadOnlyCollection<BrowserSubItem> items, bool closeIfRunning, CancellationToken ct)
    {
        var browser = Browsers.FirstOrDefault(b => b.Id == browserId);
        if (browser == null) return new CleanupCleanResult([], 0, []);
        var warnings = new List<string>();

        bool running = false;
        try { running = Process.GetProcessesByName(browser.ProcessName).Length > 0; }
        catch { }

        if (running && !closeIfRunning)
        {
            warnings.Add($"{browser.DisplayName} está abierto. No se limpió nada: cerralo o activá la opción de cerrarlo.");
            var skipped = items.Select(i => new CleanupItemResult(BrowserItemId(browser.Id, i), BrowserItemName(i), 0, 0, false)).ToList();
            return new CleanupCleanResult(skipped, 0, warnings);
        }

        if (running)
            CloseProcesses(browser.ProcessName, warnings);

        var results = new List<CleanupItemResult>();
        var profiles = SafeProfiles(browser);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            long freed = 0; int deleted = 0;
            foreach (var profile in profiles)
            {
                foreach (var path in BrowserSubPaths(browser, item, profile))
                    DeletePath(path, null, ref freed, ref deleted, warnings, ct);
            }
            results.Add(new CleanupItemResult(BrowserItemId(browser.Id, item), BrowserItemName(item), freed, deleted, false));
        }
        return new CleanupCleanResult(results, results.Sum(r => r.Bytes), warnings);
    }

    private static void CloseProcesses(string processName, List<string> warnings)
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName(processName); }
        catch { return; }
        foreach (var p in procs)
        {
            try
            {
                if (!p.CloseMainWindow())
                    p.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // El proceso terminó solo entre la consulta y el cierre: ok.
            }
        }
        try
        {
            foreach (var p in procs) p.Dispose();
        }
        catch { }
    }

    private static int DeleteRegistryValues(Target t, List<string> warnings)
    {
        int deleted = 0;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(t.RegistryPath!, writable: true);
            if (key == null) return 0;
            foreach (var name in key.GetValueNames())
            {
                if (MatchesAny(name, t.RegistryPatterns!))
                {
                    try { key.DeleteValue(name, throwOnMissingValue: false); deleted++; }
                    catch (Exception ex) { warnings.Add($"No se pudo borrar {name}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"No se pudo abrir la clave {t.RegistryPath}: {ex.Message}");
        }
        return deleted;
    }

    private static bool MatchesAny(string name, string[] patterns)
        => patterns.Any(p => p == "*" || string.Equals(p, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Borra archivos de una carpeta (o un archivo puntual). Conserva la raíz.
    /// Los archivos bloqueados se omiten y se cuentan como advertencia.
    /// </summary>
    private static void DeletePath(string path, string? pattern, ref long freed, ref int deleted, List<string> warnings, CancellationToken ct)
    {
        try
        {
            if (File.Exists(path))
            {
                TryDeleteFile(path, ref freed, ref deleted, warnings);
                return;
            }
            if (!Directory.Exists(path)) return;

            if (pattern != null)
            {
                foreach (var f in Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    TryDeleteFile(f, ref freed, ref deleted, warnings);
                }
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(entry))
                    {
                        long before = GetDirSize(entry);
                        Directory.Delete(entry, recursive: true);
                        freed += before; deleted++;
                    }
                    else
                    {
                        TryDeleteFile(entry, ref freed, ref deleted, warnings);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"No se pudo borrar: {entry}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            warnings.Add($"No se pudo limpiar: {path}");
        }
    }

    private static void TryDeleteFile(string file, ref long freed, ref int deleted, List<string> warnings)
    {
        try
        {
            long size = new FileInfo(file).Length;
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            File.Delete(file);
            freed += size; deleted++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (deleted < 50) warnings.Add($"En uso, no se pudo borrar: {file}");
            else warnings.Add("Algunos archivos quedaron en uso y no se borraron.");
        }
    }

    private static long GetDirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; }
                catch { }
            }
        }
        catch { }
        return total;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static string WinDir(params string[] rest)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return rest.Length == 0 ? root : Path.Combine([root, .. rest]);
    }
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string AppDataRoaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string ProgFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string ProgFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    /// <summary>
    /// Resuelve las rutas declaradas por un target (Files) con tolerancia a fallas:
    /// un target nunca puede tirar el análisis completo por un path inválido.
    /// </summary>
    private static List<string> ExpandPaths(Func<IEnumerable<string>>? getter)
    {
        if (getter == null) return [];
        try { return getter().ToList(); }
        catch { return []; }
    }

    private static List<string> SafeProfiles(BrowserDef b)
    {
        try { return b.Profiles().Where(Directory.Exists).ToList(); }
        catch { return []; }
    }

    private static List<string> BrowserSubPaths(BrowserDef b, BrowserSubItem item, string profile)
    {
        switch (item)
        {
            case BrowserSubItem.Cache:
                try { return b.CachePaths(profile); }
                catch { return new List<string>(); }
            case BrowserSubItem.Cookies:
                return b.CookieFiles.Select(f => Path.Combine(profile, f)).ToList();
            default:
                return b.HistoryFiles.Select(f => Path.Combine(profile, f)).ToList();
        }
    }

    private static string BrowserItemId(string browserId, BrowserSubItem item)
        => $"{browserId}.{item.ToString().ToLowerInvariant()}";

    private static string BrowserItemName(BrowserSubItem item) => item switch
    {
        BrowserSubItem.Cache => "Archivos temporales de internet",
        BrowserSubItem.Cookies => "Cookies",
        _ => "Historial de navegación"
    };
}