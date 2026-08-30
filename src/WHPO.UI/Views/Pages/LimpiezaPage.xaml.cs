using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using WHPO.Core.Services.Interfaces;
using WinForms = System.Windows.Forms;
using WinBrush = Microsoft.UI.Xaml.Media.Brush;
using WinColor = Windows.UI.Color;
using WinImage = Microsoft.UI.Xaml.Controls.Image;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Limpieza del dispositivo (estilo CCleaner):
///  - Chequeo: navegadores instalados con checks de caché / cookies / historial.
///    Si un navegador está abierto, hay que marcar "cerrarlo" para poder limpiarlo.
///  - Limpieza personalizada: categorías (Sistema, Multimedia, Utilidades,
///    Descargas de Windows, Avanzado) con cada elemento marcable.
/// </summary>
public sealed partial class LimpiezaPage : Page
{
    private readonly ICleanupService _cleanup;
    private readonly IDuplicateFinderService _dupFinder;
    private readonly ISettingsService? _settings;
    private readonly IStartupManagerService _startupMgr;
    private readonly ILoggingService _logging;
    private readonly IDriveWatcherService? _driveWatcher;

    private sealed class ChequeoUi
    {
        public required string Id { get; init; }
        public required CheckBox Check { get; set; }
        public required TextBlock Size { get; set; }
        public string? BrowserId { get; init; }
        public BrowserSubItem? SubItem { get; init; }
    }

    private sealed class CustomUi
    {
        public required CleanupTargetInfo Target { get; init; }
        public required CheckBox Check { get; init; }
        public required TextBlock Size { get; init; }
    }

    private readonly List<ChequeoUi> _chequeoItems = new();
    private readonly List<ChequeoUi> _chequeoApps = new();  // fase 2: procesos con ventana
    private readonly List<ChequeoUi> _chequeoStartup = new(); // fase 3: apps de inicio
    private int _chequeoPhase;         // 0=limpieza, 1=apps, 2=inicio
    private long _chequeoTotalBytes;   // total de fase 1, para habilitar botón Limpiar
    /// <summary>Timer que refresca en vivo la RAM de las apps en segundo plano (fase 2).</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _chequeoAppsTimer;
    /// <summary>Nombres de cada fase del Chequeo (claves de traducción).</summary>
    private static readonly string[] ChequeoPhaseNames =
    {
        "Limpieza de archivos", "Aplicaciones en segundo plano", "Aplicaciones de inicio"
    };
    private readonly List<Border> _chequeoPhaseDots = new();
    private readonly List<Border> _chequeoPhaseLines = new();
    private readonly List<CustomUi> _custom = new();
    /// <summary>Texto del total de cada categoría (``category.Id`` → TextBlock del header).</summary>
    private readonly Dictionary<string, TextBlock> _customCategoryTotals = new();
    /// <summary>Mapa target.Id → category.Id para acumular totales por categoría.</summary>
    private readonly Dictionary<string, string> _customTargetCategory = new();

    /// <summary>Opciones agrupadas del panel de inicio: categoría.Id → CheckBox.</summary>
    private readonly Dictionary<string, CheckBox> _customGroupChecks = new();

    /// <summary>Grupos (categorías) que están activos tras el último análisis.</summary>
    private HashSet<string> _customSelection = new(StringComparer.Ordinal);

    private CancellationTokenSource? _chequeoCts;
    private CancellationTokenSource? _customScanCts;
    private bool _customScanned;

    // ---- Íconos reales de navegadores: extraídos de sus .exe con IconExtractor ----
    private static readonly string BrowserIconDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WHPO", "browsericons");
    /// <summary>PNG ya extraído y listo (BitmapImage). La clave es el browserId.</summary>
    private static readonly Dictionary<string, BitmapImage> BrowserIconReady = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"true" = ya se intentó extraer y falló: no reintentar.</summary>
    private static readonly HashSet<string> BrowserIconFailed = new(StringComparer.OrdinalIgnoreCase);

    // ---- Íconos reales de apps en segundo plano: extraídos del .exe del proceso,
    //      cacheados por ruta del ejecutable (varias apps comparten exe) ----
    private static readonly Dictionary<string, BitmapImage> AppIconReady = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AppIconFailed = new(StringComparer.OrdinalIgnoreCase);

    // ---- Íconos reales de entradas de inicio: extraídos de sus .exe con IconExtractor ----
    private static readonly string StartupIconDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WHPO", "startupicons");
    /// <summary>PNG ya extraído y listo (BitmapImage). La clave es el Id de la entrada.</summary>
    private static readonly Dictionary<string, BitmapImage> StartupIconReady = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"true" = ya se intentó extraer y falló: no reintentar.</summary>
    private static readonly HashSet<string> StartupIconFailed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pone en el Border indicado el ícono del navegador. Primero muestra un
    /// FontIcon de respaldo; después extrae el ícono REAL del .exe en background
    /// y lo reemplaza vía DispatcherQueue. Mismo patrón que GestionarProcesosPage.
    /// </summary>
    private static void EnsureBrowserIcon(string browserId, string processName, Border badgeHost)
    {
        // ¿Ya tenemos el PNG?
        lock (BrowserIconReady)
        {
            if (BrowserIconReady.TryGetValue(browserId, out var bi))
            {
                badgeHost.Child = new WinImage { Source = bi, Width = 24, Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                return;
            }
        }

        var pngPath = Path.Combine(BrowserIconDir, browserId + ".png");
        if (File.Exists(pngPath))
        {
            try
            {
                var bi = new BitmapImage(new Uri(pngPath, UriKind.Absolute));
                lock (BrowserIconReady) BrowserIconReady[browserId] = bi;
                badgeHost.Child = new WinImage { Source = bi, Width = 24, Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                return;
            }
            catch { File.Delete(pngPath); }
        }

        // Fallback: FontIcon (globo) con el color de acento de la app (sin fondo
        // de color ahora, así se usa el acento para que sea visible).
        badgeHost.Child = new FontIcon
        {
            Glyph = "\uE774", FontSize = 18,
            Foreground = ThemeBrushes.Get("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };

        // ¿Ya probamos y falló?
        lock (BrowserIconFailed) { if (BrowserIconFailed.Contains(browserId)) return; }

        // Extraer en background (como GestionarProcesosPage).
        _ = Task.Run(() =>
        {
            try
            {
                // Buscar el EXE: primero por el proceso corriendo, después por rutas fijas.
                string? exePath = null;
                try
                {
                    var procs = Process.GetProcessesByName(processName);
                    foreach (var p in procs)
                    {
                        try { exePath = p.MainModule?.FileName; if (!string.IsNullOrEmpty(exePath)) break; }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    lock (BrowserIconFailed) { BrowserIconFailed.Add(browserId); }
                    return;
                }

                using var big = IconExtractor.ExtractHighResIcon(exePath);
                if (big == null)
                {
                    lock (BrowserIconFailed) { BrowserIconFailed.Add(browserId); }
                    return;
                }

                using var small = new Bitmap(28, 28);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(big, 0, 0, 28, 28);
                }
                Directory.CreateDirectory(BrowserIconDir);
                var tmp = pngPath + ".tmp";
                small.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                File.Move(tmp, pngPath, overwrite: true);

                // Reemplazar en la UI.
                _ = badgeHost.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var bi = new BitmapImage(new Uri(pngPath, UriKind.Absolute));
                        lock (BrowserIconReady) BrowserIconReady[browserId] = bi;
                        badgeHost.Child = new WinImage { Source = bi, Width = 24, Height = 24,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    }
                    catch { }
                });
            }
            catch
            {
                lock (BrowserIconFailed) { BrowserIconFailed.Add(browserId); }
            }
        });
    }

    /// <summary>
    /// Nombre de archivo de caché SEGURO para un Id de entrada: los Ids
    /// contienen caracteres inválidos para rutas de Windows ("reg|HKCU\...|OneDrive"),
    /// que rompían el guardado/lectura del PNG y el ícono nunca aparecía.
    /// Se reemplazan por '_' y se limita el largo (255 es el tope de Windows).
    /// </summary>
    private static string StartupIconCacheKey(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(c is '\\' or '/' or ':' or '|' || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString();
        if (s.Length > 200)
        {
            // Hash determinista del Id completo para no perder unicidad.
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id)));
            s = s[..150] + "_" + hash[..10];
        }
        return s;
    }

