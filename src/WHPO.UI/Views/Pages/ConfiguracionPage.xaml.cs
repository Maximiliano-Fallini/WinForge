using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using WHPO.Core;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

public sealed partial class ConfiguracionPage : Page
{
    /// <summary>
    /// Parámetro de navegación para abrir la pestaña Drivers desde otra página
    /// (por ejemplo, la badge de temperatura faltante de Sistema).
    /// </summary>
    public const string DriversTabParameter = "drivers";

    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IStartupService _startupService;
    private readonly ILoggingService _loggingService;
    private readonly IInstalledGamesService _installedGamesService;
    private readonly IUsbOverclockService _usbOverclockService;
    private readonly IAppUpdateService _appUpdateService;
    private readonly IPawnIoService _pawnIoService;
    private bool _isLoading;

    // Instalar/desinstalar PawnIO desde esta página: mientras corre no se refresca
    // la tarjeta, para no pisar el mensaje de progreso ni el resultado.
    private bool _pawnIoBusy;

    // El driver se carga al abrir LibreHardwareMonitor y se libera al cerrar el
    // proceso: instalar o desinstalar PawnIO no cambia nada hasta reiniciar WinForge.
    // La badge y el botón de reinicio salen de este flag (no de un mensaje que se
    // desvanece a los 4 segundos), así el aviso sigue ahí al volver a la pestaña.
    private bool _pawnIoRestartPending;

    // Mismo criterio para hidusbf: el refresco de esa tarjeta se saltea mientras
    // corre una instalación o desinstalación, para no pisar el progreso.
    private bool _hidUsbfBusy;

    // Pestaña seleccionada del navbar interno (Inicio, Apariencia, Drivers, Actualizaciones, Desarrollo).
    // Solo se usa como respaldo: ApplyTabVisibility compara contra el ítem seleccionado,
    // no contra el índice.
    private int _selectedTabIndex;

    // Destino pedido desde otra página (por ejemplo Sistema → badge de PawnIO).
    // No se aplica en OnNavigatedTo: ahí el SelectorBar todavía no está cargado y
    // el IsSelected="True" de la pestaña Inicio, al realizarse el XAML, pisa la
    // selección. Se aplica cuando la página ya está en el árbol (OnLoaded).
    private bool _pendingDriversTab;

    private OnboardingSimulatorWindow? _onboardingSimulator;

    // Selector del estilo 3D (cartucho + consola): las muestras se arman en código para
    // que «Del tema» tome el acento vigente. _buildingCardSkin evita que reflejar la
    // selección guardada dispare el guardado y el repintado de miniaturas.
    private readonly Dictionary<string, (Border Ring, FontIcon Check, FontIcon? ThemeGlyph)> _cardSkinSwatches = new();
    private bool _buildingCardSkin;

    // Sección de desarrollo: visible solo en builds de desarrollo (ver
    // UpdateDevelopmentToolsVisibility).
    public ConfiguracionPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _themeService = App.Services.GetRequiredService<IThemeService>();
        _startupService = App.Services.GetRequiredService<IStartupService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _installedGamesService = App.Services.GetRequiredService<IInstalledGamesService>();
        _usbOverclockService = App.Services.GetRequiredService<IUsbOverclockService>();
        _appUpdateService = App.Services.GetRequiredService<IAppUpdateService>();
        _pawnIoService = App.Services.GetRequiredService<IPawnIoService>();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _themeService.ThemeChanged += OnThemeChanged;

        // El navbar INTERNO se pinta a mano (ver RepaintConfigNavBar) porque WinUI cachea su
        // {ThemeResource}: es una superficie que no sigue al ajuste por sí sola, así que se suscribe a
        // la aplicación del ajuste y se repinta con él ya aplicado. La página usa caché de navegación:
        // la suscripción y la baja se atan a Loaded/Unloaded para no dejar el handler colgado.
        Loaded += (_, _) =>
        {
            // Baja antes del alta: la página se carga más de una vez (caché de navegación) y no puede
            // quedar suscrita dos veces.
            WHPO_UI.PanelAppearance.Applied -= RepaintConfigNavBar;
            WHPO_UI.PanelAppearance.Applied += RepaintConfigNavBar;
            RepaintConfigNavBar();
        };
        Unloaded += (_, _) => WHPO_UI.PanelAppearance.Applied -= RepaintConfigNavBar;

        // La página usa caché de navegación: estas suscripciones se hacen una sola vez.
        // Sincroniza el indicador de la pestaña interna "Actualizaciones" con el estado
        // global del chequeo (MainWindow dispara el evento al completar el check).
        if (App.MainWindowInstance != null)
            App.MainWindowInstance.AppUpdateStateChanged += OnAppUpdateStateChanged;

