using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Services;
using WHPO_UI.Views.Pages;
using WinFormsApp = System.Windows.Forms.Application;
using DrawingImage = System.Drawing.Image;
using WinUIApp = Microsoft.UI.Xaml.Application;

namespace WHPO_UI;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigationService;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private readonly ISystemInfoService _systemInfoService;
    private readonly IMemoryService _memoryService;
    private readonly INetworkService _networkService;
    private readonly IProcessService _processService;
    private readonly IInstalledGamesService _installedGamesService;
    private readonly IGameBoostService? _gameBoostService;
    private readonly IAppUpdateService _appUpdateService;

    // Último chequeo de actualizaciones (para el botón del navbar) y si ya se lanzó.
    private AppUpdateInfo? _latestUpdate;
    private bool _updateCheckStarted;

    private NotifyIcon? _notifyIcon;
    private DispatcherQueueTimer? _trayTooltipTimer;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _gpuCounter;
    private bool _centeredOnFirstActivation;
    // Últimos valores logueados del tooltip para no escribir en cada tick (2-5 s).
    private double _lastLoggedCpu = double.NaN;
    private double _lastLoggedRam = double.NaN;
    private DateTime _lastTooltipLog = DateTime.MinValue;

    /// <summary>
    /// Indica si la ventana principal está visible (no oculta en bandeja).
    /// Las páginas lo usan para pausar sus timers de fondo y ahorrar CPU/RAM.
    /// </summary>
    public bool IsWindowVisible { get; private set; } = true;

    public MainWindow()
    {
        InitializeComponent();

        // Obtener servicios desde DI
        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _systemInfoService = App.Services.GetRequiredService<ISystemInfoService>();
        _memoryService = App.Services.GetRequiredService<IMemoryService>();
        _networkService = App.Services.GetRequiredService<INetworkService>();
        _processService = App.Services.GetRequiredService<IProcessService>();
        _installedGamesService = App.Services.GetRequiredService<IInstalledGamesService>();
        _gameBoostService = App.Services.GetService<IGameBoostService>();
        _appUpdateService = App.Services.GetRequiredService<IAppUpdateService>();

        // Overlay de métricas de juegos: si quedó activado se reanuda desde el arranque
        // (ventana, hotkeys y muestreo). Se construye acá, en el hilo de UI, para que
        // OverlayService capture el DispatcherQueue correcto para su ventana WinForms.
        App.Services.GetRequiredService<WHPO_UI.Services.OverlayService>().EnsureStarted();

        // Configurar ThemeApplier con esta ventana
        var themeApplier = App.Services.GetRequiredService<IThemeApplier>();
        if (themeApplier is ThemeApplier ta)
        {
            ta.SetMainWindow(this);
        }

        // Traducciones: cargar el idioma guardado ANTES de navegar a la primera
        // página (el recorrido del árbol se hace al navegar).
        I18n.Initialize(_settingsService);
        ContentFrame.Navigated += OnFrameNavigated;
        I18n.LanguageChanged += OnLanguageChanged;
        // El botón de idiomas vive en el PaneHeader (misma fila que el botón de
        // achicar el navbar, al extremo derecho del panel): el template del control
        // lo oculta solo cuando el panel queda compacto (achicado).
        Flags.EnsureGenerated();
        ApplyLanguageButton();

        // Configurar NavigationView
        NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems[0];
        NavigationViewControl.ItemInvoked += NavigationViewControl_ItemInvoked;

        // Configurar NavigationService con el Frame
        if (_navigationService is NavigationService ns)
        {
            ns.SetFrame(new NavigationFrameAdapter(ContentFrame));
            ns.RegisterPage("sistema", typeof(SistemaPage));
            ns.RegisterPage("red", typeof(RedPage));
            ns.RegisterPage("memoria", typeof(MemoriaPage));
            ns.RegisterPage("temporizador", typeof(TemporizadorPage));
            ns.RegisterPage("nucleos", typeof(NucleosPage));
            ns.RegisterPage("procesos", typeof(GestionarProcesosPage));
            ns.RegisterPage("procesosvivos", typeof(ProcesosPage));
            ns.RegisterPage("teclado", typeof(TecladoPage));
            ns.RegisterPage("autoclicker", typeof(AutoclickerPage));
            ns.RegisterPage("estabilidad", typeof(EstabilidadPage));
            ns.RegisterPage("sensores", typeof(SensoresPage));
            ns.RegisterPage("overlay", typeof(OverlayPage));
            ns.RegisterPage("optimizaciones", typeof(OptimizacionesPage));
            ns.RegisterPage("debloat", typeof(DebloatPage));
            ns.RegisterPage("herramientas", typeof(HerramientasPage));
            ns.RegisterPage("panelwindows", typeof(PanelWindowsPage));
            ns.RegisterPage("reparacion", typeof(ReparacionPage));
            ns.RegisterPage("actualizaciones", typeof(ActualizacionesPage));
            ns.RegisterPage("limpieza", typeof(LimpiezaPage));
            ns.RegisterPage("configuracion", typeof(ConfiguracionPage));
        }

        // Aplicar la selección de pestañas hecha en el instalador (una sola vez).
        ApplyInstallerTabSelection();

        // Aplicar la visibilidad de apartados según la configuración (claves "nav.*").
        ApplyNavigationVisibility();

        // Botón "⋮" al extremo derecho de cada pestaña: menú con "Ocultar" para
        // esconder la pestaña sin pasar por Configuración. Se agrega ANTES de
        // TranslateNavbar para capturar el texto fuente en español del XAML.
        AttachNavItemMenus();

        // Navegar directamente a la página de Sistema (sin título de cabecera)
        _navigationService.NavigateTo("sistema");
        NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems[0];

        // Traducir el navbar iterando MenuItems (lógico, sin depender de que el
        // template visual esté realizado): el recorrido del árbol visual con
        // VisualTreeHelper no garantiza llegar a los ítems del NavigationView,
        // que se realizan de forma perezosa — por eso el navbar no cambiaba de
        // idioma. Los ítems del XAML conservan el texto español como fuente y acá
        // se captura y se traduce al idioma guardado.
        TranslateNavbar();

        // Configurar minimize to tray
        this.Closed += MainWindow_Closed;

        // Interceptar botón cerrar (X) para minimizar a bandeja si está activado
        this.AppWindow.Closing += AppWindow_Closing;

        // Garantizar la restauración/centrado en la primera activación: aplicar la
        // posición antes de Activate() puede ser ignorado por Windows, y con el
        // evento queda seguro. (Misma posición que la del constructor: no salta.)
        this.Activated += (_, args) =>
        {
            if (!_centeredOnFirstActivation && args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _centeredOnFirstActivation = true;
                RestoreOrCenterWindow();
                TranslateNavbar();
            }
        };

        // Barra de título acorde al tema (negra en oscuro, blanca en claro) + icono de WinForge
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow != null)
            {
                var themeService = App.Services.GetRequiredService<IThemeService>();
                ApplyTitleBarTheme(themeService);
                themeService.ThemeChanged += (s, t) => ApplyTitleBarTheme(themeService);

                var winForgeIconPath = GetWinForgeIcoPath();
                if (winForgeIconPath != null)
                    appWindow.SetIcon(winForgeIconPath);

                // Guardar posición/tamaño al mover o redimensionar (debounced).
                InitWindowPositionTracking();

                // OJO: el tamaño/posición se aplica SOLO en el evento Activated (arriba).
                // Aplicarlo aquí (antes de Activate) es el camino poco confiable de
                // MoveAndResize de WinAppSDK y puede dejar la ventana en un tamaño
                // mínimo espurio (el "cuadradito chiquito" al abrir).
            }
        }
        catch { }

        // El tray icon (NotifyIcon de WinForms) y las métricas se inicializan DIFERIDOS
        // para que la ventana aparezca al instante: su creación es costosa la primera vez.
        DispatcherQueue.TryEnqueue(() =>
        {
            // SetupTrayIcon ya invoca UpdateTrayStatus internamente.
            SetupTrayIcon();

            // Reaplicar lo que el usuario dejó iniciado en la sesión anterior
            // (resolución del temporizador y limpieza automática de memoria).
            _ = ApplyAutoStartFeaturesAsync();

            // Pre-calentar el sensor de temperatura (carga el driver de LHM en segundo
            // plano) para que la pestaña Núcleos muestre la temperatura enseguida.
            _ = Task.Run(() => { try { _systemInfoService.GetCpuTemperature(); } catch { } });
        });

        _loggingService.LogInfo("MainWindow inicializada");
    }

    private void SetupTrayIcon()
    {
        try
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = CreateWinForgeIcon() ?? SystemIcons.Application,
                Visible = true,
                Text = "WinForge"
            };

            _notifyIcon.DoubleClick += (s, e) => ShowWindow();

            // Tooltip estático: las métricas en bandeja se reemplazaron por "Optimizar Rendimiento".
            UpdateTrayStatus();

            // Menú contextual de la bandeja: renderer oscuro propio (el color table del
            // sistema usa el tema claro → hover blanco + texto blanco ilegible), esquinas
            // redondeadas vía DWM (Win11), emojis a la izquierda de cada botón y buen
            // margen. Se reconstruye en cada apertura (Opening) para que la lista de
            // favoritos esté siempre al día.
            var contextMenu = new ContextMenuStrip
            {
                // OJO: RenderMode NO se puede setear a 'Custom' directamente (WinForms lanza
                // NotSupportedException); se asigna el Renderer abajo y eso lo pone en Custom.
                // El emoji va como Image en la columna de imagen; el texto se centra en
                // vertical con el override OnRenderItemText del DarkMenuRenderer (el layout
                // nativo con Image deja el texto pegado arriba).
                ShowImageMargin = true,
                ShowCheckMargin = false,
                Font = new Font("Segoe UI", 10F),
                BackColor = Color.FromArgb(24, 24, 24),
                ForeColor = Color.White,
                Padding = new Padding(6),
                // Columna de imagen más ancha (30 px): los emojis/íconos quedan con
                // más aire horizontal. Los bitmaps se generan de 30×20 con el glifo
                // centrado, así no se estiran.
                ImageScalingSize = new Size(30, 20),
                AutoClose = true,
                Margin = new Padding(0)
            };

            contextMenu.Renderer = new DarkMenuRenderer();
            contextMenu.HandleCreated += (s, e) => ApplyRoundedMenuCorners(contextMenu.Handle);
            // Esperar los nombres/íconos de los favoritos antes de armar el menú:
            // si no, la primera apertura muestra los nombres crudos del exe
            // ("Hemingway" en vez de "SMITE 2"). Después de la primera vez la caché
            // de juegos ya está cargada y el refresco es instantáneo.
            contextMenu.Opening += async (s, e) =>
            {
                await RefreshTrayFavNamesAsync();
                BuildTrayMenu(contextMenu);
            };
            BuildTrayMenu(contextMenu);

            _notifyIcon.ContextMenuStrip = contextMenu;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error configurando tray icon", ex);
        }
    }

    // ===== Menú contextual de la bandeja =====

    /// <summary>
    /// Arma el menú completo de la bandeja. Se llama al crear el menú y en cada
    /// apertura (evento Opening) para que los favoritos estén siempre al día.
    /// </summary>
    private void BuildTrayMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        // Sistema: abre la app en la pestaña Sistema.
        menu.Items.Add(MakeTrayItem(EmojiGlyph(0x1F5A5, 0xFE0F), I18n.T("Sistema"), (s, e) =>
        {
            _navigationService.NavigateTo("sistema");
            ShowWindow();
        }));

        menu.Items.Add(new ToolStripSeparator());

        // Limpiar cache en RAM: purga la lista standby (memoria en caché).
        menu.Items.Add(MakeTrayItem(EmojiGlyph(0x1F9F9), I18n.T("Limpiar Cache en Ram"), async (s, e) =>
        {
            try { await _memoryService.CleanStandbyListAsync(); }
            catch (Exception ex) { _loggingService.LogWarning($"Bandeja: limpiar cache en RAM: {ex.Message}"); }
        }));

        menu.Items.Add(new ToolStripSeparator());

        // Biblioteca de juegos: abre la app en la pestaña de la biblioteca (como Sistema).
        menu.Items.Add(MakeTrayItem(EmojiGlyph(0x1F3AE), I18n.T("Biblioteca de juegos"), (s, e) =>
        {
            _navigationService.NavigateTo("procesos");
            ShowWindow();
        }));

        menu.Items.Add(new ToolStripSeparator());

        // Juegos favoritos (ya refrescados por el evento Opening antes de armar el menú).
        var favorites = _processService.GetFavorites();
        if (favorites.Count == 0)
        {
            var none = MakeTrayItem(EmojiGlyph(0x2B50), I18n.T("Sin juegos favoritos"), null);
            none.Enabled = false;
            none.ForeColor = Color.FromArgb(140, 140, 140);
            menu.Items.Add(none);
        }
        else
        {
            foreach (var exe in favorites)
            {
                string label = _favNames.TryGetValue(exe, out var n) && !string.IsNullOrEmpty(n)
                    ? n
                    : Path.GetFileNameWithoutExtension(exe);
                // Ícono: primero el del exe REAL del juego (ya resuelto saltándose
                // stubs/instaladores/crash handlers), después el banner oficial
                // cacheado por la biblioteca y, si no, emoji. La resolución del exe
                // es la que evita los logos genéricos (Fall Guys → RunFallGuys.exe,
                // Phasmophobia → Phasmophobia.exe); el banner es solo respaldo.
                var img = _favPaths.TryGetValue(exe, out var p) && File.Exists(p)
                    ? FavIconImage(p)
                    : _favBanners.TryGetValue(exe, out var b) && File.Exists(b)
                        ? FavBannerImage(b)
                        : EmojiGlyph(0x1F3AE);
                string favExe = exe;
                menu.Items.Add(MakeTrayItem(img, label, async (s, e) => await LaunchFavoriteFromTrayAsync(favExe)));
            }
        }

        menu.Items.Add(new ToolStripSeparator());

        // Configuración (engranaje nítido de Segoe MDL2, el mismo del XAML de la app).
        menu.Items.Add(MakeTrayItem(EmojiGlyph(0xE713, null, "Segoe MDL2 Assets", 15f), I18n.T("Configuración"), (s, e) =>
        {
            _navigationService.NavigateTo("configuracion");
            ShowWindow();
        }));

        menu.Items.Add(new ToolStripSeparator());

        // Salir (ícono de encendido/apagado ⏻).
        menu.Items.Add(MakeTrayItem(EmojiGlyph(0x23FB, 0xFE0F), I18n.T("Salir"), (s, e) =>
        {
            // Restaurar el escaneo Wi-Fi si el optimizador WLAN quedó activo, así
            // el bloqueo de fondo / modo streaming no queda aplicado tras cerrar la app.
            try
            {
                var wlan = App.Services.GetService<WHPO_UI.Services.WlanOptimizerService>();
                if (wlan != null && (wlan.BlockScanActive || wlan.StreamingActive))
                {
                    foreach (var a in wlan.GetAdapters())
                        wlan.RestoreDefaults(a.Guid);
                }
            }
            catch { }
            _notifyIcon?.Dispose();
            WinUIApp.Current.Exit();
        }));
    }

    /// <summary>
    /// Lanza un juego favorito desde el menú de la bandeja con la misma lógica que
    /// el botón "Iniciar" de la biblioteca (GameLauncher). Sin registro en la caché
    /// de la biblioteca (entrada manual o caché vieja) se lanza el exe resuelto
    /// directamente.
    /// </summary>
    private async Task LaunchFavoriteFromTrayAsync(string exe)
    {
        try
        {
            if (!_favGames.TryGetValue(exe, out var game))
            {
                // Entrada manual o caché vieja: lanzar el exe resuelto directamente.
                string? direct = _favPaths.TryGetValue(exe, out var p) ? p : null;
                if (string.IsNullOrEmpty(direct))
                {
                    _loggingService.LogWarning($"Bandeja: no se pudo lanzar {exe} — ejecutable no encontrado.");
                    return;
                }
                await GameLauncher.LaunchGameAsync(_gameBoostService, _processService, _loggingService,
                    direct, "", null, null, exe, null, TrayLaunchStatus);
                return;
            }

            // Mismo armado de parámetros que la biblioteca (ver BuildGameCard).
            string? exePath = _favPaths.TryGetValue(exe, out var pp) ? pp : null;
            bool steam = game.Launcher == "Steam" && !string.IsNullOrEmpty(game.AppId);
            bool riot = game.Launcher == "Riot" && !string.IsNullOrEmpty(game.AppId);
            string? blizzardCode = game.Launcher == "Blizzard"
                ? (string.IsNullOrEmpty(game.AppId) ? GameLauncher.GetBlizzardProductCode(exe) : game.AppId)
                : null;

            string launchFile, launchArgs;
            if (steam)
            {
                launchFile = $"steam://rungameid/{game.AppId}";
                launchArgs = "";
            }
            else if (game.Launcher == "Epic" && !string.IsNullOrEmpty(game.EpicAppName))
            {
                launchFile = $"com.epicgames.launcher://apps/{game.EpicAppName}?action=launch&silent=true";
                launchArgs = "";
            }
            else if (game.Launcher == "Xbox" && !string.IsNullOrEmpty(game.AppId))
            {
                launchFile = $"shell:AppsFolder\\{game.AppId}";
                launchArgs = "";
            }
            else if (riot)
            {
                launchFile = GameLauncher.FindRiotLauncher() ?? "";
                launchArgs = $"--launch-product={game.AppId} --launch-patchline=live";
            }
            else
            {
                launchFile = exePath ?? "";
                launchArgs = "";
            }

            await GameLauncher.LaunchGameAsync(_gameBoostService, _processService, _loggingService,
                launchFile, launchArgs, blizzardCode, game.InstallPath, exe, game.Launcher, TrayLaunchStatus);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Bandeja: lanzar {exe}: {ex.Message}");
        }
    }

    /// <summary>Estado del lanzamiento desde la bandeja: sin UI, solo log.</summary>
    private void TrayLaunchStatus(string message, LaunchStatusKind kind)
    {
        if (kind == LaunchStatusKind.Hide || string.IsNullOrEmpty(message)) return;
        _loggingService.LogInfo($"Bandeja: {message}");
    }

    private static ToolStripMenuItem MakeTrayItem(DrawingImage? image, string text, EventHandler? onClick)
    {
        var item = new ToolStripMenuItem(text)
        {
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Padding = new Padding(14, 4, 18, 4),
            Margin = new Padding(0),
            AutoSize = true,
            Image = image,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextAlign = ContentAlignment.MiddleLeft
        };
        if (onClick != null) item.Click += onClick;
        return item;
    }

    // Emojis renderizados a bitmap (GDI+ pinta emoji en color; el TextRenderer de
    // WinForms los pinta monocromo). Se cachean: una sola vez por glifo.
    // Para íconos monocromos de la app (ej. el engranaje E713 de Segoe MDL2) se
    // puede pasar otra fuente: quedan mucho más nítidos que el emoji de tuerca.
    private static readonly Dictionary<string, DrawingImage> _emojiCache = new();
    private static DrawingImage EmojiGlyph(int codepoint, int? variation = null,
        string fontName = "Segoe UI Emoji", float fontSize = 12f)
    {
        string s = char.ConvertFromUtf32(codepoint)
            + (variation.HasValue ? char.ConvertFromUtf32(variation.Value) : "");
        string key = fontName + "|" + fontSize + "|" + s;
        if (_emojiCache.TryGetValue(key, out var cached)) return cached;

        var bmp = new Bitmap(30, 20);
        using (var g = Graphics.FromImage(bmp))
        {
            using var font = new Font(fontName, fontSize);
            g.DrawString(s, font, Brushes.White, new RectangleF(0, -1, 30, 22),
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
        _emojiCache[key] = bmp;
        return bmp;
    }

    // Ícono del exe de un juego favorito (extraído una vez y cacheado). Se dibuja
    // 16×16 centrado en la misma caja de 30×20 que los emojis. Si falla, cae al
    // emoji de juego.
    private static readonly Dictionary<string, DrawingImage> _favIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static DrawingImage FavIconImage(string exePath)
    {
        if (_favIconCache.TryGetValue(exePath, out var cached)) return cached;
        DrawingImage result = EmojiGlyph(0x1F3AE);
        try
        {
            // Ícono propio del juego (.ico de la carpeta): los juegos viejos suelen
            // tenerlo aunque el exe no traiga recurso de ícono (o solo 16/32 px).
            Bitmap? iconBmp = null;
            var local = IconExtractor.FindConfidentLocalIcon(exePath);
            if (local != null)
            {
                using var ico = new System.Drawing.Icon(local);
                iconBmp = ico.ToBitmap();
            }
            else
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                iconBmp = ico?.ToBitmap();
            }
            if (iconBmp != null)
            {
                var bmp = new Bitmap(30, 20);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (iconBmp)
                        g.DrawImage(iconBmp, (30 - 20) / 2f, (20 - 20) / 2f, 20, 20);
                }
                result = bmp;
            }
        }
        catch { }
        _favIconCache[exePath] = result;
        return result;
    }

    // Nombres legibles de los favoritos (exe → nombre del juego), ruta del exe
    // (exe → ruta completa, para extraer el ícono) y banner oficial (exe → archivo
    // de banner ya cacheado por la biblioteca), desde la caché de la biblioteca.
    // Fallback del nombre: exe sin extensión.
    private readonly Dictionary<string, string> _favNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _favPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _favBanners = new(StringComparer.OrdinalIgnoreCase);
    // exe → juego instalado (para lanzar desde la bandeja con la misma lógica que la biblioteca).
    private readonly Dictionary<string, InstalledGame> _favGames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ruta del banner ya descargado por la biblioteca (misma caché en disco):
    /// Steam/Battle.net → {appId}.jpg, Epic → epic-{ns}-{id}.jpg. Si la biblioteca
    /// todavía no lo descargó, devuelve null (se cae al ícono del exe).
    /// </summary>
    private static string? GetCachedBannerPath(InstalledGame g)
    {
        try
        {
            string dir = GestionarProcesosPage.BannerCacheDir;
            if (string.IsNullOrEmpty(g.AppId)) return null;
            if (!string.IsNullOrEmpty(g.BannerUrl))
            {
                var f = Path.Combine(dir, g.AppId + ".jpg");
                return File.Exists(f) ? f : null;
            }
            if (string.Equals(g.Launcher, "Epic", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(g.ArtNamespace))
            {
                var f = Path.Combine(dir, $"epic-{g.ArtNamespace}-{g.AppId}.jpg");
                return File.Exists(f) ? f : null;
            }
        }
        catch { }
        return null;
    }

    // Banner del juego renderizado a ícono de bandeja: recorte cuadrado del centro
    // (los banners son 16:9 y el logo va al centro) escalado a 20×20 en la misma
    // caja de 30×20 que los emojis/íconos. Cacheado por archivo.
    private static readonly Dictionary<string, DrawingImage> _favBannerCache = new(StringComparer.OrdinalIgnoreCase);
    private static DrawingImage FavBannerImage(string bannerFile)
    {
        if (_favBannerCache.TryGetValue(bannerFile, out var cached)) return cached;
        DrawingImage result = EmojiGlyph(0x1F3AE);
        try
        {
            using var src = DrawingImage.FromFile(bannerFile);
            int side = Math.Min(src.Width, src.Height);
            int x = (src.Width - side) / 2;
            int y = (src.Height - side) / 2;
            var bmp = new Bitmap(30, 20);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src,
                    new RectangleF((30 - 20) / 2f, 0, 20, 20),
                    new RectangleF(x, y, side, side),
                    GraphicsUnit.Pixel);
            }
            result = bmp;
        }
        catch { }
        _favBannerCache[bannerFile] = result;
        return result;
    }

    private async Task RefreshTrayFavNamesAsync()
    {
        try
        {
            var games = await _installedGamesService.GetInstalledGamesAsync();
            _favGames.Clear();
            foreach (var g in games)
            {
                if (string.IsNullOrEmpty(g.ExeFileName)) continue;
                _favGames[g.ExeFileName] = g;
                if (!string.IsNullOrEmpty(g.Name)) _favNames[g.ExeFileName] = g.Name;
                if (string.IsNullOrEmpty(g.InstallPath)) continue;

                // Ícono del exe REAL del juego, no del stub de anti-cheat/consola
                // (ej. start_protected_game.exe de EAC → Hemingway-Win64-Shipping.exe;
                // vconsole2.exe de CS2 → cs2.exe). Se prefiere el exe CONOCIDO de la
                // biblioteca (el que inicia la app) siempre que NO sea un stub, y
                // FindBestGameExePath queda solo como respaldo: antes se elegía
                // SIEMPRE el más grande de la carpeta y en BlueStacks 5 ganaba
                // BlueStacksAI.exe (32,56 MB vs 32,52 MB de HD-Player.exe),
                // mostrando el ícono de otro ejecutable en vez del que arranca la app.
                var known = FindExePath(g.InstallPath, g.ExeFileName);
                var p = known != null && !GameExeResolver.IsStubExe(known)
                    ? known
                    : (GameExeResolver.FindBestGameExePath(g.InstallPath) ?? known);
                if (p != null) _favPaths[g.ExeFileName] = p;

                // Banner oficial para el ícono de bandeja (si ya está cacheado).
                var banner = GetCachedBannerPath(g);
                if (banner != null) _favBanners[g.ExeFileName] = banner;

                // Alias: los favoritos guardados con el nombre del stub (antes de los
                // fixes globales, o si la caché aún no se re-escaneó) siguen mostrando
                // el nombre y el ícono correctos.
                foreach (var stub in GameExeResolver.FindStubExePaths(g.InstallPath))
                {
                    var stubName = Path.GetFileName(stub);
                    if (string.IsNullOrEmpty(stubName)) continue;
                    if (!_favNames.ContainsKey(stubName) && !string.IsNullOrEmpty(g.Name))
                        _favNames[stubName] = g.Name;
                    if (!_favPaths.ContainsKey(stubName) && p != null)
                        _favPaths[stubName] = p;
                    if (!_favGames.ContainsKey(stubName))
                        _favGames[stubName] = g;
                }
            }

            // Entradas manuales: también aportan ruta.
            try
            {
                foreach (var (exe, _, installPath) in _processService.GetManualEntries())
                {
                    if (string.IsNullOrEmpty(exe)) continue;
                    if (!string.IsNullOrEmpty(installPath))
                    {
                        var p = FindExePath(installPath, exe);
                        if (p != null) _favPaths[exe] = p;
                    }
                    else if (File.Exists(exe))
                        _favPaths[Path.GetFileName(exe)] = exe;
                }
            }
            catch { }
        }
        catch { }
    }

    /// <summary>
    /// Busca el exe real del juego: primero directo en la carpeta de instalación y,
    /// si no, recursivo hasta 4 niveles (los exes modernos van anidados, ej.
    /// CS2\game\bin\win64\cs2.exe). Misma lógica que la biblioteca de juegos.
    /// </summary>
    private static string? FindExePath(string installPath, string exeFileName)
    {
        if (string.IsNullOrEmpty(installPath) || string.IsNullOrEmpty(exeFileName)) return null;
        string direct = Path.Combine(installPath, exeFileName);
        if (File.Exists(direct)) return direct;
        try
        {
            int budget = 800;
            return FindFileInTree(installPath, exeFileName, 0, ref budget);
        }
        catch { return null; }
    }

    private static string? FindFileInTree(string dir, string fileName, int depth, ref int budget)
    {
        if (depth > 4 || budget <= 0) return null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, fileName, SearchOption.TopDirectoryOnly))
            {
                if (budget-- <= 0) return null;
                return f;
            }
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                if (budget-- <= 0) return null;
                var r = FindFileInTree(d, fileName, depth + 1, ref budget);
                if (r != null) return r;
            }
        }
        catch { }
        return null;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var minimizeToTray = _settingsService.Get("window.minimizeToTray", true);
        if (minimizeToTray)
        {
            args.Cancel = true; // Cancelar el cierre
            HideWindow(); // Ocultar a bandeja
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _trayTooltipTimer?.Stop();
        _notifyIcon?.Dispose();
        _cpuCounter?.Dispose();
        _gpuCounter?.Dispose();

        // Detener el autoclicker y liberar la hotkey global al cerrar la app.
        try
        {
            var clicker = App.Services.GetService<WHPO.Core.Services.Interfaces.IAutoClickerService>();
            clicker?.Stop();
            clicker?.UnregisterHotKey();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"MainWindow: error limpiando autoclicker: {ex.Message}");
        }
    }

    private void HideWindow()
    {
        IsWindowVisible = false;
        // En WinUI 3, ocultar la ventana
        var appWindow = GetAppWindow();
        if (appWindow != null)
        {
            appWindow.Hide();
        }

        // Detener monitoreo del sistema para evitar picos de CPU en background
        _systemInfoService.StopMonitoring();
        _loggingService.LogInfo("Monitoreo de sistema detenido (ventana oculta)");

        UpdateTrayStatus();
    }

    /// <summary>
    /// Oculta WHPO en la bandeja tras el inicio cuando el usuario lo configuró así.
    /// </summary>
    internal void HideToTrayAtStartup()
    {
        HideWindow();
    }

    private void ShowWindow()
    {
        IsWindowVisible = true;
        var appWindow = GetAppWindow();
        if (appWindow != null)
        {
            appWindow.Show();
            // Cada vez que se abre (incluido al volver de la bandeja) se restaura
            // la última posición/tamaño (o se centra si aún no hay guardados).
            RestoreOrCenterWindow();
        }

        // Reanudar monitoreo solo si la página activa es Sistema (evita timers sin suscriptores)
        if (ContentFrame.Content is SistemaPage)
        {
            _systemInfoService.StartMonitoring(1000);
            _loggingService.LogInfo("Monitoreo de sistema reanudado (ventana visible, página Sistema)");
        }

        UpdateTrayStatus();
    }

    /// <summary>
    /// Estado del icono de bandeja. Con "Optimizar Rendimiento" activo: sin métricas
    /// y huella mínima (tooltip estático + liberar memoria al ocultar la ventana).
    /// Con la opción apagada: muestra el uso de CPU, memoria y temperatura en el
    /// tooltip, como el comportamiento original.
    /// </summary>
    internal void UpdateTrayStatus()
    {
        if (_notifyIcon == null) return;

        var optimize = _settingsService.Get("tray.optimizePerformance", false);

        if (optimize)
        {
            StopTrayMetrics();
            _notifyIcon.Text = IsWindowVisible ? "WinForge" : $"WinForge — {I18n.T("Optimizando rendimiento")}";
            if (!IsWindowVisible)
                TrimProcessMemory();
        }
        else
        {
            _notifyIcon.Text = "WinForge";
            StartTrayMetrics();
        }
    }

    private void StartTrayMetrics()
    {
        if (_trayTooltipTimer == null)
        {
            _trayTooltipTimer = DispatcherQueue.CreateTimer();
            _trayTooltipTimer.Tick += async (s, e) => await UpdateTrayTooltipAsync();
        }

        // Con la ventana oculta en bandeja, espaciar las consultas (WMI/LHM) para
        // ahorrar CPU en segundo plano.
        _trayTooltipTimer.Interval = IsWindowVisible ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);

        if (_cpuCounter == null && _memoryService != null)
            InitializePerformanceCounters();

        if (!_trayTooltipTimer.IsRunning)
            _trayTooltipTimer.Start();

        _ = UpdateTrayTooltipAsync();
    }

    private void StopTrayMetrics()
    {
        _trayTooltipTimer?.Stop();
        if (_cpuCounter != null || _gpuCounter != null)
        {
            _cpuCounter?.Dispose();
            _gpuCounter?.Dispose();
            _cpuCounter = null;
            _gpuCounter = null;
        }
    }

    /// <summary>
    /// Libera la memoria del proceso: compacta el heap de .NET y vacía el working
    /// set, así el Administrador de tareas muestra la RAM real que ya no se usa.
    /// Solo se invoca al ocultar la ventana con "Optimizar Rendimiento" activo.
    /// </summary>
    private static void TrimProcessMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
            var handle = System.Diagnostics.Process.GetCurrentProcess().Handle;
            EmptyWorkingSet(handle);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    private void InitializePerformanceCounters()
    {
        // CPU - siempre disponible
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // Primera llamada para inicializar
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error inicializando contador CPU", ex);
            _cpuCounter = null;
        }

        // GPU - puede no estar disponible
        try
        {
            _gpuCounter = new PerformanceCounter("GPU Engine", "Utilization Percentage", "_Total");
            _gpuCounter.NextValue();
        }
        catch (Exception ex)
        {
            _loggingService.LogInfo("Contador GPU no disponible: " + ex.Message);
            _gpuCounter = null;
        }
    }

    private async Task UpdateTrayTooltipAsync()
    {
        if (_notifyIcon == null) return;

        try
        {
            // Obtener estadísticas - usar 0 si el contador no está disponible
            var cpuUsage = _cpuCounter != null ? _cpuCounter.NextValue() : 0;
            var gpuUsage = _gpuCounter != null ? _gpuCounter.NextValue() : 0;

            // Temperaturas al lado del uso (LHM + fallbacks; fuera del hilo de UI por si WMI tarda)
            var cpuTemp = await Task.Run(() => _systemInfoService.GetCpuTemperature());
            var gpuTemp = await Task.Run(() => _systemInfoService.GetGpuTemperature());

            var memStats = _memoryService.GetMemoryStats();
            var cacheMB = memStats.StandbyMB;

            var currentTR = _memoryService.GetCurrentTimerResolution();
            var trMs = currentTR / 10000.0;

            var cpuTempPart = cpuTemp > 0 ? $" · {cpuTemp:F0}°C" : "";
            var gpuTempPart = gpuTemp > 0 ? $" · {gpuTemp:F0}°C" : "";

            // Estado de funciones con interruptor (macros / limpiador de lista en espera),
            // leído en vivo del settings para que el tooltip se actualice al cambiarlas.
            string on = I18n.T("On");
            string off = I18n.T("Off");
            string macrosState = _settingsService.Get("macrosEnabled", true) ? on : off;
            string slcState = _settingsService.Get("memory.autoStart", false) ? on : off;

            var tooltip = $"CPU: {cpuUsage:F0}%{cpuTempPart}\r\n" +
                $"GPU: {gpuUsage:F0}%{gpuTempPart}\r\n" +
                $"RAM: {memStats.UsedPercent:F0}% ({memStats.UsedMB:F0} MB)\r\n" +
                $"Cache: {cacheMB:F0} MB\r\n" +
                $"TR: {trMs:F2} ms\r\n" +
                $"{I18n.T("Macro")}: {macrosState}\r\n" +
                $"{I18n.T("SLC (standby list cleaner)")}: {slcState}";

            _notifyIcon.Text = tooltip;
            LogTooltipIfChanged(cpuUsage, memStats.UsedPercent);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error actualizando tooltip de tray", ex);
            // Fallback: mostrar al menos info básica
            try
            {
                var memStats = _memoryService.GetMemoryStats();
                var currentTR = _memoryService.GetCurrentTimerResolution();
                var trMs = currentTR / 10000.0;
                _notifyIcon.Text = $"WinForge\nRAM: {memStats.UsedMB:F0}/{memStats.TotalPhysicalMB:F0} MB\nTR: {trMs:F3} ms";
            }
            catch { }
        }
    }

    /// <summary>
    /// Loguea el tooltip solo cuando cambia el uso (>= 1 punto) o como heartbeat cada
    /// 5 minutos: el tick corre cada 2-5 s y loguear siempre llenaría el archivo de
    /// decenas de miles de líneas por día.
    /// </summary>
    private void LogTooltipIfChanged(double cpuUsage, double ramPercent)
    {
        var now = DateTime.Now;
        var cpuChanged = double.IsNaN(_lastLoggedCpu) || Math.Abs(cpuUsage - _lastLoggedCpu) >= 1.0;
        var ramChanged = double.IsNaN(_lastLoggedRam) || Math.Abs(ramPercent - _lastLoggedRam) >= 1.0;
        if (cpuChanged || ramChanged || (now - _lastTooltipLog).TotalMinutes >= 5)
        {
            _lastLoggedCpu = cpuUsage;
            _lastLoggedRam = ramPercent;
            _lastTooltipLog = now;
            _loggingService.LogInfo($"Tooltip actualizado: CPU={cpuUsage:F0}%, RAM={ramPercent:F0}%");
        }
    }

    /// <summary>
    /// Restaura la posición/tamaño guardados de la ventana; si no hay posición
    /// guardada (primer uso) o quedó fuera de pantalla (cambió el monitor), la
    /// centra en el área de trabajo con el tamaño por defecto (1400x800).
    /// Se llama al arrancar y en cada apertura desde la bandeja.
    /// </summary>
    private void RestoreOrCenterWindow()
    {
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow == null) return;

            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
            var wa = area.WorkArea;

            int w = _settingsService.Get("window.width", 1400);
            int h = _settingsService.Get("window.height", 800);
            int x = _settingsService.Get("window.x", int.MinValue);
            int y = _settingsService.Get("window.y", int.MinValue);

            // Limitar el tamaño al área de trabajo (monitores chicos).
            w = Math.Clamp(w, 800, Math.Max(800, wa.Width));
            h = Math.Clamp(h, 500, Math.Max(500, wa.Height));

            // La posición guardada solo se usa si una parte razonable de la ventana
            // sigue dentro del área visible; si no, se vuelve a centrar.
            bool visible = x != int.MinValue && y != int.MinValue &&
                x + 80 <= wa.X + wa.Width && x + w - 80 >= wa.X &&
                y + 40 <= wa.Y + wa.Height && y + h - 40 >= wa.Y;

            if (visible)
            {
                appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
            }
            else
            {
                int cx = wa.X + Math.Max(0, (wa.Width - w) / 2);
                int cy = wa.Y + Math.Max(0, (wa.Height - h) / 2);
                appWindow.MoveAndResize(new Windows.Graphics.RectInt32(cx, cy, w, h));
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudo restaurar la posición de la ventana: {ex.Message}");
        }
    }

    // ===== Guardado de posición/tamaño de la ventana (con debounce) =====
    private DispatcherQueueTimer? _windowPosSaveTimer;

    private void InitWindowPositionTracking()
    {
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow == null) return;
            appWindow.Changed += (_, args) =>
            {
                if (args.DidPositionChange || args.DidSizeChange)
                    ScheduleWindowPositionSave();
            };
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudo iniciar el guardado de posición: {ex.Message}");
        }
    }

    private void ScheduleWindowPositionSave()
    {
        if (_windowPosSaveTimer == null)
        {
            _windowPosSaveTimer = DispatcherQueue.CreateTimer();
            _windowPosSaveTimer.Interval = TimeSpan.FromMilliseconds(800);
            _windowPosSaveTimer.Tick += (_, _) =>
            {
                _windowPosSaveTimer.Stop();
                SaveWindowPosition();
            };
        }
        _windowPosSaveTimer.Stop();
        _windowPosSaveTimer.Start();
    }

    private void SaveWindowPosition()
    {
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow == null) return;
            var pos = appWindow.Position;
            var size = appWindow.Size;

            // No guardar estados transitorios inválidos: tamaños espurios (ventana aún
            // sin dimensionar en el arranque) o la posición de una ventana minimizada
            // (-32000, -32000). Guardarlos haría que la app abra como un "cuadradito".
            if (size.Width < 400 || size.Height < 300) return;
            if (pos.X < -10000 || pos.Y < -10000) return;

            _settingsService.Set("window.x", pos.X);
            _settingsService.Set("window.y", pos.Y);
            _settingsService.Set("window.width", size.Width);
            _settingsService.Set("window.height", size.Height);
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudo guardar la posición de la ventana: {ex.Message}");
        }
    }

    /// <summary>
    /// Borra la posición/tamaño guardados de la ventana (window.x/y/width/height) y
    /// la re-centra al instante en el área de trabajo. Útil si quedó fuera de
    /// pantalla tras cambiar de monitor o si se quiere volver al comportamiento
    /// por defecto (1400x800 centrado).
    /// </summary>
    public void ResetWindowPosition()
    {
        try
        {
            _settingsService.Remove("window.x");
            _settingsService.Remove("window.y");
            _settingsService.Remove("window.width");
            _settingsService.Remove("window.height");
            _settingsService.Save();
            // Sin posición guardada, RestoreOrCenterWindow la centra sola.
            RestoreOrCenterWindow();
            _loggingService.LogInfo("Posición de la ventana restablecida (re-centrada)");
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudo restablecer la posición de la ventana: {ex.Message}");
        }
    }

    private AppWindow? GetAppWindow()
    {
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    // ===== Reaplicación al iniciar (resolución del temporizador / limpieza automática) =====

    /// <summary>
    /// Si el usuario dejó activadas la resolución del temporizador o la limpieza
    /// automática de memoria, las reaplica al arrancar la app. Ambas viven en este
    /// proceso: al cerrarlo, Windows revierte la resolución y la limpieza se detiene.
    /// </summary>
    private async Task ApplyAutoStartFeaturesAsync()
    {
        try
        {
            if (_settingsService.Get("timer.autoStart", false))
            {
                double desiredMs = _settingsService.Get("memory.desiredResolutionMs", 0.5);
                int resolution100ns = (int)(desiredMs * 10000);
                var result = await _memoryService.SetTimerResolutionAsync(resolution100ns);
                if (result.Success)
                    _loggingService.LogInfo("Resolución del temporizador reaplicada al iniciar");
                else
                    _loggingService.LogWarning($"No se pudo reaplicar la resolución al iniciar: {result.Output}");
            }

            if (_settingsService.Get("memory.autoStart", false))
            {
                double minStandby = _settingsService.Get("memory.minStandbyMB", 1024.0);
                double maxFree = _settingsService.Get("memory.maxFreeMB", 4096.0);
                int pollIntervalMs = _settingsService.Get("memory.pollIntervalMs", 1000);
                _memoryService.StartAutoCleanup(minStandby, maxFree, pollIntervalMs);
                _loggingService.LogInfo("Limpieza automática de memoria reiniciada al iniciar");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Error reaplicando funciones automáticas al iniciar: {ex.Message}");
        }
    }

    // ===== Selección de pestañas del instalador =====

    /// <summary>
    /// Aplica una sola vez la selección de pestañas elegida en el instalador
    /// (HKLM\Software\WinForge\InstallTabs\&lt;tag&gt;, escrita por el MSI: "1" =
    /// visible, vacío = oculto). Las pestañas obligatorias (Sistema, Red, Memoria,
    /// Núcleos y Plan de energía, Teclado y Macros, Configuración) quedan siempre
    /// visibles. Después de la primera aplicación, el usuario controla la
    /// visibilidad desde Configuración → Menú de navegación.
    /// </summary>
    private void ApplyInstallerTabSelection()
    {
        try
        {
            // Solo se aplica en el primer arranque tras la instalación.
            if (_settingsService.Get("installer.tabsApplied", false)) return;

            using var key = Registry.LocalMachine.OpenSubKey(@"Software\WinForge\InstallTabs");
            if (key != null)
            {
                foreach (var name in key.GetValueNames())
                {
                    bool visible = key.GetValue(name) as string == "1";
                    // Pestañas obligatorias: siempre visibles, sin importar el instalador.
                    if (name is "sistema" or "red" or "memoria" or "nucleos" or "teclado" or "configuracion")
                        visible = true;
                    _settingsService.Set("nav." + name, visible);
                }
            }

            // Marcar como aplicada para no pisar cambios posteriores del usuario.
            _settingsService.Set("installer.tabsApplied", true);
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Error aplicando selección de pestañas del instalador: {ex.Message}");
        }
    }

    // ===== Visibilidad de apartados del menú lateral =====

    /// <summary>
    /// Aplica la visibilidad de los apartados del menú según la configuración
    /// (claves "nav.&lt;tag&gt;"; por defecto todas las pestañas son visibles).
    /// </summary>
    internal void ApplyNavigationVisibility()
    {
        try
        {
            foreach (var item in NavigationViewControl.MenuItems.OfType<NavigationViewItem>())
                ApplyNavItemVisibility(item);
            foreach (var item in NavigationViewControl.FooterMenuItems.OfType<NavigationViewItem>())
                ApplyNavItemVisibility(item);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Error aplicando visibilidad del menú: {ex.Message}");
        }
    }

    private void ApplyNavItemVisibility(NavigationViewItem item)
    {
        if (item.Tag is not string tag) return;
        // La pestaña Configuración no se puede ocultar: siempre visible.
        if (tag == "configuracion")
        {
            item.Visibility = Visibility.Visible;
            return;
        }
        item.Visibility = _settingsService.Get("nav." + tag, true) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== Traducciones: navbar + páginas =====

    private void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Content is FrameworkElement fe)
        {
            // El árbol puede estar parcialmente realizado durante Navigated, pero
            // traducir lo disponible evita que el idioma inicial dependa del timing.
            I18n.ApplyToVisualTree(fe);
            if (fe is Page page)
            {
                // Repetir al terminar el primer layout cubre controles perezosos y
                // páginas que crean contenido durante Loaded.
                page.Loaded -= Page_Loaded_Translate;
                page.Loaded += Page_Loaded_Translate;
            }
        }
    }

    private static void Page_Loaded_Translate(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        I18n.ApplyToVisualTree(element);
        element.DispatcherQueue.TryEnqueue(() => I18n.ApplyToVisualTree(element));
    }

    private void OnLanguageChanged()
    {
        ApplyLanguageButton();
        TranslateNavbar();
        ApplyUpdateIndicator();
        if (ContentFrame.Content is FrameworkElement fe)
            I18n.ApplyToVisualTree(fe);
    }

    // ===== Actualizaciones de la app (navbar) =====

    /// <summary>Último estado conocido del chequeo de actualizaciones (para compartir con otras vistas).</summary>
    public AppUpdateInfo? LatestUpdate => _latestUpdate;

    /// <summary>
    /// Se dispara cada vez que cambia el estado del chequeo de actualizaciones o se
    /// re-aplica el indicador (al completar el check y al cambiar de idioma). Lo usa
    /// ConfiguracionPage para sincronizar su pestaña interna "Actualizaciones".
    /// </summary>
    public event Action? AppUpdateStateChanged;

    /// <summary>True si la build instalada es más nueva que la última release publicada (build de desarrollo).</summary>
    public bool IsDevelopmentBuild => _latestUpdate?.Status == AppUpdateStatus.DevelopmentBuild;

    /// <summary>
    /// Chequeo de actualizaciones al abrir la app. Asíncrono y silencioso: si hay
    /// versión más nueva en el repo muestra el ícono "Actualizar a vX" en el
    /// navbar; si la build está adelantada al repo (en desarrollo) muestra
    /// "Versión X en desarrollo".
    /// </summary>
    public void BeginUpdateCheck()
    {
        if (_updateCheckStarted) return;
        _updateCheckStarted = true;
        _ = CheckUpdatesAsync();
    }

    private async Task CheckUpdatesAsync()
    {
        try
        {
            var info = await Task.Run(() => _appUpdateService.CheckForUpdatesAsync());
            _latestUpdate = info;
            ApplyUpdateIndicator();
        }
        catch (Exception ex)
        {
            // El fallo del chequeo no debe molestar al arranque: solo se loguea.
            _loggingService.LogWarning($"MainWindow: chequeo de actualizaciones falló: {ex.Message}");
        }
    }

    /// <summary>
    /// Aplica el estado del último chequeo al botón del navbar (ícono + tooltip
    /// + visibilidad). Se re-aplica al cambiar de idioma para retraducir el tooltip.
    /// </summary>
    private void ApplyUpdateIndicator()
    {
        try
        {
            var info = _latestUpdate;
            ApplyUpdateBadges();
            AppUpdateStateChanged?.Invoke();
            if (info == null)
            {
                UpdateButton.Visibility = Visibility.Collapsed;
                return;
            }

            switch (info.Status)
            {
                case AppUpdateStatus.UpdateAvailable:
                    UpdateButtonIcon.Glyph = "\uE896"; // Descargar
                    UpdateButtonIcon.Foreground = ThemeBrushes.Get("AccentBrush");
                    ToolTipService.SetToolTip(UpdateButton, I18n.T("Actualizar a {0}", $"v{info.LatestVersion}"));
                    UpdateButton.Visibility = Visibility.Visible;
                    break;

                case AppUpdateStatus.DevelopmentBuild:
                    UpdateButtonIcon.Glyph = "\uE946"; // Info
                    UpdateButtonIcon.Foreground = ThemeBrushes.Get("MutedBrush");
                    ToolTipService.SetToolTip(UpdateButton, I18n.T("Versión {0} en desarrollo", $"v{info.CurrentVersion}"));
                    UpdateButton.Visibility = Visibility.Visible;
                    break;

                default:
                    UpdateButton.Visibility = Visibility.Collapsed;
                    break;
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"MainWindow: indicador de actualización: {ex.Message}");
        }
    }

    /// <summary>
    /// Aplica el tipo de letra símbolo (Segoe Fluent Icons) a los íconos creados en
    /// código. Se resuelve de los recursos del tema por si el recurso no está.
    /// </summary>
    private static Microsoft.UI.Xaml.Media.FontFamily SymbolFontFamily()
    {
        if (WinUIApp.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var resource)
            && resource is Microsoft.UI.Xaml.Media.FontFamily ff)
        {
            return ff;
        }
        return new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons");
    }

    /// <summary>
    /// Muestra u oculta el badge de "actualización disponible". El ícono de
    /// notificación aparece sobre "Configuración", la sección que lleva a la pestaña
    /// de actualización de la app (Configuración > Actualizaciones).
    /// </summary>
    private void ApplyUpdateBadges()
    {
        try
        {
            var meta = _latestUpdate;
            bool show = meta is { Available: true };
            // Solo sobre "Configuración" (la pestaña lateral que lleva a la sección
            // de actualización de la app). El ítem "actualizaciones" del navbar es
            // "Windows Update" (políticas del sistema), no la actualización de la app.
            ApplyNavBadge("configuracion", show);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"MainWindow: badges de actualización: {ex.Message}");
        }
    }

    private void ApplyNavBadge(string tag, bool show)
    {
        var item = FindNavItem(tag);
        if (item == null) return;

        if (show && item.InfoBadge == null)
        {
            // Misma acción y estética que el botón de actualizar del navbar (glifo de
            // descarga): indica que se puede actualizar la app desde ese punto.
            item.InfoBadge = new InfoBadge
            {
                IconSource = new FontIconSource
                {
                    Glyph = "\uE896", // Descargar
                    FontFamily = SymbolFontFamily(),
                    FontSize = 10
                }
            };
        }
        else if (!show && item.InfoBadge != null)
        {
            item.InfoBadge = null;
        }
    }

    private NavigationViewItem? FindNavItem(string tag)
    {
        foreach (var item in AllNavItems())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var info = _latestUpdate;
        if (info == null || UpdateButton.XamlRoot == null) return;

        // Build en desarrollo: no hay nada que instalar, solo informar.
        if (info.Status == AppUpdateStatus.DevelopmentBuild)
        {
            var devDialog = new ContentDialog
            {
                XamlRoot = UpdateButton.XamlRoot,
                Title = I18n.T("Versión {0} en desarrollo", $"v{info.CurrentVersion}"),
                Content = I18n.T("Estás usando una versión en desarrollo ({0}): todavía no se publicó una versión más nueva en el repositorio.", $"v{info.CurrentVersion}"),
                CloseButtonText = I18n.T("Cerrar"),
                DefaultButton = ContentDialogButton.Close
            };
            await devDialog.ShowAsync();
            return;
        }

        if (info.Status != AppUpdateStatus.UpdateAvailable) return;

        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            var noInstaller = new ContentDialog
            {
                XamlRoot = UpdateButton.XamlRoot,
                Title = I18n.T("Actualizar WinForge"),
                Content = I18n.T("No se encontró el instalador en la release. Descargalo manualmente desde el repositorio."),
                CloseButtonText = I18n.T("Cerrar"),
                DefaultButton = ContentDialogButton.Close
            };
            await noInstaller.ShowAsync();
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = UpdateButton.XamlRoot,
            Title = I18n.T("Actualizar WinForge"),
            Content = I18n.T("Se descargará la versión {0} y la app se cerrará para instalarla. ¿Continuar?", info.LatestVersion),
            PrimaryButtonText = I18n.T("Actualizar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await InstallUpdateAsync(info);
    }

    /// <summary>
    /// Flujo de instalación compartido (navbar y Configuración): guarda la línea
    /// de relanzamiento, descarga el MSI en background y lanza la instalación
    /// silenciosa. La app se cierra sola (el MSI la mata al reemplazar archivos
    /// y el CustomAction la reabre al terminar). Devuelve false si falló la descarga.
    /// </summary>
    public async Task<bool> InstallUpdateAsync(AppUpdateInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl)) return false;
        try
        {
            var restartArg = App.Services.GetRequiredService<IPostUpdateRestartService>().PrepareRestartArg();
            bool launched = await Task.Run(() =>
                _appUpdateService.DownloadAndLaunchInstaller(info.DownloadUrl!, AppUpdateService.MsiPath, restartArg));

            if (launched)
            {
                // El MSI cierra WinForge con taskkill al reemplazar archivos.
                await Task.Delay(500);
                Close();
            }
            return launched;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"MainWindow: actualizar: {ex.Message}");
            return false;
        }
    }

    // ===== Navbar: traducción determinista por colección lógica =====

    // tag → texto fuente en español (capturado la primera vez que se ve el ítem,
    // antes de cualquier traducción). Siempre se traduce desde ese texto fuente.
    private readonly Dictionary<string, string> _navEsByTag = new(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<NavigationViewItem> AllNavItems()
    {
        foreach (var item in NavigationViewControl.MenuItems.OfType<NavigationViewItem>())
            yield return item;
        foreach (var item in NavigationViewControl.FooterMenuItems.OfType<NavigationViewItem>())
            yield return item;
    }

    private void TranslateNavbar()
    {
        try
        {
            foreach (var item in AllNavItems())
            {
                if (item.Tag is not string tag) continue;
                if (!_navEsByTag.TryGetValue(tag, out var es))
                {
                    if (item.Content is not string s) continue;
                    _navEsByTag[tag] = s;
                    es = s;
                }
                var translated = I18n.T(es);
                if (item.Content is string cur)
                {
                    if (!string.Equals(translated, cur, StringComparison.Ordinal))
                        item.Content = translated;
                }
                else if (FindNavTextBlock(item.Content) is TextBlock tb)
                {
                    // Content ya es el Grid del botón ⋮: traducir el TextBlock interno.
                    if (!string.Equals(translated, tb.Text, StringComparison.Ordinal))
                        tb.Text = translated;
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Error traduciendo el navbar: {ex.Message}");
        }
    }

    /// <summary>Busca el TextBlock del nombre dentro del Content (Grid del botón ⋮).</summary>
    private static TextBlock? FindNavTextBlock(object? content)
        => content is Grid g ? g.Children.OfType<TextBlock>().FirstOrDefault() : null;

    /// <summary>
    /// Reemplaza el Content (texto) de cada pestaña por un Grid: nombre a la
    /// izquierda + botón "⋮" al extremo derecho. El botón abre un menú con
    /// "Ocultar" que esconde la pestaña (misma clave "nav.&lt;tag&gt;" que la
    /// opción de Configuración). La pestaña Configuración no lleva botón (no se
    /// puede ocultar).
    /// </summary>
    private void AttachNavItemMenus()
    {
        foreach (var item in AllNavItems())
        {
            if (item.Tag is not string tag) continue;
            if (tag == "configuracion") continue;
            if (item.Content is not string s) continue; // ya transformado
            _navEsByTag[tag] = s;

            var tb = new TextBlock
            {
                Text = s,
                VerticalAlignment = VerticalAlignment.Center
            };
            // Botón ⋮ compacto (glyph E712 "More" de Segoe MDL2, los tres puntitos
            // estándar de Windows): sin fondo ni borde, pegado al extremo derecho.
            // Se muestra SOLO al pasar el mouse sobre la pestaña (PointerEntered).
            var moreBtn = new Microsoft.UI.Xaml.Controls.Button
            {
                Content = new FontIcon
                {
                    Glyph = "\uE712",
                    FontSize = 12,
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
                },
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
                // Sin padding derecho: el glyph queda lo más pegado posible al borde.
                Padding = new Microsoft.UI.Xaml.Thickness(2, 0, 0, 0),
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                MinWidth = 20,
                MinHeight = 22,
                Tag = tag,
                // Opacity 0 (no Collapsed): el botón siempre ocupa su lugar a la
                // derecha, así el texto no se corre al aparecer/desaparecer.
                Opacity = 0,
                IsHitTestVisible = false
            };
            // Tapped (no Click/PointerPressed): TappedRoutedEventArgs permite marcar
            // Handled para que el tap en ⋮ no navegue a la pestaña, y el menú se abre
            // después del release completo (con PointerPressed el flyout se cerraba
            // apenas se soltaba el botón).
            moreBtn.Tapped += NavItemMore_Tapped;

            // El ContentPresenter del NavigationViewItem alinea a la izquierda por
            // defecto: con HorizontalContentAlignment=Stretch el contenido ocupa todo
            // el ancho de la pestaña (el estilo por defecto ya lo deja en Stretch).
            item.HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
            var grid = new Microsoft.UI.Xaml.Controls.Grid
            {
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                // El template de WinUI envuelve el contenido en un ContentGrid con
                // Margin="0,0,14,0" fijo (espacio para el chevron/scrollbar): margen
                // derecho negativo para recuperar esos 14px y que el botón ⋮ quede
                // pegado a la pared del navbar en vez de flotar 18px antes.
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, -14, 0)
            };
            grid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(tb, 0);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(moreBtn, 1);
            grid.Children.Add(tb);
            grid.Children.Add(moreBtn);
            // Hover: el ⋮ aparece (opacidad 1) al entrar el mouse a la pestaña y
            // desaparece al salir. Cuando está oculto no recibe clics.
            void ShowMore(bool show)
            {
                moreBtn.Opacity = show ? 1 : 0;
                moreBtn.IsHitTestVisible = show;
            }
            item.PointerEntered += (s, e) => ShowMore(true);
            item.PointerExited += (s, e) => ShowMore(false);
            moreBtn.PointerEntered += (s, e) => ShowMore(true);
            moreBtn.PointerExited += (s, e) => ShowMore(false);
            item.Content = grid;
        }
    }

    /// <summary>Menú del botón ⋮ de una pestaña: "Ocultar" esconde la pestaña.</summary>
    private void NavItemMore_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true; // que el tap no navegue a la pestaña
        if (sender is not Microsoft.UI.Xaml.Controls.Button btn || btn.Tag is not string tag) return;
        var menu = new MenuFlyout();
        var hide = new MenuFlyoutItem { Text = I18n.T("Ocultar") };
        hide.Click += (s, e2) =>
        {
            try
            {
                _settingsService.Set("nav." + tag, false);
                _settingsService.Save();
                ApplyNavigationVisibility();
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"Error ocultando pestaña {tag}: {ex.Message}");
            }
        };
        menu.Items.Add(hide);
        menu.ShowAt(btn);
    }

    private void ApplyLanguageButton()
    {
        LanguageFlagImage.Source = Flags.GetImage(I18n.Current)?.Source;
        LanguageNameText.Text = I18n.Current;
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        foreach (var code in I18n.Languages)
        {
            var item = new MenuFlyoutItem
            {
                Text = code,
                Tag = code,
                Icon = Flags.GetIcon(code)
            };
            if (code == I18n.Current)
            {
                // Check de idioma activo junto a la bandera.
                item.KeyboardAcceleratorTextOverride = "✓";
            }
            var selected = code;
            item.Click += (s, args) =>
            {
                if (s is MenuFlyoutItem { Tag: string c })
                    I18n.SetLanguage(c, _settingsService);
            };
            flyout.Items.Add(item);
        }
        LanguageButton.Flyout = flyout;
        flyout.ShowAt(LanguageButton);
    }

    private void NavigationViewControl_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
            {
                _loggingService.LogInfo($"Navegando a: {tag}");
                _navigationService.NavigateTo(tag);
                _loggingService.LogInfo($"Navegación completada: {tag}");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error en NavigationViewControl_ItemInvoked: {ex}", ex);
            // No propagar la excepción
        }
    }

    // ===== Icono y barra de título: logo de WinForge + ventana negra =====

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attrValue, int attrSize);

    /// <summary>
    /// Aplica esquinas redondeadas (Windows 11) a la ventana del menú de bandeja.
    /// En Windows 10 el atributo se ignora y el menú queda con esquinas cuadradas (normal).
    /// </summary>
    private static void ApplyRoundedMenuCorners(IntPtr hwnd)
    {
        try
        {
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Carga el logo de WinForge como icono de la bandeja.
    /// </summary>
    private static Icon? CreateWinForgeIcon()
    {
        try
        {
            var pngPath = Path.Combine(AppContext.BaseDirectory, "logos", "WinForge.png");
            if (!File.Exists(pngPath)) return null;
            using var src = DrawingImage.FromFile(pngPath);
            using var bmp = new Bitmap(src, new Size(32, 32));
            var hIcon = bmp.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(hIcon).Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Ruta del .ico de WinForge que viaja con la app (Assets\WinForge.ico), que ya
    /// está embebido en el exe como ApplicationIcon: la barra de tareas usa el mismo.
    /// </summary>
    private static string? GetWinForgeIcoPath()
    {
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WinForge.ico");
        return File.Exists(icoPath) ? icoPath : null;
    }

    /// <summary>
    /// Aplica los colores de la barra de título según el tema activo.
    /// </summary>
    private void ApplyTitleBarTheme(IThemeService themeService)
    {
        var appWindow = GetAppWindow();
        if (appWindow == null) return;

        bool dark = themeService.CurrentTheme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => App.Services.GetRequiredService<IThemeApplier>().GetSystemTheme() == AppTheme.Dark
        };
        ApplyTitleBarColors(appWindow, dark);
    }

    /// <summary>
    /// Colorea la barra de título con el color del navbar: #151517 (oscuro) o
    /// blanco (claro), para que la ventana sea un solo bloque con el menú lateral.
    /// </summary>
    private static void ApplyTitleBarColors(AppWindow appWindow, bool dark)
    {
        var tb = appWindow.TitleBar;
        var bg = Windows.UI.Color.FromArgb(255, dark ? (byte)0x15 : (byte)0xFF, dark ? (byte)0x15 : (byte)0xFF, dark ? (byte)0x17 : (byte)0xFF);
        var fg = Windows.UI.Color.FromArgb(255, dark ? (byte)255 : (byte)16, dark ? (byte)255 : (byte)20, dark ? (byte)255 : (byte)24);
        var inactiveFg = Windows.UI.Color.FromArgb(255, dark ? (byte)128 : (byte)96, dark ? (byte)128 : (byte)100, dark ? (byte)128 : (byte)104);
        var hover = Windows.UI.Color.FromArgb(255, dark ? (byte)45 : (byte)224, dark ? (byte)45 : (byte)228, dark ? (byte)45 : (byte)232);
        var pressed = Windows.UI.Color.FromArgb(255, dark ? (byte)70 : (byte)200, dark ? (byte)70 : (byte)204, dark ? (byte)70 : (byte)208);
        tb.BackgroundColor = bg;
        tb.ForegroundColor = fg;
        tb.InactiveBackgroundColor = bg;
        tb.InactiveForegroundColor = inactiveFg;
        tb.ButtonBackgroundColor = bg;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverBackgroundColor = hover;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedBackgroundColor = pressed;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonInactiveBackgroundColor = bg;
        tb.ButtonInactiveForegroundColor = inactiveFg;
    }
}

/// <summary>
/// Renderer del menú de bandeja: pinta el hover/pressed con color oscuro fijo (si se
/// deja al color table del sistema, en temas claros el hover sale blanco y el texto
/// blanco queda ilegible) y dibuja un borde redondeado acorde al redondeo DWM.
/// </summary>
internal class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color HoverColor = Color.FromArgb(48, 48, 48);
    private static readonly Color PressedColor = Color.FromArgb(64, 64, 64);
    private static readonly Color BorderColor = Color.FromArgb(60, 60, 60);

    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        if (item.Selected || item.Pressed)
        {
            var bounds = new Rectangle(Point.Empty, item.Size);
            using var brush = new SolidBrush(item.Pressed ? PressedColor : HoverColor);
            e.Graphics.FillRectangle(brush, bounds);
            return;
        }
        base.OnRenderMenuItemBackground(e);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // El layout nativo de ToolStripMenuItem con Image deja el texto pegado
        // arriba (bug conocido de WinForms): se fuerza el centrado vertical real
        // del texto dentro del item, ignorando el rect que calcula el layout.
        try
        {
            var size = TextRenderer.MeasureText(e.Text, e.TextFont);
            int y = Math.Max(0, (e.Item.Height - size.Height) / 2);
            e.TextRectangle = new Rectangle(e.TextRectangle.X, y, e.TextRectangle.Width, size.Height);
        }
        catch { }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
        bounds.Width -= 1;
        bounds.Height -= 1;
        using var path = RoundedRectangle(bounds, 8);
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawPath(pen, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Tabla de colores personalizada para menú contextual oscuro.
/// </summary>
internal class DarkColorTable : ProfessionalColorTable
{
    private static readonly Color BackColor = Color.FromArgb(32, 32, 32);
    private static readonly Color BorderColor = Color.FromArgb(60, 60, 60);
    private static readonly Color HoverBackColor = Color.FromArgb(48, 48, 48);
    private static readonly Color HoverBorderColor = Color.FromArgb(138, 180, 248);
    private static readonly Color SeparatorColor = Color.FromArgb(60, 60, 60);
    private static readonly Color TextColor = Color.White;

    public override Color MenuBorder => BorderColor;
    public override Color MenuItemBorder => BorderColor;
    public override Color MenuItemSelected => HoverBackColor;
    public override Color MenuItemSelectedGradientBegin => HoverBackColor;
    public override Color MenuItemSelectedGradientEnd => HoverBackColor;
    public override Color MenuItemPressedGradientBegin => HoverBackColor;
    public override Color MenuItemPressedGradientMiddle => HoverBackColor;
    public override Color MenuItemPressedGradientEnd => HoverBackColor;
    public override Color ImageMarginGradientBegin => BackColor;
    public override Color ImageMarginGradientMiddle => BackColor;
    public override Color ImageMarginGradientEnd => BackColor;
    public override Color ImageMarginRevealedGradientBegin => BackColor;
    public override Color ImageMarginRevealedGradientMiddle => BackColor;
    public override Color ImageMarginRevealedGradientEnd => BackColor;
    public override Color SeparatorDark => SeparatorColor;
    public override Color SeparatorLight => SeparatorColor;
    public override Color ToolStripDropDownBackground => BackColor;
    public override Color ToolStripBorder => BorderColor;
    public override Color ToolStripContentPanelGradientBegin => BackColor;
    public override Color ToolStripContentPanelGradientEnd => BackColor;
    public override Color ToolStripPanelGradientBegin => BackColor;
    public override Color ToolStripPanelGradientEnd => BackColor;
}

/// <summary>
/// Adaptador que implementa INavigationFrame usando un Frame de WinUI.
/// </summary>
internal class NavigationFrameAdapter : INavigationFrame
{
    private readonly Frame _frame;

    public NavigationFrameAdapter(Frame frame)
    {
        _frame = frame;
    }

    public bool CanGoBack => _frame.CanGoBack;

    public void Navigate(Type pageType, object? parameter)
    {
        _frame.Navigate(pageType, parameter);
    }

    public void Navigate(Type pageType)
    {
        _frame.Navigate(pageType);
    }

    public void GoBack()
    {
        _frame.GoBack();
    }
}