    /// <summary>
    /// Pone en el Border indicado el ícono de la entrada de inicio.
    /// Primero muestra un FontIcon de respaldo (siempre visible); después
    /// extrae el ícono REAL del .exe/acceso directo en background y lo
    /// reemplaza vía DispatcherQueue.
    /// </summary>
    /// <summary>
    /// Las apps empaquetadas (MSIX/UWP) no tienen .ex que extraer: su ícono es el
    /// logo del paquete (Assets\Square44x44Logo*.png en su carpeta de instalación).
    ///
    /// Mismo pipeline que los íconos de registro (que sí funcionan): en background
    /// se COPIA el logo a la caché local y el BitmapImage se crea DENTRO del
    /// DispatcherQueue (hilo UI) desde la copia — no desde la ruta de WindowsApps,
    /// que fallaba silenciosamente al crear el BitmapImage en un hilo de fondo.
    /// </summary>
    private void EnsurePackagedAppIcon(StartupEntry entry, Border badgeHost)
    {
        string cacheKey = entry.Id;

        // ¿Ya lo tenemos en memoria?
        lock (StartupIconReady)
        {
            if (StartupIconReady.TryGetValue(cacheKey, out var bi))
            {
                badgeHost.Child = new WinImage
                {
                    Source = bi, Width = 24, Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                return;
            }
        }

        // ¿Ya está cacheado en disco?
        var pngPath = Path.Combine(StartupIconDir, StartupIconCacheKey(cacheKey) + ".png");
        if (File.Exists(pngPath))
        {
            try
            {
                var bi = new BitmapImage(new Uri(pngPath, UriKind.Absolute));
                lock (StartupIconReady) StartupIconReady[cacheKey] = bi;
                badgeHost.Child = new WinImage
                {
                    Source = bi, Width = 24, Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                return;
            }
            catch { }
        }

        // Fallback inmediato (mismo FontIcon que el resto).
        badgeHost.Child = new FontIcon
        {
            Glyph = "\uE7B5", FontSize = 18,
            Foreground = ThemeBrushes.Get("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };

        lock (StartupIconFailed) { if (StartupIconFailed.Contains(cacheKey)) return; }

        _ = Task.Run(() =>
        {
            try
            {
                var logo = FindPackageLogo(entry.Command);
                if (logo == null)
                {
                    _logging.LogWarning($"[StartupIcon] Paquete {entry.Name}: no se encontró logo en {entry.Command}");
                    lock (StartupIconFailed) { StartupIconFailed.Add(cacheKey); }
                    return;
                }

                // Copiar a la caché local (misma carpeta que los íconos de registro).
                Directory.CreateDirectory(StartupIconDir);
                var tmp = pngPath + ".tmp";
                File.Copy(logo, tmp, overwrite: true);
                File.Move(tmp, pngPath, overwrite: true);
                _logging.LogDebug($"[StartupIcon] Paquete {entry.Name}: logo {logo} cacheado en {pngPath}");

                // Crear el BitmapImage EN EL HILO UI (patrón que sí funciona).
                _ = badgeHost.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var bi = new BitmapImage(new Uri(pngPath, UriKind.Absolute));
                        lock (StartupIconReady) StartupIconReady[cacheKey] = bi;
                        badgeHost.Child = new WinImage
                        {
                            Source = bi, Width = 24, Height = 24,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                        };
                        _logging.LogDebug($"[StartupIcon] Paquete {entry.Name}: UI actualizada");
                    }
                    catch (Exception ex)
                    {
                        _logging.LogError($"[StartupIcon] Paquete {entry.Name}: error al mostrar: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logging.LogError($"[StartupIcon] Paquete {entry.Name}: {ex.Message}");
                lock (StartupIconFailed) { StartupIconFailed.Add(cacheKey); }
            }
        });
    }

    /// <summary>
    /// Busca el logo del paquete (Square44x44Logo en distintas escalas/dirs).
    /// </summary>
    private static string? FindPackageLogo(string packageDir)
    {
        if (string.IsNullOrWhiteSpace(packageDir) || !Directory.Exists(packageDir)) return null;
        string[] subDirs = { "Assets", "Images", "" };
        string[] names =
        {
            "Square44x44Logo.scale-400.png", "Square44x44Logo.scale-200.png",
            "Square44x44Logo.scale-100.png", "Square44x44Logo.png",
            "StoreLogo.scale-400.png", "StoreLogo.scale-100.png", "StoreLogo.png"
        };
        foreach (var sub in subDirs)
        {
            var basePath = sub.Length == 0 ? packageDir : Path.Combine(packageDir, sub);
            if (!Directory.Exists(basePath)) continue;
            foreach (var n in names)
            {
                var f = Path.Combine(basePath, n);
                if (File.Exists(f)) return f;
            }
        }
        return null;
    }

    private void EnsureStartupIcon(StartupEntry entry, Border badgeHost)
    {
        string cacheKey = entry.Id;

        // Las empaquetadas usan el logo del paquete, no extracción de exe.
        if (entry.Source == StartupSource.PackagedApp)
        {
            EnsurePackagedAppIcon(entry, badgeHost);
            return;
        }

        // ¿Ya tenemos el PNG?
        lock (StartupIconReady)
        {
            if (StartupIconReady.TryGetValue(cacheKey, out var bi))
            {
                badgeHost.Child = new WinImage
                {
                    Source = bi,
                    Width = 24, Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                return;
            }
        }

        var pngPath = Path.Combine(StartupIconDir, StartupIconCacheKey(cacheKey) + ".png");
        if (File.Exists(pngPath))
        {
            try
            {
                var bi = new BitmapImage(new Uri(pngPath, UriKind.Absolute));
                lock (StartupIconReady) StartupIconReady[cacheKey] = bi;
                badgeHost.Child = new WinImage
                {
                    Source = bi,
                    Width = 24, Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                return;
            }
            catch { File.Delete(pngPath); }
        }

        // Fallback inmediato: FontIcon de "app" con el acento de la app, para que
        // el badge NUNCA quede vacío (mismo patrón que EnsureBrowserIcon).
        badgeHost.Child = new FontIcon
        {
            Glyph = "\uE7B5", // AppIconDefault
            FontSize = 18,
            Foreground = ThemeBrushes.Get("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Mejor fallback si se puede: ícono genérico de .exe del SISTEMA (el mismo
        // que usa el Explorador para ejecutables sin recurso de ícono propio).
        try
        {
            var fallbackBmp = IconExtractor.ExtractDefaultExeIcon();
            if (fallbackBmp != null)
            {
                badgeHost.Child = new WinImage { Source = ToBitmapImageSync(fallbackBmp), Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                fallbackBmp.Dispose();
            }
        }
        catch { }

        // ¿Ya probamos y falló?
        lock (StartupIconFailed) { if (StartupIconFailed.Contains(cacheKey)) return; }

        // Extraer en background con logging detallado para diagnóstico.
        _ = Task.Run(async () =>
        {
            try
            {
                _logging.LogDebug($"[StartupIcon] Iniciando extracción para: {entry.Name} (Source={entry.Source}, Command={entry.Command})");
                
                string? exePath = null;

                // Resolver el exe según la fuente de la entrada.
                if (entry.Source == StartupSource.StartupFolder)
                {
                    // Es un .lnk: resolver el destino.
                    _logging.LogDebug($"[StartupIcon] Resolviendo shortcut: {entry.Command}");
                    try { exePath = ResolveShortcutTarget(entry.Command); } catch (Exception ex) { _logging.LogWarning($"[StartupIcon] Error resolviendo shortcut: {ex.Message}"); }
                    _logging.LogDebug($"[StartupIcon] Shortcut resuelto a: {exePath}");
                }
                else
                {
                    // Registry: Command suele ser la ruta del exe (a veces con args).
                    _logging.LogDebug($"[StartupIcon] Extrayendo exe path de comando: {entry.Command}");
                    exePath = ExtractExePath(entry.Command);
                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    {
                        var expanded = Environment.ExpandEnvironmentVariables(exePath ?? string.Empty);
                        if (!string.IsNullOrEmpty(expanded) && File.Exists(expanded)) exePath = expanded;
                    }
                    _logging.LogDebug($"[StartupIcon] Exe path extraído: {exePath}");
                }

                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    _logging.LogWarning($"[StartupIcon] Exe no encontrado o ruta vacía: '{exePath}' para {entry.Name}");
                    lock (StartupIconFailed) { StartupIconFailed.Add(cacheKey); }
                    return;
                }

                _logging.LogDebug($"[StartupIcon] Extrayendo ícono de alta resolución: {exePath}");
                using var big = IconExtractor.ExtractHighResIcon(exePath);
                if (big == null)
                {
                    _logging.LogWarning($"[StartupIcon] ExtractHighResIcon devolvió null para: {exePath}");
                    lock (StartupIconFailed) { StartupIconFailed.Add(cacheKey); }
                    return;
                }

                _logging.LogDebug($"[StartupIcon] Ícono extraído OK ({big.Width}x{big.Height}), escalando a 28x28");
                using var small = new Bitmap(28, 28);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(big, 0, 0, 28, 28);
                }
                Directory.CreateDirectory(StartupIconDir);
                var tmp = pngPath + ".tmp";
                small.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                File.Move(tmp, pngPath, overwrite: true);

                _logging.LogDebug($"[StartupIcon] PNG guardado en: {pngPath}");

                // Reemplazar en la UI.
                _ = badgeHost.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var bi = new BitmapImage(new Uri(pngPath, UriKind.Absolute));
                        lock (StartupIconReady) StartupIconReady[cacheKey] = bi;
                        badgeHost.Child = new WinImage
                        {
                            Source = bi,
                            Width = 24, Height = 24,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        _logging.LogDebug($"[StartupIcon] UI actualizada para: {entry.Name}");
                    }
                    catch (Exception ex)
                    {
                        _logging.LogError($"[StartupIcon] Error actualizando UI: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logging.LogError($"[StartupIcon] Excepción general para {entry.Name}: {ex.Message}");
                lock (StartupIconFailed) { StartupIconFailed.Add(cacheKey); }
            }
        });
    }

    /// <summary>
    /// Extrae la ruta del ejecutable de una línea de comandos.
    /// Muchas entradas del registro NO ponen comillas aunque la ruta tenga espacios
    /// (ej: `C:\Program Files (x86)\...\Lightshot.exe /silent`): cortar en el primer
    /// espacio dejaba la ruta a medias. Se prueban prefijos crecientes y se queda con
    /// el más largo que exista como archivo; si ninguno existe, con el primer token
    /// (para que el error de archivo se reporte igual).
    /// </summary>
    private static string? ExtractExePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var trimmed = commandLine.Trim();

        // Entre comillas: hasta la comilla de cierre.
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = trimmed.IndexOf("\"", 1, StringComparison.Ordinal);
            if (end > 0) return trimmed.Substring(1, end - 1);
            return trimmed.Trim('\"');
        }

        // Sin comillas: probar prefijos cada vez más largos y quedarse con el que existe.
        string? best = null;
        int idx = 0;
        while (idx < trimmed.Length)
        {
            int nextSpace = trimmed.IndexOf(' ', idx);
            if (nextSpace < 0) break;
            var candidate = trimmed[..nextSpace];
            try
            {
                if (File.Exists(candidate)) best = candidate;
            }
            catch { }
            idx = nextSpace + 1;
        }
        if (best != null) return best;

        // La ruta completa también puede ser el archivo (cuando no quedan espacios
        // y el último candidato era una carpeta, ej: "C:\Program Files\...\Lightshot.exe").
        try { if (File.Exists(trimmed)) return trimmed; } catch { }

        // Nada existe: primer token (la ruta probablemente no existe, ya se verá).
        int space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    /// <summary>
    /// Resuelve el destino de un acceso directo .lnk usando ShellLink COM.
    /// </summary>
    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic folder = shell.NameSpace(Path.GetDirectoryName(lnkPath));
            dynamic item = folder.ParseName(Path.GetFileName(lnkPath));
            dynamic link = item.GetLink;
            return link.Path;
        }
        catch { return null; }
    }

    /// <summary>
    /// Convierte un System.Drawing.Bitmap a BitmapImage de WinUI 3 (para uso en UI).
    /// </summary>
    private static async Task<BitmapImage> ToBitmapImageAsync(System.Drawing.Bitmap bmp)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        using var stream = ms.AsRandomAccessStream();
        await bi.SetSourceAsync(stream);
        return bi;
    }


    /// <summary>Convierte Bitmap a BitmapImage de forma sÃ­ncrona (UI thread).</summary>
    private static BitmapImage ToBitmapImageSync(System.Drawing.Bitmap bmp)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.SetSource(ms.AsRandomAccessStream());
        return bi;
    }
    // Etiquetas de los 3 ítems por navegador (mismo orden que BrowserSubItem).
    private static readonly string[] BrowserItemLabels =
    [
        "Archivos temporales de internet", "Cookies", "Historial de navegación"
    ];

    public LimpiezaPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _cleanup = App.Services.GetRequiredService<ICleanupService>();
        _dupFinder = App.Services.GetRequiredService<IDuplicateFinderService>();
        _settings = App.Services.GetService<ISettingsService>();
        _startupMgr = App.Services.GetRequiredService<IStartupManagerService>();
        _logging = App.Services.GetRequiredService<ILoggingService>();
        _driveWatcher = App.Services.GetService<IDriveWatcherService>();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Velocímetros de progreso: dial con aguja que muestra el % de
        // completado durante el análisis/escaneo. El DupRing es más chico
        // porque comparte fila con el texto de carpeta actual.
        ChequeoRing.Label = "";
        CustomRing.Label = "";
        DupRing.ConfigureSize(120);
        DupRing.Label = "";

        // Stepper de fases del Chequeo: puntos + línea, estilo onboarding.
        BuildChequeoPhaseDots();

        // Papelera opcional en duplicados: estado persistente en settings.json.
        if (_settings != null)
        {
            DupRecycleToggle.IsChecked = _settings.Get("duplicates.recycleBin", true);
            DupRecycleToggle.Checked += (_, _) => SaveDupRecycleSetting();
            DupRecycleToggle.Unchecked += (_, _) => SaveDupRecycleSetting();
        }

        // Detección en caliente de unidades (USB / disco externo): al enchufar algo
        // se refresca la lista del buscador de duplicados. La página usa caché de
        // navegación, así que la suscripción vive mientras la página exista.
        if (_driveWatcher != null)
        {
            _driveWatcher.EnsureStarted();
            _driveWatcher.DriveArrived += OnDriveArrived;
        }

        // La página vive con caché de navegación: re-aplicar el idioma al cambiar.
        I18n.LanguageChanged += OnLanguageChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTabsLanguage();
        RebuildCustomCategories();
    }

    private void OnLanguageChanged()
    {
        ApplyTabsLanguage();
        RebuildCustomCategories();
        if (ChequeoResultsPanel.Visibility == Visibility.Visible)
            UpdateChequeoPhaseDots();
    }

    private void ApplyTabsLanguage()
    {
        if (LimpiezaTabs.Items.Count >= 4)
        {
            ((SelectorBarItem)LimpiezaTabs.Items[0]).Text = I18n.T("Chequeo");
            ((SelectorBarItem)LimpiezaTabs.Items[1]).Text = I18n.T("Limpieza personalizada");
            ((SelectorBarItem)LimpiezaTabs.Items[2]).Text = I18n.T("Buscador de duplicados");
            ((SelectorBarItem)LimpiezaTabs.Items[3]).Text = I18n.T("Administración de inicio");
        }
    }

    private void LimpiezaTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        int idx = sender.Items.IndexOf(sender.SelectedItem);
        ChequeoTab.Visibility   = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        CustomTab.Visibility    = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        DupTab.Visibility       = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        StartupTab.Visibility   = idx == 3 ? Visibility.Visible : Visibility.Collapsed;

        // Carga perezosa: construir el contenido al abrir la pestaña por primera vez.
        if (idx == 2 && DupGroupsHost.Children.Count == 0 && DupDrivePicker.Items.Count == 0)
            PopulateDrivePicker();
        if (idx == 3 && StartupEntriesHost.Children.Count == 0)
            _ = LoadStartupEntriesAsync();

        // Cancelar scans de otras pestañas al cambiar y detener el refresco de RAM.
        if (idx != 0)
        {
            _chequeoCts?.Cancel();
            _chequeoCts = null;
            StopChequeoAppsTimer();
        }
        else if (ChequeoResultsPanel.Visibility == Visibility.Visible)
        {
            StartChequeoAppsTimer();
        }
        if (idx != 1) { _customScanCts?.Cancel(); _customScanCts = null; }
    }

    // =====================================================================
    // Chequeo (estilo CCleaner: analizar → resultados en secciones)
    // =====================================================================

    /// <summary>
    /// Botón central "Analizar": escanea navegadores, sistema, caché de apps
    /// y papelera, y muestra los resultados agrupados en secciones.
    /// </summary>
    /// <summary>
    /// Botón "Analizar". Usa EXACTAMENTE el mismo patrón que el auto-scan de
    /// Limpieza personalizada (que funciona): awaits directos a los métodos async
    /// del servicio (que ya corren en thread pool internamente), y después de cada
    /// await se toca la UI directo porque se retoma en el hilo UI. Cero TryEnqueue,
    /// cero GetAwaiter, cero Task.Run manual.
    /// </summary>
    private async void ChequeoAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        _chequeoCts?.Cancel();
        _chequeoCts = new CancellationTokenSource();
        var ct = _chequeoCts.Token;
        var sw = Stopwatch.StartNew();

        try
        {
            // ---- Resetear UI ----
            // Vuelve al estado inicial (el análisis arranca mostrando el progreso,
            // así que se oculta el panel de Analizar que deja visible el reset).
            ResetChequeoToStart();
            ChequeoStartPanel.Visibility = Visibility.Collapsed;

            SetBusy(false);

            // Mostrar anillo de progreso con porcentaje central.
            ChequeoRing.Progress = 0;
            ChequeoRing.Value = "0%";
            UpdateChequeoProgress(2, I18n.T("Preparando..."));
            ChequeoProgressPanel.Visibility = Visibility.Visible;

            IReadOnlyCollection<BrowserSubItem> allItems = [BrowserSubItem.Cache, BrowserSubItem.Cookies, BrowserSubItem.History];
            var sysIds = new[] { "sys_temp", "sys_usertemp", "sys_crashdumps", "sys_wer" };
            var cacheIds = new[] { "mm_thumbs", "mm_iconcache", "mm_wmp" };
            var rbIds = new[] { "sys_recyclebin" };

            long totalBytes = 0;
            long browserBytes = 0;
            int totalApps = 0;
            int totalStartup = 0;

            // =====================================================================
            // FASE 1/3 · Limpieza de archivos (navegadores + sistema + caché + papelera)
            // =====================================================================
            UpdateChequeoProgress(4, I18n.T("Fase {0} de {1}: {2}...", 1, 3, I18n.T("Limpieza de archivos")));

            // Detectar navegadores instalados y procesos abiertos por separado.
            // Un navegador abierto puede no tener aún un perfil detectable (por
            // ejemplo, primera ejecución), pero igualmente debe disparar el aviso.
            var browsers = await Task.Run(() => _cleanup.GetBrowsers().ToList(), ct);
            var runningByProcess = await Task.Run(GetRunningBrowserProcesses, ct);
            browsers = browsers
                .Where(b => b.IsInstalled || runningByProcess.Contains(b.ProcessName, StringComparer.OrdinalIgnoreCase))
                .Select(b => b with { IsRunning = IsBrowserRunning(b, runningByProcess) })
                .ToList();

            // Si hay un proceso compatible abierto sin entrada instalada, crear una
            // ficha mínima para que el diálogo también pueda identificarlo.
            foreach (var processName in runningByProcess)
            {
                if (browsers.Any(b => b.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))) continue;
                var fallback = _cleanup.GetBrowsers().FirstOrDefault(b => b.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
                if (fallback != null) browsers.Add(fallback with { IsInstalled = true, IsRunning = true });
            }

            // ---- Popup: si hay navegadores abiertos, avisar para cerrarlos antes
            //      de seguir (o continuar sin cerrar / cancelar el análisis). ----
            var stillOpen = await PromptCloseRunningBrowsersAsync(browsers, ct);
            _logging.LogDebug($"[Chequeo] Navegadores instalados={browsers.Count}, abiertos={browsers.Count(b => b.IsRunning)}");
            if (stillOpen == null)
            {
                ChequeoProgressPanel.Visibility = Visibility.Collapsed;
                ChequeoStartPanel.Visibility = Visibility.Visible;
                return;
            }
            var openBrowserIds = new HashSet<string>(stillOpen.Select(b => b.Id), StringComparer.Ordinal);

            // Construir cards vacías en la UI (rápido, sin I/O). Los navegadores que
            // siguen abiertos no muestran contenido: solo el aviso amarillo.
            foreach (var info in browsers)
                ChequeoBrowsersHost?.Children.Add(BuildChequeoBrowserCard(info, isOpen: openBrowserIds.Contains(info.Id)));

            // ---- Paso 1/4: Navegadores (5-25%) ----
            int totalBrowsers = browsers.Count;
            for (int i = 0; i < totalBrowsers; i++)
            {
                ct.ThrowIfCancellationRequested();
                var info = browsers[i];
                // Sigue abierto (se omitió cerrarlo): no se mide su contenido.
                if (openBrowserIds.Contains(info.Id)) continue;
                UpdateChequeoProgress(5 + (int)(20.0 * i / totalBrowsers), I18n.T("Escaneando {0}...", info.DisplayName));

                try
                {
                    var r = await _cleanup.ScanBrowserAsync(info.Id, allItems, ct);
                    totalBytes += r.TotalBytes;
                    browserBytes += r.TotalBytes;

                    // Actualizar pesos de este navegador (retomamos en UI thread).
                    foreach (var item in r.Items)
                    {
                        int idx = (int)Enum.Parse<BrowserSubItem>(item.Id.Split('.').Last());
                        SetChequeoSize(info.Id, idx, item.Bytes);
                    }
                }
                catch (Exception ex)
                {
                    _logging.LogWarning($"Chequeo: falló {info.Id}: {ex.Message}");
                }
            }
            if (browsers.Count > 0)
            {
                ChequeoBrowserSection.Visibility = Visibility.Visible;
                SetChequeoSectionTotal(ChequeoBrowserTotal, browserBytes);
            }

            // ---- Paso 2/4: Sistema (25-35%) ----
            UpdateChequeoProgress(25, "Archivos temporales del sistema...");
            var sysResult = await _cleanup.ScanCustomAsync(sysIds, null, ct);
            totalBytes += sysResult.TotalBytes;
            foreach (var item in sysResult.Items)
                AddChequeoRow(ChequeoSystemHost!, item.Id, item.Name, item.Bytes, item.FileCount, item.AnalysisOnly);
            ChequeoSystemSection.Visibility = Visibility.Visible;
            SetChequeoSectionTotal(ChequeoSystemTotal, sysResult.TotalBytes);

            // ---- Paso 3/4: Caché de aplicaciones (35-45%) ----
            UpdateChequeoProgress(35, I18n.T("Escaneando {0}...", I18n.T("Memoria caché de aplicaciones")));
            var cacheResult = await _cleanup.ScanCustomAsync(cacheIds, null, ct);
            totalBytes += cacheResult.TotalBytes;
            foreach (var item in cacheResult.Items)
                AddChequeoRow(ChequeoAppCacheHost!, item.Id, item.Name, item.Bytes, item.FileCount, item.AnalysisOnly);
            ChequeoAppCacheSection.Visibility = Visibility.Visible;
            SetChequeoSectionTotal(ChequeoAppCacheTotal, cacheResult.TotalBytes);

            // ---- Paso 4/4: Papelera (45-52%) ----
            UpdateChequeoProgress(45, I18n.T("Escaneando {0}...", I18n.T("Papelera de reciclaje")));
            var rbResult = await _cleanup.ScanCustomAsync(rbIds, null, ct);
            totalBytes += rbResult.TotalBytes;
            foreach (var item in rbResult.Items)
                AddChequeoRow(ChequeoRecycleHost!, item.Id, item.Name, item.Bytes, item.FileCount, item.AnalysisOnly);
            ChequeoRecycleSection.Visibility = Visibility.Visible;
            SetChequeoSectionTotal(ChequeoRecycleTotal, rbResult.TotalBytes);
            UpdateChequeoProgress(52, I18n.T("Limpieza de archivos") + " ✓");

            // =====================================================================
            // FASE 2/3 · Aplicaciones en segundo plano (procesos con ventana)
            // =====================================================================
            UpdateChequeoProgress(55, I18n.T("Fase {0} de {1}: {2}...", 2, 3, I18n.T("Aplicaciones en segundo plano")));
            var runningApps = await Task.Run(EnumerateBackgroundApps, ct);
            foreach (var app in runningApps)
            {
                ct.ThrowIfCancellationRequested();
                var row = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },   // ícono
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }    // RAM
                    },
                    ColumnSpacing = 10
                };
                // Badge con el ícono REAL del proceso (extraído de su .exe).
                var badge = new Border
                {
                    Width = 28, Height = 28,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new FontIcon
                    {
                        Glyph = "\uE7B5", FontSize = 16,
                        Foreground = ThemeBrushes.Get("AccentBrush"),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
                EnsureAppIcon(app.Pid, app.ProcessName, badge);
                var label = string.IsNullOrWhiteSpace(app.Title) ? app.ProcessName : $"{app.Title} ({app.ProcessName})";
                var cb = new CheckBox { Content = label, MinHeight = 26, IsChecked = false };
                cb.Checked += ChequeoAppsCheckChanged;
                cb.Unchecked += ChequeoAppsCheckChanged;
                var ram = new TextBlock
                {
                    Text = FormatRam(app.WorkingSetMB),
                    FontSize = 12,
                    Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 64,
                    TextAlignment = TextAlignment.Right
                };
                row.Children.Add(badge);
                Grid.SetColumn(cb, 1);
                row.Children.Add(cb);
                Grid.SetColumn(ram, 2);
                row.Children.Add(ram);
                ChequeoAppsHost?.Children.Add(row);
                _chequeoApps.Add(new ChequeoUi { Id = app.Pid.ToString(), Check = cb, Size = ram, BrowserId = null, SubItem = null });
                totalApps++;
            }
            if (totalApps > 0)
            {
                ChequeoAppsSection.Visibility = Visibility.Visible;
                if (ChequeoAppsTotal != null)
                {
                    ChequeoAppsTotal.Text = I18n.T("{0} detectadas", totalApps);
                    ChequeoAppsTotal.Visibility = Visibility.Visible;
                }
            }
            UpdateChequeoProgress(72, I18n.T("Aplicaciones en segundo plano") + " ✓");

            // =====================================================================
            // FASE 3/3 · Aplicaciones de inicio
            // =====================================================================
            UpdateChequeoProgress(75, I18n.T("Fase {0} de {1}: {2}...", 3, 3, I18n.T("Aplicaciones de inicio")));
            IReadOnlyList<StartupEntry> startupEntries;
            try { startupEntries = _startupMgr.GetEntries(); }
            catch { startupEntries = []; }
            foreach (var entry in startupEntries.Where(e => e.IsEnabled))
            {
                ct.ThrowIfCancellationRequested();
                var row = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    ColumnSpacing = 12
                };
                var label = string.IsNullOrWhiteSpace(entry.Command) ? entry.Name : $"{entry.Name} — {entry.Command}";
                var cb = new CheckBox { Content = label, MinHeight = 26, IsChecked = false };
                cb.Checked += ChequeoStartupCheckChanged;
                cb.Unchecked += ChequeoStartupCheckChanged;
                ToolTipService.SetToolTip(cb, entry.Command);
                var badge = new TextBlock
                {
                    Text = I18n.T("Inicio automático"),
                    FontSize = 12,
                    Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                row.Children.Add(cb);
                Grid.SetColumn(badge, 1);
                row.Children.Add(badge);
                ChequeoStartupHost?.Children.Add(row);
                _chequeoStartup.Add(new ChequeoUi { Id = entry.Id, Check = cb, Size = badge, BrowserId = null, SubItem = null });
                totalStartup++;
            }
            if (totalStartup > 0)
            {
                ChequeoStartupSection.Visibility = Visibility.Visible;
                if (ChequeoStartupTotal != null)
                {
                    ChequeoStartupTotal.Text = I18n.T("{0} detectadas", totalStartup);
                    ChequeoStartupTotal.Visibility = Visibility.Visible;
                }
            }
            UpdateChequeoProgress(95, I18n.T("Aplicaciones de inicio") + " ✓");

            // ---- Terminado ----
            // Volcar el resultado del análisis al log de desarrollo para debugueo
            // (app.log solo se escribe si "Logs de desarrollo" está activado).
            _logging.LogDebug(
                "[Chequeo] Análisis completado | navegadores=" + FormatBytes(browserBytes) +
                ", sistema=" + FormatBytes(sysResult.TotalBytes) +
                ", caché de aplicaciones=" + FormatBytes(cacheResult.TotalBytes) +
                ", papelera=" + FormatBytes(rbResult.TotalBytes) +
                ", apps en segundo plano=" + totalApps +
                ", apps de inicio=" + totalStartup +
                " | total=" + FormatBytes(totalBytes) +
                $" | duración={sw.Elapsed.TotalSeconds:F1} s");
            UpdateChequeoProgress(100, I18n.T("¡Análisis completado!"));
            await Task.Delay(300, ct);

            // Guardar estado y mostrar resultados en vistas por fase (fase 1 activa).
            _chequeoTotalBytes = totalBytes;
            _chequeoPhase = 0;
            ChequeoProgressPanel.Visibility = Visibility.Collapsed;
            ChequeoResultsPanel.Visibility = Visibility.Visible;
            if (ChequeoTotalsText != null)
            {
                ChequeoTotalsText.Text = I18n.T("Total: {0}", FormatBytes(totalBytes));
                ChequeoTotalsText.Visibility = Visibility.Visible;
            }
            // Recién acá aparece la card de total, fija arriba (fuera del scroll).
            if (ChequeoHeroCard != null) ChequeoHeroCard.Visibility = Visibility.Visible;
            if (ChequeoHeroDetails != null) ChequeoHeroDetails.Visibility = Visibility.Visible;
            UpdateChequeoPhaseView();
            // Refresco en vivo de la RAM de las apps en segundo plano (fase 2).
            StartChequeoAppsTimer();
            if (ChequeoFeedbackText != null)
            {
                ChequeoFeedbackText.Visibility = Visibility.Visible;
                Feedback.Success(ChequeoFeedbackText, I18n.T("Análisis completado: {0} en archivos, {1} apps en segundo plano y {2} de inicio.",
                    FormatBytes(totalBytes), totalApps, totalStartup));
            }
        }
        catch (OperationCanceledException)
        {
            ChequeoProgressPanel.Visibility = Visibility.Collapsed;
            ChequeoStartPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Chequeo fatal: {ex}");
            ChequeoProgressPanel.Visibility = Visibility.Collapsed;
            ChequeoStartPanel.Visibility = Visibility.Visible;
            if (ChequeoFeedbackText != null)
            {
                ChequeoFeedbackText.Visibility = Visibility.Visible;
                Feedback.Error(ChequeoFeedbackText, I18n.T("Error: {0}", ex.Message));
            }
        }
        finally
        {
            SetBusy(true);
        }
    }

