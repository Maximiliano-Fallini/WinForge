using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WHPO.Core.Services.Interfaces;
using WinImage = Microsoft.UI.Xaml.Controls.Image;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Administración de inicio (estilo Administrador de tareas de Windows): lista
/// las aplicaciones que arrancan con el sistema y permite activarlas o
/// desactivarlas sin borrarlas. Es un componente propio del hub de
/// Optimizaciones, independiente de la Limpieza del dispositivo.
/// </summary>
public sealed partial class AdministracionInicioPage : Page
{
    private readonly IStartupManagerService _startupMgr;
    private readonly IStartupImpactService _impact;
    private readonly ILoggingService _logging;

    /// <summary>Última medición por Id, para restaurarla al reactivar una entrada.</summary>
    private IReadOnlyDictionary<string, StartupImpact> _impacts =
        new Dictionary<string, StartupImpact>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Badge de impacto de cada fila visible, para refrescarlo sin recargar la lista.</summary>
    private readonly Dictionary<string, (Border Badge, TextBlock Text)> _impactUi =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Evita dos cargas a la vez: Loaded se repite cada vez que se vuelve a la página.</summary>
    private bool _loading;

    public AdministracionInicioPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _startupMgr = App.Services.GetRequiredService<IStartupManagerService>();
        _impact = App.Services.GetRequiredService<IStartupImpactService>();
        _logging = App.Services.GetRequiredService<ILoggingService>();

        // La columna Impacto sale del reporte WDI de Windows: el tooltip de la
        // cabecera explica de dónde viene y qué significan los cortes.
        ToolTipService.SetToolTip(ImpactHeader, new ToolTip
        {
            Content = new TextBlock
            {
                Text = I18n.T("Impacto medido por Windows en el último inicio de sesión. Alto: más de 1 s de CPU o 3 MB de disco; medio: entre 300 ms y 1 s de CPU, o entre 300 KB y 3 MB de disco; bajo: menos que eso."),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320
            }
        });

        // El dial de carga vive DENTRO de la card de la lista, donde un dial de
        // 300 (el del arranque) desbordaría la fila.
        StartupRing.ConfigureSize(150);

