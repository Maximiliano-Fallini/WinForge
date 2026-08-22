using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Services;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Página "Biblioteca de juegos" (estilo Process Lasso): biblioteca de videojuegos
/// instalados (Steam/Epic/Ubisoft/EA/Blizzard…) en una grilla de 3 o 5 columnas (vista
/// cambiable en la cabecera) con el
/// banner del juego, favoritos con estrella y reglas por juego (prioridad de CPU,
/// afinidad, prioridad de GPU y plan de energía). Cada ajuste admite dos alcances:
/// "Actual" (solo la apertura actual del juego, sin guardar) y "Siempre" (permanente,
/// se guarda en el registro). No muestra procesos del sistema: solo juegos.
/// </summary>
public sealed partial class GestionarProcesosPage : Page
{
    private readonly IProcessService _processService;
    private readonly IInstalledGamesService _installedGamesService;
    private readonly ICpuPowerService _cpuPowerService;
    private readonly ILoggingService _loggingService;
    private readonly IGameBoostService _gameBoostService;
    // Evita que el Toggled se dispare al cargar el estado inicial del switch.
    private bool _boostSwitchInitialized;
    // Amarillo del badge "(BETA)" (color fijo deliberado: no depende del tema).
    // Se guarda el Color (struct, seguro en un campo estático) y el SolidColorBrush se
    // crea al usarlo, en el hilo de la UI: instanciar un objeto XAML en un campo
    // estático corre en el .cctor y lanza RPC_E_WRONG_THREAD (0x8001010E).
    private static readonly Windows.UI.Color BetaColor = Windows.UI.Color.FromArgb(255, 255, 212, 0);
    // Cantidad de columnas de la grilla de juegos (fija en 3: la vista de 5 ya no
    // existe desde que se quitaron los botones de cambio de vista).
    private const int GridColumns = 3;
    // Búsqueda por nombre (filtro de la grilla); vacío = mostrar todo.
    private string _searchQuery = "";