    /// <summary>Muestra el total de una sección del Chequeo en su cabecera.</summary>
    private static void SetChequeoSectionTotal(TextBlock? total, long bytes)
    {
        if (total == null) return;
        total.Text = I18n.T("Total: {0}", FormatBytes(bytes));
        total.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Enumera las apps corriendo en segundo plano: procesos con una ventana
    /// visible (apps de escritorio abiertas, incluso minimizadas), ordenadas por
    /// consumo de RAM de mayor a menor. Excluye el proceso propio.
    /// </summary>
    private static List<(int Pid, string ProcessName, string? Title, double WorkingSetMB)> EnumerateBackgroundApps()
    {
        var apps = new List<(int Pid, string ProcessName, string? Title, double WorkingSetMB)>();
        int ownPid;
        try { ownPid = Environment.ProcessId; } catch { ownPid = -1; }
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return apps; }
        foreach (var p in procs)
        {
            try
            {
                if (p.HasExited || p.Id == ownPid) continue;
                var handle = p.MainWindowHandle;
                if (handle == IntPtr.Zero) continue; // sin ventana = servicio/tarea de fondo
                string? title = null;
                try { title = p.MainWindowTitle; } catch { }
                if (string.IsNullOrWhiteSpace(title)) continue;
                apps.Add((p.Id, p.ProcessName, title, p.WorkingSet64 / (1024.0 * 1024.0)));
            }
            catch { }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
        return apps.OrderByDescending(a => a.WorkingSetMB).ToList();
    }

    /// <summary>RAM legible: MB si es chico, GB si pasa de 1024 MB.</summary>
    private static string FormatRam(double mb)
        => mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb:F0} MB";