        // Al cambiar de idioma, actualizar el resumen del menú de navegación (la
        // página usa caché de navegación, así que se suscribe una sola vez).
        I18n.LanguageChanged += OnLanguageChanged;
        MinimizeToTrayToggle.Toggled += OnMinimizeToTrayToggled;
        TrayMetricsToggle.Toggled += OnTrayMetricsToggled;
        TrayEcoModeToggle.Toggled += OnTrayEcoModeToggled;
        DeveloperLogsToggle.Toggled += OnDeveloperLogsToggled;
        LaunchAtStartupToggle.Toggled += OnLaunchAtStartupToggled;
        StartMinimizedToggle.Toggled += OnStartMinimizedToggled;
        CardTiltToggle.Toggled += OnCardTiltToggled;
        CardSkinCustomButton.Click += CardSkinCustomButton_Click;
        // Selector 2D / 3D de las cards: toda la tarjeta es un RadioButton lookless
        // (ver LibraryStyleTile), así que el cambio llega por Checked.
        AttachPickerTileHover(LibraryStyle2DTile);
        AttachPickerTileHover(LibraryStyle3DTile);
    }

    /// <summary>
    /// Navegación con destino explícito: Sistema pide "drivers" para que la badge de
    /// temperatura faltante caiga directamente en la pestaña correcta.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string tab
            && string.Equals(tab, DriversTabParameter, StringComparison.OrdinalIgnoreCase))
        {
            _pendingDriversTab = true;
            TryApplyPendingTab(attemptsLeft: 1);
        }
    }

    /// <summary>
    /// Prepara y aplica el destino pendiente. Si se navega a Configuración por
    /// primera vez, OnNavigatedTo corre antes del Loaded: los ítems se marcan desde
    /// ya y el SelectedItem/visibilidad se fijan en OnLoaded, cuando el SelectorBar
    /// ya realizó su plantilla.
    ///
    /// El SelectorBar puede ignorar en silencio un SelectedItem asignado antes de
    /// terminar de cargar, así que si el destino no quedó aplicado se reintenta en
    /// la cola de baja prioridad en vez de perder el pedido.
    /// </summary>
    private void TryApplyPendingTab(int attemptsLeft)
    {
        if (!_pendingDriversTab) return;
        if (ConfigTabs == null || DriversTabItem == null) return;

        // Se marcan los ítems además de SelectedItem: si el SelectorBar todavía no
        // aplicó su plantilla, su sincronización inicial parte de IsSelected y el
        // "Inicio" del XAML volvía a ganar. Con la flecha ya puesta en el ítem, la
        // selección sobrevive a esa carga.
        if (HomeTabItem.IsSelected) HomeTabItem.IsSelected = false;
        if (!DriversTabItem.IsSelected) DriversTabItem.IsSelected = true;

        // Antes del Loaded solo se deja preparado el estado de los ítems; la
        // visibilidad de los paneles y el refresco se hacen con la página ya
        // cargada (ver OnLoaded).
        if (!IsLoaded) return;
        if (!ReferenceEquals(ConfigTabs.SelectedItem, DriversTabItem))
            ConfigTabs.SelectedItem = DriversTabItem;
        ApplyTabVisibility();

        if (ReferenceEquals(ConfigTabs.SelectedItem, DriversTabItem)
            && DriversTab.Visibility == Visibility.Visible)
        {
            _pendingDriversTab = false;
            return;
        }

        if (attemptsLeft > 0)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                () => TryApplyPendingTab(attemptsLeft - 1));
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;
        try
        {
            MinimizeToTrayToggle.IsOn = _settingsService.Get("window.minimizeToTray", true);
            TrayMetricsToggle.IsOn = !_settingsService.Get("tray.optimizePerformance", true);
            TrayEcoModeToggle.IsOn = _settingsService.Get("tray.ecoMode", true);
            DeveloperLogsToggle.IsOn = _settingsService.Get("logging.developerLogs", false);
            LaunchAtStartupToggle.IsOn = _startupService.IsEnabled();
            StartMinimizedToggle.IsOn = _settingsService.Get("window.startMinimized", false);
            // Estilo de las cards de la biblioteca de juegos: 2D (tarjetas planas) ↔ 3D
            // (cartuchos con profundidad). Por defecto, 3D: es el estilo con el que la
            // biblioteca arranca si el usuario nunca eligió. El ajuste lo relee la
            // biblioteca en OnNavigatedTo, así que se aplica al volver.
            if (_settingsService.Get("biblioteca.cards3D", true)) LibraryStyle3DTile.IsChecked = true;
            else LibraryStyle2DTile.IsChecked = true;
            // Inclinación de las cards 3D al pasar el mouse: activada de fábrica.
            CardTiltToggle.IsOn = _settingsService.Get("biblioteca.cards3D.tilt", true);

            // Paleta de la apariencia 3D antes de pintar las miniaturas y las muestras.
            WHPO_UI.Controls.CardSkin.Refresh();
            PaintLibraryStylePreviews();
            BuildCardSkinPicker();

            // Pestaña Apariencia: los controles reflejan el ajuste VIGENTE (el que tienen
            // puesto las superficies ahora mismo), no el guardado en disco: ese se escribe
            // 400 ms después del último movimiento del deslizador, así que puede quedar un
            // paso atrás. Al volver a entrar a la página (queda en caché) el deslizador
            // aparecía en un valor que no era el que estaba aplicado, y llevarlo "de vuelta"
            // a ese valor no cambiaba nada: se ve como que la transparencia no funciona. El
            // ajuste guardado sigue mandando al arrancar, cuando los dos valen lo mismo.
            PanelTransparencySlider.Value = Math.Clamp(
                WHPO_UI.PanelAppearance.TransparencyPercent,
                0.0, WHPO_UI.PanelAppearance.MaxTransparencyPercent);
            PanelBlurSlider.Value = Math.Clamp(WHPO_UI.PanelAppearance.BlurPercent, 0.0, 100.0);
            UpdatePanelTransparencyText();
            UpdatePanelBlurText();
            UpdateBlurAvailability();

            // Normaliza el valor del registro de inicio (agrega/quita el flag de
            // minimizado según la opción actual) y limpia feedbacks viejos.
            SyncStartupRegistration();
            UpdateStartMinimizedHint();
            Feedback.Set(StartupFeedbackText, null);
            Feedback.Set(MinimizedFeedbackText, null);

            ApplyConfigTabsLanguage();
            // La pestaña pedida por navegación se selecciona recién acá: ver
            // TryApplyPendingTab (OnNavigatedTo llega antes del Loaded). Si el
            // SelectorBar todavía no realizó sus ítems, el helper reintenta solo.
            TryApplyPendingTab(attemptsLeft: 2);
            BuildThemePicker();
            UpdateDeveloperLogsSize();
            UpdateCacheSize();
            RefreshDriversTab();
            App.MainWindowInstance?.UpdateTrayStatus();
            // El chequeo de actualizaciones es global (botón del navbar en MainWindow):
            // acá solo se refleja si esta build es de desarrollo.
            UpdateDevelopmentToolsVisibility();
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// Al salir de la página se vuelca el guardado pendiente de la transparencia: el
    /// temporizador de 400 ms puede no llegar a dispararse si el usuario se va enseguida, y
    /// el ajuste quedaba sin persistir (al volver, el deslizador y las superficies no
    /// contaban la misma historia).
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_appearanceSaveTimer != null)
            {
                _appearanceSaveTimer.Stop();
                _appearanceSaveTimer = null;
                WHPO_UI.PanelAppearance.SaveCurrent();
            }
        }
        catch { }
    }

    /// <summary>
    /// Arma la grilla de temas de la card de apariencia: una card por tema con su miniatura,
    /// agrupadas por tipo de fondo (Entorno / Degradado / Planos) y con el filtro Todos / Oscuro /
    /// Claro. Reemplaza al desplegable, que no mostraba nada del tema hasta aplicarlo.
    /// Se vuelve a llamar al cambiar de idioma (las cards se etiquetan al armarlas).
    /// </summary>
    private void BuildThemePicker()
    {
        try
        {
            ThemePickerUi.Build(ThemeGridHost, ThemeFilterBar, _themeService.CurrentTheme, ApplyThemeSelection);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Configuración: no se pudo armar el selector de temas: {ex.Message}");
        }
    }

    /// <summary>
    /// Elige un tema desde la grilla: cada selección explícita de un tema de entorno restablece
    /// transparencia y blur a los valores de diseño, aunque ya sea el tema vigente. Si es otro,
    /// se persiste y la app se reinicia para arrancar COMPLETA con la apariencia nueva.
    /// </summary>
    private void ApplyThemeSelection(AppTheme theme)
    {
        if (_isLoading) return;

        bool isCurrentTheme = theme == _themeService.CurrentTheme;
        bool resetEnvironmentAppearance = WHPO_UI.PanelAppearance.ResetEnvironmentDefaultsOnSelection(theme);

        if (isCurrentTheme)
        {
            if (resetEnvironmentAppearance)
            {
                _isLoading = true;
                try
                {
                    PanelTransparencySlider.Value = WHPO_UI.ThemeCatalog.DefaultPanelTransparency(theme);
                    PanelBlurSlider.Value = WHPO_UI.ThemeCatalog.DefaultPanelBlur(theme);
                    UpdatePanelTransparencyText();
                    UpdatePanelBlurText();
                }
                finally
                {
                    _isLoading = false;
                }
            }
            return;
        }

        ThemePickerUi.MarkSelected(theme);
        // Cada apariencia estrena su estilo de cartuchos y consola: si el usuario había
        // fijado un color propio, se vuelve a «Del tema» para que acompañe al tema nuevo
        // en vez de arrastrar el plástico de la apariencia anterior.
        WHPO_UI.Controls.CardSkin.ResetToTheme();
        _themeService.SetTheme(theme);
        App.MainWindowInstance?.RestartForThemeChange();
    }

    /// <summary>
    /// Transparencia de los paneles: se aplica AL INSTANTE (PanelAppearance muta el
    /// alfa de los pinceles del tema y la UI repinta sola) — a diferencia del tema, que
    /// sí reinicia la app, porque acá no hay que reconstruir ninguna página.
    /// </summary>
    private void PanelTransparencySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdatePanelTransparencyText();
        if (_isLoading) return;
        WHPO_UI.PanelAppearance.SetTransparencyPercent(
            Math.Clamp(e.NewValue, 0.0, WHPO_UI.PanelAppearance.MaxTransparencyPercent), persist: false);

        // La barra interna de pestañas se pinta a mano (WinUI cachea el {ThemeResource}):
        // es un panel más, así que sigue al deslizador igual que el navbar de la ventana.
        RepaintConfigNavBar();

        // El archivo de ajustes se escribe UNA vez, 400 ms después del último tick: al
        // arrastrar, el deslizador dispara muchos cambios por segundo y no tiene sentido
        // guardar en disco en cada uno (ni dejar valores intermedios escritos).
        ScheduleAppearanceSave();
    }

    /// <summary>
    /// Blur (desenfoque de los paneles): mismo trato que la transparencia —se aplica en
    /// caliente— y el porcentaje es la INTENSIDAD: elige el radio del desenfoque de la foto que
    /// queda detrás de cada panel (cards, chips, menú, barra de título). La copia de la foto se
    /// decodifica una vez y solo se vuelve a desenfocar al mover el deslizador, así que el paso es
    /// gradual y no un encendido; 0 % apaga la capa y los paneles vuelven al relleno sólido del
    /// tema (ver PanelAppearance.SetBlurPercent).
    /// </summary>
    private void PanelBlurSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdatePanelBlurText();
        if (_isLoading) return;
        WHPO_UI.PanelAppearance.SetBlurPercent(Math.Clamp(e.NewValue, 0.0, 100.0), persist: false);
        ScheduleAppearanceSave();
    }

    private DispatcherQueueTimer? _appearanceSaveTimer;

    private void ScheduleAppearanceSave()
    {
        if (_appearanceSaveTimer == null)
        {
            _appearanceSaveTimer = DispatcherQueue.CreateTimer();
            _appearanceSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _appearanceSaveTimer.Tick += (_, _) =>
            {
                _appearanceSaveTimer.Stop();
                WHPO_UI.PanelAppearance.SaveCurrent();
            };
        }
        _appearanceSaveTimer.Stop();
        _appearanceSaveTimer.Start();
    }

    /// <summary>Refleja el valor del deslizador como porcentaje (0 % = paneles opacos).</summary>
    private void UpdatePanelTransparencyText()
    {
        if (PanelTransparencyValueText != null && PanelTransparencySlider != null)
            PanelTransparencyValueText.Text = $"{Math.Round(PanelTransparencySlider.Value)} %";
    }

    /// <summary>Refleja el valor del deslizador de blur como porcentaje (0 % = apagado).</summary>
    private void UpdatePanelBlurText()
    {
        if (PanelBlurValueText != null && PanelBlurSlider != null)
            PanelBlurValueText.Text = $"{Math.Round(PanelBlurSlider.Value)} %";
    }

    private void OnMinimizeToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _settingsService.Set("window.minimizeToTray", MinimizeToTrayToggle.IsOn);
        _settingsService.Save();
    }

    private void OnTrayMetricsToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _settingsService.Set("tray.optimizePerformance", !TrayMetricsToggle.IsOn);
        _settingsService.Save();
        App.MainWindowInstance?.UpdateTrayStatus();
    }

    private void OnTrayEcoModeToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _settingsService.Set("tray.ecoMode", TrayEcoModeToggle.IsOn);
        _settingsService.Save();
    }

    /// <summary>
    /// Selector de estilo de las cards de la biblioteca: 2D (tarjetas planas con el banner)
    /// ↔ 3D (cartuchos con profundidad, arrastrables a la consola del pie). Persiste la
    /// preferencia "biblioteca.cards3D", la misma que la biblioteca relee en OnNavigatedTo
    /// para reconstruir la grilla: el cambio se ve al volver, sin reiniciar la app.
    /// El guard de carga evita que reflejar el valor guardado lo vuelva a escribir.
    /// </summary>
    private void LibraryStyleTile_Checked(object sender, RoutedEventArgs e)
    {
        bool threeD = ReferenceEquals(sender, LibraryStyle3DTile);
        if (LibraryStyleCheck2D != null)
            LibraryStyleCheck2D.Visibility = threeD ? Visibility.Collapsed : Visibility.Visible;
        if (LibraryStyleCheck3D != null)
            LibraryStyleCheck3D.Visibility = threeD ? Visibility.Visible : Visibility.Collapsed;
        // El color de carcasa solo tiene sentido con cartuchos: en 2D no hay qué teñir.
        if (CardSkinCard != null)
            CardSkinCard.Visibility = threeD ? Visibility.Visible : Visibility.Collapsed;
        if (_isLoading) return;
        _settingsService.Set("biblioteca.cards3D", threeD);
        _settingsService.Save();
    }

    /// <summary>
    /// Arma el selector de estilo 3D: una muestra por preset de CardSkin, con el color
    /// real del plástico y la etiqueta ya traducida por I18n.
    /// Se rearma al cambiar de idioma; la selección vigente se refleja sin guardar nada.
    /// </summary>
    private void BuildCardSkinPicker()
    {
        if (CardSkinHost == null) return;
        WHPO_UI.Controls.CardSkin.Refresh();
        _buildingCardSkin = true;
        try
        {
            CardSkinHost.Children.Clear();
            _cardSkinSwatches.Clear();
            string current = WHPO_UI.Controls.CardSkin.CurrentId;

            foreach (var option in WHPO_UI.Controls.CardSkin.Options)
            {
                // «Del tema» no es una muestra de color: es el atajo para volver a la
                // apariencia de la app. Va más grande y con el glifo de restaurar.
                bool isTheme = string.Equals(option.Id, WHPO_UI.Controls.CardSkin.ThemeId,
                    StringComparison.OrdinalIgnoreCase);

                // «Del tema» no es una muestra de color: es el glifo de restaurar, MÁS
                // GRANDE que los demás y sin círculo de fondo. La selección se marca con
                // el glifo en acento, no con un aro alrededor.
                FontIcon? themeGlyph = null;
                var dot = new Grid { Width = 44, Height = 44 };
                Border ring;
                if (isTheme)
                {
                    themeGlyph = new FontIcon
                    {
                        Glyph = "\uE7A7", FontSize = 36,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = WHPO_UI.ThemeBrushes.Get("PrimaryTextBrush")
                    };
                    if (Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var symbolFont)
                        && symbolFont is FontFamily family)
                        themeGlyph.FontFamily = family;
                    dot.Children.Add(themeGlyph);
                    // Solo rellena el contrato del diccionario: no se agrega al árbol visual.
                    ring = new Border { Width = 44, Height = 44, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                }
                else
                {
                    const double ringSize = 30;
                    ring = new Border
                    {
                        Width = ringSize, Height = ringSize, CornerRadius = new CornerRadius(ringSize * 0.5),
                        Background = new SolidColorBrush(option.Color is { } fixedColor
                            ? WHPO_UI.Controls.CardSkin.ToUi(fixedColor)
                            : WHPO_UI.Controls.CardSkin.Current.Swatch),
                        BorderThickness = new Thickness(2),
                        // Aro de la grilla del tema: mantiene visible una muestra blanca o negra
                        // sobre un fondo parecido. El elegido lo pasa a acento en UpdateCardSkinSelection.
                        BorderBrush = WHPO_UI.ThemeBrushes.Get("SensorGridLineBrush")
                    };
                    dot.Children.Add(ring);
                }

                // El tilde va en negro o blanco según el brillo de la muestra, para que se lea
                // tanto en el blanco como en el negro. En «Del tema» no se usa: la selección
                // la marca el propio glifo en acento.
                var check = new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(option.Color is { } checkColor
                        && (0.299 * checkColor.R + 0.587 * checkColor.G + 0.114 * checkColor.B) <= 150
                            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                            : Windows.UI.Color.FromArgb(255, 24, 26, 30)),
                    Visibility = Visibility.Collapsed
                };
                dot.Children.Add(check);
                var label = new TextBlock
                {
                    // Traducida al armar: el contenido de un RadioButton no está en el
                    // árbol visual hasta que se aplica la plantilla, y el walker de I18n
                    // no lo alcanzaría. Al cambiar de idioma se rearma el selector.
                    Text = I18n.T(option.Label),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Foreground = WHPO_UI.ThemeBrushes.Get("SecondaryTextBrush")
                };

                var swatch = new RadioButton
                {
                    Style = (Style)Resources["CardSkinSwatch"],
                    GroupName = "CardSkinPreset",
                    Tag = option.Id,
                    Content = new StackPanel { Spacing = 4, Children = { dot, label } },
                    IsChecked = string.Equals(option.Id, current, StringComparison.OrdinalIgnoreCase)
                };
                swatch.Checked += CardSkinTile_Checked;

                CardSkinHost.Children.Add(swatch);
                _cardSkinSwatches[option.Id] = (ring, check, themeGlyph);
            }
        }
        finally
        {
            _buildingCardSkin = false;
        }

        UpdateCardSkinSelection();
        UpdateCardSkinCustomRow();
    }

    /// <summary>
    /// Refresca la fila de color exacto: la muestra chica y el texto muestran el color
    /// vigente. Si el ajuste es un color a mano se marca con el aro de acento, igual que
    /// las muestras; si no, queda el color que está usando la carcasa ahora.
    /// </summary>
    private void UpdateCardSkinCustomRow()
    {
        if (CardSkinCustomSwatch == null) return;
        var palette = WHPO_UI.Controls.CardSkin.Current;
        bool custom = WHPO_UI.Controls.CardSkin.TryParseCustom(palette.Id, out var parsed);
        CardSkinCustomSwatch.Background = new SolidColorBrush(
            WHPO_UI.Controls.CardSkin.ToUi(custom ? parsed : palette.Plastic));
        CardSkinCustomSwatch.BorderBrush = custom
            ? WHPO_UI.ThemeBrushes.Get("AccentBrush")
            : WHPO_UI.ThemeBrushes.Get("SensorGridLineBrush");
        if (CardSkinCustomHint != null)
            CardSkinCustomHint.Text = custom
                ? WHPO_UI.Controls.CardSkin.FormatCustom(parsed).Replace(
                    WHPO_UI.Controls.CardSkin.CustomPrefix, "#", StringComparison.OrdinalIgnoreCase)
                : I18n.T("Elegí cualquier color, además de las muestras de arriba.");
    }

    /// <summary>
    /// Selector de color exacto del plástico de los cartuchos y la consola. Se aplica al
    /// confirmar, con el mismo camino que un preset: se guarda, se relee la paleta y se
    /// repintan la vista previa y las miniaturas.
    /// </summary>
    private async void CardSkinCustomButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var palette = WHPO_UI.Controls.CardSkin.Current;
            var seed = WHPO_UI.Controls.CardSkin.TryParseCustom(palette.Id, out var current)
                ? current
                : palette.Plastic;
            var picker = new ColorPicker
            {
                Color = WHPO_UI.Controls.CardSkin.ToUi(seed),
                ColorSpectrumShape = ColorSpectrumShape.Box,
                IsColorSpectrumVisible = true,
                IsColorPreviewVisible = true,
                IsAlphaEnabled = false,
                IsAlphaSliderVisible = false,
                IsAlphaTextInputVisible = false,
                IsColorSliderVisible = true,
                IsColorChannelTextInputVisible = true,
                IsHexInputVisible = true,
                IsMoreButtonVisible = false,
                MinWidth = 320,
                MinHeight = 340
            };
            // Vista previa EN VIVO: cada movimiento del selector publica una paleta solo
            // en memoria y repinta la muestra y la consola con el mismo código que dibuja
            // la biblioteca, así se ve exactamente cómo van a quedar. Se coalescen los
            // cambios para no rearmar el chasis en cada pixel de arrastre.
            picker.ColorChanged += (_, args) =>
            {
                WHPO_UI.Controls.CardSkin.Preview(WHPO_UI.Controls.CardSkin.FromUi(args.NewColor));
                QueueCardSkinPreviewPaint();
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = I18n.T("Color de los cartuchos y la consola"),
                Content = picker,
                PrimaryButtonText = I18n.T("Aplicar"),
                CloseButtonText = I18n.T("Cancelar"),
                DefaultButton = ContentDialogButton.Primary
            };
            bool apply = await dialog.ShowAsync() == ContentDialogResult.Primary;
            if (apply)
                WHPO_UI.Controls.CardSkin.SetCustom(WHPO_UI.Controls.CardSkin.FromUi(picker.Color));

            // Refresh relee el ajuste guardado: si se aplicó toma el color nuevo y, si se
            // canceló, descarta la vista previa y vuelve al que estaba.
            WHPO_UI.Controls.CardSkin.Refresh();
            _customPreviewTimer?.Stop();
            _customPreviewClock.Reset();
            UpdateCardSkinSelection();
            UpdateCardSkinCustomRow();
            _sampleCartridge = null;
            PaintLibraryStylePreviews();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Configuración: color exacto de los cartuchos: {ex.Message}");
        }
    }

    // Repintado de la vista previa del color exacto. El selector dispara ColorChanged en
    // cada pixel de arrastre y rearmar el chasis (bitmap + PNG) no es gratis, así que se
    // combinan dos cosas: un refresco inmediato como máximo cada 55 ms (feedback mientras
    // se arrastra) y un cierre diferido 70 ms después del último cambio, para que el color
    // final también se vea aunque el arrastre termine dentro de la ventana.
    private readonly Stopwatch _customPreviewClock = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _customPreviewTimer;

    private void QueueCardSkinPreviewPaint()
    {
        if (!_customPreviewClock.IsRunning || _customPreviewClock.ElapsedMilliseconds >= 55)
            PaintCustomPreviewNow();

        if (_customPreviewTimer == null)
        {
            _customPreviewTimer = DispatcherQueue.CreateTimer();
            _customPreviewTimer.Interval = TimeSpan.FromMilliseconds(70);
            _customPreviewTimer.IsRepeating = false;
            _customPreviewTimer.Tick += (_, _) => PaintCustomPreviewNow();
        }
        _customPreviewTimer.Stop();
        _customPreviewTimer.Start();
    }

    private void PaintCustomPreviewNow()
    {
        _customPreviewClock.Restart();
        PaintLibraryStylePreviews();
    }

    /// <summary>Marca la muestra del preset vigente con el aro de acento y el tilde.</summary>
    private void UpdateCardSkinSelection()
    {
        string current = WHPO_UI.Controls.CardSkin.CurrentId;
        foreach (var (id, parts) in _cardSkinSwatches)
        {
            bool selected = string.Equals(id, current, StringComparison.OrdinalIgnoreCase);
            bool isTheme = string.Equals(id, WHPO_UI.Controls.CardSkin.ThemeId, StringComparison.OrdinalIgnoreCase);

            // El glifo de restaurar no lleva círculo: se tiñe de acento cuando es el elegido.
            if (isTheme)
            {
                if (parts.ThemeGlyph is { } glyph)
                    glyph.Foreground = selected
                        ? WHPO_UI.ThemeBrushes.Get("AccentBrush")
                        : WHPO_UI.ThemeBrushes.Get("PrimaryTextBrush");
                parts.Check.Visibility = Visibility.Collapsed;
                continue;
            }

            parts.Ring.BorderBrush = selected
                ? WHPO_UI.ThemeBrushes.Get("AccentBrush")
                : WHPO_UI.ThemeBrushes.Get("SensorGridLineBrush");
            parts.Check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Inclinación de las cards 3D al pasar el mouse. La biblioteca relee el ajuste en
    /// cada visita y se lo pasa a las CartridgeTile nuevas; el asiento del dock queda
    /// clavado igual (ahí el tilt está desactivado a propósito).
    /// </summary>
    private void OnCardTiltToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        _settingsService.Set("biblioteca.cards3D.tilt", CardTiltToggle.IsOn);
        _settingsService.Save();
    }

    /// <summary>
    /// Elige el color de carcasa de los cartuchos 3D y de la consola del pie. Se guarda al
    /// instante ("biblioteca.cards3D.skin") y la biblioteca lo toma al volver, igual que el
    /// selector 2D/3D. La miniatura del cartucho y la de la consola se repintan acá mismo.
    /// </summary>
    private void CardSkinTile_Checked(object sender, RoutedEventArgs e)
    {
        if (_buildingCardSkin) return;
        if (sender is not RadioButton { Tag: string id }) return;

        if (!string.Equals(WHPO_UI.Controls.CardSkin.CurrentId, id, StringComparison.OrdinalIgnoreCase))
        {
            _settingsService.Set(WHPO_UI.Controls.CardSkin.SettingKey, id);
            _settingsService.Save();
            WHPO_UI.Controls.CardSkin.Refresh();
        }

        UpdateCardSkinSelection();
        _sampleCartridge = null;
        PaintLibraryStylePreviews();
    }

    /// <summary>Hover de las tarjetas del selector: levante de 1,5 % como en el selector de temas.</summary>
    private static void AttachPickerTileHover(RadioButton tile)
    {
        var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        tile.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        tile.RenderTransform = scale;
        tile.PointerEntered += (_, _) => { scale.ScaleX = 1.015; scale.ScaleY = 1.015; };
        tile.PointerExited += (_, _) => { scale.ScaleX = 1; scale.ScaleY = 1; };
    }

    /// <summary>
    /// Miniaturas del selector de estilo:
    /// - 2D: carátula de MUESTRA derivada del acento del tema (no hay un banner de juego
    ///   empaquetado, y una foto fija no acompañaría al resto de las paletas).
    /// - 3D: la cara REAL del cartucho que pinta la biblioteca (CartridgeModel), en PNG con
    ///   transparencia; se decodifica una sola vez y se comparte entre visitas a la página.
    /// </summary>
    private void PaintLibraryStylePreviews()
    {
        try
        {
            if (ThemeBrushes.Get("AccentBrush") is SolidColorBrush accent)
            {
                var c = accent.Color;
                static byte Up(byte v, int add) => (byte)Math.Clamp(v + add, 0, 255);
                static byte Down(byte v) => (byte)(v * 0.5);
                LibraryStyle2DBanner.Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, Up(c.R, 18), Up(c.G, 22), Up(c.B, 30)), Offset = 0 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, Down(c.R), Down(c.G), Down(c.B)), Offset = 0.55 },
                        new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0x0E, 0x11, 0x16), Offset = 1 }
                    }
                };
            }

            // La consola de la miniatura usa la MISMA paleta que el dock real: al cambiar
            // de carcasa, la muestra de 3D cuenta el color que va a tener la biblioteca.
            var skin = WHPO_UI.Controls.CardSkin.Current;
            if (LibraryStyle3DDock != null)
            {
                LibraryStyle3DDock.Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = WHPO_UI.Controls.CardSkin.ToUi(skin.Light(0.15f)), Offset = 0 },
                        new GradientStop { Color = WHPO_UI.Controls.CardSkin.ToUi(skin.Dark(0.55f)), Offset = 1 }
                    }
                };
            }

            if (_sampleCartridge == null || !string.Equals(_sampleSkinKey, skin.Key, StringComparison.Ordinal))
            {
                byte[] png = WHPO_UI.Controls.CartridgeModel.PaintSampleFacePng(192, 281);
                using var stream = new MemoryStream(png);
                stream.Position = 0;
                using var ras = stream.AsRandomAccessStream();
                var image = new BitmapImage();
                image.SetSource(ras);
                _sampleCartridge = image;
                _sampleSkinKey = skin.Key;
            }
            LibraryStyle3DPreview.Source = _sampleCartridge;
            PaintCardSkinStage(_sampleCartridge);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Configuración: miniaturas del estilo de cards: {ex.Message}");
        }
    }

    /// <summary>
    /// Vista previa de la apariencia 3D: no es un dibujo aparte. La card es la MISMA cara
    /// de muestra de la biblioteca y la consola la arma CartridgeDock con su chasis, su
    /// puerto, su labio y su LED reales, con el plástico vigente de CardSkin. Se rehace al
    /// elegir cada muestra (y al cargar la página) para que el color cambie en vivo.
    /// </summary>
    private void PaintCardSkinStage(ImageSource? sample)
    {
        if (CardSkinStageHost == null) return;
        CardSkinStageHost.Content = WHPO_UI.Controls.CartridgeDock.CreateAppearancePreview(
            sample, previewHeight: 320);
    }

    /// <summary>Cara de muestra del cartucho (PNG ya decodificado), compartida entre visitas.
    /// Se repinta cuando cambia la paleta de la apariencia 3D (ver _sampleSkinKey).</summary>
    private static BitmapImage? _sampleCartridge;
    private static string? _sampleSkinKey;

    private void OnDeveloperLogsToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        bool enabled = DeveloperLogsToggle.IsOn;
        _settingsService.Set("logging.developerLogs", enabled);
        _settingsService.Save();
        // Aplica al instante: deja/escribe el app.log según el estado del toggle.
        _loggingService.SetFileLoggingEnabled(enabled);
        UpdateDeveloperLogsSize();
    }

    private async void DeleteLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Borrar logs"),
            Content = I18n.T("¿Borrar todos los logs de desarrollo? Esta acción no se puede deshacer."),
            PrimaryButtonText = I18n.T("Borrar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        long freed = _loggingService.GetLogFilesSize();
        _loggingService.DeleteLogFiles();
        UpdateDeveloperLogsSize();
        Feedback.Success(DeleteLogsFeedbackText, I18n.T("Logs borrados: {0} liberados.", FormatBytes(freed)));
    }

    private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = _loggingService.LogDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ConfiguracionPage: abrir carpeta de logs: {ex.Message}");
        }
    }

    private void UpdateDeveloperLogsSize()
    {
        long size = _loggingService.GetLogFilesSize();
        DeveloperLogsSizeText.Text = I18n.T("Tamaño de los logs: {0}", FormatBytes(size));
        // Sin logs no hay nada que borrar: el botón queda deshabilitado (y el feedback limpio).
        DeleteLogsButton.IsEnabled = size > 0;
        if (size == 0) DeleteLogsFeedbackText.Text = "";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    // ===== Caché de la aplicación =====
    // Cubre todo lo que la app guarda regenerable en disco: banners e íconos de
    // juegos (GestionarProcesosPage), la biblioteca de juegos cacheada y el
    // componente descargado de Overclock USB. NO toca settings.json
    // (configuración) ni los logs (botón "Borrar logs").

    private static string WhpoCacheDir => WHPO.Core.AppPaths.RootDir;

    private void UpdateCacheSize()
    {
        CacheSizeText.Text = I18n.T("Tamaño de la caché: {0}", FormatBytes(GetCacheSizeBytes()));
    }

    private long GetCacheSizeBytes()
    {
        long total = 0;
        try
        {
            var bannerDir = GestionarProcesosPage.BannerCacheDir;
            if (Directory.Exists(bannerDir))
                total += Directory.EnumerateFiles(bannerDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            // La biblioteca cacheada lleva la versión en el nombre (gamescache.v8.json):
            // se cuentan todas las variantes para no dejar cachés viejas sin medir.
            foreach (var file in Directory.EnumerateFiles(WhpoCacheDir, "gamescache*.json*"))
            {
                try { total += new FileInfo(file).Length; }
                catch { }
            }

 // Componente descargado de Overclock USB (el binario del filtro cacheado).
            var compDir = _usbOverclockService.ComponentCachePath;
            if (Directory.Exists(compDir))
                total += Directory.EnumerateFiles(compDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch { }
        return total;
    }

    // ===================== Drivers del sistema (PawnIO + hidusbf) =====================

    /// <summary>
    /// Relee el estado de los dos drivers que WinForge usa para acceder al hardware.
    /// Se llama al abrir la página, al entrar a la pestaña Drivers y al cambiar de idioma.
    /// </summary>
    private void RefreshDriversTab()
    {
        RefreshPawnIoCard();
        RefreshHidUsbfCard();
    }

    /// <summary>
    /// Refleja el estado real de PawnIO: instalado o no, versión y si el instalador
    /// que trae WinForge es más nuevo. Nunca se ejecuta mientras hay una
    /// instalación/desinstalación en curso (el feedback de progreso manda).
    /// </summary>
    private void RefreshPawnIoCard()
    {
        if (_pawnIoBusy) return;

        var status = _pawnIoService.GetStatus();
        bool updateAvailable = status.Installed
            && !SameDriverVersion(status.Version, status.InstallerVersion);

        SetDriverStatusPill(
            PawnIoStatusPill,
            PawnIoStatusText,
            status.Installed ? I18n.T("Instalado") : I18n.T("No instalado"),
            status.Installed ? Feedback.SuccessBrush : ThemeBrushes.Get("SecondaryTextBrush"));

        PawnIoVersionText.Text = status.Installed
            ? (updateAvailable
                ? I18n.T("Versión instalada: {0} · Versión disponible: {1}", status.Version, status.InstallerVersion)
                : I18n.T("Versión instalada: {0}", status.Version))
            : I18n.T("Versión {0} disponible", status.InstallerVersion);

        PawnIoInstallButton.Visibility = status.Installed && !updateAvailable
            ? Visibility.Collapsed
            : Visibility.Visible;
        PawnIoInstallButton.Content = status.Installed ? I18n.T("Actualizar PawnIO") : I18n.T("Instalar PawnIO");
        PawnIoUninstallButton.Visibility = status.Installed ? Visibility.Visible : Visibility.Collapsed;
        PawnIoUninstallButton.Content = I18n.T("Desinstalar PawnIO");

        // Reinicio pendiente: la badge dice qué falta y el botón es la acción.
        // El feedback de resultado no repite la instrucción.
        PawnIoRestartButton.Content = I18n.T("Reiniciar WinForge");
        PawnIoRestartButton.Visibility = _pawnIoRestartPending ? Visibility.Visible : Visibility.Collapsed;
        PawnIoRestartBadge.Visibility = _pawnIoRestartPending ? Visibility.Visible : Visibility.Collapsed;
        if (_pawnIoRestartPending)
        {
            SetDriverStatusPill(PawnIoRestartBadge, PawnIoRestartBadgeText,
                I18n.T("Requiere reinicio"), Feedback.WarningBrush);
        }
    }

    /// <summary>
    /// Refleja el estado real de hidusbf: si el servicio y el binario del filtro están
    /// listos. Si el servicio quedó registrado pero falta el binario, la tarjeta lo
    /// muestra como instalación incompleta y ofrece repararla.
    /// </summary>
    private void RefreshHidUsbfCard()
    {
        if (_hidUsbfBusy) return;

        var guard = _usbOverclockService.GetGuardStatus();
        bool ready = _usbOverclockService.IsComponentReady();

        if (ready)
        {
            SetDriverStatusPill(HidUsbfStatusPill, HidUsbfStatusText,
                I18n.T("Instalado"), Feedback.SuccessBrush);

            string variant = HidUsbfVariantLabel(guard);
            HidUsbfDetailText.Text = variant.Length > 0
                ? I18n.T("Variante instalada: {0}", variant)
                : I18n.T("Servicio del filtro instalado.");

            HidUsbfInstallButton.Visibility = Visibility.Collapsed;
            HidUsbfUninstallButton.Visibility = Visibility.Visible;
        }
        else if (guard.ServiceInstalled)
        {
            SetDriverStatusPill(HidUsbfStatusPill, HidUsbfStatusText,
                I18n.T("Instalación incompleta"), Feedback.WarningBrush);
            HidUsbfDetailText.Text = I18n.T("El servicio está registrado, pero falta el binario del filtro. Repará la instalación.");
            HidUsbfInstallButton.Visibility = Visibility.Visible;
            HidUsbfInstallButton.Content = I18n.T("Reparar instalación");
            HidUsbfUninstallButton.Visibility = Visibility.Visible;
        }
        else
        {
            SetDriverStatusPill(HidUsbfStatusPill, HidUsbfStatusText,
                I18n.T("No instalado"), ThemeBrushes.Get("SecondaryTextBrush"));
            HidUsbfDetailText.Text = I18n.T("Se descarga el paquete oficial de hidusbf y se verifica el hash SHA-256 antes de instalar.");
            HidUsbfInstallButton.Visibility = Visibility.Visible;
            HidUsbfInstallButton.Content = I18n.T("Descargar e instalar");
            HidUsbfUninstallButton.Visibility = Visibility.Collapsed;
        }

        HidUsbfUninstallButton.Content = I18n.T("Desinstalar hidusbf");
    }

    /// <summary>
    /// Pinta la pastilla de estado de un driver con el color de su estado (verde
    /// instalado, ámbar incompleto, gris no instalado) y un tinte suave del mismo color.
    /// </summary>
    private static void SetDriverStatusPill(Border pill, TextBlock text, string label, SolidColorBrush brush)
    {
        text.Text = label;
        text.Foreground = brush;
        pill.Background = new SolidColorBrush(brush.Color) { Opacity = 0.14 };
    }

    /// <summary>Nombre legible de la variante instalada del filtro hidusbf y su techo.</summary>
    private static string HidUsbfVariantLabel(UsbOverclockGuardStatus guard)
    {
        string name = guard.InstalledVariant switch
        {
            "nopatch" => "NoPatch",
            "1khz" => "1 kHz",
            "2khz-4khz" => "2-4 kHz",
            "4khz-8khz" => "4-8 kHz",
            _ => ""
        };

        return name.Length == 0 || guard.CeilingHz <= 0 ? "" : $"{name} · {guard.CeilingHz} Hz";
    }

    /// <summary>
    /// Compara versiones del registro con la del instalador empaquetado sin caer en
    /// "2.2.0" vs "2.2.0.0" (misma versión): solo importan mayor, menor y build.
    /// </summary>
    private static bool SameDriverVersion(string? installed, string bundled)
    {
        if (!Version.TryParse(installed, out var a) || !Version.TryParse(bundled, out var b))
            return string.Equals(installed, bundled, StringComparison.OrdinalIgnoreCase);

        return a.Major == b.Major
            && a.Minor == b.Minor
            && Math.Max(a.Build, 0) == Math.Max(b.Build, 0);
    }

    private async void PawnIoInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pawnIoBusy) return;
        _pawnIoBusy = true;

        bool force = _pawnIoService.GetStatus().Installed;
        SetPawnIoBusy(true);
        // persistent: el progreso queda hasta que lo reemplaza el resultado (o falla).
        Feedback.Set(PawnIoFeedbackText, I18n.T("Descargando e instalando PawnIO…"), Feedback.AccentBrush, persistent: true);

        try
        {
            var result = await Task.Run(() => _pawnIoService.InstallAsync(force));
            if (result.Success)
            {
                // Instalar el driver y no reiniciar no sirve de nada: LHM cachea la
                // versión al arrancar. Se reinicia solo, sin preguntar.
                Feedback.Success(PawnIoFeedbackText, I18n.T("PawnIO instalado. Reiniciando WinForge…"));
                _pawnIoRestartPending = true;
                RefreshDriversTab();

                var main = App.MainWindowInstance;
                if (main == null || !await main.RestartAppAsync())
                {
                    // Si el reinicio no salió, la tarjeta queda con la badge y el botón:
                    // el driver está instalado, solo falta el proceso nuevo.
                    Feedback.Error(PawnIoFeedbackText,
                        I18n.T("PawnIO instalado, pero no se pudo reiniciar WinForge. Usá Reiniciar WinForge."));
                }
                return;
            }

            Feedback.Error(PawnIoFeedbackText, I18n.T("✗ No se pudo instalar PawnIO: {0}", result.Message));
        }
        catch (Exception ex)
        {
            Feedback.Error(PawnIoFeedbackText, I18n.T("✗ No se pudo instalar PawnIO: {0}", ex.Message));
        }
        finally
        {
            _pawnIoBusy = false;
            SetPawnIoBusy(false);
        }
    }

    private async void PawnIoUninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pawnIoBusy || XamlRoot == null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Desinstalar PawnIO"),
            Content = I18n.T("¿Desinstalar PawnIO? La temperatura y la potencia del CPU y los ventiladores de la placa dejan de estar disponibles en WinForge. Otras apps que lo usen, como LibreHardwareMonitor o FanControl, también lo pierden hasta que lo reinstales."),
            PrimaryButtonText = I18n.T("Desinstalar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _pawnIoBusy = true;
        SetPawnIoBusy(true);
        Feedback.Set(PawnIoFeedbackText, I18n.T("Desinstalando PawnIO…"), Feedback.AccentBrush, persistent: true);

        try
        {
            var result = await _pawnIoService.UninstallAsync();
            if (result.Success)
            {
                Feedback.Success(PawnIoFeedbackText, I18n.T("PawnIO desinstalado correctamente."));
                _pawnIoBusy = false;
                _pawnIoRestartPending = true;
                RefreshDriversTab();
                return;
            }

            Feedback.Error(PawnIoFeedbackText, I18n.T("✗ No se pudo desinstalar PawnIO: {0}", result.Message));
        }
        catch (Exception ex)
        {
            Feedback.Error(PawnIoFeedbackText, I18n.T("✗ No se pudo desinstalar PawnIO: {0}", ex.Message));
        }
        finally
        {
            _pawnIoBusy = false;
            SetPawnIoBusy(false);
        }
    }

    private void PawnIoRestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!(App.MainWindowInstance?.RestartApp() ?? false))
            Feedback.Error(PawnIoFeedbackText, I18n.T("No se pudo reiniciar WinForge. Cerralo y abrilo a mano."));
    }

    private void SetPawnIoBusy(bool busy)
    {
        PawnIoProgressRing.IsActive = busy;
        PawnIoProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PawnIoInstallButton.IsEnabled = !busy;
        PawnIoUninstallButton.IsEnabled = !busy;
    }

    // ===================== hidusbf =====================

    /// <summary>
    /// Descarga el paquete oficial de hidusbf (verificando el SHA-256 de la variante),
    /// instala el servicio del filtro y deja la tarjeta lista para Overclock USB.
    /// </summary>
    private async void HidUsbfInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hidUsbfBusy) return;
        _hidUsbfBusy = true;
        SetHidUsbfBusy(true);
        Feedback.Set(HidUsbfFeedbackText, I18n.T("Descargando e instalando hidusbf…"), Feedback.AccentBrush);

        try
        {
            string? driverPath = await _usbOverclockService.DownloadComponentAsync();
            if (string.IsNullOrEmpty(driverPath))
            {
                string? reason = _usbOverclockService.LastComponentDownloadError;
                Feedback.Error(HidUsbfFeedbackText,
                    reason == null
                        ? I18n.T("No se pudo descargar el componente. Verificá tu conexión a internet e intentá de nuevo.")
                        : I18n.T("No se pudo descargar el componente.") + " " + I18n.T(reason));
                return;
            }

            bool installed = await _usbOverclockService.InstallFilterServiceAsync(driverPath);
            if (!installed)
            {
                Feedback.Error(HidUsbfFeedbackText,
                    I18n.T("No se pudo instalar el componente. Ejecutá WinForge como administrador e intentá de nuevo."));
                return;
            }

            _usbOverclockService.InvalidateFilterServiceCache();
            Feedback.Success(HidUsbfFeedbackText,
                I18n.T("✓ hidusbf instalado. Abrí Overclock USB para ajustar tus dispositivos."));
            _hidUsbfBusy = false;
            RefreshDriversTab();
            return;
        }
        catch (Exception ex)
        {
            Feedback.Error(HidUsbfFeedbackText, I18n.T("✗ No se pudo instalar hidusbf: {0}", ex.Message));
        }
        finally
        {
            _hidUsbfBusy = false;
            SetHidUsbfBusy(false);
        }
    }

    /// <summary>
    /// Quita el filtro de todos los dispositivos que lo tengan activo, elimina el
    /// servicio y el binario del sistema, y borra el paquete cacheado. Overclock USB
    /// vuelve a pedir el driver la próxima vez que se abra.
    /// </summary>
    private async void HidUsbfUninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_hidUsbfBusy || XamlRoot == null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Desinstalar hidusbf"),
            Content = I18n.T("¿Desinstalar hidusbf? El ajuste de la tasa de sondeo deja de funcionar en todos los dispositivos y el filtro se quita de los que lo tengan activo. Podés reinstalarlo desde acá cuando quieras."),
            PrimaryButtonText = I18n.T("Desinstalar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _hidUsbfBusy = true;
        SetHidUsbfBusy(true);
        Feedback.Set(HidUsbfFeedbackText, I18n.T("Desinstalando hidusbf…"), Feedback.AccentBrush);

        try
        {
            await Task.Run(() => _usbOverclockService.ClearComponentCache());
            _usbOverclockService.InvalidateFilterServiceCache();

            if (_usbOverclockService.IsComponentReady())
            {
                Feedback.Error(HidUsbfFeedbackText,
                    I18n.T("No se pudo desinstalar hidusbf: puede estar en uso. Reiniciá Windows e intentá de nuevo."));
                return;
            }

            Feedback.Success(HidUsbfFeedbackText,
                I18n.T("✓ hidusbf desinstalado. Overclock USB vuelve a pedir el driver cuando lo abras."));
            _hidUsbfBusy = false;
            RefreshDriversTab();
            return;
        }
        catch (Exception ex)
        {
            Feedback.Error(HidUsbfFeedbackText, I18n.T("✗ No se pudo desinstalar hidusbf: {0}", ex.Message));
        }
        finally
        {
            _hidUsbfBusy = false;
            SetHidUsbfBusy(false);
        }
    }

    private void SetHidUsbfBusy(bool busy)
    {
        HidUsbfProgressRing.IsActive = busy;
        HidUsbfProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        HidUsbfInstallButton.IsEnabled = !busy;
        HidUsbfUninstallButton.IsEnabled = !busy;
    }

    private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Limpiar caché"),
            Content = I18n.T("¿Borrar toda la caché de la aplicación? Se volverán a descargar los banners de juegos, la biblioteca se re-escanneará y el componente de Overclock USB se desinstalará (se vuelve a descargar e instalar cuando abras Overclock USB). La configuración no se toca."),
            PrimaryButtonText = I18n.T("Limpiar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            long freed = GetCacheSizeBytes();

            // Banners e íconos de juegos: se re-descargan/re-extraen al abrir la biblioteca.
            var bannerDir = GestionarProcesosPage.BannerCacheDir;
            if (Directory.Exists(bannerDir))
                Directory.Delete(bannerDir, recursive: true);
                // Íconos de procesos en memoria: apuntan a archivos recién borrados;
                // invalidar para que se re-extraigan sin reiniciar la app.
                ProcesosPage.ClearIconCache();

            // Biblioteca cacheada (memoria + disco): re-escaneo en la próxima visita.
            _installedGamesService.ClearCache();

 // Componente de Overclock USB: se borra la copia descargada para que la
 // próxima vez que haga falta (reparar/reinstalar) se vuelva a descargar.
            _usbOverclockService.ClearComponentCache();

            UpdateCacheSize();
            Feedback.Success(ClearCacheFeedbackText, I18n.T("Caché borrada: {0} liberados.", FormatBytes(freed)));
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ConfiguracionPage: limpiar caché: {ex.Message}");
            Feedback.Error(ClearCacheFeedbackText, I18n.T("No se pudo limpiar la caché: {0}", ex.Message));
        }
    }

    private void ResetWindowPositionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Borra window.x/y/width/height y re-centra al instante: sin posición
            // guardada, RestoreOrCenterWindow vuelve al 1400x800 centrado.
            App.MainWindowInstance?.ResetWindowPosition();
            Feedback.Success(ResetWindowPositionFeedbackText, I18n.T("Posición de la ventana restablecida."));
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ConfiguracionPage: restablecer posición de ventana: {ex.Message}");
            Feedback.Error(ResetWindowPositionFeedbackText, I18n.T("No se pudo restablecer la posición: {0}", ex.Message));
        }
    }

    /// <summary>
    /// Reescribe el valor del registro Run para que coincida con la opción de
    /// minimizado actual. Normaliza instalaciones viejas (el valor quedó sin el
    /// flag cuando la opción se activó antes de que el flag existiera).
    /// </summary>
    private void SyncStartupRegistration()
    {
        try
        {
            if (_startupService.IsEnabled())
                _startupService.SetEnabled(true, StartMinimizedToggle.IsOn);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ConfiguracionPage: no se pudo sincronizar el inicio automático: {ex.Message}");
        }
    }

    /// <summary>
    /// Muestra un aviso bajo "Iniciar minimizado" cuando la opción está activa pero
    /// no puede tener efecto (el inicio con Windows está apagado): así no parece
    /// que el toggle no funcionara.
    /// </summary>
    private void UpdateStartMinimizedHint()
    {
        StartMinimizedHint.Visibility = (StartMinimizedToggle.IsOn && !LaunchAtStartupToggle.IsOn)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnLaunchAtStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        var result = _startupService.SetEnabled(LaunchAtStartupToggle.IsOn, StartMinimizedToggle.IsOn);
        if (!result.Success)
        {
            // Se revierte el toggle para reflejar el estado real y se muestra por qué falló.
            _isLoading = true;
            LaunchAtStartupToggle.IsOn = !LaunchAtStartupToggle.IsOn;
            _isLoading = false;
            Feedback.Error(StartupFeedbackText, I18n.T("No se pudo cambiar el inicio con Windows: {0}", result.Message));
        }
        else
        {
            Feedback.Success(StartupFeedbackText, LaunchAtStartupToggle.IsOn
                ? I18n.T("Inicio con Windows activado.")
                : I18n.T("Inicio con Windows desactivado."));
        }

        UpdateStartMinimizedHint();
    }

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading) return;

        bool minimized = StartMinimizedToggle.IsOn;

        // Dependencias de "Iniciar minimizado": la bandeja (para ocultarse ahí) y el
        // inicio con Windows (la única forma de que Windows lo lance al iniciar sesión).
        if (minimized && !MinimizeToTrayToggle.IsOn)
        {
            _isLoading = true;
            MinimizeToTrayToggle.IsOn = true;
            _isLoading = false;
            _settingsService.Set("window.minimizeToTray", true);
        }

        _settingsService.Set("window.startMinimized", minimized);

        if (minimized && !LaunchAtStartupToggle.IsOn)
        {
            // Activar minimizado implica activar el inicio automático (si no, no
            // tendría efecto): se escribe el valor del registro con el flag.
            var result = _startupService.SetEnabled(true, true);
            if (result.Success)
            {
                _isLoading = true;
                LaunchAtStartupToggle.IsOn = true;
                _isLoading = false;
                Feedback.Success(StartupFeedbackText, I18n.T("Inicio con Windows activado."));
            }
            else
            {
                // No se pudo activar el inicio automático: revertir minimizado.
                _isLoading = true;
                StartMinimizedToggle.IsOn = false;
                _isLoading = false;
                _settingsService.Set("window.startMinimized", false);
                Feedback.Error(MinimizedFeedbackText, I18n.T("No se pudo cambiar el inicio con Windows: {0}", result.Message));
            }
        }
        else if (_startupService.IsEnabled())
        {
            // El inicio ya está activo: reescribir el valor con/sin el flag para que
            // el próximo arranque de Windows respete el nuevo estado.
            var result = _startupService.SetEnabled(true, minimized);
            if (!result.Success)
                Feedback.Error(StartupFeedbackText, I18n.T("No se pudo cambiar el inicio con Windows: {0}", result.Message));
        }

        _settingsService.Save();
        UpdateStartMinimizedHint();

        if (StartMinimizedToggle.IsOn)
            Feedback.Success(MinimizedFeedbackText, I18n.T("Se abrirá minimizado al iniciar sesión."));
    }

    // ===================== Herramientas de desarrollo =====================

    private void UpdateDevVersionInfo()
    {
        try
        {
            if (DevVersionInfoText == null) return;
            var info = App.MainWindowInstance?.LatestUpdate;
            // Sin chequeo de actualizaciones todavía (build de depuración recién abierta o
            // sin red) la card igual dice qué versión está corriendo.
            if (info == null)
            {
                DevVersionInfoText.Text = I18n.T("Versión instalada: {0}", AppUpdateService.CurrentVersion());
                return;
            }

            var current = info.CurrentVersion ?? AppUpdateService.CurrentVersion();

            // Estado legible (no el nombre crudo del enum): si no se pudo contactar
            // a GitHub, el motivo típico es no tener internet y eso es lo que se
            // muestra — no un "Error" genérico.
            var status = info.Status switch
            {
                AppUpdateStatus.NoConnection => I18n.T("Falta de conexión a internet"),
                AppUpdateStatus.UpToDate => I18n.T("WinForge está actualizado."),
                AppUpdateStatus.UpdateAvailable => I18n.T("¡Hay una versión nueva disponible!"),
                AppUpdateStatus.DevelopmentBuild => I18n.T("Versión en desarrollo"),
                _ => I18n.T("No se pudo comprobar actualizaciones.")
            };

            var lines = new List<string>
            {
                I18n.T("Versión instalada: {0}", current),
                I18n.T("Estado del chequeo: {0}", status)
            };
            if (!string.IsNullOrEmpty(info.LatestVersion))
            {
                // Solo si el chequeo detectó la release: si no, no hay contra qué
                // comparar y las líneas "última release"/"por delante/por detrás"
                // serían inventadas.
                var latest = info.LatestVersion;
                var ahead = false;
                try { ahead = Version.Parse(current).CompareTo(Version.Parse(latest!)) > 0; } catch { }
                var relation = I18n.T(ahead ? "por delante" : "por detrás");
                lines.Add(I18n.T("Última release en GitHub: {0}", latest));
                lines.Add(I18n.T("La build de desarrollo está {0} de la release publicada.", relation));
            }
            DevVersionInfoText.Text = string.Join(Environment.NewLine, lines);
        }
        catch { DevVersionInfoText.Text = I18n.T("No se pudo obtener la información de versión."); }
    }

    private void ResetWindowPosButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindowInstance != null)
        {
            // Llamar al método que ya existe en MainWindow para restablecer posición
            App.MainWindowInstance.ResetWindowPosition();
            Feedback.Success(DevFeedbackText, I18n.T("Posición de ventana restablecida."));
        }
    }

    private void SimulateOnboardingButton_Click(object sender, RoutedEventArgs e)
    {
        // Abre el simulador del asistente de primera configuración (tema → aviso
        // gratis): se ve idéntico al onboarding real pero no persiste nada.
        if (_onboardingSimulator != null)
        {
            _onboardingSimulator.Activate();
            return;
        }

        _onboardingSimulator = new OnboardingSimulatorWindow();
        _onboardingSimulator.Closed += (_, _) => _onboardingSimulator = null;
        _onboardingSimulator.Activate();
    }

    private void UpdateDevelopmentToolsVisibility()
    {
        // La pestaña Desarrollo depende SOLO de la versión: aparece cuando esta build va
        // ADELANTADA a la última release publicada en GitHub. No importa si es Debug o
        // Release (antes toda Debug se consideraba de desarrollo, aunque su versión
        // coincidiera con la publicada) ni el sufijo beta/alpha del número: manda la
        // comparación que hace AppUpdateService contra el tag más nuevo del repositorio.
        var update = App.MainWindowInstance?.LatestUpdate;
        bool isDevelopmentBuild = update?.Status == AppUpdateStatus.DevelopmentBuild;

        DevelopmentTabItem.Visibility = isDevelopmentBuild ? Visibility.Visible : Visibility.Collapsed;
        DevSection.Visibility = isDevelopmentBuild ? Visibility.Visible : Visibility.Collapsed;
        DevTab.Visibility = isDevelopmentBuild && ReferenceEquals(ConfigTabs.SelectedItem, DevelopmentTabItem)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Si una comprobación posterior deja de considerar esta build como de
        // desarrollo, no dejamos seleccionada una pestaña que acaba de ocultarse.
        if (!isDevelopmentBuild && ReferenceEquals(ConfigTabs.SelectedItem, DevelopmentTabItem))
        {
            _selectedTabIndex = 0;
            ConfigTabs.SelectedItem = ConfigTabs.Items.OfType<SelectorBarItem>().FirstOrDefault();
        }

        if (isDevelopmentBuild)
            UpdateDevVersionInfo();

        ApplyTabVisibility();
    }

    // ===================== Actualizaciones de la app =====================

    private void OnAppUpdateStateChanged()
    {
        UpdateDevelopmentToolsVisibility();
    }

    private void OnLanguageChanged()
    {
        ApplyConfigTabsLanguage();
        // La grilla de temas se etiqueta al armarla (I18n.T en cada card): se rearma para que los
        // nombres y los encabezados de grupo queden en el idioma nuevo.
        ThemePickerUi.Relabel(_themeService.CurrentTheme);
        // Las etiquetas de las muestras de carcasa se arman en código: se rearman traducidas.
        BuildCardSkinPicker();
        UpdateDeveloperLogsSize();
        RefreshDriversTab();

        // La pestaña Actualizaciones arma sus cards en código (textos y fechas): se rehace
        // entera para que quede en el idioma nuevo sin volver a pedir nada al repositorio.
        if (_updatesLoaded)
        {
            UpdateUpdatesHeader();
            RenderReleases();
        }
    }

    /// <summary>
    /// Repinta el navbar interno (ConfigNavBar) al cambiar de tema. El Border
    /// usa {ThemeResource NavigationViewDefaultPaneBackground}, que WinUI cachea:
    /// al cambiar entre temas de la misma base (Negro/Azul → Oscuro) el
    /// diccionario se actualiza pero el control no repinta. Setear el
    /// Background directamente con el color del tema (igual que MainWindow hace
    /// con el navbar principal) es lo único que funciona de forma confiable.
    /// </summary>
    private void OnThemeChanged(object? sender, AppTheme theme)
    {
        // El repintado en caliente quedó reemplazado por el reinicio de la app
        // (RestartForThemeChange): aquí solo se refleja la elección en el picker.
        ThemePickerUi.MarkSelected(theme);
        RepaintConfigNavBar();
        UpdateBlurAvailability();
    }

    /// <summary>
    /// El desenfoque solo tiene efecto donde hay una FOTO detrás de los paneles: los temas de
    /// entorno. En los planos y de degradado (y en Claro/Oscuro) no hay copia que desenfocar
    /// —PanelBlurLayer se queda sin fuente—, así que el deslizador se apaga para que no parezca
    /// roto —por eso esos temas declaran 0 % de blur (ver ThemeCatalog.KindOf).
    ///
    /// En los temas PLANOS tampoco hay nada detrás que la transparencia pueda mostrar (ni foto
    /// ni gradiente): las dos cards se ocultan y los valores quedan fijos en 0 % y 0 %
    /// (ver PanelAppearance.Resolve).
    /// </summary>
    private void UpdateBlurAvailability()
    {
        try
        {
            var theme = WHPO_UI.PanelAppearance.EffectiveTheme();
            bool hasPhoto = WHPO_UI.Wallpaper.SavedImagePath(theme) != null;
            PanelBlurSlider.IsEnabled = hasPhoto;
            bool isFlat = WHPO_UI.ThemeCatalog.KindOf(theme) == WHPO_UI.ThemeCatalog.ThemeKind.Flat;
            PanelTransparencyCard.Visibility = isFlat
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;
            PanelBlurCard.Visibility = isFlat
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;
        }
        catch { /* arranque temprano */ }
    }

    /// <summary>
    /// Relleno de la barra de pestañas interna, reusado entre pasadas: con el vidrio encendido es el
    /// ACRÍLICO de la plataforma (ver <see cref="WHPO_UI.PanelAppearance.SurfaceFill"/>) y con el
    /// desenfoque apagado el sólido de siempre.
    /// </summary>
    private Brush? _navFill;

    /// <summary>
    /// Pinta el navbar interno (ConfigNavBar) con el color del panel del tema, ya con la
    /// transparencia vigente.
    ///
    /// El Border usa {ThemeResource NavigationViewDefaultPaneBackground}, que WinUI cachea: al
    /// cambiar entre temas de la misma base (Negro/Azul → Oscuro) el diccionario se actualiza
    /// pero el control no repinta, y al mover el deslizador de transparencia tampoco. Setear el
    /// Background con el color de la DEFINICIÓN del tema (el mismo del panel del menú y de la
    /// barra de título) es lo único que funciona de forma confiable y además deja la barra de
    /// pestañas de Configuración como parte de la misma pieza que el menú.
    /// </summary>
    private void RepaintConfigNavBar()
    {
        try
        {
            // El relleno sale del MISMO helper que el de las otras barras internas (Núcleos y
            // Limpieza, ver PanelAppearance.PaneBarFill): color del panel del menú con el alfa del
            // ajuste y el vidrio vigente —compositor, acrílico o sólido—. Acá solo queda asignarlo:
            // la barra tiene que adoptar la instancia NUEVA cuando el camino cambia (encender o
            // apagar el vidrio), que es lo que el {ThemeResource} del XAML no hace.
            _navFill = WHPO_UI.PanelAppearance.PaneBarFill(_navFill);
            ConfigNavBar.Background = _navFill;
        }
        catch { /* arranque temprano */ }
    }

    // ===================== Navbar interno (pestañas) =====================

    /// <summary>
    /// Traduce las pestañas del SelectorBar interno (Inicio / Apariencia / Drivers / Actualizaciones / Desarrollo).
    /// Los SelectorBarItem pueden no estar realizados en el árbol visual,
    /// así que se traducen por colección lógica (mismo patrón que NucleosPage).
    /// </summary>
    private void ApplyConfigTabsLanguage()
    {
        TranslateSelectorBar(ConfigTabs);
    }

    /// <summary>Traduce los ítems de un SelectorBar por colección lógica (no viven en el árbol visual).</summary>
    private static void TranslateSelectorBar(SelectorBar? bar)
    {
        try
        {
            if (bar == null) return;
            foreach (var item in bar.Items.OfType<SelectorBarItem>())
            {
                if (item.Text is string s && Translations.TryGetSource(s, I18n.Current, out var source))
                    item.Text = I18n.T(source);
            }
        }
        catch { }
    }

    // ===================== Navbar interno (pestañas) =====================

    private void ConfigTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ConfigTabs == null || ConfigTabs.Items.Count == 0) return;
        int idx = ConfigTabs.SelectedItem != null ? ConfigTabs.Items.IndexOf(ConfigTabs.SelectedItem) : 0;
        _selectedTabIndex = idx < 0 ? 0 : idx;
        ApplyTabVisibility();
    }

    private void ApplyTabVisibility()
    {
        if (HomeTab == null || AppearanceTab == null || DriversTab == null || DevTab == null) return;

        // Cada panel se muestra según el ÍTEM seleccionado (no por índice fijo): así
        // sumar una pestaña en el medio no corre la visibilidad de las demás.
        var selected = ConfigTabs.SelectedItem;
        HomeTab.Visibility = ReferenceEquals(selected, HomeTabItem) ? Visibility.Visible : Visibility.Collapsed;
        AppearanceTab.Visibility = ReferenceEquals(selected, AppearanceTabItem) ? Visibility.Visible : Visibility.Collapsed;
        bool driversSelected = ReferenceEquals(selected, DriversTabItem);
        DriversTab.Visibility = driversSelected ? Visibility.Visible : Visibility.Collapsed;
        // La página usa caché de navegación y el driver pudo instalarse desde el
        // Workshop o desde otra pestaña: al entrar a Drivers se relee el estado real.
        if (driversSelected) RefreshDriversTab();
        bool updatesSelected = ReferenceEquals(selected, UpdatesTabItem);
        UpdatesTab.Visibility = updatesSelected ? Visibility.Visible : Visibility.Collapsed;
        DevTab.Visibility = ReferenceEquals(selected, DevelopmentTabItem)
            && DevelopmentTabItem.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

        // La primera vez que se abre la pestaña se consulta el repositorio. Después queda
        // cacheado en memoria: volver a entrar no repite la descarga (ver LoadUpdatesAsync).
        if (updatesSelected) _ = LoadUpdatesAsync(forceRefresh: false);
    }

    // ===================== Pestaña Actualizaciones =====================

    private AppUpdateInfo? _updatesInfo;
    private IReadOnlyList<AppReleaseInfo>? _releases;
    private bool _updatesLoading;
    private bool _updatesLoaded;

    // Estado de los desplegables del historial: sobrevive a los renders (buscar
    // actualizaciones, cambio de idioma) para respetar lo que el usuario dejó abierto.
    private readonly HashSet<string> _expandedReleases = new(StringComparer.OrdinalIgnoreCase);
    private bool _releasesRendered;

    /// <summary>
    /// Carga el estado del chequeo y el historial de versiones. La primera pasada sale del
    /// repositorio; <paramref name="forceRefresh"/> (botón "Buscar actualizaciones") saltea
    /// la caché en memoria del servicio y del propio estado de la página.
    /// </summary>
    private async Task LoadUpdatesAsync(bool forceRefresh)
    {
        if (_updatesLoading) return;
        if (_updatesLoaded && !forceRefresh) return;

        _updatesLoading = true;
        UpdatesProgressRing.IsActive = true;
        UpdatesProgressRing.Visibility = Visibility.Visible;
        CheckUpdatesButton.IsEnabled = false;
        ReleasesFeedbackText.Visibility = Visibility.Collapsed;

        try
        {
            var info = await Task.Run(() => _appUpdateService.CheckForUpdatesAsync());

            var previous = _releases;
            IReadOnlyList<AppReleaseInfo> releases;
            try
            {
                releases = await _appUpdateService.GetReleasesAsync(12, forceRefresh);
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"Configuración: historial de versiones: {ex.Message}");
                releases = previous ?? Array.Empty<AppReleaseInfo>();
                // Si ya había una lista en pantalla se conserva y se avisa del error; si no
                // había, RenderReleases muestra el aviso en su lugar.
                if (previous != null)
                {
                    // Sin conexión se dice derecho: el aviso genérico solo haría pensar que
                    // el historial está roto. Un 403/500 de GitHub no es falta de internet.
                    ReleasesFeedbackText.Text = I18n.T("No se pudieron cargar las versiones.")
                        + (NetworkError.IsConnectionFailure(ex) ? " " + I18n.T(NetworkError.ConnectionHint) : "");
                    ReleasesFeedbackText.Visibility = Visibility.Visible;
                }
            }

            _updatesInfo = info;
            _releases = releases;
            _updatesLoaded = true;
            UpdateUpdatesHeader();
            RenderReleases();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Configuración: actualizaciones: {ex.Message}");
            ReleasesFeedbackText.Text = I18n.T("No se pudo comprobar actualizaciones.")
                + (NetworkError.IsConnectionFailure(ex) ? " " + I18n.T(NetworkError.ConnectionHint) : "");
            ReleasesFeedbackText.Visibility = Visibility.Visible;
        }
        finally
        {
            _updatesLoading = false;
            UpdatesProgressRing.IsActive = false;
            UpdatesProgressRing.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Pinta la cabecera: versión instalada, estado del chequeo y acción principal. El botón
    /// de instalar solo aparece cuando hay una versión más nueva publicada.
    /// </summary>
    private void UpdateUpdatesHeader()
    {
        if (UpdatesInstalledVersionText == null) return;

        string current = "v" + AppUpdateService.CurrentVersion();
        UpdatesInstalledVersionText.Text = I18n.T("Versión instalada: {0}", current);

        var accent = ThemeBrushes.Get("AccentBrush");
        var success = Feedback.SuccessBrush;
        var warning = Feedback.WarningBrush;

        InstallUpdateButton.Visibility = Visibility.Collapsed;

        switch (_updatesInfo?.Status)
        {
            case AppUpdateStatus.UpdateAvailable:
                UpdatesStatusText.Text = I18n.T("¡Hay una versión más nueva disponible!") + $" · v{_updatesInfo.LatestVersion}";
                SetUpdatesBadge(I18n.T("Desactualizado"), accent);
                InstallUpdateButton.Visibility = Visibility.Visible;
                break;

            case AppUpdateStatus.DevelopmentBuild:
                UpdatesStatusText.Text = I18n.T(
                    "Estás usando una versión en desarrollo ({0}): todavía no se publicó una versión más nueva en el repositorio.",
                    current);
                SetUpdatesBadge(I18n.T("Desarrollo"), warning);
                break;

            case AppUpdateStatus.NoConnection:
                UpdatesStatusText.Text = I18n.T("No se pudo comprobar actualizaciones.")
                    + " " + I18n.T("Revisá tu conexión a internet y volvé a intentarlo.");
                SetUpdatesBadge(I18n.T("Sin conexión"), warning);
                break;

            case AppUpdateStatus.Error:
                UpdatesStatusText.Text = I18n.T("No se pudo comprobar actualizaciones.");
                SetUpdatesBadge(I18n.T("Sin conexión"), warning);
                break;

            case AppUpdateStatus.UpToDate:
                UpdatesStatusText.Text = I18n.T("WinForge está actualizado.");
                SetUpdatesBadge(I18n.T("Actualizado"), success);
                break;

            default:
                UpdatesStatusText.Text = I18n.T("Comprobando actualizaciones...");
                UpdatesStatusBadge.Visibility = Visibility.Collapsed;
                break;
        }
    }

    /// <summary>Pastilla de estado del tema vigente: tinte de fondo + texto en el color recibido.</summary>
    private void SetUpdatesBadge(string text, SolidColorBrush color)
    {
        UpdatesStatusBadge.Visibility = Visibility.Visible;
        UpdatesStatusBadgeText.Text = text;
        UpdatesStatusBadgeText.Foreground = color;
        UpdatesStatusBadge.Background = new SolidColorBrush(color.Color) { Opacity = 0.14 };
    }

    /// <summary>
    /// Arma las cards del historial. Se rehace entero al cambiar de idioma (los textos y las
    /// fechas se arman acá) y al volver a entrar a la pestaña con datos nuevos.
    /// </summary>
    private void RenderReleases()
    {
        if (ReleasesHost == null) return;
        ReleasesHost.Children.Clear();

        var list = _releases;
        if (list == null || list.Count == 0)
        {
            ReleasesHost.Children.Add(new TextBlock
            {
                Text = I18n.T("No se pudieron cargar las versiones."),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush")
            });
            return;
        }

        string current = AppUpdateService.CurrentVersion();
        // La primera vez se abre la versión más reciente. Después manda el estado que dejó
        // el usuario: pulsar «Buscar actualizaciones» ya no vuelve a abrir la última.
        bool firstRender = !_releasesRendered;
        _releasesRendered = true;
        for (int i = 0; i < list.Count; i++)
        {
            var release = list[i];
            bool expanded = firstRender ? i == 0 : _expandedReleases.Contains(release.Version);
            if (expanded) _expandedReleases.Add(release.Version);
            ReleasesHost.Children.Add(BuildReleaseCard(
                release,
                string.Equals(release.Version, current, StringComparison.OrdinalIgnoreCase),
                // GitHub entrega las releases de la más nueva a la más vieja: la primera es
                // la última publicada, y lleva su badge siempre (aunque la app esté en una
                // build de desarrollo, donde la instalada no coincide con ninguna release).
                i == 0,
                expanded,
                isOpen =>
                {
                    if (isOpen) _expandedReleases.Add(release.Version);
                    else _expandedReleases.Remove(release.Version);
                }));
        }
    }

    /// <summary>
    /// Un desplegable por versión: el encabezado es SOLO el nombre de la versión (con sus
    /// distintivos y la fecha) y los cambios quedan adentro, así la pestaña se lee como un
    /// índice de versiones en vez de una pared de texto.
    /// </summary>
    private Border BuildReleaseCard(AppReleaseInfo release, bool isCurrent, bool isLatest, bool expanded, Action<bool> onExpandedChanged)
    {
        var headerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        // La versión es un enlace a su release en GitHub (subrayado apagado: se lee como título).
        var versionText = new TextBlock
        {
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var versionLink = new Hyperlink { UnderlineStyle = UnderlineStyle.None };
        versionLink.Inlines.Add(new Run { Text = "v" + release.Version });
        if (Uri.TryCreate(release.WebUrl, UriKind.Absolute, out var releaseUri))
            versionLink.NavigateUri = releaseUri;
        versionText.Inlines.Add(versionLink);
        headerRow.Children.Add(versionText);

        if (isLatest) headerRow.Children.Add(MakeReleaseBadge(I18n.T("Última versión"), ThemeBrushes.Get("AccentBrush")));
        if (isCurrent) headerRow.Children.Add(MakeReleaseBadge(I18n.T("Actual"), Feedback.SuccessBrush));
        if (release.Prerelease) headerRow.Children.Add(MakeReleaseBadge(I18n.T("Beta"), Feedback.WarningBrush));

        if (release.PublishedAt is { } published)
        {
            headerRow.Children.Add(new TextBlock
            {
                Text = I18n.T("Publicado el {0}", published.ToLocalTime().ToString("d MMM yyyy")),
                FontSize = 11.5,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush")
            });
        }

        // Desplegable PROPIO en vez de Expander: el control nativo dibuja su propio
        // contorno (ControlStroke) y las cards de la app no llevan reborde. Acá el
        // header es un botón transparente y sin borde, y el contenido se muestra u
        // oculta según el estado.
        var chevron = new FontIcon
        {
            Glyph = expanded ? "\uE70E" : "\uE70D",
            FontSize = 11,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var headerGrid = new Grid { ColumnSpacing = 10 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(headerRow);
        Grid.SetColumn(chevron, 1);
        headerGrid.Children.Add(chevron);

        var body = new StackPanel
        {
            Spacing = 7,
            Margin = new Thickness(12, 0, 12, 10),
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed
        };
        body.Children.Add(BuildReleaseNotes(release));

        var toggle = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = headerGrid
        };
        toggle.Click += (_, _) =>
        {
            bool open = body.Visibility == Visibility.Visible;
            body.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            chevron.Glyph = open ? "\uE70D" : "\uE70E";
            onExpandedChanged(!open);
        };

        var panel = new StackPanel();
        panel.Children.Add(toggle);
        panel.Children.Add(body);

        return new Border
        {
            Background = ThemeBrushes.GetSurface("CardBackgroundBrush"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(2, 2, 2, 2),
            Child = panel
        };
    }

    private static Border MakeReleaseBadge(string text, SolidColorBrush color) => new()
    {
        Background = new SolidColorBrush(color.Color) { Opacity = 0.14 },
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 2, 8, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = color
        }
    };

    /// <summary>
    /// Renderiza las notas de la release (Markdown de GitHub) sin traer un motor entero:
    /// encabezados, viñetas, citas y texto con **negrita**, `código` y [enlaces](url).
    /// El encabezado que repite el nombre de la versión se omite: la card ya lo muestra arriba.
    /// </summary>
    private UIElement BuildReleaseNotes(AppReleaseInfo release)
    {
        var host = new StackPanel { Spacing = 7 };
        string notes = (release.Notes ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Trim();

        if (notes.Length == 0)
        {
            host.Children.Add(MakeNotesLine(I18n.T("Sin notas publicadas."), ThemeBrushes.Get("SecondaryTextBrush")));
            return host;
        }

        var quoteLines = new List<string>();
        bool firstBlock = true;

        void FlushQuote()
        {
            if (quoteLines.Count == 0) return;
            host.Children.Add(BuildReleaseQuote(quoteLines));
            quoteLines.Clear();
        }

        foreach (var rawLine in notes.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                FlushQuote();
                continue;
            }

            // Cita / aviso de GitHub ("[!WARNING]"): se agrupa hasta la línea en blanco.
            if (line.StartsWith(">", StringComparison.Ordinal))
            {
                quoteLines.Add(line.TrimStart('>').Trim());
                continue;
            }
            FlushQuote();

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                string heading = line.TrimStart('#').Trim();
                // El título de la release repite versión y nombre: ya está en el encabezado.
                if (firstBlock && (string.Equals(heading, release.Name, StringComparison.OrdinalIgnoreCase)
                                   || heading.Contains(release.Version, StringComparison.OrdinalIgnoreCase)))
                {
                    firstBlock = false;
                    continue;
                }
                firstBlock = false;
                if (heading.Length == 0) continue;

                // Los títulos de cada bloque de cambios tienen que leerse MÁS grandes que
                // el texto de las viñetas: antes iban casi al mismo tamaño y se perdían.
                var headingText = new TextBlock
                {
                    FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 2)
                };
                headingText.Inlines.Add(new Run { Text = heading });
                host.Children.Add(headingText);
                continue;
            }

            firstBlock = false;

            if (line.StartsWith("- ", StringComparison.Ordinal)
                || line.StartsWith("* ", StringComparison.Ordinal)
                || line.StartsWith("• ", StringComparison.Ordinal))
            {
                var bullet = new Grid { ColumnSpacing = 8 };
                bullet.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                bullet.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                bullet.Children.Add(new TextBlock
                {
                    Text = "•",
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Top,
                    Foreground = ThemeBrushes.Get("AccentBrush")
                });

                var bulletText = MakeNotesLine(string.Empty, ThemeBrushes.Get("PrimaryTextBrush"));
                AppendInline(bulletText, line.Substring(2).Trim());
                Grid.SetColumn(bulletText, 1);
                bullet.Children.Add(bulletText);

                host.Children.Add(bullet);
                continue;
            }

            var paragraph = MakeNotesLine(string.Empty, ThemeBrushes.Get("PrimaryTextBrush"));
            AppendInline(paragraph, line);
            host.Children.Add(paragraph);
        }

        FlushQuote();
        return host;
    }

    private static TextBlock MakeNotesLine(string text, Brush foreground) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Foreground = foreground
    };

    /// <summary>Cita / aviso: barra lateral (amarilla si es un "[!WARNING]" de GitHub) y fondo tenue.</summary>
    private static Border BuildReleaseQuote(List<string> lines)
    {
        string text = string.Join(" ", lines);
        bool warning = text.Contains("[!WARNING]", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("[!CAUTION]", StringComparison.OrdinalIgnoreCase);
        foreach (var marker in new[] { "[!WARNING]", "[!NOTE]", "[!IMPORTANT]", "[!TIP]", "[!CAUTION]" })
            text = text.Replace(marker, string.Empty, StringComparison.OrdinalIgnoreCase);

        var textBlock = MakeNotesLine(string.Empty, ThemeBrushes.Get("SecondaryTextBrush"));
        textBlock.FontSize = 12.5;
        AppendInline(textBlock, text.Trim());

        return new Border
        {
            Background = ThemeBrushes.GetSurface("ChipBackgroundBrush"),
            BorderBrush = warning ? Feedback.WarningBrush : ThemeBrushes.Get("AccentBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Child = textBlock
        };
    }

    /// <summary>
    /// Agrega a un TextBlock el texto con **negrita**, `código` y [enlaces](url) resueltos a
    /// Runs/Hyperlinks. Si un marcador no cierra, se deja el texto literal (nunca se come texto).
    /// </summary>
    private static void AppendInline(TextBlock target, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        int index = 0;
        while (index < text.Length)
        {
            int bold = text.IndexOf("**", index, StringComparison.Ordinal);
            int code = text.IndexOf('`', index);
            int link = text.IndexOf('[', index);

            int next = int.MaxValue;
            if (bold >= 0) next = Math.Min(next, bold);
            if (code >= 0) next = Math.Min(next, code);
            if (link >= 0) next = Math.Min(next, link);

            if (next == int.MaxValue)
            {
                target.Inlines.Add(new Run { Text = text.Substring(index) });
                return;
            }

            if (next > index)
                target.Inlines.Add(new Run { Text = text.Substring(index, next - index) });

            if (next == bold)
            {
                int end = text.IndexOf("**", next + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    target.Inlines.Add(new Run { Text = text.Substring(next) });
                    return;
                }
                target.Inlines.Add(new Run
                {
                    Text = text.Substring(next + 2, end - next - 2),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                index = end + 2;
            }
            else if (next == code)
            {
                int end = text.IndexOf('`', next + 1);
                if (end < 0)
                {
                    target.Inlines.Add(new Run { Text = text.Substring(next) });
                    return;
                }
                target.Inlines.Add(new Run
                {
                    Text = text.Substring(next + 1, end - next - 1),
                    FontFamily = new FontFamily("Consolas")
                });
                index = end + 1;
            }
            else
            {
                int labelEnd = text.IndexOf(']', next + 1);
                int urlStart = labelEnd > next ? text.IndexOf('(', labelEnd + 1) : -1;
                int urlEnd = urlStart > labelEnd ? text.IndexOf(')', urlStart + 1) : -1;

                if (labelEnd > next && urlStart == labelEnd + 1 && urlEnd > urlStart
                    && Uri.TryCreate(text.Substring(urlStart + 1, urlEnd - urlStart - 1), UriKind.Absolute, out var uri))
                {
                    var hyperlink = new Hyperlink { NavigateUri = uri };
                    hyperlink.Inlines.Add(new Run { Text = text.Substring(next + 1, labelEnd - next - 1) });
                    target.Inlines.Add(hyperlink);
                    index = urlEnd + 1;
                }
                else
                {
                    target.Inlines.Add(new Run { Text = "[" });
                    index = next + 1;
                }
            }
        }
    }

    private void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        => _ = LoadUpdatesAsync(forceRefresh: true);

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var info = _updatesInfo;
        if (info == null || info.Status != AppUpdateStatus.UpdateAvailable || XamlRoot == null) return;

        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            var noInstaller = new ContentDialog
            {
                XamlRoot = XamlRoot,
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
            XamlRoot = XamlRoot,
            Title = I18n.T("Actualizar WinForge"),
            Content = I18n.T("Se descargará la versión {0} y la app se cerrará para instalarla. ¿Continuar?", info.LatestVersion),
            PrimaryButtonText = I18n.T("Actualizar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        // Flujo compartido con el botón "Actualizar" del navbar: descarga, lanza el MSI y
        // cierra la app (el instalador la reabre al terminar).
        var main = App.MainWindowInstance;
        if (main != null) await main.InstallUpdateAsync(info);
    }

    private void OpenReleasesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProjectEndpoints.Releases) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Configuración: abrir releases: {ex.Message}");
        }
    }
}
