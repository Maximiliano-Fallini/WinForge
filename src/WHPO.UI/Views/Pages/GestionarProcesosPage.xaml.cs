using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net.Http;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Polygon = Microsoft.UI.Xaml.Shapes.Polygon;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WHPO.Core.Services;
using WHPO_UI.Controls;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Services;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Página "Biblioteca de juegos" (estilo ): biblioteca de videojuegos
/// instalados (Steam/Epic/Ubisoft/EA/Blizzard…) en una grilla de 3 o 5 columnas (vista
/// cambiable en la cabecera) con el
/// banner del juego, favoritos con estrella y reglas por juego (prioridad de CPU,
/// afinidad, prioridad de GPU y plan de energía). Cada ajuste admite dos alcances:
/// "Actual" (se recuerda y aplica al lanzar desde WinForge) y "Siempre" (permanente,
/// se guarda en el registro). No muestra procesos del sistema: solo juegos.
/// </summary>
public sealed partial class GestionarProcesosPage : Page, IBackgroundPausable
{
    private readonly IProcessService _processService;
    private readonly IInstalledGamesService _installedGamesService;
    private readonly ICpuPowerService _cpuPowerService;
    private readonly ILoggingService _loggingService;
    private readonly IGameBoostService _gameBoostService;
    private readonly ISettingsService _settings;
    // Evita que el Toggled se dispare al cargar el estado inicial del switch.
    private bool _boostSwitchInitialized;
    // Estilo de las cards de la biblioteca: false = vanilla (banners planos, lo de
    // siempre), true = 3D (cartuchos con profundidad, estilo Carthage). Persistido.
    private bool _cards3D;
    // Inclinación de las cards 3D al hover (Configuración → Apariencia). Activada de
    // fábrica; se relee en cada visita porque la página queda en caché.
    private bool _cards3DTilt = true;
    // ===== Modo 3D (modelo de cartucho) =====
    // Alto del cartucho por unidad de ancho (relación 1,4 del propio modelo): la card
    // 3D es vertical y el objeto entra completo, sin deformarse.
    // Alto/ancho del cartucho 3D: sale del propio modelo (cara 600x878), así la grilla
    // no lo deforma.
    private static readonly double CartridgeHeightRatio = CartridgeModel.Aspect;

    // Columnas base de la grilla (en ventana estándar ~1400 px): 5 en modo 3D (el
    // cartucho es vertical) y 3 en modo clásico. Cuando la ventana se agranda o pasa a
    // pantalla completa, GetEffectiveColumns suma columnas automáticamente para que las
    // cards no se estiren de más.
    private const int GridColumns3D = 5;
    // Amarillo del badge "(BETA)" (color fijo deliberado: no depende del tema).
    // Se guarda el Color (struct, seguro en un campo estático) y el SolidColorBrush se
    // crea al usarlo, en el hilo de la UI: instanciar un objeto XAML en un campo
    // estático corre en el .cctor y lanza RPC_E_WRONG_THREAD (0x8001010E).
    private static readonly Windows.UI.Color BetaColor = Windows.UI.Color.FromArgb(255, 255, 212, 0);
    private const int GridColumns = 3;
    private const double MaxCardWidth3D = 205;
    private const double MaxCardWidthVanilla = 360;
    private const double MinCardWidth3D = 140;
    private const double MinCardWidthVanilla = 230;
    private const double GridCardSpacing = 16;
    // Búsqueda por nombre (filtro de la grilla); vacío = mostrar todo.
    private string _searchQuery = "";
 // Filtro de categoría: "all", "games", "console_emu", "mobile_emu".
 // Por defecto "all" (todos). La clasificación de cada item se hace en
 // GetItemCategory: los emuladores se dividen en consola vs celular por nombre.
    private string _currentFilter = "all";