        // La página vive en la caché de navegación del hub: al volver se recargan
        // las entradas para no mostrar el estado viejo.
        Loaded += (_, _) => _ = LoadStartupEntriesAsync();
    }

    // ---- Íconos reales de entradas de inicio: extraídos de sus .exe con IconExtractor ----
    private static readonly string StartupIconDir = Path.Combine(
        WHPO.Core.AppPaths.RootDir, "startupicons");
    /// <summary>PNG ya extraído y listo (BitmapImage). La clave es el Id de la entrada.</summary>
    private static readonly Dictionary<string, BitmapImage> StartupIconReady = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"true" = ya se intentó extraer y falló: no reintentar.</summary>
    private static readonly HashSet<string> StartupIconFailed = new(StringComparer.OrdinalIgnoreCase);

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

    private async Task LoadStartupEntriesAsync()
    {
        if (_loading) return;
        _loading = true;

        StartupEntriesHost.Children.Clear();

        // Mientras carga: el velocímetro del componente, indeterminado (no hay
        // avance que medir todavía). Se apaga siempre al terminar —incluso si
        // GetEntries falla— para no dejar el barrido corriendo de por vida.
        StartupLoadingPanel.Visibility = Visibility.Visible;
        StartupRing.IsIndeterminate = true;

        IReadOnlyList<StartupEntry> entries;
        try
        {
            // GetEntries puede tardar (schtasks) y el impacto lee el XML de WDI:
            // las dos cosas corren en background.
            entries = await Task.Run(() => _startupMgr.GetEntries());
            _impacts = await Task.Run(() => _impact.GetImpacts(entries));
        }
        finally
        {
            _loading = false;
            StartupRing.IsIndeterminate = false;
            StartupLoadingPanel.Visibility = Visibility.Collapsed;
        }

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

        _impactUi.Clear();
        foreach (var entry in entries)
        {
            if (!_impacts.TryGetValue(entry.Id, out var impact))
                impact = entry.IsEnabled ? StartupImpact.Unknown : StartupImpact.Disabled;

            var row = BuildStartupRow(entry, impact);
            StartupEntriesHost.Children.Add(row);
        }
    }

    private Grid BuildStartupRow(StartupEntry entry, StartupImpact impact)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },        // Col 0: ícono
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },  // Col 1: nombre + comando
                new ColumnDefinition { Width = new GridLength(84) },     // Col 2: impacto
                // 150: el ToggleSwitch trae su etiqueta "Activado/Desactivado" al
                // lado del interruptor y con 76 px se cortaba.
                new ColumnDefinition { Width = new GridLength(150) }     // Col 3: toggle
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

        // Col 2: impacto (mismo criterio que el Administrador de tareas)
        var impactBadge = BuildImpactBadge(entry, impact);
        Grid.SetColumn(impactBadge.Badge, 2);
        row.Children.Add(impactBadge.Badge);
        _impactUi[entry.Id] = (impactBadge.Badge, impactBadge.Text);

        // Col 3: toggle. FontSize 12 = el de la fila: con el 14 heredado, la
        // etiqueta "Desactivado" no entraba en la columna y se cortaba.
        var toggle = new ToggleSwitch { IsOn = entry.IsEnabled, MinWidth = 0, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        toggle.Toggled += (_, _) => OnStartupToggled(entry.Id, toggle.IsOn);
        Grid.SetColumn(toggle, 3);
        row.Children.Add(toggle);

        return row;
    }

    private void OnStartupToggled(string id, bool enable)
    {
        bool ok = _startupMgr.Toggle(id, enable);
        if (!ok)
        {
            Feedback.Warning(StartupFeedbackText, I18n.T("No se pudo cambiar el estado. Probablemente necesitás permisos de administrador."));
            return;
        }

        // Desactivada no tiene impacto; al reactivarla vuelve la última medición
        // de Windows (si la hay).
        if (_impactUi.TryGetValue(id, out var ui))
        {
            var impact = !enable
                ? StartupImpact.Disabled
                : _impacts.TryGetValue(id, out var measured) ? measured : StartupImpact.Unknown;
            ApplyImpact(ui.Badge, ui.Text, impact);
        }

        Feedback.Success(StartupFeedbackText, enable ? "Entrada activada." : "Entrada desactivada.", persistent: false);
    }

    /// <summary>
    /// Badge de la columna Impacto. El color sigue al nivel y el tooltip muestra
    /// los números crudos de Windows (CPU y disco medidos durante el arranque).
    /// </summary>
    private static (Border Badge, TextBlock Text) BuildImpactBadge(StartupEntry entry, StartupImpact impact)
    {
        var text = new TextBlock
        {
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var badge = new Border
        {
            Width = 74,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = text
        };
        ApplyImpact(badge, text, impact);
        return (badge, text);
    }

    private static void ApplyImpact(Border badge, TextBlock text, StartupImpact impact)
    {
        var (label, color) = ImpactStyle(impact.Level);
        text.Text = I18n.T(label);
        text.Foreground = new SolidColorBrush(color);

        // Fondo tenue del mismo color: se lee como etiqueta sin gritar.
        badge.Background = new SolidColorBrush(color) { Opacity = 0.14 };
        badge.BorderBrush = new SolidColorBrush(color) { Opacity = 0.35 };
        ToolTipService.SetToolTip(badge, BuildImpactToolTip(impact));
    }

    private static (string Label, Windows.UI.Color Color) ImpactStyle(StartupImpactLevel level)
    {
        var muted = ThemeBrushes.Get("SecondaryTextBrush").Color;
        return level switch
        {
            StartupImpactLevel.High => ("Alto", Feedback.ErrorBrush.Color),
            StartupImpactLevel.Medium => ("Medio", Feedback.WarningBrush.Color),
            StartupImpactLevel.Low => ("Bajo", Feedback.SuccessBrush.Color),
            StartupImpactLevel.None => ("Ninguno", muted),
            _ => ("Sin medir", muted)
        };
    }

    /// <summary>
    /// Por qué la entrada tiene ese estado: el motivo del nivel, los números
    /// reales cuando hubo medición y de dónde salen.
    /// </summary>
    private static ToolTip BuildImpactToolTip(StartupImpact impact)
    {
        var reason = impact.Level switch
        {
            StartupImpactLevel.None =>
                I18n.T("Ninguno: la entrada está desactivada, así que no se ejecuta al iniciar sesión y no tiene impacto."),
            StartupImpactLevel.NotMeasured =>
                I18n.T("Sin medir: la entrada está habilitada, pero Windows no registró su arranque en el último inicio de sesión. Suele pasar si la activaste después de ese inicio o si el diagnóstico de arranque está desactivado."),
            StartupImpactLevel.High =>
                I18n.T("Alto: usó más de 1 s de CPU o más de 3 MB de disco al iniciar sesión."),
            StartupImpactLevel.Medium =>
                I18n.T("Medio: usó entre 300 ms y 1 s de CPU, o entre 300 KB y 3 MB de disco al iniciar sesión."),
            _ =>
                I18n.T("Bajo: usó menos de 300 ms de CPU y menos de 300 KB de disco al iniciar sesión.")
        };

        if (impact.HasMeasurement)
        {
            var cpu = impact.CpuTime.TotalMilliseconds < 1000
                ? $"{impact.CpuTime.TotalMilliseconds:0} ms"
                : $"{impact.CpuTime.TotalSeconds:0.0} s";

            reason += "\n" + I18n.T("CPU al iniciar: {0} · Disco: {1}", cpu, FormatDisk(impact.DiskBytes));
            reason += "\n" + I18n.T("Medido por Windows en el último inicio de sesión.");
        }

        return new ToolTip
        {
            Content = new TextBlock
            {
                Text = reason,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340
            }
        };
    }

    private static string FormatDisk(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB"
    };

    private void StartupRefreshButton_Click(object sender, RoutedEventArgs e) => _ = LoadStartupEntriesAsync();
}