    /// <summary>
    /// Pone en el Border indicado el ícono REAL del proceso (extraído de su .exe,
    /// cacheado por ruta: varias apps comparten ejecutable). Primero muestra un
    /// FontIcon de respaldo y después lo reemplaza en background vía DispatcherQueue.
    ///
    /// La ruta del exe se resuelve con varios intentos para que el ícono aparezca
    /// aunque MainModule falle (procesos elevados/protegidos dan acceso denegado):
    /// 1) MainModule del pid, 2) MainModule por nombre de proceso, 3) WMI
    /// (Win32_Process.ExecutablePath, funciona cruzando elevación). Si el exe
    /// no tiene recurso de ícono propio, se cae al ícono genérico de .exe del
    /// Explorador en vez de dejar el FontIcon de respaldo.
    /// </summary>
    private static void EnsureAppIcon(int pid, string processName, Border badgeHost)
    {
        // Fallback inmediato: ícono de app con el acento, para que el badge
        // NUNCA quede vacío (mismo patrón que EnsureBrowserIcon/EnsureStartupIcon).
        badgeHost.Child = new FontIcon
        {
            Glyph = "\uE7B5", FontSize = 16,
            Foreground = ThemeBrushes.Get("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Resolver la ruta del exe: MainModule por pid → por nombre → WMI.
        string? exePath = ResolveAppExePath(pid, processName);
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

        // ¿Ya cacheado?
        lock (AppIconReady)
        {
            if (AppIconReady.TryGetValue(exePath, out var bi))
            {
                badgeHost.Child = new WinImage
                {
                    Source = bi, Width = 24, Height = 24,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                return;
            }
        }
        lock (AppIconFailed) { if (AppIconFailed.Contains(exePath)) return; }

        // Extraer en background (como GestionarProcesosPage).
        _ = Task.Run(() =>
        {
            try
            {
                using var big = IconExtractor.ExtractHighResIcon(exePath);
                if (big == null)
                {
                    // Sin recurso de ícono propio: usar el genérico de .exe.
                    var fallbackBmp = IconExtractor.ExtractDefaultExeIcon();
                    if (fallbackBmp != null)
                    {
                        var fb = fallbackBmp;
                        _ = badgeHost.DispatcherQueue.TryEnqueue(() =>
                        {
                            try
                            {
                                lock (AppIconReady) AppIconReady[exePath] = ToBitmapImageSync(fb);
                                badgeHost.Child = new WinImage
                                {
                                    Source = AppIconReady[exePath], Width = 24, Height = 24,
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center
                                };
                            }
                            catch { }
                            finally { fb.Dispose(); }
                        });
                    }
                    else
                    {
                        lock (AppIconFailed) { AppIconFailed.Add(exePath); }
                    }
                    return;
                }

                using var small = new Bitmap(28, 28);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(big, 0, 0, 28, 28);
                }

                // Serializar el PNG en el hilo de trabajo y recién tocar la UI.
                byte[] pngBytes;
                using (var ms = new MemoryStream())
                {
                    small.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    pngBytes = ms.ToArray();
                }

                _ = badgeHost.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream(pngBytes);
                        var bi = new BitmapImage();
                        bi.SetSource(ms.AsRandomAccessStream());
                        lock (AppIconReady) AppIconReady[exePath] = bi;
                        badgeHost.Child = new WinImage
                        {
                            Source = bi, Width = 24, Height = 24,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                    }
                    catch { }
                });
            }
            catch { lock (AppIconFailed) { AppIconFailed.Add(exePath); } }
        });
    }