    private List<InstalledGame> _installed = new();
    private List<(string Exe, string? Name, string? InstallPath)> _manual = new();
    // Estado de los desplegables de la biblioteca (se conserva entre rebuilds).
    // Favoritos y Detectados nacen expandidos, Ocultos colapsado.
    private bool _favoritesSectionExpanded = true;
    private bool _detectedSectionExpanded = true;
    private bool _hiddenSectionExpanded;
    private HashSet<string> _runningExes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Border Card, FrameworkElement Body)> _cards = new();
    private readonly List<(StackPanel Host, List<Border> Cards)> _wrapSectionGroups = new();
    private int _currentGridCols;
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
    // Detalle 3D (Opción 1): cartucho real (Cartridge3D, D3D) montado sobre RootGrid.
    private Grid? _detail3DPanel;
    private bool _detail3DCloseHooked;
    private Action<int>? _navigateDetail3D;
    // Lista ordenada de los juegos visibles en la biblioteca actual (para navegar con
    // las flechas laterales / teclas Izquierda y Derecha en la vista de detalle).
    private readonly List<(InstalledGame? Game, string Exe, string Name, bool IsManual, string? InstallPath)> _orderedLibraryGames = new();
    private bool _counterStatusVisible;
    // Skeleton de carga: cards placeholder que se agregan al MISMO panel de la
    // biblioteca (como los juegos reales) mientras se escanea. El pulso replica el
    // patrón de SistemaPage: un Storyboard suave (1.0 → 0.35 en 900 ms, auto-reverse)
    // por bloque de cada card, y un mínimo visible para que el efecto se aprecie.
    private readonly List<(Border Card, FrameworkElement Body, FrameworkElement[] Blocks)> _skeletonCards = new();
    private readonly List<Storyboard> _skeletonStoryboards = new();
    // Columnas con las que se acomodaron las filas del skeleton y modo (2D/3D) con el
    // que se construyeron: si cambia el ancho o el estilo, se reflujan/reconstruyen.
    private int _skeletonGridCols;
    private bool _skeletonCards3D;
    // Última versión de caché vista: limpiar la caché desde Configuración la cambia
    // y la próxima visita fuerza skeleton + re-escaneo, aunque otro servicio ya la
    // haya vuelto a llenar mientras tanto.
    private int _seenCacheClearVersion;
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
        WHPO.Core.AppPaths.RootDir, "gamebanners");

    // Sombra compartida de las cards de la biblioteca (ThemeShadow, receptor
    // configurado en el constructor sobre CardShadowReceiver).
    private readonly Microsoft.UI.Xaml.Media.ThemeShadow _cardShadow = new();
    // Último estado de regla recibido: la página está cacheada y, al volver, hay
    // que releerlo del servicio para no mostrar una partida que ya terminó.
    private GameRuleStatus? _lastGameRuleStatus;
    // Texto del footer que reacciona al estado de la regla. Se recrea en cada
    // rebuild del dock; lo pinta UpdateDockStatusText.
    private TextBlock? _dockStatusLine;
    private string? _dockDefaultStatus;
    // Host del LED del dock 3D: permite que la luz siga a la regla sin reconstruir
    // la consola (el rebuild parpadea). Se recrea en cada rebuild del dock.
    private ContentControl? _dockLedHost;
    // Estado de consola con el que se armó el LED (para restaurarlo al terminar la regla).
    private bool _dockLedConnected;
    private bool _dockLedRunning;
    // Cuenta regresiva de la espera launcher→juego (1 s): pinta el tiempo restante
    // real de la ventana del servicio en el footer, el header y la barra 2D.
    private DispatcherQueueTimer? _ruleCountdownTimer;

    // Posición original de la card "Modo juego de WinForge" dentro del header
    // (HeaderPanel): en modo 3D la card se MUEVE al ala derecha del dock inferior
    // y al volver a Vanilla se restaura acá, en este índice.
    private int _gameBoostCardHomeIndex = -1;
    // Coalescing del rebuild del dock: al volver a la pestaña se encadenan
    // OnNavigatedTo → UpdateCartridgeDock + dispatcher diferido + RefreshAsync
    // (async) → RebuildCards → UpdateCartridgeDock, más eventos WMI. Cada rebuild
    // hace Clear()+Add del host (la card parpadea: aparece y desaparece). Con el
    // flag se colapsa todo en UN solo rebuild por tick del dispatcher.
    private bool _dockUpdatePending;
    // Paleta con la que se armó el dock VIVO: si Apariencia cambia el color de la
    // carcasa, el early-out de GameBoostCardSettledInDock tiene que dejar de saltear
    // el rebuild (si no, la consola se quedaría con el plástico viejo).
    private string? _dockSkinKey;
    // Generación de navegación: invalida el dispatcher diferido de una visita
    // anterior (OnNavigatedFrom la incrementa). Sin esto, el callback diferido
    // de la visita N podía reconstruir el dock durante la visita N+1 y pisar el
    // estado recién armado.
    private int _navGeneration;

    /// <summary>
    /// Relleno de la banda de cabecera, reusado entre pasadas: con el vidrio encendido es
    /// el ACRÍLICO de la plataforma (ver <see cref="WHPO_UI.PanelAppearance.SurfaceFill"/>)
    /// y con el desenfoque apagado el sólido de siempre.
    /// </summary>
    private Brush? _headerFill;

    /// <summary>
    /// Pinta la banda de cabecera (HeaderCard) con el relleno del panel del menú, ya con la
    /// transparencia vigente y el vidrio que esté en juego (ver
    /// <see cref="WHPO_UI.PanelAppearance.PaneBarFill"/>): la misma pieza que el navbar de
    /// la izquierda y que la cabecera del Workshop, así que la franja de botones se lee
    /// como continuación del panel y no como un bloque aparte cortado sobre el fondo.
    /// </summary>
    private void RepaintHeaderCard()
    {
        try
        {
            _headerFill = WHPO_UI.PanelAppearance.PaneBarFill(_headerFill);
            HeaderCard.Background = _headerFill;
        }
        catch { /* arranque temprano */ }
    }

    public GestionarProcesosPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _processService = App.Services.GetRequiredService<IProcessService>();
        _installedGamesService = App.Services.GetRequiredService<IInstalledGamesService>();
        _cpuPowerService = App.Services.GetRequiredService<ICpuPowerService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _gameBoostService = App.Services.GetRequiredService<IGameBoostService>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        CleanupLegacyIconCacheDirs();

        // Sombra de las cards de la biblioteca: una sola ThemeShadow compartida
        // por todas las cards (casters), proyectada sobre el Border receptor
        // del XAML (WinUI 3: colección Receivers). La elevación (z de
        // card.Translation) se activa al hover.
        _cardShadow.Receivers.Add(CardShadowReceiver);        // Selección inicial del desplegable de categorías («Todos»). El
        // SelectionChanged no reconstruye la grilla porque _currentFilter ya es "all".
        FilterComboBox.SelectedIndex = 0;
        ApplyLibraryFilterLanguage();
        UpdateInstalledCount();
        // Casa de la card del Modo juego: su índice dentro del header. Se registra
        // una sola vez (la página está cacheada y el constructor corre una vez).
        _gameBoostCardHomeIndex = HeaderPanel.Children.IndexOf(GameBoostCard);

        // Simetría de la fila: el botón "?" debe medir lo mismo de alto que el de
        // "Configuración" (que gana su altura del padding + contenido). Se sincroniza
        // con el tamaño real y con cada cambio (escalado de texto, idioma, etc.).
        GameBoostSettingsButton.SizeChanged += (s, e) =>
        {
            if (!_cards3D || GameBoostCard.Parent == HeaderPanel)
                GameBoostInfoButton.Height = Math.Max(1, e.NewSize.Height);
        };
        RootGrid.SizeChanged += (_, _) => UpdateCartridgeDockWidth();

        // La banda de la cabecera (HeaderCard) se pinta a mano, IGUAL que la del Workshop y
        // el navbar del menú (ver RepaintHeaderCard): WinUI cachea el {ThemeResource} del
        // Border y no lo vuelve a resolver cuando el ajuste REEMPLAZA la instancia del
        // diccionario, así que con el XAML solo la banda quedaba opaca y sin seguir el
        // vidrio/transparencia del tema. La página usa caché de navegación, así que la
        // suscripción y la baja se atan a Loaded/Unloaded para no dejar el handler colgado.
        Loaded += (_, _) =>
        {
            // Baja antes del alta: la página se carga más de una vez (caché de navegación)
            // y no puede quedar suscrita dos veces.
            WHPO_UI.PanelAppearance.Applied -= RepaintHeaderCard;
            WHPO_UI.PanelAppearance.Applied += RepaintHeaderCard;
            RepaintHeaderCard();
        };
        Unloaded += (_, _) => WHPO_UI.PanelAppearance.Applied -= RepaintHeaderCard;
        Loaded += (_, _) =>
        {
            // El early-out vive en RequestCartridgeDockUpdate
            // (GameBoostCardSettledInDock): si la card ya está en el dock, no
            // se reconstruye.
            if (_cards3D) RequestCartridgeDockUpdate();
        };
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Estado del switch "Modo rendimiento".
        _boostSwitchInitialized = false;
        GameBoostSwitch.IsChecked = _gameBoostService.IsEnabled;
        _boostSwitchInitialized = true;
        UpdateGameBoostLabel();

        // Estilo de las cards (2D / 3D), persistido. Se elige en Configuración →
        // Apariencia (el header de la biblioteca ya no tiene switch) y se relee acá en
        // cada visita: por defecto, cartuchos 3D.
        _cards3D = _settings.Get("biblioteca.cards3D", true);
        _cards3DTilt = _settings.Get("biblioteca.cards3D.tilt", true);
        // Paleta de la apariencia 3D (cartucho + consola): se refresca acá, en la UI,
        // porque el acento del tema vive en recursos de XAML y los pintores corren en
        // segundo plano. Al volver de Configuración → Apariencia ya toma el color nuevo.
        CardSkin.Refresh();
        // UN solo rebuild coalescado por visita: RequestCartridgeDockUpdate hace
        // early-out solo si la card ya vive en el dock armado
        // (GameBoostCardSettledInDock). La generación invalida el callback
        // diferido de una visita anterior (ver OnNavigatedFrom).
        int gen = ++_navGeneration;
        RequestCartridgeDockUpdate();
        if (_cards3D)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (gen != _navGeneration) return;
                RequestCartridgeDockUpdate();
            });
        }

        // La biblioteca viene cacheada (InstalledGamesService): la primera vez se
        // escanea y se guarda, y las siguientes aperturas la leen sin re-escannear.
        // El skeleton cubre la primera carga real (sin caché) y también la visita
        // posterior a "Limpiar caché": esa operación sube CacheClearVersion, así que
        // aunque otro servicio haya repoblado la caché en el medio, se vuelve a
        // escanear y se muestra el skeleton.
        int cacheClearVersion = _installedGamesService.CacheClearVersion;
        bool cacheCleared = cacheClearVersion != _seenCacheClearVersion;
        _seenCacheClearVersion = cacheClearVersion;
        _ = RefreshAsync(
            showSkeleton: cacheCleared || !_installedGamesService.HasCachedResult,
            refreshCache: cacheCleared);
        I18n.LanguageChanged += OnLanguageChanged;
        // Estado en vivo de los juegos (badge "En ejecución"): los eventos WMI
        // publican un snapshot sin polling; acá solo se refleja en las cards.
        _processService.RunningGamesChanged += OnRunningGamesChanged;
        // Estado de la regla (esperando el proceso real / aplicada / no se pudo
        // identificar): con launcher de por medio, el juego no nace con el exe de la
        // biblioteca y el usuario necesita ver qué pasó, sin avisos por cada arranque.
        _processService.GameRuleStatusChanged += OnGameRuleStatusChanged;
        // La página está cacheada: al volver, el último estado pudo quedar viejo si
        // el juego cerró mientras estaba en otra pestaña. Se relee del servicio.
        RestoreGameRuleStatus();

        // Estado del launcher (botón "Iniciar"): chequeo único al abrir + eventos
        // WMI en adelante. Cero polling mientras la página esté visible.
        _processService.LauncherStateChanged += OnLauncherStateChanged;
        UpdateAllLauncherButtons();

        // Precalentar el cache de reglas: el menú de la tuerca (ShowRuleMenu) lo
        // lee en el hilo de UI; sin esto, el PRIMER click de cada sesión pagaba la
        // lectura del JSON de settings del disco (los siguientes ya iban cacheados).
        _ = Task.Run(() =>
        {
            try { _processService.GetRulesCached(); } catch { }
        });
        // Feedback del Modo juego de WinForge: al empezar a aplicar se muestra el
        // estado "aplicando…" (los comandos son asíncronos) y recién cuando el
        // servicio CONFIRMA el resultado llega el resumen (reglas, servicios
        // detenidos, procesos optimizados y cerrados). Si se cancela a mitad de
        // camino, se limpia el estado en curso.
        // Los eventos llegan desde el hilo del Task.Run del servicio: se re-despachan a la UI.
        _gameBoostService.BoostApplying += OnBoostApplying;
        _gameBoostService.BoostApplied += OnBoostApplied;
        _gameBoostService.BoostCancelled += OnBoostCancelled;
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Invalidar el callback diferido pendiente de esta visita (ver
        // OnNavigatedTo): si corre después de salir, pisaría el dock de la
        // visita siguiente. También se descarta el coalescing pendiente.
        _navGeneration++;
        StopRuleCountdown();
        _dockUpdatePending = false;
        I18n.LanguageChanged -= OnLanguageChanged;
        _processService.RunningGamesChanged -= OnRunningGamesChanged;
        _processService.GameRuleStatusChanged -= OnGameRuleStatusChanged;
        _processService.LauncherStateChanged -= OnLauncherStateChanged;
        _gameBoostService.BoostApplying -= OnBoostApplying;
        _gameBoostService.BoostApplied -= OnBoostApplied;
        _gameBoostService.BoostCancelled -= OnBoostCancelled;
        // Si se sale de la página (cacheada) estando en la vista de configuración,
        // volver al estado base para que el próximo ingreso muestre la biblioteca.
        if (BoostConfigView.Visibility == Visibility.Visible)
            CloseBoostConfigView(saveChanges: false);
        CancelCartridgeDragImmediate();
        // NO estacionar la card en el header al salir: la página está cacheada y
        // la card vive en el dock armado. Mudarla al header acá y de vuelta al
        // dock al volver eran dos mudanzas visibles = la card "aparecía y
        // desaparecía" al cambiar de pestaña. Con la página cacheada, al volver
        // el dock ya está armado con la card adentro y RequestCartridgeDockUpdate
        // lo detecta (GameBoostCardSettledInDock) sin reconstruir.
        CloseGame3DDetail();
        StopSkeletonPulse();
    }

    private void OnLanguageChanged()
    {
        ApplyLibraryFilterLanguage();
        RetranslateScopeItems(_servicesScopeItems);
        RetranslateScopeItems(_tasksScopeItems);
        UpdateGameBoostLabel();
        RebuildCards();
        // Re-traducir el contador de juegos instalados: es texto dinámico seteado
        // con I18n.T, el walker no puede re-traducirlo solo (la clave es con {0}).
        if (InstalledCountText.Visibility == Visibility.Visible)
            UpdateInstalledCount();
        // El placeholder del buscador también se re-aplica por si cambió de idioma
        // antes de que la página navegara acá.
        SearchBox.PlaceholderText = I18n.T("Buscar juegos...");
    }

    /// <summary>
    /// Traduce los ítems del desplegable de categorías de la biblioteca (Todos /
    /// Videojuegos / Emuladores). Los ComboBoxItem no se realizan en el árbol visual
    /// hasta que se abre el desplegable —su popup vive en su propio árbol—, así que el
    /// recorrido de I18n no siempre los alcanza: se traducen por código, mismo patrón que
    /// los ítems de tema en Configuración y los de preajuste en Memoria.
    ///
    /// Además el motor no puede resolverlos al revés: los packs traducen «Todos» y «Todas»
    /// al mismo 全部, así que un ítem sin registrar quedaba clavado en el idioma anterior
    /// (y con «Videojuegos» el desempate lo atribuía a la clave «Gaming» y lo escribía
    /// "Gaming", que es el nombre del preset de Optimizaciones, no el ítem del filtro).
    /// </summary>
    private void ApplyLibraryFilterLanguage()
    {
        FilterAllItem.Content = I18n.T("Todos");
        FilterGamesItem.Content = I18n.T("Videojuegos");
        FilterConsoleEmulatorsItem.Content = I18n.T("Emuladores de consolas");
        FilterMobileEmulatorsItem.Content = I18n.T("Emuladores de celular");
    }

    /// <summary>
    /// Re-traduce los ítems de un desplegable armado por código (el Tag guarda la clave
    /// fuente). Se llama al cambiar de idioma: el ítem no está en el árbol visual hasta
    /// abrir el popup, así que el recorrido de I18n no lo alcanza y quedaría en el idioma
    /// anterior.
    /// </summary>
    private static void RetranslateScopeItems(ComboBoxItem[]? items)
    {
        if (items == null) return;
        foreach (var item in items)
            if (item.Tag is string key)
                item.Content = I18n.T(key);
    }

    private void GameBoostSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_boostSwitchInitialized) return;
        bool on = GameBoostSwitch.IsChecked == true;
        _gameBoostService.SetEnabled(on);
        // Forzar el estado visual del template (Checked/Unchecked): si el VSM
        // automático no enganchó (la perilla quedaba clavada), esto la gira.
        VisualStateManager.GoToState(GameBoostSwitch, on ? "Checked" : "Unchecked", true);
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
        GameBoostSwitch.IsChecked = GameBoostSwitch.IsChecked != true;
    }

    private static bool IsWithin(DependencyObject node, DependencyObject root)
    {
        for (var current = node; current != null; current = VisualTreeHelper.GetParent(current))
            if (current == root) return true;
        return false;
    }

    /// <summary>Etiqueta del switch ("Modo rendimiento"), indicador OFF/ON de la
    /// perilla y tooltip informativo.</summary>
    private void UpdateGameBoostLabel()
    {
        bool inDock = _cards3D && GameBoostCard.Parent != null && GameBoostCard.Parent != HeaderPanel;
        GameBoostLabel.Inlines.Clear();
        GameBoostLabel.Text = I18n.T("Modo rendimiento");
        // El switch ya no es un ToggleSwitch con On/OffContent: nombrarlo para los
        // lectores de pantalla (el estilo del mock lleva el Off/On en el template).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GameBoostSwitch, I18n.T("Modo rendimiento"));
        if (inDock)
        {
            GameBoostLabel.Foreground = CartridgeDock.TextBrush();
        }
        else
            GameBoostLabel.ClearValue(TextBlock.ForegroundProperty);

        // Tooltip con el mismo estilo que los botones "?" informativos del resto de
        // la app: título (semi-negrita) + descripción con salto de línea, Placement
        // Bottom y ancho máximo 420.
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
                Text = I18n.T("Modo rendimiento"),
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
                "Detiene temporalmente los servicios de mantenimiento, diagnóstico e impresión que estén corriendo (SysMain, WSearch, DiagTrack, Spooler…): los ya detenidos no se tocan.",
                "Pausa Windows Update mientras dura la partida.",
                "Baja la prioridad y activa el modo de eficiencia en los procesos de segundo plano (búsqueda, widgets, OneDrive…).",
                "Silencia las notificaciones durante la partida.",
                "Libera recursos: más CPU, disco y red para tu juego."
            })
            {
                content.Children.Add(Line(key, bullet: true));
            }

            content.Children.Add(Line("Al cerrar el juego (o la app) todo vuelve a su estado previo: solo se reactivan los servicios que estaban corriendo y las prioridades y notificaciones vuelven a su estado original."));

            return new ToolTip
            {
                Placement = inDock ? PlacementMode.Top : PlacementMode.Bottom,
                Content = content
            };
        }
        ToolTipService.SetToolTip(GameBoostInfoButton, BuildGameBoostToolTip());
        ToolTipService.SetToolTip(GameBoostLabel, BuildGameBoostToolTip());
    }
    /// <summary>
    /// Feedback del Modo juego de WinForge al aplicarse (juego iniciado): muestra
    /// en StatusText el resumen con reglas de juego aplicadas, servicios optimizados,
    /// procesos optimizados y procesos cerrados. El evento llega desde el hilo del
    /// servicio (Task.Run): se re-despacha a la UI.
    /// </summary>
    /// <summary>
    /// Aviso de "aplicando" del Modo juego: los comandos del boost (sc stop, cierre de
    /// procesos) son asíncronos y su efecto real se confirma después, así que mientras
    /// tanto se muestra un estado en curso en lugar de anunciar un éxito sin verificar.
    /// </summary>
    private void OnBoostApplying()
    {
        DispatcherQueue.TryEnqueue(() => Feedback.Running(StatusText,
            I18n.T("Modo juego: aplicando optimización... esperando confirmación de servicios y procesos."),
            persistent: true));
    }

    /// <summary>
    /// El boost se canceló antes de poder confirmar (típicamente el juego se cerró
    /// enseguida): se limpia el estado en curso para no dejar un "aplicando…" eterno.
    /// </summary>
    private void OnBoostCancelled()
    {
        DispatcherQueue.TryEnqueue(() => Feedback.Set(StatusText, null));
    }

    /// <summary>
    /// Resumen del Modo juego, ya VERIFICADO por el servicio: los conteos son los que
    /// se pudieron confirmar (servicios que dejaron de correr, procesos que aceptaron
    /// la prioridad baja / Efficiency Mode). Lo que no se pudo confirmar se aclara
    /// aparte, en un aviso, en vez de contarse como logro.
    /// </summary>
    private void OnBoostApplied(GameBoostApplyResult result)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // "No había nada que optimizar" en vez de una fila de ceros: si el equipo no
            // tenía ninguno de los servicios de la lista corriendo ni procesos en segundo
            // plano, el resumen con ceros se lee como si el boost hubiera fallado, cuando
            // en realidad no había nada que hacer (una PC sin SysMain/DiagTrack, por
            // ejemplo). El cero SÍ se sigue mostrando cuando algo resistió: los resistidos
            // entran en la clasificación y aparecen en el aviso de abajo. La decisión está
            // en Core (GameBoostSummary) para poder verificarla sin levantar la UI.
            var msg = GameBoostSummary.Classify(result) switch
            {
                GameBoostSummaryKind.NothingToOptimize =>
                    I18n.T("Modo juego activo: no había nada que optimizar."),

                GameBoostSummaryKind.RulesOnly =>
                    I18n.T("Modo juego activo: {0} reglas aplicadas. No había servicios ni procesos para optimizar.",
                        result.RulesApplied),

                _ => I18n.T(
                    "Modo juego activo: {0} reglas aplicadas, {1} servicios detenidos, {2} procesos optimizados y {3} procesos cerrados.",
                    result.RulesApplied, result.ServicesOptimized, result.ProcessesOptimized, result.ProcessesKilled),
            };

            var caveats = new List<string>();
            if (result.ServicesResisted is { Count: > 0 } resisted)
                caveats.Add(I18n.T("No se confirmó la detención de: {0}.", string.Join(", ", resisted)));
            if (result.ProcessesResisted > 0)
                caveats.Add(I18n.T("No se confirmó la optimización de {0} procesos (siguen con prioridad normal).", result.ProcessesResisted));
            if (result.GameModeWarning)
                // El Modo Juego de WINDOWS no se pudo activar (huérfano: faltan archivos
                // de Game Bar): aviso claro en vez de éxito falso. El boost de WinForge
                // (procesos/servicios) igual se aplicó.
                caveats.Add(I18n.T("El Modo Juego de Windows no se pudo activar (faltan archivos de Game Bar). Reinstalá Game Bar desde la Microsoft Store."));

            if (caveats.Count == 0)
                Feedback.Success(StatusText, msg, persistent: true);
            else
                Feedback.Warning(StatusText, msg + " — " + string.Join(" ", caveats), persistent: true);
        });
    }

    // ===================== Tuerca: configuración del optimizador =====================

    /// <summary>
    /// Popup de configuración del optimizador: procesos en segundo plano.
    /// Los procesos DEFAULT del boost aparecen bloqueados (no se pueden quitar); los
    /// AGREGADOS por el usuario sí, y se suman desde la lista de procesos en ejecución
    /// con doble clic (sin tipear nombres a mano).
    /// </summary>
    private void GameBoostSettingsButton_Click(object sender, RoutedEventArgs e) => OpenBoostConfigView();

    // ===================== Salud del Modo Juego de Windows =====================

    /// <summary>
    /// Card de la pestaña "Salud del Modo Juego" (configuración del boost): muestra
    /// el estado del Modo Juego de WINDOWS (no el de WinForge) en un banner con
    /// paleta profesional de TRES estados:
    ///  · Verde "Funcional": todo correcto, se activará al iniciar un juego.
    ///  · Ámbar "Activable": solo están deshabilitadas las reglas del registro
    ///    (estilo AtlasOS) — un clic lo deja listo sin reinstalar nada.
    ///  · Rojo "Crítico": falta infraestructura de Game Bar (Appx
    ///    XboxGamingOverlay o GameBarPresenceWriter.dll — debloat extremo):
    ///    ninguna regla de registro puede revivirlo, hay que reinstalar Game Bar.
    /// </summary>
    private UIElement BuildGameModeHealthTab()
    {
        var muted = Feedback.MutedBrush;

        // Tint de un pincel de estado del tema (no colores hardcodeados): se usa
        // para el fondo y el borde del banner en el color del estado actual.
        static SolidColorBrush Tint(SolidColorBrush brush, byte alpha)
        {
            var c = brush.Color;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B));
        }

        // Banner de estado: ícono grande + título del estado + resumen, sobre un
        // fondo tintado con el borde del color del estado (verde/ámbar/rojo).
        var statusIcon = new FontIcon { Glyph = "\uE9CE", FontSize = 26 };
        var statusTitle = new TextBlock { FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var statusSummary = new TextBlock { FontSize = 12, Foreground = muted, TextWrapping = TextWrapping.Wrap, Opacity = 0.95 };
        var statusBanner = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
            Child = new Grid { ColumnSpacing = 14, ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            } }
        };
        var bannerGrid = (Grid)statusBanner.Child!;
        bannerGrid.Children.Add(statusIcon);
        var statusTexts = new StackPanel { Spacing = 3, Children = { statusTitle, statusSummary } };
        Grid.SetColumn(statusTexts, 1);
        bannerGrid.Children.Add(statusTexts);

        // Checks individuales (registro / Appx / DLL): check o cruz + nombre — detalle.
        var checksHost = new StackPanel { Spacing = 8 };

        // Acciones según estado (solo una visible a la vez):
        //  · "Activable" → reactivar las reglas de registro (el Enable Game Mode.reg de AtlasOS).
        //  · "Crítico" → abrir Game Bar en la Microsoft Store (reinstalar es la única salida).
        var actionHost = new StackPanel { Spacing = 8 };
        var repairButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE777", FontSize = 13 },
                    new TextBlock { Text = I18n.T("Reactivar ahora"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }
                }
            },
            Padding = new Thickness(16, 8, 16, 8),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Feedback.WarningBrush,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 24, 10))
        };
        // Construido una sola vez; los handlers se suman abajo.
        var storeButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE8B7", FontSize = 13 },
                    new TextBlock { Text = I18n.T("Reinstalar Game Bar"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }
                }
            },
            Padding = new Thickness(16, 8, 16, 8),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Feedback.ErrorBrush,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
        };
        var repairFeedback = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(statusBanner);
        content.Children.Add(checksHost);
        content.Children.Add(actionHost);
        content.Children.Add(repairFeedback);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 10, 0),
            Content = content
        };

        var card = MakeSettingsCard(scroll);

        async Task LoadAsync()
        {
            var health = App.Services.GetRequiredService<IWindowsGameModeHealthService>();

            // Cargando: banner neutro mientras se lee registro + Appx + archivos.
            statusIcon.Glyph = "\uE9CE";
            statusIcon.Foreground = muted;
            statusTitle.Text = I18n.T("Chequeando...");
            statusTitle.Foreground = muted;
            statusSummary.Text = I18n.T("Leyendo el registro, el paquete Appx y los archivos del Modo Juego...");
            checksHost.Children.Clear();
            actionHost.Children.Clear();
            repairFeedback.Visibility = Visibility.Collapsed;
            statusBanner.Background = Tint(muted, 12);
            statusBanner.BorderBrush = muted;

            var info = await health.CheckAsync();

            // Paleta profesional: verde=Funcional · ámbar=Activable · rojo=Crítico.
            var (glyph, title, color) = info.Status switch
            {
                WindowsGameModeHealth.Ok => ("\uE73E", I18n.T("Funcional"), Feedback.SuccessBrush),
                WindowsGameModeHealth.DisabledByRules => ("\uE7BA", I18n.T("Activable"), Feedback.WarningBrush),
                WindowsGameModeHealth.Orphaned => ("\uE783", I18n.T("Crítico"), Feedback.ErrorBrush),
                _ => ("\uE9CE", I18n.T("Estado desconocido"), Feedback.WarningBrush)
            };
            statusIcon.Glyph = glyph;
            statusIcon.Foreground = color;
            statusTitle.Text = title;
            statusTitle.Foreground = color;
            statusSummary.Text = info.Status switch
            {
                WindowsGameModeHealth.Ok => I18n.T("Todo correcto: el Modo Juego de Windows se activará al iniciar un juego."),
                WindowsGameModeHealth.DisabledByRules => I18n.T("Solo está deshabilitado por las reglas del registro (estilo AtlasOS): se puede reactivar al instante sin reinstalar nada."),
                WindowsGameModeHealth.Orphaned => I18n.T("Faltan archivos de Game Bar (Appx XboxGamingOverlay o GameBarPresenceWriter.dll). Sin Game Bar el Modo Juego no puede funcionar: reinstalalo desde la Microsoft Store."),
                _ => I18n.T("No se pudo verificar el estado del Modo Juego. Reintentá más tarde.")
            };
            // Fondo y borde del banner tintados con el color del estado.
            statusBanner.Background = Tint(color, 24);
            statusBanner.BorderBrush = Tint(color, 128);

            checksHost.Children.Clear();
            foreach (var (component, present, detail) in info.Checks)
            {
                // Grid en vez de StackPanel horizontal: "component — detalle" es un
                // texto largo que con StackPanel se mide con ancho infinito y se corta;
                // la columna "*" lo limita y TextWrapping.Wrap lo envuelve.
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new FontIcon
                {
                    Glyph = present ? "\uE73E" : "\uE783", // CheckMark / Error
                    FontSize = 13,
                    Foreground = present ? Feedback.SuccessBrush : Feedback.ErrorBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                var checkText = new TextBlock
                {
                    // Los textos vienen del servicio en Core (español crudo): traducir
                    // cada parte por separado — el compuesto no es clave conocida del
                    // diccionario y quedaría sin traducir en cualquier idioma no-es.
                    Text = $"{I18n.T(component)} — {I18n.T(detail)}",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(checkText, 1);
                row.Children.Add(checkText);
                checksHost.Children.Add(row);
            }

            // Acción según el estado: reactivación de reglas (ámbar) o reinstalación
            // de Game Bar en la Microsoft Store (rojo). Verde/desconocido: nada.
            actionHost.Children.Clear();
            if (info.Status == WindowsGameModeHealth.DisabledByRules)
                actionHost.Children.Add(repairButton);
            else if (info.Status == WindowsGameModeHealth.Orphaned)
                actionHost.Children.Add(storeButton);
        }

        repairButton.Click += async (_, _) =>
        {
            repairButton.IsEnabled = false;
            try
            {
                var health = App.Services.GetRequiredService<IWindowsGameModeHealthService>();
                var fixedInfo = await health.EnableGameModeAsync();
                repairFeedback.Visibility = Visibility.Visible;
                if (fixedInfo.Status == WindowsGameModeHealth.Ok)
                {
                    repairFeedback.Text = I18n.T("Modo Juego de Windows reactivado: se activará al iniciar un juego.");
                    repairFeedback.Foreground = Feedback.SuccessBrush;
                }
                else
                {
                    repairFeedback.Text = I18n.T(fixedInfo.Summary);
                    repairFeedback.Foreground = Feedback.WarningBrush;
                }
                await LoadAsync();
            }
            finally
            {
                repairButton.IsEnabled = true;
            }
        };

        // Estado "Crítico": el único camino es reinstalar Game Bar. El botón abre
        // su ficha en la Microsoft Store (ProductId de Xbox Game Bar = 9NZKPSTSNW4P).
        // La Store depende de Windows Update para instalar: si está desactivado por
        // el perfil de WHPO, se avisa primero y se habilita (perfil recomendado).
        storeButton.Click += async (_, _) =>
        {
            try
            {
                var wu = App.Services.GetRequiredService<IWindowsUpdateService>();
                var policy = wu.GetCurrentPolicy();
                if (policy.Mode == WindowsUpdateMode.Disabled && XamlRoot is not null)
                {
                    // Aviso ANTES de tocar nada: sin Windows Update la Store no instala nada.
                    var dialog = new ContentDialog
                    {
                        Title = I18n.T("Habilitar Windows Update"),
                        Content = I18n.T("La Microsoft Store necesita Windows Update activo para poder instalar Game Bar. WinForge lo va a habilitar antes de abrir la Store (podés volver a desactivarlo después desde la pestaña de Windows Update)."),
                        PrimaryButtonText = I18n.T("Habilitar y abrir la Store"),
                        CloseButtonText = I18n.T("Cancelar"),
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = XamlRoot
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                    repairFeedback.Visibility = Visibility.Visible;
                    repairFeedback.Foreground = muted;
                    repairFeedback.Text = I18n.T("Habilitando Windows Update...");

                    var result = await wu.ApplyPolicyAsync(WindowsUpdateMode.Recommended);
                    if (!result.Success)
                    {
                        repairFeedback.Foreground = Feedback.ErrorBrush;
                        repairFeedback.Text = result.Message;
                        _loggingService.LogWarning($"GameModeHealth: no se pudo habilitar Windows Update: {result.Message}");
                        return;
                    }
                    repairFeedback.Foreground = Feedback.SuccessBrush;
                    repairFeedback.Text = I18n.T("Windows Update habilitado. Abriendo la Microsoft Store...");
                }

                _ = Windows.System.Launcher.LaunchUriAsync(
                    new Uri("ms-windows-store://pdp/?ProductId=9NZKPSTSNW4P"));
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"GameModeHealth: no se pudo abrir la Microsoft Store: {ex.Message}");
            }
        };

        // Primera carga: UpdateMode la dispara al entrar en la pestaña
        // vía LoadGameModeHealthAsync().
        _loadGameModeHealth = () => _ = LoadAsync();
        return card;
    }

    /// <summary>Acción de recarga de la pestaña Salud del Modo Juego (se setea al construirla).</summary>
    private Action? _loadGameModeHealth;

    /// <summary>Punto de entrada de recarga llamado por UpdateMode al entrar a la pestaña.</summary>
    private void LoadGameModeHealthAsync() => _loadGameModeHealth?.Invoke();

 // Listas en edición mientras la vista de configuración está abierta: solo se
 // persisten en el servicio al presionar "Guardar" (volver descarta los cambios).
    private List<string>? _boostEfficiencyCustom;
    private List<string>? _boostKillCustom;

 // Auto-actualización de la lista "Procesos en ejecución" de la vista de
 // configuración: un timer refresca el snapshot mientras la vista está abierta.
 // El timer se recrea en cada entrada a la vista y se detiene al cerrarla
 // (CloseBoostConfigView) o al entrar de nuevo (token de generación).
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _boostPickerTimer;
    private int _boostPickerGeneration;

 /// <summary>
 /// Cambia la página al estado "Configuración del optimizador": reemplaza al
 /// ContentDialog (tanta información no tiene sentido en un popup) por una vista
 /// dedicada dentro de la pestaña, con botón para volver a la biblioteca. El
 /// contenido se construye nuevo en cada entrada para partir siempre de los
 /// valores guardados.
 /// </summary>
    private void OpenBoostConfigView()
    {
        var defaults = _gameBoostService.GetDefaultBackgroundProcesses();
        _boostEfficiencyCustom = _gameBoostService.GetBackgroundProcesses()
            .Where(n => !defaults.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();
        _boostKillCustom = _gameBoostService.GetKillProcesses();

        try
        {
            BoostConfigHost.Content = BuildProfessionalProcessesTab(
                defaults, _boostEfficiencyCustom, _boostKillCustom);
        }
        catch (Exception ex)
        {
 // Sin esto, una excepción al construir la vista quedaba como "no controlada"
 // en el log y el click parecía "no hacer nada". Acá queda asentada con
 // stack y la UI no se rompe.
            _loggingService.LogError($"BoostConfig: error construyendo la vista: {ex}");
            StatusText.Text = I18n.T("No se pudo abrir la configuración del optimizador. Revisá el log de errores.");
            StatusText.Foreground = Feedback.ErrorBrush;
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        MainView.Visibility = Visibility.Collapsed;
        BoostConfigView.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Pausa de bandeja (IBackgroundPausable): Stop real del timer de 5 s de la
    /// vista de configuración (lista de procesos en ejecución). La biblioteca en
    /// sí ya es cero-polling (eventos WMI): no hay nada más que pausar.
    /// Al volver se reanuda si la vista de configuración sigue abierta; si se
    /// cerró, CloseBoostConfigView ya detuvo el timer.
    /// </summary>
    public void PauseBackgroundTimers()
    {
        _boostPickerTimer?.Stop();
        CancelCartridgeDragImmediate();
    }

    public void ResumeBackgroundTimers()
    {
        if (App.MainWindowInstance?.IsWindowVisible == true
            && BoostConfigView.Visibility == Visibility.Visible)
            _boostPickerTimer?.Start();
    }

    private void BoostBackButton_Click(object sender, RoutedEventArgs e) => CloseBoostConfigView(saveChanges: false);

    private void BoostSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boostEfficiencyCustom == null || _boostKillCustom == null) return;
        _gameBoostService.SetBackgroundProcesses(_boostEfficiencyCustom);
        _gameBoostService.SetKillProcesses(_boostKillCustom);
        CloseBoostConfigView(saveChanges: true);
    }

 /// <summary>Vuelve a la vista de biblioteca, opcionalmente con feedback de guardado.</summary>
    private void CloseBoostConfigView(bool saveChanges)
    {
 // Detener la auto-actualización de la lista de procesos: sin vista abierta
 // no hay nada que refrescar (y el timer no debe sobrevivir al cierre).
        _boostPickerTimer?.Stop();
        _boostPickerTimer = null;
        _boostPickerGeneration++;
 // Invalidar el snapshot de servicios: cada apertura de la pestaña re-consulta
 // WMI, así los servicios instalados DESPUÉS de abrir la app (ej. MongoDB
 // Server) aparecen la próxima vez sin reiniciar WinForge. El filtro también
 // se resetea porque el TextBox se reconstruye vacío en cada apertura.
        _allServicesCache = null;
        _servicesFilter = "";
        _servicesRecommendedOnly = true;
        _servicesSearchDebounce?.Stop();
        _servicesSearchDebounce = null;
        BoostConfigHost.Content = null;
        _boostEfficiencyCustom = null;
        _boostKillCustom = null;
        BoostConfigView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
        if (saveChanges)
        {
            StatusText.Text = I18n.T("Configuración del Modo juego guardada.");
            StatusText.Foreground = Feedback.SuccessBrush;
            StatusText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Card estándar del popup: fondo/borde de card del tema, esquinas y padding.</summary>
    private static Border MakeSettingsCard(UIElement inner) => new()
    {
        Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
        BorderBrush = ThemeBrushes.Get("CardBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14),
 // Estiramiento EXPLÍCITO: asegura que el contenido (el grid de filas) ocupe
 // toda la card. Sin esto no se garantiza que la fila * reparta el sobrante.
        VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalAlignment = HorizontalAlignment.Stretch,
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
        WHPO.Core.AppPaths.RootDir, "gamebanners", "procicons-v2");

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
            foreach (var legacy in new[]
            {
                Path.Combine(WHPO.Core.AppPaths.RootDir, "gamebanners", "procicons"),
                Path.Combine(WHPO.Core.AppPaths.RootDir, "gamebanners", "exeicons")
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
    /// Pestaña Procesos: tres columnas —
    /// 1) procesos en ejecución (doble clic para agregar a la lista elegida),
    /// 2) Modo eficiencia: procesos a los que se les baja prioridad + EcoQoS,
 /// 3) Cerrar al iniciar: procesos que se cierran al lanzar un juego.
 /// El selector de destino (botones Eficiencia/Cerrar) decide dónde cae el
 /// proceso al hacer doble clic en la columna izquierda.
 /// </summary>
 /// <summary>
 /// Vista "Configuración del optimizador" (columna izquierda: selector de
 /// procesos; derecha: modo + destino + servicios).
 /// La card de servicios muestra el estado REAL de cada servicio en Windows
 /// (Desactivado / Manual / Activado) con un selector segmentado de 3 opciones
 /// + punto de color (corriendo o no). Cambiar el selector ejecuta
 /// <c>sc config</c> en el momento: no hay lista intermedia que guardar.
 /// </summary>
    private UIElement BuildProfessionalProcessesTab(List<string> defaults, List<string> efficiencyCustom, List<string> killCustom)
    {
        var accent = ThemeBrushes.Get("AccentBrush");
        var accentForeground = ThemeBrushes.Get("AccentForegroundBrush");
        var muted = Feedback.MutedBrush;
        var borderBrush = ThemeBrushes.Get("CardBorderBrush");
        var secondaryFill = ThemeBrushes.Get("CardBackgroundFillColorSecondaryBrush");
 // Relleno de las pestañas inactivas del selector: la card de la app, un tono
 // por encima/detrás del contenedor, para que cada pestaña se lea como un
 // botón y no como texto suelto sobre el fondo.
        var tabFill = ThemeBrushes.GetSurface("CardBackgroundBrush");
        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "MemCompression", "csrss",
            "smss", "wininit", "winlogon", "lsass", "services", "svchost", "dwm",
            "fontdrvhost", "conhost", "dllhost", "RuntimeBroker", "audiodg", "MsMpEng",
            "spoolsv", "WudfHost", "WinForge"
        };
        var running = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(process.ProcessName) && seen.Add(process.ProcessName))
                        running.Add(process.ProcessName);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
        catch { }

        var efficiencyRows = new StackPanel { Spacing = 3 };
        var efficiencyLockedRows = new StackPanel { Spacing = 3 };
        var killRows = new StackPanel { Spacing = 3 };
        var selectedTarget = "efficiency";

 // Helper: grid de filas (todas Auto excepto la de índice stretchIndex, que
 // estira su contenido hasta llenar la card, así no queda espacio muerto al
 // pie). rowCount SIEMPRE tiene que cubrir a todos los hijos: una fila
 // declarada de menos hace que WinUI apile el último elemento sobre el
 // anterior y que un ScrollViewer en fila Auto no se restrinja nunca.
        static Grid MakeRowsGrid(int rowCount, int stretchIndex, double spacing)
        {
            var g = new Grid { RowSpacing = spacing, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch };
            for (int i = 0; i < rowCount; i++)
                g.RowDefinitions.Add(new RowDefinition { Height = i == stretchIndex ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            return g;
        }

 // Helper: encabezado de sección centrado sobre una línea (línea — texto — línea).
 // Lo usan ambas secciones ("Predeterminados" y "Agregados manualmente") para que
 // tengan exactamente el mismo estilo (tamaño, peso, color, márgenes).
        static Grid MakeSectionHeader(string text, SolidColorBrush accent, SolidColorBrush borderBrush)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = borderBrush, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });
            var headerText = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = accent,
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(headerText, 1);
            grid.Children.Add(headerText);
            var rightLine = new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = borderBrush, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(rightLine, 2);
            grid.Children.Add(rightLine);
            return grid;
        }

        var pickerList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Background = transparent,
            BorderThickness = new Thickness(0)
        };
        var pickerFrame = new Border
        {
            MinHeight = 260,
            Background = secondaryFill,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = pickerList
        };
        var modeTitle = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var modeDescription = new TextBlock { FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap, Opacity = 0.9 };
 // Procesos con candado (defaults del modo eficiencia) y procesos agregados
 // por el usuario comparten el MISMO ScrollViewer: los defaults son ~20 y su
 // lista fija (fuera del scroll) desbordaba la card entera — el ScrollViewer
 // quedaba reducido a su MinHeight y nunca había scroll. Los candados van
 // arriba de la lista y el ícono indica que no se pueden quitar.
        var lockedHost = new StackPanel { Spacing = 3 };
        lockedHost.Children.Add(efficiencyLockedRows);
 // Ambas secciones usan el MISMO helper de encabezado (MakeSectionHeader) para que
 // tengan estilo idéntico: línea — texto centrado — línea, FontSize 12, accent.
        var lockedSection = new StackPanel { Spacing = 6 };
        lockedSection.Children.Add(MakeSectionHeader(I18n.T("Predeterminados (no editables)"), accent, borderBrush));
        lockedSection.Children.Add(lockedHost);
        var customSection = new StackPanel { Spacing = 6 };
        customSection.Children.Add(MakeSectionHeader(I18n.T("Agregados manualmente"), accent, borderBrush));
        customSection.Children.Add(efficiencyRows);
        customSection.Children.Add(killRows);
        var targetScrollContent = new StackPanel { Spacing = 12 };
        targetScrollContent.Children.Add(lockedSection);
        targetScrollContent.Children.Add(customSection);
        var targetHost = new ScrollViewer
        {
 // Estiramiento EXPLÍCITO + sin MinHeight: el contenido debe llenar la
 // fila * del grid y el scroll aparece cuando el contenido lo supera.
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
 // Padding derecho: el scrollbar de WinUI es overlay (se dibuja ENCIMA del
 // contenido); sin este hueco tapa el borde de las filas al hacer scroll.
            Padding = new Thickness(0, 0, 10, 0),
            Content = targetScrollContent
        };
 // Pestaña "Servicios" (todos los servicios del sistema): lista virtualizada
 // con buscador, llena en background por LoadAllServicesAsync. Se crea acá para
 // que UpdateMode pueda conmutar su visibilidad; la carga arranca al entrar en
 // la pestaña por primera vez.
        // Los constructores devuelven UIElement y no todos son Border: castear a Border
        // reventaba con InvalidCastException al abrir la configuración del Modo Juego.
        // FrameworkElement sí (Border/Grid/StackPanel lo son) y es lo que piden las
        // llamadas Grid.SetRow/SetColumn de más abajo.
        FrameworkElement servicesTabCard = (FrameworkElement)BuildServicesTab();
 // Pestaña "Salud del Modo Juego": estado del Modo Juego de Windows (interruptor
 // de registro, Appx XboxGamingOverlay y GameBarPresenceWriter.dll) con la
 // reparación de un clic para el caso "solo deshabilitado".
        FrameworkElement healthTabCard = (FrameworkElement)BuildGameModeHealthTab();
 // Pestaña "Tareas": tareas programadas a pausar por partida (whitelist de
 // mantenimiento + agregadas por el usuario).
        FrameworkElement tasksTabCard = (FrameworkElement)BuildScheduledTasksTab();

 // Cabecera de la card: ícono del modo + título y descripción ordenados en un
 // bloque propio (mismo lenguaje que la cabecera de "Procesos en ejecución").
 // Antes el título y la descripción colgaban sueltos arriba de la lista y
 // competían con los encabezados de sección; con el bloque tintado la jerarquía
 // queda clara y la lista arranca después.
        var modeIcon = new FontIcon
        {
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var modeIconBadge = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Top,
            Child = modeIcon
        };
        var modeHeaderTexts = new StackPanel { Spacing = 2, Children = { modeTitle, modeDescription } };
        var modeHeaderGrid = new Grid { ColumnSpacing = 10 };
        modeHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modeHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeHeaderGrid.Children.Add(modeIconBadge);
        modeHeaderGrid.Children.Add(modeHeaderTexts);
        Grid.SetColumn(modeHeaderTexts, 1);
        var modeHeader = new Border
        {
            Background = secondaryFill,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Child = modeHeaderGrid
        };

        var targetGrid = MakeRowsGrid(2, 1, 10);
        targetGrid.Children.Add(modeHeader);
        targetGrid.Children.Add(targetHost);
        Grid.SetRow(targetHost, 1);
        var targetCard = MakeSettingsCard(targetGrid);

        var pickerGrid = MakeRowsGrid(3, 2, 10);

 // Alta MANUAL: un proceso que no está corriendo (WinRAR cerrado, la Store, un
 // juego aún sin abrir) no existe en la lista en vivo y antes no había forma de
 // configurarlo. Se escribe el nombre (con o sin .exe) y Enter lo agrega igual:
 // la regla queda guardada y se aplica cuando el proceso arranque.
 // Declarado ANTES de RefreshRunningAsync/RefreshPicker: los local functions
 // capturan esta variable y C# exige asignación definida antes de la llamada.
        var manualBox = new TextBox
        {
            PlaceholderText = I18n.T("Agregar un proceso que no está en la lista (nombre, sin .exe)"),
            FontSize = 12,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        manualBox.KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                AddProcess(manualBox.Text ?? "");
                manualBox.Text = "";
            }
        };

 // ===== Auto-actualización + botón de pausa =====
 // La lista de procesos en ejecución se refresca sola (timer de 5 s) para que
 // los procesos que se abren/cierran aparezcan sin reabrir la vista. El botón
 // de la derecha pausa/reanuda la actualización (útil para leer la lista o
 // hacer doble clic sin que las filas se muevan debajo del cursor).
        var pickerGen = ++_boostPickerGeneration;
        var pickerTimer = DispatcherQueue.CreateTimer();
        _boostPickerTimer?.Stop();
        _boostPickerTimer = pickerTimer;
        var pickerPaused = false;
        var pickerRefreshing = false;

        async Task RefreshRunningAsync()
        {
            if (pickerRefreshing) return;
            pickerRefreshing = true;
            try
            {
                var snapshot = await Task.Run(() =>
                {
                    var list = new List<string>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var process in Process.GetProcesses())
                        {
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(process.ProcessName) && seen.Add(process.ProcessName))
                                    list.Add(process.ProcessName);
                            }
                            catch { }
                            finally { process.Dispose(); }
                        }
                    }
                    catch { }
                    return list;
                });

 // La vista pudo cerrarse (o reabrirse con otra generación) mientras se
 // enumeraba: descartar el snapshot para no pisar la vista actual.
                if (pickerPaused || pickerGen != _boostPickerGeneration || BoostConfigView.Visibility != Visibility.Visible) return;
                running.Clear();
                foreach (var name in snapshot) running.Add(name);
                RefreshPicker();
            }
            finally { pickerRefreshing = false; }
        }

        var pauseIcon = new FontIcon { Glyph = "\uE769", FontSize = 12 }; // Pause
        var pauseText = new TextBlock { Text = I18n.T("Pausar"), FontSize = 12 };
        var pauseButton = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { pauseIcon, pauseText } },
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            Background = transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = borderBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        void UpdatePauseButton()
        {
            pauseIcon.Glyph = pickerPaused ? "\uE768" : "\uE769"; // Play / Pause
            pauseText.Text = I18n.T(pickerPaused ? "Reanudar" : "Pausar");
            ToolTipService.SetToolTip(pauseButton, new ToolTip
            {
                Content = new TextBlock
                {
                    Text = pickerPaused
                        ? I18n.T("Actualización automática pausada")
                        : I18n.T("La lista se actualiza automáticamente"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 260
                },
                Placement = PlacementMode.Bottom
            });
        }
        pauseButton.Click += (_, _) =>
        {
            pickerPaused = !pickerPaused;
            if (pickerPaused)
            {
                pickerTimer.Stop();
            }
            else
            {
                pickerTimer.Start();
                _ = RefreshRunningAsync();
            }
            UpdatePauseButton();
        };
        UpdatePauseButton();

 // Encabezado de la card: título/descripción (izquierda) + botón pausa (derecha).
        var pickerHeader = new Grid { ColumnSpacing = 8 };
        pickerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pickerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickerHeader.Children.Add(MakePanelHeader(I18n.T("Procesos en ejecución"), I18n.T("Doble clic para agregar a la lista del modo activo. Los ya configurados se marcan; también podés escribir un nombre y Enter.")));
        pickerHeader.Children.Add(pauseButton);
        Grid.SetColumn(pauseButton, 1);

 // Layout: encabezado (fila 0), alta manual (fila 1), lista estirada (fila 2).
        pickerGrid.Children.Add(pickerHeader);
        pickerGrid.Children.Add(manualBox);
        pickerGrid.Children.Add(pickerFrame);
        Grid.SetRow(manualBox, 1);
        Grid.SetRow(pickerFrame, 2);
        var pickerCard = MakeSettingsCard(pickerGrid);

        Grid MakePanelHeader(string title, string description)
        {
            var panel = new Grid { ColumnSpacing = 10 };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(6),
                Background = accent, Child = new FontIcon { Glyph = "\uE7FC", FontSize = 15, Foreground = accentForeground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            panel.Children.Add(icon);
            var text = new StackPanel { Spacing = 1, Children =
            {
                new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = description, FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap }
            }};
            Grid.SetColumn(text, 1);
            panel.Children.Add(text);
            return panel;
        }

        UIElement MakeProcessRow(string name, bool removable, Action? remove, string? stateBadge = null)
        {
            var row = new Grid { Height = 30, ColumnSpacing = 8, Padding = new Thickness(5, 0, 3, 0), Tag = name };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Microsoft.UI.Xaml.Controls.Image { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            EnsureProcessIcon(name, icon);
            row.Children.Add(icon);
            var label = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
 // Badge de estado (columna 2): para qué está configurado este proceso.
            if (stateBadge is not null)
            {
                var badge = MakeStateBadge(stateBadge);
                Grid.SetColumn(badge, 2);
                row.Children.Add(badge);
            }
            if (removable)
            {
                var delete = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 }, Width = 26, Height = 26, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = transparent, Foreground = Feedback.ErrorBrush };
                delete.Click += (_, _) => remove();
                Grid.SetColumn(delete, 3);
                row.Children.Add(delete);
            }
            else if (remove == null)
            {
                var lockIcon = new FontIcon { Glyph = "\uE72E", FontSize = 10, Foreground = muted, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(lockIcon, 3);
                row.Children.Add(lockIcon);
            }
            return row;
        }

 // Pastilla de estado de las filas del desplegable (misma familia que las badges
 // del desplegable de latencia): fondo de tinte del tema + texto en el color
 // fuerte, así acompaña el tema claro y el oscuro sin valores hardcodeados.
        Border MakeStateBadge(string text)
        {
            return new Border
            {
                Padding = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(8),
                Background = ThemeBrushes.Get("AccentTintBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeBrushes.Get("AccentBrush") }
            };
        }

        void RefreshPicker()
        {
 // La lista muestra TODOS los procesos en ejecución, con una badge que dice
 // dónde está cada uno ("En eficiencia" / "En cierre" / "Sistema"): antes se
 // ocultaban los ya configurados y parecía que faltaban procesos. Doble clic
 // sobre un proceso sin badge lo agrega al modo activo; los marcados no se
 // pueden duplicar (AddProcess además re-verifica).
            var desired = running
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name =>
                {
                    string? badge = null;
                    if (blocked.Contains(name)) badge = I18n.T("Sistema");
                    else if (defaults.Contains(name, StringComparer.OrdinalIgnoreCase) || efficiencyCustom.Contains(name, StringComparer.OrdinalIgnoreCase)) badge = I18n.T("En eficiencia");
                    else if (killCustom.Contains(name, StringComparer.OrdinalIgnoreCase)) badge = I18n.T("En cierre");
                    return (name, badge);
                })
                .ToList();

 // Si el resultado es idéntico al que ya está en pantalla, NO tocar la lista:
 // reconstruir Items en cada ciclo del timer (5 s) reseteaba el scroll del
 // usuario al tope, aunque nada hubiera cambiado.
            var same = pickerList.Items.Count == desired.Count;
            if (same)
            {
                for (int i = 0; i < desired.Count; i++)
                {
                    if (pickerList.Items[i] is not Grid row || row.Tag is not string tag || !string.Equals(tag, desired[i].name, StringComparison.Ordinal) || !RowBadgeMatches(row, desired[i].badge))
                    {
                        same = false;
                        break;
                    }
                }
            }

 // La badge escrita en pantalla: cada fila pinta su pastilla como UN Border
 // con TextBlock (único hijo directo así). Comparar también el texto —no solo
 // el nombre del proceso— hace que al agregar a eficiencia/cierre la badge
 // aparezca EN EL MOMENTO, aunque la lista de procesos no haya cambiado.
            bool RowBadgeMatches(Grid row, string? expected)
            {
                string? shown = null;
                foreach (var child in row.Children)
                    if (child is Border badge && badge.Child is TextBlock badgeText)
                    {
                        shown = badgeText.Text;
                        break;
                    }
                return string.Equals(shown, expected, StringComparison.Ordinal);
            }
            if (same) return;

 // Cambió algo (entró/salió un proceso o cambió una badge): reconstruir y
 // devolver el scroll a donde estaba el usuario.
            var scroll = FindDescendant<ScrollViewer>(pickerList);
            var offset = scroll?.VerticalOffset ?? 0;
            pickerList.Items.Clear();
            foreach (var (name, badge) in desired)
                pickerList.Items.Add(MakeProcessRow(name, removable: false, remove: null, stateBadge: badge));
            if (scroll is not null)
                pickerList.DispatcherQueue.TryEnqueue(() =>
                {
                    scroll.UpdateLayout(); // realiza los nuevos ítems antes de reposicionar
                    scroll.ChangeView(null, offset, null, disableAnimation: true);
                });
        }

        void RefreshTargetRows()
        {
            var rows = selectedTarget == "efficiency" ? efficiencyRows : killRows;
            var list = selectedTarget == "efficiency" ? efficiencyCustom : killCustom;
            rows.Children.Clear();
            efficiencyLockedRows.Children.Clear();

 // Los defaults del modo eficiencia (con candado) viven en su propio host
 // FUERA del ScrollViewer: quedan siempre visibles y el scroll solo cubre
 // la lista de agregados. En modo "cerrar" el host de candados se vacía
 // (y colapsa para no dejar espacio muerto).
 // La sección de candados (encabezado + filas) se muestra solo en modo
 // eficiencia; la sección de agregados siempre (sus listas se conmutan abajo).
            lockedSection.Visibility = selectedTarget == "efficiency" && defaults.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
 // Cada sección del scroll content se muestra solo en su modo: candados y
 // lista de eficiencia en "Modo eficiencia", lista de cierre en "Cerrar".
            efficiencyRows.Visibility = selectedTarget == "efficiency" ? Visibility.Visible : Visibility.Collapsed;
            killRows.Visibility = selectedTarget == "kill" ? Visibility.Visible : Visibility.Collapsed;
            if (selectedTarget == "efficiency")
            {
                foreach (var name in defaults)
                    efficiencyLockedRows.Children.Add(MakeProcessRow(name, removable: false, remove: null, stateBadge: I18n.T("En eficiencia")));
            }

            if (list.Count == 0 && selectedTarget == "efficiency" && defaults.Count > 0)
                rows.Children.Add(new TextBlock { Text = I18n.T("No hay procesos agregados. Agregá procesos desde la lista de la izquierda."), FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 6, 5, 0) });
            if (list.Count == 0 && selectedTarget == "kill")
                rows.Children.Add(new TextBlock { Text = I18n.T("No hay procesos para cerrar. Agregá procesos desde la lista de la izquierda."), FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 6, 5, 0) });
            foreach (var name in list.ToList())
            {
                var copy = name;
                rows.Children.Add(MakeProcessRow(copy, removable: true, remove: () => { list.Remove(copy); RefreshTargetRows(); RefreshPicker(); }, stateBadge: null));
            }
        }

        void AddProcess(string name)
        {
            name = name.Trim().TrimEnd('.');
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length == 0) return;
 // Alta manual: el nombre puede no existir como proceso hoy; se acepta igual
 // (la regla se aplicará cuando arranque). Duplicados y del sistema se ignoran.
            if (blocked.Contains(name) || defaults.Contains(name, StringComparer.OrdinalIgnoreCase) || efficiencyCustom.Contains(name, StringComparer.OrdinalIgnoreCase) || killCustom.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
            if (selectedTarget == "efficiency") efficiencyCustom.Add(name); else killCustom.Add(name);
            RefreshTargetRows();
            RefreshPicker();
        }

        pickerList.DoubleTapped += (_, _) => { if (pickerList.SelectedItem is FrameworkElement row && row.Tag is string name) AddProcess(name); };
 // Enter también agrega: la fila queda seleccionada con un clic y Enter confirma
 // sin necesidad de doble clic.
        pickerList.KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter && pickerList.SelectedItem is FrameworkElement krow && krow.Tag is string kname)
            {
                e.Handled = true;
                AddProcess(kname);
            }
        };

 // Selector segmentado: radio de esquina 4 en las mitades para encajar dentro
 // de la píldora (radio 6 con padding 2). Con radio 6 las esquinas quedaban
 // "cortadas" contra el borde de la píldora. Cuatro modos: eficiencia / cerrar /
 // servicios / salud del Modo Juego de Windows.
 // Cada pestaña es una pastilla propia (radio 5, con 4 px de aire entre una y
 // otra): al llevar fondo, las inactivas dejaban de leerse como un bloque
 // continuo y hacía falta separarlas. Etiqueta corta a propósito: el título
 // completo vive en el encabezado de la card.
        var efficiencyButton = new Button { Content = I18n.T("Eficiencia"), Height = 34, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var killButton = new Button { Content = I18n.T("Cerrar"), Height = 34, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var servicesButton = new Button { Content = I18n.T("Servicios"), Height = 34, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var healthButton = new Button { Content = I18n.T("Salud"), Height = 34, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var tasksButton = new Button { Content = I18n.T("Tareas"), Height = 34, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };

 // Columna derecha: card destino estirando (ocupa todo el sobrante). Se crea
 // ANTES de declarar UpdateMode y suscribir los clicks: la local function
 // captura 'right' y el compilador exige asignación definida previa.
        Grid right = new();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.Children.Add(targetCard);

 // Conmutación de vistas: procesos (picker + destino) en los dos primeros
 // modos; la pestaña de servicios ocupa toda la grilla en el tercero.
        void UpdateMode()
        {
            var efficiency = selectedTarget == "efficiency";
            var services = selectedTarget == "services";
            var health = selectedTarget == "health";
            var tasks = selectedTarget == "tasks";
 // Cada pestaña lleva SU fondo (la activa en acento y las demás con el relleno
 // de card): así el selector se lee como una barra de pestañas y no como
 // etiquetas flotando sobre el contenedor.
            void PaintTab(Button button, bool active)
            {
                button.Background = active ? accent : tabFill;
                button.Foreground = active ? accentForeground : muted;
            }
            PaintTab(efficiencyButton, efficiency);
            PaintTab(killButton, selectedTarget == "kill");
            PaintTab(servicesButton, services);
            PaintTab(healthButton, health);
            PaintTab(tasksButton, tasks);

            // Ícono del bloque de título: cambia con la pestaña activa.
            modeIcon.Glyph = efficiency ? "\uE950"
                : selectedTarget == "kill" ? "\uE711"
                : selectedTarget == "health" ? "\uE9CE"
                : tasks ? "\uE787"
                : "\uE713";
            modeIcon.Foreground = accentForeground;
            modeTitle.Text = I18n.T(efficiency ? "Modo eficiencia" : selectedTarget == "kill" ? "Cerrar al iniciar" : selectedTarget == "health" ? "Salud del Modo Juego" : tasks ? "Tareas programadas" : "Servicios");
            modeDescription.Text = efficiency
                ? I18n.T("Se baja la prioridad y se activa el modo ahorro de energía de estos procesos mientras jugás.")
                : selectedTarget == "kill"
                ? I18n.T("Agregá procesos acá. Se cierran automáticamente al lanzar un juego para liberar recursos.")
                : selectedTarget == "health"
                ? I18n.T("Verifica que el Modo Juego de Windows se va a activar correctamente al iniciar un juego (interruptor, archivos y componentes).")
                : tasks
                ? I18n.T("Tareas programadas que el boost deshabilita mientras jugás y re-habilita al cerrar el juego. El antivirus (Defender en vivo) no se toca.")
                : I18n.T("Todos los servicios de Windows con su estado real. Cambiá el modo de arranque (Desactivado / Manual / Activado); el cambio se aplica al momento.");
            var processesVisible = services || health || tasks ? Visibility.Collapsed : Visibility.Visible;
            pickerCard.Visibility = processesVisible;
            right.Visibility = processesVisible;
            servicesTabCard.Visibility = services ? Visibility.Visible : Visibility.Collapsed;
            healthTabCard.Visibility = health ? Visibility.Visible : Visibility.Collapsed;
            tasksTabCard.Visibility = tasks ? Visibility.Visible : Visibility.Collapsed;
            if (services) _ = LoadAllServicesAsync(servicesTabCard);
            if (health) LoadGameModeHealthAsync();
            RefreshTargetRows();
            RefreshPicker(); // las badges dependen del modo: re-listar al cambiar de pestaña
        }
        efficiencyButton.Click += (_, _) => { selectedTarget = "efficiency"; UpdateMode(); };
        killButton.Click += (_, _) => { selectedTarget = "kill"; UpdateMode(); };
        servicesButton.Click += (_, _) => { selectedTarget = "services"; UpdateMode(); };
        healthButton.Click += (_, _) => { selectedTarget = "health"; UpdateMode(); };
        tasksButton.Click += (_, _) => { selectedTarget = "tasks"; UpdateMode(); };

        var modeSelector = new Border { BorderBrush = borderBrush, BorderThickness = new Thickness(1), Background = secondaryFill, CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = new Grid { ColumnSpacing = 4, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition() }, Children = { efficiencyButton, killButton, servicesButton, healthButton, tasksButton } } };
        Grid.SetColumn(killButton, 1);
        Grid.SetColumn(servicesButton, 2);
        Grid.SetColumn(healthButton, 3);
        Grid.SetColumn(tasksButton, 4);

 // Layout de 2 filas: selector arriba (Auto, centrado sobre la derecha) y las
 // dos cards lado a lado estirando al * restante. Ambas cards reciben el mismo
 // alto y sus pies quedan alineados. La pestaña de servicios ocupa fila 1
 // abarcando ambas columnas (ColumnSpan 2) y solo es visible en modo "Servicios".
        var columns = new Grid { ColumnSpacing = 14, RowSpacing = 10 };
        columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        columns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(pickerCard);
        Grid.SetRow(pickerCard, 1);
        Grid.SetColumn(right, 1);
        Grid.SetRow(right, 1);
        columns.Children.Add(right);
        Grid.SetColumn(servicesTabCard, 0);
        Grid.SetColumnSpan(servicesTabCard, 2);
        Grid.SetRow(servicesTabCard, 1);
        columns.Children.Add(servicesTabCard);
        servicesTabCard.Visibility = Visibility.Collapsed;
        Grid.SetColumn(healthTabCard, 0);
        Grid.SetColumnSpan(healthTabCard, 2);
        Grid.SetRow(healthTabCard, 1);
        columns.Children.Add(healthTabCard);
        healthTabCard.Visibility = Visibility.Collapsed;
        Grid.SetColumn(tasksTabCard, 0);
        Grid.SetColumnSpan(tasksTabCard, 2);
        Grid.SetRow(tasksTabCard, 1);
        columns.Children.Add(tasksTabCard);
        tasksTabCard.Visibility = Visibility.Collapsed;
 // Selector centrado horizontalmente sobre la columna derecha (los tres
 // opciones del modo). Fila 0, columna 1.
        Grid.SetColumn(modeSelector, 1);
        Grid.SetRow(modeSelector, 0);
 // Centrado vertical: la fila es más alta que la píldora (a su izquierda van dos
 // switches), y sin esto la píldora se estiraría y los botones quedarían flotando.
        modeSelector.VerticalAlignment = VerticalAlignment.Center;
        columns.Children.Add(modeSelector);

 // Los DOS switches del boost van a la IZQUIERDA de las pestañas (fila 0, columna 0):
 // son ajustes del boost completo, no de una pestaña, y así se ven los dos a la vez
 // sin tener que ir y volver entre "Servicios" y "Tareas". Cada pestaña construye su
 // switch (con sus handlers) y acá solo se lo coloca junto al otro.
 // Apilados en vertical y no en horizontal: los dos textos juntos (~530 px) no entran
 // en media card en una ventana chica y el segundo switch se recortaba.
 // Cada switch en su propia píldora con fondo y borde: son dos ajustes del
 // boost que viven en la cabecera del selector y, sueltos sobre el fondo de la
 // página, se perdían. La píldora los separa del selector y entre sí.
        Border MakeSwitchCard(UIElement row) => new()
        {
            Background = secondaryFill,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 10, 4),
            Child = row
        };
        var headerSwitches = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { MakeSwitchCard(_servicesAutoOptRow!), MakeSwitchCard(_tasksAutoPauseRow!) }
        };
        Grid.SetColumn(headerSwitches, 0);
        Grid.SetRow(headerSwitches, 0);
        columns.Children.Add(headerSwitches);
        RefreshPicker();
        UpdateMode();

 // Auto-actualización de la lista de procesos: primer refresh a los 5 s y
 // luego periódico (el snapshot inicial ya se tomó arriba, al construir).
        pickerTimer.Interval = TimeSpan.FromSeconds(5);
        pickerTimer.Tick += (_, _) => _ = RefreshRunningAsync();
        pickerTimer.Start();

        return columns;
    }

 // ===================== Pestaña Servicios (todos los del sistema) =====================

 // Servicios que la UI NO deja cambiar (arrancarlos con Windows o desactivarlos
 // puede dejar el sistema inestable o sin sesión). sc config igual fallaría en
 // varios; el candado evita el intento y el susto.
    private static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "DcomLaunch", "Winmgmt", "Schedule", "EventLog", "gpsvc",
        "ProfSvc", "SamSs", "KeyIso", "Netlogon", "LanmanServer", "LanmanWorkstation",
        "nsi", "Dhcp", "Dnscache", "Themes", "Audiosrv", "AudioEndpointBuilder",
        "BrokerInfrastructure", "StateRepository", "UserManager", "CryptSvc",
        "msiserver", "BFE", "mpssvc",        "AppInfo", "DeviceInstall", "PlugPlay",
        "Power", "winmgmt"
    };

 // Caché del snapshot de servicios: se consulta WMI UNA vez por apertura de
 // la vista (la lista es larga y no cambia mientras se mira). Los cambios
 // hechos en esta sesión se reflejan en la fila (el selector vuelve con el
 // estado real).
    private List<SystemServiceInfo>? _allServicesCache;
    private string _servicesFilter = "";
 // Alcance del desplegable de la pestaña Servicios: true = solo los servicios
 // recomendados para gaming (GameBoostService.ManagedServices), false = todos.
 // "Recomendados" es el default al entrar a la pestaña; se resetea al cerrar.
    private bool _servicesRecommendedOnly = true;
    // Ítems del desplegable de alcance de la pestaña Servicios (los arma BuildServicesPanel):
    // el Tag guarda la clave fuente para re-traducirlos al cambiar de idioma. El popup no
    // está en el árbol visual, así que el recorrido de I18n no los alcanza.
    private ComboBoxItem[]? _servicesScopeItems;
 // Referencias directas a la lista y el contador de la pestaña de servicios
 // (evita buscar descendientes en el árbol visual, que podía agarrar el
 // TextBlock interno del TextBox de búsqueda).
    private ListView? _servicesListView;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _servicesSearchDebounce;
 // Generación de refresco manual: se incrementa al presionar "Refrescar" (y al
 // cerrar la vista); la consulta en background se descarta si una más nueva
 // empezó. Evita que un snapshot viejo pise uno nuevo al presionar dos veces.
    private int _servicesRefreshGeneration;
 // Switch "Servicios Optimizados Automaticos" de la pestaña Servicios: cuando
 // está activo, los selectores de las filas se bloquean (la optimización
 // hardcodeada del boost manda); apagado, el usuario edita libremente.
    private Microsoft.UI.Xaml.Controls.ToggleSwitch? _servicesAutoOptSwitch;

 // Fila del switch "Optimización automática activa" (pestaña Servicios): la construye
 // la pestaña, pero la COLOCA el armador de la card en la cabecera, a la izquierda de
 // las pestañas, para que los dos switches del boost se vean juntos sin cambiar de
 // pestaña. El switch y su handler siguen siendo de la pestaña (operan sobre su lista).
    private UIElement? _servicesAutoOptRow;

 // Fila del switch "Pausa de tareas activa" (pestaña Tareas): mismo trato que el anterior.
    private UIElement? _tasksAutoPauseRow;

 // El switch de optimización automática está activo (default true).
    private bool ServicesAutoOptimizationOn => _servicesAutoOptSwitch?.IsOn ?? true;

 /// <summary>
 /// Construye la card de la pestaña "Servicios": buscador + lista virtualizada
 /// de TODOS los servicios de Windows. Cada fila: punto de estado, nombre
 /// técnico + nombre para mostrar, candado (si es crítico) y selector de 3
 /// estados que aplica <c>sc config</c> al momento. La carga de datos es en
 /// background (LoadAllServicesAsync) para no congelar la UI.
 /// </summary>
    private UIElement BuildServicesTab()
    {
        var muted = Feedback.MutedBrush;
        var borderBrush = ThemeBrushes.Get("CardBorderBrush");
        var secondaryFill = ThemeBrushes.Get("CardBackgroundFillColorSecondaryBrush");

 // Lista de servicios: las filas se fabrican en C# (MakeSystemServiceRow) y se
 // agregan DIRECTAMENTE como items — el mismo patrón de pickerList de procesos
 // (Items.Add de UIElement), que ya funciona en esta página. Se descartó el
 // ItemsSource + PrepareContainerForItemOverride: WinUI ignora el Content que
 // se asigna ahí y presenta el item con ToString(), por lo que se veía el
 // nombre del tipo ("WHPO.UI...ServiceRow") repetido en vez de las filas.
        var servicesList = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0)
        };
        _servicesListView = servicesList;

        var searchBox = new TextBox
        {
            PlaceholderText = I18n.T("Buscar servicio..."),
            FontSize = 12,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
 // Desplegable de alcance: "Recomendados" (servicios típicos de tweaks de
 // gaming: update, telemetría, Hyper-V, Xbox, impresión, legacy — la misma
 // lista curada que usa el boost/card de servicios) o "Todos". Default: Recomendados.
        var scopeCombo = new ComboBox
        {
            MinWidth = 136,
            FontSize = 12,
            Height = 32,
            CornerRadius = new CornerRadius(6)
        };
        var recommendedItem = new ComboBoxItem { Content = I18n.T("Recomendados"), Tag = "Recomendados" };
        var allServicesItem = new ComboBoxItem { Content = I18n.T("Todos"), Tag = "Todos" };
        scopeCombo.Items.Add(recommendedItem);
        scopeCombo.Items.Add(allServicesItem);
        _servicesScopeItems = new[] { recommendedItem, allServicesItem };
        scopeCombo.SelectedIndex = 0;
        scopeCombo.SelectionChanged += (_, _) =>
        {
            _servicesRecommendedOnly = scopeCombo.SelectedIndex == 0;
            RefillServicesList(servicesList);
        };

 // Botón "Actualizar": vuelve a consultar Windows (invalida el snapshot cacheado
 // de WMI) y rellena la lista con los estados reales del momento. Sirve para
 // servicios instalados/detenido/arrancados fuera de la app sin reabrir la vista.
 // Mismo tamaño/estilo que el desplegable de alcance (32px, fuente 12, radio 6,
 // mismo borde y fondo) para que la fila quede simétrica.
        var refreshIcon = new FontIcon { Glyph = "\uE72C", FontSize = 12 }; // Refresh
        var refreshText = new TextBlock { Text = I18n.T("Actualizar"), FontSize = 12 };
        var refreshButton = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { refreshIcon, refreshText } },
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            Background = secondaryFill,
            BorderThickness = new Thickness(1),
            BorderBrush = borderBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(refreshButton, new ToolTip
        {
            Content = new TextBlock
            {
                Text = I18n.T("Vuelve a consultar los servicios de Windows con sus estados reales."),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260
            },
            Placement = PlacementMode.Bottom
        });
        refreshButton.Click += (_, _) =>
        {
            _servicesRefreshGeneration++;
            _allServicesCache = null;
            _ = LoadAllServicesAsync(servicesList);
        };

        var searchRow = new Grid { ColumnSpacing = 10 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.Children.Add(searchBox);
        searchRow.Children.Add(scopeCombo);
        searchRow.Children.Add(refreshButton);
        Grid.SetColumn(scopeCombo, 1);
        Grid.SetColumn(refreshButton, 2);

 // ===== Switch "Servicios Optimizados Automaticos" =====
 // Activo por defecto: el boost detiene temporalmente los servicios del
 // optimizador (Windows Update, BITS, SysMain...) al lanzar un juego y los
 // restaura al cerrarlo. Apagado: el boost no toca servicios por partida y los
 // selectores de las filas de abajo se bloquean para que la configuración
 // automática no se mezcle con cambios manuales.
        var autoOptSwitch = new ToggleSwitch
        {
            IsOn = _gameBoostService.IsAutomaticServiceOptimizationEnabled,
            OnContent = I18n.T("Optimización automática activa"),
            OffContent = I18n.T("Sin optimización automática"),
            Margin = new Thickness(0, 2, 0, 0)
        };
        var autoOptInfoButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE946", FontSize = 11 },
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        var autoOptRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { autoOptSwitch, autoOptInfoButton }
        };
 // El contenido se arma al ABRIR el tooltip y sale de la lista real que detiene el boost
 // (GameBoostService.BoostStopServices), con el nombre para mostrar que informa Windows
 // (que ya viene localizado): así el tooltip no queda desactualizado cuando se agrega o se
 // quita un servicio del optimizador.
        var autoOptTip = new ToolTip { Placement = PlacementMode.Bottom };
        autoOptTip.Opened += (s, _) =>
        {
            if (s is ToolTip tip) tip.Content = BuildAutoOptTooltip();
        };
        ToolTipService.SetToolTip(autoOptInfoButton, autoOptTip);
        autoOptSwitch.Toggled += (_, _) =>
        {
            _gameBoostService.IsAutomaticServiceOptimizationEnabled = autoOptSwitch.IsOn;
            RefillServicesList(servicesList);
        };

 // Filtrado local (en memoria, instantáneo): por nombre técnico o nombre para
 // mostrar; el texto del propio TextBox no se pisa (el walker de I18n recuerda
 // el PlaceholderText original).
 // Debounce de 200 ms: reescribir ~400 filas por tecla se siente pesado al
 // filtrar. Cada TextChanged reinicia el timer; el refill corre cuando el
 // usuario se detiene (se detiene también al cerrar la vista).
        searchBox.TextChanged += (_, _) =>
        {
            _servicesFilter = searchBox.Text ?? "";
            _servicesSearchDebounce?.Stop();
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(200);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => RefillServicesList(servicesList);
            _servicesSearchDebounce = timer;
            timer.Start();
        };
 // servicesTabCard es null mientras BuildServicesTab construye la card (el
 // botón se crea antes); en runtime el Click siempre la recibe completa.
 // 2 filas: buscador (Auto) + lista de servicios (estira y scrollea con
 // virtualización). El switch de optimización automática ya no vive acá: se arma
 // arriba y lo coloca el armador de la card, a la izquierda de las pestañas.
        var grid = new Grid { RowSpacing = 8 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(searchRow);
        grid.Children.Add(servicesList);
        Grid.SetRow(servicesList, 1);
 // Referencia para bloquear/desbloquear los selectores de las filas según el
 // estado del switch de optimización automática.
        _servicesAutoOptRow = autoOptRow;
        _servicesAutoOptSwitch = autoOptSwitch;
        var card = MakeSettingsCard(grid);
        ToolTipService.SetToolTip(searchBox, new ToolTip
        {
            Content = new TextBlock
            {
                Text = I18n.T("Filtrá por nombre técnico o nombre para mostrar. El desplegable separa los servicios recomendados para gaming del resto."),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 280
            },
            Placement = PlacementMode.Bottom
        });
        return card;


        void RefillServicesList(ListView list)
        {
            var snapshot = _allServicesCache;
            if (snapshot is null) return;
            var filter = _servicesFilter.Trim();
 // Alcance primero (Recomendados/Todos) y después el texto del buscador. El
 // total del contador es el del alcance: "3 de 30 servicios" (recomendados)
 // o "3 de 412" (todos).
            IEnumerable<SystemServiceInfo> query = snapshot;
            if (_servicesRecommendedOnly)
            {
                var recommended = GameBoostService.ManagedServices;
                query = query.Where(s => recommended.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
            }
            if (filter.Length > 0)
            {
                query = query.Where(s =>
                    s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }
 // Orden estable: corriendo primero no (ruido al cambiar); alfabético por
 // nombre para mostrar y, a igualdad, por nombre técnico.
            var items = query
                .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
 // Filas UI directas: Items.Add (patrón pickerList). Con ItemsSource WinUI
 // presentaba ServiceRow.ToString() (el nombre del tipo) en vez de la fila.
            list.Items.Clear();
            foreach (var info in items)
                list.Items.Add(MakeSystemServiceRow(new ServiceRow(info)));
        }
    }

 /// <summary>
 /// Contenido del tooltip del switch de optimización automática: los servicios que el boost
 /// detiene por partida, uno por línea y en el orden de la lista real, más qué implica
 /// apagarlo. Se arma en cada apertura y reutiliza el nombre para mostrar que ya trajo el
 /// snapshot de servicios de Windows (si todavía no llegó, cae al nombre técnico).
 /// </summary>
    private UIElement BuildAutoOptTooltip()
    {
        var list = new StackPanel { Spacing = 3 };
        foreach (var service in GameBoostService.BoostStopServices)
        {
            var display = _allServicesCache?
                .FirstOrDefault(s => string.Equals(s.Name, service, StringComparison.OrdinalIgnoreCase))?.DisplayName;
            list.Children.Add(new TextBlock
            {
                Text = "· " + (string.IsNullOrWhiteSpace(display) ? service : $"{display} ({service})"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var header = new TextBlock
        {
            Text = I18n.T("Al lanzar un juego se detienen temporalmente estos servicios y se restauran al cerrarlo:"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var note = new TextBlock
        {
            Text = I18n.T("Con esto apagado, el boost solo gestiona procesos y valen los estados que configuraste a mano."),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        };

        return new StackPanel
        {
            Spacing = 8,
            MaxWidth = 320,
            Children = { header, list, note }
        };
    }

 /// <summary>Item ligero para la lista virtualizada de servicios.</summary>
    private sealed class ServiceRow
    {
        public SystemServiceInfo Info { get; }
        public ServiceRow(SystemServiceInfo info) => Info = info;
    }

 // ===================== Pestaña Tareas programadas (boost por partida) =====================

 // Caché del snapshot de tareas del sistema: una consulta de schtasks por
 // apertura de la pestaña (y por "Actualizar"). Formato CSV: "path","próxima
 // ejecución","estado" — el estado viene localizado, así que solo se muestra.
    private List<(string Path, string Status)>? _allTasksCache;
    private string _tasksFilter = "";
    private bool _tasksWhitelistOnly = true;
    // Ítems del desplegable de alcance de la pestaña Tareas (mismo motivo que
    // _servicesScopeItems): el Tag guarda la clave fuente.
    private ComboBoxItem[]? _tasksScopeItems;
    private ListView? _tasksListView;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tasksSearchDebounce;
    private int _tasksRefreshGeneration;

 /// <summary>
 /// Pestaña "Tareas" de la configuración del boost: lista de TODAS las tareas
 /// programadas (schtasks /Query) para agregar a la pausa por partida + la
 /// whitelist de mantenimiento (con candado) y las agregadas por el usuario.
 /// El switch "Pausar tareas programadas" vive ACÁ: es la pausa de las TAREAS, no la
 /// de los servicios — tenerlo en la pestaña Servicios hacía que esa card prometiera
 /// una configuración que estaba en otra pestaña.
 /// </summary>
    private UIElement BuildScheduledTasksTab()
    {
        var muted = Feedback.MutedBrush;
        var accent = ThemeBrushes.Get("AccentBrush");
        var borderBrush = ThemeBrushes.Get("CardBorderBrush");
        var secondaryFill = ThemeBrushes.Get("CardBackgroundFillColorSecondaryBrush");
        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        var taskList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Background = transparent,
            BorderThickness = new Thickness(0)
        };
        _tasksListView = taskList;

        var searchBox = new TextBox
        {
            PlaceholderText = I18n.T("Buscar tarea..."),
            FontSize = 12,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var scopeCombo = new ComboBox
        {
            MinWidth = 136,
            FontSize = 12,
            Height = 32,
            CornerRadius = new CornerRadius(6)
        };
        var whitelistItem = new ComboBoxItem { Content = I18n.T("Whitelist"), Tag = "Whitelist" };
        var allTasksItem = new ComboBoxItem { Content = I18n.T("Todas"), Tag = "Todas" };
        scopeCombo.Items.Add(whitelistItem);
        scopeCombo.Items.Add(allTasksItem);
        _tasksScopeItems = new[] { whitelistItem, allTasksItem };
        scopeCombo.SelectedIndex = 0;
        scopeCombo.SelectionChanged += (_, _) =>
        {
            _tasksWhitelistOnly = scopeCombo.SelectedIndex == 0;
            RefillTasksList(taskList);
        };

        var refreshButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE72C", FontSize = 12 },
                    new TextBlock { Text = I18n.T("Actualizar"), FontSize = 12 }
                }
            },
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            Background = secondaryFill,
            BorderThickness = new Thickness(1),
            BorderBrush = borderBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        refreshButton.Click += (_, _) =>
        {
            _tasksRefreshGeneration++;
            _allTasksCache = null;
            _ = LoadAllTasksAsync();
        };

        var searchRow = new Grid { ColumnSpacing = 10 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.Children.Add(searchBox);
        searchRow.Children.Add(scopeCombo);
        searchRow.Children.Add(refreshButton);
        Grid.SetColumn(scopeCombo, 1);
        Grid.SetColumn(refreshButton, 2);

 // Filtrado local instantáneo con debounce (misma mecánica que la pestaña
 // Servicios: reescribir cientos de filas por tecla se siente pesado).
        searchBox.TextChanged += (_, _) =>
        {
            _tasksFilter = searchBox.Text ?? "";
            _tasksSearchDebounce?.Stop();
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(200);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => RefillTasksList(taskList);
            _tasksSearchDebounce = timer;
            timer.Start();
        };

 // Columna izquierda: selector de tareas (buscador + lista virtualizada).
        var pickerGrid = new Grid { RowSpacing = 8 };
        pickerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pickerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pickerGrid.Children.Add(searchRow);
        pickerGrid.Children.Add(taskList);
        Grid.SetRow(taskList, 1);
        var pickerCard = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = secondaryFill,
            Padding = new Thickness(12),
            Child = pickerGrid
        };

 // Columna derecha: whitelist (candado) + agregadas (borrables) + alta manual.
        var lockedHost = new StackPanel { Spacing = 3 };
        var customHost = new StackPanel { Spacing = 3 };
        var statusText = new TextBlock
        {
            FontSize = 11,
            Foreground = Feedback.ErrorBrush,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var manualBox = new TextBox
        {
            PlaceholderText = I18n.T("\\Carpeta\\Tarea de la tarea a pausar"),
            FontSize = 12,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var addButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon { Glyph = "\uE710", FontSize = 12 },
                    new TextBlock { Text = I18n.T("Agregar tarea"), FontSize = 12 }
                }
            },
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            Background = secondaryFill,
            BorderThickness = new Thickness(1),
            BorderBrush = borderBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var manualRow = new Grid { ColumnSpacing = 10 };
        manualRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        manualRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        manualRow.Children.Add(manualBox);
        manualRow.Children.Add(addButton);
        Grid.SetColumn(addButton, 1);

        var scrollContent = new StackPanel { Spacing = 12 };
        scrollContent.Children.Add(statusText);
        scrollContent.Children.Add(MakeSectionHeader(I18n.T("Predeterminados (no editables)"), accent, borderBrush));
        scrollContent.Children.Add(lockedHost);
        scrollContent.Children.Add(MakeSectionHeader(I18n.T("Agregadas manualmente"), accent, borderBrush));
        scrollContent.Children.Add(manualRow);
        scrollContent.Children.Add(customHost);
        var taskScroll = new ScrollViewer
        {
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 10, 0),
            Content = scrollContent
        };

        var title = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Text = I18n.T("Tareas programadas") };
        var description = new TextBlock
        {
            FontSize = 11,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.9,
            Text = I18n.T("Estas tareas se deshabilitan mientras jugás y vuelven solas al cerrar el juego. Las de la whitelist son las de mantenimiento de Windows (escaneos de update y de Defender, defrag, diagnóstico); podés agregar las que quieras con el buscador o a mano.")
        };

 // ===== Switch "Pausar tareas programadas" =====
 // Activo por defecto: el boost deshabilita temporalmente las tareas de mantenimiento
 // (defrag, escaneos de update y de Defender, diagnóstico, telemetría) durante la partida
 // y las re-habilita al cerrar. Apagado: no se toca ninguna tarea ni el Mantenimiento
 // automático de Windows.
        var taskPauseSwitch = new ToggleSwitch
        {
            IsOn = _gameBoostService.IsAutomaticTaskPauseEnabled,
            OnContent = I18n.T("Pausa de tareas activa"),
            OffContent = I18n.T("Sin pausa de tareas"),
            Margin = new Thickness(0, 2, 0, 0)
        };
        var taskPauseInfoButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE946", FontSize = 11 },
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        var taskPauseRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { taskPauseSwitch, taskPauseInfoButton }
        };
        ToolTipService.SetToolTip(taskPauseInfoButton, new ToolTip
        {
            Content = new TextBlock
            {
                Text = I18n.T("El boost deshabilita temporalmente estas tareas mientras jugás y las re-habilita al cerrar el juego: escaneos de Windows Update, escaneos programados de Defender (el antivirus sigue activo), defrag/TRIM, diagnóstico y telemetría. Nada afecta a la captura de pantalla ni al audio: no interfiere si compartís la pantalla."),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320
            },
            Placement = PlacementMode.Bottom
        });
        taskPauseSwitch.Toggled += (_, _) =>
        {
            _gameBoostService.IsAutomaticTaskPauseEnabled = taskPauseSwitch.IsOn;
        };

 // La fila viaja a la cabecera de la card (ver el armador), junto al switch de
 // servicios: los dos ajustes del boost se ven juntos, sin cambiar de pestaña.
        _tasksAutoPauseRow = taskPauseRow;

        var taskGrid = new Grid { RowSpacing = 8 };
        taskGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        taskGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        taskGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        taskGrid.Children.Add(title);
        taskGrid.Children.Add(description);
        Grid.SetRow(description, 1);
        taskGrid.Children.Add(taskScroll);
        Grid.SetRow(taskScroll, 2);
        var taskCard = new Border
        {
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = secondaryFill,
            Padding = new Thickness(12),
            Child = taskGrid
        };

        Grid MakeSectionHeader(string text, SolidColorBrush accentBrush, SolidColorBrush lineBrush)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = lineBrush, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });
            var headerText = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = accentBrush,
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(headerText, 1);
            grid.Children.Add(headerText);
            var rightLine = new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = lineBrush, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(rightLine, 2);
            grid.Children.Add(rightLine);
            return grid;
        }

        UIElement MakeTaskRow(string path, bool locked, Action? remove)
        {
            var row = new Grid { Height = 28, ColumnSpacing = 8, Padding = new Thickness(5, 0, 3, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FontIcon
            {
                Glyph = locked ? "\uE72E" : "\uE9D9",  // candado / reloj
                FontSize = 11,
                Foreground = locked ? muted : accent,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(icon);
            var label = new TextBlock
            {
                Text = path,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = locked ? 0.75 : 1
            };
            if (locked) label.Foreground = muted;
            ToolTipService.SetToolTip(label, path);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            if (remove != null)
            {
                var delete = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 10 },
                    Width = 26,
                    Height = 26,
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    Background = transparent,
                    Foreground = Feedback.ErrorBrush
                };
                delete.Click += (_, _) => remove();
                Grid.SetColumn(delete, 2);
                row.Children.Add(delete);
            }
            return row;
        }

        void ShowStatus(string message)
        {
            statusText.Text = message;
            statusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        void RefreshTaskRows()
        {
            var defaultsList = _gameBoostService.GetDefaultPauseTaskPaths();
            var customList = _gameBoostService.GetPauseTaskPaths()
                .Where(p => !defaultsList.Contains(p, StringComparer.OrdinalIgnoreCase))
                .ToList();

            lockedHost.Children.Clear();
            foreach (var path in defaultsList)
                lockedHost.Children.Add(MakeTaskRow(path, locked: true, remove: null));

            customHost.Children.Clear();
            if (customList.Count == 0)
            {
                customHost.Children.Add(new TextBlock
                {
                    Text = I18n.T("No hay tareas agregadas. Agregá tareas desde la lista de la izquierda o escribí su ruta."),
                    FontSize = 11,
                    Foreground = muted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(5, 6, 5, 0)
                });
            }
            foreach (var path in customList.ToList())
            {
                var copy = path;
                customHost.Children.Add(MakeTaskRow(copy, locked: false, remove: () =>
                {
                    customList.Remove(copy);
                    _gameBoostService.SetPauseTaskPaths(customList);
                    RefreshTaskRows();
                    RefillTasksList(taskList);
                }));
            }
        }

        void AddTask(string rawPath)
        {
            var clean = rawPath.Trim().Trim('"').Replace('/', '\\');
            if (clean.Length > 0 && !clean.StartsWith("\\", StringComparison.Ordinal)) clean = "\\" + clean;
            clean = clean.TrimEnd('\\');
            if (clean.Count(c => c == '\\') < 2)
            {
                ShowStatus(I18n.T("Ruta de tarea inválida: usá el formato \\Carpeta\\Tarea."));
                return;
            }
            var defaultsList = _gameBoostService.GetDefaultPauseTaskPaths();
            if (defaultsList.Contains(clean, StringComparer.OrdinalIgnoreCase))
            {
                ShowStatus(I18n.T("Ya está en la whitelist de mantenimiento."));
                return;
            }
            var customList = _gameBoostService.GetPauseTaskPaths()
                .Where(p => !defaultsList.Contains(p, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (customList.Contains(clean, StringComparer.OrdinalIgnoreCase))
            {
                ShowStatus(I18n.T("La tarea ya estaba agregada."));
                return;
            }
            customList.Add(clean);
            _gameBoostService.SetPauseTaskPaths(customList);
            ShowStatus("");
            manualBox.Text = "";
            RefreshTaskRows();
            RefillTasksList(taskList);
        }

        addButton.Click += (_, _) => AddTask(manualBox.Text ?? "");
        manualBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) AddTask(manualBox.Text ?? "");
        };
        taskList.DoubleTapped += (_, _) =>
        {
            if (taskList.SelectedItem is FrameworkElement row && row.Tag is string path)
                AddTask(path);
        };

        var columns = new Grid { ColumnSpacing = 14 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(pickerCard);
        columns.Children.Add(taskCard);
        Grid.SetColumn(taskCard, 1);

        RefillTasksList(taskList);
        RefreshTaskRows();
        _ = LoadAllTasksAsync();
        return columns;

        void RefillTasksList(ListView list)
        {
            var snapshot = _allTasksCache;
            if (snapshot is null) return;
            var filter = _tasksFilter.Trim();
            IEnumerable<(string Path, string Status)> query = snapshot;
            if (_tasksWhitelistOnly)
            {
                var whitelist = _gameBoostService.GetPauseTaskPaths();
                var exact = new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);
                query = query.Where(t => exact.Contains(t.Path));
            }
            if (filter.Length > 0)
                query = query.Where(t => t.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));
            var items = query.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
            list.Items.Clear();
            foreach (var t in items)
            {
                var row = new Grid { Height = 28, ColumnSpacing = 8, Padding = new Thickness(5, 0, 3, 0), Tag = t.Path };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var label = new TextBlock
                {
                    Text = t.Path,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                ToolTipService.SetToolTip(row, $"{t.Path}\n{t.Status}");
                row.Children.Add(label);
                list.Items.Add(row);
            }
        }

        async Task LoadAllTasksAsync()
        {
            var generationAtStart = _tasksRefreshGeneration;
            var snapshot = await Task.Run(QueryAllTasks);
            if (BoostConfigView.Visibility != Visibility.Visible) return;
            if (generationAtStart != _tasksRefreshGeneration) return;
            _allTasksCache = snapshot;
            if (_tasksListView != null) RefillTasksList(_tasksListView);
        }
    }

 /// <summary>
 /// Consulta schtasks /Query /FO CSV /NH y parsea (ruta, estado) de cada tarea.
 /// El CSV puede traer campos con comillas: el parser respeta comillas. Si el
 /// formato cambia o falla, devuelve lo que pueda (lista vacía = selector vacío).
 /// </summary>
    private static List<(string Path, string Status)> QueryAllTasks()
    {
        var result = new List<(string, string)>(300);
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Query /FO CSV /NH")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p == null) return result;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0) continue;
                var fields = SplitCsvLine(line);
                if (fields.Count == 0) continue;
                var path = fields[0].Trim();
                if (!path.StartsWith("\\", StringComparison.Ordinal)) continue;
                var status = fields.Count > 2 ? fields[^1].Trim() : "";
                result.Add((path, status));
            }
        }
        catch { }
        return result;

        static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else current.Append(c);
            }
            fields.Add(current.ToString());
            return fields;
        }
    }

 /// <summary>
 /// Carga el snapshot de todos los servicios en background y llena la lista.
 /// Con la vista cerrada o reabierta mientras se consulta, el resultado se
 /// descarta (mismo patrón de generación que la lista de procesos).
 /// </summary>
    private async Task LoadAllServicesAsync(UIElement card)
    {
        var generationAtStart = _servicesRefreshGeneration;
        if (_allServicesCache is null)
        {
            var snapshot = await Task.Run(() => _gameBoostService.GetAllServicesSnapshot());

 // La vista pudo cerrarse mientras se consultaba (el Content del host se
 // limpia al cerrar): si la card ya no está en el árbol, descartar. Si el
 // usuario presionó Refrescar de nuevo mientras se consultaba, el snapshot
 // es viejo: descartarlo también (una consulta más nueva ya arrancó).
            if (BoostConfigView.Visibility != Visibility.Visible) return;
            if (generationAtStart != _servicesRefreshGeneration) return;

            _allServicesCache = snapshot;
        } // Re-pintar con el snapshot en caché (y el filtro vigente). Referencias
 // directas a lista y contador (FindDescendant podía agarrar el TextBlock
 // interno del TextBox y pintaba el contador en el lugar equivocado).
        if (_servicesListView is null) return;

 // Re-pintar la lista (la primera vez estaba vacía; luego re-filtra).
        var filter = _servicesFilter.Trim();
        IEnumerable<SystemServiceInfo> query = _allServicesCache;
        if (_servicesRecommendedOnly)
        {
            var recommended = GameBoostService.ManagedServices;
            query = query.Where(s => recommended.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
        }
        if (filter.Length > 0)
        {
            query = query.Where(s =>
                s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        var items = query
            .OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _servicesListView.Items.Clear();
        foreach (var info in items)
            _servicesListView.Items.Add(MakeSystemServiceRow(new ServiceRow(info)));
    }

 /// <summary>Primer descendiente de tipo T en el árbol visual (o null).</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var deep = FindDescendant<T>(child);
            if (deep != null) return deep;
        }
        return null;
    }

 /// <summary>Fila de la lista de servicios: punto + nombre + candado + selector.</summary>
    private UIElement MakeSystemServiceRow(ServiceRow item)
    {
        var s = item.Info;
        var muted = Feedback.MutedBrush;
        var critical = CriticalServices.Contains(s.Name);
 // StartMode Boot/System: sc config no puede cambiarlos a auto/disabled de
 // forma útil; se muestran como Activado bloqueado.
        var fixedAuto = s.StartMode is "Boot" or "System";

        var row = new Grid { Height = 36, ColumnSpacing = 8, Padding = new Thickness(8, 0, 6, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // punto
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // nombre
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // selector
 // Punto de estado en vivo: verde = corriendo, gris = detenido.
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = s.IsRunning ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 175, 80))
                               : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128))
        };
        ToolTipService.SetToolTip(dot, s.IsRunning ? I18n.T("En ejecución") : I18n.T("Detenido"));
        row.Children.Add(dot);
 // Nombre tal como aparece en services.msc (DisplayName) como etiqueta
 // principal + nombre técnico del SCM como referencia secundaria. Algunos
 // servicios tienen DisplayName vacío o igual al técnico: caen al nombre
 // técnico y no se muestra dos veces lo mismo.
        var primaryName = string.IsNullOrWhiteSpace(s.DisplayName) ? s.Name : s.DisplayName!;
 // Texto "apagado": los nombres de servicios van en el gris secundario del tema
 // (así la lista se lee en reposo y resalta solo el estado y los controles).
        var serviceNameBrush = ThemeBrushes.Get("SecondaryTextBrush");
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        namePanel.Children.Add(new TextBlock
        {
            Text = primaryName,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = serviceNameBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 340
        });
        if (!string.Equals(primaryName, s.Name, StringComparison.Ordinal))
            namePanel.Children.Add(new TextBlock
            {
                Text = s.Name,
                FontSize = 11,
                Foreground = muted,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 220
            });
 // Botón (i) al lado del nombre: muestra la descripción del servicio en un
 // tooltip. La descripción la provee el SO (en el idioma del sistema) y las
 // etiquetas alrededor usan I18n.T (idioma de la app); como toda la vista se
 // reconstruye al abrir la configuración, siempre se arma con el idioma vigente.
        var infoButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE946", FontSize = 11 },
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        namePanel.Children.Add(infoButton);
        if (critical || fixedAuto)
        {
            namePanel.Children.Add(new FontIcon { Glyph = "\uE72E", FontSize = 10, Foreground = muted, VerticalAlignment = VerticalAlignment.Center });
        }
        Grid.SetColumn(namePanel, 1);
        row.Children.Add(namePanel);
 // Con "Servicios Optimizados Automaticos" activo, el boost gestiona los
 // servicios del optimizador por partida: los selectores quedan bloqueados
 // para que la configuración automática no se mezcle con cambios manuales.
        var combo = new ComboBox
        {
            MinWidth = 118,
            FontSize = 12,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            IsEnabled = !critical && !fixedAuto && !ServicesAutoOptimizationOn,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        foreach (var st in new[] { I18n.T("Desactivado"), I18n.T("Manual"), I18n.T("Activado") })
            combo.Items.Add(st);
 // Estado actual real (GetAllServicesSnapshot ya trae StartMode).
        var currentState = s.StartMode switch
        {
            "Disabled" => ServiceStartState.Disabled,
            "Manual" => ServiceStartState.Manual,
            "Auto" => ServiceStartState.Auto,
            "Boot" or "System" => ServiceStartState.Auto,
            _ => (ServiceStartState?)null
        };
        combo.SelectedIndex = currentState switch
        {
            ServiceStartState.Disabled => 0,
            ServiceStartState.Manual => 1,
            ServiceStartState.Auto => 2,
            _ => -1
        };
        combo.SelectionChanged += async (_, _) =>
        {
            if (!combo.IsEnabled || combo.SelectedIndex < 0) return;
            var chosen = (ServiceStartState)combo.SelectedIndex;
            var applied = await Task.Run(() => _gameBoostService.SetServiceStartState(s.Name, chosen));
 // Si Windows no pudo aplicar (permisos, dependencias), volver al estado real.
            if (applied != chosen)
            {
                combo.SelectedIndex = applied switch
                {
                    ServiceStartState.Disabled => 0,
                    ServiceStartState.Manual => 1,
                    _ => 2
                };
                _loggingService.LogError($"GameBoost: no se pudo cambiar el estado de {s.Name} a {chosen}.", null);
            }
        };
 // Etiqueta de estado a la izquierda del desplegable: "En ejecución" (verde,
 // igual que el punto) o "Detenido" (gris). Refleja el snapshot del momento
 // en que se abrió la pestaña, igual que el punto de estado.
        var startTypePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        startTypePanel.Children.Add(new TextBlock
        {
            Text = I18n.T(s.IsRunning ? "En ejecución" : "Detenido"),
            FontSize = 11,
            Foreground = s.IsRunning
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 175, 80))
                : muted,
            VerticalAlignment = VerticalAlignment.Center
        });
        startTypePanel.Children.Add(combo);
        Grid.SetColumn(startTypePanel, 2);
        row.Children.Add(startTypePanel);
 // Contenedor visible de la fila: banda redondeada con el fill de grupo de
 // los ThemeDictionaries (tema claro/oscuro/paletas via {ThemeResource}) y
 // margen inferior para separar filas. El ListView agrega hover/selection
 // por encima, igual que en el resto de las listas de la app.
        var rowHost = new Border
        {
            // Pincel LIVE de ThemeBrushes: tema claro/oscuro/paletas correcto y
            // se repinta solo al cambiar de tema (el lookup directo en
            // Application.Resources usaría el tema del SISTEMA, no el de la app).
            Background = ThemeBrushes.GetSurface("SensorGroupFillBrush"),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 3),
            Child = row
        };
 // Tooltip con la descripción del servicio (del SO, sin traducir) + estado.
        UIElement MakeTip()
        {
            var tip = new StackPanel { Spacing = 4, MaxWidth = 320 };
            tip.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(s.DisplayName) ? s.Name : $"{s.DisplayName} ({s.Name})",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrWhiteSpace(s.Description))
                tip.Children.Add(new TextBlock
                {
                    Text = s.Description!,
                    FontSize = 11,
                    Foreground = muted,
                    TextWrapping = TextWrapping.Wrap
                });
            tip.Children.Add(new TextBlock
            {
                Text = I18n.T("Estado actual: {0} · {1}",
                    I18n.T(currentState switch
                    {
                        ServiceStartState.Disabled => "Desactivado",
                        ServiceStartState.Manual => "Manual",
                        _ => "Activado"
                    }),
                    s.IsRunning ? I18n.T("En ejecución") : I18n.T("Detenido")),
                FontSize = 11,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap
            });
            if (critical)
                tip.Children.Add(new TextBlock
                {
                    Text = I18n.T("Servicio crítico del sistema: WinForge no permite cambiarlo."),
                    FontSize = 11,
                    Foreground = Feedback.ErrorBrush,
                    TextWrapping = TextWrapping.Wrap
                });
            return tip;
        }        ToolTipService.SetToolTip(infoButton, new ToolTip { Content = MakeTip(), Placement = PlacementMode.Top });

        return rowHost;
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
                // RunningRuleExes cubre los juegos con launcher: el proceso real
                // puede llamarse distinto del exe de la biblioteca y, aun así, la
                // card tiene que mostrar "En ejecución" (no dejar relanzar).
                _runningExes = new HashSet<string>(
                    _processService.RunningGameExes.Concat(_processService.RunningRuleExes),
                    StringComparer.OrdinalIgnoreCase);
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
                if (_cards3D) RequestCartridgeDockUpdate();
                if (_counterStatusVisible)
                {
                    UpdateInstalledCount();
                }
            }
            catch { }
        });
    }

    /// <summary>
    /// Estado de la regla que publica ProcessService. Se usa la barra de estado de la
    /// biblioteca (no un diálogo): Waiting cuando el juego se lanzó y todavía no
    /// apareció su proceso real (launcher de por medio), Applied con el proceso al que
    /// se aplicó, y Failed solo si venció la ventana de espera. Nunca se pregunta por
    /// el launcher en cada arranque.
    /// </summary>
    private void OnGameRuleStatusChanged(GameRuleStatus status)
    {
        _lastGameRuleStatus = status;
        DispatcherQueue.TryEnqueue(() =>
        {
            try { ApplyGameRuleStatus(status); }
            catch { }
        });
    }

    /// <summary>
    /// Pinta el estado de la regla en la barra del header (la de siempre) y en la
    /// barra inferior con luz propia. Debe correr en el hilo de UI.
    /// </summary>
    private void ApplyGameRuleStatus(GameRuleStatus status)
    {
        _lastGameRuleStatus = status;
        string message = GameRuleStatusMessage(status);

        StatusText.Text = message;
        StatusText.Foreground = status.State switch
        {
            GameRuleState.Applied when !status.Limited => Feedback.SuccessBrush,
            GameRuleState.Waiting => Feedback.WarningBrush,
            GameRuleState.Applied or GameRuleState.Failed => Feedback.WarningBrush,
            _ => Feedback.MutedBrush
        };
        // El juego cerró (o la regla dejó de existir): no dejar colgado un
        // "Regla aplicada a…" de una partida que ya terminó.
        StatusText.Visibility = status.State == GameRuleState.None
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Barra de regla: SOLO en 2D. En 3D el estado lo lleva el texto del propio dock
        // (UpdateDockStatusText) y la torre del cartucho montado pasaría por detrás del
        // vidrio de la barra, mezclando el texto con el arte. En su fila nada pasa por
        // detrás: corte limpio siempre.
        if (status.State == GameRuleState.None)
        {
            GameRuleStatusBar.Visibility = Visibility.Collapsed;
            GameRuleStatusLedHost.Content = null;
        }
        else
        {
            GameRuleStatusText.Text = message;
            GameRuleStatusLedHost.Content = CartridgeDock.CreateRuleLed(status.State, status.Limited);
            // En 3D la barra queda colapsada (el dock ya muestra este mismo mensaje en
            // su texto reactivo): así la torre nunca se ve a través del vidrio.
            GameRuleStatusBar.Visibility = _cards3D ? Visibility.Collapsed : Visibility.Visible;
        }

        UpdateDockStatusText();
        string launcherLabel = string.IsNullOrWhiteSpace(status.Launcher) ? "-" : status.Launcher;
        string processLabel = string.IsNullOrWhiteSpace(status.ProcessName) ? "-" : status.ProcessName;
        _loggingService.LogDebug($" Biblioteca: regla de {status.Exe} = {status.State} (launcher={launcherLabel}, proceso={processLabel}, limited={status.Limited}, strong={status.Strong})");
        // En 3D la única luz es la de la consola: sigue a la regla (amarilla en
        // espera, verde solo cuando se aplicó). Sin regla activa vuelve a la de siempre.
        if (_cards3D && _dockLedHost != null)
            _dockLedHost.Content = BuildDockLed();
        // La espera lleva cuenta regresiva real; cualquier otro estado la detiene.
        if (status.State == GameRuleState.Waiting)
            StartRuleCountdown();
        else
            StopRuleCountdown();
    }

    /// <summary>Texto del estado de la regla (header, barra y segunda luz del dock).
    /// En espera puede llevar el tiempo restante delante: el footer 3D trunca por
    /// la derecha (MaxLines=1), así la cuenta se sigue viendo aunque se corte el texto.</summary>
    private string GameRuleStatusMessage(GameRuleStatus status, TimeSpan? remaining = null, bool compact = false)
    {
        string name = FindGameDisplayName(status.Exe);
        string waitPrefix = remaining.HasValue ? "(" + remaining.Value.ToString(@"m\:ss") + ") " : "";
        // En el footer 3D (una sola línea que se trunca) el nombre largo del juego
        // tapaba lo importante: ahí va el texto compacto genérico con la cuenta real.
        if (compact && status.State == GameRuleState.Waiting)
        {
            string left = remaining.HasValue ? remaining.Value.ToString(@"m\:ss") : "…";
            return string.IsNullOrWhiteSpace(status.Launcher)
                ? I18n.T("Esperando el proceso del juego… quedan {0}", left)
                : I18n.T("Escuchando el launcher ({0})… quedan {1}", status.Launcher, left);
        }
        return status.State switch
        {
            GameRuleState.Waiting => waitPrefix + (string.IsNullOrWhiteSpace(status.Launcher)
                ? I18n.T("Regla de {0}: esperando el proceso del juego…", name)
                : I18n.T("Regla de {0}: esperando el proceso del juego (launcher: {1})…", name, status.Launcher)),
            GameRuleState.Applied when status.Limited => I18n.T(
                "Regla de {0} aplicada a {1} con limitaciones (proceso protegido o anti-cheat).",
                name, status.ProcessName ?? status.Exe),
            GameRuleState.Applied => I18n.T(
                "Regla de {0} aplicada a {1}.", name, status.ProcessName ?? status.Exe),
            GameRuleState.Failed => string.IsNullOrWhiteSpace(status.Launcher)
                ? I18n.T("No se pudo identificar el proceso de {0} después de esperarlo. La regla no se aplicó.", name)
                : I18n.T("No se pudo identificar el proceso de {0} (launcher: {1}) después de esperarlo. La regla no se aplicó.", name, status.Launcher),
            _ => ""
        };
    }

    /// <summary>
    /// Pinta el estado de la regla en la segunda luz del footer. Se llama desde
    /// ApplyGameRuleStatus (cambios en vivo) y al armar el dock (estado vigente).
    /// Debe correr en el hilo de UI.
    /// </summary>
    private void UpdateDockStatusText()
    {
        if (_dockStatusLine == null) return;

        var status = _lastGameRuleStatus;
        bool hasStatus = status != null && status.State != GameRuleState.None;
        _dockStatusLine.Text = hasStatus ? GameRuleStatusMessage(status!) : _dockDefaultStatus ?? "";
        // Un renglón cuando manda el estado de la regla: así no se estira sobre la
        // costura del chasis (el footer mide 66 DIP y la costura está a 49).
        _dockStatusLine.MaxLines = hasStatus ? 1 : 2;
    }

    /// <summary>Luz del dock 3D: con regla activa muestra EL ESTADO DE LA REGLA
    /// (amarilla esperando, verde solo aplicada, roja fallida); sin regla, la de la consola.</summary>
    private Grid BuildDockLed()
    {
        var st = _lastGameRuleStatus;
        if (st != null && st.State != GameRuleState.None)
            return CartridgeDock.CreateRuleLed(st.State, st.Limited);
        return CartridgeDock.CreateLed(_dockLedConnected, _dockLedRunning, _dockLaunchFailed);
    }

    /// <summary>Arranca la cuenta regresiva de la espera (1 s, hilo de UI).</summary>
    private void StartRuleCountdown()
    {
        StopRuleCountdown();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += RuleCountdownTick;
        timer.Start();
        _ruleCountdownTimer = timer;
        RefreshRuleCountdown();
    }

    /// <summary>Detiene la cuenta regresiva (regla aplicada, fallida o sin estado).</summary>
    private void StopRuleCountdown()
    {
        var timer = _ruleCountdownTimer;
        _ruleCountdownTimer = null;
        if (timer == null) return;
        try
        {
            timer.Tick -= RuleCountdownTick;
            timer.Stop();
        }
        catch { }
    }

    private void RuleCountdownTick(DispatcherQueueTimer? timer, object? e) => RefreshRuleCountdown();

    /// <summary>Refresca el tiempo restante de la espera con el valor real del servicio.</summary>
    private void RefreshRuleCountdown()
    {
        var st = _lastGameRuleStatus;
        if (st == null || st.State != GameRuleState.Waiting) { StopRuleCountdown(); return; }
        // Sin sesión activa ya no hay nada que contar: el evento Failed/None actualiza el texto.
        var remaining = _processService.GetLaunchSessionRemaining(st.Exe);
        if (remaining == null) { StopRuleCountdown(); return; }
        string message = GameRuleStatusMessage(st, remaining.Value);
        StatusText.Text = message;
        GameRuleStatusText.Text = message;
        // El footer lleva el texto compacto (sin el nombre del juego): con el nombre
        // largo se truncaba y no se llegaba a ver la cuenta.
        if (_dockStatusLine != null) _dockStatusLine.Text = GameRuleStatusMessage(st, remaining.Value, compact: true);
    }

    /// <summary>Al volver a la pestaña, relee el estado del servicio: si el juego cerró
    /// mientras la página estaba en segundo plano, el evento None no se recibió y
    /// la barra quedaría mostrando una partida terminada.
    /// </summary>
    private void RestoreGameRuleStatus()
    {
        GameRuleStatus? current = _lastGameRuleStatus != null
            ? _processService.GetGameRuleStatus(_lastGameRuleStatus.Exe)
            : null;

        // Sin estado previo en esta página (primer ingreso, o la app se abrió con
        // un juego ya en ejecución): preguntar al servicio por las reglas que
        // tiene enganchadas en vivo. Antes se salía temprano y la segunda luz
        // nunca aparecía hasta el próximo cambio de estado.
        if (current == null || current.State == GameRuleState.None)
        {
            foreach (var exe in _processService.RunningRuleExes)
            {
                var candidate = _processService.GetGameRuleStatus(exe);
                if (candidate is { State: GameRuleState.Applied or GameRuleState.Waiting })
                {
                    current = candidate;
                    break;
                }
            }
        }

        if (current != null)
        {
            ApplyGameRuleStatus(current);
            return;
        }

        // Nada en vivo: limpiar lo que haya quedado de la visita anterior.
        if (_lastGameRuleStatus != null)
            ApplyGameRuleStatus(new GameRuleStatus(
                _lastGameRuleStatus.Exe, GameRuleState.None, _lastGameRuleStatus.Launcher));
    }

    /// <summary>Nombre visible del juego a partir del exe de la regla (biblioteca o manual).</summary>
    private string FindGameDisplayName(string exe)
    {
        var game = _installed.FirstOrDefault(g => string.Equals(g.ExeFileName, exe, StringComparison.OrdinalIgnoreCase));
        if (game != null && !string.IsNullOrWhiteSpace(game.Name)) return game.Name;
        var manual = _manual.FirstOrDefault(m => string.Equals(m.Exe, exe, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(manual.Name) ? exe : manual.Name!;
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
            // Los juegos manuales también tienen card: incluirlos para que el estado
            // "En ejecución" (RunningRuleExes) se aplique al reabrir la página.
            knownExes.UnionWith(_manual.Select(m => m.Exe));

            // Registrar exe → carpeta de instalación: el monitor de Core matchea por
            // ruta los procesos cuyo nombre difiere del exe detectado (ej. launchers
            // como Smite.exe que corren SmiteGame-Win64-Shipping.exe).
            PublishKnownInstallPaths();

            // Estado de ejecución desde el snapshot de los eventos WMI: no se
            // re-enumeran procesos ni se mide CPU en cada apertura (antes la página
            // pagaba ~250 ms de GetRunningAppsAsync para saber qué juegos corren).
            _runningExes = new HashSet<string>(
                _processService.RunningGameExes.Concat(_processService.RunningRuleExes)
                    .Where(e => knownExes.Contains(e)),
                StringComparer.OrdinalIgnoreCase);

            // Garantizar un mínimo de tiempo visible del skeleton (como en SistemaPage)
            // para que el efecto de carga se aprecie aunque el escaneo sea veloz.
            await EnsureMinSkeletonVisibleAsync();
            RebuildCards();
            // Traducir el contenido ahora que los elementos reales están en el árbol visual
            I18n.ApplyToVisualTree(this);
            UpdateInstalledCount();
            InstalledCountText.Visibility = Visibility.Visible;
            _counterStatusVisible = true;

            // Después de dibujar: reparar sola las rutas vencidas (no bloquea la página).
            _ = RepairMissingPathsAsync();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"GestionarProcesosPage: no se pudo escanear juegos: {ex.GetType().Name}: {ex.Message} | {ex.StackTrace}");
            StatusText.Text = I18n.T("No se pudieron escanear los juegos: {0}", ex.Message);
            StatusText.Foreground = Feedback.ErrorBrush;
            StatusText.Visibility = Visibility.Visible;
            InstalledCountText.Visibility = Visibility.Collapsed;
            _counterStatusVisible = false;
            // El skeleton reemplazó la grilla anterior: si el escaneo falla no se
            // puede dejar la biblioteca en blanco. Se reconstruye con la última
            // lista conocida (y el estado vacío si tampoco había juegos).
            if (showSkeleton)
            {
                try { RebuildCards(); }
                catch (Exception rebuildEx)
                {
                    _loggingService.LogWarning($"GestionarProcesosPage: restaurar grilla tras fallo de escaneo: {rebuildEx.Message}");
                }
            }
        }
        finally
        {
            RedetectButton.IsEnabled = true;
            HideSkeleton();
        }
    }

    /// <summary>
    /// Publica en el enganche (ProcessService) el mapa exe → carpeta de instalación.
    /// Es lo que permite matchear procesos cuyo nombre difiere del exe detectado.
    /// </summary>
    private void PublishKnownInstallPaths()
    {
        var knownPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _installed)
            if (!string.IsNullOrEmpty(g.InstallPath))
                knownPaths[g.ExeFileName] = g.InstallPath;
        foreach (var (exe, _, path) in _manual)
            if (!string.IsNullOrEmpty(path))
                knownPaths[exe] = path;
        _processService.SetKnownInstallPaths(knownPaths);
    }

    private bool _repairRunning;

    /// <summary>
    /// "Reparar sola": si alguna entrada quedó apuntando a una carpeta que ya no existe
    /// —juegos de la Store, que llevan la versión en el nombre de la carpeta, o un juego
    /// o emulador que se movió— se vuelve a resolver en segundo plano y la biblioteca se
    /// actualiza sin pedir un Re-detectar. No agrega ni quita juegos: solo corrige la
    /// carpeta de entradas que ya estaban, así que un juego borrado a propósito no
    /// reaparece.
    /// </summary>
    private async Task RepairMissingPathsAsync()
    {
        if (_repairRunning) return;
        _repairRunning = true;
        try
        {
            var repairs = await _installedGamesService.RepairMissingInstallPathsAsync(_installed);
            if (repairs.Count == 0) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    foreach (var (broken, fresh) in repairs)
                    {
                        var idx = _installed.IndexOf(broken);
                        if (idx >= 0) _installed[idx] = fresh;
                    }
                    PublishKnownInstallPaths();
                    RebuildCards();
                    I18n.ApplyToVisualTree(this);
                    UpdateInstalledCount();
                }
                catch (Exception ex)
                {
                    _loggingService.LogWarning($"GestionarProcesosPage: aplicar rutas reparadas: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"GestionarProcesosPage: reparar rutas vencidas: {ex.Message}");
        }
        finally
        {
            _repairRunning = false;
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

        // La página queda cacheada: al limpiar la caché desde Configuración y volver,
        // la grilla todavía tiene las cards de la visita anterior. El skeleton tiene
        // que REEMPLAZARLAS; si solo se agrega al final del LibraryPanel queda debajo
        // de todas las cards, fuera de vista, y la carga no se percibe. Se limpia lo
        // mismo que limpia RebuildCards antes de armar la grilla nueva.
        if (LibraryPanel.Children.Count > 0)
        {
            CancelCartridgeDragImmediate();
            _cartridgeVisuals.Clear();
            LibraryPanel.Children.Clear();
            _cards.Clear();
            _wrapSectionGroups.Clear();
            _launcherButtons.Clear();
            _gameLaunchButtons.Clear();
            _orderedLibraryGames.Clear();
            _skeletonPanel = null;
        }
        EmptyStatePanel.Visibility = Visibility.Collapsed;

        // Si se guardó entre corridas con el otro estilo de card, la silueta y el
        // alto ya no sirven: se descarta y se reconstruye con el modo actual.
        if (_skeletonCards.Count > 0 && _skeletonCards3D != _cards3D)
        {
            foreach (var (card, _, _) in _skeletonCards)
                if (card.Parent is Panel parent)
                    parent.Children.Remove(card);
            _skeletonCards.Clear();
        }

        if (_skeletonCards.Count == 0)
        {
            _skeletonCards3D = _cards3D;
            // Dos filas completas con las columnas REALES de la vista: 5 en modo 3D
            // y 3 en clásico. Antes eran siempre 3 placeholders por fila, así que
            // en 3D la primera fila ocupaba solo 3/5 del ancho y no coincidía con
            // la grilla de cartuchos.
            int count = Math.Clamp(GetEffectiveColumns() * 2, 6, 12);
            for (int i = 0; i < count; i++)
            {
                var (card, body, blocks) = BuildSkeletonCard(_cards3D);
                _skeletonCards.Add((card, body, blocks));
            }
        }

        if (_skeletonPanel == null)
        {
            // Re-mostrar skeletons ya construidos (el panel se limpió en RebuildCards).
            _skeletonPanel = new StackPanel { Spacing = GridCardSpacing };
            LibraryPanel.Children.Add(_skeletonPanel);
        }
        // Mismo espaciado y mismas columnas que la grilla real. ReflowSkeleton
        // desengancha las cards de su fila anterior antes de re-colgarlas: sin eso
        // el árbol tira "Element is already the child of another element".
        ReflowSkeleton(GetEffectiveColumns());
        UpdateCardWidth();
        StartSkeletonPulse();
    }

    /// <summary>
    /// Acomoda las cards del skeleton en filas de N columnas, igual que
    /// <see cref="ReflowWrapSection"/>. Se llama al mostrarlo y cada vez que cambia
    /// el ancho disponible (la cantidad de columnas puede cambiar al maximizar).
    /// </summary>
    private void ReflowSkeleton(int perRow)
    {
        if (_skeletonPanel == null) return;
        perRow = Math.Max(1, perRow);
        _skeletonGridCols = perRow;
        foreach (var child in _skeletonPanel.Children)
            if (child is Panel oldRow)
                oldRow.Children.Clear();
        _skeletonPanel.Children.Clear();
        for (int i = 0; i < _skeletonCards.Count; i += perRow)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = GridCardSpacing };
            for (int j = i; j < Math.Min(i + perRow, _skeletonCards.Count); j++)
            {
                var card = _skeletonCards[j].Card;
                if (card.Parent is Panel previous)
                    previous.Children.Remove(card);
                row.Children.Add(card);
            }
            _skeletonPanel.Children.Add(row);
        }
    }

    private void HideSkeleton()
    {
        if (!_skeletonActive) return;
        _skeletonActive = false;
        StopSkeletonPulse();
        // RebuildCards ya limpia el panel; esto cubre el caso de error antes de
        // reconstruir (que queden skeletons huérfanos).
        if (_skeletonPanel != null)
        {
            LibraryPanel.Children.Remove(_skeletonPanel);
            // Las filas se van con el panel, pero las cards son de la página (se reusan en el
            // próximo skeleton): se las suelta ahora para que no queden con padre colgado.
            foreach (var (card, _, _) in _skeletonCards)
            {
                if (card.Parent is Panel parent)
                    parent.Children.Remove(card);
            }
        }
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
    /// Card placeholder del skeleton, espejo de la card real. En 2D replica el banner
    /// panorámico (460:215) con la barra de título al pie; en 3D replica el CARTUCHO:
    /// misma silueta con esquinas redondeadas y muescas, mismo padding de 4 px y misma
    /// relación de alto (600×878) que la card real. UpdateCardWidth le da el tamaño
    /// exacto de la grilla.
    /// </summary>
    private static (Border Card, FrameworkElement Body, FrameworkElement[] Blocks) BuildSkeletonCard(bool cards3D)
    {
        // Mismo pincel que los bloques del skeleton de SistemaPage
        // (ControlFillColorSecondaryBrush): el gris clásico de carga.
        var skeletonBrush = ThemeBrushes.Get("ControlFillColorSecondaryBrush");

        if (cards3D)
        {
            // Silueta idéntica a la del cartucho real: esquinas de 36/600 y muescas
            // laterales. El Polygon se recalcula en SizeChanged, así el contorno
            // acompaña cualquier ancho de columna.
            var shell = new Polygon
            {
                Fill = skeletonBrush,
                IsHitTestVisible = false
            };
            // Pozo oscuro de la etiqueta: el mismo contraste que el arte real sobre
            // el plástico, para que el placeholder se lea como cartucho y no como
            // un rectángulo gris.
            var sticker = new Grid { Margin = new Thickness(14, 16, 14, 24) };
            var well = new Border
            {
                Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
                CornerRadius = new CornerRadius(10),
                Opacity = 0.92
            };
            Grid.SetRowSpan(well, 3);
            sticker.Children.Add(well);
            sticker.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.22, GridUnitType.Star) });
            sticker.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            sticker.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.16, GridUnitType.Star) });

            var header = new Border
            {
                Background = skeletonBrush,
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(7, 7, 7, 5)
            };
            var art = new Border
            {
                Background = skeletonBrush,
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(7, 3, 7, 3)
            };
            var code = new Border
            {
                Background = skeletonBrush,
                Width = 56,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(header, 0);
            Grid.SetRow(art, 1);
            Grid.SetRow(code, 2);
            sticker.Children.Add(header);
            sticker.Children.Add(art);
            sticker.Children.Add(code);

            // Pestaña superior hundida, el mismo detalle del cartucho real.
            var tab = new Border
            {
                Width = 22,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var body = new Grid
            {
                // Tamaño intrínseco para la primera apertura; UpdateCardWidth lo
                // ajusta a (ancho de card - 8) × Aspect, igual que el cartucho real.
                Height = 260
            };
            body.Children.Add(shell);
            body.Children.Add(sticker);
            body.Children.Add(tab);
            body.SizeChanged += (_, e) =>
                shell.Points = CartridgeTile.BuildSilhouettePoints(e.NewSize.Width, e.NewSize.Height);

            var card3D = new Border
            {
                // Mismo chasis que BuildGameCard3D: 4 px de padding y fondo
                // transparente; el volumen y la forma los da la silueta.
                Padding = new Thickness(4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                CornerRadius = new CornerRadius(10),
                Tag = "no-reveal",
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = body
            };
            // El chasis queda fijo y los bloques internos pulsan: el placeholder no
            // pierde su forma de cartucho durante la animación.
            return (card3D, body, new FrameworkElement[] { header, art, code });
        }

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
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            // "no-reveal": excluye la card del efecto reveal global (RevealEffect);
            // los juegos ya tienen su propio hover (elevación + overlay de lanzar).
            Tag = "no-reveal",
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = bannerGrid
        };
        // Los bloques animables: banner y barra de título (cada uno con su pulso).
        return (card, banner, new FrameworkElement[] { banner, titleBar });
    }

    // Re-detectar: re-escanea los launchers a propósito y actualiza la caché
    // (por si se instaló/desinstaló un juego desde la última visita).
    private void RedetectButton_Click(object sender, RoutedEventArgs e)
    {
        // Re-detectar limpia ocultos, manuales y eliminados para que solo se
        // muestren los juegos detectados automáticamente por el escáner (un juego
        // eliminado de la biblioteca tampoco debe reaparecer al re-detectear).
        _processService.ClearHiddenExes();
        _processService.ClearManualExes(); // Limpiar juegos manuales
        _processService.ClearDeletedGames(); // Limpiar eliminados (vuelven los quitados)
        _ = RefreshAsync(showSkeleton: false, refreshCache: true);
    }

    // ===== Emuladores: carpeta portable y escaneo profundo =====

    /// <summary>
    /// Escanea UNA carpeta elegida por el usuario buscando emuladores portables (Capa 3 del
    /// escáner: la carpeta queda guardada, así que los emuladores que aparezcan se detectan
    /// también en las próximas aperturas). A diferencia del escaneo profundo, esto es acotado.
    /// </summary>
    /// <summary>
    /// Escaneo profundo: recorre todas las unidades fijas/removibles buscando emuladores
    /// conocidos (Capa 4). Puede tardar minutos y es la única parte del escáner que exige
    /// confirmación explícita; el avance se muestra en la barra de estado.
    /// </summary>
    private async void DeepScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null) return;
        try
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = I18n.T("Escaneo profundo"),
                Content = I18n.T("Esto puede tardar varios minutos y solo se ejecutará cuando lo confirmes. Se buscarán emuladores conocidos, no juegos."),
                PrimaryButtonText = I18n.T("Iniciar escaneo"),
                CloseButtonText = I18n.T("Cancelar"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            DeepScanButton.IsEnabled = false;
            RedetectButton.IsEnabled = false;

            // El servicio informa porcentaje + contexto (la unidad en curso, o una etiqueta
            // como "Preparando unidades..."): el contexto se traduce acá porque el texto nace
            // en Core.
            var progress = new Progress<EmulatorScanProgress>(p =>
            {
                string step = I18n.T("Buscando emuladores en todos los discos...");
                string context = ScanProgressContext(p.Current);
                Feedback.Running(StatusText,
                    context.Length > 0 ? $"{step} {p.Percent}% · {context}" : $"{step} {p.Percent}%",
                    persistent: true);
            });

            await _installedGamesService.GetInstalledGamesAsync(
                refresh: true, deepEmulatorScan: true, progress);

            // La caché ya trae el resultado del escaneo profundo: se relee sin volver a escanear.
            await RefreshAsync(showSkeleton: false, refreshCache: false);
            Feedback.Success(StatusText, I18n.T("Finalizó el escaneo profundo de emuladores."), persistent: true);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("GestionarProcesosPage: error en el escaneo profundo de emuladores", ex);
            Feedback.Error(StatusText, I18n.T("No se pudieron escanear los juegos: {0}", ex.Message));
        }
        finally
        {
            DeepScanButton.IsEnabled = true;
            RedetectButton.IsEnabled = true;
        }
    }

    /// <summary>Contexto del avance: las etiquetas conocidas del servicio se traducen; el resto
    /// (raíz de una unidad, p. ej. "D:\\") se muestra tal cual.</summary>
    private static string ScanProgressContext(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return "";
        return Translations.HasKey(context) ? I18n.T(context) : context;
    }

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
            var skippedLaunchers = new List<string>();
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
                // Un launcher/instalador/anti-cheat (ej. BlacksmithBootstrap.exe) no
                // es un juego: no tiene sentido en la biblioteca (no se puede lanzar
                // el juego desde ahí). Se avisa y se omite.
                if (GameExeResolver.IsStubExeName(resolved))
                {
                    skippedLaunchers.Add(Path.GetFileName(resolved));
                    continue;
                }
                string? dir = Path.GetDirectoryName(resolved);
                _processService.AddManualExe(Path.GetFileName(resolved), Path.GetFileNameWithoutExtension(resolved), dir);

                // Detectar automáticamente si es Dark y configurar Blacksmith
                if (GameLauncher.IsDarkAndDarker(dir, Path.GetFileName(resolved)))
                {
                    _processService.SetManualGameLauncher(Path.GetFileName(resolved), "Blacksmith");
                }

                added.Add(resolved);
            }

            if (added.Count == 0)
            {
                StatusText.Text = skippedLaunchers.Count > 0
                    ? I18n.T("No se agregó «{0}»: es un launcher o instalador, no el juego.", skippedLaunchers[0])
                    : I18n.T("No se agregó ningún juego: «{0}» no es un ejecutable.", skipped.FirstOrDefault() ?? "");
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
        CancelCartridgeDragImmediate();
        _cartridgeVisuals.Clear();
        LibraryPanel.Children.Clear();
        _cards.Clear();
        _wrapSectionGroups.Clear();
        _launcherButtons.Clear();
        _gameLaunchButtons.Clear();
        _orderedLibraryGames.Clear();
        _skeletonPanel = null; // el skeleton (si estaba) se fue con el clear del panel

        var hidden = _processService.GetHiddenExes();
        var deleted = _processService.GetDeletedGames();
        var items = new List<(InstalledGame? game, string exe, string? name, bool isManual, string? installPath)>();
        string q = _searchQuery.Trim();
        foreach (var g in _installed)
            if (!hidden.Contains(g.ExeFileName) && !deleted.Contains(g.ExeFileName))
                items.Add((g, g.ExeFileName, g.Name, false, g.InstallPath));
        foreach (var (exe, name, path) in _manual)
            if (!hidden.Contains(exe) && !deleted.Contains(exe) && !items.Any(i => string.Equals(i.exe, exe, StringComparison.OrdinalIgnoreCase)))
                items.Add((null, exe, name ?? exe, true, path));
        // Búsqueda por nombre (case-insensitive, parcial): vacío muestra todo.
        if (q.Length > 0)
        {
            items = items
                .Where(i => (i.name ?? i.exe).Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
 // Filtro de categoría: "all" (todos), "games", "console_emu", "mobile_emu".
 // GetItemCategory clasifica cada item; "all" no filtra nada.
        if (_currentFilter != "all")
        {
            items = items
                .Where(i => GetItemCategory(i.game) == _currentFilter)
                .ToList();
        }

        // Tres secciones con el MISMO desplegable simple: Favoritos / Detectados /
        // Ocultos. Dentro de cada grupo, primero los que están corriendo,
        // después alfabético.
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
            foreach (var it in favorites)
                _orderedLibraryGames.Add((it.game, it.exe, it.name ?? it.exe, it.isManual, it.installPath));
            LibraryPanel.Children.Add(BuildLibrarySection(
                glyph: "\uE735", // estrella llena (favorito)
                text: I18n.T("Favoritos ({0})", favorites.Count),
                content: BuildWrapPanel(favorites),
                expanded: _favoritesSectionExpanded,
                onToggled: v => _favoritesSectionExpanded = v));
        }
        if (others.Count > 0)
        {
            foreach (var it in others)
                _orderedLibraryGames.Add((it.game, it.exe, it.name ?? it.exe, it.isManual, it.installPath));
            LibraryPanel.Children.Add(BuildLibrarySection(
                glyph: "\uE7FC", // joystick (juego detectado)
                text: I18n.T("Detectados ({0})", others.Count),
                content: BuildWrapPanel(others),
                expanded: _detectedSectionExpanded,
                onToggled: v => _detectedSectionExpanded = v));
        }

        // ===== Sección de JUEGOS OCULTOS (desplegable, colapsada por defecto) =====
        // Los juegos "ocultados" no desaparecen: viven acá y se restauran con
        // clic derecho → Mostrar en la biblioteca. Los ELIMINADOS no se listan.
        // Se filtran por la pestaña activa igual que los items visibles.
        var hiddenItems = new List<(InstalledGame? game, string exe, string? name, bool isManual, string? installPath)>();
        foreach (var hExe in _processService.GetHiddenExes())
        {
            if (deleted.Contains(hExe)) continue;
            var g = _installed.FirstOrDefault(x => string.Equals(x.ExeFileName, hExe, StringComparison.OrdinalIgnoreCase));
            if (g != null)
            {
 // Filtrar por categoría: solo mostrar ocultos del tipo correcto.
                if (_currentFilter != "all" && GetItemCategory(g) != _currentFilter) continue;
                hiddenItems.Add((g, g.ExeFileName, g.Name, false, g.InstallPath));
                continue;
            }
 // Juegos manuales ocultos: siempre son "games" (no emuladores),
 // solo se muestran en "Todos" o "Videojuegos".
            if (_currentFilter != "all" && _currentFilter != "games") continue;
            var m = _manual.FirstOrDefault(mm => string.Equals(mm.Exe, hExe, StringComparison.OrdinalIgnoreCase));
            hiddenItems.Add((null, hExe, m.Name ?? hExe, m.Exe != null, m.InstallPath));
        }
        if (hiddenItems.Count > 0)
        {
            foreach (var it in hiddenItems)
                _orderedLibraryGames.Add((it.game, it.exe, it.name ?? it.exe, it.isManual, it.installPath));
            LibraryPanel.Children.Add(BuildLibrarySection(
                glyph: "\uED1A", // ojo tachado (oculto)
                text: I18n.T("Ocultos ({0})", hiddenItems.Count),
                content: BuildWrapPanel(hiddenItems),
                expanded: _hiddenSectionExpanded,
                onToggled: v => _hiddenSectionExpanded = v));
        }

        // Estado vacío: cuando no hay items para la pestaña/filtro actual.
        // Mensaje distinto según la pestaña y si hay búsqueda activa.
        if (items.Count == 0)
        {
            if (_searchQuery.Trim().Length > 0)
            {
                EmptyStateText.Text = I18n.T("No se encontraron juegos que coincidan con la búsqueda.");
                EmptyStateIcon.Glyph = "\uE721;"; // lupa
            }
            else
            {
                EmptyStateText.Text = _currentFilter switch
                {
                    "console_emu" => I18n.T("Instalá un emulador de consolas para verlo en este apartado y configurarlo a tu gusto!"),
                    "mobile_emu" => I18n.T("Instalá un emulador de celular para verlo en este apartado y configurarlo a tu gusto!"),
                    "games" => I18n.T("Instalá un juego para verlo en este apartado y configurarlo a tu gusto!"),
                    _ => I18n.T("Instalá un juego o emulador para verlo en este apartado y configurarlo a tu gusto!")
                };
                EmptyStateIcon.Glyph = "\uE7FC;"; // controler
            }
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
        UpdateCardWidth();
        // En modo 3D mostramos la consola/dock inferior y pintamos las caras visibles.
        // Coalescado: RebuildCards llega al final del RefreshAsync (async, tarde),
        // cuando el dock de OnNavigatedTo ya se pintó; un rebuild directo acá era
        // el segundo Clear()+Add que parpadeaba la card al volver.
        RequestCartridgeDockUpdate();
        // Viewport pegado a la banda y al dock/regla: el corte al scrollear pasa justo
        // en esos bordes (el aire en reposo lo pone LibraryContentGrid, así que quieto
        // la grilla queda en el mismo lugar que antes en ambos modos).
        if (_cards3D)
        {
            LibraryScroll.Margin = new Thickness(24, 0, 24, 0);
            LibraryContentGrid.Margin = new Thickness(0, 24, 0, 8);
            HookPaintOnScroll();
            SchedulePaintVisible3D();
        }
        else
        {
            LibraryScroll.Margin = new Thickness(24, 0, 24, 0);
            LibraryContentGrid.Margin = new Thickness(0, 24, 0, 32);
        }
    }

    /// <summary>Filtra la grilla por el texto del buscador (sin re-escannear).</summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text ?? "";
        RebuildCards();
    }

    /// <summary>
    /// Desplegable de categorías (Todos / Videojuegos / Emuladores de consolas /
    /// Emuladores de celular): aplica el filtro seleccionado a la grilla.
    /// El índice 0 («Todos») mapea a "all"; el guard evita rebuilds redundantes.
    /// </summary>
    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string filter = FilterComboBox.SelectedIndex switch
        {
            1 => "games",
            2 => "console_emu",
            3 => "mobile_emu",
            _ => "all"
        };
        if (_currentFilter == filter) return;
        _currentFilter = filter;
        UpdateInstalledCount();
        RebuildCards();
    }

    // Emuladores de celular conocidos (por nombre del InstalledGame.Name).
    // BlueStacks se detecta como "Independiente" (no "Emulador"), así que se
    // identifica por nombre también. El resto de emuladores de celular sí
    // traen Launcher="Emulador".
    private static readonly HashSet<string> MobileEmulatorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "BlueStacks 5", "LDPlayer", "NoxPlayer", "MEmu", "Genymotion", "MSI App Player",
        "MuMu Player", "GameLoop", "Google Play Games"
    };

    /// <summary>
    /// Clasifica un item en su categoría: "games" (videojuegos),
    /// "console_emu" (emulador de consola) o "mobile_emu" (emulador de celular).
    /// Los emuladores de consola son los que traen Launcher="Emulador" y NO
    /// están en la lista de emuladores móviles. Los de celular pueden traer
    /// Launcher="Emulador" o "Independiente" (BlueStacks) y se identifican por
    /// nombre. Los juegos manuales (game==null) son siempre "games".
    /// </summary>
    private static string GetItemCategory(InstalledGame? game)
    {
        if (game == null) return "games";
    // Emulador de celular por nombre (incluye BlueStacks que trae Launcher="Independiente").
        if (MobileEmulatorNames.Contains(game.Name)) return "mobile_emu";
    // El resto con Launcher="Emulador" son de consola.
        if (string.Equals(game.Launcher, "Emulador", StringComparison.OrdinalIgnoreCase)) return "console_emu";
    // Todo lo demás es videojuego.
        return "games";
    }

    /// <summary>
    /// Actualiza el contador según la categoría activa. El texto cambia:
    /// «Instalados: N» (todos), «Juegos instalados: N» (videojuegos),
    /// «Emuladores de consolas: N», «Emuladores de celular: N».
    /// </summary>
    private void UpdateInstalledCount()
    {
        int count = 0;
        var hidden = _processService.GetHiddenExes();
        var deleted = _processService.GetDeletedGames();
        foreach (var g in _installed)
        {
            if (hidden.Contains(g.ExeFileName) || deleted.Contains(g.ExeFileName)) continue;
            if (_currentFilter == "all" || GetItemCategory(g) == _currentFilter) count++;
        }
    // Los manuales (sin game) son siempre videojuegos.
        if (_currentFilter == "all" || _currentFilter == "games")
        {
            foreach (var (exe, _, _) in _manual)
            {
                if (hidden.Contains(exe) || deleted.Contains(exe)) continue;
                if (!_installed.Any(g => string.Equals(g.ExeFileName, exe, StringComparison.OrdinalIgnoreCase)))
                    count++;
            }
        }
        InstalledCountText.Text = _currentFilter switch
        {
            "games" => I18n.T("Juegos instalados: {0}", count),
            "console_emu" => I18n.T("Emuladores de consolas: {0}", count),
            "mobile_emu" => I18n.T("Emuladores de celular: {0}", count),
            _ => I18n.T("Instalados: {0}", count)
        };
    }

    /// <summary>
    /// Sección desplegable SIMPLE de la biblioteca (Favoritos / Detectados /
    /// Ocultos): las tres usan este mismo helper, mismo estilo.
    ///
    /// A propósito NO usa Expander de WinUI: su fondo de card + bordes + padding
    /// interno rompían la elevación al hover de las cards (el contenido quedaba
    /// recortado dentro de la card del Expander y la sombra/levantada de 6 px se
    /// perdía). Esto es solo un botón de header transparente (chevron + icono +
    /// texto, sin fondo ni borde) y el contenido suelto debajo, con margen
    /// interno para que la elevación y la sombra nunca se recorten.
    /// </summary>
    // ===== Pintado diferido y escalonado de la grilla 3D =====
    // La grilla 3D pinta solo las cards VISIBLES y de a pocas por vez:
    // - el scroll se congela si N cartuchos pintan (GDI+) y suben (GPU) a la vez;
    // - las secciones colapsadas (Ocultos) nunca pintan hasta expandirse.
    // _paintScheduled evita encolar el pase dos veces.
    // _exeCache evita re-escanear el disco por cada card: FindMainExePath recorre
    // la carpeta del juego (hasta 800 exes) y, con N juegos, eso solo ya tarda
    // una eternidad. La caché vive mientras la página esté abierta.
    private bool _paintScheduled;
    private bool _paintHooked;
    private readonly Dictionary<string, (string? ExePath, string? IconPath)> _exeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pinta las cards 3D visibles que todavía estén pendientes, de a
    /// <see cref="Cartridge3D.MaxConcurrentPaints"/> por vez. Se llama al terminar el
    /// rebuild, al asentarse el scroll y al expandir una sección.
    /// </summary>
    private void SchedulePaintVisible3D()
    {
        if (!_cards3D || _paintScheduled) return;
        _paintScheduled = true;
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _paintScheduled = false;
            PaintVisible3D();
        });
    }

    /// <summary>
    /// Recorre las cards 3D y pinta las que estén en el viewport del scroll (con un
    /// margen de una altura de pantalla arriba/abajo). Las colapsadas
    /// (Visibility.Collapsed por una sección cerrada) se saltan solas: su rectángulo
    /// en pantalla no intersecta el viewport.
    /// </summary>
    private void PaintVisible3D()
    {
        if (!_cards3D) return;
        try
        {
            var scroll = LibraryScroll;
            if (scroll == null) return;
            double viewTop = 0, viewBottom = scroll.ViewportHeight;
            if (viewBottom <= 0) viewBottom = scroll.ActualHeight;
            if (viewBottom <= 0) return;
            const double margen = 1.0; // una pantalla de margen arriba y abajo
            double top = viewTop - viewBottom * margen;
            double bottom = viewBottom + viewBottom * margen;
            foreach (var (cardEl, body) in _cards)
            {
                if (body is not Controls.Cartridge3D cartucho) continue;
                if (!cartucho.IsPaintPending) continue;
                GeneralTransform? t = null;
                try { t = cardEl.TransformToVisual(scroll); } catch { continue; }
                Windows.Foundation.Point p;
                try { p = t.TransformPoint(new Windows.Foundation.Point(0, 0)); } catch { continue; }
                double cardTop = p.Y, cardBottom = p.Y + cardEl.ActualHeight;
                if (cardBottom < top || cardTop > bottom) continue; // fuera de vista
                cartucho.EnsurePainted();
            }
        }
        catch { }
    }

    /// <summary>
    /// Engancha el scroll para pintar las cards 3D a medida que entran en vista
    /// (una sola vez). El scroll dispara ViewChanged muy seguido: solo se reacciona
    /// cuando deja de moverse (IsIntermediate == false).
    /// </summary>
    private void HookPaintOnScroll()
    {
        if (_paintHooked) return;
        _paintHooked = true;
        try
        {
            LibraryScroll.ViewChanged += (s, e) =>
            {
                if (e.IsIntermediate) return;
                SchedulePaintVisible3D();
            };
        }
        catch { }
    }

    private UIElement BuildLibrarySection(
        string glyph, string text, FrameworkElement content,
        bool expanded, Action<bool> onToggled)
    {
        var section = new StackPanel { Spacing = 8 };

        // El header es el TÍTULO de la sección, no una fila más de texto gris: el
        // chevron queda apagado (es solo el affordance), el icono va con el acento del
        // tema y el nombre en el color primario, 15 px seminegrita. Antes todo iba en
        // SecondaryTextBrush tamaño 14 y "Favoritos (7)" se leía como texto plano.
        var chevron = new FontIcon
        {
            Glyph = expanded ? "\uE70D" : "\uE76C", // abajo / derecha
            FontSize = 12,
            Foreground = ThemeBrushes.Get("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerContent.Children.Add(chevron);
        headerContent.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Foreground = ThemeBrushes.Get("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        headerContent.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeBrushes.Get("PrimaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        // Botón 100% transparente (sin fondo/borde en ningún estado): solo el
        // chevron + icono + texto actúan como toggle, sin romper el hover-lift.
        var toggle = new Button
        {
            Content = headerContent,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 6, 4, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        toggle.Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        toggle.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        toggle.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        toggle.Resources["ButtonBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        toggle.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        toggle.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        bool isOpen = expanded;
        content.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        toggle.Click += (s, e) =>
        {
            isOpen = !isOpen;
            content.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = isOpen ? "\uE70D" : "\uE76C";
            onToggled(isOpen);
            // Al expandir una sección en modo 3D, sus cards entran en vista: se
            // pintan ahora (antes quedaban en plástico gris hasta el próximo scroll).
            if (isOpen) SchedulePaintVisible3D();
        };
        section.Children.Add(toggle);

        // Margen interno generoso abajo (36 px en modo 3D): la torre del dock (hasta
        // ~43 DIP) respira debajo sin que se la vea recortada entre las cards. En 2D
        // (sin torre) bastan 10 px.
        var body = new Border
        {
            Child = content,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 6, 0, _cards3D ? 36 : 10),
            Clip = null
        };
        section.Children.Add(body);
        return section;
    }

    /// <summary>
    /// Grilla de un grupo de juegos en filas de N columnas adaptables (StackPanels
    /// puros, sin ItemsWrapGrid): render garantizado en cualquier contenedor y
    /// reflujo instantáneo al redimensionar o pasar a pantalla completa.
    /// </summary>
    private FrameworkElement BuildWrapPanel(List<(InstalledGame? game, string exe, string? name, bool isManual, string? installPath)> items)
    {
        var panel = new StackPanel { Spacing = GridCardSpacing };
        var sectionCards = new List<Border>(items.Count);
        foreach (var item in items)
        {
            var card = BuildGameCard(item.game, item.exe, item.name ?? item.exe, item.installPath, item.isManual);
            sectionCards.Add(card);
        }
        int perRow = GetEffectiveColumns();
        _currentGridCols = perRow;
        ReflowWrapSection(panel, sectionCards, perRow);
        _wrapSectionGroups.Add((panel, sectionCards));
        return panel;
    }

    private static void ReflowWrapSection(StackPanel panel, List<Border> cards, int perRow)
    {
        foreach (var child in panel.Children)
        {
            if (child is Panel oldRow)
                oldRow.Children.Clear();
        }
        panel.Children.Clear();
        for (int i = 0; i < cards.Count; i += perRow)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = GridCardSpacing };
            for (int j = i; j < Math.Min(i + perRow, cards.Count); j++)
            {
                var card = cards[j];
                if (card.Parent is Panel prev)
                    prev.Children.Remove(card);
                row.Children.Add(card);
            }
            panel.Children.Add(row);
        }
    }

    private Border BuildGameCard(InstalledGame? game, string exe, string name, string? installPath, bool isManual)
    {
        // Modo 3D: la card es un MODELO (cartucho D3D) arrastrable, SIN el HUD de la
        // card vanilla (título, logo del launcher y botones de acciones).
        if (_cards3D) return BuildGameCard3D(game, exe, name, installPath, isManual);

        bool fav = _processService.IsFavorite(exe);
        string? exePath = GameLauncher.FindExePath(installPath ?? "", exe);
        // Si el registro/launcher no entregó una ruta válida, resolver el exe desde
        // la carpeta completa. Esto es especialmente importante para Battle.net:
        // Hearthstone suele llegar con un nombre de exe vacío o con una ruta distinta
        // a la carpeta real, aunque Hearthstone.exe exista en el disco.
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            exePath = GameExeResolver.FindMainExePath(installPath ?? "");
        // El ícono de la card se extrae del exe REAL del juego (no del stub de
        // anti-cheat/consola): el exe detectado puede ser start_protected_game.exe
        // (EAC) o vconsole2.exe (CS2), cuyo ícono no es el del juego. El exe de
        // lanzamiento NO se toca: los juegos con anti-cheat arrancan por el stub.
        string? iconPath = GameExeResolver.IsStubExe(exePath ?? "") && !string.IsNullOrEmpty(installPath)
            ? GameExeResolver.FindBestGameExePath(installPath)
            : exePath;

        // ===== Banner e ícono (imagen principal del juego) =====
        // Cadena de visualización: banner remoto → ícono local del exe/.ico →
        // logo de WinForge. El fallback siempre permanece visible hasta que una
        // imagen válida se haya cargado correctamente.
        var mediaImage = new Image
        {
            // Base para íconos de fallback (exe/.ico): centrada y compacta (72 px).
            // El banner REAL después se muestra en Fill (ver ShowBannerImage).
            Width = 72,
            Height = 72,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        var fallbackIcon = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///logos/WinForge.png")),
            // Logo de WinForge cuando no hay NADA mejor: aún más chico (64 px),
            // centrado, para que el banner vacío se vea prolijo.
            Width = 64,
            Height = 64,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var banner = new Border
        {
            // La card es solo el banner (los botones van en el overlay al hover):
            // las cuatro esquinas redondeadas. El fondo inicial mantiene visible el
            // icono de WinForge incluso mientras se resuelve el recurso real.
            // Geometría IDÉNTICA para todas las cards (simétricas, estilo NVIDIA
            // App): el alto real lo fija UpdateCardWidth con BannerHeightRatio.
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0),
            MinHeight = 100,
            Background = ThemeBrushes.Get("CardHoverBrush")
        };
        var bannerGrid = new Grid();
        bannerGrid.Children.Add(fallbackIcon);
        bannerGrid.Children.Add(mediaImage);
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
        gearBtn.Click += (s, e) => ShowRuleMenu(gearBtn, exe, name, isManual, defenderFolder?.TrimEnd('\\'), cfgExe);

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
            "Blacksmith" => "ms-appx:///logos/launcher/blacksmithlogo.png",
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
        bool blacksmithLaunch = game?.Launcher == "Blacksmith";

        // Para juegos manuales, verificar si tiene un launcher configurado
        string? manualLauncher = null;
        if (isManual && game == null)
        {
            manualLauncher = _processService.GetManualGameLauncher(exe);
            if (!string.IsNullOrEmpty(manualLauncher))
            {
                blacksmithLaunch = string.Equals(manualLauncher, "Blacksmith", StringComparison.OrdinalIgnoreCase);
                steamLaunch = string.Equals(manualLauncher, "Steam", StringComparison.OrdinalIgnoreCase);
                riotLaunch = string.Equals(manualLauncher, "Riot", StringComparison.OrdinalIgnoreCase);
            }
        }
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
            // Epic Games"). La URI lleva namespace + catalogItemId + AppName: la
            // forma corta (solo AppName) quedó obsoleta y el juego no abría.
            // LaunchGameAsync asegura que el launcher esté corriendo.
            launchFile = GameLauncher.BuildEpicLaunchUri(game.ArtNamespace, game.AppId, game.EpicAppName);
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
        else if (game?.Launcher == "Ubisoft" && !string.IsNullOrEmpty(game.AppId))
        {
            // Ubisoft Connect: el AppId es el gameId numérico del registro
            // (HKLM\Ubisoft\Launcher\Installs\<id>); los accesos directos del
            // propio Connect usan uplay://launch/<id>/0, que abre el launcher
            // si hace falta y arranca el juego con su sesión. Sin AppId se cae
            // al exe directo (rama else).
            launchFile = $"uplay://launch/{game.AppId}/0";
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
        else if (blacksmithLaunch)
        {
            // Blacksmith (Dark and Darker): el launcher Blacksmith maneja el lanzamiento.
            // LaunchGameAsync se encarga de abrir Blacksmith y pasarle los argumentos.
            launchFile = exePath ?? "";
            launchArgs = "";
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
            // Epic no depende de encontrar el exe del launcher: la URI del juego la
            // abre el protocolo registrado del launcher (y GameLauncher la entrega
            // al shell del usuario, porque la app corre elevada).
            launcherFound = true;
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
        else if (blacksmithLaunch)
        {
            // Dark and Darker se lanza por su exe directo (DungeonCrawler.exe):
            // el launcher Blacksmith no es un launcher de arranque (no lanza el
            // juego solo y no es necesario para jugar). La card se comporta como
            // un juego normal: sin estado de launcher externo ni "Blacksmith no
            // iniciado".
            launcherProc = null;
        }
        // Para juegos manuales con launcher configurado. EXCEPTO Blacksmith
        // (Dark and Darker): ese launcher no es de arranque — no lanza el juego
        // solo y no es necesario para jugar; el juego se lanza por su exe directo
        // (DungeonCrawler.exe), así que la card se comporta como un juego normal
        // (sin estado de launcher externo).
        else if (!string.IsNullOrEmpty(manualLauncher)
                 && !string.Equals(manualLauncher, "Blacksmith", StringComparison.OrdinalIgnoreCase))
        {
            launcherProc = manualLauncher;
            launcherFound = _processService.IsLauncherRunning(manualLauncher);
            autoOpen = true;
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
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            // "no-reveal": excluye la card del efecto reveal global (RevealEffect);
            // los juegos ya tienen su propio hover (elevación + overlay de lanzar).
            Tag = "no-reveal",
            // Sin margin: el Spacing de los StackPanel (horizontal y vertical)
            // maneja todo el espaciado para que sea uniforme (12 px) en ambas
            // direcciones, entre cards de la misma fila y entre filas.
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch
            // El contenido (el banner a sangre) se asigna más abajo, junto con el
            // transform de hover.
        };
        // La card vanilla: el banner a sangre de siempre. El modo 3D no pasa por acá
        // (construye un modelo de cartucho en BuildGameCard3D).
        var lift = new TranslateTransform();   // elevación al hover
        card.Child = banner;
        card.RenderTransform = lift;
        card.Shadow = _cardShadow;
        card.PointerEntered += (s, e) =>
        {
            AnimateCardLift(lift, -6);
            card.Translation = new System.Numerics.Vector3(0f, 0f, 24f);
            FadeOverlay(overlay, true);
        };
        card.PointerExited += (s, e) =>
        {
            AnimateCardLift(lift, 0);
            card.Translation = System.Numerics.Vector3.Zero;
            FadeOverlay(overlay, false);
        };

        // Doble clic en la card → abre el cartucho 3D arrastrable + panel de parámetros.
        card.DoubleTapped += (s, e) => ShowGame3DDetail(game, exe, name, installPath, isManual);
        ToolTipService.SetToolTip(card, I18n.T("Doble clic para ver el cartucho 3D"));

        _cards.Add((card, banner));
        if (!string.IsNullOrEmpty(game?.BannerUrl))
            _ = LoadBannerAsync(banner, mediaImage, fallbackIcon, game!.AppId, game.BannerUrl, iconPath);
        else if (game?.Launcher == "Epic" && !string.IsNullOrEmpty(game.AppId) && !string.IsNullOrEmpty(game.ArtNamespace))
            _ = LoadEpicBannerAsync(banner, mediaImage, fallbackIcon, game.ArtNamespace, game.AppId, iconPath);
        else
            // Siempre iniciar la cadena de fallback aunque el launcher no tenga
            // banner: exe/icono local → icono WinForge ya visible.
            _ = LoadExeIconAsync(mediaImage, fallbackIcon, iconPath ?? "");

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
    /// Elevación suave de una card al hover: anima el Y de su TranslateTransform
    /// (0 → -6 al entrar, -6 → 0 al salir) con easing cúbico ease-out de 180 ms.
    /// Detiene la animación previa del transform para que entradas/salidas rápidas
    /// no se pisen. La sombra va aparte (ThemeShadow vía card.Translation z).
    /// </summary>
    private static void AnimateCardLift(TranslateTransform lift, double to)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(anim, lift);
        Storyboard.SetTargetProperty(anim, "Y");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    /// <summary>
    /// Card del modo 3D estilo Carthage:
    /// - Hueco moldeado ("recess") en la bandeja, con el título grabado abajo cuando el
    ///   cartucho se levanta y se arrastra fuera del hueco.
    /// - Imagen de cartucho completo (carcasa con muescas laterales, pestaña superior,
    ///   etiqueta con logo del launcher, carátula, franja inferior de código y flecha).
    /// - Al mover el mouse encima, el cartucho se levanta, se inclina hasta ±10° hacia el
    ///   puntero con PlaneProjection y muestra un reflejo radial suave.
    /// - Al arrastrar (> 8 px), se levanta el cartucho flotando con balanceo por velocidad:
    ///   soltarlo en el Dock inferior lo inserta e inicia el juego; soltarlo afuera lo
    ///   devuelve con resorte a su hueco.
    /// - Un clic abre la vista de inspección centrada con el cartucho 3D real a la
    ///   izquierda y todos los parámetros de optimización (CPU/GPU/Afinidad/Plan/etc.) a la derecha.
    /// </summary>
    private Border BuildGameCard3D(InstalledGame? game, string exe, string name, string? installPath, bool isManual)
    {
        string cacheKey = (installPath ?? "") + "|" + exe;
        if (!_exeCache.TryGetValue(cacheKey, out var cached))
        {
            string? exePath = GameLauncher.FindExePath(installPath ?? "", exe);
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                exePath = GameExeResolver.FindMainExePath(installPath ?? "");
            string? iconPath = GameExeResolver.IsStubExe(exePath ?? "") && !string.IsNullOrEmpty(installPath)
                ? GameExeResolver.FindBestGameExePath(installPath)
                : exePath;
            cached = (exePath, iconPath);
            _exeCache[cacheKey] = cached;
        }
        string? exePath2 = cached.ExePath;
        string? iconPath2 = cached.IconPath;

        string launcher = string.IsNullOrWhiteSpace(game?.Launcher) ? "Manual" : game!.Launcher!;
        string code = CartridgeModel.Code(launcher, game?.AppId, name);


        var tile = new CartridgeTile(name, launcher)
        {
            GameExe = exe,
            HoverTiltEnabled = _cards3DTilt,
            CoordinateRoot = RootGrid,
            CanInteract = () => _activeDrag == null && !_cartridgeFlight && !_dockLaunching
                && !string.Equals(_connectedCartridge?.Exe, exe, StringComparison.OrdinalIgnoreCase)
        };
        // Sin sombra proyectada en el cartucho: se veía como un halo gris alrededor
        // del plástico. El volumen lo dan el tilt, el lift y la silueta del propio arte.
        var card = new Border
        {
            Tag = "no-reveal", Padding = new Thickness(4), Child = tile,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(10)
        };
        tile.DragStarted += (point, grab) => StartCartridgeDrag(
            card, tile.CartridgeVisual, tile.Artwork.Source, game, exe, name, installPath, isManual,
            grab.X, grab.Y, point);
        tile.DragMoved += MoveCartridgeDrag;
        tile.DragCompleted += EndCartridgeDrag;
        tile.InspectRequested += () => ShowGame3DDetail(game, exe, name, installPath, isManual);

        _cartridgeVisuals.Add((exe, tile.CartridgeVisual));
        tile.SetRemoved(string.Equals(_connectedCartridge?.Exe, exe, StringComparison.OrdinalIgnoreCase));
        string? defenderFolder = !string.IsNullOrEmpty(installPath) ? installPath
            : (!string.IsNullOrEmpty(exePath2) ? Path.GetDirectoryName(exePath2) : null);
        string? cfgExe = !string.IsNullOrEmpty(iconPath2) ? Path.GetFileName(iconPath2) : exe;
        card.RightTapped += (s, e) => ShowRuleMenu(card, exe, name, isManual, defenderFolder?.TrimEnd('\\'), cfgExe);
        // Sin tooltip en las cards 3D: el cartel al hover se pidió quitar.

        // Renderiza y cachea la imagen completa del cartucho (con banner/ícono cuando esté listo)
        _ = LoadCartridgeGridImageAsync(tile.Artwork, game, name, launcher, code, iconPath2, exe);

        _cards.Add((card, tile));
        return card;
    }

    /// <summary>
    /// Versión del pintado de los cartuchos de la grilla. Es parte del nombre de la carpeta
    /// y de la clave de caché: al tocar `CartridgeModel` hay que subirla, si no la grilla
    /// sigue sirviendo los PNG viejos y el cambio no se ve hasta borrar la caché a mano.
    /// </summary>
    private const string GridShellCacheVersion = "v9";

    private static string GridShellCacheDir =>
        Path.Combine(WHPO.Core.AppPaths.RootDir, $"cartshells-{GridShellCacheVersion}");

    /// <summary>
    /// Asegura el archivo de arte (banner de Steam/Epic o ícono del ejecutable) y
    /// pinta la imagen 2D del cartucho (con muescas, pestaña, cabecera, arte y código)
    /// en segundo plano, guardándola en caché para que la grilla cargue al instante.
    /// </summary>
    private async Task LoadCartridgeGridImageAsync(
        Image target, InstalledGame? game, string title, string launcher, string code, string? iconPath, string exe)
    {
        try
        {
            // Publish a solid cartridge FIRST. Network art must never gate the shell.
            string? localArt = ResolveBannerFile(game, iconPath, exe);
            await PaintAndLoadAsync(localArt);
            string? artFile = await EnsureGameArtFileAsync(game, iconPath, exe);
            if (!string.Equals(artFile, localArt, StringComparison.OrdinalIgnoreCase))
                await PaintAndLoadAsync(artFile);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Cartridge artwork '{title}': {ex}");
        }

        async Task PaintAndLoadAsync(string? artFile)
        {
            string stamp = "";
            if (!string.IsNullOrEmpty(artFile) && File.Exists(artFile))
            {
                var fi = new FileInfo(artFile);
                stamp = $"{Path.GetFileName(artFile)}-{fi.Length}-{fi.LastWriteTimeUtc.Ticks}";
            }
            GameHeader header = GameLook.Header(exe);
            string hash = HashString($"{GridShellCacheVersion}|{title}|{launcher}|{code}|{stamp}|{CardSkin.Current.Key}|{header.Signature}");
            string cachedPng = Path.Combine(GridShellCacheDir, $"shell-{hash}.png");

            if (!File.Exists(cachedPng) || !IsValidImageFile(cachedPng))
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(GridShellCacheDir);
                    var plastic = CartridgeModel.PlasticColor(title, artFile);
                    using var bmp = CartridgeModel.PaintFace(title, launcher, code, artFile, plastic, flat: false, header: header);
                    string temp = cachedPng + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        bmp.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
                        File.Move(temp, cachedPng, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                });
            }

            if (File.Exists(cachedPng))
            {
                // Stream loading verifies the actual decode, not just the PNG signature.
                using var stream = File.OpenRead(cachedPng).AsRandomAccessStream();
                var bi = new BitmapImage { DecodePixelWidth = 360 };
                await bi.SetSourceAsync(stream);
                target.Source = bi;
            }
        }
    }

    /// <summary>
    /// Resuelve o descarga el archivo de imagen local del juego (banner de Steam/Battle.net,
    /// banner del catálogo de Epic o ícono extraído del ejecutable) para alimentar tanto
    /// el cartucho de la grilla como el modelo 3D.
    /// </summary>
    private static async Task<string?> EnsureGameArtFileAsync(InstalledGame? game, string? exePath, string? exe = null)
    {
        try
        {
            // La carátula propia manda sobre el banner de la tienda y el ícono del exe.
            string? custom = GameLook.CustomArt(exe ?? game?.ExeFileName
                ?? (string.IsNullOrWhiteSpace(exePath) ? null : Path.GetFileName(exePath)));
            if (custom != null) return custom;

            if (game != null && !string.IsNullOrEmpty(game.AppId))
            {
                if (!string.IsNullOrEmpty(game.BannerUrl))
                {
                    string file = Path.Combine(BannerCacheDir, $"{game.AppId}.jpg");
                    if (!File.Exists(file))
                    {
                        try
                        {
                            Directory.CreateDirectory(BannerCacheDir);
                            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                            var bytes = await http.GetByteArrayAsync(game.BannerUrl);
                            if (IsWebp(bytes))
                            {
                                var jpeg = await ConvertWebpToJpegAsync(bytes);
                                if (jpeg != null) File.WriteAllBytes(file, jpeg);
                            }
                            else if (IsValidImageBytes(bytes))
                            {
                                File.WriteAllBytes(file, bytes);
                            }
                        }
                        catch { }
                    }
                    if (File.Exists(file) && IsValidImageFile(file)) return file;
                }
                else if (game.Launcher == "Epic" && !string.IsNullOrEmpty(game.ArtNamespace))
                {
                    string file = Path.Combine(BannerCacheDir, $"epic-{game.ArtNamespace}-{game.AppId}.jpg");
                    if (!File.Exists(file))
                    {
                        try
                        {
                            string? url = FindEpicKeyImageUrl(game.ArtNamespace, game.AppId);
                            if (url != null)
                            {
                                Directory.CreateDirectory(BannerCacheDir);
                                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                                var bytes = await http.GetByteArrayAsync(url);
                                if (IsValidImageBytes(bytes)) File.WriteAllBytes(file, bytes);
                            }
                        }
                        catch { }
                    }
                    if (File.Exists(file) && IsValidImageFile(file)) return file;
                }
            }

            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                string cacheDir = Path.Combine(BannerCacheDir, "exeicons-v2");
                string cacheBase = Path.Combine(cacheDir, HashString(exePath));
                string icoFile = cacheBase + "-ico.png";
                string hiFile = cacheBase + "-hi.png";
                string smallFile = cacheBase + "-small.png";

                if (File.Exists(icoFile) && IsValidImageFile(icoFile)) return icoFile;
                if (File.Exists(hiFile) && IsValidImageFile(hiFile)) return hiFile;

                // Si no hay ícono cacheado todavía, extraerlo ahora (igual que LoadExeIconAsync)
                string? extracted = await Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(cacheDir);
                        string? local = IconExtractor.FindConfidentLocalIcon(exePath);
                        if (local != null)
                        {
                            try
                            {
                                using var ico = new System.Drawing.Icon(local, 64, 64);
                                using var srcIco = ico.ToBitmap();
                                using var bmpIco = new System.Drawing.Bitmap(64, 64);
                                using var gIco = System.Drawing.Graphics.FromImage(bmpIco);
                                gIco.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                gIco.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                                gIco.Clear(System.Drawing.Color.Transparent);
                                gIco.DrawImage(srcIco, 0, 0, 64, 64);
                                bmpIco.Save(icoFile, System.Drawing.Imaging.ImageFormat.Png);
                                if (File.Exists(icoFile) && IsValidImageFile(icoFile)) return icoFile;
                            }
                            catch { }
                        }

                        System.Drawing.Bitmap? src = IconExtractor.ExtractHighResIcon(exePath);
                        if (src == null)
                        {
                            using var small = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                            if (small != null) src = small.ToBitmap();
                        }
                        if (src != null)
                        {
                            using (src)
                            using (var bmp = new System.Drawing.Bitmap(64, 64))
                            {
                                using var g = System.Drawing.Graphics.FromImage(bmp);
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                                g.Clear(System.Drawing.Color.Transparent);
                                g.DrawImage(src, 0, 0, 64, 64);
                                string targetPath = src.Width >= 64 && src.Height >= 64 ? hiFile : smallFile;
                                bmp.Save(targetPath, System.Drawing.Imaging.ImageFormat.Png);
                                if (File.Exists(targetPath) && IsValidImageFile(targetPath)) return targetPath;
                            }
                        }
                    }
                    catch { }
                    return null;
                });
                if (!string.IsNullOrEmpty(extracted)) return extracted;
                if (File.Exists(smallFile) && IsValidImageFile(smallFile)) return smallFile;
            }
        }
        catch { }
        return ResolveBannerFile(game, exePath, exe);
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
    /// <summary>
    /// Ruta local del banner ya cacheado de un juego (para mostrarlo en el cartucho 3D).
    /// Devuelve null si todavía no hay banner en disco (el cartucho muestra el plástico).
    /// Misma convención que LoadBannerAsync/LoadEpicBannerAsync.
    /// </summary>
    private static string? ResolveBannerFile(InstalledGame? game, string? exePath = null, string? exe = null)
    {
        try
        {
            // Carátula elegida por el usuario: se resuelve antes que cualquier arte de tienda.
            string? custom = GameLook.CustomArt(exe ?? game?.ExeFileName
                ?? (string.IsNullOrWhiteSpace(exePath) ? null : Path.GetFileName(exePath)));
            if (custom != null) return custom;

            if (game != null)
            {
                if (!string.IsNullOrEmpty(game.BannerUrl) && !string.IsNullOrEmpty(game.AppId))
                {
                    var f = Path.Combine(BannerCacheDir, $"{game.AppId}.jpg");
                    if (File.Exists(f) && IsValidImageFile(f)) return f;
                }
                if (game.Launcher == "Epic" && !string.IsNullOrEmpty(game.AppId) && !string.IsNullOrEmpty(game.ArtNamespace))
                {
                    var f = Path.Combine(BannerCacheDir, $"epic-{game.ArtNamespace}-{game.AppId}.jpg");
                    if (File.Exists(f) && IsValidImageFile(f)) return f;
                }
            }
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                string cacheDir = Path.Combine(BannerCacheDir, "exeicons-v2");
                string cacheBase = Path.Combine(cacheDir, HashString(exePath));
                foreach (var suffix in new[] { "-ico.png", "-hi.png", "-small.png" })
                {
                    var f = cacheBase + suffix;
                    if (File.Exists(f) && IsValidImageFile(f)) return f;
                }
            }
        }
        catch { }
        return null;
    }

    // =====================================================================
    // ARRASTRE FÍSICO DE CARTUCHOS + CONSOLA / DOCK INFERIOR (estilo Carthage)
    // =====================================================================

    private sealed class ActiveCartridgeDrag
    {
        public required Border CardElement;
        public required UIElement SourceVisual;
        public required Grid ProxyRoot;
        public required RotateTransform SwingTransform;
        public required ScaleTransform ScaleTransform;
        public required Border HintBadge;
        public required TextBlock HintText;
        public required InstalledGame? Game;
        public required string Exe;
        public required string Name;
        public required string? InstallPath;
        public required bool IsManual;
        public required double GrabX;
        public required double GrabY;
        public required double CardW;
        public required double CardH;
        public required Windows.Foundation.Point HomePos;
        public ImageSource? Artwork;
        public double Vx;
        public long LastTicks;
        public bool OverDock;
        /// <summary>Escala del proxy al aterrizar en el hueco de la biblioteca.</summary>
        public double HomeScale = 1;
        /// <summary>El arrastre salió del cartucho conectado en el dock (retiro).</summary>
        public bool FromDock;
    }

    private ActiveCartridgeDrag? _activeDrag;
    private ActiveCartridgeDrag? _connectedCartridge;
    private ActiveCartridgeDrag? _flightDrag;
    private Storyboard? _cartridgeStoryboard;
    private bool _cartridgeFlight;
    private bool _dockLaunching;
    private bool _dockLaunchFailed;
    private string? _dockMessage;
    private Border? _dockSlotBorder;
    private Canvas? _dockCartridgeBay;
    // All insertion positions share the same mouth coordinate. The cartridge can
    // protrude above the footer without reserving an empty row in the library.
    private const double DockFooterHeight = 66;
    private const double DockMouthY = 8; // shell border (1) + top padding (7): la boca coincide
    // con el pozo pintado del chasis (y=7..33); con 11 quedaba 3 px por debajo del receso.
    private const double DockSlotDepth = 20; // cuánto entra el cartucho dentro del pozo antes del labio frontal
    private const double DockApproachGap = 6;
    private const double DockLipWall = 9; // paredes laterales del labio frontal
    private const double DockBaySideGap = 12; // margen del pozo a cada lado del cartucho
    // El cartucho del dock mide SIEMPRE lo mismo que la card 3D de la grilla: la ranura
    // (boca, labio y pozo) se deriva de ese ancho en vez de usar medidas fijas. Así la
    // card entra en una ranura de su propio tamaño y no queda más grande que la boca,
    // sin importar el ancho de la ventana (UpdateCardWidth recalcula _dockCardWidth al
    // redimensionar y el dock se reconstruye con la medida nueva).
    private double _dockCardWidth = MaxCardWidth3D - 8;
    private double DockCardWidth => _dockCardWidth;
    // Whole DIPs avoid layout-rounding offsets between the bay clip and slot lip.
    private double DockCardHeight => Math.Ceiling(DockCardWidth * CartridgeHeightRatio);
    /// <summary>Ancho exterior del conector: el cartucho entra justo entre las dos paredes.</summary>
    private double DockPortWidth => DockCardWidth + 2 * DockLipWall;
    private double DockBayWidth => DockPortWidth + 2 * DockBaySideGap;
    private double DockBayHeight => DockCardHeight + DockApproachGap;
    private double DockCardLeft => (DockBayWidth - DockCardWidth) * 0.5;
    /// <summary>Cuánto asoma el cartucho por encima de la ranura (~15% de la altura).
    /// La torre tiene que caber en el aire bajo la última fila (44 DIP): con 0.25
    /// llegaba a 72 DIP y se veía recortada en tiras detrás de las cards de abajo.
    /// La boca, el labio y el pozo no cambian (derivan del ancho), entra más adentro.</summary>
    private double DockProtrusion => Math.Round(DockCardHeight * 0.15);
    private double DockSeatedTop => DockBayHeight - (DockProtrusion + DockSlotDepth);
    private readonly List<(string Exe, UIElement Visual)> _cartridgeVisuals = new();

    private static void SetCartridgeRemoved(UIElement visual, bool removed)
    {
        DependencyObject? owner = visual;
        while (owner != null && owner is not CartridgeTile)
            owner = VisualTreeHelper.GetParent(owner);
        if (owner is CartridgeTile tile) tile.SetRemoved(removed);
        else visual.Opacity = removed ? 0 : 1;
    }

    private (string LaunchFile, string LaunchArgs, string? BlizzardCode, string? LauncherProc, bool LauncherFound, bool AutoOpen, bool CanLaunch)
        ResolveGameLaunchSpec(InstalledGame? game, string exe, string? installPath, bool isManual)
    {
        string cacheKey = (installPath ?? "") + "|" + exe;
        if (!_exeCache.TryGetValue(cacheKey, out var cached))
        {
            string? ep = GameLauncher.FindExePath(installPath ?? "", exe);
            if (string.IsNullOrWhiteSpace(ep) || !File.Exists(ep))
                ep = GameExeResolver.FindMainExePath(installPath ?? "");
            string? ip = GameExeResolver.IsStubExe(ep ?? "") && !string.IsNullOrEmpty(installPath)
                ? GameExeResolver.FindBestGameExePath(installPath)
                : ep;
            cached = (ep, ip);
            _exeCache[cacheKey] = cached;
        }
        string? exePath = cached.ExePath;

        bool steamLaunch = game?.Launcher == "Steam" && !string.IsNullOrEmpty(game?.AppId);
        bool riotLaunch = game?.Launcher == "Riot" && !string.IsNullOrEmpty(game?.AppId);
        bool blacksmithLaunch = game?.Launcher == "Blacksmith";

        string? manualLauncher = null;
        if (isManual && game == null)
        {
            manualLauncher = _processService.GetManualGameLauncher(exe);
            if (!string.IsNullOrEmpty(manualLauncher))
            {
                blacksmithLaunch = string.Equals(manualLauncher, "Blacksmith", StringComparison.OrdinalIgnoreCase);
                // Manual games have no AppId: use their resolved exe instead of game!.AppId.
                steamLaunch = false;
                riotLaunch = false;
            }
        }

        string? blizzardCode = game?.Launcher == "Blizzard"
            ? (string.IsNullOrEmpty(game.AppId) ? GameLauncher.GetBlizzardProductCode(exe) : game.AppId)
            : null;

        string launchFile;
        string launchArgs = "";
        if (steamLaunch)
        {
            launchFile = $"steam://rungameid/{game!.AppId}";
        }
        else if (game?.Launcher == "Epic" && !string.IsNullOrEmpty(game.EpicAppName))
        {
            // Misma URI canónica que el botón de la card (namespace + catalogItemId +
            // AppName): la forma corta ya no la aceptan las versiones nuevas.
            launchFile = GameLauncher.BuildEpicLaunchUri(game.ArtNamespace, game.AppId, game.EpicAppName);
        }
        else if (game?.Launcher == "Xbox" && !string.IsNullOrEmpty(game.AppId))
        {
            launchFile = $"shell:AppsFolder\\{game.AppId}";
        }
        else if (game?.Launcher == "Ubisoft" && !string.IsNullOrEmpty(game.AppId))
        {
            launchFile = $"uplay://launch/{game.AppId}/0";
        }
        else if (riotLaunch)
        {
            launchFile = GameLauncher.FindRiotLauncher() ?? "";
            launchArgs = $"--launch-product={game!.AppId} --launch-patchline=live";
        }
        else
        {
            launchFile = exePath ?? "";
        }

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
            // Epic no depende de encontrar el exe del launcher: la URI del juego la
            // abre el protocolo registrado del launcher (y GameLauncher la entrega
            // al shell del usuario, porque la app corre elevada).
            launcherFound = true;
        }
        else if (gogLaunch)
        {
            launcherProc = "GalaxyClient";
            launcherFound = GameLauncher.FindGogLauncher() != null;
            autoOpen = false;
        }
        else if (xboxLaunch)
        {
            launcherProc = "Xbox";
            launcherFound = true;
            autoOpen = false;
        }
        else if (riotLaunch)
        {
            launcherProc = "RiotClientServices";
            launcherFound = GameLauncher.FindRiotLauncher() != null;
        }
        else if (!blacksmithLaunch && !string.IsNullOrEmpty(manualLauncher)
                 && !string.Equals(manualLauncher, "Blacksmith", StringComparison.OrdinalIgnoreCase))
        {
            launcherProc = manualLauncher;
            launcherFound = _processService.IsLauncherRunning(manualLauncher);
            autoOpen = true;
        }

        bool canLaunch = launcherFound && (autoOpen || launcherProc == null || IsLauncherRunning(launcherProc))
            && (!string.IsNullOrEmpty(launchFile) || blizzardCode != null);
        return (launchFile, launchArgs, blizzardCode, launcherProc, launcherFound, autoOpen, canLaunch);
    }

    private void StartCartridgeDrag(
        Border card, UIElement sourceVisual, ImageSource? imgSource,
        InstalledGame? game, string exe, string name, string? installPath, bool isManual,
        double grabX, double grabY, Windows.Foundation.Point pointerInRoot,
        double cardWOverride = 0, double homeScale = 1, bool fromDock = false)
    {
        if (_cartridgeFlight || _dockLaunching) return;
        // cardWOverride: el arrastre que sale del dock usa el tamaño del cartucho
        // sentado y crece hasta su tamaño de biblioteca (homeScale) al volver a casa.
        double cardW = cardWOverride > 0 ? cardWOverride : Math.Max(1, card.ActualWidth - 8);
        double cardH = cardW * CartridgeHeightRatio;

        Windows.Foundation.Point homePos = new(pointerInRoot.X - grabX * cardW, pointerInRoot.Y - grabY * cardH);
        try
        {
            var t = sourceVisual.TransformToVisual(RootGrid);
            homePos = t.TransformPoint(new Windows.Foundation.Point(0, 0));
        }
        catch { }

        // Ocultar el cartucho en la celda para dejar visible el hueco con el nombre grabado
        SetCartridgeRemoved(sourceVisual, true);

        var swingTransform = new RotateTransform { CenterX = grabX * cardW, CenterY = grabY * cardH };
        var scaleTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        var tg = new TransformGroup();
        tg.Children.Add(scaleTransform);
        tg.Children.Add(swingTransform);

        var proxyImg = new Image
        {
            Source = imgSource,
            Width = cardW,
            Height = cardH,
            Stretch = Stretch.Fill
        };

        var hintText = new TextBlock
        {
            Text = I18n.T("Soltá para conectar e iniciar"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeBrushes.Get("AccentForegroundBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var hintBadge = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = ThemeBrushes.Get("AccentBrush"),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 5, 12, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -36, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new FontIcon
                    {
                        Glyph = "\uE768",
                        FontSize = 11,
                        Foreground = ThemeBrushes.Get("AccentForegroundBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    hintText
                }
            }
        };

        var proxyVisual = CartridgeTile.CreateVisual(
            name, string.IsNullOrWhiteSpace(game?.Launcher) ? "Manual" : game.Launcher, proxyImg);
        // El proxy arrastrable tampoco proyecta sombra (coherente con la grilla).
        var proxyHost = new Border
        {
            CornerRadius = new CornerRadius(10),
            Child = proxyVisual
        };
        var proxyRoot = new Grid
        {
            Width = cardW,
            Height = cardH,
            RenderTransform = tg,
            IsHitTestVisible = false,
            Children =
            {
                proxyHost,
                hintBadge
            }
        };

        double nx = pointerInRoot.X - grabX * cardW;
        double ny = pointerInRoot.Y - grabY * cardH;
        Canvas.SetLeft(proxyRoot, nx);
        Canvas.SetTop(proxyRoot, ny);
        CartridgeDragLayer.Children.Add(proxyRoot);

        _activeDrag = new ActiveCartridgeDrag
        {
            CardElement = card,
            SourceVisual = sourceVisual,
            ProxyRoot = proxyRoot,
            SwingTransform = swingTransform,
            ScaleTransform = scaleTransform,
            HintBadge = hintBadge,
            HintText = hintText,
            Game = game,
            Exe = exe,
            Name = name,
            InstallPath = installPath,
            IsManual = isManual,
            GrabX = grabX,
            GrabY = grabY,
            CardW = cardW,
            CardH = cardH,
            HomePos = homePos,
            Artwork = imgSource,
            LastTicks = Stopwatch.GetTimestamp(),
            HomeScale = homeScale,
            FromDock = fromDock
        };

        HighlightDockDropTarget(false);
    }

    private void MoveCartridgeDrag(Windows.Foundation.Point pointerInRoot)
    {
        var d = _activeDrag;
        if (d == null) return;

        double oldX = Canvas.GetLeft(d.ProxyRoot);
        double nx = pointerInRoot.X - d.GrabX * d.CardW;
        double ny = pointerInRoot.Y - d.GrabY * d.CardH;

        long now = Stopwatch.GetTimestamp();
        double dtMs = Math.Max(1.0, (now - d.LastTicks) * 1000.0 / Stopwatch.Frequency);
        d.LastTicks = now;

        // Suavizado de velocidad horizontal y balanceo (-vx * 0.35, clamp ±9°) igual que CardDrag.qml
        d.Vx = d.Vx * 0.6 + 0.4 * ((nx - oldX) / dtMs * 16.0);
        double swing = Math.Clamp(-d.Vx * 0.38, -11.0, 11.0);
        d.SwingTransform.Angle = swing;

        Canvas.SetLeft(d.ProxyRoot, nx);
        Canvas.SetTop(d.ProxyRoot, ny);

        // Para retirar el montado la zona es la boca exacta (ver IsPointOverCartridgeMouth):
        // el highlight y la suelta usan la misma medida, sin sorpresas.
        bool overDock = d.FromDock
            ? IsPointOverCartridgeMouth(pointerInRoot)
            : IsPointOverCartridgeDock(pointerInRoot);
        if (overDock != d.OverDock)
        {
            d.OverDock = overDock;
            d.ScaleTransform.ScaleX = 1;
            d.ScaleTransform.ScaleY = 1;
            d.HintBadge.Visibility = overDock ? Visibility.Visible : Visibility.Collapsed;
            HighlightDockDropTarget(overDock);
        }
    }

    private void EndCartridgeDrag(Windows.Foundation.Point? pointerInRoot)
    {
        var d = _activeDrag;
        if (d == null) return;
        _activeDrag = null;
        HighlightDockDropTarget(false);

        // El montado solo se reinserta sobre la boca exacta: soltarlo en la torre o al
        // costado lo retira (antes la zona generosa lo relanzaba con un arrastre corto).
        bool dropInDock = pointerInRoot.HasValue && (d.FromDock
            ? IsPointOverCartridgeMouth(pointerInRoot.Value)
            : IsPointOverCartridgeDock(pointerInRoot.Value));
        d.HintBadge.Visibility = Visibility.Collapsed;
        d.SwingTransform.Angle = 0;
        _cartridgeFlight = true;
        _flightDrag = d;
        if (dropInDock)
        {
            var spec = ResolveGameLaunchSpec(d.Game, d.Exe, d.InstallPath, d.IsManual);
            if (!spec.CanLaunch)
            {
                StatusText.Text = I18n.T("Ejecutable no encontrado");
                StatusText.Foreground = Feedback.WarningBrush;
                StatusText.Visibility = Visibility.Visible;
                ReturnCartridgeHome(d);
                return;
            }
            // Align above the mouth, slide INTO the well behind the front lip, then settle into the latch.
            // Launch only after every phase finishes; cancellation stops the same storyboard.
            var slot = GetDockSlotCenterInRoot();
            double scale = DockCardWidth / d.CardW;
            double x = slot.X - DockCardWidth * 0.5;
            double y = slot.Y + DockSlotDepth - DockBayHeight;
            AnimateProxyTo(d.ProxyRoot, d.ScaleTransform, x, y, scale, 260,
                new CubicEase { EasingMode = EasingMode.EaseOut }, () =>
                {
                    if (!ReferenceEquals(_flightDrag, d) || _dockCartridgeBay == null) return;
                    CartridgeDragLayer.Children.Remove(d.ProxyRoot);
                    _dockCartridgeBay.Children.Clear();
                    _dockCartridgeBay.Children.Add(d.ProxyRoot);
                    Canvas.SetLeft(d.ProxyRoot, DockCardLeft);
                    Canvas.SetTop(d.ProxyRoot, 0);
                    AnimateProxyTo(d.ProxyRoot, d.ScaleTransform, DockCardLeft, DockSeatedTop + 3, scale, 300,
                        new CubicEase { EasingMode = EasingMode.EaseInOut }, () =>
                        {
                            if (!ReferenceEquals(_flightDrag, d)) return;
                            AnimateProxyTo(d.ProxyRoot, d.ScaleTransform, DockCardLeft, DockSeatedTop, scale, 110,
                                new CubicEase { EasingMode = EasingMode.EaseOut }, () =>
                                {
                                    if (!ReferenceEquals(_flightDrag, d)) return;
                                    _flightDrag = null;
                                    _cartridgeFlight = false;
                                    _cartridgeStoryboard = null;
                                    _connectedCartridge = d;
                                    _dockLaunchFailed = false;
                                    _dockMessage = null;
                                    _dockLaunching = !_runningExes.Contains(d.Exe);
                                    UpdateCartridgeDock();
                                    _loggingService.LogInfo($"Cartridge connected: {d.Exe}; starting after insertion.");
                                    if (_dockLaunching) _ = LaunchConnectedCartridgeAsync(d);
                                });
                        });
                });
            return;
        }
        ReturnCartridgeHome(d);
    }

    private void ReturnCartridgeHome(ActiveCartridgeDrag d)
    {
        AnimateProxyTo(d.ProxyRoot, d.ScaleTransform, d.HomePos.X, d.HomePos.Y, d.HomeScale, 340,
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }, () =>
            {
                if (!ReferenceEquals(_flightDrag, d)) return;
                CartridgeDragLayer.Children.Remove(d.ProxyRoot);
                SetCartridgeRemoved(d.SourceVisual, false);
                _flightDrag = null;
                _cartridgeFlight = false;
                _cartridgeStoryboard = null;
                if (d.FromDock)
                {
                    // El cartucho salió del dock para volver a su hueco: queda
                    // desconectado y la consola vuelve al estado "lista".
                    _dockMessage = null;
                    _dockLaunchFailed = false;
                    UpdateCartridgeDock();
                }
            });
    }

    private async Task LaunchConnectedCartridgeAsync(ActiveCartridgeDrag d)
    {
        try
        {
            var spec = ResolveGameLaunchSpec(d.Game, d.Exe, d.InstallPath, d.IsManual);
            await GameLauncher.LaunchGameAsync(_gameBoostService, _processService, _loggingService,
                spec.LaunchFile, spec.LaunchArgs, spec.BlizzardCode, d.InstallPath, d.Exe,
                d.Game?.Launcher ?? (d.IsManual ? _processService.GetManualGameLauncher(d.Exe) : null),
                (message, kind) =>
                {
                    if (!ReferenceEquals(_connectedCartridge, d)) return;
                    if (kind == LaunchStatusKind.Warning) _dockLaunchFailed = true;
                    if (kind != LaunchStatusKind.Hide) _dockMessage = message;
                    StatusText.Text = message;
                    StatusText.Foreground = kind == LaunchStatusKind.Warning ? Feedback.WarningBrush : Feedback.MutedBrush;
                    StatusText.Visibility = kind == LaunchStatusKind.Hide ? Visibility.Collapsed : Visibility.Visible;
                    UpdateCartridgeDock();
                });
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Cartridge launch '{d.Name}': {ex.Message}");
            _dockLaunchFailed = true;
            _dockMessage = ex.Message;
        }
        finally
        {
            _dockLaunching = false;
            UpdateCartridgeDock();
        }
    }

    /// <summary>
    /// Retira el cartucho conectado ARRASTRÁNDOLO hacia afuera del dock (sólo cuando
    /// el juego ya se detectó como cerrado). Reutiliza el mismo proxy de arrastre de
    /// la biblioteca: soltarlo sobre la ranura lo vuelve a conectar e iniciar, y
    /// soltarlo en cualquier otro lado lo devuelve a su hueco de la biblioteca.
    /// </summary>
    private void BeginDockCartridgePull(CartridgeTile seat, Windows.Foundation.Point pointInRoot, Windows.Foundation.Point grab)
    {
        var c = _connectedCartridge;
        if (c == null || _activeDrag != null || _cartridgeFlight || _dockLaunching) return;
        if (_runningExes.Contains(c.Exe)) return; // juego abierto: el cartucho queda clavado

        var source = _cartridgeVisuals.FirstOrDefault(x => string.Equals(x.Exe, c.Exe, StringComparison.OrdinalIgnoreCase));
        var entry = _cards.FirstOrDefault(x => x.Body is CartridgeTile t && ReferenceEquals(t.CartridgeVisual, source.Visual));
        if (source.Visual == null || entry.Card == null) return;

        // Desconectar en el estado SIN reconstruir el dock: el asiento sigue vivo
        // (oculto) para conducir el gesto hasta que se suelte el puntero.
        _connectedCartridge = null;
        _dockMessage = null;
        _dockLaunchFailed = false;
        seat.Opacity = 0;

        StartCartridgeDrag(entry.Card, source.Visual, c.Artwork, c.Game, c.Exe, c.Name,
            c.InstallPath, c.IsManual, grab.X, grab.Y, pointInRoot,
            cardWOverride: DockCardWidth,
            homeScale: Math.Max(1, entry.Card.ActualWidth - 8) / DockCardWidth,
            fromDock: true);
    }

    private void CancelCartridgeDragImmediate()
    {
        var drag = _activeDrag;
        var flight = _flightDrag;
        bool pulledFromDock = drag?.FromDock == true || flight?.FromDock == true;
        _activeDrag = null;
        _flightDrag = null;
        _cartridgeFlight = false;
        _cartridgeStoryboard?.Stop();
        _cartridgeStoryboard = null;
        foreach (var d in new[] { drag, flight })
        {
            if (d == null) continue;
            CartridgeDragLayer.Children.Remove(d.ProxyRoot);
            _dockCartridgeBay?.Children.Remove(d.ProxyRoot);
            SetCartridgeRemoved(d.SourceVisual, false);
            if (d.CardElement.Child is CartridgeTile tile) tile.CancelGesture();
        }
        HighlightDockDropTarget(false);
        // Si el arrastre cancelado había salido del dock, restaurar su asiento/estado.
        if (pulledFromDock) UpdateCartridgeDock();
    }

    private void AnimateProxyTo(
        UIElement proxy, ScaleTransform scale, double toX, double toY, double toScale,
        int durationMs, EasingFunctionBase easing, Action onCompleted)
    {
        var dur = new Duration(TimeSpan.FromMilliseconds(durationMs));
        var sb = new Storyboard();

        var animX = new DoubleAnimation { To = toX, Duration = dur, EasingFunction = easing, EnableDependentAnimation = true };
        Storyboard.SetTarget(animX, proxy);
        Storyboard.SetTargetProperty(animX, "(Canvas.Left)");
        sb.Children.Add(animX);

        var animY = new DoubleAnimation { To = toY, Duration = dur, EasingFunction = easing, EnableDependentAnimation = true };
        Storyboard.SetTarget(animY, proxy);
        Storyboard.SetTargetProperty(animY, "(Canvas.Top)");
        sb.Children.Add(animY);

        var animSx = new DoubleAnimation { To = toScale, Duration = dur, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true };
        Storyboard.SetTarget(animSx, scale);
        Storyboard.SetTargetProperty(animSx, "ScaleX");
        sb.Children.Add(animSx);

        var animSy = new DoubleAnimation { To = toScale, Duration = dur, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true };
        Storyboard.SetTarget(animSy, scale);
        Storyboard.SetTargetProperty(animSy, "ScaleY");
        sb.Children.Add(animSy);

        sb.Completed += (s, e) =>
        {
            // Release held animation values before reparenting into the slot bay.
            sb.Stop();
            Canvas.SetLeft(proxy, toX);
            Canvas.SetTop(proxy, toY);
            scale.ScaleX = scale.ScaleY = toScale;
            onCompleted();
        };
        _cartridgeStoryboard = sb;
        sb.Begin();
    }

    private bool IsPointOverCartridgeDock(Windows.Foundation.Point ptInRoot)
    {
        if (_connectedCartridge != null || _dockLaunching || _dockSlotBorder == null
            || CartridgeDockHost.Visibility != Visibility.Visible || _dockSlotBorder.ActualWidth <= 0)
            return false;
        try
        {
            var t = _dockSlotBorder.TransformToVisual(RootGrid);
            var dockTopLeft = t.TransformPoint(new Windows.Foundation.Point(0, 0));
            double left = dockTopLeft.X - 28;
            double right = dockTopLeft.X + _dockSlotBorder.ActualWidth + 28;
            double top = dockTopLeft.Y - (DockProtrusion + 28);
            double bottom = dockTopLeft.Y + _dockSlotBorder.ActualHeight + 20;
            return ptInRoot.X >= left && ptInRoot.X <= right && ptInRoot.Y >= top && ptInRoot.Y <= bottom;
        }
        catch { return false; }
    }

    /// <summary>Boca EXACTA de la ranura (el pozo), sin la generosidad de
    /// <see cref="IsPointOverCartridgeDock"/>. Es la que vale para el cartucho que se está
    /// retirando del dock: apuntar a la boca lo vuelve a meter (y relanza); soltar en
    /// cualquier otro lado —la torre, el chasis, afuera— lo retira a su hueco. Así sacar
    /// el montado no exige arrastrar lejos: un tirón corto hacia afuera alcanza.</summary>
    private bool IsPointOverCartridgeMouth(Windows.Foundation.Point ptInRoot)
    {
        if (_dockLaunching || _dockSlotBorder == null
            || CartridgeDockHost.Visibility != Visibility.Visible || _dockSlotBorder.ActualWidth <= 0)
            return false;
        try
        {
            var t = _dockSlotBorder.TransformToVisual(RootGrid);
            var dockTopLeft = t.TransformPoint(new Windows.Foundation.Point(0, 0));
            const double padX = 10;
            const double padTop = 8;
            const double padBottom = 10;
            return ptInRoot.X >= dockTopLeft.X - padX
                && ptInRoot.X <= dockTopLeft.X + _dockSlotBorder.ActualWidth + padX
                && ptInRoot.Y >= dockTopLeft.Y - padTop
                && ptInRoot.Y <= dockTopLeft.Y + _dockSlotBorder.ActualHeight + padBottom;
        }
        catch { return false; }
    }

    private Windows.Foundation.Point GetDockSlotCenterInRoot()
    {
        try
        {
            FrameworkElement target = (FrameworkElement?)_dockSlotBorder ?? CartridgeDockHost;
            var t = target.TransformToVisual(RootGrid);
            return t.TransformPoint(new Windows.Foundation.Point(target.ActualWidth * 0.5, 0));
        }
        catch
        {
            return new Windows.Foundation.Point(RootGrid.ActualWidth * 0.5, RootGrid.ActualHeight - 48);
        }
    }

    private void UpdateCartridgeDockWidth()
    {
        // El dock ocupa todo el ancho inferior de la vista y queda pegado al fondo de
        // la ventana (HorizontalAlignment="Stretch", Margin="0" en XAML). Si la vista
        // es angosta, oculta el texto largo del Modo rendimiento para no apretar la ranura.
        if (RootGrid.ActualWidth <= 0) return;
        CartridgeDockHost.ClearValue(FrameworkElement.WidthProperty);
        if (_cards3D && GameBoostCard.Parent != null && GameBoostCard.Parent != HeaderPanel)
        {
            bool compact = RootGrid.ActualWidth < 760;
            GameBoostLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void HighlightDockDropTarget(bool active)
    {
        if (_dockSlotBorder == null) return;
        _dockSlotBorder.BorderBrush = active
            ? ThemeBrushes.Get("AccentBrush")
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 9, 11, 16));
    }

    /// <summary>
    /// Contenedores por los que pasó la card (el dock crea un rightWing nuevo en cada
    /// rebuild). Ver <see cref="DetachGameBoostCard"/>: WinUI puede desincronizar el
    /// Parent del elemento respecto de la colección que lo contiene, y sin esta lista
    /// no hay forma de soltarla (el próximo Add/Insert revienta con 0x800F1000).
    /// </summary>
    private readonly List<Panel> _gameBoostCardContainers = new();

    /// <summary>
    /// Desengancha la card "Modo juego de WinForge" de donde esté (header o dock).
    /// WinUI no permite un control con dos padres: hay que soltarla antes de moverla.
    /// OJO con confiar solo en <c>GameBoostCard.Parent</c>: medido en runtime (ver
    /// app.log, "enganchar card del Modo juego al dock") el Parent puede quedar null o
    /// apuntando a otro contenedor mientras el elemento sigue REGISTRADO como hijo de
    /// una colección; en ese estado cualquier Children.Add/Insert lanza la COMException
    /// 0x800F1000 ("No se han detectado componentes instalados", que en realidad es el
    /// "invalid operation" de XAML = "Element is already the child of another element").
    /// Por eso se barre por TODOS los contenedores conocidos, no solo por el Parent.
    /// </summary>
    private void DetachGameBoostCard()
    {
        // 1) Todos los contenedores por los que pasó la card (header + wings del dock).
        foreach (var panel in _gameBoostCardContainers)
            panel.Children.Remove(GameBoostCard);
        HeaderPanel.Children.Remove(GameBoostCard);

        // 2) Red final por si quedó enganchada en un contenedor no previsto.
        switch (GameBoostCard.Parent)
        {
            case Panel panel:
                panel.Children.Remove(GameBoostCard);
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, GameBoostCard):
                contentControl.Content = null;
                break;
            case Border border when ReferenceEquals(border.Child, GameBoostCard):
                border.Child = null;
                break;
        }
    }

    /// <summary>Registra un contenedor de la card para poder soltarla después.</summary>
    private void RememberGameBoostCardContainer(Panel panel)
    {
        foreach (var known in _gameBoostCardContainers)
            if (ReferenceEquals(known, panel)) return;
        _gameBoostCardContainers.Add(panel);
    }

    /// <summary>
    /// Excepción en una sola línea de log CON el detalle real de XAML. El COMException
    /// 0x800F1000 muestra "No se han detectado componentes instalados" (choque de
    /// códigos de error con SPAPI): el texto útil viene después, dos líneas más abajo
    /// en ToString(), y sin esto se perdía en el log.
    /// </summary>
    private static string DescribeEx(Exception ex)
        => ex.ToString().Replace("\r", " ").Replace("\n", " | ");

    /// <summary>
    /// Aplica el estilo visual a la card "Modo juego de WinForge":
    /// - En el dock inferior (modo 3D): chasis grabado/empotrado sobre el plástico de la
    ///   consola (CartridgeDock), con botones metálicos compactos y switch con acento LED.
    /// - En el header (modo Vanilla): restaura los pinceles, bordes y tamaños de tema.
    /// </summary>
    private void ApplyGameBoostCardStyle(bool inDock, bool compactDock = false)
    {
        bool wasInit = _boostSwitchInitialized;
        _boostSwitchInitialized = false;
        try
        {
            if (inDock)
            {
                CartridgeDock.StyleWell(GameBoostCard);
                GameBoostPanel.Spacing = 6;

                // En el dock inferior los controles se integran directo sobre el chasis
                // de la consola (sin div/card encima): etiqueta + "?" (tooltip) + tuerca + switch.
                GameBoostIcon.Visibility = Visibility.Collapsed;

                GameBoostLabel.FontSize = 11.5;
                GameBoostLabel.TextWrapping = TextWrapping.NoWrap;
                GameBoostLabel.Foreground = CartridgeDock.TextBrush();
                GameBoostLabel.Visibility = compactDock ? Visibility.Collapsed : Visibility.Visible;

                CartridgeDock.StyleButton(GameBoostInfoButton);
                GameBoostInfoButton.Width = 24;
                GameBoostInfoButton.Height = 24;
                GameBoostInfoButton.Padding = new Thickness(0);
                GameBoostInfoIcon.FontSize = 11;
                GameBoostInfoButton.Visibility = Visibility.Visible;

                CartridgeDock.StyleButton(GameBoostSettingsButton);
                GameBoostSettingsButton.Width = 24;
                GameBoostSettingsButton.Height = 24;
                GameBoostSettingsButton.Padding = new Thickness(0);
                GameBoostSettingsIcon.FontSize = 11;
                GameBoostSettingsText.Visibility = Visibility.Collapsed;
                ToolTipService.SetToolTip(GameBoostSettingsButton, I18n.T("Configuración"));
            }
            else
            {
                GameBoostCard.Tag = null;
                CartridgeDock.ClearDockResources(GameBoostCard);
                GameBoostCard.CornerRadius = new CornerRadius(10);
                GameBoostCard.Padding = new Thickness(14, 9, 14, 9);
                GameBoostCard.BorderThickness = new Thickness(0);
                GameBoostCard.ClearValue(Border.BorderBrushProperty);
                GameBoostCard.Background = ThemeBrushes.GetSurface("CardBackgroundBrush");
                GameBoostPanel.Spacing = 12;

                GameBoostIcon.Width = 28;
                GameBoostIcon.Height = 28;
                GameBoostIcon.Visibility = Visibility.Visible;

                GameBoostLabel.FontSize = 13;
                GameBoostLabel.TextWrapping = TextWrapping.Wrap;
                GameBoostLabel.Visibility = Visibility.Visible;
                GameBoostLabel.ClearValue(TextBlock.ForegroundProperty);

                CartridgeDock.ClearDockResources(GameBoostInfoButton);
                GameBoostInfoButton.Visibility = Visibility.Visible;
                GameBoostInfoButton.Width = 28;
                GameBoostInfoButton.Padding = new Thickness(0);
                GameBoostInfoButton.CornerRadius = new CornerRadius(14);
                GameBoostInfoButton.BorderThickness = new Thickness(1);
                GameBoostInfoButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                GameBoostInfoButton.BorderBrush = ThemeBrushes.Get("SensorGridLineBrush");
                GameBoostInfoButton.Foreground = ThemeBrushes.Get("MutedBrush");
                GameBoostInfoIcon.FontSize = 12;

                CartridgeDock.ClearDockResources(GameBoostSettingsButton);
                GameBoostSettingsButton.ClearValue(FrameworkElement.WidthProperty);
                GameBoostSettingsButton.ClearValue(FrameworkElement.HeightProperty);
                GameBoostSettingsButton.Padding = new Thickness(11, 5, 11, 5);
                GameBoostSettingsButton.CornerRadius = new CornerRadius(6);
                GameBoostSettingsButton.BorderThickness = new Thickness(1);
                GameBoostSettingsButton.Background = ThemeBrushes.Get("AccentTintBrush");
                GameBoostSettingsButton.BorderBrush = ThemeBrushes.Get("AccentBrush");
                GameBoostSettingsButton.Foreground = ThemeBrushes.Get("AccentBrush");
                GameBoostSettingsIcon.FontSize = 12;
                GameBoostSettingsText.FontSize = 12;
                GameBoostSettingsText.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(GameBoostSettingsButton, null);

                if (GameBoostSettingsButton.ActualHeight > 0)
                    GameBoostInfoButton.Height = Math.Max(1, GameBoostSettingsButton.ActualHeight);

                // El switch conserva su estilo propio (PerformanceSwitchStyle) en ambos
                // modos: la perilla rotatoria del ModeSwitch de Carthage, igual en header
                // y en el dock.
            }

            GameBoostSwitch.IsChecked = _gameBoostService.IsEnabled;
            UpdateGameBoostLabel();
        }
        finally
        {
            _boostSwitchInitialized = wasInit;
        }
    }

    /// <summary>
    /// Devuelve la card del Modo juego a su lugar en el header (modo Vanilla, sin dock).
    /// </summary>
    private void RestoreGameBoostCardHome()
    {
        bool wasInit = _boostSwitchInitialized;
        _boostSwitchInitialized = false;
        try
        {
            if (GameBoostCard.Parent != HeaderPanel)
            {
                DetachGameBoostCard();
                int idx = Math.Clamp(_gameBoostCardHomeIndex, 0, HeaderPanel.Children.Count);
                HeaderPanel.Children.Insert(idx, GameBoostCard);
            }
            ApplyGameBoostCardStyle(inDock: false);
        }
        finally
        {
            _boostSwitchInitialized = wasInit;
        }
    }

    /// <summary>
    /// Muda la card del Modo juego al ala derecha del dock sin romper nunca la
    /// página: si el reparentado falla (árbol visual en transición, doble padre),
    /// la card vuelve al header y el dock se arma igual sin ella. Sin esto, una
    /// excepción acá abortaba RebuildCards y dejaba el footer vacío.
    /// </summary>
    private void SafeMoveGameBoostCardTo(Panel parent, bool compactDock = false)
    {
        bool wasInit = _boostSwitchInitialized;
        _boostSwitchInitialized = false;
        try
        {
            // ENGANCHE y ESTILO son pasos separados a propósito: si el estilo falla,
            // la card YA está viviendo en el dock y sacarla de ahí (como hacía el catch
            // anterior) era justamente el "el Modo rendimiento desaparece del footer al
            // volver a la pestaña". Solo un fallo del enganche la devuelve al header.
            try
            {
                RememberGameBoostCardContainer(parent);
                DetachGameBoostCard();
                parent.Children.Add(GameBoostCard);
            }
            catch (Exception ex)
            {
                // Un reintento tras el barrido: si el estado venía corrupto de un fallo
                // anterior, el primer intento puede chocar con el registro viejo y el
                // segundo (tras soltarla de todos los contenedores) engancha derecho.
                try
                {
                    DetachGameBoostCard();
                    parent.Children.Add(GameBoostCard);
                }
                catch (Exception exRetry)
                {
                    _loggingService.LogWarning($"GestionarProcesosPage: enganchar card del Modo juego al dock: {DescribeEx(exRetry)} (previo: {ex.GetType().Name})");
                    try { RestoreGameBoostCardHome(); } catch (Exception ex2)
                    {
                        _loggingService.LogWarning($"GestionarProcesosPage: rescate tras fallo de enganche: {DescribeEx(ex2)}");
                    }
                    return;
                }
            }

            try
            {
                ApplyGameBoostCardStyle(inDock: true, compactDock: compactDock);
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"GestionarProcesosPage: estilizar card del Modo juego en el dock: {DescribeEx(ex)}");
            }
        }
        finally
        {
            _boostSwitchInitialized = wasInit;
        }
    }

    /// <summary>
    /// Restaura la card al header sin romper nunca la página (ver SafeMoveGameBoostCardTo).
    /// </summary>
    private void SafeRestoreGameBoostCardHome()
    {
        try { RestoreGameBoostCardHome(); }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"GestionarProcesosPage: restaurar card del Modo juego: {DescribeEx(ex)}");
        }
    }

    /// <summary>
    /// La card está en un árbol vivo (llega hasta el RootGrid) o quedó colgada
    /// de un panel descartado (un dock viejo ya limpiado). Parent != null NO
    /// alcanza: tras un Clear() el padre muerto sigue referenciado.
    /// </summary>
    private bool GameBoostCardInLiveTree()
    {
        try
        {
            DependencyObject? cur = GameBoostCard;
            while (cur != null)
            {
                if (ReferenceEquals(cur, RootGrid)) return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }
        catch
        {
            return GameBoostCard.Parent != null;
        }
    }

    /// <summary>
    /// Red de seguridad: si la card quedó sin árbol vivo (rebuild del dock
    /// interrumpido), se devuelve al header sin aplicar estilos (el Insert no
    /// falla; el estilo se re-aplica en el próximo rebuild exitoso). Garantiza
    /// que la card siempre esté visible en algún lado.
    /// </summary>
    private void EnsureGameBoostCardVisible()
    {
        if (GameBoostCardInLiveTree()) return;
        try
        {
            DetachGameBoostCard();
            int idx = Math.Clamp(_gameBoostCardHomeIndex, 0, HeaderPanel.Children.Count);
            HeaderPanel.Children.Insert(idx, GameBoostCard);
            try { ApplyGameBoostCardStyle(inDock: false); } catch { }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"GestionarProcesosPage: rescatar card del Modo juego: {DescribeEx(ex)}");
        }
    }

    /// <summary>
    /// Chequea si el dock ya está armado y la card vive en él (en el árbol vivo):
    /// en ese caso el rebuild es redundante y se salta. Sin esto, cada
    /// RequestCartridgeDockUpdate hacía Clear()+Add del host aunque nada hubiera
    /// cambiado — la card parpadeaba (aparece/desaparece) al cambiar de pestaña.
    /// </summary>
    private bool GameBoostCardSettledInDock()
    {
        if (!_cards3D) return false;
        if (CartridgeDockHost.Children.Count == 0) return false;
        if (CartridgeDockHost.Visibility != Visibility.Visible) return false;
        if (GameBoostCard.Parent == HeaderPanel) return false;
        if (!string.Equals(_dockSkinKey, CardSkin.Current.Key, StringComparison.Ordinal)) return false;
        return GameBoostCardInLiveTree();
    }

    /// <summary>
    /// Rebuild coalescado del dock: colapsa los N pedidos encadenados al volver
    /// a la pestaña (OnNavigatedTo + dispatcher diferido + RefreshAsync async +
    /// eventos WMI) en UN solo rebuild por tick del dispatcher. Sin esto cada
    /// rebuild hacía Clear()+Add del host y la card parpadeaba (aparece y
    /// desaparece al cambiar de pestaña).
    /// </summary>
    private void RequestCartridgeDockUpdate()
    {
        if (_dockUpdatePending) return;
        // Si la card ya vive en el dock armado, no hay nada que reconstruir:
        // el rebuild redundante hacía Clear()+Add y la card parpadeaba.
        if (GameBoostCardSettledInDock()) return;
        _dockUpdatePending = true;
        int gen = _navGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            _dockUpdatePending = false;
            // Si se cambió de pestaña en el medio, no reconstruir: el tick es de
            // la visita anterior y pisaría el dock recién armado (parpadeo).
            if (gen != _navGeneration) return;
            // Re-chequear al correr: otro pedido puede haberlo armado mientras
            // este esperaba su turno (el Clear()+Add redundante parpadea).
            if (GameBoostCardSettledInDock()) return;
            UpdateCartridgeDock();
        });
    }

    /// <summary>
    /// Construye / actualiza la consola inferior (Dock) estilo Carthage:
    /// - Ala izquierda: LED + estado ("Listo — Arrastrá un cartucho aquí para jugar" o
    ///   "Jugando <Juego>" cuando hay un cartucho insertado/corriendo).
    /// - Centro: bahía / ranura hundida donde se inserta el cartucho arrastrado (y donde
    ///   asoma la cabeza del cartucho activo).
    /// - Ala derecha: la card "Modo rendimiento de WinForge" (mudada desde el
    ///   header). El cartucho conectado se retira arrastrándolo hacia afuera (con el
    ///   juego cerrado) y un clic sobre él abre la inspección: no hay botones rápidos.
    /// </summary>
    private void UpdateCartridgeDock()
    {
        if (!_cards3D)
        {
            SafeRestoreGameBoostCardHome();
            CartridgeDockHost.Visibility = Visibility.Collapsed;
            return;
        }

        CartridgeDockHost.Visibility = Visibility.Visible;
        // Keep the bay stable while a cartridge is sliding behind its lip or while a
        // cartridge pulled out of the dock is still being dragged: the seated tile
        // drives the gesture and must survive until the pointer is released.
        // El guard va ANTES del try: ese return temprano no toca la card y no
        // necesita red de seguridad.
        if (_cartridgeFlight || _activeDrag is { FromDock: true }) return;
        // Rebuild ATÓMICO: el dock nuevo se construye en variables locales y el
        // host viejo (con la card adentro) sigue visible hasta el swap final de
        // abajo. Si la construcción falla a mitad, el host viejo queda intacto y
        // el catch devuelve la card al header: nunca queda sin un padre vivo
        // (ese era el "desaparece al volver de otra pestaña").
        try
        {

        // Buscar si hay algún juego corriendo (o el último insertado si sigue activo)
        string? activeExe = null;
        if (_connectedCartridge != null) activeExe = _connectedCartridge.Exe;
        foreach (var (exe, visual) in _cartridgeVisuals)
            SetCartridgeRemoved(visual, string.Equals(activeExe, exe, StringComparison.OrdinalIgnoreCase)
                || ReferenceEquals(_activeDrag?.SourceVisual, visual));

        InstalledGame? activeGame = null;
        string? activeName = null;
        string? activeInstallPath = null;
        bool activeIsManual = false;

        if (!string.IsNullOrEmpty(activeExe))
        {
            activeGame = _installed.FirstOrDefault(g => string.Equals(g.ExeFileName, activeExe, StringComparison.OrdinalIgnoreCase));
            if (activeGame != null)
            {
                activeName = activeGame.Name;
                activeInstallPath = activeGame.InstallPath;
            }
            else
            {
                var m = _manual.FirstOrDefault(x => string.Equals(x.Exe, activeExe, StringComparison.OrdinalIgnoreCase));
                activeName = m.Name ?? activeExe;
                activeInstallPath = m.InstallPath;
                activeIsManual = true;
            }
        }

        if (_connectedCartridge != null)
        {
            activeGame = _connectedCartridge.Game;
            activeName = _connectedCartridge.Name;
            activeInstallPath = _connectedCartridge.InstallPath;
            activeIsManual = _connectedCartridge.IsManual;
        }
        bool hasRunning = activeExe != null && _runningExes.Contains(activeExe);
        bool hasConnected = _connectedCartridge != null;

        // Indicador LED (verde con un cartucho puesto o jugando, gris en espera).
        // Con regla activa la luz sigue a LA REGLA (amarilla esperando, verde solo
        // aplicada): va en un host para cambiarla sin reconstruir la consola.
        _dockLedConnected = hasConnected;
        _dockLedRunning = hasRunning;
        _dockLedHost = new ContentControl
        {
            Content = BuildDockLed(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // Línea 1 (WINFORGE + estado): sin el aviso idle "Cartucho conectado"
        // (ruido: el cartucho a la vista ya lo dice). Con cartucho puesto y sin
        // actividad queda solo "WINFORGE". Lleva el estilo grande que antes tenía
        // la línea de abajo (intercambio pedido).
        string dockState = _dockLaunchFailed ? I18n.T("No se pudo iniciar") : hasRunning ? I18n.T("En ejecución")
            : _dockLaunching ? I18n.T("Cartucho conectado · iniciando") : hasConnected ? "" : I18n.T("Consola lista");
        var statusLine1 = new TextBlock
        {
            Text = string.IsNullOrEmpty(dockState) ? "WINFORGE" : "WINFORGE  /  " + dockState.ToUpperInvariant(),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = CartridgeDock.TextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // Texto base del footer. La segunda línea es la que reacciona al estado de
        // la regla (esperando detectar el juego detrás del launcher, aplicada, no
        // identificada): así el feedback se lee sin agregar otra luz al chasis.
        _dockDefaultStatus = hasConnected
            ? (_dockMessage ?? activeName!)
            : I18n.T("Arrastrá un cartucho a la ranura para iniciar");
        var statusLine2 = new TextBlock
        {
            Text = _dockDefaultStatus,
            FontSize = 9,
            CharacterSpacing = 60,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = CartridgeDock.TextBrush(secondary: true),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        };
        _dockStatusLine = statusLine2;

        var statusText = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { statusLine1, statusLine2 }
        };

        var leftWing = new Grid
        {
            ColumnSpacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _dockLedHost, statusText }
        };
        leftWing.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftWing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(statusText, 1);

        // El cartucho entra 20 DIP DENTRO del pozo de la ranura (por delante del fondo
        // oscuro del puerto y por detrás del labio frontal CreatePortFrontLip).
        _dockCartridgeBay = new Canvas
        {
            Width = DockBayWidth, Height = DockBayHeight,
            Margin = new Thickness(0, (DockMouthY + DockSlotDepth) - DockBayHeight, 0, 0),
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, DockBayWidth, DockBayHeight) },
            // El asiento del cartucho recibe el puntero: un clic abre la inspección y,
            // con el juego cerrado, se puede arrastrar el cartucho hacia afuera.
            IsHitTestVisible = true
        };
        if (hasConnected && _connectedCartridge is { } connected)
        {
            // Asiento INTERACTIVO (mismo CartridgeTile que las cards de la biblioteca,
            // a tamaño de dock): el cartucho conectado se puede agarrar y retirar.
            var seat = new CartridgeTile(activeName!, activeGame?.Launcher ?? "Manual")
            {
                Width = DockCardWidth,
                Height = DockCardHeight,
                CoordinateRoot = RootGrid,                    // Clavado: pasar el mouse por la ranura no lo mueve (ni tilt ni lift).
                    // Salir (arrastrar hacia afuera) e inspeccionar (clic) siguen vivos.
                    HoverTiltEnabled = false,
                // Manito solo cuando realmente se puede retirar: juego cerrado y consola
                // libre. Se evalúa en vivo al hover (el estado cambia sin rebuild).
                GrabHintAllowed = () => _activeDrag == null && !_cartridgeFlight && !_dockLaunching
                    && _connectedCartridge != null && !_runningExes.Contains(_connectedCartridge.Exe),
                CanInteract = () => _activeDrag == null && !_cartridgeFlight && !_dockLaunching
            };
            seat.Artwork.Source = connected.Artwork;
            // Sentado en la ranura: sin sombra propia (la sombra cálida del puerto ya la
            // dibuja el labio frontal). El asiento no lleva ThemeShadow.
            seat.DragStarted += (p, g) => BeginDockCartridgePull(seat, p, g);
            seat.DragMoved += MoveCartridgeDrag;
            seat.DragCompleted += EndCartridgeDrag;
            seat.InspectRequested += () => ShowGame3DDetail(activeGame, activeExe!, activeName!, activeInstallPath, activeIsManual);
            ToolTipService.SetToolTip(seat, I18n.T("Clic para inspeccionar y configurar · arrastrá hacia afuera para retirar"));
            Canvas.SetLeft(seat, DockCardLeft);
            Canvas.SetTop(seat, DockSeatedTop);
            _dockCartridgeBay.Children.Add(seat);
        }


        _dockSlotBorder = CartridgeDock.CreatePort(hasConnected, DockPortWidth);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_dockSlotBorder, "CartridgeSlot");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_dockSlotBorder, I18n.T("Ranura de cartucho"));
        // Ala derecha: la card del Modo rendimiento (mudada desde el header). Es el
        // único control del ala: los botones de acción sobre el cartucho se sacaron.
        // Alineada ARRIBA con un desfase de −2: centrada en la fila (45 DIP), la
        // perilla de 32 px terminaba justo sobre la costura moldeada del chasis, que
        // se pinta en y=49 de los 66 DIP del dock (PaintShell: "seam"). Con la perilla
        // de 38 px de alto desde y=9, el borde inferior del dial queda en y=44 y la
        // costura respira (5 DIP de aire) sin descolgar el switch de la ranura.
        var rightWing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        // La card del Modo rendimiento vive acá en modo 3D: es el MISMO control del
        // header (switch, ?, Configuración y tooltip intactos), mudado de padre. Es el
        // único control del ala: los botones "Parámetros" y "Retirar cartucho" se
        // sacaron (el cartucho sale arrastrándolo y un clic sobre él inspecciona).
        SafeMoveGameBoostCardTo(rightWing, compactDock: RootGrid.ActualWidth > 0 && RootGrid.ActualWidth < 760);
        var port = new Grid { Width = DockPortWidth, Height = 44, Children = { _dockSlotBorder } };
        _dockSlotBorder.VerticalAlignment = VerticalAlignment.Top;
        var dockRow = new Grid { ColumnSpacing = 12 };
        dockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(port, 1);
        Grid.SetColumn(rightWing, 2);
        dockRow.Children.Add(leftWing);
        dockRow.Children.Add(port);
        dockRow.Children.Add(rightWing);

        var dockShell = CartridgeDock.CreateShell(dockRow, DockFooterHeight, DockPortWidth);
        var frontLip = CartridgeDock.CreatePortFrontLip(hasConnected, DockPortWidth);
        frontLip.Margin = new Thickness(0, DockMouthY, 0, 0);

        CartridgeDockHost.Height = DockFooterHeight;
        UpdateCartridgeDockWidth();
        // Orden de capas reales de la ranura:
        // 1) dockShell (consola + pozo trasero del conector)
        // 2) _dockCartridgeBay (el cartucho entra 20 px dentro del pozo)
        // 3) frontLip (labio frontal y guías laterales del conector que abrazan al cartucho por delante)
        var dockLayout = new Grid { Children = { dockShell, _dockCartridgeBay, frontLip } };
        _dockCartridgeBay.HorizontalAlignment = HorizontalAlignment.Center;
        _dockCartridgeBay.VerticalAlignment = VerticalAlignment.Top;
        // Swap final: recién acá se descarta el dock viejo. La card ya vive en
        // el rightWing nuevo (SafeMoveGameBoostCardTo de arriba); si algo de la
        // construcción falló, el host viejo sigue intacto y el catch la devuelve
        // al header.
        CartridgeDockHost.Children.Clear();
        CartridgeDockHost.Children.Add(dockLayout);
        _dockSkinKey = CardSkin.Current.Key;
        // Estado vigente de la regla en el texto del footer (si hay una partida
        // con regla enganchada, manda ese mensaje sobre el texto base).
        UpdateDockStatusText();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"GestionarProcesosPage: reconstruir dock: {DescribeEx(ex)}");
            EnsureGameBoostCardVisible();
        }
    }

    // =====================================================================
    // VISTA DE INSPECCIÓN CENTRADA (estilo Inspect.qml / GameView.qml de Carthage)
    // Cartucho 3D interactivo a la izquierda + todos los parámetros de WinForge a la derecha
    // =====================================================================

    private void ShowGame3DDetail(
        InstalledGame? game, string exe, string name, string? installPath = null, bool isManual = false)
    {
        _loggingService.LogInfo($"Cartridge3D: inspeccionar '{name}' (exe={exe}, launcher={game?.Launcher})");
        // Al CAMBIAR de juego con las flechas el panel ya estaba abierto: se mantiene
        // estable (sin re-animar el fondo) y solo entra girando el cartucho nuevo.
        bool panelAbierto = _detail3DPanel != null;
        if (_detail3DPanel != null) CloseGame3DDetail();

        // El panel de inspección es una superficie OSCURA en los dos temas (velo de blur
        // sobre negro). Si tomara los pinceles del tema, en la apariencia Claro el título
        // y las etiquetas salían en negro sobre negro y los combos se veían como islas
        // grises flotando en el velo. Este traductor devuelve la tinta clara del panel
        // para los textos y deja pasar el acento tal cual, que ya funciona sobre oscuro.
        SolidColorBrush DetailBrush(string key) => key switch
        {
            "PrimaryTextBrush" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF3, 0xF6, 0xFB)),
            "SecondaryTextBrush" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xA6, 0xAE, 0xBA)),
            "MutedBrush" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x8B, 0x93, 0x9F)),
            // Los avisos del panel también vienen del tema: el verde y el rojo de la
            // apariencia Claro son demasiado oscuros para el velo. Versiones aclaradas.
            "SuccessBrush" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7F, 0xE0, 0xA4)),
            "ErrorBrush" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x8C, 0x7B)),
            _ => ThemeBrushes.Get(key)
        };

        string cacheKey = (installPath ?? "") + "|" + exe;
        if (!_exeCache.TryGetValue(cacheKey, out var cached))
        {
            string? ep = GameLauncher.FindExePath(installPath ?? "", exe);
            if (string.IsNullOrWhiteSpace(ep) || !File.Exists(ep))
                ep = GameExeResolver.FindMainExePath(installPath ?? "");
            string? ip = GameExeResolver.IsStubExe(ep ?? "") && !string.IsNullOrEmpty(installPath)
                ? GameExeResolver.FindBestGameExePath(installPath)
                : ep;
            cached = (ep, ip);
            _exeCache[cacheKey] = cached;
        }
        string? exePath = cached.ExePath;
        string? iconPath = cached.IconPath;
        string? defenderFolder = !string.IsNullOrEmpty(installPath) ? installPath
            : (!string.IsNullOrEmpty(exePath) ? Path.GetDirectoryName(exePath) : null);
        string? cfgExe = !string.IsNullOrEmpty(iconPath) ? Path.GetFileName(iconPath) : exe;

        string launcherDetalle = string.IsNullOrWhiteSpace(game?.Launcher) ? "Manual" : game!.Launcher!;
        string code = CartridgeModel.Code(launcherDetalle, game?.AppId, name);

        // ── Columna izquierda: Cartucho 3D real (frente + cantos + dorso con contactos dorados) ──
        var cartridge = new Cartridge3D
        {
            Width = 310,
            Height = 454,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        cartridge.Logger = m => _loggingService.LogInfo($"Cartridge3D[{name}]: {m}");
        string? initialCover = ResolveBannerFile(game, iconPath, exe);
        cartridge.SetCard(name, launcherDetalle, code, initialCover, header: GameLook.Header(exe));
        // Entrada con giro: el cartucho se pone en su lugar girando 180° (dorso → frente).
        cartridge.PlayIntro();
        // Si el arte todavía no estaba descargado en disco, asegurarlo y refrescar el 3D
        _ = Task.Run(async () =>
        {
            string? ensured = await EnsureGameArtFileAsync(game, iconPath, exe);
            if (!string.IsNullOrEmpty(ensured) && ensured != initialCover)
            {
                DispatcherQueue.TryEnqueue(() =>
                    cartridge.SetCard(name, launcherDetalle, code, ensured, header: GameLook.Header(exe)));
            }
        });

        var flipBtn = new Button
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 0, 0)),
            // Sin reborde claro, igual que el resto de botones de la inspección 3D.
            BorderThickness = new Thickness(0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE72C", FontSize = 12 },
                    new TextBlock
                    {
                        Text = I18n.T("Voltear cartucho (Frente / Dorso)"),
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    }
                }
            }
        };
        flipBtn.Click += (s, e) => cartridge.Flip();

        var hint = new TextBlock
        {
            Text = I18n.T("Arrastrá para girar 360° · clic en el cartucho para voltear · rueda para acercar"),
            FontSize = 11,
            Foreground = DetailBrush("SecondaryTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300
        };

        var leftColumn = new StackPanel
        {
            Width = 330,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                cartridge,
                flipBtn,
                hint
            }
        };

        // ── Columna derecha: Cabecera, botón Iniciar/Favorito y todos los parámetros de WinForge ──
        var eyebrowTxt = new TextBlock
        {
            Text = $"{launcherDetalle.ToUpperInvariant()} · {code}",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = DetailBrush("AccentBrush"),
            CharacterSpacing = 80
        };

        var titleTxt = new TextBlock
        {
            Text = name,
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = DetailBrush("PrimaryTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        };

        var closeBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 14 },
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(18),
            Width = 36,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        ToolTipService.SetToolTip(closeBtn, I18n.T("Cerrar (Esc)"));
        closeBtn.Click += (s, e) => CloseGame3DDetail();

        var titleHeaderGrid = new Grid();
        titleHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel { Spacing = 2, Children = { eyebrowTxt, titleTxt } };
        Grid.SetColumn(titleStack, 0);
        Grid.SetColumn(closeBtn, 1);
        titleHeaderGrid.Children.Add(titleStack);
        titleHeaderGrid.Children.Add(closeBtn);

        // Fila de acciones principales: Iniciar (grande, estilo Inspect.qml) + Favorito
        var spec = ResolveGameLaunchSpec(game, exe, installPath, isManual);
        bool isRunningNow = _runningExes.Contains(exe);
        var playBtn = new Button
        {
            Content = isRunningNow ? I18n.T("En ejecución") : I18n.T("Conectar e iniciar"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 10, 20, 10),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Background = DetailBrush("AccentBrush"),
            Foreground = DetailBrush("AccentForegroundBrush"),
            IsEnabled = spec.CanLaunch && !isRunningNow
        };
        ApplyCardButtonHover(playBtn, accent: true);
        if (!isRunningNow && spec.LauncherProc != null)
        {
            UpdateLauncherButton(playBtn, spec.LauncherProc, spec.LauncherFound,
                spec.LauncherFound && IsLauncherRunning(spec.LauncherProc), spec.AutoOpen);
        }
        playBtn.Click += (s, e) =>
        {
            CloseGame3DDetail();
            // Keyboard/accessibility alternative uses the SAME insertion pipeline as dragging.
            if (_connectedCartridge != null || _cartridgeFlight || _dockLaunching) return;
            var source = _cartridgeVisuals.FirstOrDefault(x => string.Equals(x.Exe, exe, StringComparison.OrdinalIgnoreCase));
            var cardEntry = _cards.FirstOrDefault(x => x.Body is CartridgeTile t && t.Title == name);
            if (cardEntry.Card == null || source.Visual == null) return;
            var slot = GetDockSlotCenterInRoot();
            StartCartridgeDrag(cardEntry.Card, source.Visual,
                (cardEntry.Body as CartridgeTile)?.Artwork.Source, game, exe, name, installPath, isManual, 0.5, 0.5,
                new Windows.Foundation.Point(RootGrid.ActualWidth * 0.5, RootGrid.ActualHeight * 0.5));
            EndCartridgeDrag(slot);
        };

        bool fav = _processService.IsFavorite(exe);
        var favIcon = new FontIcon
        {
            Glyph = fav ? "\uE735" : "\uE734",
            FontSize = 16,
            Foreground = fav
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xC1, 0x07))
                : (Brush)DetailBrush("PrimaryTextBrush")
        };
        var favBtn = new Button
        {
            Content = favIcon,
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 0, 0)),
            // Sin reborde, igual que los botones de carátula y etiqueta.
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(favBtn, I18n.T(fav ? "Quitar de favoritos" : "Marcar como favorito"));
        favBtn.Click += (s, e) =>
        {
            _processService.ToggleFavorite(exe);
            fav = _processService.IsFavorite(exe);
            favIcon.Glyph = fav ? "\uE735" : "\uE734";
            favIcon.Foreground = fav
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0xC1, 0x07))
                : (Brush)DetailBrush("PrimaryTextBrush");
            ToolTipService.SetToolTip(favBtn, I18n.T(fav ? "Quitar de favoritos" : "Marcar como favorito"));
            RebuildCards();
        };

        var actionRow = new Grid { ColumnSpacing = 10 };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(playBtn, 0);
        Grid.SetColumn(favBtn, 1);
        actionRow.Children.Add(playBtn);
        actionRow.Children.Add(favBtn);

        // Feedback interno del panel de inspección
        var detailStatus = new TextBlock
        {
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };

        // ── Personalización del cartucho: carátula propia y encabezado de la etiqueta ──
        // Se guarda por ejecutable (GameLook) y se aplica a la card de la grilla, al 3D
        // de la inspección y al cartucho conectado en el dock.
        Button BuildLookButton(string glyph, string label, string tooltip)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 0, 0, 0)),
                // Sin reborde: el contorno claro de 1 px se leía como un borde blanco
                // sobre el panel oscuro de la inspección.
                BorderThickness = new Thickness(0),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new FontIcon { Glyph = glyph, FontSize = 13 },
                        new TextBlock
                        {
                            Text = I18n.T(label), FontSize = 12,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                        }
                    }
                }
            };
            ApplyCardButtonHover(button, accent: false);
            ToolTipService.SetToolTip(button, I18n.T(tooltip));
            return button;
        }

        async Task RefreshLookAsync()
        {
            string? art = GameLook.CustomArt(exe)
                ?? await EnsureGameArtFileAsync(game, iconPath, exe)
                ?? ResolveBannerFile(game, iconPath, exe);
            cartridge.SetCard(name, launcherDetalle, code, art, header: GameLook.Header(exe));

            var tile = _cards
                .Select(c => c.Body)
                .OfType<CartridgeTile>()
                .FirstOrDefault(t => string.Equals(t.GameExe, exe, StringComparison.OrdinalIgnoreCase));
            if (tile == null) return;

            await LoadCartridgeGridImageAsync(tile.Artwork, game, name, launcherDetalle, code, iconPath, exe);
            if (_connectedCartridge != null &&
                string.Equals(_connectedCartridge.Exe, exe, StringComparison.OrdinalIgnoreCase))
            {
                _connectedCartridge.Artwork = tile.Artwork.Source;
                UpdateCartridgeDock();
            }
        }

        var coverBtn = BuildLookButton("\uE91B", "Carátula…",
            "Elegir una imagen y ajustar el recorte que se ve en el cartucho");
        coverBtn.Click += async (s, e) =>
        {
            try
            {
                string? initial = GameLook.ArtSource(exe) ?? GameLook.CustomArt(exe)
                    ?? ResolveBannerFile(game, iconPath, exe);
                // Sin arte previo (ni banner ni ícono), el primer paso es elegir la imagen.
                if (initial == null) initial = BrowseForImage();
                if (initial == null) return;
                bool hasCustom = GameLook.CustomArt(exe) != null;
                var dialog = new ArtCropDialog(exe, name, initial, hasCustom, BrowseForImage)
                {
                    XamlRoot = XamlRoot
                };
                await dialog.LoadInitialAsync();
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary && !string.IsNullOrEmpty(dialog.OutputPath))
                {
                    // Copia local de la fuente para poder reajustar el recorte más adelante
                    // aunque el archivo original se mueva o se borre.
                    string? localSource = CopyArtSource(exe, dialog.SourcePath);
                    GameLook.SetArt(exe, dialog.OutputPath!, localSource ?? dialog.SourcePath);
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    GameLook.ClearArt(exe);
                }
                else return;

                await RefreshLookAsync();
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"Biblioteca: carátula personalizada de '{name}': {ex.Message}");
            }
        };

        var headerBtn = BuildLookButton("\uE70F", "Etiqueta…",
            "Elegir el texto del encabezado de la etiqueta del cartucho");
        headerBtn.Click += async (s, e) =>
        {
            try
            {
                var dialog = BuildHeaderDialog(exe, name);
                if (dialog == null) return;
                var result = await dialog.ShowAsync();
                if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary)) return;
                await RefreshLookAsync();
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"Biblioteca: etiqueta personalizada de '{name}': {ex.Message}");
            }
        };

        var lookRow = new Grid { ColumnSpacing = 10 };
        lookRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lookRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(coverBtn, 0);
        Grid.SetColumn(headerBtn, 1);
        lookRow.Children.Add(coverBtn);
        lookRow.Children.Add(headerBtn);

        // ── Controles de parámetros (Prioridad CPU, Prioridad GPU, Prioridad E/S, Afinidad, Plan de energía, Defender, CFG) ──
        var rules = _processService.GetRulesCached();
        var rule = rules.TryGetValue(exe, out var r0) ? r0 : new ProcessRule(null, null, null);
        var sessionRule = _processService.GetSessionRule(exe) ?? new ProcessRule(null, null, null);
        int procCount = _processService.ProcessorCount;
        long fullMask = procCount >= 64 ? -1L : ((1L << procCount) - 1);
        bool scopeAlways = true; // Por defecto edita "Siempre" (aplica siempre); "Actual" aplica solo al lanzar desde WinForge

        bool RuleIsEmptyLocal(ProcessRule? r)
            => r == null
            || (r.CpuPriority == null && r.AffinityMask == null && r.GpuPriority == null
                && string.IsNullOrEmpty(r.PowerPlanGuid)
                && r.IoPriority == null);

        void ApplyRuleChanges(bool sessionScope)
        {
            if (sessionScope)
                _processService.SetSessionRule(exe, sessionRule);
            else
                _processService.SaveRule(exe, rule);

            var apps = _processService.FindRunningProcessesForRule(exe);
            if (apps.Count == 0)
            {
                detailStatus.Text = sessionScope
                    ? I18n.T("Reglas de sesión listas. Se aplicarán cuando el juego se abra.")
                    : I18n.T("Reglas guardadas. Se aplicarán cuando el juego esté en ejecución.");
                detailStatus.Foreground = DetailBrush("SuccessBrush");
                detailStatus.Visibility = Visibility.Visible;
                return;
            }

            var effective = _processService.GetEffectiveRule(exe);
            foreach (var app in apps)
            {
                if (effective == null || RuleIsEmptyLocal(effective))
                    _processService.ApplyRuleWithFeedback(app, new ProcessRule(2, fullMask, 3, null, 2));
                else
                    _processService.ApplyRuleWithFeedback(app, effective);
            }

            if (effective == null || RuleIsEmptyLocal(effective))
                _processService.RevertPowerPlanIfApplied(exe);
            else if (!string.IsNullOrEmpty(effective.PowerPlanGuid))
                _processService.ApplyPowerPlanIfRunning(exe, effective.PowerPlanGuid);
            else
                _processService.RevertPowerPlanIfApplied(exe);

            detailStatus.Text = sessionScope
                ? I18n.T("Reglas de sesión aplicadas a {0}", exe)
                : I18n.T("Reglas aplicadas a {0}", exe);
            detailStatus.Foreground = DetailBrush("SuccessBrush");
            detailStatus.Visibility = Visibility.Visible;
        }

        // Selector de alcance: "Siempre" (aplica siempre, incluso desde Steam) vs "Actual" (solo al lanzar desde WinForge)
        var scopeAlwaysBtn = new Button
        {
            Content = I18n.T("Siempre"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        var scopeSessionBtn = new Button
        {
            Content = I18n.T("Actual"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };

        string[] cpuNames = { "Por defecto", "Mínima", "Baja", "Normal", "Por encima de lo normal", "Alta", "Tiempo real" };
        string[] gpuNames = { "Por defecto", "Baja", "Normal", "Alta" };
        string[] ioNames = { "Por defecto", "Muy baja", "Baja", "Normal", "Alta", "Crítica" };

        var cpuCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        foreach (var n in cpuNames) cpuCombo.Items.Add(I18n.T(n));

        var gpuCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        foreach (var n in gpuNames) gpuCombo.Items.Add(I18n.T(n));

        var ioCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        foreach (var n in ioNames) ioCombo.Items.Add(I18n.T(n));

        var planCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        var planGuids = new List<string?> { null };
        planCombo.Items.Add(I18n.T("Por defecto"));

        var coreCheckBoxes = new List<CheckBox>();
        var coresWrap = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
        const int CoreCols = 4;
        for (int c = 0; c < CoreCols; c++)
            coresWrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int coreRows = (procCount + CoreCols - 1) / CoreCols;
        for (int r = 0; r < coreRows; r++)
            coresWrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Afinidad como desplegable: con CPUs de 16, 24 o 32 núcleos la grilla de
        // checks ocupa varias filas y empujaba el pie del panel fuera de la vista
        // (MaxHeight 380). Mismo patrón de header-botón que las secciones de la
        // biblioteca —chevron + título, transparente en todos los estados— y a la
        // derecha el resumen de la selección, para leerla sin desplegar.
        coresWrap.Visibility = Visibility.Collapsed;
        coresWrap.Margin = new Thickness(2, 2, 0, 0);

        var affChevron = new FontIcon
        {
            Glyph = "\uE76C", // cerrado: apunta a la derecha
            FontSize = 11,
            Foreground = DetailBrush("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var affSummary = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            Foreground = DetailBrush("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var affLabelRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                affChevron,
                new TextBlock
                {
                    Text = I18n.T("Afinidad de CPU"),
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = DetailBrush("SecondaryTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        var affHeaderGrid = new Grid { ColumnSpacing = 8 };
        affHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        affHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(affLabelRow, 0);
        Grid.SetColumn(affSummary, 1);
        affHeaderGrid.Children.Add(affLabelRow);
        affHeaderGrid.Children.Add(affSummary);

        var affToggle = new Button
        {
            Content = affHeaderGrid,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 4, 2, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        // El header no se pinta en ningún estado: es solo el título con chevron.
        foreach (var stateKey in new[]
                 {
                     "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed",
                     "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed"
                 })
            affToggle.Resources[stateKey] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        bool syncingUi = false;

        for (int i = 0; i < procCount; i++)
        {
            int ci = i;
            var cb = new CheckBox
            {
                Content = I18n.T("Núcleo {0}", ci + 1),
                FontSize = 12,
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            cb.Checked += (s, e) => OnCoreAffinityToggled();
            cb.Unchecked += (s, e) => OnCoreAffinityToggled();
            Grid.SetRow(cb, ci / CoreCols);
            Grid.SetColumn(cb, ci % CoreCols);
            coreCheckBoxes.Add(cb);
            coresWrap.Children.Add(cb);
        }

        void UpdateAffinitySummary()
        {
            int on = 0;
            foreach (var cb in coreCheckBoxes)
                if (cb.IsChecked == true) on++;
            affSummary.Text = on == 0 || on >= coreCheckBoxes.Count
                ? I18n.T("Todos los núcleos")
                : I18n.T("{0} de {1} núcleos", on, coreCheckBoxes.Count);
        }

        bool affOpen = false;
        void SetAffinityOpen(bool open)
        {
            affOpen = open;
            coresWrap.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            affChevron.Glyph = open ? "\uE70D" : "\uE76C";
        }
        affToggle.Click += (s, e) => SetAffinityOpen(!affOpen);
        UpdateAffinitySummary();

        void OnCoreAffinityToggled()
        {
            if (syncingUi) return;
            UpdateAffinitySummary();
            long m = 0;
            for (int k = 0; k < coreCheckBoxes.Count; k++)
                if (coreCheckBoxes[k].IsChecked == true)
                    m |= 1L << k;
            long? affinity = (m == 0 || m == fullMask) ? fullMask : m;
            if (scopeAlways)
                rule = new ProcessRule(rule.CpuPriority, affinity, rule.GpuPriority, rule.PowerPlanGuid, rule.IoPriority);
            else
                sessionRule = new ProcessRule(sessionRule.CpuPriority, affinity, sessionRule.GpuPriority, sessionRule.PowerPlanGuid, sessionRule.IoPriority);
            ApplyRuleChanges(sessionScope: !scopeAlways);
        }

        void SyncControlsFromScope()
        {
            syncingUi = true;
            var activeRule = scopeAlways ? rule : sessionRule;

            scopeAlwaysBtn.Background = scopeAlways ? DetailBrush("AccentBrush") : new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));
            scopeAlwaysBtn.Foreground = scopeAlways ? DetailBrush("AccentForegroundBrush") : DetailBrush("PrimaryTextBrush");
            scopeSessionBtn.Background = !scopeAlways ? DetailBrush("AccentBrush") : new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255));
            scopeSessionBtn.Foreground = !scopeAlways ? DetailBrush("AccentForegroundBrush") : DetailBrush("PrimaryTextBrush");

            int cpuIdx = activeRule.CpuPriority is int cp ? Array.IndexOf(CpuPriorityValues, cp) + 1 : 0;
            cpuCombo.SelectedIndex = Math.Clamp(cpuIdx, 0, cpuNames.Length - 1);

            int gpuIdx = activeRule.GpuPriority is int gp ? Array.IndexOf(GpuPriorityValues, gp) + 1 : 0;
            gpuCombo.SelectedIndex = Math.Clamp(gpuIdx, 0, gpuNames.Length - 1);

            int ioIdx = activeRule.IoPriority is int io ? Array.IndexOf(IoPriorityValues, io) + 1 : 0;
            ioCombo.SelectedIndex = Math.Clamp(ioIdx, 0, ioNames.Length - 1);

            long? mask = activeRule.AffinityMask;
            bool allCores = mask == null || mask == fullMask;
            for (int i = 0; i < coreCheckBoxes.Count; i++)
                coreCheckBoxes[i].IsChecked = allCores || (mask!.Value & (1L << i)) != 0;

            string? pGuid = activeRule.PowerPlanGuid;
            int planIdx = 0;
            if (!string.IsNullOrEmpty(pGuid))
            {
                for (int i = 1; i < planGuids.Count; i++)
                {
                    if (string.Equals(planGuids[i], pGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        planIdx = i;
                        break;
                    }
                }
            }
            planCombo.SelectedIndex = planIdx;
            syncingUi = false;
            UpdateAffinitySummary();
        }

        scopeAlwaysBtn.Click += (s, e) => { scopeAlways = true; SyncControlsFromScope(); };
        scopeSessionBtn.Click += (s, e) => { scopeAlways = false; SyncControlsFromScope(); };

        cpuCombo.SelectionChanged += (s, e) =>
        {
            if (syncingUi) return;
            int idx = cpuCombo.SelectedIndex;
            int? val = idx <= 0 ? null : CpuPriorityValues[idx - 1];
            if (scopeAlways)
                rule = new ProcessRule(val, rule.AffinityMask, rule.GpuPriority, rule.PowerPlanGuid, rule.IoPriority);
            else
                sessionRule = new ProcessRule(val, sessionRule.AffinityMask, sessionRule.GpuPriority, sessionRule.PowerPlanGuid, sessionRule.IoPriority);
            ApplyRuleChanges(sessionScope: !scopeAlways);
        };

        gpuCombo.SelectionChanged += (s, e) =>
        {
            if (syncingUi) return;
            int idx = gpuCombo.SelectedIndex;
            int? val = idx <= 0 ? null : GpuPriorityValues[idx - 1];
            if (scopeAlways)
                rule = new ProcessRule(rule.CpuPriority, rule.AffinityMask, val, rule.PowerPlanGuid, rule.IoPriority);
            else
                sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, val, sessionRule.PowerPlanGuid, sessionRule.IoPriority);
            ApplyRuleChanges(sessionScope: !scopeAlways);
        };

        ioCombo.SelectionChanged += (s, e) =>
        {
            if (syncingUi) return;
            int idx = ioCombo.SelectedIndex;
            int? val = idx <= 0 ? null : IoPriorityValues[idx - 1];
            if (scopeAlways)
                rule = new ProcessRule(rule.CpuPriority, rule.AffinityMask, rule.GpuPriority, rule.PowerPlanGuid, val);
            else
                sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, sessionRule.GpuPriority, sessionRule.PowerPlanGuid, val);
            ApplyRuleChanges(sessionScope: !scopeAlways);
        };

        planCombo.SelectionChanged += (s, e) =>
        {
            if (syncingUi) return;
            int idx = planCombo.SelectedIndex;
            string? guid = (idx >= 0 && idx < planGuids.Count) ? planGuids[idx] : null;
            if (scopeAlways)
                rule = new ProcessRule(rule.CpuPriority, rule.AffinityMask, rule.GpuPriority, guid, rule.IoPriority);
            else
                sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, sessionRule.GpuPriority, guid, sessionRule.IoPriority);
            ApplyRuleChanges(sessionScope: !scopeAlways);
        };

        SyncControlsFromScope();

        // Cargar planes de energía en segundo plano
        _ = Task.Run(() =>
        {
            try
            {
                var plans = _cpuPowerService.GetPowerPlans();
                DispatcherQueue.TryEnqueue(() =>
                {
                    syncingUi = true;
                    foreach (var p in plans)
                    {
                        planGuids.Add(p.Guid);
                        planCombo.Items.Add(p.Name);
                    }
                    syncingUi = false;
                    SyncControlsFromScope();
                });
            }
            catch { }
        });

        // Botones de Windows Defender y Control Flow Guard
        var defenderBtn = new Button
        {
            Content = I18n.T("Consultando Windows Defender..."),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 12, 7),
            FontSize = 12,
            IsEnabled = !string.IsNullOrEmpty(defenderFolder)
        };
        if (!string.IsNullOrEmpty(defenderFolder))
        {
            string defTarget = defenderFolder.TrimEnd('\\');
            _ = Task.Run(async () =>
            {
                bool ex = await DefenderService.IsPathExcludedAsync(defTarget);
                DispatcherQueue.TryEnqueue(() =>
                    defenderBtn.Content = I18n.T(ex ? "Quitar exclusión de Windows Defender" : "Excluir de Windows Defender"));
            });
            defenderBtn.Click += async (s, e) =>
            {
                bool ex = await DefenderService.IsPathExcludedAsync(defTarget);
                var (ok, _) = ex
                    ? await DefenderService.RemovePathExclusionAsync(defTarget)
                    : await DefenderService.AddPathExclusionAsync(defTarget);
                if (ok)
                {
                    bool nowEx = !ex;
                    defenderBtn.Content = I18n.T(nowEx ? "Quitar exclusión de Windows Defender" : "Excluir de Windows Defender");
                    detailStatus.Text = I18n.T(nowEx ? "Excepción de Windows Defender agregada" : "Excepción de Windows Defender quitada");
                    detailStatus.Foreground = DetailBrush("SuccessBrush");
                    detailStatus.Visibility = Visibility.Visible;
                }
            };
        }

        var cfgBtn = new Button
        {
            Content = I18n.T("Consultando Control Flow Guard..."),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 12, 7),
            FontSize = 12,
            IsEnabled = !string.IsNullOrEmpty(cfgExe)
        };
        if (!string.IsNullOrEmpty(cfgExe))
        {
            string cfgTarget = cfgExe;
            _ = Task.Run(async () =>
            {
                bool dis = await CfgService.IsDisabledAsync(cfgTarget);
                DispatcherQueue.TryEnqueue(() =>
                    cfgBtn.Content = I18n.T(dis ? "Activar Control Flow Guard" : "Desactivar Control Flow Guard"));
            });
            cfgBtn.Click += async (s, e) =>
            {
                bool dis = await CfgService.IsDisabledAsync(cfgTarget);
                var (ok, _) = await CfgService.SetAsync(cfgTarget, !dis);
                if (ok)
                {
                    bool nowDis = !dis;
                    cfgBtn.Content = I18n.T(nowDis ? "Activar Control Flow Guard" : "Desactivar Control Flow Guard");
                    detailStatus.Text = I18n.T(nowDis ? "Control Flow Guard desactivado para {0}" : "Control Flow Guard activado para {0}", cfgTarget);
                    detailStatus.Foreground = DetailBrush("SuccessBrush");
                    detailStatus.Visibility = Visibility.Visible;
                }
            };
        }

        // Botones inferiores: Restablecer reglas / Ocultar
        var resetRulesBtn = new Button
        {
            Content = I18n.T("Eliminar reglas"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 12,
            Foreground = DetailBrush("ErrorBrush") // acción destructiva: en rojo
        };
        resetRulesBtn.Click += (s, e) =>
        {
            _processService.RemoveRule(exe);
            _processService.ClearSessionRule(exe);
            rule = new ProcessRule(null, null, null);
            sessionRule = new ProcessRule(null, null, null);
            SyncControlsFromScope();
            detailStatus.Text = I18n.T("Reglas eliminadas para {0}", exe);
            detailStatus.Foreground = DetailBrush("MutedBrush");
            detailStatus.Visibility = Visibility.Visible;
        };

        bool isHidden = _processService.GetHiddenExes().Contains(exe, StringComparer.OrdinalIgnoreCase);
        var hideToggleBtn = new Button
        {
            Content = I18n.T(isHidden ? "Mostrar en la biblioteca" : "Ocultar de la biblioteca"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 12
        };
        hideToggleBtn.Click += async (s, e) =>
        {
            CloseGame3DDetail();
            if (isHidden) _processService.UnhideExe(exe);
            else _processService.HideExe(exe);
            await RefreshAsync();
        };

        StackPanel BuildLabeledField(string labelText, FrameworkElement control) => new()
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = labelText,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = DetailBrush("SecondaryTextBrush")
                },
                control
            }
        };

        var prioGrid = new Grid { ColumnSpacing = 12, RowSpacing = 10 };
        prioGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        prioGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        prioGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        prioGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var cpuField = BuildLabeledField(I18n.T("Prioridad de CPU"), cpuCombo);
        var gpuField = BuildLabeledField(I18n.T("Prioridad de GPU"), gpuCombo);
        var ioField = BuildLabeledField(I18n.T("Prioridad de E/S"), ioCombo);
        var planField = BuildLabeledField(I18n.T("Plan de energía"), planCombo);

        Grid.SetRow(cpuField, 0); Grid.SetColumn(cpuField, 0);
        Grid.SetRow(gpuField, 0); Grid.SetColumn(gpuField, 1);
        Grid.SetRow(ioField, 1);  Grid.SetColumn(ioField, 0);
        Grid.SetRow(planField, 1); Grid.SetColumn(planField, 1);
        prioGrid.Children.Add(cpuField);
        prioGrid.Children.Add(gpuField);
        prioGrid.Children.Add(ioField);
        prioGrid.Children.Add(planField);

        var sysGrid = new Grid { ColumnSpacing = 12 };
        sysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(defenderBtn, 0);
        Grid.SetColumn(cfgBtn, 1);
        sysGrid.Children.Add(defenderBtn);
        sysGrid.Children.Add(cfgBtn);

        var scopeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = I18n.T("Alcance de reglas:"),
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = DetailBrush("SecondaryTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                },
                scopeAlwaysBtn,
                scopeSessionBtn
            }
        };

        var bottomActionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            // El par de botones queda centrado en el panel (no pegado a la
            // izquierda, que era como se veía).
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                resetRulesBtn,
                hideToggleBtn
            }
        };

        var paramsStack = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                scopeRow,
                prioGrid,
                new StackPanel { Spacing = 4, Children = { affToggle, coresWrap } },
                sysGrid,
                bottomActionsRow,
                detailStatus
            }
        };

        var paramsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 380,
            Content = paramsStack
        };

        // El scrollbar vertical se dibuja ENCIMA del contenido (el ScrollViewer
        // de WinUI no reserva el ancho de la barra): sin este margen la barra
        // tapaba la columna derecha de los combos (prioridad de GPU, plan de
        // energía) y los botones de abajo. El margen se aplica solo cuando hay
        // algo que scrollear: si todo entra, no queda una franja vacía.
        const double ParamsScrollGutter = 16;
        void UpdateParamsScrollGutter()
        {
            double right = paramsScroll.ScrollableHeight > 0 ? ParamsScrollGutter : 0;
            if (Math.Abs(paramsStack.Margin.Right - right) < 0.5) return;
            paramsStack.Margin = new Thickness(
                paramsStack.Margin.Left, paramsStack.Margin.Top, right, paramsStack.Margin.Bottom);
        }
        paramsScroll.SizeChanged += (_, _) => UpdateParamsScrollGutter();
        paramsStack.SizeChanged += (_, _) => UpdateParamsScrollGutter();
        paramsScroll.Loaded += (_, _) => UpdateParamsScrollGutter();

        var rightColumn = new StackPanel
        {
            Width = 480,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                titleHeaderGrid,
                actionRow,
                lookRow,
                new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(35, 255, 255, 255))
                },
                paramsScroll
            }
        };

        var bodyLayout = new Border
        {
            Tag = "no-reveal",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(16),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 36,
                Children =
                {
                    leftColumn,
                    rightColumn
                }
            }
        };
        // Evitar que un clic dentro del contenido cierre la vista de inspección
        bodyLayout.Tapped += (s, e) => e.Handled = true;

        // Navegación entre juegos de la biblioteca con flechas laterales (y teclas ← / →)
        int currentIdx = _orderedLibraryGames.FindIndex(g =>
            string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (currentIdx < 0)
            currentIdx = _orderedLibraryGames.FindIndex(g =>
                string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase));

        bool canNav = _orderedLibraryGames.Count > 1 && currentIdx >= 0;
        _navigateDetail3D = canNav
            ? delta =>
            {
                if (_orderedLibraryGames.Count <= 1) return;
                int nextIdx = (currentIdx + delta % _orderedLibraryGames.Count + _orderedLibraryGames.Count) % _orderedLibraryGames.Count;
                var target = _orderedLibraryGames[nextIdx];
                ShowGame3DDetail(target.Game, target.Exe, target.Name, target.InstallPath, target.IsManual);
            }
            : null;

        Button BuildNavArrowButton(string glyph, string tooltipKey, int delta, HorizontalAlignment align)
        {
            var btn = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 18 },
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(24),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(135, 18, 18, 18)),
                // Sin reborde claro: mismo criterio que los botones de carátula/etiqueta.
                BorderThickness = new Thickness(0),
                HorizontalAlignment = align,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(28, 0, 28, 0),
                Visibility = canNav ? Visibility.Visible : Visibility.Collapsed
            };
            ApplyCardButtonHover(btn, accent: false);
            ToolTipService.SetToolTip(btn, I18n.T(tooltipKey));
            btn.Tapped += (s, e) => e.Handled = true;
            btn.Click += (s, e) => _navigateDetail3D?.Invoke(delta);
            return btn;
        }

        var prevBtn = BuildNavArrowButton("\uE76B", "Juego anterior (←)", -1, HorizontalAlignment.Left);
        var nextBtn = BuildNavArrowButton("\uE76C", "Siguiente juego (→)", +1, HorizontalAlignment.Right);

        // Fondo blureado a pantalla completa sobre la biblioteca (sin div/pop-up encajonado):
        // usa el mismo grafo de GaussianBlurEffect del compositor (GlassBlurBrush) con radio
        // amplio para desenfocar toda la biblioteca detrás del cartucho 3D y sus controles.
        // Velo NEUTRO, sin tinte de color: gris puro (R=G=B). El tinte anterior (8, 10,
        // 16) era azulado y teñía toda la transparencia del popup.
        Brush backdropBrush = (Brush?)GlassBlurBrush.TryCreate(24f, Windows.UI.Color.FromArgb(185, 10, 10, 10))
            ?? new AcrylicBrush
            {
                TintColor = Windows.UI.Color.FromArgb(255, 10, 10, 10),
                TintOpacity = 0.78,
                FallbackColor = Windows.UI.Color.FromArgb(228, 10, 10, 10)
            };

        // El velo (transparencia + blur) y el contenido van en capas SEPARADAS para que
        // cada uno entre con su propio fade: primero se materializa la transparencia y
        // después entra el texto, los botones y el cartucho — todo suave, sin pop.
        var veil = new Border { Tag = "no-reveal", Background = backdropBrush };
        var contentHost = new Border
        {
            Tag = "no-reveal",
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new Grid { Children = { bodyLayout, prevBtn, nextBtn } },
            // Los controles estándar (combos, botones sin estilo propio) heredan el tema
            // forzado del panel: así en la apariencia Claro no quedan grises con texto
            // negro sobre el velo oscuro.
            RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Dark
        };
        var backdrop = new Grid { Children = { veil, contentHost } };
        Canvas.SetZIndex(backdrop, 50);
        backdrop.Tapped += (s, e) => CloseGame3DDetail();

        if (!_detail3DCloseHooked)
        {
            RootGrid.KeyDown += (s, e) =>
            {
                if (_detail3DPanel == null) return;
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    CloseGame3DDetail();
                    e.Handled = true;
                }
                else if (e.Key == Windows.System.VirtualKey.Left && _navigateDetail3D != null)
                {
                    _navigateDetail3D(-1);
                    e.Handled = true;
                }
                else if (e.Key == Windows.System.VirtualKey.Right && _navigateDetail3D != null)
                {
                    _navigateDetail3D(+1);
                    e.Handled = true;
                }
            };
            _detail3DCloseHooked = true;
        }

        if (!panelAbierto)
        {
            // Estado inicial INVISIBLE antes de entrar al árbol: sin esto, el primer
            // frame puede mostrar el popup entero y después "pegar" el fade (el pop que
            // se veía en el texto y en la transparencia).
            veil.Opacity = 0;
            contentHost.Opacity = 0;
            AnimateDetailEntrance(veil, contentHost, bodyLayout);
        }
        RootGrid.Children.Add(backdrop);
        _detail3DPanel = backdrop;
        backdrop.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Entrada suave de la vista de inspección en dos capas: primero el velo de
    /// transparencia (blur) se materializa con un fade, y después entra el contenido —
    /// el texto, los botones y el cartucho— con fade + escala + subida, en sync con el
    /// giro del cartucho (PlayIntro). Los estados iniciales en 0 se restauran antes de
    /// animar, y hay un fallback por si Loaded no llegara: la vista jamás puede quedar
    /// invisible.
    /// </summary>
    private void AnimateDetailEntrance(FrameworkElement veil, UIElement contentHost, UIElement body)
    {
        void Restore()
        {
            veil.Opacity = 1;
            contentHost.Opacity = 1;
        }

        void Start()
        {
            Restore(); // el fade de composición pisa estos valores durante la animación
            try
            {
                var bodyVisual = ElementCompositionPreview.GetElementVisual(body);
                var compositor = bodyVisual.Compositor;
                var ease = compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

                // 1) La transparencia: el velo blureado se materializa primero.
                var veilFade = compositor.CreateScalarKeyFrameAnimation();
                veilFade.InsertKeyFrame(0f, 0f);
                veilFade.InsertKeyFrame(1f, 1f, ease);
                veilFade.Duration = TimeSpan.FromMilliseconds(320);
                ElementCompositionPreview.GetElementVisual(veil).StartAnimation("Opacity", veilFade);

                // 2) El texto y el contenido: fade con leve demora (entra tras el velo).
                var contentFade = compositor.CreateScalarKeyFrameAnimation();
                contentFade.InsertKeyFrame(0f, 0f);
                contentFade.InsertKeyFrame(0.22f, 0f);
                contentFade.InsertKeyFrame(1f, 1f, ease);
                contentFade.Duration = TimeSpan.FromMilliseconds(460);
                ElementCompositionPreview.GetElementVisual(contentHost).StartAnimation("Opacity", contentFade);

                bodyVisual.CenterPoint = new Vector3(bodyVisual.Size.X / 2f, bodyVisual.Size.Y / 2f, 0f);
                var scale = compositor.CreateVector3KeyFrameAnimation();
                scale.InsertKeyFrame(0f, new Vector3(0.86f, 0.86f, 1f));
                scale.InsertKeyFrame(1f, Vector3.One, ease);
                scale.Duration = TimeSpan.FromMilliseconds(420);
                bodyVisual.StartAnimation("Scale", scale);

                var slide = compositor.CreateVector3KeyFrameAnimation();
                slide.InsertKeyFrame(0f, new Vector3(0f, 28f, 0f));
                slide.InsertKeyFrame(1f, Vector3.Zero, ease);
                slide.Duration = TimeSpan.FromMilliseconds(420);
                bodyVisual.StartAnimation("Translation", slide);
            }
            catch (Exception ex)
            {
                Restore();
                _loggingService.LogWarning($"GestionarProcesosPage: animación de entrada: {DescribeEx(ex)}");
            }
        }

        veil.Loaded += (_, _) => Start();
        // Fallback: si Loaded no llegara, la vista se restaura igual (nunca invisible).
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Restore);
    }

    private void CloseGame3DDetail()
    {
        _navigateDetail3D = null;
        if (_detail3DPanel != null)
        {
            try { RootGrid.Children.Remove(_detail3DPanel); } catch { }
            _detail3DPanel = null;
        }
    }

    private static async Task LoadBannerAsync(Border banner, Image mediaImage, Image fallbackIcon, string appId, string url, string? exePath)
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
                catch
                {
                    if (!string.IsNullOrEmpty(exePath)) _ = LoadExeIconAsync(mediaImage, fallbackIcon, exePath);
                    return;
                }
            }
            // Validar que la caché sea una imagen real (JPEG/PNG); si está corrupta,
            // descartarla y dejar el ícono/emoji (no un banner vacío con todo oculto).
            if (!IsValidImageFile(file))
            {
                try { File.Delete(file); } catch { }
                if (!string.IsNullOrEmpty(exePath)) _ = LoadExeIconAsync(mediaImage, fallbackIcon, exePath);
                return;
            }
            ApplyBannerFile(banner, mediaImage, fallbackIcon, file);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Banner {appId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Muestra la imagen del juego LLENANDO el banner (fill centrado, sin bandas
    /// ni barras): la imagen ocupa todo el banner. Corre en hilo UI.
    /// </summary>
    private static void ShowBannerImage(
        Image mediaImage, Image fallbackIcon, ImageSource source)
    {
        // BANNER REAL: la imagen cubre todo el banner (UniformToFill) y queda
        // anclada al CENTRO (el recorte interno del cover también es centrado).
        // No toca el layout de la card: solo la alineación de la propia imagen.
        mediaImage.Stretch = Stretch.UniformToFill;
        mediaImage.HorizontalAlignment = HorizontalAlignment.Center;
        mediaImage.VerticalAlignment = VerticalAlignment.Center;
        mediaImage.Width = double.NaN;
        mediaImage.Height = double.NaN;
        mediaImage.Source = source;
        mediaImage.Visibility = Visibility.Visible;
        fallbackIcon.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Aplica un archivo de imagen ya validado como banner. La imagen llena la
    /// card panorámica (fill centrado, mismo look simétrico para todas las cards).
    /// </summary>
    private static void ApplyBannerFile(Border banner, Image mediaImage, Image fallbackIcon, string file)
    {
        var bmp = new BitmapImage(new Uri(file));
        // Decodificar a tamaño de card (no a resolución completa): banners tipo
        // 2160×2160 (Battle.net) o 2560×1440 (Epic) decodifican mucho más rápido y
        // con mucha menos memoria si se pide el decode ya escalado.
        bmp.DecodePixelWidth = 640;
        // El banner llena la card (fill centrado) sin tocar el layout: TODAS las
        // cards miden lo mismo (estilo NVIDIA App) y el recorte del excedente es
        // simétrico, igual para cualquier fuente (Steam/Epic/Battle.net).
        ShowBannerImage(mediaImage, fallbackIcon, bmp);
        banner.Background = ThemeBrushes.Get("CardHoverBrush");
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
            // OJO: DataWriter.Dispose dispone el stream subyacente, así que el
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
    private static async Task LoadEpicBannerAsync(Border banner, Image mediaImage, Image fallbackIcon, string ns, string catalogItemId, string? exePath)
    {
        void FallbackToIcon()
        {
            if (!string.IsNullOrEmpty(exePath))
                _ = LoadExeIconAsync(mediaImage, fallbackIcon, exePath);
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
                ApplyBannerFile(banner, mediaImage, fallbackIcon, file);
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
    /// Muestra un ícono local (exe/.ico, 72 px centrado). Corre en hilo UI.
    /// </summary>
    private static void ShowExeIconImage(
        Image mediaImage, Image fallbackIcon, BitmapImage iconImage)
    {
        mediaImage.Source = iconImage;
        mediaImage.Stretch = Stretch.Uniform;
        mediaImage.Width = 72;
        mediaImage.Height = 72;
        mediaImage.HorizontalAlignment = HorizontalAlignment.Center;
        mediaImage.VerticalAlignment = VerticalAlignment.Center;
        mediaImage.Visibility = Visibility.Visible;
        fallbackIcon.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Extrae el ícono del exe del juego (recurso del ejecutable) y lo muestra; si
    /// falla, queda el emoji. El PNG se guarda en caché y se carga DESDE ARCHIVO
    /// (igual que los banners): SetSource con un MemoryStream descartado dejaba la
    /// imagen en blanco porque BitmapImage decodifica en forma asíncrona.
    /// </summary>
    private static async Task LoadExeIconAsync(Image mediaImage, Image fallbackIcon, string exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                // El fallback de WinForge ya está visible; no ocultarlo si no hay
                // ejecutable resoluble (caso frecuente en juegos de Battle.net).
                return;
            }
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
                        var iconImage = new BitmapImage(new Uri(icoCache));
                        iconImage.DecodePixelWidth = 72;
                        ShowExeIconImage(mediaImage, fallbackIcon, iconImage);
                        return;
                    }
                }
            }
            else if (IsValidImageFile(icoCache))
            {
                // Ícono local del juego desde caché: mismo tamaño compacto y centrado
                // que el resto de los fallbacks de ícono.
                ShowExeIconImage(mediaImage, fallbackIcon, new BitmapImage(new Uri(icoCache)));
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
                        ShowExeIconImage(mediaImage, fallbackIcon, new BitmapImage(new Uri(small)));
                    }
                    return;
                }
            }
            if (!IsValidImageFile(cacheFile)) return;
            var finalImage = new BitmapImage(new Uri(cacheFile));
            finalImage.DecodePixelWidth = 72;
            ShowExeIconImage(mediaImage, fallbackIcon, finalImage);
        }
        catch
        {
            // Nunca ocultar el fallback: una extracción fallida no debe dejar la
            // card sin ningún recurso visual.
        }
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

    // Ratio de banner de las cards (alto = ancho de card × ratio): panorámico
    // 460:215, el del header de Steam. Todas las cards comparten esta geometría
    // (simetría total, estilo NVIDIA App); la imagen llena el marco con recorte
    // central (UniformToFill) sin importar el aspecto del archivo original.
    private const double BannerHeightRatio = 215.0 / 460.0;

    private void LibraryScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardWidth();

    private int GetEffectiveColumns()
    {
        int baseCols = _cards3D ? GridColumns3D : GridColumns;
        double w = LibraryScroll?.ActualWidth ?? 0;
        if (w <= 0) return baseCols;

        double sidePad = _cards3D ? 18 : 0;
        double avail = w - (ScrollBarReserve + sidePad * 2);
        if (avail <= 0) return baseCols;

        double maxCardW = _cards3D ? MaxCardWidth3D : MaxCardWidthVanilla;
        double minCardW = _cards3D ? MinCardWidth3D : MinCardWidthVanilla;

        // Si la ventana es más ancha (p. ej. maximizada o en pantalla completa),
        // agregamos columnas para que las cards no superen su tamaño máximo ideal.
        int cols = Math.Max(baseCols,
            (int)Math.Ceiling((avail + GridCardSpacing) / (maxCardW + GridCardSpacing)));

        // Si la ventana se achica, reducimos columnas para respetar el ancho mínimo.
        int maxThatFit = Math.Max(1,
            (int)Math.Floor((avail + GridCardSpacing) / (minCardW + GridCardSpacing)));
        if (cols > maxThatFit)
            cols = maxThatFit;

        return cols;
    }

    private void UpdateCardWidth()
    {
        double w = LibraryScroll.ActualWidth;
        if (w <= 0) return;
        // La ranura del dock sigue el ancho de la card 3D: se actualiza ANTES del
        // early-out de la grilla vacía, así la consola también conserva la medida
        // cuando no hay juegos que dibujar.
        UpdateDockCardWidth(w);
        if (_cards.Count == 0 && _skeletonCards.Count == 0) return;
        // Reserva fija de la canaleta del scrollbar (LibraryPanel margen derecho = 14)
        // más el aire lateral que necesita el tilt de los cartuchos en los bordes.
        double sidePad = _cards3D ? 18 : 0;
        double reserve = ScrollBarReserve + sidePad * 2;
        int cols = GetEffectiveColumns();
        if (cols != _currentGridCols && _wrapSectionGroups.Count > 0)
        {
            _currentGridCols = cols;
            foreach (var (host, sectionCards) in _wrapSectionGroups)
                ReflowWrapSection(host, sectionCards, cols);
            if (_cards3D) SchedulePaintVisible3D();
        }
        // El skeleton sigue la misma cantidad de columnas: si se maximiza mientras
        // carga, sus filas no pueden quedar con el ancho viejo.
        if (_skeletonActive && _skeletonPanel != null && cols != _skeletonGridCols)
            ReflowSkeleton(cols);
        double avail = w - reserve;
        double maxCardW = _cards3D ? MaxCardWidth3D : MaxCardWidthVanilla;
        double minCardW = _cards3D ? MinCardWidth3D : MinCardWidthVanilla;
        double rawCardWidth = (avail - GridCardSpacing * (cols - 1)) / cols;
        double cardWidth = Math.Clamp(rawCardWidth, minCardW, maxCardW);
        // Aire perimetral para que el tilt y la elevación de las cards en la primera/
        // última columna y en la primera/última fila no queden cortados por el borde de
        // LibraryContentGrid ni por el viewport del scroll.
        if (LibraryPanel != null)
            LibraryPanel.Margin = new Thickness(sidePad, _cards3D ? 18 : 8, ScrollBarReserve + sidePad,
                _cards3D ? DockProtrusion + 18 : 8);
        if (LibraryContentGrid != null)
            LibraryContentGrid.Margin = new Thickness(0);
        double bannerMin = 100;
        double bannerWidth = Math.Max(80, cardWidth);
        foreach (var (cardEl, body) in _cards)
        {
            cardEl.Width = cardWidth;
            // Alto IDÉNTICO para todas las cards. Vanilla: ratio panorámico 460:215
            // (el del header de Steam), así los banners de Steam se ven COMPLETOS y
            // sin recortes horizontales. 3D: relación vertical del propio cartucho,
            // así el modelo entra completo y sin deformarse.
            body.Height = _cards3D
                ? (cardWidth - 8) * CartridgeHeightRatio
                : bannerWidth * BannerHeightRatio;
        }
        // Misma geometría para el skeleton (si está visible en la grilla). En 3D el
        // cuerpo mide lo mismo que el cartucho real: ancho de card menos el padding
        // de 4 px por lado, multiplicado por el Aspect del modelo.
        foreach (var (card, body, _) in _skeletonCards)
        {
            card.Width = cardWidth;
            body.Height = _cards3D
                ? Math.Max(bannerMin, (cardWidth - 8) * CartridgeHeightRatio)
                : Math.Max(bannerMin, bannerWidth * BannerHeightRatio);
        }
    }

    /// <summary>
    /// Mantiene el cartucho (y la ranura) del dock con el mismo ancho que la card 3D
    /// de la grilla. Repite el cálculo de columnas de <see cref="UpdateCardWidth"/> a
    /// propósito: tiene que valer también con la biblioteca vacía, donde ese método
    /// corta antes de tocar las cards.
    /// </summary>
    private void UpdateDockCardWidth(double libraryWidth)
    {
        if (!_cards3D) return;
        double sidePad = 18;
        double reserve = ScrollBarReserve + sidePad * 2;
        int cols = GetEffectiveColumns();
        double avail = libraryWidth - reserve;
        double rawCardWidth = (avail - GridCardSpacing * (cols - 1)) / cols;
        double cardWidth = Math.Clamp(rawCardWidth, MinCardWidth3D, MaxCardWidth3D);
        // La card se dibuja dentro de un Border con 4 px de padding: el cartucho
        // visible (el que entra en la ranura) mide 8 px menos.
        double dockCardWidth = Math.Max(1, cardWidth - 8);
        if (Math.Abs(dockCardWidth - _dockCardWidth) < 0.5) return;
        _dockCardWidth = dockCardWidth;
        ScheduleDockGeometryUpdate();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dockGeometryTimer;

    /// <summary>
    /// Reconstruye el dock cuando cambia el ancho del cartucho 3D, con una espera corta:
    /// durante el arrastre del borde de la ventana el ancho cambia en cada frame y
    /// repintar el chasis (GDI+) a 60 fps haría temblar la consola.
    /// </summary>
    private void ScheduleDockGeometryUpdate()
    {
        if (_dockGeometryTimer == null)
        {
            _dockGeometryTimer = DispatcherQueue.CreateTimer();
            _dockGeometryTimer.IsRepeating = false;
            _dockGeometryTimer.Interval = TimeSpan.FromMilliseconds(90);
            _dockGeometryTimer.Tick += (_, _) =>
            {
                if (!_cards3D) return;
                if (CartridgeDockHost.Visibility != Visibility.Visible) return;
                // UpdateCartridgeDock es atómico y respeta el arrastre en curso.
                UpdateCartridgeDock();
            };
        }
        _dockGeometryTimer.Stop();
        _dockGeometryTimer.Start();
    }

    // ===== Tuerca → desplegable simple (submenús) =====

    private void ShowRuleMenu(FrameworkElement target, string exe, string name, bool isManual, string? defenderFolder = null, string? cfgExe = null)
    {
        // Diagnóstico de latencia de apertura: mide (1) lectura de reglas+sesión,
        // (2) construcción del árbol del menú, (3) hasta que ShowAt retorna. Cada
        // tramo se loguea para ver dónde van los ms si un click se percibe lento.
        var buildClock = System.Diagnostics.Stopwatch.StartNew();
        var rules = _processService.GetRulesCached();
        var rule = rules.TryGetValue(exe, out var r) ? r : new ProcessRule(null, null, null);
        // Regla de sesión ("Actual"): se recuerda y aplica solo al lanzar desde WinForge.
        var sessionRule = _processService.GetSessionRule(exe) ?? new ProcessRule(null, null, null);
        int procCount = _processService.ProcessorCount;
        long fullMask = procCount >= 64 ? -1L : ((1L << procCount) - 1);
        long rulesMs = buildClock.ElapsedMilliseconds;

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        // Launcher de por medio: se avisa UNA vez en la configuración del juego, no en
        // cada arranque. La regla se aplica al proceso real cuando aparece; el launcher
        // es compartido por varios juegos y no se toca.
        string? launchLauncher = _installed
            .FirstOrDefault(g => string.Equals(g.ExeFileName, exe, StringComparison.OrdinalIgnoreCase))?.Launcher;
        if (string.IsNullOrWhiteSpace(launchLauncher) && isManual)
            launchLauncher = _processService.GetManualGameLauncher(exe);
        bool hasLauncher = !string.IsNullOrWhiteSpace(launchLauncher)
            && !string.Equals(launchLauncher, "Independiente", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(launchLauncher, "Emulador", StringComparison.OrdinalIgnoreCase);
        if (hasLauncher)
        {
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = I18n.T("Este juego se lanza con {0}: la regla se aplicará al proceso del juego cuando aparezca, no al launcher.", launchLauncher),
                IsEnabled = false
            });
            menu.Items.Add(new MenuFlyoutSeparator());
        }

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
                    ? hasLauncher
                        ? I18n.T("Reglas de sesión listas. Se aplicarán al proceso del juego cuando aparezca (launcher: {0}).", launchLauncher)
                        : I18n.T("Reglas de sesión listas. Se aplicarán cuando el juego se abra.")
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

        // "Siempre": se guarda en el registro y aplica en cada apertura (incluso desde Steam).
        void ApplyAndSave(ProcessRule newRule)
        {
            _processService.SaveRule(exe, newRule);
            ApplyEffectiveToRunning(sessionScope: false);
        }

        // "Actual": se recuerda pero aplica solo al lanzar desde WinForge.
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

        // ===== Prioridad de CPU: alcance "Actual" (solo WinForge) / "Siempre" =====
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
        // ANTES esto corría síncrono en el hilo de UI y congelaba la apertura del
        // menú: FindRunningProcess enumera TODOS los procesos del sistema (handle +
        // ruta + WorkingSet por cada uno, cientos de ms). Ahora: placeholder +
        // consulta en background (mismo patrón que Defender/CFG de abajo).
        var liveItem = new MenuFlyoutItem
        {
            Text = I18n.T("Consultando estado en vivo..."),
            IsEnabled = false,
            MinWidth = 300
        };
        menu.Items.Add(liveItem);
        _ = Task.Run(async () =>
        {
            try
            {
                var runningApp = _processService.FindRunningProcess(exe);
                if (runningApp == null)
                {
                    // Juego no corriendo: quitar el placeholder del menú.
                    DispatcherQueue.TryEnqueue(() => menu.Items.Remove(liveItem));
                    return;
                }

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

                var realIo = _processService.GetIoPriority(runningApp.Id);
                string ioText = realIo == null
                    ? I18n.T("Prioridad E/S actual: no se pudo leer (proceso protegido: anti-cheat)")
                    : I18n.T("Prioridad E/S actual: {0} ({1})", IoLabel(realIo.Value), realIo.Value);

                DispatcherQueue.TryEnqueue(() =>
                {
                    int idx = menu.Items.IndexOf(liveItem);
                    if (idx < 0) return; // el placeholder ya no está (menú cerrado)
                    liveItem.Text = infoText;
                    menu.Items.Insert(idx, new MenuFlyoutSeparator());
                    menu.Items.Insert(idx + 2, new MenuFlyoutItem { Text = ioText, IsEnabled = false, MinWidth = 300 });
                });
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"GestionarProcesosPage: verificación en vivo {exe}: {ex.Message}");
                DispatcherQueue.TryEnqueue(() => menu.Items.Remove(liveItem));
            }
        });

        // ===== Plan de energía: desplegable directo de los planes instalados.
        // Se activa al correr el juego y se revierte al cerrar (por juego solo
        // aplica en la sesión actual; el plan permanente se maneja en el apartado
        // "Núcleos y Plan de energía"). =====
        // ANTES GetPowerPlans() corría síncrono en el hilo de UI: lanza powercfg /l
        // + powercfg /getactivescheme (DOS procesos de consola por click en la
        // tuerca, cientos de ms congelando el menú). Ahora: "Por defecto" aparece
        // al instante y los planes se consultan en background.
        var planSub = new MenuFlyoutSubItem { Text = I18n.T("Plan de energía") };
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
        var planPlaceholder = new MenuFlyoutItem { Text = I18n.T("Consultando planes de energía..."), IsEnabled = false };
        planSub.Items.Add(planPlaceholder);
        menu.Items.Add(planSub);
        _ = Task.Run(() =>
        {
            try
            {
                var plans = _cpuPowerService.GetPowerPlans();
                DispatcherQueue.TryEnqueue(() =>
                {
                    int idx = planSub.Items.IndexOf(planPlaceholder);
                    if (idx < 0) return; // menú cerrado / placeholder ya reemplazado
                    planSub.Items.RemoveAt(idx);
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
                        planSub.Items.Insert(idx + pi, item);
                    }
                });
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"GestionarProcesosPage: planes de energía para {exe}: {ex.Message}");
                DispatcherQueue.TryEnqueue(() =>
                {
                    int idx = planSub.Items.IndexOf(planPlaceholder);
                    if (idx >= 0) planSub.Items.RemoveAt(idx);
                });
            }
        });

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

        // "Eliminar reglas" es destructivo: en rojo, igual que "Eliminar de la
        // biblioteca" (más abajo en el mismo menú).
        var resetItem = new MenuFlyoutItem
        {
            Text = I18n.T("Eliminar reglas"),
            Foreground = Feedback.ErrorBrush
        };
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

        // Ocultar / Mostrar: reversible. Los juegos ocultos se listan en la sección
        // "Ocultos" al final de la biblioteca y vuelven con "Mostrar" o re-detectando.
        bool isHidden = _processService.GetHiddenExes()
            .Contains(exe, StringComparer.OrdinalIgnoreCase);
        if (!isHidden)
        {
            var hideItem = new MenuFlyoutItem
            {
                Text = I18n.T("Ocultar de la biblioteca")
            };
            hideItem.Click += async (s, e) =>
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = I18n.T("Ocultar de la biblioteca"),
                    Content = I18n.T("¿Ocultar {0}? Podés volver a mostrarlo desde la sección Ocultos.", name),
                    PrimaryButtonText = I18n.T("Ocultar"),
                    CloseButtonText = I18n.T("Cancelar"),
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    _processService.HideExe(exe);
                    _processService.ClearSessionRule(exe);
                    await RefreshAsync();
                }
            };
            menu.Items.Add(hideItem);
        }
        else
        {
            var showItem = new MenuFlyoutItem
            {
                Text = I18n.T("Mostrar en la biblioteca"),
                Foreground = Feedback.SuccessBrush
            };
            showItem.Click += async (s, e) =>
            {
                _processService.UnhideExe(exe);
                StatusText.Text = I18n.T("{0} volvió a la biblioteca.", name);
                StatusText.Foreground = Feedback.MutedBrush;
                StatusText.Visibility = Visibility.Visible;
                await RefreshAsync();
            };
            menu.Items.Add(showItem);
        }

        // Eliminar: saca el juego de TODA la biblioteca (ni ocultos lo lista).
        // Si es manual: elimina de la lista de manuales (no vuelve a aparecer).
        // Si es detectado: agrega a eliminados (vuelve con Re-detectar).
        menu.Items.Add(new MenuFlyoutSeparator());
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
                Content = isManual
                    ? I18n.T("¿Eliminar {0} de la biblioteca? No se desinstala. Al ser un juego manual, no volverá a aparecer con «Re-detectar».", name)
                    : I18n.T("¿Eliminar {0} de la biblioteca? No se desinstala. Podés re-detectarlo con el botón «Re-detectar».", name),
                PrimaryButtonText = I18n.T("Eliminar"),
                CloseButtonText = I18n.T("Cancelar"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (isManual)
                {
                    // Juego manual: eliminar de la lista de manuales y registrarlo
                    // como eliminado para que NO vuelva a aparecer aunque el escáner
                    // también lo detecte (ej. por registro de desinstalación) ni con
                    // «Re-detectar» (el filtro de eliminados cubre manuales y detectados).
                    _processService.RemoveManualExe(exe);
                    _processService.DeleteGame(exe);
                    StatusText.Text = I18n.T("{0} eliminado de la biblioteca. Al ser manual, no se re-detectará.", name);
                }
                else
                {
                    // Juego detectado: agregar a eliminados
                    _processService.DeleteGame(exe);
                    StatusText.Text = I18n.T("{0} eliminado de la biblioteca. Podés re-detectarlo con «Re-detectar».", name);
                }
                StatusText.Foreground = Feedback.MutedBrush;
                StatusText.Visibility = Visibility.Visible;
                await RefreshAsync();
            }
        };
        menu.Items.Add(deleteItem);

        menu.ShowAt(target);

        buildClock.Stop();
        _loggingService.LogInfo(
            $"ShowRuleMenu '{exe}': reglas={rulesMs} ms, construcción+ShowAt={buildClock.ElapsedMilliseconds - rulesMs} ms, total={buildClock.ElapsedMilliseconds} ms");
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

    /// <summary>
    /// Diálogo del encabezado de la etiqueta: logo + plataforma (de fábrica), solo la
    /// plataforma, el nombre del juego, un texto libre o la etiqueta en blanco. Al
    /// aplicar escribe GameLook; el refresco del cartucho lo hace el llamador.
    /// </summary>
    private ContentDialog? BuildHeaderDialog(string exe, string name)
    {
        if (XamlRoot == null) return null;
        GameHeader current = GameLook.Header(exe);

        (string Mode, string Label)[] modes =
        {
            (GameLook.HeaderPlatform, "Logotipo y plataforma"),
            (GameLook.HeaderPlatformText, "Solo la plataforma"),
            (GameLook.HeaderTitle, "Nombre del juego"),
            (GameLook.HeaderCustom, "Texto personalizado"),
            (GameLook.HeaderNone, "Sin texto")
        };

        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        int selected = 0;
        for (int i = 0; i < modes.Length; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = I18n.T(modes[i].Label), Tag = modes[i].Mode });
            if (string.Equals(modes[i].Mode, current.Mode, StringComparison.Ordinal)) selected = i;
        }
        combo.SelectedIndex = selected;

        var box = new TextBox
        {
            Text = current.Text,
            PlaceholderText = I18n.T("Texto de la etiqueta"),
            MaxLength = 40
        };

        var error = new TextBlock
        {
            Text = I18n.T("Escribí un texto para la etiqueta."),
            FontSize = 12,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x7A, 0x7A)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        void SyncBox()
        {
            box.IsEnabled = string.Equals(
                (combo.SelectedItem as ComboBoxItem)?.Tag as string,
                GameLook.HeaderCustom, StringComparison.Ordinal);
            error.Visibility = Visibility.Collapsed;
        }
        combo.SelectionChanged += (_, _) => SyncBox();
        SyncBox();

        var panel = new StackPanel
        {
            Width = 380,
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = I18n.T("Encabezado de la etiqueta"),
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                combo,
                box,
                new TextBlock
                {
                    Text = I18n.T("El encabezado se ve en la card de la biblioteca y en el cartucho del dock. Los textos largos se achican solos para entrar en la etiqueta."),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrushes.Get("SecondaryTextBrush")
                },
                error
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Etiqueta de {0}", name),
            PrimaryButtonText = I18n.T("Aplicar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Primary,
            Content = panel
        };
        if (!current.IsDefault) dialog.SecondaryButtonText = I18n.T("Restaurar");

        dialog.PrimaryButtonClick += (_, args) =>
        {
            string mode = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? GameLook.HeaderPlatform;
            if (string.Equals(mode, GameLook.HeaderCustom, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(box.Text))
            {
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }
            GameLook.SetHeader(exe, mode, box.Text);
        };
        dialog.SecondaryButtonClick += (_, _) =>
            GameLook.SetHeader(exe, GameLook.HeaderPlatform, "");

        return dialog;
    }

    /// <summary>Copia la imagen elegida a la carpeta de personalizaciones para que el
    /// reajuste del recorte no dependa de un archivo externo.</summary>
    private static string? CopyArtSource(string exe, string? source)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return null;
            string target = GameLook.SourceArtPath(exe, Path.GetExtension(source));
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, overwrite: true);
            return target;
        }
        catch { return source; }
    }

    /// <summary>Selector nativo de imagen. WinRT FileOpenPicker no funciona en una app
    /// elevada, así que se usa el diálogo de WinForms con la ventana como dueño.</summary>
    private static string? BrowseForImage()
    {
        try
        {
            using var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = I18n.T("Elegir carátula"),
                Filter = $"{I18n.T("Imágenes")}|*.png;*.jpg;*.jpeg;*.bmp",
                CheckFileExists = true,
                Multiselect = false
            };
            if (App.MainWindowInstance != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
                var owner = System.Windows.Forms.NativeWindow.FromHandle(hwnd);
                if (dlg.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    return dlg.FileName;
            }
        }
        catch { }
        return null;
    }

}