    private List<InstalledGame> _installed = new();
    private List<(string Exe, string? Name, string? InstallPath)> _manual = new();
    private HashSet<string> _runningExes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Border card, Border banner)> _cards = new();
    // Contenedor del skeleton (filas de cards placeholder): se agrega/remueve del
    // LibraryPanel. El render de la biblioteca usa filas de StackPanel puros (3 por
    // fila), no ItemsWrapGrid, para garantizar el layout en cualquier contexto.
    private StackPanel? _skeletonPanel;
    // Botones "Iniciar" de todas las cards, por exe: el estado de ejecución se
    // refleja en el propio botón (sin badge aparte) y se actualiza con los eventos
    // WMI (RunningGamesChanged), sin reconstruir la grilla. CanLaunch = se puede
    // lanzar (exe o launcher encontrado); si el juego está corriendo, el botón
    // muestra "En ejecución" y queda deshabilitado (no se puede relanzar).
    private readonly List<(string Exe, Button Btn, bool CanLaunch)> _gameLaunchButtons = new();
    private bool _counterStatusVisible;
    // Skeleton de carga: cards placeholder que se agregan al MISMO panel de la
    // biblioteca (como los juegos reales) mientras se escanea. El pulso replica el
    // patrón de SistemaPage: un Storyboard suave (1.0 → 0.35 en 900 ms, auto-reverse)
    // por bloque de cada card, y un mínimo visible para que el efecto se aprecie.
    private readonly List<(Border Card, Border Banner, Border[] Blocks)> _skeletonCards = new();
    private readonly List<Storyboard> _skeletonStoryboards = new();
    private bool _skeletonActive;
    private long _skeletonShownAtMs;
    private const int MinSkeletonVisibleMs = 550;
    // Botones de lanzamiento que dependen de un launcher externo (Battle.net, Epic,
    // GOG Galaxy, Xbox): se actualizan al abrir la página y por eventos WMI (cuando
    // el launcher nace o muere) — sin polling periódico. AutoOpen=false = el launcher
    // NO se abre solo (GOG/Xbox): si está cerrado, el botón queda deshabilitado.
    private readonly List<(string Exe, Button Btn, string ProcessName, bool LauncherFound, bool AutoOpen)> _launcherButtons = new();

    // Valores de prioridad de CPU (0=Idle ... 5=RealTime), GPU (2..4) y E/S
    // (IO_PRIORITY_HINT: 0=VeryLow, 1=Low, 2=Normal, 3=High, 4=Critical).
    private static readonly int[] CpuPriorityValues = { 0, 1, 2, 3, 4, 5 };
    private static readonly int[] GpuPriorityValues = { 2, 3, 4 };
    private static readonly int[] IoPriorityValues = { 0, 1, 2, 3, 4 };

    private static string IoLabel(int v) => v switch
    {
        0 => I18n.T("Muy baja"),
        1 => I18n.T("Baja"),
        2 => I18n.T("Normal"),
        3 => I18n.T("Alta"),
        4 => I18n.T("Crítica"),
        _ => v.ToString()
    };

    // Internal: la página de Configuración la usa para el botón "Limpiar caché".
    internal static readonly string BannerCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WHPO", "gamebanners");

    public GestionarProcesosPage()
    {
        InitializeComponent();
        _processService = App.Services.GetRequiredService<IProcessService>();
        _installedGamesService = App.Services.GetRequiredService<IInstalledGamesService>();
        _cpuPowerService = App.Services.GetRequiredService<ICpuPowerService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _gameBoostService = App.Services.GetRequiredService<IGameBoostService>();
        CleanupLegacyIconCacheDirs();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Estado del switch "Optimizar procesos al iniciar un juego (BETA)".
        _boostSwitchInitialized = false;
        GameBoostSwitch.IsOn = _gameBoostService.IsEnabled;
        _boostSwitchInitialized = true;
        UpdateGameBoostLabel();

        // La biblioteca viene cacheada (InstalledGamesService): la primera vez se
        // escanea y se guarda, y las siguientes aperturas la leen sin re-escannear.
        // El skeleton cubre solo la primera carga real (cuando todavía no hay caché).
        _ = RefreshAsync(showSkeleton: !_installedGamesService.HasCachedResult);
        I18n.LanguageChanged += OnLanguageChanged;
        // Estado en vivo de los juegos (badge "En ejecución"): los eventos WMI
        // publican un snapshot sin polling; acá solo se refleja en las cards.
        _processService.RunningGamesChanged += OnRunningGamesChanged;

        // Estado del launcher (botón "Iniciar"): chequeo único al abrir + eventos
        // WMI en adelante. Cero polling mientras la página esté visible.
        _processService.LauncherStateChanged += OnLauncherStateChanged;
        UpdateAllLauncherButtons();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        I18n.LanguageChanged -= OnLanguageChanged;
        _processService.RunningGamesChanged -= OnRunningGamesChanged;
        _processService.LauncherStateChanged -= OnLauncherStateChanged;
        // Si la página se deja durante la carga, cortar el pulso del skeleton.
        StopSkeletonPulse();
    }

    private void OnLanguageChanged()
    {
        UpdateGameBoostLabel();
        RebuildCards();
    }

    private void GameBoostSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_boostSwitchInitialized) return;
        _gameBoostService.SetEnabled(GameBoostSwitch.IsOn);
    }

    /// <summary>
    /// Toggling de todo el panel (texto + switch): el ToggleSwitch de WinUI es chico
    /// (barrita + círculo) y los clics que caían en la etiqueta "no agarraban" el switch.
    /// Acá toda la fila togglea. El botón "?" y el propio switch se excluyen para no
    /// duplicar el cambio (el switch ya dispara su propio Toggled).
    /// </summary>
    private void GameBoostPanel_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_boostSwitchInitialized) return;
        var source = e.OriginalSource as DependencyObject;
        if (source == null) return;
        if (IsWithin(source, GameBoostInfoButton)
            || IsWithin(source, GameBoostSettingsButton)
            || IsWithin(source, GameBoostSwitch)) return;
        GameBoostSwitch.IsOn = !GameBoostSwitch.IsOn;
    }

    private static bool IsWithin(DependencyObject node, DependencyObject root)
    {
        for (var current = node; current != null; current = VisualTreeHelper.GetParent(current))
            if (current == root) return true;
        return false;
    }

    /// <summary>Etiqueta del switch con el badge "(BETA)" en amarillo, y tooltip con todos los cambios que aplica.</summary>
    private void UpdateGameBoostLabel()
    {
        GameBoostLabel.Inlines.Clear();
        GameBoostLabel.Inlines.Add(new Run { Text = I18n.T("Optimizar procesos al iniciar un juego") + " " });
        GameBoostLabel.Inlines.Add(new Run { Text = I18n.T("(BETA)"), Foreground = new SolidColorBrush(BetaColor) });
        // Tooltip con el mismo estilo que los botones "?" informativos del resto de
        // la app: título (semi-negrita) + descripción con salto de línea, Placement
        // Bottom y ancho máximo 420. El "(BETA)" amarillo queda solo en la etiqueta.
        ToolTip BuildGameBoostToolTip()
        {
            TextBlock Line(string key, bool bullet = false) => new()
            {
                Text = (bullet ? "• " : string.Empty) + I18n.T(key),
                FontSize = 12,
                Foreground = Feedback.MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(bullet ? 8 : 0, 0, 0, 0)
            };

            var content = new StackPanel { Spacing = 6, MaxWidth = 430 };
            content.Children.Add(new TextBlock
            {
                Text = I18n.T("Optimizar procesos al iniciar un juego"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });

            content.Children.Add(new TextBlock
            {
                Text = I18n.T("Mientras jugás:"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Feedback.MutedBrush
            });
            foreach (var key in new[]
            {
                "Pausa Windows Update (se reanuda solo al salir).",
                "Detiene servicios de mantenimiento y telemetría que estén corriendo (SysMain, DiagTrack, búsqueda, etc.): los ya detenidos no se tocan.",
                "Baja prioridad y activa el modo de eficiencia en procesos de segundo plano (búsqueda, widgets, OneDrive…).",
                "Si hay un plan de energía global configurado (⚙️) lo activa; el plan propio de cada juego tiene prioridad."
            })
            {
                content.Children.Add(Line(key, bullet: true));
            }

            content.Children.Add(Line("Al cerrar el juego (o la app) todo vuelve a su estado previo: solo se reactivan los servicios que ya estaban corriendo y las prioridades vuelven a su valor original. El juego nunca se toca."));

            return new ToolTip
            {
                Placement = PlacementMode.Bottom,
                Content = content
            };
        }
        ToolTipService.SetToolTip(GameBoostInfoButton, BuildGameBoostToolTip());
    }

    // ===================== Tuerca: configuración del optimizador =====================

    /// <summary>
    /// Popup con dos pestañas: plan de energía global y procesos en segundo plano.
    /// Los procesos DEFAULT del boost aparecen bloqueados (no se pueden quitar); los
    /// AGREGADOS por el usuario sí, y se suman desde la lista de procesos en ejecución
    /// con doble clic (sin tipear nombres a mano).
    /// </summary>
    private async void GameBoostSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = _gameBoostService.GetDefaultBackgroundProcesses();
        var custom = _gameBoostService.GetBackgroundProcesses()
            .Where(n => !defaults.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // ---------- Pestaña 1: plan de energía ----------
        var planBox = new ComboBox { MinWidth = 240, MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left };
        planBox.Items.Add(new ComboBoxItem { Content = I18n.T("Usar el plan actual"), Tag = string.Empty });
        foreach (var plan in _cpuPowerService.GetPowerPlans())
            planBox.Items.Add(new ComboBoxItem { Content = plan.Name, Tag = plan.Guid });
        var savedPlan = _gameBoostService.GetGlobalPowerPlanGuid() ?? string.Empty;
        planBox.SelectedIndex = 0;
        for (int i = 1; i < planBox.Items.Count; i++)
            if (string.Equals((string?)((ComboBoxItem)planBox.Items[i]!).Tag, savedPlan, StringComparison.OrdinalIgnoreCase))
            { planBox.SelectedIndex = i; break; }

        var planCard = MakeSettingsCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = I18n.T("Plan de energía global"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14 },
                new TextBlock
                {
                    Text = I18n.T("Se activa al iniciar cualquier juego, salvo que el juego tenga uno propio configurado desde su tarjeta."),
                    FontSize = 12,
                    Foreground = Feedback.MutedBrush,
                    TextWrapping = TextWrapping.Wrap
                },
                planBox
            }
        });

        // ---------- Pestaña 2: procesos ----------
        var processesPanel = BuildProcessesTab(defaults, custom);

        var pivot = new Pivot { Margin = new Thickness(0, 2, 0, 0) };
        pivot.Items.Add(new PivotItem { Header = I18n.T("Plan de energía"), Content = planCard });
        pivot.Items.Add(new PivotItem { Header = I18n.T("Procesos"), Content = processesPanel });

        var dialog = new ContentDialog
        {
            Title = I18n.T("Configuración del optimizador"),
            XamlRoot = XamlRoot,
            PrimaryButtonText = I18n.T("Guardar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Primary,
            Content = pivot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var selected = planBox.SelectedItem as ComboBoxItem;
        _gameBoostService.SetGlobalPowerPlanGuid(selected?.Tag as string ?? string.Empty);
        _gameBoostService.SetBackgroundProcesses(custom);
    }

    /// <summary>Card estándar del popup: fondo/borde de card del tema, esquinas y padding.</summary>
    private static Border MakeSettingsCard(StackPanel inner) => new()
    {
        Background = ThemeBrushes.Get("CardBackgroundBrush"),
        BorderBrush = ThemeBrushes.Get("CardBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14),
        Child = inner
    };

    // ===== Íconos de procesos (cache en memoria + disco) =====
    // Misma técnica que los íconos de juegos: IconExtractor (shell JUMBO) → PNG 32px
    // en disco → BitmapImage. La extracción corre en background (abrir MainModule
    // puede tardar o denegarse) y completa el Source por el dispatcher al terminar.
    private static readonly Dictionary<string, BitmapImage> ProcIconCache = new(StringComparer.OrdinalIgnoreCase);
    // Sufijo "-v2": la caché v1 podía guardar íconos legacy con el dibujo chico en
    // una esquina de la tela (ver IconExtractor.TrimTransparentMargins); con la v2 se
    // re-extrae todo con el recorte correcto.
    private static readonly string ProcIconDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WHPO", "gamebanners", "procicons-v2");

    /// <summary>
    /// Descarta las carpetas de caché de íconos de la versión anterior (procicons y
    /// exeicons v1), que pueden contener íconos legacy con el dibujo chico en una
    /// esquina (7×7 px en 32×32). Se llama una sola vez al abrir la página; las
    /// carpetas nuevas (…-v2) se crean solas al extraer.
    /// </summary>
    private static int _iconCacheCleanupDone;
    internal static void CleanupLegacyIconCacheDirs()
    {
        if (Interlocked.Exchange(ref _iconCacheCleanupDone, 1) != 0) return;
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var legacy in new[]
            {
                Path.Combine(baseDir, "WHPO", "gamebanners", "procicons"),
                Path.Combine(baseDir, "WHPO", "gamebanners", "exeicons")
            })
            {
                try { if (Directory.Exists(legacy)) Directory.Delete(legacy, recursive: true); }
                catch { /* archivo en uso: se reintenta en la próxima apertura */ }
            }
        }
        catch { }
    }

    /// <summary>Pone el ícono del proceso en el Image indicado (cache → disco → extraer).</summary>
    private static void EnsureProcessIcon(string processName, Microsoft.UI.Xaml.Controls.Image target)
    {
        try
        {
            lock (ProcIconCache)
            {
                if (ProcIconCache.TryGetValue(processName, out var cached))
                {
                    target.Source = cached;
                    return;
                }
            }

            var file = Path.Combine(ProcIconDir, processName + ".png");
            if (File.Exists(file))
            {
                var bi = new BitmapImage(new Uri(file));
                lock (ProcIconCache) ProcIconCache[processName] = bi;
                target.Source = bi;
                return;
            }

            // Placeholder INMEDIATO: ícono genérico de .exe. Nunca queda una fila vacía:
            // si después se extrae el ícono real, lo reemplaza.
            var fallback = GetDefaultIconImage();
            if (fallback != null) target.Source = fallback;

            // Extraer en segundo plano y reemplazar el placeholder cuando esté listo.
            _ = Task.Run(() =>
            {
                try
                {
                    string? exePath = null;
                    var p = Process.GetProcessesByName(processName).FirstOrDefault();
                    if (p != null)
                    {
                        try { exePath = p.MainModule?.FileName; }
                        catch { /* protegido por anti-cheat/sistema */ }
                        finally { p.Dispose(); }
                    }

                    // Si no se pudo leer el exe (o no trae ícono propio), el fallback
                    // genérico ya está visible: no hay nada que guardar para este nombre.
                    if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;
                    using var big = IconExtractor.ExtractHighResIcon(exePath);
                    if (big == null) return;

                    using var small = new System.Drawing.Bitmap(32, 32);
                    using (var g = System.Drawing.Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(big, 0, 0, 32, 32);
                    }
                    Directory.CreateDirectory(ProcIconDir);
                    var tmp = file + ".tmp";
                    small.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                    File.Move(tmp, file, overwrite: true);
                }
                catch { }

                _ = App.MainWindowInstance?.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        var bi = new BitmapImage(new Uri(file));
                        lock (ProcIconCache) ProcIconCache[processName] = bi;
                        target.Source = bi;
                    }
                    catch { }
                });
            });
        }
        catch { }
    }

    /// <summary>
    /// Ícono genérico de .exe como BitmapImage (cacheado en memoria y en disco como
    /// "_default.png"). Placeholder para procesos cuyo exe no se puede leer.
    /// </summary>
    private static BitmapImage? GetDefaultIconImage()
    {
        try
        {
            lock (ProcIconCache)
            {
                if (ProcIconCache.TryGetValue("__default__", out var cached))
                    return cached;
            }

            var file = Path.Combine(ProcIconDir, "_default.png");
            if (!File.Exists(file))
            {
                using var big = IconExtractor.ExtractDefaultExeIcon();
                if (big == null) return null;
                using var small = new System.Drawing.Bitmap(32, 32);
                using (var g = System.Drawing.Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(big, 0, 0, 32, 32);
                }
                Directory.CreateDirectory(ProcIconDir);
                var tmp = file + ".tmp";
                small.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                File.Move(tmp, file, overwrite: true);
            }

            var bi = new BitmapImage(new Uri(file));
            lock (ProcIconCache) ProcIconCache["__default__"] = bi;
            return bi;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fila [ícono + nombre] para las listas del popup. Tag = nombre del proceso.</summary>
    private static Grid MakeProcessRow(string name, bool muted = false)
    {
        var row = new Grid { ColumnSpacing = 8, Tag = name };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Microsoft.UI.Xaml.Controls.Image
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        EnsureProcessIcon(name, icon);
        row.Children.Add(icon);
        var tb = new TextBlock
        {
            Text = name,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = muted ? 0.75 : 1
        };
        if (muted) tb.Foreground = Feedback.MutedBrush;
        Grid.SetColumn(tb, 1);
        row.Children.Add(tb);
        return row;
    }

    /// <summary>
    /// Pestaña Procesos: izquierda = procesos en ejecución (doble clic agrega al boost);
    /// derecha = lo que ya está en el boost: defaults bloqueados y agregados con su ✗.
    /// </summary>
    private UIElement BuildProcessesTab(List<string> defaults, List<string> custom)
    {
        var addedRows = new StackPanel { Spacing = 4 };

        // Nunca ofrecemos procesos críticos del sistema, Defender ni la propia app.
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "MemCompression",
            "csrss", "smss", "wininit", "winlogon", "lsass", "services", "svchost",
            "dwm", "fontdrvhost", "conhost", "dllhost", "RuntimeBroker", "audiodg",
            "MsMpEng", "spoolsv", "WudfHost", "WinForge"
        };

        // Snapshot de nombres de procesos en ejecución (distintos, sin duplicar).
        var running = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var n = p.ProcessName;
                    if (!string.IsNullOrWhiteSpace(n) && seen.Add(n)) running.Add(n);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        var searchBox = new TextBox
        {
            PlaceholderText = I18n.T("Buscar proceso..."),
            FontSize = 12
        };
        var pickerList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 252,
            Margin = new Thickness(0, 2, 0, 0)
        };

        void RebuildPicker()
        {
            var q = searchBox.Text?.Trim() ?? string.Empty;
            var excluded = new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase);
            foreach (var c in custom) excluded.Add(c);
            foreach (var b in blocked) excluded.Add(b);
            pickerList.Items.Clear();
            foreach (var name in running
                .Where(n => !excluded.Contains(n)
                            && (q.Length == 0 || n.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                pickerList.Items.Add(MakeProcessRow(name));
            }
        }

        searchBox.TextChanged += (_, _) => RebuildPicker();
        pickerList.DoubleTapped += (_, _) =>
        {
            if (pickerList.SelectedItem is FrameworkElement fe && fe.Tag is string name) AddProcess(name);
        };

        var leftCard = MakeSettingsCard(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = I18n.T("Procesos en ejecución"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 },
                searchBox,
                pickerList,
                new TextBlock
                {
                    Text = I18n.T("Doble clic para agregar"),
                    FontSize = 11,
                    Foreground = Feedback.MutedBrush,
                    Opacity = 0.85
                }
            }
        });

        var defaultsRows = new StackPanel { Spacing = 4 };
        foreach (var name in defaults)
        {
            // [ícono del proceso][nombre][🔒]: los defaults están fijos, el candado lo
            // comunica sin necesidad de texto extra.
            var r = new Grid { ColumnSpacing = 6 };
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Microsoft.UI.Xaml.Controls.Image
            {
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            EnsureProcessIcon(name, icon);
            r.Children.Add(icon);
            var tb = new TextBlock
            {
                Text = name,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Feedback.MutedBrush
            };
            Grid.SetColumn(tb, 1);
            r.Children.Add(tb);
            var lockIcon = new FontIcon
            {
                Glyph = "\uE72E", // candado
                FontSize = 10,
                Foreground = Feedback.MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(lockIcon, 2);
            r.Children.Add(lockIcon);
            defaultsRows.Children.Add(r);
        }

        void RefreshSide()
        {
            addedRows.Children.Clear();
            if (custom.Count == 0)
            {
                addedRows.Children.Add(new TextBlock
                {
                    Text = "—",
                    FontSize = 12,
                    Foreground = Feedback.MutedBrush,
                    Opacity = 0.6
                });
                return;
            }
            foreach (var name in custom.ToList())
            {
                var copy = name;
                var g = new Grid { ColumnSpacing = 8 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = new Microsoft.UI.Xaml.Controls.Image
                {
                    Width = 16,
                    Height = 16,
                    VerticalAlignment = VerticalAlignment.Center
                };
                EnsureProcessIcon(copy, icon);
                g.Children.Add(icon);
                var tb = new TextBlock
                {
                    Text = copy,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(tb, 1);
                g.Children.Add(tb);
                var del = new Button
                {
                    Content = "✗",
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    FontSize = 11,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Foreground = Feedback.ErrorBrush
                };
                del.Click += (_, _) =>
                {
                    custom.Remove(copy);
                    RefreshSide();
                    RebuildPicker();
                };
                Grid.SetColumn(del, 2);
                g.Children.Add(del);
                addedRows.Children.Add(g);
            }
        }

        void AddProcess(string raw)
        {
            raw = raw.Trim();
            if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) raw = raw[..^4];
            if (raw.Length == 0) return;
            // Sin duplicados contra defaults, contra lo ya agregado ni contra bloqueados.
            if (defaults.Contains(raw, StringComparer.OrdinalIgnoreCase)
                || custom.Contains(raw, StringComparer.OrdinalIgnoreCase)
                || blocked.Contains(raw))
                return;
            custom.Add(raw);
            RefreshSide();
            RebuildPicker();
        }

        var restoreButton = new Button
        {
            Content = I18n.T("Restaurar lista por defecto"),
            FontSize = 12,
            Padding = new Thickness(10, 5, 10, 5),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0)
        };
        restoreButton.Click += (_, _) =>
        {
            custom.Clear();
            RefreshSide();
            RebuildPicker();
        };

        var rightCard = MakeSettingsCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = I18n.T("En el boost"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 },
                new TextBlock { Text = I18n.T("Por defecto"), FontSize = 11, Foreground = Feedback.MutedBrush },
                defaultsRows,
                new TextBlock { Text = I18n.T("Agregados"), FontSize = 11, Foreground = Feedback.MutedBrush },
                addedRows,
                restoreButton
            }
        });

        RebuildPicker();
        RefreshSide();

        var columns = new Grid { ColumnSpacing = 14 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(leftCard);
        Grid.SetColumn(rightCard, 1);
        columns.Children.Add(rightCard);
        return columns;
    }

    /// <summary>
    /// Refleja el snapshot de juegos en ejecución (eventos WMI) en el botón Iniciar
    /// de cada card (muestra "En ejecución" y se deshabilita) y en el contador del
    /// encabezado. El evento llega de un hilo de fondo: se marshaliza al dispatcher
    /// de la UI. No reconstruye la grilla.
    /// </summary>
    private void OnRunningGamesChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _runningExes = new HashSet<string>(_processService.RunningGameExes, StringComparer.OrdinalIgnoreCase);
                foreach (var (exe, btn, canLaunch) in _gameLaunchButtons)
                {
                    if (_runningExes.Contains(exe))
                    {
                        // El juego ya corre: feedback en el propio botón y no se
                        // puede lanzar de nuevo.
                        btn.Content = I18n.T("En ejecución");
                        btn.IsEnabled = false;
                        ToolTipService.SetToolTip(btn, I18n.T("El juego ya está en ejecución"));
                    }
                    else
                    {
                        // Restaurar el estado normal: launcher (Battle.net/Epic/GOG/Xbox)
                        // o lanzamiento directo por exe.
                        var launcher = _launcherButtons.FirstOrDefault(l =>
                            l.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));
                        if (launcher.Btn != null)
                        {
                            UpdateLauncherButton(launcher.Btn, launcher.ProcessName, launcher.LauncherFound,
                                launcher.LauncherFound && IsLauncherRunning(launcher.ProcessName), launcher.AutoOpen);
                        }
                        else
                        {
                            btn.Content = I18n.T("Iniciar");
                            btn.IsEnabled = canLaunch;
                            ToolTipService.SetToolTip(btn, canLaunch ? null : I18n.T("Ejecutable no encontrado"));
                        }
                    }
                }
                if (_counterStatusVisible)
                {
                    InstalledCountText.Text = I18n.T("Juegos instalados: {0}", _installed.Count);
                }
            }
            catch { }
        });
    }

    // ===== Detección =====

    private async Task RefreshAsync(bool showSkeleton = false, bool refreshCache = false)
    {
        try
        {
            RedetectButton.IsEnabled = false;
            // Skeleton solo cuando se pide explícitamente (primera carga sin caché):
            // en Re-detectar las cards ya cargadas se mantienen durante el escaneo.
            if (showSkeleton)
                ShowSkeleton();

            // Juegos instalados (launchers) + manuales. Con refreshCache=true (botón
            // Re-detectar) se re-escanean los launchers; si no, se usa la caché.
            _installed = await _installedGamesService.GetInstalledGamesAsync(refreshCache);
            _manual = _processService.GetManualEntries();
            var knownExes = new HashSet<string>(
                _installed.Where(g => !string.IsNullOrEmpty(g.ExeFileName)).Select(g => g.ExeFileName!),
                StringComparer.OrdinalIgnoreCase);

            // Registrar exe → carpeta de instalación: el monitor de Core matchea por
            // ruta los procesos cuyo nombre difiere del exe detectado (ej. launchers
            // como Smite.exe que corren SmiteGame-Win64-Shipping.exe).
            var knownPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in _installed)
                if (!string.IsNullOrEmpty(g.InstallPath))
                    knownPaths[g.ExeFileName] = g.InstallPath;
            foreach (var (exe, _, path) in _manual)
                if (!string.IsNullOrEmpty(path))
                    knownPaths[exe] = path;
            _processService.SetKnownInstallPaths(knownPaths);

            // Estado de ejecución desde el snapshot de los eventos WMI: no se
            // re-enumeran procesos ni se mide CPU en cada apertura (antes la página
            // pagaba ~250 ms de GetRunningAppsAsync para saber qué juegos corren).
            _runningExes = new HashSet<string>(
                _processService.RunningGameExes.Where(e => knownExes.Contains(e)),
                StringComparer.OrdinalIgnoreCase);

            // Garantizar un mínimo de tiempo visible del skeleton (como en SistemaPage)
            // para que el efecto de carga se aprecie aunque el escaneo sea veloz.
            await EnsureMinSkeletonVisibleAsync();
            RebuildCards();
            InstalledCountText.Text = I18n.T("Juegos instalados: {0}", _installed.Count);
            InstalledCountText.Visibility = Visibility.Visible;
            _counterStatusVisible = true;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"GestionarProcesosPage: no se pudo escanear juegos: {ex.Message}");
            StatusText.Text = I18n.T("No se pudieron escanear los juegos: {0}", ex.Message);
            StatusText.Foreground = Feedback.ErrorBrush;
            StatusText.Visibility = Visibility.Visible;
            InstalledCountText.Visibility = Visibility.Collapsed;
            _counterStatusVisible = false;
        }
        finally
        {
            RedetectButton.IsEnabled = true;
            HideSkeleton();
        }
    }

    private async Task EnsureMinSkeletonVisibleAsync()
    {
        if (!_skeletonActive) return;
        long elapsed = Environment.TickCount64 - _skeletonShownAtMs;
        if (elapsed < MinSkeletonVisibleMs)
            await Task.Delay((int)(MinSkeletonVisibleMs - elapsed));
    }

    // ===== Skeleton de carga =====

    /// <summary>
    /// Muestra las cards placeholder en la biblioteca mientras se escanean los
    /// juegos. Van en el MISMO panel de wrap (misma geometría de N columnas que la
    /// vista elegida, mismo lugar que los juegos reales) para garantizar que se
    /// vean donde corresponde.
    /// </summary>
    private void ShowSkeleton()
    {
        if (_skeletonActive) return;
        _skeletonActive = true;
        _skeletonShownAtMs = Environment.TickCount64;
        int per = GridColumns;
        if (_skeletonCards.Count == 0)
        {
            _skeletonPanel = new StackPanel { Spacing = 12 };
            LibraryPanel.Children.Add(_skeletonPanel);
            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            _skeletonPanel.Children.Add(row1);
            _skeletonPanel.Children.Add(row2);
            for (int i = 0; i < 6; i++)
            {
                var (card, banner, blocks) = BuildSkeletonCard();
                _skeletonCards.Add((card, banner, blocks));
                (i < per ? row1 : row2).Children.Add(card);
            }
        }
        else if (_skeletonPanel == null)
        {
            // Re-mostrar skeletons ya construidos (el panel se limpió en RebuildCards).
            _skeletonPanel = new StackPanel { Spacing = 12 };
            LibraryPanel.Children.Add(_skeletonPanel);
            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            _skeletonPanel.Children.Add(row1);
            _skeletonPanel.Children.Add(row2);
            for (int i = 0; i < _skeletonCards.Count; i++)
                (i < per ? row1 : row2).Children.Add(_skeletonCards[i].Card);
        }
        UpdateCardWidth();
        StartSkeletonPulse();
    }

    private void HideSkeleton()
    {
        if (!_skeletonActive) return;
        _skeletonActive = false;
        StopSkeletonPulse();
        // RebuildCards ya limpia el panel; esto cubre el caso de error antes de
        // reconstruir (que queden skeletons huérfanos).
        if (_skeletonPanel != null)
            LibraryPanel.Children.Remove(_skeletonPanel);
        _skeletonPanel = null;
    }

    /// <summary>
    /// Anima cada bloque de las cards del skeleton con un pulso de opacidad suave
    /// (mismo patrón que el skeleton de SistemaPage): 1.0 → 0.35 en 900 ms con
    /// auto-reverse infinito, aplicado a cada bloque por separado.
    /// </summary>
    private void StartSkeletonPulse()
    {
        try
        {
            foreach (var (_, _, blocks) in _skeletonCards)
            {
                foreach (var block in blocks)
                {
                    var sb = new Storyboard
                    {
                        RepeatBehavior = RepeatBehavior.Forever,
                        AutoReverse = true
                    };
                    var anim = new DoubleAnimation
                    {
                        From = 1.0,
                        To = 0.35,
                        Duration = new Duration(TimeSpan.FromMilliseconds(900))
                    };
                    Storyboard.SetTarget(anim, block);
                    Storyboard.SetTargetProperty(anim, "Opacity");
                    sb.Children.Add(anim);
                    _skeletonStoryboards.Add(sb);
                    sb.Begin();
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"GestionarProcesosPage: animación de skeleton: {ex.Message}");
        }
    }

    private void StopSkeletonPulse()
    {
        foreach (var sb in _skeletonStoryboards)
        {
            try { sb.Stop(); } catch { }
        }
        _skeletonStoryboards.Clear();
        foreach (var (_, _, blocks) in _skeletonCards)
            foreach (var b in blocks)
                b.Opacity = 1.0;
    }

    /// <summary>
    /// Card placeholder del skeleton, espejo de la card real (estilo GearUpBooster):
    /// la card es SOLO el banner 16:9 con el título al pie (los botones de acción
    /// viven en el overlay al hover, no hay botón fijo debajo). El skeleton replica
    /// esa geometría: banner redondeado en las 4 esquinas + barra de título simulada
    /// al pie. UpdateCardWidth lo dimensiona igual que a las cards reales.
    /// </summary>
    private static (Border Card, Border Banner, Border[] Blocks) BuildSkeletonCard()
    {
        // Mismo pincel que los bloques del skeleton de SistemaPage
        // (ControlFillColorSecondaryBrush): el gris clásico de carga.
        var skeletonBrush = ThemeBrushes.Get("ControlFillColorSecondaryBrush");
        var banner = new Border
        {
            Background = skeletonBrush,
            // Mismo radio que la card real: las cuatro esquinas redondeadas.
            CornerRadius = new CornerRadius(12),
            // Tamaño intrínseco: visible aunque el layout de la grilla aún no haya
            // corrido (primera apertura); UpdateCardWidth lo refina después.
            Height = 150
        };
        // Título simulado al pie del banner (como el título real de la card).
        var titleBar = new Border
        {
            Background = skeletonBrush,
            Width = 110,
            Height = 13,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(10, 0, 0, 9),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var bannerGrid = new Grid();
        bannerGrid.Children.Add(banner);
        bannerGrid.Children.Add(titleBar);

        var card = new Border
        {
            // Sin reborde (mismo estilo de cards que el resto de la app).
            Background = ThemeBrushes.Get("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            // Sin margin: el Spacing de los StackPanel (horizontal y vertical)
            // maneja todo el espaciado para que sea uniforme (12 px) en ambas
            // direcciones, entre cards de la misma fila y entre filas.
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = bannerGrid
        };
        // Los bloques animables: banner y barra de título (cada uno con su pulso).
        return (card, banner, new[] { banner, titleBar });
    }

    // Re-detectar: re-escanea los launchers a propósito y actualiza la caché
    // (por si se instaló/desinstaló un juego desde la última visita).
    private void RedetectButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync(showSkeleton: false, refreshCache: true);

    // ===== Añadir manual (seleccionar el exe del juego) =====

    private async void AddManualButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null || App.MainWindowInstance == null) return;
        try
        {
            // FileOpenPicker de WinRT NO funciona en apps elevadas (administrador):
            // la ventana del picker no puede arrancar en una sesión elevada — es un
            // problema conocido de Windows y la excepción llega con mensaje vacío.
            // Se usa el diálogo nativo (Common Item Dialog vía WinForms), que corre
            // en el mismo proceso y funciona elevado sin problema.
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = I18n.T("Seleccionar el exe del juego"),
                // Filtro por defecto = ejecutables (incluye .bat/.cmd/.com y accesos
                // directos .lnk): el *.exe solo ocultaba los launchers con otras
                // extensiones. "Todos los archivos" queda en el desplegable como
                // escape, pero abajo se valida que lo elegido sea un ejecutable.
                Filter = "Ejecutables (*.exe;*.bat;*.cmd;*.com;*.lnk)|*.exe;*.bat;*.cmd;*.com;*.lnk|Todos los archivos (*.*)|*.*",
                FilterIndex = 1,
                Multiselect = true,
                CheckFileExists = false,
                RestoreDirectory = true,
                // Abrir en la carpeta de juegos del usuario (si existe) para que
                // los juegos instalados se encuentren sin navegar de más.
                InitialDirectory = Directory.Exists(@"C:\Games")
                    ? @"C:\Games"
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            var owner = System.Windows.Forms.NativeWindow.FromHandle(hwnd);
            if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK
                || dialog.FileNames.Length == 0)
                return;

            var added = new List<string>();
            var skipped = new List<string>();
            foreach (var path in dialog.FileNames)
            {
                // Acceso directo (.lnk): resolver el destino real (el exe del juego),
                // así se agrega el juego y no el atajo (que no tiene proceso propio).
                string resolved = ResolveShortcut(path) ?? path;
                string ext = Path.GetExtension(resolved);
                bool isExecutable = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".com", StringComparison.OrdinalIgnoreCase);
                if (!isExecutable)
                {
                    // Desde "Todos los archivos" se puede elegir cualquier cosa:
                    // los no-ejecutables (txt, png…) se omiten con aviso.
                    skipped.Add(Path.GetFileName(resolved));
                    continue;
                }
                string? dir = Path.GetDirectoryName(resolved);
                _processService.AddManualExe(Path.GetFileName(resolved), Path.GetFileNameWithoutExtension(resolved), dir);
                added.Add(resolved);
            }

            if (added.Count == 0)
            {
                StatusText.Text = I18n.T("No se agregó ningún juego: «{0}» no es un ejecutable.", skipped.FirstOrDefault() ?? "");
                StatusText.Foreground = Feedback.WarningBrush;
                StatusText.Visibility = Visibility.Visible;
                return;
            }

            await RefreshAsync();

            if (skipped.Count > 0)
                StatusText.Text = I18n.T("Se agregaron {0} juegos. Se omitieron {1} archivos que no son ejecutables.", added.Count, skipped.Count);
            else
                StatusText.Text = added.Count == 1
                    ? I18n.T("Se agregó «{0}» a la biblioteca.", Path.GetFileName(added[0]))
                    : I18n.T("Se agregaron {0} juegos a la biblioteca.", added.Count);
            StatusText.Foreground = Feedback.SuccessBrush;
            StatusText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"GestionarProcesosPage: añadir manual: {ex}");
            StatusText.Text = I18n.T("No se pudo agregar el juego: {0}",
                string.IsNullOrWhiteSpace(ex.Message) ? $"0x{ex.HResult:X8}" : ex.Message);
            StatusText.Foreground = Feedback.ErrorBrush;
            StatusText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Resuelve el destino de un acceso directo de Windows (.lnk): si el usuario elige
    /// un atajo, se agrega el exe real al que apunta (el .lnk no tiene proceso propio
    /// y las reglas no podrían matchearlo). Devuelve null si no es un .lnk válido con
    /// destino existente.
    /// </summary>
    private static string? ResolveShortcut(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
        // 1) WScript.Shell (rápido y simple).
        try
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t != null)
            {
                dynamic shell = Activator.CreateInstance(t)!;
                dynamic lnk = shell.CreateShortcut(path);
                string? target = lnk.TargetPath as string;
                if (!string.IsNullOrEmpty(target) && File.Exists(target))
                    return target;
            }
        }
        catch { /* caer al resolvedor nativo */ }

        // 2) IShellLinkW nativo (Shell32): no depende del ProgID de WScript.Shell y
        // funciona en procesos elevados (la app corre como administrador), donde la
        // activación COM de WScript puede fallar. Es el mismo mecanismo que usa
        // Explorer para abrir un acceso directo.
        try
        {
            var link = (IShellLinkW)new ShellLink();
            var persist = (IPersistFile)link;
            persist.Load(path, 0);
            var buf = new System.Text.StringBuilder(1024);
            link.GetPath(buf, buf.Capacity, IntPtr.Zero, 0);
            string target = buf.ToString();
            if (!string.IsNullOrEmpty(target) && File.Exists(target))
                return target;
        }
        catch { }
        return null;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    // ===== Grilla de juegos =====

    private void RebuildCards()
    {
        LibraryPanel.Children.Clear();
        _cards.Clear();
        _launcherButtons.Clear();
        _gameLaunchButtons.Clear();
        _skeletonPanel = null; // el skeleton (si estaba) se fue con el clear del panel

        var hidden = _processService.GetHiddenExes();
        var items = new List<(InstalledGame? game, string exe, string? name, bool isManual, string? installPath)>();
        string q = _searchQuery.Trim();
        foreach (var g in _installed)
            if (!hidden.Contains(g.ExeFileName))
                items.Add((g, g.ExeFileName, g.Name, false, g.InstallPath));
        foreach (var (exe, name, path) in _manual)
            if (!hidden.Contains(exe) && !items.Any(i => string.Equals(i.exe, exe, StringComparison.OrdinalIgnoreCase)))
                items.Add((null, exe, name ?? exe, true, path));
        // Búsqueda por nombre (case-insensitive, parcial): vacío muestra todo.
        if (q.Length > 0)
        {
            items = items
                .Where(i => (i.name ?? i.exe).Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Favoritos aparte del resto (barra diferenciadora): dentro de cada grupo,
        // primero los que están corriendo, después alfabético.
        var favorites = items
            .Where(i => _processService.IsFavorite(i.exe))
            .OrderByDescending(i => _runningExes.Contains(i.exe))
            .ThenBy(i => i.name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var others = items
            .Where(i => !_processService.IsFavorite(i.exe))
            .OrderByDescending(i => _runningExes.Contains(i.exe))
            .ThenBy(i => i.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (favorites.Count > 0)
        {
            // Favoritos y juegos detectados separados por la barra divisoria, con
            // el mismo espaciado que entre filas de una misma sección (Spacing del
            // LibraryPanel, 12 px arriba y abajo de la barra).
            LibraryPanel.Children.Add(BuildWrapPanel(favorites));
            LibraryPanel.Children.Add(BuildSectionDivider());
            LibraryPanel.Children.Add(BuildWrapPanel(others));
        }
        else
        {
            LibraryPanel.Children.Add(BuildWrapPanel(others));
        }

        // Mensaje distinto cuando el buscador no encuentra nada (vs. biblioteca vacía).
        if (items.Count == 0)
        {
            EmptyText.Text = _searchQuery.Trim().Length > 0
                ? I18n.T("No se encontraron juegos que coincidan con la búsqueda.")
                : I18n.T("No se encontraron juegos instalados. Probá «Re-detectar» o «Añadir manual».");
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }
        UpdateCardWidth();
    }

    /// <summary>Filtra la grilla por el texto del buscador (sin re-escannear).</summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text ?? "";
        RebuildCards();
    }

    /// <summary>
    /// Barra diferenciadora entre favoritos y juegos detectados: línea divisoria
    /// con el mismo espaciado arriba y abajo (12 px cada lado, igual que el
    /// Spacing del LibraryPanel entre filas de cards).
    /// </summary>
    private UIElement BuildSectionDivider()
    {
        return new Border
        {
            Height = 1,
            Background = (Brush)ThemeBrushes.Get("CardBorderBrush"),
            // Margin bottom compensa el Spacing del LibraryPanel para que la
            // separación sea simétrica (igual arriba y abajo de la barra).
            Margin = new Thickness(0, 0, 12, 0)
        };
    }

    /// <summary>
    /// Grilla de un grupo de juegos en filas de N columnas (3 default o 5 chicas;
    /// StackPanels puros, sin ItemsWrapGrid): render garantizado en cualquier
    /// contenedor.
    /// </summary>
    private FrameworkElement BuildWrapPanel(List<(InstalledGame? game, string exe, string? name, bool isManual, string? installPath)> items)
    {
        var panel = new StackPanel { Spacing = 16 };
        int perRow = GridColumns;
        for (int i = 0; i < items.Count; i += perRow)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            for (int j = i; j < Math.Min(i + perRow, items.Count); j++)
            {
                var item = items[j];
                var card = BuildGameCard(item.game, item.exe, item.name ?? item.exe, item.installPath);
                row.Children.Add(card);
            }
            panel.Children.Add(row);
        }
        return panel;
    }

    private Border BuildGameCard(InstalledGame? game, string exe, string name, string? installPath)
    {
        bool fav = _processService.IsFavorite(exe);
        string? exePath = GameLauncher.FindExePath(installPath ?? "", exe);
        // El ícono de la card se extrae del exe REAL del juego (no del stub de
        // anti-cheat/consola): el exe detectado puede ser start_protected_game.exe
        // (EAC) o vconsole2.exe (CS2), cuyo ícono no es el del juego. El exe de
        // lanzamiento NO se toca: los juegos con anti-cheat arrancan por el stub.
        string? iconPath = GameExeResolver.IsStubExe(exePath ?? "") && !string.IsNullOrEmpty(installPath)
            ? GameExeResolver.FindBestGameExePath(installPath)
            : exePath;

        // ===== Banner (imagen principal del juego) =====
        // Cadena de visualización: 1) banner (CDN de Steam o catálogo de Epic),
        // 2) ícono extraído del exe del juego, 3) emoji de control 🎮. El banner se
        // aplica como ImageBrush de fondo (el CornerRadius del Border la recorta) y
        // el ícono/emoji van como elementos centrados que se ocultan cuando carga la
        // imagen real.
        var mediaImage = new Image
        {
            Width = 64,
            Height = 64,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        var emojiText = new TextBlock
        {
            Text = "\U0001F3AE", // 🎮 control de consola
            FontSize = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var banner = new Border
        {
            // La card es solo el banner (los botones van en el overlay al hover):
            // las cuatro esquinas redondeadas.
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0),
            Background = ThemeBrushes.Get("CardHoverBrush")
        };
        var bannerGrid = new Grid();
        bannerGrid.Children.Add(mediaImage);
        bannerGrid.Children.Add(emojiText);
        banner.Child = bannerGrid;

        // Estrella (favorito) arriba a la izquierda del banner.
        var starBtn = new Button
        {
            Content = new FontIcon
            {
                Glyph = fav ? "\uE735" : "\uE734",
                FontSize = 15,
                Foreground = fav ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xC1, 0x07)) : (Brush)ThemeBrushes.Get("SecondaryTextBrush")
            },
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 10, 0, 0)
        };
        ToolTipService.SetToolTip(starBtn, I18n.T(fav ? "Quitar de favoritos" : "Marcar como favorito"));
        starBtn.Click += (s, e) =>
        {
            _processService.ToggleFavorite(exe);
            RebuildCards();
        };

        // Engranaje (reglas) arriba a la derecha del banner. Dentro del menú está
        // el resto de acciones (prioridad, afinidad, plan de energía, Windows
        // Defender…), así la card no se llena de botones.
        var gearBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = 14 },
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0)
        };
        ToolTipService.SetToolTip(gearBtn, I18n.T("Reglas"));
        // Carpeta a excluir de Windows Defender (la del juego, o la del exe).
        string? defenderFolder = !string.IsNullOrEmpty(installPath) ? installPath
            : (!string.IsNullOrEmpty(exePath) ? Path.GetDirectoryName(exePath) : null);
        // Ejecutable para el CFG: el exe REAL del juego (el mismo que se usa para
        // el ícono, saltando stubs de anti-cheat), o el exe detectado.
        string? cfgExe = !string.IsNullOrEmpty(iconPath) ? Path.GetFileName(iconPath) : exe;
        gearBtn.Click += (s, e) => ShowRuleMenu(gearBtn, exe, name, defenderFolder?.TrimEnd('\\'), cfgExe);

        // ===== Título sobre el banner =====
        // Sombra débil: degradado oscuro suave que sube desde el borde inferior de la
        // card (donde está el título) para darle legibilidad, sin sombra dura.
        var titleStrip = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = 0 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(150, 0, 0, 0), Offset = 1 }
                }
            },
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(10, 10, 10, 9),
            CornerRadius = new CornerRadius(0) // abajo puntiagudo (sigue al banner)
        };
        // Nombre a la izquierda + logo del launcher a la derecha: el logo identifica
        // la plataforma de cada juego sobre el banner/ícono (Steam, Epic, Battle.net,
        // Ubisoft, EA, GOG y Xbox).
        string? launcherLogo = game?.Launcher switch
        {
            "Steam" => "ms-appx:///logos/launcher/steamlogo.png",
            "Epic" => "ms-appx:///logos/launcher/epicgameslogo.png",
            "Blizzard" => "ms-appx:///logos/launcher/battlenet.png",
            "Ubisoft" => "ms-appx:///logos/launcher/ubisoftlogo.png",
            "EA" => "ms-appx:///logos/launcher/ealogo.png",
            "GOG" => "ms-appx:///logos/launcher/goglogo.png",
            "Xbox" => "ms-appx:///logos/launcher/xboxlogo.png",
            "Riot" => "ms-appx:///logos/launcher/riotlogo.png",
            _ => null
        };
        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nameText = new TextBlock
        {
            Text = name,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameText, 0);
        titleGrid.Children.Add(nameText);
        if (launcherLogo != null)
        {
            try
            {
                var logo = new Image
                {
                    Source = new BitmapImage(new Uri(launcherLogo)),
                    Width = 36,
                    Height = 36,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(logo, 1);
                titleGrid.Children.Add(logo);
            }
            catch { }
        }
        titleStrip.Child = titleGrid;

        // El título va al pie del banner y queda SIEMPRE visible; los botones de
        // acción (estrella, engranaje, iniciar) van en un overlay que aparece al
        // hover (estilo GearUpBooster) — ver el bloque del overlay más abajo.
        bannerGrid.Children.Add(titleStrip);

        // ===== Pie de la card: botón Iniciar (azul) =====
        // Los juegos de Steam se lanzan por steam://rungameid (maneja el DRM y el
        // exe suele estar muy anidado: CS2, Dead by Daylight…). Los de Battle.net
        // se lanzan a través de su launcher con --exec="launch <código>": abrir el
        // exe directo falla si el launcher no está corriendo, porque los juegos de
        // Blizzard necesitan la sesión del launcher. El resto, por su exe.
        bool steamLaunch = game?.Launcher == "Steam" && !string.IsNullOrEmpty(game?.AppId);
        bool riotLaunch = game?.Launcher == "Riot" && !string.IsNullOrEmpty(game?.AppId);
        // El código de producto Battle.net viene del servicio (AppId: product.db o
        // mapeo por carpeta); el mapeo por exe queda como respaldo por si llega vacío.
        string? blizzardCode = game?.Launcher == "Blizzard"
            ? (string.IsNullOrEmpty(game.AppId) ? GameLauncher.GetBlizzardProductCode(exe) : game.AppId)
            : null;
        string launchFile;
        string launchArgs;
        if (steamLaunch)
        {
            launchFile = $"steam://rungameid/{game!.AppId}";
            launchArgs = "";
        }
        else if (game?.Launcher == "Epic" && !string.IsNullOrEmpty(game.EpicAppName))
        {
            // Los juegos de Epic se lanzan por la URI del launcher (como steam://):
            // así el launcher autentica el juego con Epic Online Services. El exe
            // directo abre el juego pero lo online no funciona ("requiere iniciar
            // Epic Games"). LaunchGameAsync asegura que el launcher esté corriendo.
            launchFile = $"com.epicgames.launcher://apps/{game.EpicAppName}?action=launch&silent=true";
            launchArgs = "";
        }
        else if (game?.Launcher == "Xbox" && !string.IsNullOrEmpty(game.AppId))
        {
            // Los juegos de Xbox son paquetes MSIX: se lanzan por su AUMID vía
            // shell:AppsFolder (como el acceso directo del menú Inicio), que activa
            // el paquete con la sesión del app de Xbox. El exe directo no funciona.
            launchFile = $"shell:AppsFolder\\{game.AppId}";
            launchArgs = "";
        }
        else if (riotLaunch)
        {
            // Riot: el lanzamiento lo hace el Riot Client con el id de producto
            // (--launch-product=<id> --launch-patchline=live); la secuencia es
            // Riot Client → client del juego → proceso del juego.
            launchFile = GameLauncher.FindRiotLauncher() ?? "";
            launchArgs = $"--launch-product={game!.AppId} --launch-patchline=live";
        }
        else
        {
            // Battle.net, GOG y el resto: el exe del juego se lanza directo (en
            // Battle.net DESPUÉS de asegurar que el launcher esté corriendo, y en
            // GOG solo si GOG Galaxy está abierto — ver LaunchGameAsync).
            launchFile = exePath ?? "";
            launchArgs = "";
        }

        // Para juegos con launcher externo el botón comunica el estado real y se
        // actualiza en vivo (timer): "Iniciar" si ya está corriendo, o "<Launcher>
        // no iniciado" si hay que abrirlo primero. Battle.net y Epic se abren solos
        // (el juego se lanza después de que levante); GOG Galaxy y Xbox NO se abren
        // solos: si el launcher está cerrado, el botón queda deshabilitado.
        bool epicLaunch = game?.Launcher == "Epic" && !string.IsNullOrEmpty(game.EpicAppName);
        bool gogLaunch = game?.Launcher == "GOG";
        bool xboxLaunch = game?.Launcher == "Xbox";
        string? battleNetLauncher = blizzardCode != null ? GameLauncher.FindBattleNetLauncher() : null;
        string? launcherProc = null;
        bool launcherFound = true;
        bool autoOpen = true;
        if (blizzardCode != null)
        {
            launcherProc = "Battle.net";
            launcherFound = battleNetLauncher != null;
        }
        else if (epicLaunch)
        {
            launcherProc = "EpicGamesLauncher";
            launcherFound = GameLauncher.FindEpicLauncher() != null;
        }
        else if (gogLaunch)
        {
            launcherProc = "GalaxyClient";
            launcherFound = GameLauncher.FindGogLauncher() != null;
            autoOpen = false;
        }
        else if (xboxLaunch)
        {
            // El launcher de Xbox es el app de la Store: si hay juegos instalados,
            // el app existe. El estado real lo gobierna el proceso en ejecución.
            launcherProc = "Xbox";
            launcherFound = true;
            autoOpen = false;
        }
        else if (riotLaunch)
        {
            launcherProc = "RiotClientServices";
            launcherFound = GameLauncher.FindRiotLauncher() != null;
        }
        bool canLaunch = launcherFound && (!string.IsNullOrEmpty(launchFile) || blizzardCode != null);
        var launchBtn = new Button
        {
            Content = I18n.T("Iniciar"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(8, 8, 8, 8),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 12, 7),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Background = ThemeBrushes.Get("AccentBrush"),
            Foreground = ThemeBrushes.Get("AccentForegroundBrush"),
            IsEnabled = canLaunch
        };
        // Hover del botón Iniciar: el estado PointerOver por defecto lo vuelve
        // translúcido; se lo reemplaza por un azul más oscuro (y más oscuro aún al
        // presionar). Los botones de ícono (estrella/engranaje) igual: hover sólido
        // oscuro en vez de translúcido.
        ApplyCardButtonHover(launchBtn, accent: true);
        ApplyCardButtonHover(starBtn, accent: false);
        ApplyCardButtonHover(gearBtn, accent: false);
        if (launcherProc != null)
        {
            _launcherButtons.Add((exe, launchBtn, launcherProc, launcherFound, autoOpen));
            UpdateLauncherButton(launchBtn, launcherProc, launcherFound, launcherFound && IsLauncherRunning(launcherProc), autoOpen);
        }
        else if (!canLaunch)
        {
            ToolTipService.SetToolTip(launchBtn, I18n.T("Ejecutable no encontrado"));
        }
        launchBtn.Click += async (s, e) => await LaunchGameAsync(launchFile, launchArgs, blizzardCode, game?.InstallPath, exe, game?.Launcher);

        // El estado de ejecución se refleja en el propio botón (sin badge aparte):
        // si el juego ya está corriendo, muestra "En ejecución" y queda deshabilitado.
        _gameLaunchButtons.Add((exe, launchBtn, canLaunch));
        if (_runningExes.Contains(exe))
        {
            launchBtn.Content = I18n.T("En ejecución");
            launchBtn.IsEnabled = false;
            ToolTipService.SetToolTip(launchBtn, I18n.T("El juego ya está en ejecución"));
        }

        // ===== Overlay de acciones (estilo GearUpBooster) =====
        // Los botones (engranaje, iniciar y — si no es favorito — la estrella) NO
        // están fijos en la card: aparecen al pasar el mouse con un fade suave y
        // desaparecen al salir. La estrella de un juego ya marcado como favorito
        // queda SIEMPRE visible arriba a la izquierda (se agrega directo al banner,
        // fuera del overlay). El título queda arriba de todo (siempre visible).
        var overlay = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 0, 0, 0)),
            Opacity = 0,
            Visibility = Visibility.Collapsed
        };
        launchBtn.HorizontalAlignment = HorizontalAlignment.Center;
        launchBtn.VerticalAlignment = VerticalAlignment.Center;
        launchBtn.Margin = new Thickness(0);
        launchBtn.MinWidth = 110;
        overlay.Children.Add(gearBtn);
        overlay.Children.Add(launchBtn);
        bannerGrid.Children.Add(overlay);
        // La estrella: dentro del overlay si NO es favorito (aparece al hover),
        // directo al banner (siempre visible) si YA es favorito.
        if (fav)
            bannerGrid.Children.Add(starBtn);
        else
            overlay.Children.Add(starBtn);
        // El título va encima del overlay: se lee siempre, incluso con el overlay visible.
        bannerGrid.Children.Remove(titleStrip);
        bannerGrid.Children.Add(titleStrip);

        var card = new Border
        {
            // Sin reborde (mismo estilo de cards que el resto de la app).
            Background = ThemeBrushes.Get("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            // Sin margin: el Spacing de los StackPanel (horizontal y vertical)
            // maneja todo el espaciado para que sea uniforme (12 px) en ambas
            // direcciones, entre cards de la misma fila y entre filas.
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = banner
        };
        card.PointerEntered += (s, e) => FadeOverlay(overlay, true);
        card.PointerExited += (s, e) => FadeOverlay(overlay, false);

        _cards.Add((card, banner));
        if (!string.IsNullOrEmpty(game?.BannerUrl))
            _ = LoadBannerAsync(banner, mediaImage, emojiText, game!.AppId, game.BannerUrl);
        else if (game?.Launcher == "Epic" && !string.IsNullOrEmpty(game.AppId) && !string.IsNullOrEmpty(game.ArtNamespace))
            _ = LoadEpicBannerAsync(banner, mediaImage, emojiText, game.ArtNamespace, game.AppId, iconPath);
        else if (!string.IsNullOrEmpty(iconPath))
            _ = LoadExeIconAsync(mediaImage, emojiText, iconPath);

        return card;
    }

    /// <summary>
    /// Fade suave del overlay de acciones de una card (estilo GearUpBooster):
    /// 0→1 al entrar el mouse, 1→0 al salir. Detiene cualquier fade en curso para
    /// que entradas/salidas rápidas no se pisen entre sí.
    /// </summary>
    private static void FadeOverlay(Grid overlay, bool show)
    {
        if (overlay.Tag is Storyboard old)
        {
            old.Stop();
            overlay.Tag = null;
        }
        if (show)
        {
            overlay.Visibility = Visibility.Visible;
            overlay.Opacity = 0;
        }
        var anim = new DoubleAnimation
        {
            To = show ? 1.0 : 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, overlay);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (s, e) =>
        {
            overlay.Tag = null;
            if (!show) overlay.Visibility = Visibility.Collapsed;
        };
        overlay.Tag = sb;
        sb.Begin();
    }

    /// <summary>
    /// Hover de los botones de las cards: el estado PointerOver por defecto de
    /// WinUI los vuelve translúcidos. Para el botón Iniciar (accent) se usa un azul
    /// más oscuro que el acento (y más oscuro aún al presionar); para los botones
    /// de ícono (estrella/engranaje) un gris oscuro sólido en vez de translúcido.
    /// </summary>
    private static void ApplyCardButtonHover(Button btn, bool accent)
    {
        try
        {
            if (accent)
            {
                var baseColor = ((SolidColorBrush)ThemeBrushes.Get("AccentBrush")).Color;
                var over = new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                    (byte)(baseColor.R * 0.78), (byte)(baseColor.G * 0.78), (byte)(baseColor.B * 0.78)));
                var pressed = new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                    (byte)(baseColor.R * 0.62), (byte)(baseColor.G * 0.62), (byte)(baseColor.B * 0.62)));
                btn.Resources["ButtonBackgroundPointerOver"] = over;
                btn.Resources["ButtonBackgroundPressed"] = pressed;
                btn.Resources["ButtonForegroundPointerOver"] = ThemeBrushes.Get("AccentForegroundBrush");
                btn.Resources["ButtonForegroundPressed"] = ThemeBrushes.Get("AccentForegroundBrush");
            }
            else
            {
                // Fondo oscuro translúcido por defecto (Argb 100): al hover se hace
                // más sólido, nunca transparente.
                var over = new SolidColorBrush(Windows.UI.Color.FromArgb(170, 0, 0, 0));
                var pressed = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 0, 0, 0));
                btn.Resources["ButtonBackgroundPointerOver"] = over;
                btn.Resources["ButtonBackgroundPressed"] = pressed;
            }
        }
        catch { }
    }

    /// <summary>
    /// Actualiza el texto del item de Defender según el estado actual (en background):
    /// "Excluir" si la carpeta no está excluida, "Quitar exclusión" si ya lo está.
    /// </summary>
    private static async Task RefreshDefenderItemStateAsync(MenuFlyoutItem item, string folder)
    {
        try
        {
            bool excluded = await DefenderService.IsPathExcludedAsync(folder);
            item.Text = I18n.T(excluded ? "Quitar exclusión de Windows Defender" : "Excluir de Windows Defender");
        }
        catch { }
    }

    /// <summary>
    /// Actualiza el texto del item de CFG según el estado actual (en background):
    /// "Desactivar" si el CFG está activo, "Activar" si ya está desactivado.
    /// </summary>
    private static async Task RefreshCfgItemStateAsync(MenuFlyoutItem item, string exeName)
    {
        try
        {
            bool disabled = await CfgService.IsDisabledAsync(exeName);
            item.Text = I18n.T(disabled ? "Activar Control Flow Guard" : "Desactivar Control Flow Guard");
        }
        catch { }
    }

    /// <summary>
    /// Descarga el banner con caché local. Los WebP (Battle.net) se convierten UNA vez
    /// a JPEG acotado: la CDN sirve el box art a 2160×2160 / 3.6 MB y decodificar eso a
    /// resolución completa en cada apertura era el "delay" de Hearthstone. Después de
    /// la conversión, cada apertura decodifica una imagen chica al instante. Si algo
    /// falla, queda el ícono/emoji.
    /// </summary>
    private static async Task LoadBannerAsync(Border banner, Image mediaImage, TextBlock emojiText, string appId, string url)
    {
        try
        {
            string file = Path.Combine(BannerCacheDir, $"{appId}.jpg");
            string webpFile = Path.Combine(BannerCacheDir, $"{appId}.webp");

            // Caché vieja guardada como WebP (antes del fix): convertirla UNA sola vez
            // a JPEG acotado y borrar el .webp. Así las siguientes aperturas decodifican
            // una imagen chica y no dependen del codec WebP del sistema.
            if (File.Exists(webpFile) && !File.Exists(file))
            {
                byte[]? jpeg = await ConvertWebpToJpegAsync(await File.ReadAllBytesAsync(webpFile));
                if (jpeg != null)
                {
                    File.WriteAllBytes(file, jpeg);
                    try { File.Delete(webpFile); } catch { }
                }
            }

            if (!File.Exists(file))
            {
                try
                {
                    Directory.CreateDirectory(BannerCacheDir);
                    using var http = new HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(10);
                    var bytes = await http.GetByteArrayAsync(url);
                    if (IsWebp(bytes))
                    {
                        // Nunca se guarda WebP: convertir a JPEG acotado. Si no se puede
                        // (sistema sin codec WebP), no cachear nada: la card usa el ícono.
                        byte[]? jpeg = await ConvertWebpToJpegAsync(bytes);
                        if (jpeg == null) return;
                        File.WriteAllBytes(file, jpeg);
                    }
                    else
                    {
                        File.WriteAllBytes(file, bytes);
                    }
                }
                catch { return; }
            }
            // Validar que la caché sea una imagen real (JPEG/PNG); si está corrupta,
            // descartarla y dejar el ícono/emoji (no un banner vacío con todo oculto).
            if (!IsValidImageFile(file))
            {
                try { File.Delete(file); } catch { }
                return;
            }
            ApplyBannerFile(banner, mediaImage, emojiText, file);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Banner {appId}: {ex.Message}");
        }
    }

    /// <summary>Aplica un archivo de imagen ya validado como fondo 16:9 de la card.</summary>
    private static void ApplyBannerFile(Border banner, Image mediaImage, TextBlock emojiText, string file)
    {
        var bmp = new BitmapImage(new Uri(file));
        // Decodificar a tamaño de card (no a resolución completa): banners tipo
        // 2160×2160 (Battle.net) o 2560×1440 (Epic) decodifican mucho más rápido y
        // con mucha menos memoria si se pide el decode ya escalado.
        bmp.DecodePixelWidth = 640;
        // El ícono/emoji quedaría flotando sobre la imagen: se ocultan al aplicar el banner.
        mediaImage.Visibility = Visibility.Collapsed;
        emojiText.Visibility = Visibility.Collapsed;
        banner.Background = new ImageBrush
        {
            ImageSource = bmp,
            Stretch = Stretch.UniformToFill
        };
    }

    /// <summary>
    /// Convierte un WebP a JPEG acotado a ~640 px de ancho usando WIC vía WinRT
    /// (Windows.Graphics.Imaging, sin dependencias nuevas). La CDN de Battle.net sirve
    /// box arts de 2160×2160 / 3.6 MB: convertido una vez, cada apertura de la
    /// biblioteca decodifica una imagen chica. Devuelve null si no se pudo (p. ej.
    /// sistema sin codec WebP).
    /// </summary>
    private static async Task<byte[]?> ConvertWebpToJpegAsync(byte[] webp)
    {
        const uint MaxWidth = 640;
        try
        {
            using var inStream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(inStream);
            writer.WriteBytes(webp);
            await writer.StoreAsync();
            // OJO: DataWriter.Dispose() dispone el stream subyacente, así que el
            // writer vive hasta el final del método (nunca se descarta antes de
            // usar el stream). Descartarlo temprano tira ObjectDisposedException.
            inStream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(inStream);
            uint w = decoder.PixelWidth, h = decoder.PixelHeight;
            if (w == 0 || h == 0) return null;
            uint tw = Math.Min(w, MaxWidth);
            uint th = Math.Max(1, (uint)Math.Round((double)h * tw / w));

            var transform = new BitmapTransform
            {
                ScaledWidth = tw,
                ScaledHeight = th,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            var bmp = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using var outStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream);
            encoder.SetSoftwareBitmap(bmp);
            await encoder.FlushAsync();

            outStream.Seek(0);
            using var reader = new DataReader(outStream);
            await reader.LoadAsync((uint)outStream.Size);
            var result = new byte[outStream.Size];
            reader.ReadBytes(result);
            return result;
        }
        catch
        {
            return null;
        }
    }

    // El catálogo del storefront de Epic está detrás de Cloudflare, que bloquea el
    // TLS fingerprint del HttpClient de .NET (403). La fuente confiable es el caché
    // local del catálogo que el propio launcher descarga (catcache.bin, base64 → JSON):
    // ahí está cada juego con su keyImages (DieselGameBox 2560×1440 ideal para la card).
    private static readonly object EpicCatalogCacheLock = new();
    private static DateTime _epicCatalogCacheStamp;
    private static List<(string Ns, string Id, string Url)>? _epicCatalogCache;

    /// <summary>
    /// Busca el banner de un juego de Epic en el caché local del catálogo del launcher
    /// (catcache.bin) usando el CatalogItemId + CatalogNamespace del manifest, con
    /// caché propia en disco. Si falla (launcher nunca abierto, juego dado de baja...),
    /// cae al ícono del exe.
    /// </summary>
    private static async Task LoadEpicBannerAsync(Border banner, Image mediaImage, TextBlock emojiText, string ns, string catalogItemId, string? exePath)
    {
        void FallbackToIcon()
        {
            if (!string.IsNullOrEmpty(exePath))
                _ = LoadExeIconAsync(mediaImage, emojiText, exePath);
        }

        try
        {
            string file = Path.Combine(BannerCacheDir, $"epic-{ns}-{catalogItemId}.jpg");
            if (!File.Exists(file))
            {
                try
                {
                    string? url = FindEpicKeyImageUrl(ns, catalogItemId);
                    if (url == null) { FallbackToIcon(); return; }
                    using var http = new HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(15);
                    var bytes = await http.GetByteArrayAsync(url);
                    if (!IsValidImageBytes(bytes)) { FallbackToIcon(); return; }
                    Directory.CreateDirectory(BannerCacheDir);
                    File.WriteAllBytes(file, bytes);
                }
                catch { FallbackToIcon(); return; }
            }
            if (IsValidImageFile(file))
            {
                ApplyBannerFile(banner, mediaImage, emojiText, file);
            }
            else
            {
                // Caché corrupta: descartarla y caer al ícono.
                try { File.Delete(file); } catch { }
                FallbackToIcon();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Banner Epic {ns}/{catalogItemId}: {ex.Message}");
            FallbackToIcon();
        }
    }

    /// <summary>
    /// Devuelve la URL 16:9 (DieselGameBox 2560×1440 idealmente) del catálogo local de
    /// Epic para el CatalogItemId del manifest.
    /// </summary>
    private static string? FindEpicKeyImageUrl(string ns, string catalogItemId)
    {
        var cache = LoadEpicCatalogCache();
        if (cache == null) return null;
        foreach (var (itemNs, id, url) in cache)
        {
            if (!string.Equals(id, catalogItemId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(itemNs) && !string.Equals(itemNs, ns, StringComparison.OrdinalIgnoreCase)) continue;
            return url;
        }
        return null;
    }

    /// <summary>
    /// Lee y parsea catcache.bin (base64 → JSON) del launcher de Epic, con caché en
    /// memoria mientras el archivo no cambie.
    /// </summary>
    private static List<(string Ns, string Id, string Url)>? LoadEpicCatalogCache()
    {
        string file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");
        lock (EpicCatalogCacheLock)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(file);
                if (_epicCatalogCache != null && stamp == _epicCatalogCacheStamp)
                    return _epicCatalogCache;

                string b64;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                    b64 = sr.ReadToEnd();
                string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim()));
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                var list = new List<(string, string, string)>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string? id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;
                    string? itemNs = item.TryGetProperty("namespace", out var n) ? n.GetString() : null;
                    string? url = null;
                    string? wide = null;
                    string? any = null;
                    if (item.TryGetProperty("keyImages", out var images))
                    {
                        foreach (var img in images.EnumerateArray())
                        {
                            if (!img.TryGetProperty("url", out var u)) continue;
                            string? u2 = u.GetString();
                            if (string.IsNullOrEmpty(u2)) continue;
                            string type = img.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                            // 16:9 para la card: DieselGameBox (2560×1440) es el ideal;
                            // otras wide/GameBox como respaldo y la primera como último recurso.
                            if (type == "DieselGameBox") { url = u2; break; }
                            if (type is "DieselGameBoxWide" or "OfferImageWide" or "DieselStoreFrontWide")
                                wide ??= u2;
                            any ??= u2;
                        }
                    }
                    list.Add((itemNs ?? "", id, url ?? wide ?? any ?? ""));
                }

                _epicCatalogCache = list;
                _epicCatalogCacheStamp = stamp;
                return _epicCatalogCache;
            }
            catch
            {
                return _epicCatalogCache;
            }
        }
    }

    /// <summary>Valida la firma de un buffer de imagen (JPEG/PNG/WebP).</summary>
    private static bool IsValidImageBytes(byte[] bytes)
    {
        if (bytes.Length < 12) return false;
        Span<byte> head = bytes.AsSpan(0, 12);
        return IsValidImageSignature(head);
    }

    private static bool IsValidImageSignature(ReadOnlySpan<byte> h)
    {
        // JPEG: FF D8 FF ... | PNG: 89 50 4E 47 0D 0A 1A 0A | WebP: RIFF....WEBP
        return (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
            || (h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47)
            || IsWebp(h);
    }

    /// <summary>¿La firma es WebP (RIFF....WEBP)? El box art de Battle.net viene en WebP.</summary>
    private static bool IsWebp(ReadOnlySpan<byte> h)
    {
        return h.Length >= 12
            && h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46
            && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50;
    }

    // ===== Estado en vivo de Battle.net =====

    /// <summary>
    /// Refleja el estado real del launcher de Battle.net en el botón de la card:
    /// "Iniciar" si está corriendo (el juego se lanza vía el launcher), "Battle.net
    /// no iniciado" si hay que abrirlo primero, y deshabilitado con tooltip si el
    /// launcher no está instalado. Lo llama el timer cada pocos segundos para que el
    /// estado no quede congelado mientras la página está abierta.
    /// </summary>
    /// <summary>
    /// Actualiza el botón de un juego que depende de un launcher externo (Battle.net,
    /// Epic Games, GOG Galaxy o Xbox): "Iniciar" si el launcher ya está corriendo, o
    /// "<Launcher> no iniciado" si hay que abrirlo primero. Con autoOpen=true
    /// (Battle.net/Epic) el botón queda habilitado y el clic abre el launcher solo;
    /// con autoOpen=false (GOG/Xbox) queda deshabilitado: el launcher NO se abre
    /// solo, hay que abrirlo a mano. Deshabilitado también si el launcher no está
    /// instalado. Lo llama el timer cada pocos segundos para que el estado no quede
    /// congelado mientras la página está abierta.
    /// </summary>
    private static void UpdateLauncherButton(Button btn, string processName, bool launcherFound, bool running, bool autoOpen)
    {
        string displayName = processName switch
        {
            "Battle.net" => "Battle.net",
            "EpicGamesLauncher" => "Epic Games",
            "GalaxyClient" => "GOG Galaxy",
            "Xbox" => "Xbox",
            "RiotClientServices" => "Riot Client",
            _ => processName
        };
        if (!launcherFound)
        {
            btn.Content = I18n.T("{0} no iniciado", displayName);
            btn.IsEnabled = false;
            ToolTipService.SetToolTip(btn, I18n.T("No se encontró el launcher de {0}.", displayName));
        }
        else if (running)
        {
            btn.Content = I18n.T("Iniciar");
            btn.IsEnabled = true;
            ToolTipService.SetToolTip(btn, null);
        }
        else if (autoOpen)
        {
            btn.Content = I18n.T("{0} no iniciado", displayName);
            btn.IsEnabled = true;
            ToolTipService.SetToolTip(btn, I18n.T("Se abrirá {0} y luego se iniciará el juego.", displayName));
        }
        else
        {
            // GOG/Xbox: el launcher no se abre solo — avisar que hay que abrirlo.
            btn.Content = I18n.T("{0} no iniciado", displayName);
            btn.IsEnabled = false;
            ToolTipService.SetToolTip(btn, I18n.T("Abrí {0} y volvé a intentar.", displayName));
        }
    }

    /// <summary>
    /// Refleja el estado de los launchers en los botones "Iniciar". Se llama al
    /// abrir la página (chequeo único) y cuando un launcher nace o muere (eventos
    /// WMI). No hay polling periódico.
    /// </summary>
    private void UpdateAllLauncherButtons()
    {
        if (_launcherButtons.Count == 0) return;
        foreach (var (exe, btn, procName, launcherFound, autoOpen) in _launcherButtons)
        {
            // Si el juego ya está corriendo, el botón muestra "En ejecución" (lo
            // maneja RunningGamesChanged): el estado del launcher no debe pisarlo.
            if (_runningExes.Contains(exe)) continue;
            UpdateLauncherButton(btn, procName, launcherFound, launcherFound && IsLauncherRunning(procName), autoOpen);
        }
    }

    private void OnLauncherStateChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateAllLauncherButtons);
    }

    /// <summary>
    /// ¿El launcher indicado está corriendo? El app de Xbox cambió de nombre varias
    /// veces (XboxStub del app nuevo, Xbox del clásico, GamingApp de versiones
    /// intermedias): se aceptan todos para no dejar el botón congelado en "no
    /// iniciado" cuando el app está abierto.
    /// </summary>
    private bool IsLauncherRunning(string launcherProc)
        => GameLauncher.IsLauncherRunning(_processService, launcherProc);

    /// <summary>
    /// Extrae el ícono del exe del juego (recurso del ejecutable) y lo muestra; si
    /// falla, queda el emoji. El PNG se guarda en caché y se carga DESDE ARCHIVO
    /// (igual que los banners): SetSource con un MemoryStream descartado dejaba la
    /// imagen en blanco porque BitmapImage decodifica en forma asíncrona.
    /// </summary>
    private static async Task LoadExeIconAsync(Image mediaImage, TextBlock emojiText, string exePath)
    {
        try
        {
            // Sufijo "-hi": la caché vieja guardaba íconos estirados de 32 px (borrosos);
            // con el sufijo nuevo se re-extrae todo en alta resolución (hasta 256 px).
            // "exeicons-v2": descarta la v1 que podía guardar íconos legacy con el
            // dibujo chico en una esquina de la tela (ver TrimTransparentMargins).
            string cacheDir = Path.Combine(BannerCacheDir, "exeicons-v2");
            string cacheBase = Path.Combine(cacheDir, HashString(exePath));
            string cacheFile = cacheBase + "-hi.png";

            // Ícono propio del juego (.ico en su carpeta): los juegos VIEJOS a menudo
            // no traen recurso de ícono en el exe (o solo 16/32 px) pero sí un .ico
            // (game.ico, icon.ico o el mismo nombre del exe). Se prefiere al del exe.
            string icoCache = cacheBase + "-ico.png";
            if (!File.Exists(icoCache))
            {
                string? local = await Task.Run(() => IconExtractor.FindConfidentLocalIcon(exePath));
                if (local != null)
                {
                    bool okIco = await Task.Run(() =>
                    {
                        try
                        {
                            Directory.CreateDirectory(cacheDir);
                            using var ico = new System.Drawing.Icon(local, 64, 64);
                            using var src = ico.ToBitmap();
                            using var bmp = new System.Drawing.Bitmap(64, 64);
                            using var g = System.Drawing.Graphics.FromImage(bmp);
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.Clear(System.Drawing.Color.Transparent);
                            g.DrawImage(src, 0, 0, 64, 64);
                            bmp.Save(icoCache, System.Drawing.Imaging.ImageFormat.Png);
                            return true;
                        }
                        catch { return false; }
                    });
                    if (okIco && IsValidImageFile(icoCache))
                    {
                        mediaImage.Source = new BitmapImage(new Uri(icoCache));
                        mediaImage.Visibility = Visibility.Visible;
                        emojiText.Visibility = Visibility.Collapsed;
                        return;
                    }
                }
            }
            else if (IsValidImageFile(icoCache))
            {
                mediaImage.Source = new BitmapImage(new Uri(icoCache));
                mediaImage.Visibility = Visibility.Visible;
                emojiText.Visibility = Visibility.Collapsed;
                return;
            }

            if (!File.Exists(cacheFile))
            {
                bool ok = await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(exePath)) return false;
                        Directory.CreateDirectory(cacheDir);
                        // Ícono en alta resolución (lista JUMBO del shell, hasta 256 px;
                        // fallback al asociado de 32 px si el exe no tiene más grande).
                        System.Drawing.Bitmap? src = IconExtractor.ExtractHighResIcon(exePath);
                        if (src == null)
                        {
                            using var small = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                            if (small == null) return false;
                            src = small.ToBitmap();
                        }
                        using (src)
                        using (var bmp = new System.Drawing.Bitmap(64, 64))
                        {
                            using var g = System.Drawing.Graphics.FromImage(bmp);
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                            g.Clear(System.Drawing.Color.Transparent);
                            // Fuente >= 64 px → downscale (nítido); fuente chica → upscale
                            // (exes viejos; es lo máximo que tienen).
                            g.DrawImage(src, 0, 0, 64, 64);
                            // Solo perseguir como "-hi" si la fuente es de alta resolución.
                            // Si el shell devolvió un ícono chico (fallback), se guarda como
                            // "-small": así la próxima apertura REINTENTA el hi-res (el shell
                            // suele tenerlo cacheado para entonces) en vez de quedar
                            // congelado con el ícono estirado para siempre.
                            bmp.Save((src.Width >= 64 && src.Height >= 64 ? cacheFile : cacheBase + "-small.png"),
                                System.Drawing.Imaging.ImageFormat.Png);
                        }
                        return File.Exists(cacheFile);
                    }
                    catch { return false; }
                });
                // Si no se obtuvo hi-res, mostrar el fallback -small de esta corrida
                // (mejor que el emoji), y reintentar en la próxima apertura.
                if (!ok)
                {
                    string small = cacheBase + "-small.png";
                    if (File.Exists(small) && IsValidImageFile(small))
                    {
                        mediaImage.Source = new BitmapImage(new Uri(small));
                        mediaImage.Visibility = Visibility.Visible;
                        emojiText.Visibility = Visibility.Collapsed;
                    }
                    return;
                }
            }
            if (!IsValidImageFile(cacheFile)) return;
            mediaImage.Source = new BitmapImage(new Uri(cacheFile));
            mediaImage.Visibility = Visibility.Visible;
            emojiText.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    /// <summary>Hash corto y estable de una ruta (para el nombre del archivo de caché del ícono).</summary>
    private static string HashString(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16];
    }


    /// <summary>Valida la firma del archivo de imagen (JPEG/PNG/WebP) para no usar caché corrupta.</summary>
    private static bool IsValidImageFile(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            if (fs.Length < 12) return false;
            Span<byte> head = stackalloc byte[12];
            fs.ReadExactly(head);
            // JPEG: FF D8 FF ... | PNG: 89 50 4E 47 0D 0A 1A 0A | WebP: RIFF....WEBP
            return IsValidImageSignature(head);
        }
        catch { return false; }
    }

    // ===== Grilla de N columnas =====

    // El scrollbar nativo del LibraryScroll es overlay: se dibuja POR ENCIMA del
    // contenido. La canaleta se reserva SIEMPRE (margin fijo del LibraryPanel, 14px)
    // para que el scroll quede por fuera de las cards y nunca las tape; al ser fija,
    // las cards no cambian de ancho cuando el scrollbar aparece o desaparece.
    private const double ScrollBarReserve = 14;

    private void LibraryScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardWidth();

    private void UpdateCardWidth()
    {
        double w = LibraryScroll.ActualWidth;
        if (w <= 0 || (_cards.Count == 0 && _skeletonCards.Count == 0)) return;
        // Reserva fija de la canaleta del scrollbar (ver comentario arriba).
        double reserve = ScrollBarReserve;
        // N columnas exactas: cada fila es un StackPanel horizontal (Spacing 12) y la
        // card tiene margen derecho 12, así que cols*(cardWidth+12)+(cols-1)*12 = w.
        // El padding de 32 del GridView original ya no existe (el ScrollViewer
        // lleva el margen).
        int cols = GridColumns;
        double cardWidth = Math.Max(230, (w - 48 - reserve) / cols);
        double bannerMin = 110;
        foreach (var (cardEl, banner) in _cards)
        {
            cardEl.Width = cardWidth;
            // El banner ocupa el ancho completo de la card (sin márgenes): 16:9 real.
            banner.Height = Math.Max(bannerMin, cardWidth * 9.0 / 16.0);
        }
        // Misma geometría para el skeleton (si está visible en la grilla).
        foreach (var (card, banner, _) in _skeletonCards)
        {
            card.Width = cardWidth;
            banner.Height = Math.Max(bannerMin, cardWidth * 9.0 / 16.0);
        }
    }

    // ===== Tuerca → desplegable simple (submenús) =====

    private void ShowRuleMenu(FrameworkElement target, string exe, string name, string? defenderFolder = null, string? cfgExe = null)
    {
        var rules = _processService.GetRules();
        var rule = rules.TryGetValue(exe, out var r) ? r : new ProcessRule(null, null, null);
        // Regla de sesión ("Actual"): solo la apertura actual del juego, en memoria.
        var sessionRule = _processService.GetSessionRule(exe) ?? new ProcessRule(null, null, null);
        int procCount = _processService.ProcessorCount;
        long fullMask = procCount >= 64 ? -1L : ((1L << procCount) - 1);

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        // Aplica en vivo la regla EFECTIVA (la de sesión gana campo por campo sobre
        // la guardada) o restaura los valores del sistema si no queda ninguna.
        void ApplyEffectiveToRunning(bool sessionScope)
        {
            // Aplicar a TODOS los procesos que matchean la regla (launcher + juego
            // real + stub de anti-cheat): si la regla está sobre el launcher, la
            // afinidad/prioridad también llega al proceso real del juego.
            var apps = _processService.FindRunningProcessesForRule(exe);
            if (apps.Count == 0)
            {
                StatusText.Text = sessionScope
                    ? I18n.T("Reglas de sesión listas. Se aplicarán cuando el juego se abra.")
                    : I18n.T("Reglas guardadas. Se aplicarán cuando el juego esté en ejecución.");
                StatusText.Foreground = Feedback.MutedBrush;
                StatusText.Visibility = Visibility.Visible;
                return;
            }

            var effective = _processService.GetEffectiveRule(exe);
            var anyFailed = new RuleApplyFeedback(false, false, false);
            foreach (var app in apps)
            {
                var fb = RuleIsEmpty(effective)
                    ? _processService.ApplyRuleWithFeedback(app, new ProcessRule(2, fullMask, 3, null, 2))
                    : _processService.ApplyRuleWithFeedback(app, effective);
                anyFailed = anyFailed with
                {
                    CpuFailed = anyFailed.CpuFailed || fb.CpuFailed,
                    AffinityFailed = anyFailed.AffinityFailed || fb.AffinityFailed,
                    GpuFailed = anyFailed.GpuFailed || fb.GpuFailed,
                    IoFailed = anyFailed.IoFailed || fb.IoFailed
                };
            }

            // Plan de energía: una sola vez (idempotente), no por cada proceso.
            if (RuleIsEmpty(effective))
                _processService.RevertPowerPlanIfApplied(exe);
            else if (!string.IsNullOrEmpty(effective.PowerPlanGuid))
                _processService.ApplyPowerPlanIfRunning(exe, effective.PowerPlanGuid);
            else
                _processService.RevertPowerPlanIfApplied(exe);

            if (anyFailed.AnyFailed)
            {
                // Proceso protegido por anti-cheat (EAC) o cerrado: avisar en vez
                // de fallar en silencio. La prioridad de CPU igual queda fijada al
                // nacer por registro (PerfOptions).
                var parts = new List<string>();
                if (anyFailed.CpuFailed) parts.Add(I18n.T("prioridad de CPU"));
                if (anyFailed.AffinityFailed) parts.Add(I18n.T("afinidad"));
                if (anyFailed.GpuFailed) parts.Add(I18n.T("prioridad de GPU"));
                if (anyFailed.IoFailed) parts.Add(I18n.T("prioridad de E/S"));
                StatusText.Text = I18n.T("No se pudo aplicar {0} en vivo a {1} (proceso protegido o cerrado). La prioridad de CPU queda fijada al nacer por registro.",
                    string.Join(", ", parts), name);
                StatusText.Foreground = Feedback.WarningBrush;
            }
            else
            {
                StatusText.Text = RuleIsEmpty(_processService.GetEffectiveRule(exe))
                    ? I18n.T("Valores por defecto restaurados en {0}", exe)
                    : sessionScope
                        ? I18n.T("Reglas de sesión aplicadas a {0}", exe)
                        : I18n.T("Reglas aplicadas a {0}", exe);
                StatusText.Foreground = Feedback.SuccessBrush;
            }
            StatusText.Visibility = Visibility.Visible;
        }

        // "Siempre": se guarda en el registro y aplica en cada apertura del juego.
        void ApplyAndSave(ProcessRule newRule)
        {
            _processService.SaveRule(exe, newRule);
            ApplyEffectiveToRunning(sessionScope: false);
        }

        // "Actual": solo la apertura actual del juego (en memoria, sin guardar).
        void ApplySessionAndNotify()
        {
            _processService.SetSessionRule(exe, sessionRule);
            ApplyEffectiveToRunning(sessionScope: true);
        }

        // ¿La regla no configura nada? (todo "Por defecto")
        bool RuleIsEmpty(ProcessRule? r)
            => r == null
            || (r.CpuPriority == null && r.AffinityMask == null && r.GpuPriority == null
                && string.IsNullOrEmpty(r.PowerPlanGuid)
                && r.IoPriority == null);

        // Submenú de un alcance ("Actual"/"Siempre") para un ajuste de valor único
        // (prioridad de CPU/GPU, plan de energía): opción marcada + clic → onPick(índice),
        // donde 0 = "Por defecto".
        MenuFlyoutSubItem BuildScope(string scopeLabel, string[] labels, int selected, Action<int> onPick)
        {
            var sub = new MenuFlyoutSubItem { Text = scopeLabel };
            var items = new List<ToggleMenuFlyoutItem>();
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var item = new ToggleMenuFlyoutItem { Text = I18n.T(labels[i]), IsChecked = idx == selected };
                item.Click += (s, e) =>
                {
                    foreach (var it in items) it.IsChecked = it == item;
                    onPick(idx);
                };
                items.Add(item);
                sub.Items.Add(item);
            }
            return sub;
        }

        // Submenú de alcance para la afinidad (checks por núcleo).
        MenuFlyoutSubItem BuildAffinityScope(string scopeLabel, long? mask, Action<long?> onPick)
        {
            var sub = new MenuFlyoutSubItem { Text = scopeLabel };
            bool allCores = mask == null || mask == fullMask;
            var coreItems = new List<ToggleMenuFlyoutItem>();
            for (int i = 0; i < procCount; i++)
            {
                int ci = i;
                var item = new ToggleMenuFlyoutItem
                {
                    Text = I18n.T("Núcleo {0}", ci + 1),
                    IsChecked = allCores || (mask!.Value & (1L << ci)) != 0
                };
                item.Click += (s, e) =>
                {
                    long m = 0;
                    for (int k = 0; k < coreItems.Count; k++)
                        if (coreItems[k].IsChecked == true)
                            m |= 1L << k;
                    // Todos marcados (o ninguno) = máscara completa EXPLÍCITA: restaura
                    // todos los núcleos. Antes se guardaba null ("no tocar") y si el juego
                    // ya tenía una afinidad restringida de una regla previa, elegir "todos
                    // los núcleos" no la restauraba.
                    long? affinity = (m == 0 || m == fullMask) ? fullMask : m;
                    onPick(affinity);
                };
                coreItems.Add(item);
                sub.Items.Add(item);
            }
            return sub;
        }

        // ===== Prioridad de CPU: alcance "Actual" (solo esta apertura) / "Siempre" =====
        var cpuSub = new MenuFlyoutSubItem { Text = I18n.T("Prioridad de CPU") };
        string[] cpuNames = { "Por defecto", "Mínima", "Baja", "Normal", "Por encima de lo normal", "Alta", "Tiempo real" };
        int cpuPerSel = rule.CpuPriority is int cp ? Array.IndexOf(CpuPriorityValues, cp) + 1 : 0;
        int cpuSesSel = sessionRule.CpuPriority is int scp ? Array.IndexOf(CpuPriorityValues, scp) + 1 : 0;
        cpuSub.Items.Add(BuildScope(I18n.T("Actual"), cpuNames, cpuSesSel, idx =>
        {
            sessionRule = new ProcessRule(idx <= 0 ? null : CpuPriorityValues[idx - 1], sessionRule.AffinityMask, sessionRule.GpuPriority, sessionRule.PowerPlanGuid, sessionRule.IoPriority);
            ApplySessionAndNotify();
        }));
        cpuSub.Items.Add(BuildScope(I18n.T("Siempre"), cpuNames, cpuPerSel, idx =>
        {
            rule = new ProcessRule(idx <= 0 ? null : CpuPriorityValues[idx - 1], rule.AffinityMask, rule.GpuPriority, rule.PowerPlanGuid, rule.IoPriority);
            ApplyAndSave(rule);
        }));
        menu.Items.Add(cpuSub);

        // ===== Afinidad de CPU: checks por núcleo, con alcance Actual/Siempre =====
        // Por defecto TODOS seleccionados (afinidad sin restricción = todos los
        // núcleos). Desmarcar uno fija la máscara real.
        var affSub = new MenuFlyoutSubItem { Text = I18n.T("Afinidad de CPU") };
        affSub.Items.Add(BuildAffinityScope(I18n.T("Actual"), sessionRule.AffinityMask, aff =>
        {
            sessionRule = new ProcessRule(sessionRule.CpuPriority, aff, sessionRule.GpuPriority, sessionRule.PowerPlanGuid, sessionRule.IoPriority);
            ApplySessionAndNotify();
        }));
        affSub.Items.Add(BuildAffinityScope(I18n.T("Siempre"), rule.AffinityMask, aff =>
        {
            rule = new ProcessRule(rule.CpuPriority, aff, rule.GpuPriority, rule.PowerPlanGuid, rule.IoPriority);
            ApplyAndSave(rule);
        }));
        menu.Items.Add(affSub);

        // ===== Prioridad de GPU: alcance Actual/Siempre =====
        var gpuSub = new MenuFlyoutSubItem { Text = I18n.T("Prioridad de GPU") };
        string[] gpuNames = { "Por defecto", "Baja", "Normal", "Alta" };
        int gpuPerSel = rule.GpuPriority is int gp ? Array.IndexOf(GpuPriorityValues, gp) + 1 : 0;
        int gpuSesSel = sessionRule.GpuPriority is int sgp ? Array.IndexOf(GpuPriorityValues, sgp) + 1 : 0;
        gpuSub.Items.Add(BuildScope(I18n.T("Actual"), gpuNames, gpuSesSel, idx =>
        {
            sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, idx <= 0 ? null : GpuPriorityValues[idx - 1], sessionRule.PowerPlanGuid, sessionRule.IoPriority);
            ApplySessionAndNotify();
        }));
        gpuSub.Items.Add(BuildScope(I18n.T("Siempre"), gpuNames, gpuPerSel, idx =>
        {
            rule = new ProcessRule(rule.CpuPriority, rule.AffinityMask, idx <= 0 ? null : GpuPriorityValues[idx - 1], rule.PowerPlanGuid, rule.IoPriority);
            ApplyAndSave(rule);
        }));
        menu.Items.Add(gpuSub);

        // ===== Prioridad de E/S: alcance Actual/Siempre =====
        // IO_PRIORITY_HINT: define quién gana cuando varios procesos compiten por el
        // disco. Se aplica en vivo como la GPU (sin clave de nacimiento).
        var ioSub = new MenuFlyoutSubItem { Text = I18n.T("Prioridad de E/S") };
        string[] ioNames = { "Por defecto", "Muy baja", "Baja", "Normal", "Alta", "Crítica" };
        int ioPerSel = rule.IoPriority is int io ? Array.IndexOf(IoPriorityValues, io) + 1 : 0;
        int ioSesSel = sessionRule.IoPriority is int sio ? Array.IndexOf(IoPriorityValues, sio) + 1 : 0;
        ioSub.Items.Add(BuildScope(I18n.T("Actual"), ioNames, ioSesSel, idx =>
        {
            sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, sessionRule.GpuPriority, sessionRule.PowerPlanGuid, idx <= 0 ? null : IoPriorityValues[idx - 1]);
            ApplySessionAndNotify();
        }));
        ioSub.Items.Add(BuildScope(I18n.T("Siempre"), ioNames, ioPerSel, idx =>
        {
            rule = new ProcessRule(rule.CpuPriority, rule.AffinityMask, rule.GpuPriority, rule.PowerPlanGuid, idx <= 0 ? null : IoPriorityValues[idx - 1]);
            ApplyAndSave(rule);
        }));
        menu.Items.Add(ioSub);

        // ===== Verificación en vivo (solo si el juego está corriendo) =====
        // Matching por ruta: funciona también para procesos cuyo nombre difiere
        // del exe detectado (ej. SmiteGame-Win64-Shipping.exe con regla Smite.exe).
        var runningApp = _processService.FindRunningProcess(exe);
        if (runningApp != null)
        {
            var realGpu = _processService.GetGpuPriority(runningApp.Id);
            string infoText;
            if (realGpu == null)
            {
                int st = _processService.LastGpuPriorityStatus;
                // 0xC0000022 = STATUS_ACCESS_DENIED: el proceso está protegido (anti-cheat)
                // o no se puede leer. No es un error de la app: ni Windows lo permite.
                infoText = st == unchecked((int)0xC0000022)
                    ? I18n.T("Prioridad GPU actual: no se pudo leer (proceso protegido: anti-cheat)")
                    : I18n.T("Prioridad GPU actual: no se pudo leer (error 0x{0:X8})", st);
            }
            else
            {
                string gpuLabel = realGpu switch
                {
                    2 => I18n.T("Baja"),
                    3 => I18n.T("Normal"),
                    4 => I18n.T("Alta"),
                    _ => realGpu.Value.ToString()
                };
                infoText = I18n.T("Prioridad GPU actual: {0} ({1})", gpuLabel, realGpu.Value);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = infoText, IsEnabled = false });

            var realIo = _processService.GetIoPriority(runningApp.Id);
            string ioText = realIo == null
                ? I18n.T("Prioridad E/S actual: no se pudo leer (proceso protegido: anti-cheat)")
                : I18n.T("Prioridad E/S actual: {0} ({1})", IoLabel(realIo.Value), realIo.Value);
            menu.Items.Add(new MenuFlyoutItem { Text = ioText, IsEnabled = false });
        }

        // ===== Plan de energía: desplegable directo de los planes instalados.
        // Se activa al correr el juego y se revierte al cerrar (por juego solo
        // aplica en la sesión actual; el plan permanente se maneja en el apartado
        // "Núcleos y Plan de energía"). =====
        var planSub = new MenuFlyoutSubItem { Text = I18n.T("Plan de energía") };
        var plans = _cpuPowerService.GetPowerPlans();
        var planItems = new List<ToggleMenuFlyoutItem>();

        // "Por defecto" = sin regla de plan (el juego no cambia el plan del sistema).
        var noneItem = new ToggleMenuFlyoutItem
        {
            Text = I18n.T("Por defecto"),
            IsChecked = string.IsNullOrEmpty(sessionRule.PowerPlanGuid)
        };            noneItem.Click += (s, e) =>
            {
                foreach (var it in planItems) it.IsChecked = it == noneItem;
                sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, sessionRule.GpuPriority, null, sessionRule.IoPriority);
                ApplySessionAndNotify();
            };
        planItems.Add(noneItem);
        planSub.Items.Add(noneItem);
        planSub.Items.Add(new MenuFlyoutSeparator());

        for (int pi = 0; pi < plans.Count; pi++)
        {
            var plan = plans[pi];
            var item = new ToggleMenuFlyoutItem
            {
                Text = plan.Name,
                IsChecked = string.Equals(sessionRule.PowerPlanGuid, plan.Guid, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (s, e) =>
            {
                foreach (var it in planItems) it.IsChecked = it == item;
                // NO se cambia el plan en el momento: se activa cuando el juego
                // corre y se revierte al plan por defecto al cerrar.
                sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, sessionRule.GpuPriority, plan.Guid, sessionRule.IoPriority);
                ApplySessionAndNotify();
            };
            planItems.Add(item);
            planSub.Items.Add(item);
        }
        if (plans.Count > 0)
            menu.Items.Add(planSub);

        // ===== Windows Defender =====
        // Excepción de la carpeta de instalación del juego (cubre exe + subprocesos).
        // La app corre elevada, así que los cmdlets de Defender no piden UAC.
        // Empieza en "Consultando Windows Defender..." mientras se consulta el
        // estado real ("Excluir" ↔ "Quitar exclusión"). MinWidth fijo: al
        // alternar el texto el item no cambia de tamaño ni envuelve, así el
        // menú no se mueve.
        var defenderItem = new MenuFlyoutItem
        {
            Text = I18n.T("Consultando Windows Defender..."),
            MinWidth = 300
        };
        if (string.IsNullOrEmpty(defenderFolder))
        {
            defenderItem.IsEnabled = false;
            ToolTipService.SetToolTip(defenderItem, I18n.T("Carpeta del juego no encontrada"));
        }
        else
        {
            string defTarget = defenderFolder;
            // El texto refleja el estado real mientras el menú está abierto
            // ("Excluir" vs "Quitar exclusión"): consulta en background, sin
            // retrasar la apertura del menú.
            _ = RefreshDefenderItemStateAsync(defenderItem, defTarget);
            defenderItem.Click += async (s, e) =>
            {
                try
                {
                    bool excluded = await DefenderService.IsPathExcludedAsync(defTarget);
                    var (ok, _) = excluded
                        ? await DefenderService.RemovePathExclusionAsync(defTarget)
                        : await DefenderService.AddPathExclusionAsync(defTarget);
                    if (ok)
                    {
                        StatusText.Text = I18n.T(excluded
                            ? "Excepción de Windows Defender quitada"
                            : "Excepción de Windows Defender agregada");
                        StatusText.Foreground = Feedback.SuccessBrush;
                    }
                    else
                    {
                        StatusText.Text = I18n.T("No se pudo cambiar la excepción de Windows Defender: {0}", defTarget);
                        StatusText.Foreground = Feedback.ErrorBrush;
                    }
                    StatusText.Visibility = Visibility.Visible;
                }
                catch (Exception ex2)
                {
                    _loggingService.LogWarning($"GestionarProcesosPage: excepción de Defender {exe}: {ex2.Message}");
                }
            };
        }
        menu.Items.Add(defenderItem);

        // ===== Control Flow Guard =====
        // Desactiva el CFG SOLO para el ejecutable del juego (IFEO): evita los
        // micro-cortes que causa la inspección de CFG en el código gráfico de
        // DirectX. La recomendación original de la desarrolladora de SMITE 2;
        // aplica a cualquier juego. Empieza en "Consultando..." y MinWidth fijo
        // para que el menú no se mueva al alternar el texto.
        var cfgItem = new MenuFlyoutItem
        {
            Text = I18n.T("Consultando Control Flow Guard..."),
            MinWidth = 300
        };
        if (string.IsNullOrEmpty(cfgExe))
        {
            cfgItem.IsEnabled = false;
            ToolTipService.SetToolTip(cfgItem, I18n.T("Ejecutable del juego no encontrado"));
        }
        else
        {
            string cfgTarget = cfgExe;
            _ = RefreshCfgItemStateAsync(cfgItem, cfgTarget);
            cfgItem.Click += async (s, e) =>
            {
                try
                {
                    bool disabled = await CfgService.IsDisabledAsync(cfgTarget);
                    var (ok, _) = await CfgService.SetAsync(cfgTarget, !disabled);
                    if (ok)
                    {
                        StatusText.Text = I18n.T(disabled
                            ? "Control Flow Guard activado para {0}"
                            : "Control Flow Guard desactivado para {0}", cfgTarget);
                        StatusText.Foreground = Feedback.SuccessBrush;
                    }
                    else
                    {
                        StatusText.Text = I18n.T("No se pudo cambiar el Control Flow Guard: {0}", cfgTarget);
                        StatusText.Foreground = Feedback.ErrorBrush;
                    }
                    StatusText.Visibility = Visibility.Visible;
                }
                catch (Exception ex2)
                {
                    _loggingService.LogWarning($"GestionarProcesosPage: CFG {exe}: {ex2.Message}");
                }
            };
        }
        menu.Items.Add(cfgItem);

        // ===== Acciones =====
        menu.Items.Add(new MenuFlyoutSeparator());

        var resetItem = new MenuFlyoutItem { Text = I18n.T("Eliminar reglas") };
        resetItem.Click += async (s, e) =>
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = I18n.T("Eliminar reglas"),
                Content = I18n.T("¿Eliminar las reglas de {0}? También se quitará la prioridad de nacimiento del registro.", name),
                PrimaryButtonText = I18n.T("Eliminar"),
                CloseButtonText = I18n.T("Cancelar"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            _processService.RemoveRule(exe);
            _processService.ClearSessionRule(exe);
            var app = _processService.FindRunningProcess(exe);
            if (app != null)
            {
                _processService.ApplyCpuPriority(app.Id, 2);
                _processService.ApplyAffinity(app.Id, fullMask);
                _processService.ApplyGpuPriority(app.Id, 3);
                _processService.ApplyIoPriority(app.Id, 2);
            }
            // Si el plan activo era el de este juego, volver al plan por defecto.
            _processService.RevertPowerPlanIfApplied(exe);
            StatusText.Text = I18n.T("Reglas eliminadas para {0}", exe);
            StatusText.Foreground = Feedback.MutedBrush;
            StatusText.Visibility = Visibility.Visible;
        };
        menu.Items.Add(resetItem);

        var deleteItem = new MenuFlyoutItem
        {
            Text = I18n.T("Eliminar de la biblioteca"),
            Foreground = Feedback.ErrorBrush
        };
        deleteItem.Click += async (s, e) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = I18n.T("Eliminar de la biblioteca"),
                Content = I18n.T("¿Eliminar {0} de la biblioteca? El juego no se desinstala.", name),
                PrimaryButtonText = I18n.T("Eliminar"),
                CloseButtonText = I18n.T("Cancelar"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (_processService.GetManualEntries().Any(m => string.Equals(m.Exe, exe, StringComparison.OrdinalIgnoreCase)))
                    _processService.RemoveManualExe(exe);
                _processService.ClearSessionRule(exe);
                _processService.HideExe(exe);
                await RefreshAsync();
            }
        };
        menu.Items.Add(deleteItem);

        menu.ShowAt(target);
    }

    /// <summary>
    /// Lanza el juego con la lógica compartida GameLauncher (la misma que usa el
    /// menú de la bandeja). Los mensajes de estado se reflejan en StatusText.
    /// </summary>
    private Task LaunchGameAsync(string fileName, string arguments, string? blizzardCode, string? installPath, string? exeFileName, string? launcher)
        => GameLauncher.LaunchGameAsync(
            _gameBoostService, _processService, _loggingService,
            fileName, arguments, blizzardCode, installPath, exeFileName, launcher,
            (message, kind) =>
            {
                if (kind == LaunchStatusKind.Hide)
                {
                    StatusText.Visibility = Visibility.Collapsed;
                    return;
                }
                StatusText.Text = message;
                StatusText.Foreground = kind == LaunchStatusKind.Warning ? Feedback.WarningBrush : Feedback.MutedBrush;
                StatusText.Visibility = Visibility.Visible;
            });

}