    /// <summary>
    /// Resuelve la ruta del ejecutable de un proceso con varios intentos:
    /// MainModule por pid → MainModule por nombre de proceso → WMI (funciona
    /// aunque el proceso esté elevado y el nuestro no, o viceversa).
    /// </summary>
    private static string? ResolveAppExePath(int pid, string processName)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited)
            {
                var m = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(m) && File.Exists(m)) return m;
            }
        }
        catch { }

        try
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                using (p)
                {
                    var m = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(m) && File.Exists(m)) return m;
                }
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var path = obj["ExecutablePath"] as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Arranca el refresco en vivo de la RAM de las apps en segundo plano
    /// (cada 2 s re-lee el WorkingSet de cada proceso y actualiza el texto).
    /// </summary>
    private void StartChequeoAppsTimer()
    {
        if (_chequeoApps.Count == 0) return;
        if (_chequeoAppsTimer == null)
        {
            _chequeoAppsTimer = DispatcherQueue.CreateTimer();
            _chequeoAppsTimer.Interval = TimeSpan.FromSeconds(2);
            _chequeoAppsTimer.Tick += (s, e) => RefreshChequeoAppRam();
        }
        if (!_chequeoAppsTimer.IsRunning) _chequeoAppsTimer.Start();
    }

    private void StopChequeoAppsTimer() => _chequeoAppsTimer?.Stop();

    /// <summary>
    /// Actualiza el consumo de RAM en vivo de cada app en segundo plano. Si el
    /// proceso ya no existe (se cerró), la fila queda en "—" y deshabilitada.
    /// </summary>
    private void RefreshChequeoAppRam()
    {
        foreach (var app in _chequeoApps)
        {
            if (!app.Check.IsEnabled) continue; // ya cerrada
            try
            {
                int pid = int.Parse(app.Id);
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    app.Size.Text = "—";
                    app.Check.IsEnabled = false;
                    continue;
                }
                app.Size.Text = FormatRam(p.WorkingSet64 / (1024.0 * 1024.0));
            }
            catch
            {
                app.Size.Text = "—";
                app.Check.IsEnabled = false;
            }
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopChequeoAppsTimer();

    /// <summary>Actualiza el botón "Cerrar seleccionadas" según haya apps tildadas.</summary>
    private void ChequeoAppsCheckChanged(object sender, RoutedEventArgs e)
        => UpdateChequeoAppsButton();

    private void ChequeoStartupCheckChanged(object sender, RoutedEventArgs e)
        => UpdateChequeoStartupButton();

    private void UpdateChequeoAppsButton()
    {
        int selected = _chequeoApps.Count(c => c.Check.IsChecked == true);
        ChequeoPhaseActionButton.Content = selected > 0
            ? I18n.T("Cerrar seleccionadas ({0})", selected)
            : I18n.T("Cerrar seleccionadas");
        ChequeoPhaseActionButton.IsEnabled = selected > 0;
    }

    private void UpdateChequeoStartupButton()
    {
        int selected = _chequeoStartup.Count(c => c.Check.IsChecked == true);
        ChequeoPhaseActionButton.Content = selected > 0
            ? I18n.T("Desactivar seleccionadas ({0})", selected)
            : I18n.T("Desactivar seleccionadas");
        ChequeoPhaseActionButton.IsEnabled = selected > 0;
    }

    /// <summary>
    /// Muestra la vista de la fase activa y configura el botón de acción y la
    /// navegación (Anterior/Siguiente) según la fase.
    /// </summary>
    private void UpdateChequeoPhaseView()
    {
        ChequeoPhase1View.Visibility = _chequeoPhase == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChequeoPhase2View.Visibility = _chequeoPhase == 1 ? Visibility.Visible : Visibility.Collapsed;
        ChequeoPhase3View.Visibility = _chequeoPhase == 2 ? Visibility.Visible : Visibility.Collapsed;

        if (ChequeoPhaseActionButton != null)
        {
            if (_chequeoPhase == 0)
            {
                ChequeoPhaseActionButton.Content = I18n.T("Limpiar");
                ChequeoPhaseActionButton.IsEnabled = _chequeoTotalBytes > 0;
            }
            else if (_chequeoPhase == 1)
                UpdateChequeoAppsButton();
            else
                UpdateChequeoStartupButton();
        }

        if (ChequeoSkipPhaseButton != null)
            ChequeoSkipPhaseButton.Visibility = Visibility.Visible;

        UpdateChequeoPhaseDots();
    }

    /// <summary>
    /// Construye el stepper de fases como puntos + línea (estilo onboarding).
    /// Cada punto tiene un Tooltip con el nombre de la fase y es clickeable para saltar.
    /// </summary>
    private void BuildChequeoPhaseDots()
    {
        for (int i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                var line = new Border
                {
                    Width = 34,
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    Margin = new Thickness(0, 6, 0, 0),
                    Background = ThemeBrushes.Get("MutedBrush")
                };
                _chequeoPhaseLines.Add(line);
                ChequeoPhaseDots.Children.Add(line);
            }

            var dot = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = ThemeBrushes.Get("MutedBrush")
            };
            int phase = i;
            dot.Tapped += (_, _) => JumpToChequeoPhase(phase);
            ToolTipService.SetToolTip(dot,
                I18n.T("Fase {0} de {1}", i + 1, 3) + " · " + I18n.T(ChequeoPhaseNames[i]));
            _chequeoPhaseDots.Add(dot);
            ChequeoPhaseDots.Children.Add(dot);
        }
    }

    /// <summary>
    /// Actualiza el stepper superior: puntos alcanzados + fase activa = acento,
    /// el resto apagado. El título muestra en qué fase estás parado.
    /// </summary>
    private void UpdateChequeoPhaseDots()
    {
        var done = ThemeBrushes.Get("AccentBrush");
        var inactive = ThemeBrushes.Get("MutedBrush");

        for (int i = 0; i < _chequeoPhaseDots.Count; i++)
            _chequeoPhaseDots[i].Background = i <= _chequeoPhase ? done : inactive;
        for (int i = 0; i < _chequeoPhaseLines.Count; i++)
            _chequeoPhaseLines[i].Background = (i + 1) <= _chequeoPhase ? done : inactive;

        if (ChequeoPhaseTitle != null)
            ChequeoPhaseTitle.Text = I18n.T("Fase {0} de {1}", _chequeoPhase + 1, 3) + " · " + I18n.T(ChequeoPhaseNames[_chequeoPhase]);
    }

    /// <summary>Salta a la fase indicada (0-2) desde el stepper de puntos.</summary>
    private void JumpToChequeoPhase(int idx)
    {
        if (idx < 0 || idx > 2 || idx == _chequeoPhase) return;
        _chequeoPhase = idx;
        UpdateChequeoPhaseView();
    }

    /// <summary>
    /// "Saltar": avanza a la siguiente fase. En la última (fase 3) vuelve a la
    /// vista inicial del Chequeo (botón Analizar + texto), para arrancar un
    /// análisis nuevo desde cero.
    /// </summary>
    private void ChequeoSkipPhaseButton_Click(object sender, RoutedEventArgs e)
    {
        // Última fase: volver al panel de inicio (botón Analizar + texto).
        if (_chequeoPhase >= 2)
        {
            ResetChequeoToStart();
            return;
        }
        _chequeoPhase++;
        UpdateChequeoPhaseView();
    }

    /// <summary>
    /// Devuelve la pestaña Chequeo al estado inicial: panel de Analizar visible,
    /// resultados y card de total ocultos, listas y temporizador limpiados.
    /// </summary>
    private void ResetChequeoToStart()
    {
        StopChequeoAppsTimer();
        _chequeoItems.Clear();
        _chequeoApps.Clear();
        _chequeoStartup.Clear();
        _chequeoPhase = 0;
        _chequeoTotalBytes = 0;

        ChequeoStartPanel.Visibility = Visibility.Visible;
        ChequeoResultsPanel.Visibility = Visibility.Collapsed;
        // La card de total NO aparece antes de analizar.
        if (ChequeoHeroCard != null) ChequeoHeroCard.Visibility = Visibility.Collapsed;
        if (ChequeoHeroDetails != null) ChequeoHeroDetails.Visibility = Visibility.Collapsed;
        ChequeoBrowserSection.Visibility = Visibility.Collapsed;
        ChequeoSystemSection.Visibility = Visibility.Collapsed;
        ChequeoAppCacheSection.Visibility = Visibility.Collapsed;
        ChequeoRecycleSection.Visibility = Visibility.Collapsed;
        ChequeoAppsSection.Visibility = Visibility.Collapsed;
        ChequeoStartupSection.Visibility = Visibility.Collapsed;
        ChequeoBrowsersHost?.Children.Clear();
        ChequeoSystemHost?.Children.Clear();
        ChequeoAppCacheHost?.Children.Clear();
        ChequeoRecycleHost?.Children.Clear();
        ChequeoAppsHost?.Children.Clear();
        ChequeoStartupHost?.Children.Clear();
        if (ChequeoTotalsText != null)
        {
            ChequeoTotalsText.Text = "—";
            ChequeoTotalsText.Visibility = Visibility.Visible;
        }
        if (ChequeoFeedbackText != null) ChequeoFeedbackText.Visibility = Visibility.Collapsed;
        if (ChequeoBrowserTotal != null) ChequeoBrowserTotal.Visibility = Visibility.Collapsed;
        if (ChequeoSystemTotal != null) ChequeoSystemTotal.Visibility = Visibility.Collapsed;
        if (ChequeoAppCacheTotal != null) ChequeoAppCacheTotal.Visibility = Visibility.Collapsed;
        if (ChequeoRecycleTotal != null) ChequeoRecycleTotal.Visibility = Visibility.Collapsed;
        if (ChequeoAppsTotal != null) ChequeoAppsTotal.Visibility = Visibility.Collapsed;
        if (ChequeoStartupTotal != null) ChequeoStartupTotal.Visibility = Visibility.Collapsed;
        if (ChequeoPhaseActionButton != null)
        {
            ChequeoPhaseActionButton.Content = I18n.T("Limpiar");
            ChequeoPhaseActionButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// Acción de la fase activa: Limpiar (fase 1), Cerrar apps (fase 2) o
    /// Desactivar inicio (fase 3).
    /// </summary>
    private void ChequeoPhaseActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chequeoPhase == 1) { _ = CloseSelectedBackgroundAppsAsync(); }
        else if (_chequeoPhase == 2) { _ = DisableSelectedStartupAsync(); }
        else { ChequeoCleanButton_Click(sender, e); }
    }

    /// <summary>FASE 2 · Cierra las apps en segundo plano tildadas (CloseMainWindow suave, Kill solo si no responde).</summary>
    private async Task CloseSelectedBackgroundAppsAsync()
    {
        var selected = _chequeoApps.Where(c => c.Check.IsChecked == true).ToList();
        if (selected.Count == 0 || XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Cerrar aplicaciones"),
            Content = I18n.T("Se cerrarán {0} aplicaciones en segundo plano. Guardá tu trabajo antes de continuar.", selected.Count),
            PrimaryButtonText = I18n.T("Cerrar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        int closed = 0;
        foreach (var app in selected)
        {
            var originalLabel = app.Check.Content as string ?? string.Empty;
            try
            {
                int pid = int.Parse(app.Id);
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                {
                    if (!p.CloseMainWindow())
                        p.Kill();
                }
                closed++;
                app.Check.IsChecked = false;
                app.Check.IsEnabled = false;
                app.Check.Content = I18n.T("{0} (cerrada)", originalLabel);
            }
            catch { }
        }
        UpdateChequeoAppsButton();
        Feedback.Success(ChequeoFeedbackText, I18n.T("Aplicaciones cerradas: {0}.", closed));
    }

    /// <summary>FASE 3 · Deshabilita las apps de inicio tildadas (Toggle con enable=false).</summary>
    private async Task DisableSelectedStartupAsync()
    {
        var selected = _chequeoStartup.Where(c => c.Check.IsChecked == true).ToList();
        if (selected.Count == 0 || XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Desactivar inicio"),
            Content = I18n.T("Se desactivarán {0} aplicaciones de inicio. Podés volver a activarlas desde Administración de inicio.", selected.Count),
            PrimaryButtonText = I18n.T("Desactivar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        int disabled = 0;
        foreach (var entry in selected)
        {
            try
            {
                if (_startupMgr.Toggle(entry.Id, false)) disabled++;
                entry.Check.IsChecked = false;
                entry.Check.IsEnabled = false;
            }
            catch { }
        }
        UpdateChequeoStartupButton();
        Feedback.Success(ChequeoFeedbackText, I18n.T("Aplicaciones de inicio desactivadas: {0}.", disabled));
    }

    /// <summary>
    /// Limpia todo lo que esté tildado en las 4 secciones del Chequeo.
    /// </summary>
    private async void ChequeoCleanButton_Click(object sender, RoutedEventArgs e)
    {
        var checkedItems = _chequeoItems.Where(c => c.Check.IsChecked == true).ToList();
        if (checkedItems.Count == 0) return;

        if (XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Limpiar elementos"),
            Content = I18n.T("Se limpiarán {0} elementos seleccionados. Esta acción no se puede deshacer.", checkedItems.Count),
            PrimaryButtonText = I18n.T("Limpiar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(false);
        Feedback.Running(ChequeoFeedbackText, "Limpiando...", persistent: true);
        var dq = DispatcherQueue;
        long totalFreed = 0;

        try
        {
            // Agrupar: browsers por browserId, custom targets juntos.
            var browserItems = checkedItems.Where(c => c.BrowserId != null).GroupBy(c => c.BrowserId!).ToList();
            var customIds = checkedItems.Where(c => c.BrowserId == null).Select(c => c.Id).Distinct().ToList();

            // Limpiar navegadores (uno por uno).
            foreach (var group in browserItems)
            {
                var subItems = group.Where(g => g.SubItem.HasValue).Select(g => g.SubItem!.Value).ToList();
                if (subItems.Count == 0) continue;
                var result = await _cleanup.CleanBrowserAsync(group.Key, subItems, closeIfRunning: true);
                totalFreed += result.TotalBytes;
            }

            // Limpiar targets personalizados (sistema, caché, papelera).
            if (customIds.Count > 0)
            {
                var result = await _cleanup.CleanCustomAsync(customIds);
                totalFreed += result.TotalBytes;
            }

            dq.TryEnqueue(() =>
            {
                foreach (var c in checkedItems)
                    c.Size.Text = "—";
                ChequeoBrowserTotal.Visibility = Visibility.Collapsed;
                ChequeoSystemTotal.Visibility = Visibility.Collapsed;
                ChequeoAppCacheTotal.Visibility = Visibility.Collapsed;
                ChequeoRecycleTotal.Visibility = Visibility.Collapsed;
                ChequeoTotalsText.Text = I18n.T("Limpieza completada: {0} liberados.", FormatBytes(totalFreed));
                ChequeoTotalsText.Visibility = Visibility.Visible;
                _chequeoTotalBytes = 0;
                UpdateChequeoPhaseView();
                Feedback.Success(ChequeoFeedbackText, I18n.T("Limpieza completada: {0} liberados.", FormatBytes(totalFreed)));
            });
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"LimpiezaPage: limpiar chequeo: {ex.Message}");
            dq.TryEnqueue(() =>
                Feedback.Error(ChequeoFeedbackText, I18n.T("No se pudo completar la limpieza: {0}", ex.Message)));
        }
        finally
        {
            dq.TryEnqueue(() => SetBusy(true));
        }
    }

    /// <summary>
    /// Agrega una fila (checkbox + tamaño) a un host de sección.
    /// </summary>
    private void AddChequeoRow(StackPanel host, string id, string name, long bytes, int fileCount, bool analysisOnly)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 12
        };
        var label = analysisOnly && fileCount > 0
            ? I18n.T(name) + $" ({I18n.T("{0} entradas", fileCount)})"
            : I18n.T(name);
        var cb = new CheckBox { Content = label, MinHeight = 26, IsChecked = bytes > 0 };
        var size = new TextBlock
        {
            Text = bytes > 0 ? FormatBytes(bytes) : (analysisOnly ? "—" : "0 B"),
            FontSize = 12,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 64,
            TextAlignment = TextAlignment.Right
        };
        row.Children.Add(cb);
        Grid.SetColumn(size, 1);
        row.Children.Add(size);
        host.Children.Add(row);

        _chequeoItems.Add(new ChequeoUi { Id = id, Check = cb, Size = size });
    }

    /// <summary>
    /// Construye la card de un navegador en el Chequeo (sin datos aún,
    /// solo la estructura: ícono + nombre + 3 checkboxes con tamaño).
    /// Si sigue abierto (se omitió cerrarlo), no muestra el contenido:
    /// solo un aviso amarillo "Navegador no cerrado".
    /// </summary>
    private Border BuildChequeoBrowserCard(BrowserCleanupInfo info, bool isOpen = false)
    {
        // Card interna de navegador: fondo secundario para distinguirla de la card
        // de sección que la contiene (mismo estilo en la app).
        var card = new Border
        {
            Background = ThemeBrushes.Get("CardBackgroundFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var stack = new StackPanel { Spacing = 6 };
        card.Child = stack;

        // Cabecera: ícono + nombre.
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var badgeBg = new Border
        {
            Width = 28, Height = 28,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uE774", FontSize = 16,
                Foreground = ThemeBrushes.Get("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        EnsureBrowserIcon(info.Id, info.ProcessName, badgeBg);
        header.Children.Add(badgeBg);
        header.Children.Add(new TextBlock
        {
            Text = I18n.T(info.DisplayName),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(header);

        if (isOpen)
        {
            // Navegador que sigue abierto: no se mide ni se muestra su contenido.
            var warnRow = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                },
                ColumnSpacing = 8
            };
            warnRow.Children.Add(new TextBlock
            {
                Text = Feedback.WarningPrefix,
                FontSize = 13,
                Foreground = Feedback.WarningBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            var warnText = new TextBlock
            {
                Text = I18n.T("Navegador no cerrado"),
                FontSize = 12,
                Foreground = Feedback.WarningBrush,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(warnText, 1);
            warnRow.Children.Add(warnText);
            stack.Children.Add(warnRow);
        }
        else
        {
            // 3 filas: caché / cookies / historial.
            for (int i = 0; i < 3; i++)
            {
                var row = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    ColumnSpacing = 12
                };
                var subItem = (BrowserSubItem)i;
                var cb = new CheckBox { Content = I18n.T(BrowserItemLabels[i]), MinHeight = 26, IsChecked = true };
                var size = new TextBlock
                {
                    Text = "…",
                    FontSize = 12,
                    Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 64,
                    TextAlignment = TextAlignment.Right
                };
                row.Children.Add(cb);
                Grid.SetColumn(size, 1);
                row.Children.Add(size);
                stack.Children.Add(row);

                _chequeoItems.Add(new ChequeoUi
                {
                    Id = $"{info.Id}.{subItem.ToString().ToLowerInvariant()}",
                    Check = cb, Size = size,
                    BrowserId = info.Id, SubItem = subItem
                });
            }
        }

        return card;
    }

    /// <summary>
    /// Si hay navegadores abiertos, muestra un popup que pide cerrarlos antes de
    /// seguir con el análisis: cada navegador abierto aparece con su ícono real y
    /// un check para decidir si se cierra. Devuelve:
    ///  - null si el usuario canceló (se aborta el análisis);
    ///  - lista vacía si no había navegadores abiertos o se cerraron todos;
    ///  - los navegadores que siguen abiertos (no se marcaron para cerrar).
    /// </summary>
    private static HashSet<string> GetRunningBrowserProcesses()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "brave", "opera", "opera_gx", "thorium", "vivaldi", "firefox", "librewolf", "waterfox", "yandex", "arc"
        };
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!process.HasExited && known.Contains(process.ProcessName))
                        running.Add(process.ProcessName);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
        catch { }
        return running;
    }

    private static bool IsBrowserRunning(BrowserCleanupInfo browser, ISet<string>? runningProcesses = null)
    {
        var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            browser.ProcessName
        };
        if (browser.Id.Equals("opera", StringComparison.OrdinalIgnoreCase))
            processNames.Add("opera_gx");
        if (browser.Id.Equals("thorium", StringComparison.OrdinalIgnoreCase))
            processNames.Add("thorium_shell");
        if (browser.Id.Equals("vivaldi", StringComparison.OrdinalIgnoreCase))
            processNames.Add("vivaldi_crash_handler");

        if (runningProcesses != null && processNames.Any(runningProcesses.Contains))
            return true;

        try
        {
            foreach (var processName in processNames)
            {
                var processes = Process.GetProcessesByName(processName);
                try
                {
                    if (processes.Any(p =>
                    {
                        try { return !p.HasExited; }
                        catch { return false; }
                    }))
                        return true;
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private async Task<IReadOnlyList<BrowserCleanupInfo>?> PromptCloseRunningBrowsersAsync(IReadOnlyList<BrowserCleanupInfo> browsers, CancellationToken ct)
    {
        var running = browsers.Where(b => b.IsRunning).ToList();
        if (running.Count == 0 || XamlRoot == null) return [];

        // Contenido del diálogo: header con badge de acento + subtítulo, filas de
        // navegadores como mini-cards y contador en vivo de lo que se cerrará.
        var content = new StackPanel { Spacing = 14, MaxWidth = 440 };

        // Header: badge con ícono de globo + título + subtítulo.
        var header = new StackPanel { Spacing = 10 };
        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconBadge = new Border
        {
            Width = 40, Height = 40,
            CornerRadius = new CornerRadius(12),
            Background = ThemeBrushes.Get("AccentTintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uE774", FontSize = 20,
                Foreground = ThemeBrushes.Get("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var titleBlock = new TextBlock
        {
            Text = I18n.T("Cerrá los navegadores en uso"),
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerRow.Children.Add(iconBadge);
        headerRow.Children.Add(titleBlock);
        header.Children.Add(headerRow);
        header.Children.Add(new TextBlock
        {
            Text = I18n.T("Cerrá tu navegador para continuar u omitilo."),
            FontSize = 13,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(header);

        // Filas: cada navegador abierto como mini-card con su ícono real + check.
        var rows = new StackPanel { Spacing = 8 };
        var checks = new List<CheckBox>();
        foreach (var info in running)
        {
            var card = new Border
            {
                Background = ThemeBrushes.Get("CardBackgroundBrush"),
                BorderBrush = ThemeBrushes.Get("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 6, 12, 6)
            };
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                },
                ColumnSpacing = 10
            };
            var badge = new Border
            {
                Width = 28, Height = 28,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon
                {
                    Glyph = "\uE774", FontSize = 16,
                    Foreground = ThemeBrushes.Get("AccentBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            EnsureBrowserIcon(info.Id, info.ProcessName, badge);
            var cb = new CheckBox
            {
                Content = I18n.T(info.DisplayName),
                MinHeight = 28,
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            row.Children.Add(badge);
            Grid.SetColumn(cb, 1);
            row.Children.Add(cb);
            card.Child = row;
            rows.Children.Add(card);
            checks.Add(cb);
        }
        content.Children.Add(rows);

        // Contador en vivo: qué se va a cerrar según los checks tildados.
        var countText = new TextBlock
        {
            FontSize = 12,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        content.Children.Add(countText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = null,
            Content = content,
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"]
        };

        // Botón mutable: si hay algún check tildado → "Cerrar y continuar";
        // si ninguno → "Continuar sin cerrar". El contador refleja lo mismo.
        void UpdatePrimaryButton()
        {
            int n = checks.Count(c => c.IsChecked == true);
            dialog.PrimaryButtonText = n > 0
                ? I18n.T("Cerrar y continuar")
                : I18n.T("Continuar sin cerrar");
            countText.Text = n == 0
                ? I18n.T("Ningún navegador se cerrará. El análisis continuará igual.")
                : n == 1
                    ? I18n.T("Se cerrará {0} navegador y continuará el análisis.", n)
                    : I18n.T("Se cerrarán {0} navegadores y continuará el análisis.", n);
        }
        foreach (var c in checks)
        {
            c.Checked += (_, _) => UpdatePrimaryButton();
            c.Unchecked += (_, _) => UpdatePrimaryButton();
        }
        UpdatePrimaryButton();

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null; // Cancelar → aborta el análisis

        // Cerrar los navegadores tildados (cierre suave, Kill solo si no responde)
        // y devolver los que siguen abiertos (no se marcaron).
        var stillOpen = new List<BrowserCleanupInfo>();
        for (int i = 0; i < running.Count; i++)
        {
            var info = running[i];
            if (checks[i].IsChecked != true)
            {
                stillOpen.Add(info);
                continue;
            }
            try
            {
                foreach (var p in Process.GetProcessesByName(info.ProcessName))
                {
                    using (p)
                    {
                        if (!p.HasExited && !p.CloseMainWindow())
                            p.Kill(entireProcessTree: true);
                    }
                }
            }
            catch { }
        }
        return stillOpen;
    }

    /// <summary>
    /// Pone el tamaño escaneado en el TextBlock correspondiente a un ítem de navegador.
    /// </summary>
    private void SetChequeoSize(string browserId, int subItemIdx, long bytes)
    {
        var subItem = (BrowserSubItem)subItemIdx;
        foreach (var c in _chequeoItems)
        {
            if (c.BrowserId == browserId && c.SubItem == subItem)
            {
                c.Size.Text = FormatBytes(bytes);
                return;
            }
        }
    }

    // =====================================================================
    // Limpieza personalizada
    // =====================================================================

    /// <summary>
    /// Ícono del badge de cada categoría de limpieza personalizada.
    /// </summary>
    private static string CustomCategoryGlyph(string id) => id switch
    {
        "sistema" => "\uE7C3",    // DesktopLocal: sistema
        "multimedia" => "\uE8B9", // Picture: miniaturas/imágenes
        "utilidades" => "\uE81C", // History: recientes/historial
        "descargas" => "\uE896",  // Download: descargas de Windows
        "avanzado" => "\uE72E",   // Lock: bajo nivel, solo usuarios que saben
        _ => "\uE7C3"
    };

    /// <summary>
    /// Reconstruye las opciones agrupadas del panel de inicio y el contenido
    /// (si ya hubo un análisis) según la selección actual de grupos.
    /// </summary>
    private void RebuildCustomCategories()
    {
        _customScanned = false;
        RebuildCustomGroupChecks();

        if (_customSelection.Count > 0)
        {
            // Ya se analizó: volver a mostrar el contenido con los mismos grupos
            // (p. ej. al cambiar de idioma).
            CustomStartPanel.Visibility = Visibility.Collapsed;
            BuildCustomCategoryCards(_customSelection);
            CustomContentPanel.Visibility = Visibility.Visible;
            CustomProgressPanel.Visibility = Visibility.Collapsed;
            UpdateCustomCleanEnabled();
            return;
        }

        // Primer estado: panel de inicio con opciones agrupadas (estilo CCleaner).
        CustomStartPanel.Visibility = Visibility.Visible;
        CustomContentPanel.Visibility = Visibility.Collapsed;
        CustomProgressPanel.Visibility = Visibility.Collapsed;
        UpdateCustomCleanEnabled();
    }

    /// <summary>Construye los checkboxes de grupos del panel de inicio (categoría.Id → CheckBox).</summary>
    private void RebuildCustomGroupChecks()
    {
        CustomGroupChecksHost.Children.Clear();
        _customGroupChecks.Clear();
        foreach (var category in _cleanup.GetCustomCategories())
        {
            var cb = new CheckBox
            {
                Content = I18n.T(category.Name),
                MinHeight = 28,
                IsChecked = true
            };
            ToolTipService.SetToolTip(cb, I18n.T(category.Description));
            CustomGroupChecksHost.Children.Add(cb);
            _customGroupChecks[category.Id] = cb;
        }
    }

    /// <summary>
    /// Construye en CategoriesHost las cards de los grupos indicados (con sus
    /// filas individuales, desmarcables una por una).
    /// </summary>
    private void BuildCustomCategoryCards(IReadOnlyCollection<string> groupIds)
    {
        CategoriesHost.Children.Clear();
        _custom.Clear();
        _customCategoryTotals.Clear();
        _customTargetCategory.Clear();

        foreach (var category in _cleanup.GetCustomCategories())
        {
            if (!groupIds.Contains(category.Id)) continue;
            BuildCustomCategoryCard(category);
        }
    }

    /// <summary>Construye la card expandida de una categoría con sus filas individuales.</summary>
    private void BuildCustomCategoryCard(CleanupCategoryInfo category)
    {
        var card = new Border
        {
            Background = ThemeBrushes.Get("CardBackgroundBrush"),
            BorderBrush = ThemeBrushes.Get("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var stack = new StackPanel { Spacing = 8 };
        card.Child = stack;

        // Cabecera de categoría: badge con ícono + título + total acumulado.
        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };
        var badge = new Border
        {
            Width = 26, Height = 26,
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrushes.Get("AccentTintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = CustomCategoryGlyph(category.Id),
                FontSize = 12,
                Foreground = ThemeBrushes.Get("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var title = new TextBlock
        {
            Text = I18n.T(category.Name),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var catTotal = new TextBlock
        {
            Text = "—",
            FontSize = 12,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(badge);
        header.Children.Add(title);
        Grid.SetColumn(title, 1);
        header.Children.Add(catTotal);
        Grid.SetColumn(catTotal, 2);
        stack.Children.Add(header);
        _customCategoryTotals[category.Id] = catTotal;

        var desc = new TextBlock
        {
            Text = I18n.T(category.Description),
            FontSize = 11.5,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        stack.Children.Add(desc);

        foreach (var target in category.Targets)
        {
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                ColumnSpacing = 12
            };
            var cb = new CheckBox
            {
                Content = I18n.T(target.Name) + (target.AnalysisOnly ? " (" + I18n.T("solo análisis") + ")" : ""),
                MinHeight = 28,
                IsChecked = target.DefaultChecked
            };
            cb.Checked += OnCustomCheckChanged;
            cb.Unchecked += OnCustomCheckChanged;
            ToolTipService.SetToolTip(cb, I18n.T(target.Description));
            var size = new TextBlock
            {
                Text = "—",
                FontSize = 12,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 64,
                TextAlignment = TextAlignment.Right
            };
            row.Children.Add(cb);
            Grid.SetColumn(size, 1);
            row.Children.Add(size);
            stack.Children.Add(row);
            _custom.Add(new CustomUi { Target = target, Check = cb, Size = size });
            _customTargetCategory[target.Id] = category.Id;
        }

        CategoriesHost.Children.Add(card);
    }

    private void OnCustomCheckChanged(object sender, RoutedEventArgs e) => UpdateCustomCleanEnabled();

    private List<CustomUi> SelectedCustom() => _custom.Where(c => c.Check.IsChecked == true).ToList();

    /// <summary>
    /// Botón "Analizar" del panel de inicio: arma el contenido con los grupos
    /// marcados en las opciones y escanea. Mide todo lo del grupo; los checks
    /// individuales deciden qué se limpia.
    /// </summary>
    private async void CustomStartButton_Click(object sender, RoutedEventArgs e)
    {
        CustomStartFeedback.Visibility = Visibility.Collapsed;
        var selected = _customGroupChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        if (selected.Count == 0)
        {
            Feedback.Warning(CustomStartFeedback, "Elegí al menos un grupo de carpetas para analizar.");
            return;
        }

        _customSelection = selected.ToHashSet(StringComparer.Ordinal);
        CustomStartPanel.Visibility = Visibility.Collapsed;
        BuildCustomCategoryCards(selected);
        await RunCustomAnalysisAsync(_custom);
    }

    /// <summary>
    /// Escanea los targets indicados con velocímetro de progreso y muestra los
    /// resultados en las filas (aunque el checkbox esté desmarcado, como en el
    /// Chequeo: tildar solo decide qué se limpia).
    /// </summary>
    private async Task RunCustomAnalysisAsync(IReadOnlyCollection<CustomUi> toScan)
    {
        _customScanCts?.Cancel();
        _customScanCts = new CancellationTokenSource();
        var ct = _customScanCts.Token;

        SetBusy(false);
        CustomFeedbackText.Visibility = Visibility.Collapsed;
        CustomContentPanel.Visibility = Visibility.Collapsed;

        var chunks = toScan.Chunk(4).ToArray();
        int steps = chunks.Length;
        int step = 0;

        foreach (var c in toScan) c.Size.Text = "…";
        foreach (var t in _customCategoryTotals.Values) t.Text = "…";

        // Velocímetro de progreso: mismo flujo que el Chequeo.
        CustomRing.Progress = 0;
        CustomRing.Value = "0%";
        UpdateCustomProgress(2, I18n.T("Preparando..."));
        CustomProgressPanel.Visibility = Visibility.Visible;

        try
        {
            var sw = Stopwatch.StartNew();
            var detalles = new List<string>();
            long total = 0;
            int warnings = 0;
            var catBytes = new Dictionary<string, long>();
            var catEntries = new Dictionary<string, long>();

            for (int i = 0; i < chunks.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                step++;
                UpdateCustomProgress(
                    5 + (int)(90.0 * step / steps),
                    I18n.T("Analizando {0}...", I18n.T(chunks[i][0].Target.Name)));

                var cb = await _cleanup.ScanCustomAsync(chunks[i].Select(c => c.Target.Id).ToList(), null, ct);
                total += cb.TotalBytes;
                warnings += cb.Warnings.Count;
                foreach (var item in cb.Items)
                {
                    detalles.Add($"{item.Id}={(item.FileCount > 0 && item.Bytes == 0 ? $"{item.FileCount} entradas" : FormatBytes(item.Bytes))}");

                    // Actualizar el tamaño aunque el checkbox esté desmarcado:
                    // el análisis mide todo, el check solo decide qué se limpia.
                    var ci = _custom.FirstOrDefault(s => s.Target.Id == item.Id);
                    if (ci == null) continue;
                    ci.Size.Text = item.FileCount > 0 && item.Bytes == 0
                        ? I18n.T("{0} entradas", item.FileCount)
                        : FormatBytes(item.Bytes);

                    // Acumular el total de la categoría que contiene este ítem.
                    if (_customTargetCategory.TryGetValue(item.Id, out var catId))
                    {
                        catBytes.TryGetValue(catId, out var bytes);
                        catBytes[catId] = bytes + item.Bytes;
                        catEntries.TryGetValue(catId, out var entries);
                        catEntries[catId] = entries + item.FileCount;
                    }
                }
            }

            // Resultado al log de desarrollo para debugueo (se escribe solo si
            // "Logs de desarrollo" está activado en Configuración).
            _logging.LogDebug(
                $"[Personalizada] Análisis manual completado | elementos={detalles.Count} | total={FormatBytes(total)}" +
                $" | advertencias={warnings} | duración={sw.Elapsed.TotalSeconds:F1} s");
            if (detalles.Count > 0)
                _logging.LogDebug("[Personalizada] Detalle: " + string.Join(", ", detalles));

            _customScanned = true;
            CustomTotals.Text = I18n.T("Total: {0}", FormatBytes(total));
            CustomTotals.Visibility = Visibility.Visible;

            // Totales por categoría: tamaño si hay bytes; si no, cantidad de entradas (registro/análisis).
            foreach (var (catId, tb) in _customCategoryTotals)
            {
                if (!catBytes.TryGetValue(catId, out var bytes))
                {
                    tb.Text = "—";
                    continue;
                }
                if (bytes > 0)
                    tb.Text = FormatBytes(bytes);
                else if (catEntries.TryGetValue(catId, out var entries) && entries > 0)
                    tb.Text = I18n.T("{0} entradas", entries);
                else
                    tb.Text = "—";
            }

            UpdateCustomProgress(100, I18n.T("¡Análisis completado!"));
            await Task.Delay(300, ct);

            CustomProgressPanel.Visibility = Visibility.Collapsed;
            CustomContentPanel.Visibility = Visibility.Visible;
            UpdateCustomCleanEnabled();
            if (warnings > 0)
                Feedback.Warning(CustomFeedbackText, "Algunas carpetas no se pudieron leer (están en uso o sin permisos).");
            else
                Feedback.Success(CustomFeedbackText, I18n.T("Análisis completado: {0} encontrados.", FormatBytes(total)));
        }
        catch (OperationCanceledException)
        {
            CustomProgressPanel.Visibility = Visibility.Collapsed;
            CustomContentPanel.Visibility = Visibility.Visible;
            CustomTotals.Visibility = Visibility.Collapsed;
            CustomFeedbackText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"LimpiezaPage: analizar personalizado: {ex.Message}");
            CustomProgressPanel.Visibility = Visibility.Collapsed;
            CustomContentPanel.Visibility = Visibility.Visible;
            CustomFeedbackText.Visibility = Visibility.Visible;
            Feedback.Error(CustomFeedbackText, I18n.T("No se pudo completar el análisis: {0}", ex.Message));
        }
        finally
        {
            SetBusy(true);
        }
    }

    private async void CleanCustomButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedCustom();
        if (selected.Count == 0) return;

        if (XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Limpiar elementos"),
            Content = I18n.T("Se borrará lo marcado ({0} elementos seleccionados). Esta acción no se puede deshacer.", selected.Count),
            PrimaryButtonText = I18n.T("Limpiar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(false);
        Feedback.Running(CustomFeedbackText, "Limpiando...", persistent: true);
        try
        {
            var sw = Stopwatch.StartNew();
            long total = 0;
            long warnings = 0;
            var limpiados = new List<string>();

            foreach (var chunk in selected.Chunk(6))
            {
                var result = await _cleanup.CleanCustomAsync(chunk.Select(c => c.Target.Id).ToList());
                total += result.TotalBytes;
                warnings += result.Warnings.Count;
                foreach (var item in result.Items)
                {
                    limpiados.Add(item.Id);
                    var ui = selected.FirstOrDefault(s => s.Target.Id == item.Id);
                    if (ui == null) continue;
                    ui.Size.Text = item.AnalysisOnly ? I18n.T("solo análisis") : "—";
                }
            }

            // Los totales por categoría ya no aplican: quedó todo en "—".
            foreach (var tb in _customCategoryTotals.Values) tb.Text = "—";

            // Resultado al log de desarrollo (se escribe solo si "Logs de desarrollo"
            // está activado en Configuración).
            _logging.LogDebug(
                $"[Personalizada] Limpieza completada | elementos={limpiados.Count} | liberado={FormatBytes(total)}" +
                $" | advertencias={warnings} | duración={sw.Elapsed.TotalSeconds:F1} s");
            if (limpiados.Count > 0)
                _logging.LogDebug("[Personalizada] Limpieza detalle: " + string.Join(", ", limpiados));

            CustomTotals.Text = I18n.T("Limpieza completada: {0} liberados.", FormatBytes(total));
            CustomTotals.Visibility = Visibility.Visible;
            UpdateCustomCleanEnabled();
            if (warnings > 0)
                Feedback.Warning(CustomFeedbackText, I18n.T("Limpieza completada: {0} liberados. Hay archivos en uso que no se borraron.", FormatBytes(total)));
            else
                Feedback.Success(CustomFeedbackText, I18n.T("Limpieza completada: {0} liberados.", FormatBytes(total)));
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"LimpiezaPage: limpiar personalizado: {ex.Message}");
            Feedback.Error(CustomFeedbackText, I18n.T("No se pudo completar la limpieza: {0}", ex.Message));
        }
        finally
        {
            SetBusy(true);
        }
    }

    // =====================================================================
    // Buscador de duplicados
    // =====================================================================

    private CancellationTokenSource? _dupCts;

    /// <summary>
    /// Llena el ComboBox con todas las unidades del sistema (fijas, extraíbles, red)
    /// como se ven en "Este equipo" del Explorador de Windows.
    /// <paramref name="selectDrive"/>: raíz de la unidad a seleccionar (ej: "E:\"
    /// cuando se la enchufa en caliente); si es null se restaura la selección actual
    /// y, si nunca hubo, queda C: por defecto.
    /// </summary>
    private void PopulateDrivePicker(string? selectDrive = null)
    {
        // Recordar qué había elegido el usuario para restaurarlo tras el refresco
        // (puede ser una unidad o una carpeta elegida con "Examinar carpeta...").
        string? previousPath = selectDrive;
        if (previousPath == null && !string.IsNullOrWhiteSpace(DupPathBox.Text))
            previousPath = DupPathBox.Text;

        DupDrivePicker.Items.Clear();
        bool selected = false;

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                // Omitir CD-ROM / DVD (no se pueden escanear).
                if (drive.DriveType == DriveType.CDRom) continue;

                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? I18n.T("Disco local")
                    : drive.VolumeLabel;
                string type = drive.DriveType switch
                {
                    DriveType.Fixed => "",                // no hace falta aclarar
                    DriveType.Removable => " (USB)",
                    DriveType.Network => I18n.T(" (Red)"),
                    _ => ""
                };
                string free = I18n.T("{0} libre", FormatBytes(drive.AvailableFreeSpace));

                // Formato: "C: (Windows) — 120 GB libre"
                string display = drive.DriveType == DriveType.Fixed
                    ? $"{drive.Name} ({label}) — {free}"
                    : $"{drive.Name} {label}{type} — {free}";

                DupDrivePicker.Items.Add(display);

                // Restaurar la selección si esta unidad es la que estaba elegida
                // (o la que acaba de enchufarse).
                var root = drive.RootDirectory.FullName;
                if (!selected && previousPath != null &&
                    string.Equals(root, previousPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    DupDrivePicker.SelectedIndex = DupDrivePicker.Items.Count - 1;
                    DupPathBox.Text = root;
                    selected = true;
                }
            }
        }
        catch { /* sin permisos para enumerar unidades */ }

        // La carpeta previa era personalizada (no una unidad): mostrarla como ítem
        // temporal, igual que hace el flujo de "Examinar carpeta...".
        if (!selected && previousPath != null && Directory.Exists(previousPath))
        {
            int idx = DupDrivePicker.Items.Count; // antes de "Examinar carpeta..."
            DupDrivePicker.Items.Insert(idx, $"📂  {previousPath}");
            DupDrivePicker.SelectedIndex = idx;
            DupPathBox.Text = previousPath;
            selected = true;
        }

        // Sin nada que restaurar (primera vez): C: por defecto.
        if (!selected)
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady && drive.Name.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
                    {
                        for (int i = 0; i < DupDrivePicker.Items.Count; i++)
                        {
                            if (((string)DupDrivePicker.Items[i]).StartsWith("C:", StringComparison.OrdinalIgnoreCase))
                            {
                                DupDrivePicker.SelectedIndex = i;
                                DupPathBox.Text = drive.RootDirectory.FullName;
                                break;
                            }
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        // Ítem especial: "Examinar carpeta..." (abre el diálogo nativo).
        DupDrivePicker.Items.Add($"📂  {I18n.T("Examinar carpeta...")}");
    }

    /// <summary>
    /// Se enchufó una unidad nueva (pendrive / disco externo): refrescar la lista
    /// de unidades del buscador de duplicados dejando la nueva seleccionada.
    /// El evento llega desde un thread de WMI, así que se marshalea al hilo de UI.
    /// </summary>
    private void OnDriveArrived(string driveRoot)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Por si llegó un evento viejo o duplicado: la unidad tiene que seguir.
                if (!Directory.Exists(driveRoot)) return;

                PopulateDrivePicker(driveRoot);
                Feedback.Info(DupFeedbackText, I18n.T("Nueva unidad detectada: {0}", driveRoot), persistent: true);
                _logging.LogInfo($"LimpiezaPage: unidad nueva detectada ({driveRoot}), lista actualizada.");
            }
            catch { }
        });
    }

    /// <summary>
    /// Al seleccionar un elemento del ComboBox: si es un disco, guarda su raíz;
    /// si es "Examinar carpeta...", abre el diálogo nativo de Windows.
    /// </summary>
    private async void DupDrivePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DupDrivePicker.SelectedIndex < 0) return;

        var selected = DupDrivePicker.SelectedItem as string;
        if (selected == null) return;

        // ¿Es el ítem "Examinar carpeta..."? (comparado traducido: el ítem se
        // agrega con I18n.T en PopulateDrivePicker).
        if (selected.Contains(I18n.T("Examinar carpeta...")))
        {
            // Restaurar selección anterior para que el ComboBox no quede en blanco.
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(50); // dejar que se cierre el dropdown
                var path = BrowseForFolder(
                    I18n.T("Elegí una carpeta para buscar duplicados"),
                    DupPathBox.Text);
                if (!string.IsNullOrEmpty(path))
                {
                    DupPathBox.Text = path;
                    // Mostrar la ruta en el ComboBox como ítem temporal.
                    string display = $"📂  {path}";
                    // Reemplazar el último ítem (Examinar carpeta...) manteniéndolo al final.
                    int last = DupDrivePicker.Items.Count - 1;
                    DupDrivePicker.Items.Insert(last, display);
                    DupDrivePicker.SelectedIndex = last;
                }
                else
                {
                    // Volver al ítem anterior si hay.
                    if (DupDrivePicker.Items.Count > 1)
                        DupDrivePicker.SelectedIndex = DupDrivePicker.Items.Count - 2;
                }
            });
            return;
        }

        // Es un disco: extraer la raíz del texto (ej: "C:\\").
        try
        {
            var root = selected.Split(' ')[0]; // "C:"
            if (root.EndsWith(":"))
            {
                root += "\\";
                if (Directory.Exists(root))
                    DupPathBox.Text = root;
            }
        }
        catch { }
    }

    /// <summary>
    /// Abre el diálogo nativo de Windows para elegir una carpeta.
    /// FileOpenPicker de WinRT no funciona en apps elevadas (admin),
    /// así que se usa FolderBrowserDialog de WinForms.
    /// </summary>
    private string? BrowseForFolder(string description, string? initialPath = null)
    {
        try
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = description,
                SelectedPath = !string.IsNullOrWhiteSpace(initialPath) ? initialPath :
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ShowNewFolderButton = false
            };
            if (App.MainWindowInstance != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
                var owner = WinForms.NativeWindow.FromHandle(hwnd);
                if (dlg.ShowDialog(owner) == WinForms.DialogResult.OK)
                    return dlg.SelectedPath;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Cancela el análisis del Chequeo en curso.</summary>
    private void ChequeoCancelButton_Click(object sender, RoutedEventArgs e)
        => _chequeoCts?.Cancel();

    /// <summary>Cancela el análisis de Limpieza personalizada en curso.</summary>
    private void CustomCancelButton_Click(object sender, RoutedEventArgs e)
        => _customScanCts?.Cancel();

    /// <summary>Cancela el escaneo de duplicados en curso.</summary>
    private void DupCancelButton_Click(object sender, RoutedEventArgs e)
        => _dupCts?.Cancel();

    private async void DupScanButton_Click(object sender, RoutedEventArgs e)
    {
        var dir = DupPathBox.Text;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            Feedback.Warning(DupFeedbackText, "Elegí una carpeta válida antes de escanear.");
            return;
        }

        _dupCts?.Cancel();
        _dupCts = new CancellationTokenSource();
        var ct = _dupCts.Token;
        var sw = Stopwatch.StartNew();

        DupScanButton.IsEnabled = false;
        DupDeleteButton.IsEnabled = false;
        ResetDupResults();
        DupProgressPanel.Visibility = Visibility.Visible;
        DupRing.Progress = 0;
        DupRing.Value = "0%";
        DupProgressText.Text = I18n.T("Enumerando archivos...");
        DupFeedbackText.Visibility = Visibility.Collapsed;

        try
        {
            var progress = new Progress<(double Percent, string Path)>(p =>
            {
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (!string.IsNullOrEmpty(p.Path))
                        DupProgressText.Text = p.Path;
                    if (p.Percent > 0)
                    {
                        DupRing.Progress = p.Percent;
                        DupRing.Value = $"{(int)Math.Round(p.Percent * 100)}%";
                    }
                });
            });

            var result = await _dupFinder.ScanAsync([dir], progress, ct);

            sw.Stop();
            // Volcar el resultado completo al log de desarrollo (app.log solo se
            // escribe si "Logs de desarrollo" está activado en Configuración).
            LogDupScanResult(dir, result, sw.Elapsed);

            DupProgressPanel.Visibility = Visibility.Collapsed;

            if (result.Groups.Count == 0)
            {
                Feedback.Success(DupFeedbackText, "No se encontraron archivos duplicados en esta carpeta.");
                return;
            }

            BuildDupResults(result);
            DupResultsCard.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            DupProgressPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            DupProgressPanel.Visibility = Visibility.Collapsed;
            Feedback.Error(DupFeedbackText, I18n.T("Error al escanear: {0}", ex.Message));
        }
        finally
        {
            DupScanButton.IsEnabled = true;
            UpdateDupDeleteEnabled();
        }
    }

    // ===== Estado de los resultados del buscador de duplicados =====
    // Un grupo renderizado: su card + las casillas de las copias marcables
    // (todas menos la original). El borrado y el refresco de totales trabajan
    // sobre este estado, NO recorriendo el árbol visual.
    private sealed class DupGroupVm
    {
        public required Border Card { get; init; }
        public required long LengthPerFile { get; init; }
        public required List<(string Path, CheckBox Check)> Copies { get; init; }
    }

    private const int DupRenderBatch = 40;   // grupos por tanda para no saturar la UI

    private DuplicateScanResult? _dupResult;                          // resultado completo del último escaneo
    private readonly List<DuplicateGroup> _dupPendingGroups = new();  // aún sin renderizar
    private readonly List<DupGroupVm> _dupGroupUis = new();           // ya renderizados

    /// <summary>
    /// Deja el panel de resultados en cero (nuevo escaneo o fin de todo).
    /// </summary>
    private void ResetDupResults()
    {
        _dupResult = null;
        _dupPendingGroups.Clear();
        _dupGroupUis.Clear();
        DupGroupsHost.Children.Clear();
        DupShowMoreButton.Visibility = Visibility.Collapsed;
        DupResultsCard.Visibility = Visibility.Collapsed;
    }

    private void BuildDupResults(DuplicateScanResult result)
    {
        ResetDupResults();
        if (result.Groups.Count == 0) return;

        _dupResult = result;
        _dupPendingGroups.AddRange(result.Groups);
        DupResultsCard.Visibility = Visibility.Visible;
        RenderNextDupBatch();
    }

    /// <summary>
    /// Renderiza la próxima tanda de grupos pendientes (de a 40) y refresca
    /// el resumen. Así resultados enormes no saturan la UI de entrada.
    /// </summary>
    private void RenderNextDupBatch()
    {
        if (_dupResult == null) return;

        int take = Math.Min(DupRenderBatch, _dupPendingGroups.Count);
        for (int i = 0; i < take; i++)
        {
            var vm = BuildDupGroup(_dupPendingGroups[i]);
            _dupGroupUis.Add(vm);
            DupGroupsHost.Children.Add(vm.Card);
        }
        _dupPendingGroups.RemoveRange(0, take);

        DupShowMoreButton.Visibility = _dupPendingGroups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDupTotals();
    }

    private void DupShowMoreButton_Click(object sender, RoutedEventArgs e) => RenderNextDupBatch();

    /// <summary>
    /// Recalcula resumen y totales a partir de lo que QUEDA
    /// (renderizado + pendiente), así refleja los borrados hechos.
    /// </summary>
    private void UpdateDupTotals()
    {
        if (_dupResult == null) return;

        long bytes = 0;
        int files = 0;
        foreach (var g in _dupPendingGroups)
        {
            bytes += g.Length * (g.Files.Count - 1);
            files += g.Files.Count - 1;
        }
        foreach (var vm in _dupGroupUis)
        {
            bytes += vm.LengthPerFile * vm.Copies.Count;
            files += vm.Copies.Count;
        }

        int totalGroups = _dupGroupUis.Count + _dupPendingGroups.Count;
        string summary = I18n.T("{0} grupos de duplicados encontrados", totalGroups);
        if (_dupPendingGroups.Count > 0)
            summary += I18n.T(" · mostrando {0}", _dupGroupUis.Count);
        DupSummaryText.Text = summary;
        DupDetailText.Text = I18n.T("{0} archivos duplicados · {1} que podés liberar", files, FormatBytes(bytes));
    }

    /// <summary>
    /// Vuelca el resultado del escaneo de duplicados al log de desarrollo.
    /// LoggingService solo escribe a app.log cuando el ajuste "Logs de desarrollo"
    /// está activado; igualmente el detalle se acota a los primeros 100 grupos
    /// para no inflar el archivo en escaneos enormes.
    /// </summary>
    private void LogDupScanResult(string dir, DuplicateScanResult result, TimeSpan elapsed)
    {
        _logging.LogDebug(
            $"[Duplicados] Escaneo completado | carpeta={dir} | archivos escaneados={result.FilesScanned}" +
            $" | grupos={result.Groups.Count} | duplicados={result.TotalDuplicateFiles}" +
            $" | espacio liberable={FormatBytes(result.TotalDuplicateBytes)}" +
            $" | duración={elapsed.TotalMilliseconds:F0} ms");

        const int maxGruposEnLog = 100;
        foreach (var g in result.Groups.Take(maxGruposEnLog))
        {
            var paths = string.Join(" | ", g.Files.Select(f => f.FullPath));
            _logging.LogDebug(
                $"[Duplicados] Grupo {g.Hash[..Math.Min(12, g.Hash.Length)]}…" +
                $" ({g.Files.Count} copias, {FormatBytes(g.Length)} c/u): {paths}");
        }
        if (result.Groups.Count > maxGruposEnLog)
            _logging.LogDebug($"[Duplicados] …{result.Groups.Count - maxGruposEnLog} grupos más omitidos del log.");
    }

    private DupGroupVm BuildDupGroup(DuplicateGroup group)
    {
        var fileName = "";
        try { fileName = Path.GetFileName(group.Files[0].FullPath); } catch { }

        var stack = new StackPanel { Spacing = 6 };

        // Cabecera del grupo: badge con la cantidad de copias + nombre + tamaño por archivo.
        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };
        var badge = new Border
        {
            Background = ThemeBrushes.Get("AccentTintBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "×" + group.Files.Count,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeBrushes.Get("AccentBrush")
            }
        };
        header.Children.Add(badge);
        var nameTb = new TextBlock
        {
            Text = fileName,
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameTb, 1);
        header.Children.Add(nameTb);
        var sizeTb = new TextBlock
        {
            Text = I18n.T("{0} c/u", FormatBytes(group.Length)),
            FontSize = 11.5,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };
        Grid.SetColumn(sizeTb, 2);
        header.Children.Add(sizeTb);
        stack.Children.Add(header);

        // La primera copia es la original: se conserva siempre (no es borrable).
        var original = BuildDupFileRow(
            group.Files[0].FullPath, group.Length, isOriginal: true, check: null);
        stack.Children.Add(original);

        // Las demás copias van con checkbox (marcadas por defecto para borrar).
        var copies = new List<(string Path, CheckBox Check)>();
        for (int i = 1; i < group.Files.Count; i++)
        {
            var cb = new CheckBox { IsChecked = true, Tag = group.Files[i].FullPath, MinHeight = 22 };
            cb.Checked += (_, _) => UpdateDupDeleteEnabled();
            cb.Unchecked += (_, _) => UpdateDupDeleteEnabled();
            copies.Add((group.Files[i].FullPath, cb));
            stack.Children.Add(BuildDupFileRow(group.Files[i].FullPath, group.Length, isOriginal: false, cb));
        }

        var card = new Border
        {
            Background = ThemeBrushes.Get("CardBackgroundBrush"),
            BorderBrush = ThemeBrushes.Get("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Child = stack
        };
        return new DupGroupVm { Card = card, LengthPerFile = group.Length, Copies = copies };
    }

    /// <summary>
    /// Fila de archivo de un grupo de duplicados. La copia ORIGINAL (isOriginal) no
    /// tiene checkbox —nunca se borra— y se marca con un tilde + etiqueta "Original";
    /// las demás llevan checkbox y se tildan por defecto para borrar.
    /// </summary>
    private static Grid BuildDupFileRow(string path, long length, bool isOriginal, CheckBox? check)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 6
        };

        if (isOriginal)
        {
            var icon = new FontIcon
            {
                Glyph = "\uE73E", // Marca de verificación
                FontSize = 12,
                Foreground = ThemeBrushes.Get("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(icon);
            row.Tag = "original";
            var label = new TextBlock
            {
                Text = I18n.T("Original"),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeBrushes.Get("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
        }
        else
        {
            row.Children.Add(check ?? new CheckBox { IsChecked = true, MinHeight = 22 });
            var label = new TextBlock
            {
                Text = path,
                FontSize = 11,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
        }

        var size = new TextBlock
        {
            Text = FormatBytes(length),
            FontSize = 11,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 56,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(size, 2);
        row.Children.Add(size);
        return row;
    }

    /// <summary>
    /// Habilita el botón de borrado según haya copias marcadas y muestra cuántas
    /// están seleccionadas (el conteo va dentro del propio botón del hero).
    /// </summary>
    private void UpdateDupDeleteEnabled()
    {
        int selected = 0;
        foreach (var vm in _dupGroupUis)
            foreach (var (_, check) in vm.Copies)
                if (check.IsChecked == true)
                    selected++;

        DupDeleteButton.Content = selected > 0
            ? I18n.T("Borrar seleccionados ({0})", selected)
            : I18n.T("Borrar seleccionados");
        DupDeleteButton.IsEnabled = selected > 0;
    }

    private void SaveDupRecycleSetting()
    {
        if (_settings == null) return;
        _settings.Set("duplicates.recycleBin", DupRecycleToggle.IsChecked == true);
        _settings.Save();
        _logging.LogDebug($"[Duplicados] Papelera de reciclaje: {DupRecycleToggle.IsChecked == true}");
    }

    private async void DupDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var toDelete = new List<string>();
        foreach (var vm in _dupGroupUis)
            foreach (var (_, check) in vm.Copies)
                if (check.IsChecked == true)
                    toDelete.Add((string)check.Tag!);

        if (toDelete.Count == 0 || XamlRoot == null) return;

        bool toRecycleBin = DupRecycleToggle.IsChecked == true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Borrar duplicados"),
            Content = toRecycleBin
                ? I18n.T("¿Enviar {0} archivos a la papelera de reciclaje?", toDelete.Count)
                : I18n.T("¿Borrar {0} archivos permanentemente? Esta acción no se puede deshacer.", toDelete.Count),
            PrimaryButtonText = I18n.T("Limpiar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        DupDeleteButton.IsEnabled = false;
        try
        {
            // El servicio devuelve SOLO las rutas efectivamente borradas:
            // así la lista refleja la realidad sin necesidad de re-escanear.
            var deletedList = await _dupFinder.DeleteAsync(toDelete, toRecycleBin);
            var deletedSet = new HashSet<string>(deletedList, StringComparer.OrdinalIgnoreCase);

            foreach (var vm in _dupGroupUis.ToList())
            {
                var sp = vm.Card.Child as StackPanel;
                for (int ci = vm.Copies.Count - 1; ci >= 0; ci--)
                {
                    var (path, check) = vm.Copies[ci];
                    if (!deletedSet.Contains(path)) continue;

                    vm.Copies.RemoveAt(ci);
                    if (sp != null && check.Parent is Grid row) sp.Children.Remove(row);
                }

                // Sin copias marcables: quedó solo el original, el grupo se va.
                if (vm.Copies.Count == 0)
                {
                    DupGroupsHost.Children.Remove(vm.Card);
                    _dupGroupUis.Remove(vm);
                }
            }

            int fallados = toDelete.Count - deletedList.Count;
            string msg = I18n.T(
                toRecycleBin ? "{0} archivos enviados a la papelera." : "{0} archivos borrados.", deletedList.Count);
            if (fallados > 0)
                Feedback.Warning(DupFeedbackText,
                    msg + " " + I18n.T("{0} no se pudieron borrar (en uso o sin permisos).", fallados));
            else
                Feedback.Success(DupFeedbackText, msg);

            if (_dupGroupUis.Count == 0 && _dupPendingGroups.Count == 0)
            {
                ResetDupResults();
            }
            else
            {
                UpdateDupTotals();
                UpdateDupDeleteEnabled();
            }

            _logging.LogDebug(
                $"[Duplicados] Borrado: {deletedList.Count}/{toDelete.Count} OK " +
                $"({(toRecycleBin ? "papelera" : "permanente")}, fallados={fallados}).");
        }
        catch (Exception ex)
        {
            Feedback.Error(DupFeedbackText, I18n.T("No se pudo completar el borrado: {0}", ex.Message));
        }
    }

    // =====================================================================
    // Administración de inicio
    // =====================================================================

    private async Task LoadStartupEntriesAsync()
    {
        StartupEntriesHost.Children.Clear();

        // Placeholder mientras carga.
        StartupEntriesHost.Children.Add(new TextBlock
        {
            Text = I18n.T("Cargando entradas de inicio..."),
            FontSize = 12,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            Margin = new Thickness(0, 8, 0, 0)
        });

        // GetEntries puede tardar (schtasks), corre en background.
        var entries = await Task.Run(() => _startupMgr.GetEntries());

        StartupEntriesHost.Children.Clear();

        if (entries.Count == 0)
        {
            StartupEntriesHost.Children.Add(new TextBlock
            {
                Text = I18n.T("No se encontraron entradas de inicio."),
                FontSize = 12,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }

        foreach (var entry in entries)
        {
            var row = BuildStartupRow(entry);
            StartupEntriesHost.Children.Add(row);
        }
    }

    private Grid BuildStartupRow(StartupEntry entry)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },        // Col 0: ícono
                new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) }, // Col 1: nombre + comando
                new ColumnDefinition { Width = new GridLength(0.6, GridUnitType.Star) }  // Col 2: toggle
            },
            ColumnSpacing = 8,
            Margin = new Thickness(0, 2, 0, 2),
            Tag = entry.Id
        };

        // Col 0: ícono de la aplicación
        var iconBadge = new Border
        {
            Width = 28, Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        EnsureStartupIcon(entry, iconBadge);
        row.Children.Add(iconBadge);

        // Col 1: nombre + comando (nombre en negrita, comando abajo)
        var nameStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var nameTb = new TextBlock { Text = entry.Name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var cmdShow = entry.Source == StartupSource.PackagedApp
            ? Path.GetFileName(entry.Command.TrimEnd('\\'))
            : entry.Command;
        var cmdTb = new TextBlock { Text = cmdShow, FontSize = 11, Foreground = ThemeBrushes.Get("SecondaryTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        nameStack.Children.Add(nameTb);
        nameStack.Children.Add(cmdTb);
        Grid.SetColumn(nameStack, 1);
        row.Children.Add(nameStack);

        // Col 2: toggle
        var toggle = new ToggleSwitch { IsOn = entry.IsEnabled, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Center };
        toggle.Toggled += (_, _) => OnStartupToggled(entry.Id, toggle.IsOn);
        Grid.SetColumn(toggle, 2);
        row.Children.Add(toggle);

        return row;
    }

    private void OnStartupToggled(string id, bool enable)
    {
        bool ok = _startupMgr.Toggle(id, enable);
        if (!ok)
            Feedback.Warning(StartupFeedbackText, I18n.T("No se pudo cambiar el estado. Probablemente necesitás permisos de administrador."));
        else
            Feedback.Success(StartupFeedbackText, enable ? "Entrada activada." : "Entrada desactivada.", persistent: false);
    }

    private void StartupRefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadStartupEntriesAsync();

    // =====================================================================
    // Helpers de UI
    // =====================================================================

    private void UpdateChequeoProgress(int percentage, string message)
    {
        int value = Math.Clamp(percentage, 0, 100);
        ChequeoRing.Value = $"{value}%";
        ChequeoRing.Progress = value / 100.0;
        ChequeoProgressText.Text = message;
    }

    private void UpdateCustomProgress(int percentage, string message)
    {
        int value = Math.Clamp(percentage, 0, 100);
        CustomRing.Value = $"{value}%";
        CustomRing.Progress = value / 100.0;
        CustomProgressText.Text = message;
    }

    private void UpdateCustomCleanEnabled()
    {
        int selected = SelectedCustom().Count;
        CleanCustomButton.Content = selected > 0
            ? I18n.T("Limpiar ({0})", selected)
            : I18n.T("Limpiar");
        CleanCustomButton.IsEnabled = selected > 0;
    }

    /// <summary>
    /// Deshabilita/habilita los botones de acción durante una operación.
    /// restore=false (operación en curso) → deshabilitados;
    /// restore=true (operación terminada) → habilitados de nuevo.
    /// </summary>
    private void SetBusy(bool restore)
    {
        ChequeoAnalyzeButton.IsEnabled = restore;
        CleanCustomButton.IsEnabled = restore && SelectedCustom().Count > 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
    }

    private static SolidColorBrush FromHex(string hex)
    {
        try
        {
            return new SolidColorBrush(WinColor.FromArgb(
                255,
                byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber),
                byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber),
                byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber)));
        }
        catch
        {
            return Feedback.AccentBrush;
        }
    }
}



