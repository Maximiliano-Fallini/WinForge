using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private readonly List<CustomUi> _custom = new();
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
            Foreground = (WinBrush)Application.Current.Resources["AccentBrush"],
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
            Foreground = (WinBrush)Application.Current.Resources["AccentBrush"],
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
            Foreground = (WinBrush)Application.Current.Resources["AccentBrush"],
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

        // Velocímetros de progreso: dial con aguja que muestra el % de
        // completado durante el análisis/escaneo. El DupRing es más chico
        // porque comparte fila con el texto de carpeta actual.
        ChequeoRing.Label = "";
        DupRing.ConfigureSize(120);
        DupRing.Label = "";

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

        // Cancelar scans de otras pestañas al cambiar.
        if (idx != 0) { _chequeoCts?.Cancel(); _chequeoCts = null; }
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
            _chequeoItems.Clear();

            ChequeoStartPanel.Visibility = Visibility.Collapsed;
            ChequeoResultsPanel.Visibility = Visibility.Collapsed;
            ChequeoBrowserSection.Visibility = Visibility.Collapsed;
            ChequeoSystemSection.Visibility = Visibility.Collapsed;
            ChequeoAppCacheSection.Visibility = Visibility.Collapsed;
            ChequeoRecycleSection.Visibility = Visibility.Collapsed;
            ChequeoBrowsersHost?.Children.Clear();
            ChequeoSystemHost?.Children.Clear();
            ChequeoAppCacheHost?.Children.Clear();
            ChequeoRecycleHost?.Children.Clear();
            if (ChequeoTotalsText != null) ChequeoTotalsText.Visibility = Visibility.Collapsed;
            if (CleanBrowsersButton != null) CleanBrowsersButton.IsEnabled = false;
            if (ChequeoFeedbackText != null) ChequeoFeedbackText.Visibility = Visibility.Collapsed;

            SetBusy(false);

            // Mostrar anillo de progreso con porcentaje central.
            ChequeoRing.Progress = 0;
            ChequeoRing.Value = "0%";
            UpdateChequeoProgress(2, I18n.T("Preparando..."));
            ChequeoProgressPanel.Visibility = Visibility.Visible;

            IReadOnlyCollection<BrowserSubItem> allItems = [BrowserSubItem.Cache, BrowserSubItem.Cookies, BrowserSubItem.History];
            var sysIds = new[] { "sys_temp", "sys_usertemp", "sys_inetcache", "sys_crashdumps", "sys_wer" };
            var cacheIds = new[] { "mm_thumbs", "mm_iconcache", "mm_wmp" };
            var rbIds = new[] { "sys_recyclebin" };

            // ---- Detectar navegadores en background (evita congelar la UI) ----
            var browsers = await Task.Run(() => _cleanup.GetBrowsers().Where(b => b.IsInstalled).ToList(), ct);

            // Construir cards vacías en la UI (rápido, sin I/O).
            foreach (var info in browsers)
                ChequeoBrowsersHost?.Children.Add(BuildChequeoBrowserCard(info));

            long totalBytes = 0;
            long browserBytes = 0;

            // ---- Paso 1/4: Navegadores (5-30%) ----
            int totalBrowsers = browsers.Count;
            for (int i = 0; i < totalBrowsers; i++)
            {
                ct.ThrowIfCancellationRequested();
                var info = browsers[i];
                UpdateChequeoProgress(5 + (int)(25.0 * i / totalBrowsers), I18n.T("Escaneando {0}...", info.DisplayName));

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
            if (browsers.Count > 0) ChequeoBrowserSection.Visibility = Visibility.Visible;

            // ---- Paso 2/4: Sistema (35-55%) ----
            UpdateChequeoProgress(35, "Archivos temporales del sistema...");
            var sysResult = await _cleanup.ScanCustomAsync(sysIds, null, ct);
            totalBytes += sysResult.TotalBytes;
            foreach (var item in sysResult.Items)
                AddChequeoRow(ChequeoSystemHost!, item.Id, item.Name, item.Bytes, item.FileCount, item.AnalysisOnly);
            ChequeoSystemSection.Visibility = Visibility.Visible;
            UpdateChequeoProgress(55, I18n.T("Archivos temporales del sistema") + " ✓");

            // ---- Paso 3/4: Caché de aplicaciones (60-75%) ----
            UpdateChequeoProgress(60, I18n.T("Escaneando {0}...", I18n.T("Memoria caché de aplicaciones")));
            var cacheResult = await _cleanup.ScanCustomAsync(cacheIds, null, ct);
            totalBytes += cacheResult.TotalBytes;
            foreach (var item in cacheResult.Items)
                AddChequeoRow(ChequeoAppCacheHost!, item.Id, item.Name, item.Bytes, item.FileCount, item.AnalysisOnly);
            ChequeoAppCacheSection.Visibility = Visibility.Visible;
            UpdateChequeoProgress(75, I18n.T("Memoria caché de aplicaciones") + " ✓");

            // ---- Paso 4/4: Papelera (80-95%) ----
            UpdateChequeoProgress(80, I18n.T("Escaneando {0}...", I18n.T("Papelera de reciclaje")));
            var rbResult = await _cleanup.ScanCustomAsync(rbIds, null, ct);
            totalBytes += rbResult.TotalBytes;
            foreach (var item in rbResult.Items)
                AddChequeoRow(ChequeoRecycleHost!, item.Id, item.Name, item.Bytes, item.FileCount, item.AnalysisOnly);
            ChequeoRecycleSection.Visibility = Visibility.Visible;

            // ---- Terminado ----
            // Volcar el resultado del análisis al log de desarrollo para debugueo
            // (app.log solo se escribe si "Logs de desarrollo" está activado).
            _logging.LogDebug(
                "[Chequeo] Análisis completado | navegadores=" + FormatBytes(browserBytes) +
                ", sistema=" + FormatBytes(sysResult.TotalBytes) +
                ", caché de aplicaciones=" + FormatBytes(cacheResult.TotalBytes) +
                ", papelera=" + FormatBytes(rbResult.TotalBytes) +
                " | total=" + FormatBytes(totalBytes) +
                $" | duración={sw.Elapsed.TotalSeconds:F1} s");
            UpdateChequeoProgress(100, I18n.T("¡Análisis completado!"));
            await Task.Delay(300, ct);

            ChequeoProgressPanel.Visibility = Visibility.Collapsed;
            ChequeoResultsPanel.Visibility = Visibility.Visible;
            if (ChequeoTotalsText != null)
            {
                ChequeoTotalsText.Text = I18n.T("Total: {0}", FormatBytes(totalBytes));
                ChequeoTotalsText.Visibility = Visibility.Visible;
            }
            if (CleanBrowsersButton != null) CleanBrowsersButton.IsEnabled = totalBytes > 0;
            if (ChequeoFeedbackText != null)
            {
                ChequeoFeedbackText.Visibility = Visibility.Visible;
                if (totalBytes == 0)
                    Feedback.Info(ChequeoFeedbackText, "No se encontraron datos para limpiar.");
                else
                    Feedback.Success(ChequeoFeedbackText, I18n.T("Análisis completado: {0} encontrados.", FormatBytes(totalBytes)));
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
                ChequeoTotalsText.Visibility = Visibility.Collapsed;
                CleanBrowsersButton.IsEnabled = false;
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
            Foreground = (WinBrush)Application.Current.Resources["SecondaryTextBrush"],
            VerticalAlignment = VerticalAlignment.Center
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
    /// </summary>
    private Border BuildChequeoBrowserCard(BrowserCleanupInfo info)
    {
        var card = new Border
        {
            Background = (WinBrush)Application.Current.Resources["CardBackgroundBrush"],
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var stack = new StackPanel { Spacing = 8 };
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
                Foreground = (WinBrush)Application.Current.Resources["AccentBrush"],
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
                Foreground = (WinBrush)Application.Current.Resources["SecondaryTextBrush"],
                VerticalAlignment = VerticalAlignment.Center
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

        return card;
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

    private void RebuildCustomCategories()
    {
        _customScanned = false;
        _custom.Clear();
        CategoriesHost.Children.Clear();

        // Estado inicial: botón central de Analizar, contenido oculto hasta analizar.
        CustomStartPanel.Visibility = Visibility.Visible;
        CustomContentPanel.Visibility = Visibility.Collapsed;

        foreach (var category in _cleanup.GetCustomCategories())
        {
            var card = new Border
            {
                Background = (WinBrush)Application.Current.Resources["CardBackgroundBrush"],
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var stack = new StackPanel { Spacing = 8 };
            card.Child = stack;

            var title = new StackPanel { Spacing = 2 };
            var titleText = new TextBlock
            {
                Text = I18n.T(category.Name),
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            stack.Children.Add(titleText);
            var desc = new TextBlock
            {
                Text = I18n.T(category.Description),
                FontSize = 11.5,
                Foreground = (WinBrush)Application.Current.Resources["SecondaryTextBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            stack.Children.Add(desc);

            foreach (var target in category.Targets)
            {
                var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } }, ColumnSpacing = 12 };
                var cb = new CheckBox
                {
                    Content = I18n.T(target.Name) + (target.AnalysisOnly ? " (" + I18n.T("solo análisis") + ")" : ""),
                    MinHeight = 28,
                    IsChecked = target.DefaultChecked
                };
                cb.Checked += OnCustomCheckChanged;
                cb.Unchecked += OnCustomCheckChanged;
                ToolTipService.SetToolTip(cb, I18n.T(target.Description));
                var size = new TextBlock { Text = "—", FontSize = 12, Foreground = (WinBrush)Application.Current.Resources["SecondaryTextBrush"], VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(cb);
                Grid.SetColumn(size, 1);
                row.Children.Add(size);
                stack.Children.Add(row);
                _custom.Add(new CustomUi { Target = target, Check = cb, Size = size });
            }

            CategoriesHost.Children.Add(card);
        }

        UpdateCustomCleanEnabled();
    }

    private void OnCustomCheckChanged(object sender, RoutedEventArgs e) => UpdateCustomCleanEnabled();

    private List<CustomUi> SelectedCustom() => _custom.Where(c => c.Check.IsChecked == true).ToList();

    /// <summary>
    /// Botón central de "Analizar" (como el de Chequeo): muestra el contenido
    /// (categorías + totales) y lanza el análisis de TODO — como las categorías
    /// recién se ven y vienen todas marcadas por defecto, esto equivale a
    /// analizarlas todas a la vez.
    /// </summary>
    private void CustomStartButton_Click(object sender, RoutedEventArgs e)
    {
        CustomStartPanel.Visibility = Visibility.Collapsed;
        CustomContentPanel.Visibility = Visibility.Visible;
        AnalyzeCustomButton_Click(sender, e);
    }

    private async void AnalyzeCustomButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedCustom();
        if (selected.Count == 0)
        {
            Feedback.Warning(CustomFeedbackText, "Elegí al menos un elemento para analizar.");
            return;
        }

        SetBusy(false);
        Feedback.Running(CustomFeedbackText, "Analizando...", persistent: true);
        foreach (var c in selected) c.Size.Text = "…";

        try
        {
            var sw = Stopwatch.StartNew();
            var detalles = new List<string>();
            long total = 0;
            int warnings = 0;
            foreach (var chunk in selected.Chunk(4))
            {
                var result = await _cleanup.ScanCustomAsync(chunk.Select(c => c.Target.Id).ToList());
                total += result.TotalBytes;
                warnings += result.Warnings.Count;
                foreach (var item in result.Items)
                {
                    detalles.Add($"{item.Id}={(item.AnalysisOnly && item.FileCount > 0 ? $"{item.FileCount} entradas" : FormatBytes(item.Bytes))}");
                    var ui = selected.FirstOrDefault(s => s.Target.Id == item.Id);
                    if (ui == null) continue;
                    ui.Size.Text = item.AnalysisOnly && item.FileCount > 0
                        ? I18n.T("{0} entradas", item.FileCount)
                        : FormatBytes(item.Bytes);
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
            UpdateCustomCleanEnabled();
            if (warnings > 0)
                Feedback.Warning(CustomFeedbackText, "Algunas carpetas no se pudieron leer (están en uso o sin permisos).");
            else
                Feedback.Success(CustomFeedbackText, I18n.T("Análisis completado: {0} encontrados.", FormatBytes(total)));
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"LimpiezaPage: analizar personalizado: {ex.Message}");
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
            long total = 0;
            long warnings = 0;
            foreach (var chunk in selected.Chunk(6))
            {
                var result = await _cleanup.CleanCustomAsync(chunk.Select(c => c.Target.Id).ToList());
                total += result.TotalBytes;
                warnings += result.Warnings.Count;
                foreach (var item in result.Items)
                {
                    var ui = selected.FirstOrDefault(s => s.Target.Id == item.Id);
                    if (ui == null) continue;
                    ui.Size.Text = item.AnalysisOnly ? I18n.T("solo análisis") : "—";
                }
            }
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
                var path = BrowseForFolder();
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
    private string? BrowseForFolder()
    {
        try
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = I18n.T("Elegí una carpeta para buscar duplicados"),
                SelectedPath = !string.IsNullOrWhiteSpace(DupPathBox.Text) ? DupPathBox.Text :
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
        var header = new TextBlock
        {
            Text = I18n.T("☐ {0} ({1} copias, {2} c/u)",
                fileName, group.Files.Count, FormatBytes(group.Length)),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(header);

        // Marcar todas las copias menos la primera (la más vieja) para borrar.
        var copies = new List<(string Path, CheckBox Check)>();
        for (int i = 1; i < group.Files.Count; i++)
        {
            var file = group.Files[i];
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } }, ColumnSpacing = 6 };
            var cb = new CheckBox { IsChecked = true, Tag = file.FullPath, MinHeight = 22 };
            cb.Checked += (_, _) => UpdateDupDeleteEnabled();
            cb.Unchecked += (_, _) => UpdateDupDeleteEnabled();
            copies.Add((file.FullPath, cb));
            var label = new TextBlock
            {
                Text = file.FullPath,
                FontSize = 11,
                Foreground = (WinBrush)Application.Current.Resources["SecondaryTextBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(cb);
            // El texto va en la columna 1 (estrella); sin esto cae en la columna 0
            // y queda superpuesto sobre el checkbox.
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            stack.Children.Add(row);
        }

        var card = new Border
        {
            Background = (WinBrush)Application.Current.Resources["CardBackgroundBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = stack
        };
        return new DupGroupVm { Card = card, LengthPerFile = group.Length, Copies = copies };
    }

    private void UpdateDupDeleteEnabled()
    {
        DupDeleteButton.IsEnabled = _dupGroupUis.Any(vm => vm.Copies.Any(c => c.Check.IsChecked == true));
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
            Foreground = (WinBrush)Application.Current.Resources["SecondaryTextBrush"],
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
                Foreground = (WinBrush)Application.Current.Resources["SecondaryTextBrush"],
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
                new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) }, // Col 2: toggle
                new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) }  // Col 3: botón borrar
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
        var cmdTb = new TextBlock { Text = cmdShow, FontSize = 11, Foreground = (WinBrush)Application.Current.Resources["SecondaryTextBrush"], TextTrimming = TextTrimming.CharacterEllipsis };
        nameStack.Children.Add(nameTb);
        nameStack.Children.Add(cmdTb);
        Grid.SetColumn(nameStack, 1);
        row.Children.Add(nameStack);

        // Col 2: toggle
        var toggle = new ToggleSwitch { IsOn = entry.IsEnabled, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Center };
        toggle.Toggled += (_, _) => OnStartupToggled(entry.Id, toggle.IsOn);
        Grid.SetColumn(toggle, 2);
        row.Children.Add(toggle);

        // Col 3: botón borrar (las apps empaquetadas no se pueden borrar:
        // Windows solo permite habilitarlas/deshabilitarlas, como Task Manager).
        if (entry.Source != StartupSource.PackagedApp)
        {
            var delBtn = new Button
            {
                Content = "✕",
                FontSize = 12,
                Width = 28, Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            delBtn.Click += (_, _) => OnStartupDelete(entry.Id, row);
            Grid.SetColumn(delBtn, 3);
            row.Children.Add(delBtn);
        }

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

    private async void OnStartupDelete(string id, Grid row)
    {
        if (XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Borrar entrada de inicio"),
            Content = I18n.T("¿Borrar esta entrada de inicio? La próxima vez que inicies sesión ya no se ejecutará."),
            PrimaryButtonText = I18n.T("Limpiar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        bool ok = _startupMgr.Delete(id);
        if (ok)
        {
            StartupEntriesHost.Children.Remove(row);
            Feedback.Success(StartupFeedbackText, "Entrada de inicio borrada.");
        }
        else
            Feedback.Warning(StartupFeedbackText, I18n.T("No se pudo borrar la entrada. Probablemente necesitás permisos de administrador."));
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

    private void UpdateCustomCleanEnabled() => CleanCustomButton.IsEnabled = SelectedCustom().Count > 0;

    /// <summary>Deshabilita/habilita los botones de acción durante una operación.</summary>
    private void SetBusy(bool busy)
    {
        ChequeoAnalyzeButton.IsEnabled = !busy;
        AnalyzeCustomButton.IsEnabled = !busy;
        CleanCustomButton.IsEnabled = !busy && SelectedCustom().Count > 0;
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



