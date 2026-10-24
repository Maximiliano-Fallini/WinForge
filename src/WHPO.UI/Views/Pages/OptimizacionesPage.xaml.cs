using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WHPO.Core.Optimizations;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Components;
using WHPO_UI.Controls;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Hub de optimizaciones: el punto de entrada ÚNICO de todo lo que se puede
/// tocar en Windows.
///
/// Arriba están las cards de cada categoría del catálogo (418 ajustes
/// repartidos en 10 categorías) y de las herramientas del sistema, y al abrir
/// una card el drill-down con sus grupos y sus ajustes.
///
/// Las 7 herramientas (TCP, Debloat, Herramientas, Panel de Windows,
/// Reparación, Windows Update y Limpieza) ya no son pestañas del navbar: son
/// cards del hub y se abren dentro de esta misma página, en un Frame propio.
/// </summary>
public sealed partial class OptimizacionesPage : Page
{
    private readonly ILoggingService _loggingService;
    private readonly ComponentRegistry _registry;

    private readonly OptimizationCatalog _catalog;
    private readonly OptimizationCatalogText _catalogText;
    private readonly OptimizationStateService _stateService = new();
    private readonly OptimizationApplier _applier = new();

    // Estado REAL del sistema por ajuste (id → estado leído).
    private readonly Dictionary<string, OptimizationSettingState> _states = new(StringComparer.OrdinalIgnoreCase);

    // Contadores de badges por categoría (id → resumen).
    private readonly Dictionary<string, OptimizationFeatureSummary> _summaries = new(StringComparer.OrdinalIgnoreCase);

    // Cambios pendientes de aplicar: switches, opciones de combo y valores de slider.
    private readonly Dictionary<string, bool> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _pendingNumeric = new(StringComparer.OrdinalIgnoreCase);

    // Carga de estados por categoría (una sola vez por categoría).
    private readonly Dictionary<string, Task> _featureLoads = new(StringComparer.OrdinalIgnoreCase);

    // Categorías con cambios aplicados en el lote, para releer su estado después.
    private readonly HashSet<string> _touchedFeatures = new(StringComparer.OrdinalIgnoreCase);

    // Índice de todos los ajustes del catálogo (id → ajuste).
    private readonly Dictionary<string, OptimizationSetting> _settings = new(StringComparer.OrdinalIgnoreCase);

    // Controles de las filas del drill-down, para refrescarlos sin rehacer todo.
    private readonly Dictionary<string, StackPanel> _rowBadges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToggleSwitch> _rowToggles = new(StringComparer.OrdinalIgnoreCase);

    // Cards del hub, para refrescar los contadores sin reconstruirlas.
    private readonly Dictionary<string, TextBlock> _cardCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CardPills> _cardPills = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Las tres pastillas de estado de una card (con su texto actualizable).</summary>
    private sealed record CardPills(
        StackPanel Host,
        Border Recommended, TextBlock RecommendedText,
        Border Default, TextBlock DefaultText,
        Border Custom, TextBlock CustomText);

    // Las lecturas del sistema (registro, tareas, powercfg) NO se serializan
    // del todo: el servicio cachea en diccionarios concurrentes y el hub lee
    // las categorías en paralelo, pero con un techo global de lecturas a la vez
    // para no saturar la máquina (40 hilos lanzando procesos era peor que
    // hacerlo en serie).
    private static readonly SemaphoreSlim StateReadGate = new(6, 6);

    /// <summary>
    /// Último estado REAL leído por categoría, compartido entre instancias de la
    /// página. El Frame crea una página nueva cada vez que se vuelve a la
    /// pestaña, así que sin esto se releen los 418 ajustes desde cero y las
    /// cards se quedan en «Calculando estado…». Con la caché, al volver se
    /// muestran los últimos valores leídos y se refrescan en segundo plano.
    /// </summary>
    private static readonly ConcurrentDictionary<string, CachedStates> StateCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedStates(
        List<OptimizationSettingState> States,
        OptimizationFeatureSummary Summary,
        DateTime At);

    private enum HubMode { Hub, Category, Utility }

    private HubMode _mode = HubMode.Hub;
    private string? _openId;
    private bool _dataLoaded;
    private bool _busy;

    // ====== PINCELES (siguen al tema claro/oscuro) ======
    private static Brush CardBrush => ThemeBrushes.GetSurface("CardBackgroundBrush");
    private static SolidColorBrush CardHoverBrush => ThemeBrushes.Get("CardHoverBrush");
    private static SolidColorBrush AccentBrush => ThemeBrushes.Get("AccentBrush");
    private static SolidColorBrush SuccessBrush => (SolidColorBrush)App.Current.Resources["SuccessBrush"];
    private static SolidColorBrush WarningBrush => (SolidColorBrush)App.Current.Resources["WarningBrush"];
    private static SolidColorBrush MutedBrush => ThemeBrushes.Get("MutedBrush");
    private static SolidColorBrush ErrorBrush = new(Windows.UI.Color.FromArgb(255, 0xF0, 0x61, 0x6D));
    private static SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    /// <summary>
    /// Relleno de la banda de cabecera, reusado entre pasadas: con el vidrio encendido
    /// es el ACRÍLICO de la plataforma (ver <see cref="WHPO_UI.PanelAppearance.SurfaceFill"/>)
    /// y con el desenfoque apagado el sólido de siempre.
    /// </summary>
    private Brush? _headerFill;

    /// <summary>
    /// Pinta la banda de cabecera (HeaderCard) con el relleno del panel del menú, ya con
    /// la transparencia vigente y el vidrio que esté en juego (ver
    /// <see cref="WHPO_UI.PanelAppearance.PaneBarFill"/>): la misma pieza que el panel
    /// del menú, así que la banda se lee como continuación del navbar y no como un
    /// bloque aparte cortado sobre el fondo.
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

    public OptimizacionesPage()
    {
        try
        {
            InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;

            _loggingService = App.Services.GetRequiredService<ILoggingService>();
            _registry = App.Services.GetRequiredService<ComponentRegistry>();
            _catalog = OptimizationCatalogProvider.Catalog;
            _catalogText = new OptimizationCatalogText(_catalog);

            foreach (var feature in _catalog.Features)
                foreach (var setting in feature.Settings)
                    _settings[setting.Id] = setting;

            Loaded += OnLoaded;

            // La banda de la cabecera (HeaderCard) se pinta a mano, IGUAL que la del
            // Workshop y que el navbar interno de Configuración (ver RepaintHeaderCard):
            // WinUI cachea el {ThemeResource} del Border y no lo vuelve a resolver cuando
            // el ajuste REEMPLAZA la instancia del diccionario, así que con el XAML solo
            // la banda quedaba opaca y sin seguir el vidrio/transparencia del tema. La
            // página usa caché de navegación, así que la suscripción y la baja se atan a
            // Loaded/Unloaded para no dejar el handler colgado.
            Loaded += (_, _) =>
            {
                // Baja antes del alta: la página se carga más de una vez (caché de
                // navegación) y no puede quedar suscrita dos veces.
                WHPO_UI.PanelAppearance.Applied -= RepaintHeaderCard;
                WHPO_UI.PanelAppearance.Applied += RepaintHeaderCard;
                RepaintHeaderCard();
            };
            Unloaded += (_, _) => WHPO_UI.PanelAppearance.Applied -= RepaintHeaderCard;

            // La página está cacheada: sin esto la consola volvía a aparecer ABIERTA
            // (con la salida del lote anterior) en la próxima visita.
            Unloaded += (_, _) => HideConsole();

            // Al cambiar el idioma se reconstruye todo: los textos del catálogo
            // vienen en el idioma del usuario y las cards se arman en código.
            I18n.LanguageChanged += OnLanguageChanged;

            // Al cambiar el tema se re-aplican los colores (las cards se arman
            // en código con pinceles resueltos en el momento).
            ActualThemeChanged += (s, e) =>
            {
                if (_dataLoaded)
                    DispatcherQueue.TryEnqueue(() => RebuildAll());
            };
        }
        catch (Exception ex)
        {
            if (DebugText != null)
                DebugText.Text = $"Error init: {ex.Message}";
            try { _loggingService?.LogError($"Error en constructor OptimizacionesPage: {ex}", ex); } catch { }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_dataLoaded)
        {
            RefreshCachedHubCards();
            return;
        }
        _dataLoaded = true;

        try
        {
            BuildHub();
            ShowHeaderForHub();

            // Los estados se leen en segundo plano: el hub se muestra al toque
            // y los contadores se van completando categoría por categoría.
            foreach (var feature in _catalog.Features.OrderBy(f => f.Order))
                _ = LoadFeatureStatesAsync(feature);
        }
        catch (Exception ex)
        {
            DebugText.Text = $"Error: {ex.Message}";
            try { _loggingService.LogError($"Error en OnLoaded OptimizacionesPage: {ex}", ex); } catch { }
        }
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_dataLoaded) return;

        RefreshCachedHubCards();
        foreach (var feature in _catalog.Features.OrderBy(f => f.Order))
        {
            if (!_summaries.ContainsKey(feature.Id))
                _ = LoadFeatureStatesAsync(feature);
        }
    }

    private void RefreshCachedHubCards()
    {
        foreach (var feature in _catalog.Features)
        {
            if (_summaries.ContainsKey(feature.Id))
                UpdateCategoryCard(feature);
        }
    }

    /// <summary>
    /// Trae a esta instancia el último estado conocido de cada categoría (el de
    /// la sesión anterior de la página), para que las cards arranquen con los
    /// contadores reales en vez de «Calculando estado…».
    /// </summary>
    private void ApplyCachedSummaries()
    {
        foreach (var (featureId, cached) in StateCache)
        {
            _summaries[featureId] = cached.Summary;
            foreach (var state in cached.States)
                _states[state.Setting.Id] = state;
        }
    }

    private void OnLanguageChanged()
    {
        try
        {
            if (!_dataLoaded) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                RebuildAll();
                if (_mode == HubMode.Category && _openId != null) OpenCategory(_openId);
            });
        }
        catch (Exception ex)
        {
            try { _loggingService.LogError("Error re-traduciendo OptimizacionesPage", ex); } catch { }
        }
    }

    /// <summary>Rehace el hub (cards y contadores) sin perder los cambios pendientes.</summary>
    private void RebuildAll()
    {
        BuildHub();
        if (_mode == HubMode.Category && _openId != null) OpenCategory(_openId);
    }

    // =====================================================================
    // HUB
    // =====================================================================

    private void BuildHub()
    {
        if (HubGrid == null) return;

        // Las cards viejas se van con el panel: sus referencias también.
        _cardCounters.Clear();
        _cardPills.Clear();

        HubGrid.Children.Clear();
        HubGrid.RowDefinitions.Clear();
        HubGrid.ColumnDefinitions.Clear();
        // UNA sola columna: las cards se apilan como en la referencia (icono ·
        // título/subtítulo · pastillas · chevron), no en dos columnas que
        // apretaban el subtítulo y dejaban el chevron pegado al texto.
        HubGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cards = new List<Border>();

        foreach (var feature in _catalog.Features.OrderBy(f => f.Order))
            cards.Add(BuildCategoryCard(feature));

        foreach (var utility in BuildUtilityCards())
            cards.Add(BuildUtilityCard(utility));

        for (int i = 0; i < cards.Count; i++)
        {
            int row = i;
            while (HubGrid.RowDefinitions.Count <= row)
                HubGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetColumn(cards[i], 0);
            Grid.SetRow(cards[i], row);
            HubGrid.Children.Add(cards[i]);
        }

        // Reaplicar los resúmenes ya calculados (p. ej. al volver a la pestaña,
        // cambiar el tema o cambiar el idioma) para no dejar las cards nuevas en
        // "Calculando estado…".
        ApplyCachedSummaries();
        RefreshCachedHubCards();
    }

    /// <summary>
    /// Card de una categoría del catálogo: ícono, título, resumen de grupos y
    /// los contadores de estado (recomendado / por defecto / personalizado).
    /// Al tocarla se abre el drill-down de la categoría.
    /// </summary>
    private Border BuildCategoryCard(OptimizationFeature feature)
    {
        var title = _catalogText.FeatureName(feature, I18n.Current);

        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 12, 12),
            BorderBrush = TransparentBrush,
            BorderThickness = new Thickness(1)
        };

        // Misma geografía que las cards de Winhance: ícono · título/subtítulo ·
        // pastillas de estado · chevron.
        var root = new Grid { ColumnSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        root.Children.Add(BuildIconBadge(
            string.IsNullOrWhiteSpace(feature.Icon) ? "\uE713" : feature.Icon));

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // Subtítulo: los primeros grupos de la categoría (lo que hay adentro),
        // en una sola línea con puntos suspensivos si no entra.
        text.Children.Add(new TextBlock
        {
            Text = GroupSummary(feature),
            FontSize = 11.5,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // Mientras se lee el sistema: un solo aviso tenue, sin pastillas vacías.
        var counters = new TextBlock
        {
            Text = I18n.T("Calculando estado\u2026"),
            FontSize = 11,
            Foreground = MutedBrush,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _cardCounters[feature.Id] = counters;
        text.Children.Add(counters);

        Grid.SetColumn(text, 1);
        root.Children.Add(text);

        // Pastillas de estado (★ Recomendado / ~ De fábrica / ✓ Personalizado).
        var pills = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var rec = BuildStatePill("\uE735", SuccessBrush, Windows.UI.Color.FromArgb(38, 0x4C, 0xAF, 0x50), isTextSymbol: false);
        var def = BuildStatePill("~", MutedBrush, Windows.UI.Color.FromArgb(28, 0x80, 0x80, 0x80), isTextSymbol: true);
        var cus = BuildStatePill("\uE73E", WarningBrush, Windows.UI.Color.FromArgb(38, 0xFF, 0xB3, 0x00), isTextSymbol: false);
        pills.Children.Add(rec.Host);
        pills.Children.Add(def.Host);
        pills.Children.Add(cus.Host);
        _cardPills[feature.Id] = new CardPills(pills, rec.Host, rec.Text, def.Host, def.Text, cus.Host, cus.Text);
        Grid.SetColumn(pills, 2);
        root.Children.Add(pills);

        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 12,
            FontFamily = SymbolFont(),
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chevron, 3);
        root.Children.Add(chevron);

        card.Child = root;
        RevealEffect.Mark(card);

        // SIN swap de fondo al hover: CardHoverBrush es un velo plano (~15% blanco en
        // los temas con vidrio) que tapaba el esmerilado y la card "desaparecía" dejando
        // solo el fondo. El halo de RevealEffect (ya marcado arriba) es el feedback de
        // hover y no toca el relleno.
        card.Tapped += (s, e) => OpenCategory(feature.Id);

        return card;
    }

    /// <summary>
    /// Ícono de la card dentro de un círculo, como en la referencia: el glifo
    /// va centrado en un disco del color de la superficie de hover.
    /// </summary>
    private Border BuildIconBadge(string glyph)
    {
        return new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = CardHoverBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 17,
                FontFamily = SymbolFont(),
                Foreground = ThemeBrushes.Get("PrimaryTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    /// <summary>
    /// Los nombres de los primeros grupos de la categoría, para el subtítulo de la
    /// card ("Seguridad, Entrega de Contenido y Publicidad, …"), en el idioma activo.
    /// </summary>
    private string GroupSummary(OptimizationFeature feature)
    {
        const int maxGroups = 4;

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in feature.Settings)
        {
            var name = _catalogText.GroupName(setting, I18n.Current);
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
            names.Add(name);
            if (names.Count >= maxGroups) break;
        }

        if (names.Count == 0) return I18n.T("{0} ajustes", feature.Settings.Count);

        var summary = string.Join(", ", names);
        int groupCount = feature.Settings
            .Select(s => s.Group ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return groupCount > names.Count ? $"{summary}, …" : summary;
    }

    /// <summary>Pastilla de estado con ícono ("★ Recomendado 42/88", "~ De fábrica 48/88", "✓ Personalizado 1/88").</summary>
    private (Border Host, TextBlock Text) BuildStatePill(string? glyph, Brush foreground, Windows.UI.Color background, bool isTextSymbol = false)
    {
        var text = new TextBlock
        {
            Text = "—",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (!string.IsNullOrEmpty(glyph))
        {
            if (isTextSymbol)
            {
                content.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                });
            }
            else
            {
                content.Children.Add(new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 11,
                    FontFamily = SymbolFont(),
                    Foreground = foreground,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }
        content.Children.Add(text);

        var host = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(9, 3, 10, 3),
            Background = new SolidColorBrush(background),
            Child = content
        };

        return (host, text);
    }

    /// <summary>
    /// Card de una herramienta del sistema (TCP, Debloat, Repair…). La página
    /// original se abre en el Frame de abajo, dentro del mismo hub.
    /// </summary>
    private Border BuildUtilityCard(UtilityInfo utility)
    {
        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 12, 12),
            BorderBrush = TransparentBrush,
            BorderThickness = new Thickness(1)
        };

        var root = new Grid { ColumnSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        root.Children.Add(BuildIconBadge(utility.Glyph));

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = I18n.T(utility.Title),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = I18n.T(utility.Description),
            FontSize = 11.5,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        Grid.SetColumn(text, 1);
        root.Children.Add(text);

        // El chevron va en SU columna: sin esto caía en la columna 0, encima
        // del ícono (parecía un ícono roto).
        var chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 12,
            FontFamily = SymbolFont(),
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(chevron, 2);
        root.Children.Add(chevron);

        card.Child = root;
        RevealEffect.Mark(card);

        // Igual que las cards de categorías (ver arriba): sin swap al hover para que
        // la card no se vuelva transparente sobre el fondo.
        card.Tapped += (s, e) => OpenUtility(utility.Id);

        return card;
    }

    /// <summary>
    /// Las herramientas del sistema que se muestran como cards del hub. El
    /// nombre y la descripción salen del registro de componentes (una sola
    /// fuente de verdad) y la página original es la copia del exe.
    /// </summary>
    private List<UtilityInfo> BuildUtilityCards()
    {
        var result = new List<UtilityInfo>();
        foreach (var id in ComponentRegistry.HubIntegratedIds)
        {
            var comp = _registry.Find(id);
            var pageType = UtilityPageTypes.Map.TryGetValue(id, out var t) ? t : null;
            if (comp is null || pageType is null) continue;

            result.Add(new UtilityInfo(
                id,
                comp.Name,
                comp.Description,
                comp.IconGlyph,
                pageType));
        }
        return result;
    }

    // =====================================================================
    // DRILL-DOWN
    // =====================================================================

    private void OpenCategory(string featureId)
    {
        var feature = _catalog.Features.FirstOrDefault(f => string.Equals(f.Id, featureId, StringComparison.OrdinalIgnoreCase));
        if (feature is null) return;

        _mode = HubMode.Category;
        _openId = feature.Id;

        HubScroll.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;
        UtilityHost.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;

        HeaderIcon.Glyph = string.IsNullOrWhiteSpace(feature.Icon) ? "\uE713" : feature.Icon;
        HeaderTitleText.Text = _catalogText.FeatureName(feature, I18n.Current);
        UpdateHeaderSubtitleForCategory(feature);
        UpdateFeatureBulkActions(feature);

        // El bloque de energía (plan + gestión de planes) vive en la categoría
        // "Energía". El control decide su propio alto según la pestaña interna:
        // la del plan se mide por contenido, las de listas se acotan para que
        // su ScrollViewer siga funcionando.
        EnergyHost.Children.Clear();
        EnergyHost.Visibility = Visibility.Collapsed;
        _settingsSuppressed = false;
        if (string.Equals(feature.Id, "power", StringComparison.OrdinalIgnoreCase))
        {
            // Sin alto fijo: el control se mide por contenido. Con el alto fijo de
            // antes quedaba un hueco vacío entre la card del plan y las optimizaciones.
            // El scroll lo maneja DetailScroll, como en el resto de las categorías.
            var energy = new EnergyPlanControl();
            energy.TabChanged += OnEnergyTabChanged;
            EnergyHost.Children.Add(energy);
            EnergyHost.Visibility = Visibility.Visible;
            _settingsSuppressed = energy.SelectedTabIndex != 0;
        }

        RebuildSettings();
        ApplySettingsVisibility();

        // Los estados de la categoría se leen en segundo plano si todavía no
        // se leyeron: las filas se refrescan solas cuando llegan.
        _ = LoadFeatureStatesAsync(feature);
    }

    /// <summary>
    /// True cuando la pestaña interna de Energía no es la del plan (Gestionar o
    /// Comparar): ahí los ajustes de la categoría no tienen nada que ver y se
    /// ocultan junto con los atajos de cabecera.
    /// </summary>
    private bool _settingsSuppressed;

    private void OnEnergyTabChanged(int tabIndex)
    {
        bool suppressed = tabIndex != 0;
        if (suppressed == _settingsSuppressed) return;
        _settingsSuppressed = suppressed;
        ApplySettingsVisibility();
    }

    /// <summary>Aplica el ocultamiento de ajustes/atajos según la pestaña de Energía.</summary>
    private void ApplySettingsVisibility()
    {
        if (SettingsHost is not null)
            SettingsHost.Visibility = _settingsSuppressed ? Visibility.Collapsed : Visibility.Visible;
        UpdateFeatureBulkActions();
    }

    private void OpenUtility(string utilityId)
    {
        if (!UtilityPageTypes.Map.TryGetValue(utilityId, out var pageType)) return;
        var comp = _registry.Find(utilityId);

        _settingsSuppressed = false;
        _mode = HubMode.Utility;
        _openId = utilityId;

        HubScroll.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Collapsed;
        UtilityHost.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;

        HeaderIcon.Glyph = comp?.IconGlyph ?? "\uE713";
        HeaderTitleText.Text = comp?.Name ?? utilityId;
        HeaderSubtitleText.Text = comp?.Description ?? "";
        UpdateFeatureBulkActions();

        try
        {
            // Debloat y «Ajustes del sistema» comparten página: el modo se fija antes
            // de navegar (igual que el id de componente en ComponentHostPage) y, si la
            // página ya está montada porque el Frame la cachea, se le pide el cambio
            // directo para que reconstruya la lista.
            if (pageType == typeof(DebloatPage))
            {
                var systemTweaks = string.Equals(utilityId, "sistematweaks", StringComparison.OrdinalIgnoreCase);
                DebloatPage.PendingSystemTweaks = systemTweaks;
                if (UtilityFrame.Content is DebloatPage open)
                    open.ApplyMode(systemTweaks);
            }

            UtilityFrame.Navigate(pageType);
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error abriendo la herramienta {utilityId}", ex);
            ShowNotification(I18n.T("No se pudo abrir «{0}».", comp?.Name ?? utilityId), "error");
        }
    }

    private void BackToHub()
    {
        // Al salir del componente (categoría o herramienta) la consola ya cumplió:
        // se cierra sola, salvo que haya un lote corriendo, donde es la única
        // prueba de que la app sigue trabajando.
        if (!_busy) HideConsole();

        _mode = HubMode.Hub;
        _openId = null;
        _settingsSuppressed = false;

        HubScroll.Visibility = Visibility.Visible;
        DetailScroll.Visibility = Visibility.Collapsed;
        UtilityHost.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;

        ShowHeaderForHub();
    }

    private void ShowHeaderForHub()
    {
        HeaderIcon.Glyph = "\uE90F";
        HeaderTitleText.Text = I18n.T("Optimizaciones");
        // Solo se cuentan los ajustes que la app puede leer o aplicar: los que no
        // tienen mecanismo no se muestran, así que tampoco se prometen.
        HeaderSubtitleText.Text = I18n.T(
            "{0} ajustes en {1} categorías, más las herramientas del sistema.",
            _catalog.Features.Sum(f => f.Settings.Count(HasMechanism)),
            _catalog.Features.Count);
        UpdateFeatureBulkActions();
    }

    private void UpdateHeaderSubtitleForCategory(OptimizationFeature feature)
    {
        var rows = feature.Settings.Count(HasMechanism);
        if (_summaries.TryGetValue(feature.Id, out var summary))
        {
            HeaderSubtitleText.Text = I18n.T(
                "{0} ajustes · {1} recomendados · {2} de fábrica · {3} personalizados",
                rows, summary.Recommended, summary.Default, summary.Custom);
        }
        else
        {
            HeaderSubtitleText.Text = I18n.T("{0} ajustes", rows);
        }
    }

    /// <summary>Rehace las filas de la categoría abierta (grupos + ajustes).</summary>
    private void RebuildSettings()
    {
        if (SettingsHost == null) return;
        SettingsHost.Children.Clear();
        _rowBadges.Clear();
        _rowToggles.Clear();

        if (_mode != HubMode.Category || _openId is null) return;
        var feature = _catalog.Features.FirstOrDefault(f => string.Equals(f.Id, _openId, StringComparison.OrdinalIgnoreCase));
        if (feature is null) return;

        var groups = new List<SettingGroup>();
        foreach (var setting in feature.Settings)
        {
            // Un ajuste sin mecanismo (ni registro, ni tarea, ni energía, ni
            // script) no se puede leer NI aplicar: mostrarlo sería un
            // interruptor que no hace nada. El plan de energía, por ejemplo,
            // ya tiene su propio control en esta categoría.
            if (!HasMechanism(setting)) continue;

            var key = setting.Group ?? "";
            var group = groups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                group = new SettingGroup(key, _catalogText.GroupName(setting, I18n.Current));
                groups.Add(group);
            }
            group.Settings.Add(setting);
        }

        foreach (var group in groups)
            SettingsHost.Children.Add(BuildGroupSection(group));
    }

    /// <summary>Sección plegable con los ajustes de un grupo.</summary>
    private Border BuildGroupSection(SettingGroup group)
    {
        var section = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            BorderBrush = TransparentBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 14)
        };

        var root = new StackPanel { Spacing = 10 };

        var chevron = new FontIcon
        {
            Glyph = "\uE70D",
            FontSize = 12,
            FontFamily = SymbolFont(),
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var groupIcon = new FontIcon
        {
            Glyph = ResolveGroupGlyph(group.Key),
            FontSize = 16,
            FontFamily = SymbolFont(),
            Foreground = AccentBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var headerTitle = string.IsNullOrWhiteSpace(group.Name)
            ? I18n.T("General")
            : I18n.T(group.Name);

        var headerStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        headerStack.Children.Add(new TextBlock
        {
            Text = headerTitle,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = I18n.T("{0} ajustes", group.Settings.Count),
            FontSize = 11.5,
            Foreground = MutedBrush
        });

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        headerRow.Children.Add(chevron);
        headerRow.Children.Add(groupIcon);
        headerRow.Children.Add(headerStack);

        var header = new Button
        {
            Content = headerRow,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8)
        };

        var rows = new StackPanel { Spacing = 6 };
        foreach (var setting in group.Settings)
            rows.Children.Add(BuildSettingRow(setting));

        bool expanded = true;
        header.Click += (s, e) =>
        {
            expanded = !expanded;
            rows.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
        };

        root.Children.Add(header);
        root.Children.Add(rows);
        section.Child = root;
        return section;
    }

    /// <summary>Fila de un ajuste: ícono, nombre, descripción, badges y el control.</summary>
    private Border BuildSettingRow(OptimizationSetting setting)
    {
        _states.TryGetValue(setting.Id, out var state);

        var card = new Border
        {
            Background = ThemeBrushes.GetSurface("ExpanderHeaderBackground"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            BorderBrush = TransparentBrush,
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Columna 0: ícono del ajuste dentro de un círculo simétrico (estilo Winhance)
        var iconBadge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = CardHoverBrush,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = ResolveSettingGlyph(setting),
                FontSize = 15,
                FontFamily = SymbolFont(),
                Foreground = ThemeBrushes.Get("PrimaryTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(iconBadge, 0);
        grid.Children.Add(iconBadge);

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = _catalogText.SettingName(setting, I18n.Current),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        var description = _catalogText.SettingDescription(setting, I18n.Current);
        if (!string.IsNullOrWhiteSpace(description))
        {
            text.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 11.5,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        var badges = BuildBadgesRow(setting, state);
        _rowBadges[setting.Id] = badges;
        text.Children.Add(badges);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // Columna 2: los dos atajos de la fila, juntos para que se lean como un
        // par — estrella = valor RECOMENDADO, deshacer = valor de FÁBRICA.
        var shortcuts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (HasRecommendedValue(setting))
        {
            var recommended = new Button
            {
                Content = new FontIcon { Glyph = "\uE735", FontSize = 13, FontFamily = SymbolFont() },
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                MinWidth = 0,
                MinHeight = 0,
                Tag = setting.Id,
                Foreground = SuccessBrush,
                VerticalAlignment = VerticalAlignment.Center,
                // Queda siempre activa (igual que la de fábrica): si ya está en el
                // valor recomendado, volver a aplicarlo no cambia nada y confirma
                // con el aviso, en vez de mostrar un botón gris apagado.
                IsEnabled = state?.UnsupportedReason is null
            };
            ToolTipService.SetToolTip(recommended, I18n.T("Aplicar el valor recomendado"));
            recommended.Click += ApplyRecommendedButton_Click;
            shortcuts.Children.Add(recommended);
        }

        // Botón de "volver al valor de fábrica" (solo si el catálogo lo define).
        if (HasDefaultValue(setting))
        {
            var restore = new Button
            {
                Content = new FontIcon { Glyph = "\uE7A7", FontSize = 13, FontFamily = SymbolFont() },
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                MinWidth = 0,
                MinHeight = 0,
                Tag = setting.Id,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(restore, I18n.T("Restaurar valor de fábrica"));
            restore.Click += RestoreDefaultButton_Click;
            shortcuts.Children.Add(restore);
        }

        if (shortcuts.Children.Count > 0)
        {
            Grid.SetColumn(shortcuts, 2);
            grid.Children.Add(shortcuts);
        }

        var control = BuildSettingControl(setting, state);
        Grid.SetColumn(control, 3);
        grid.Children.Add(control);

        card.Child = grid;
        return card;
    }

    /// <summary>
    /// True si el ajuste tiene con qué leerse o aplicarse (registro, tarea
    /// programada, powercfg, script u opciones/rango).
    /// </summary>
    private static bool HasMechanism(OptimizationSetting setting)
        => setting.Registry.Count > 0
           || setting.Tasks.Count > 0
           || setting.Power.Count > 0
           || setting.Scripts.Count > 0
           || OptimizationStateService.IsSelection(setting)
           || OptimizationStateService.IsNumeric(setting);

    /// <summary>
    /// True si el ajuste es una ACCIÓN y no un estado: solo tiene scripts, no
    /// hay valor que leer. Se muestra con un botón, no con un interruptor.
    /// </summary>
    private static bool IsActionOnly(OptimizationSetting setting)
        => setting.Registry.Count == 0
           && setting.Tasks.Count == 0
           && setting.Power.Count == 0
           && setting.Scripts.Count > 0
           && !OptimizationStateService.IsSelection(setting)
           && !OptimizationStateService.IsNumeric(setting);

    /// <summary>El control del ajuste según su tipo (botón, toggle, combo o slider).</summary>
    private FrameworkElement BuildSettingControl(OptimizationSetting setting, OptimizationSettingState? state)
    {
        bool unsupported = state?.UnsupportedReason is not null;
        bool unknown = state is null;

        if (IsActionOnly(setting))
            return BuildActionButton(setting, state);

        if (OptimizationStateService.IsSelection(setting))
            return BuildSelectionControl(setting, state);

        if (OptimizationStateService.IsNumeric(setting))
            return BuildNumericControl(setting, state);

        bool desired = _pending.TryGetValue(setting.Id, out var pending)
            ? pending
            : (state?.IsOn ?? false);

        var toggle = new ToggleSwitch
        {
            IsOn = desired,
            OnContent = I18n.T("Activado"),
            OffContent = I18n.T("Desactivado"),
            MinWidth = 115,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = !unsupported && !unknown,
            Tag = setting.Id
        };
        toggle.Toggled += (s, e) => OnSettingToggled(setting, toggle.IsOn, toggle);
        _rowToggles[setting.Id] = toggle;
        return toggle;
    }

    /// <summary>
    /// Botón de un ajuste que es una acción (abrir el diálogo de DNS, limpiar el
    /// menú Inicio…). No tiene estado que leer, así que no lleva interruptor: un
    /// switch que nunca queda encendido sería mentir sobre lo que está aplicado.
    /// </summary>
    private Button BuildActionButton(OptimizationSetting setting, OptimizationSettingState? state)
    {
        var button = new Button
        {
            Content = I18n.T("Ejecutar"),
            MinWidth = 115,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            IsEnabled = state?.UnsupportedReason is null,
            Tag = setting.Id
        };
        ToolTipService.SetToolTip(button, I18n.T("Ejecutar"));
        button.Click += RunActionSetting_Click;
        return button;
    }

    private async void RunActionSetting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button || !_settings.TryGetValue(id, out var setting)) return;

        button.IsEnabled = false;
        var name = _catalogText.SettingName(setting, I18n.Current);
        AppendConsole(I18n.T("Ejecutando {0} de {1}: {2}...", 1, 1, name), ConsoleStatus.Running);

        try
        {
            var progress = new Progress<string>(line => AppendConsole(line, ConsoleStatus.Command));
            var timer = Stopwatch.StartNew();
            var result = await Task.Run(() => _applier.Apply(setting, true, null, null, progress));
            timer.Stop();

            if (result.Success)
            {
                AppendConsole(I18n.T("{0} de {1}: {2} terminada en {3}", 1, 1, name, FormatDuration(timer.Elapsed)), ConsoleStatus.Applied);
                ShowNotification(I18n.T("«{0}» ejecutada.", name), "success");
            }
            else
            {
                AppendConsole(I18n.T("{0} falló: {1}", name, result.Message), ConsoleStatus.Error);
                ShowNotification(I18n.T("No se pudo ejecutar «{0}».", name), "error");
            }
        }
        catch (Exception ex)
        {
            AppendConsole(I18n.T("{0} falló: {1}", name, ex.Message), ConsoleStatus.Error);
            try { _loggingService.LogError($"Error ejecutando el ajuste {id}", ex); } catch { }
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private readonly Dictionary<string, int> _pendingSelection = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Combo de un ajuste con opciones (nivel de UAC, SysMain, DNS…).</summary>
    private FrameworkElement BuildSelectionControl(OptimizationSetting setting, OptimizationSettingState? state)
    {
        int initial = _pendingSelection.TryGetValue(setting.Id, out var pending)
            ? pending
            : state?.SelectedOptionIndex ?? -1;

        var combo = new ComboBox
        {
            MinWidth = 190,
            MaxWidth = 260,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = state is not null && state.UnsupportedReason is null
        };

        for (int i = 0; i < setting.Options.Count; i++)
        {
            var opt = setting.Options[i];
            var optName = _catalogText.OptionName(opt, I18n.Current);
            if (opt.IsRecommended)
                optName = $"★ {optName}";
            else if (opt.IsDefault)
                optName = $"~ {optName}";
            combo.Items.Add(optName);
        }

        if (initial >= 0 && initial < combo.Items.Count)
            combo.SelectedIndex = initial;

        // La suscripción va DESPUÉS de fijar el índice inicial: SelectedIndex dispara
        // SelectionChanged también cuando lo pone el código, así que suscribirse antes
        // dejaba TODOS los combos marcados como "pendientes" con solo abrir la categoría.
        combo.SelectionChanged += (s, e) =>
        {
            if (combo.SelectedIndex < 0) return;
            if (state?.SelectedOptionIndex == combo.SelectedIndex) _pendingSelection.Remove(setting.Id);
            else _pendingSelection[setting.Id] = combo.SelectedIndex;
            UpdatePendingUi();
            RefreshRowBadges(setting.Id);
        };

        return combo;
    }

    /// <summary>Slider de un ajuste con rango numérico (%, minutos, milisegundos…).</summary>
    private FrameworkElement BuildNumericControl(OptimizationSetting setting, OptimizationSettingState? state)
    {
        var range = setting.Numeric;
        long min = range?.Min ?? 0;
        long max = range?.Max ?? 100;
        if (max <= min) max = min + 100;
        long step = range?.Step is > 0 ? range.Step : 1;

        long current = _pendingNumeric.TryGetValue(setting.Id, out var pending)
            ? pending
            : state?.NumericValue
              ?? OptimizationStateService.DefaultNumericValue(setting)
              ?? (min + max) / 2;

        var valueText = new TextBlock
        {
            Text = FormatNumeric(setting, current),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 56,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Value = current,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = state is not null && state.UnsupportedReason is null,
            Tag = setting.Id
        };

        slider.ValueChanged += (s, e) =>
        {
            long value = (long)Math.Round(slider.Value / step) * step;
            if (state?.NumericValue == value) _pendingNumeric.Remove(setting.Id);
            else _pendingNumeric[setting.Id] = value;
            valueText.Text = FormatNumeric(setting, value);
            UpdatePendingUi();
            RefreshRowBadges(setting.Id);
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        panel.Children.Add(slider);
        panel.Children.Add(valueText);
        return panel;
    }

    /// <summary>Valor del slider con su unidad (". 100 %", "5 min").</summary>
    private static string FormatNumeric(OptimizationSetting setting, long value)
    {
        var units = setting.Numeric?.Units;
        return string.IsNullOrWhiteSpace(units) ? value.ToString() : $"{value} {units}";
    }

    /// <summary>True si el ajuste tiene un cambio esperando el "Aplicar".</summary>
    private bool HasPending(string settingId)
        => _pending.ContainsKey(settingId)
           || _pendingSelection.ContainsKey(settingId)
           || _pendingNumeric.ContainsKey(settingId);

    /// <summary>True si el ajuste tiene algún valor de fábrica al que volver.</summary>
    private static bool HasDefaultValue(OptimizationSetting setting)
        => OptimizationStateService.DefaultState(setting) is not null
           || OptimizationStateService.DefaultOptionIndex(setting) is not null
           || OptimizationStateService.DefaultNumericValue(setting) is not null;

    /// <summary>True si el catálogo define un valor recomendado para este ajuste.</summary>
    private static bool HasRecommendedValue(OptimizationSetting setting)
    {
        // Una acción (solo script) no tiene "valor recomendado": no hay estado.
        if (IsActionOnly(setting)) return false;

        if (OptimizationStateService.IsSelection(setting))
            return OptimizationStateService.RecommendedOptionIndex(setting) is not null;

        if (OptimizationStateService.IsNumeric(setting))
            return OptimizationStateService.RecommendedNumericValue(setting) is not null;

        return OptimizationStateService.RecommendedState(setting) is not null;
    }

    private void OnSettingToggled(OptimizationSetting setting, bool value, ToggleSwitch toggle)
    {
        if (_states.TryGetValue(setting.Id, out var state) && state.IsOn is bool current && current == value)
            _pending.Remove(setting.Id);
        else
            _pending[setting.Id] = value;

        UpdatePendingUi();
        RefreshRowBadges(setting.Id);
    }

    private void RefreshRowBadges(string settingId)
    {
        if (!_rowBadges.TryGetValue(settingId, out var panel)) return;
        if (!_settings.TryGetValue(settingId, out var setting)) return;
        _states.TryGetValue(settingId, out var state);

        panel.Children.Clear();
        foreach (var badge in BuildBadges(setting, state))
            panel.Children.Add(badge);
    }

    private StackPanel BuildBadgesRow(OptimizationSetting setting, OptimizationSettingState? state)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var badge in BuildBadges(setting, state))
            panel.Children.Add(badge);
        return panel;
    }

    /// <summary>
    /// Los badges de una fila: el valor RECOMENDADO y el valor ACTUAL del equipo,
    /// uno al lado del otro para compararlos de un vistazo
    /// ("★ Recomendado: Activado · Actual: Desactivado"), más el requisito del
    /// Windows y si el cambio quedó pendiente.
    ///
    /// No hay pastilla de "por defecto": el estado de fábrica no se explica con una
    /// palabra sola (y el botón de deshacer de la fila ya lo representa). Tampoco
    /// hay "Personalizado": si el valor actual no coincide con el recomendado, se
    /// ve en la comparación de los dos badges.
    /// </summary>
    private List<Border> BuildBadges(OptimizationSetting setting, OptimizationSettingState? state)
    {
        var badges = new List<Border>();

        // Acción (solo script): no hay estado que comparar, así que el badge lo
        // dice en vez de inventar un "recomendado" que no existe.
        if (IsActionOnly(setting))
        {
            badges.Add(BuildBadge(I18n.T("Acción"), AccentBrush, new SolidColorBrush(Windows.UI.Color.FromArgb(34, 0x00, 0x78, 0xD4)), "\uE768"));
            return badges;
        }

        if (state is not null && state.HasBadgeData)
        {
            // Primero la recomendación del catálogo: es la referencia contra la
            // que se lee el valor actual, coincida o no.
            var recommendedLabel = DescribeRecommendedValue(setting);
            if (!string.IsNullOrEmpty(recommendedLabel))
            {
                badges.Add(BuildBadge(
                    $"{I18n.T("Recomendado")}: {recommendedLabel}",
                    SuccessBrush,
                    new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0x4C, 0xAF, 0x50)),
                    "\uE735"));
            }

            // Después el valor real aplicado hoy en el sistema.
            var currentLabel = DescribeCurrentValue(setting, state);
            if (!string.IsNullOrEmpty(currentLabel))
            {
                badges.Add(BuildBadge(
                    $"{I18n.T("Actual")}: {currentLabel}",
                    ThemeBrushes.Get("SecondaryTextBrush"),
                    new SolidColorBrush(Windows.UI.Color.FromArgb(22, 0x80, 0x80, 0x80))));
            }

            if (state.UnsupportedReason is string reason)
                badges.Add(BuildBadge(I18n.T(reason), MutedBrush, new SolidColorBrush(Windows.UI.Color.FromArgb(28, 0x80, 0x80, 0x80)), "\uE7BA"));
        }

        if (HasPending(setting.Id))
        {
            var pendingLabel = DescribePendingValue(setting);
            var text = string.IsNullOrEmpty(pendingLabel)
                ? I18n.T("Pendiente")
                : $"{I18n.T("Pendiente")} → {pendingLabel}";
            badges.Add(BuildBadge(text, AccentBrush, new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0x00, 0x78, 0xD4)), "\uE823"));
        }

        return badges;
    }

    private string? DescribeCurrentValue(OptimizationSetting setting, OptimizationSettingState state)
    {
        if (OptimizationStateService.IsSelection(setting))
        {
            if (state.SelectedOptionIndex is int idx && idx >= 0 && idx < setting.Options.Count)
                return _catalogText.OptionName(setting.Options[idx], I18n.Current);
            return I18n.T("Personalizado");
        }

        if (OptimizationStateService.IsNumeric(setting))
        {
            if (state.NumericValue is long val)
                return FormatNumeric(setting, val);
            return null;
        }

        if (state.IsOn is bool on)
            return on ? I18n.T("Activado") : I18n.T("Desactivado");

        return null;
    }

    private string? DescribeRecommendedValue(OptimizationSetting setting)
    {
        if (OptimizationStateService.IsSelection(setting))
        {
            if (OptimizationStateService.RecommendedOptionIndex(setting) is int idx && idx >= 0 && idx < setting.Options.Count)
                return _catalogText.OptionName(setting.Options[idx], I18n.Current);
            return null;
        }

        if (OptimizationStateService.IsNumeric(setting))
        {
            if (OptimizationStateService.RecommendedNumericValue(setting) is long val)
                return FormatNumeric(setting, val);
            return null;
        }

        if (OptimizationStateService.RecommendedState(setting) is bool on)
            return on ? I18n.T("Activado") : I18n.T("Desactivado");

        return null;
    }

    private string? DescribePendingValue(OptimizationSetting setting)
    {
        if (_pendingSelection.TryGetValue(setting.Id, out var idx) && idx >= 0 && idx < setting.Options.Count)
            return _catalogText.OptionName(setting.Options[idx], I18n.Current);

        if (_pendingNumeric.TryGetValue(setting.Id, out var num))
            return FormatNumeric(setting, num);

        if (_pending.TryGetValue(setting.Id, out var on))
            return on ? I18n.T("Activado") : I18n.T("Desactivado");

        return null;
    }

    private static Border BuildBadge(string text, Brush foreground, Brush background, string? glyph = null, bool isTextSymbol = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center
        };

        UIElement child;
        if (!string.IsNullOrEmpty(glyph))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (isTextSymbol)
            {
                row.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                });
            }
            else
            {
                row.Children.Add(new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 10,
                    FontFamily = SymbolFont(),
                    Foreground = foreground,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            row.Children.Add(label);
            child = row;
        }
        else
        {
            child = label;
        }

        return new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            Background = background,
            Child = child
        };
    }

    // =====================================================================
    // ESTADO DEL SISTEMA
    // =====================================================================

    /// <summary>
    /// Lee el estado real de los ajustes de una categoría (en segundo plano) y
    /// actualiza los contadores de su card. La lectura se serializa porque el
    /// servicio cachea lo que consulta.
    /// </summary>
    private async Task LoadFeatureStatesAsync(OptimizationFeature feature)
    {
        if (_featureLoads.TryGetValue(feature.Id, out var existing))
        {
            await existing;
            return;
        }

        // Las lecturas van EN PARALELO. Antes iban en serie detrás de un candado
        // global: leer una categoría de 110 ajustes lanza un schtasks por ajuste
        // de tarea, y las diez categorías en fila tardaban minutos —la card se
        // quedaba en "Calculando estado…" y no terminaba nunca. El servicio
        // cachea en diccionarios concurrentes, así que varios hilos pueden
        // leer al mismo tiempo.
        var task = Task.Run(() =>
        {
            var list = new OptimizationSettingState[feature.Settings.Count];
            var failures = new List<string>();

            Parallel.For(0, feature.Settings.Count,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                i =>
                {
                    var setting = feature.Settings[i];
                    StateReadGate.Wait();
                    try
                    {
                        list[i] = _stateService.ReadState(setting);
                    }
                    catch (Exception ex)
                    {
                        // Un ajuste que falla no puede dejar la categoría entera
                        // sin badges: se sigue con el resto.
                        list[i] = new OptimizationSettingState { Setting = setting };
                        lock (failures) failures.Add($"{setting.Id}: {ex.Message}");
                    }
                    finally
                    {
                        StateReadGate.Release();
                    }
                });

            if (failures.Count > 0)
                try { _loggingService.LogWarning($"Optimizaciones: {failures.Count} ajustes sin estado en «{feature.Id}»."); } catch { }

            return list.ToList();
        });
        _featureLoads[feature.Id] = task;

        List<OptimizationSettingState> states;
        try
        {
            states = await task;
        }
        catch (Exception ex)
        {
            try { _loggingService.LogError($"Error leyendo el estado de {feature.Id}", ex); } catch { }
            // La card no puede quedarse pegada en "Calculando estado…": se avisa
            // que no se pudo leer y se sigue.
            if (_cardCounters.TryGetValue(feature.Id, out var failedCounter))
            {
                failedCounter.Text = I18n.T("No se pudo leer el estado");
                failedCounter.Foreground = ErrorBrush;
            }
            return;
        }

        foreach (var st in states)
            _states[st.Setting.Id] = st;

        var summary = OptimizationStateService.Summarize(feature, states);
        _summaries[feature.Id] = summary;
        StateCache[feature.Id] = new CachedStates(states, summary, DateTime.Now);
        UpdateCategoryCard(feature);

        if (_mode == HubMode.Category && string.Equals(_openId, feature.Id, StringComparison.OrdinalIgnoreCase))
        {
            UpdateHeaderSubtitleForCategory(feature);
            RebuildSettings();
        }
    }

    private void UpdateCategoryCard(OptimizationFeature feature)
    {
        if (!_summaries.TryGetValue(feature.Id, out var summary)) return;

        // Con el resumen listo, el aviso de carga se va y entran las pastillas.
        if (_cardCounters.TryGetValue(feature.Id, out var counters))
            counters.Visibility = Visibility.Collapsed;

        if (_cardPills.TryGetValue(feature.Id, out var pills))
        {
            int total = Math.Max(summary.Total, 0);
            string ratio(int value) => $"{value}/{total}";

            pills.RecommendedText.Text = $"{I18n.T("Recomendado")} {ratio(summary.Recommended)}";
            // "De fábrica" en vez de "Por defecto": es lo mismo que promete el botón
            // de deshacer de cada fila, y el término viejo no se entendía.
            pills.DefaultText.Text = $"{I18n.T("De fábrica")} {ratio(summary.Default)}";
            pills.CustomText.Text = $"{I18n.T("Personalizado")} {ratio(summary.Custom)}";

            var visible = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            pills.Recommended.Visibility = visible;
            pills.Default.Visibility = visible;
            // "Personalizado" solo se muestra si hay algo que no es ni lo
            // recomendado ni lo de fábrica (igual que Winhance).
            pills.Custom.Visibility = summary.Custom > 0 ? visible : Visibility.Collapsed;

            // El CONTENEDOR de las pastillas también: se creaba collapsed y
            // nunca se volvía a mostrar, así que las tres badges no se veían
            // aunque tuvieran su texto.
            pills.Host.Visibility = visible;
        }
    }

    // =====================================================================
    // CAMBIOS PENDIENTES
    // =====================================================================

    /// <summary>Total de cambios esperando el "Aplicar" (switches + combos + sliders).</summary>
    private int TotalPending => _pending.Count + _pendingSelection.Count + _pendingNumeric.Count;

    private void UpdatePendingUi()
    {
        int count = TotalPending;

        if (ActionBar == null) return;

        ActionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count == 0) return;

        PendingText.Text = I18n.T("{0} cambios pendientes de aplicar", count);
        ApplyButton.Content = _busy ? I18n.T("Aplicando...") : I18n.T("Aplicar cambios");
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        _pending.Clear();
        _pendingSelection.Clear();
        _pendingNumeric.Clear();
        UpdatePendingUi();
        RebuildSettings();
        ShowNotification(I18n.T("Se descartaron los cambios pendientes."), "warning");
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e) => await ConfirmAndApplyAsync();

    private async Task ConfirmAndApplyAsync()
    {
        if (TotalPending == 0)
        {
            ShowNotification(I18n.T("No hay ajustes pendientes de aplicar."), "warning");
            return;
        }

        if (XamlRoot is null) return;

        var dialog = new ContentDialog
        {
            Title = I18n.T("Aplicar {0} ajustes", TotalPending),
            PrimaryButtonText = I18n.T("Aplicar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = BuildReviewPanel()
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        await RunApplyAsync();
    }

    private UIElement BuildReviewPanel()
    {
        var root = new StackPanel { Spacing = 10, MaxWidth = 560 };
        root.Children.Add(new TextBlock
        {
            Text = I18n.T("Se van a aplicar {0} ajustes. Revisá la lista antes de continuar.", TotalPending),
            FontSize = 13,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap
        });

        var list = new StackPanel { Spacing = 6 };
        foreach (var (id, value) in _pending)
        {
            if (!_settings.TryGetValue(id, out var setting)) continue;
            list.Children.Add(BuildReviewRow(setting, value ? I18n.T("Activado") : I18n.T("Desactivado")));
        }

        foreach (var (id, index) in _pendingSelection)
        {
            if (!_settings.TryGetValue(id, out var setting)) continue;
            var option = index >= 0 && index < setting.Options.Count ? setting.Options[index] : null;
            var name = option is null ? "" : _catalogText.OptionName(option, I18n.Current);
            list.Children.Add(BuildReviewRow(setting, name));
        }

        foreach (var (id, value) in _pendingNumeric)
        {
            if (!_settings.TryGetValue(id, out var setting)) continue;
            list.Children.Add(BuildReviewRow(setting, FormatNumeric(setting, value)));
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = list
        };
        root.Children.Add(scroll);
        return root;
    }

    private Border BuildReviewRow(OptimizationSetting setting, string value)
    {
        var row = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            BorderBrush = TransparentBrush,
            BorderThickness = new Thickness(1)
        };

        var col = new StackPanel { Spacing = 2 };
        col.Children.Add(new TextBlock
        {
            Text = _catalogText.SettingName(setting, I18n.Current),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        col.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap
        });

        row.Child = col;
        return row;
    }

    private async Task RunApplyAsync()
    {
        var changes = _pending.ToList();
        var selections = _pendingSelection.ToList();
        var numbers = _pendingNumeric.ToList();
        if (changes.Count == 0 && selections.Count == 0 && numbers.Count == 0) return;

        _busy = true;
        SetBatchBusy(true);
        BeginBatchUi(changes.Count + selections.Count + numbers.Count);

        int ok = 0, failed = 0, skipped = 0;
        var failures = new List<string>();

        AppendConsole(I18n.T("Iniciando lote..."), ConsoleStatus.Neutral);

        var progress = new Progress<string>(line => AppendConsole(line, ConsoleStatus.Command));

        try
        {
            int index = 0;
            int total = changes.Count + selections.Count + numbers.Count;

            foreach (var (id, value) in changes)
            {
                index++;
                if (!_settings.TryGetValue(id, out var setting)) { failed++; continue; }

                var name = _catalogText.SettingName(setting, I18n.Current);
                var unsupported = OptimizationStateService.GetUnsupportedReason(setting);
                if (unsupported is not null)
                {
                    skipped++;
                    AppendConsole(I18n.T("{0} de {1}: {2} omitida ({3})", index, total, name, unsupported), ConsoleStatus.Skipped);
                    UpdateBatchUi(index, name);
                    continue;
                }

                if (setting.RequiresConfirmation)
                {
                    bool confirmed = await ConfirmSettingAsync(setting, name);
                    if (!confirmed)
                    {
                        skipped++;
                        AppendConsole(I18n.T("{0} de {1}: {2} cancelada", index, total, name), ConsoleStatus.Skipped);
                        UpdateBatchUi(index, name);
                        continue;
                    }
                }

                AppendConsole(I18n.T("Ejecutando {0} de {1}: {2}...", index, total, name), ConsoleStatus.Running);
                var timer = Stopwatch.StartNew();
                var result = await Task.Run(() => _applier.Apply(setting, value, null, null, progress));
                timer.Stop();

                if (result.Success)
                {
                    ok++;
                    AppendConsole(I18n.T("{0} de {1}: {2} terminada en {3}", index, total, name, FormatDuration(timer.Elapsed)), ConsoleStatus.Applied);
                }
                else
                {
                    failed++;
                    failures.Add($"{name}: {result.Message}");
                    AppendConsole(I18n.T("{0} de {1}: {2} falló en {3}", index, total, name, FormatDuration(timer.Elapsed)), ConsoleStatus.Error);
                }

                UpdateBatchUi(index, name);
            }

            foreach (var (id, optionIndex) in selections)
            {
                index++;
                if (!_settings.TryGetValue(id, out var setting)) { failed++; continue; }

                var name = _catalogText.SettingName(setting, I18n.Current);
                AppendConsole(I18n.T("Ejecutando {0} de {1}: {2}...", index, total, name), ConsoleStatus.Running);
                var timer = Stopwatch.StartNew();
                var result = await Task.Run(() => _applier.Apply(setting, null, optionIndex, null, progress));
                timer.Stop();

                if (result.Success)
                {
                    ok++;
                    AppendConsole(I18n.T("{0} de {1}: {2} terminada en {3}", index, total, name, FormatDuration(timer.Elapsed)), ConsoleStatus.Applied);
                }
                else
                {
                    failed++;
                    failures.Add($"{name}: {result.Message}");
                    AppendConsole(I18n.T("{0} de {1}: {2} falló en {3}", index, total, name, FormatDuration(timer.Elapsed)), ConsoleStatus.Error);
                }

                UpdateBatchUi(index, name);
            }

            // Ajustes con slider: se escribe el valor numérico tal cual (powercfg
            // o la clave de registro que define el rango).
            foreach (var (id, value) in numbers)
            {
                index++;
                if (!_settings.TryGetValue(id, out var setting)) { failed++; continue; }

                var name = _catalogText.SettingName(setting, I18n.Current);
                AppendConsole(I18n.T("Ejecutando {0} de {1}: {2}...", index, total, name), ConsoleStatus.Running);
                var timer = Stopwatch.StartNew();
                var result = await Task.Run(() => _applier.Apply(setting, null, null, value, progress));
                timer.Stop();

                if (result.Success)
                {
                    ok++;
                    AppendConsole(I18n.T("{0} de {1}: {2} terminada en {3}", index, total, name, FormatDuration(timer.Elapsed)), ConsoleStatus.Applied);
                }
                else
                {
                    failed++;
                    failures.Add($"{name}: {result.Message}");
                    AppendConsole(I18n.T("{0} de {1}: {2} falló en {3}", index, total, name, FormatDuration(timer.Elapsed)), ConsoleStatus.Error);
                }

                UpdateBatchUi(index, name);
            }
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add(ex.Message);
            AppendConsole(I18n.T("ERROR de lote: {0}", ex.Message), ConsoleStatus.Error);
            _loggingService.LogError("Error ejecutando el lote de optimizaciones", ex);
        }
        finally
        {
            _busy = false;
            SetBatchBusy(false);
        }

        // Los cambios aplicados se descuentan de la lista de pendientes.
        foreach (var (id, _) in changes) MarkTouched(id);
        foreach (var (id, _) in selections) MarkTouched(id);
        foreach (var (id, _) in numbers) MarkTouched(id);

        _pending.Clear();
        _pendingSelection.Clear();
        _pendingNumeric.Clear();
        UpdatePendingUi();

        // Si el lote tocó la apariencia del PROPIO Windows (efectos de transparencia o modo
        // claro/oscuro), la app tiene que volver a decidir cómo pinta sus paneles. Va antes de
        // releer los estados para que el repintado no se mezcle con el rearmado de las filas.
        RefreshAppearanceAfterSystemChange(
            changes.Select(c => c.Key).Concat(selections.Select(s => s.Key)).Concat(numbers.Select(n => n.Key)));

        // Releer el estado de las categorías tocadas para que los badges y los
        // contadores reflejen el sistema real.
        await ReloadTouchedFeaturesAsync();

        var summary = I18n.T("{0} aplicadas, {1} omitidas, {2} con error.", ok, skipped, failed);
        AppendConsole(I18n.T("Lote completado en {0}: {1}.", FormatDuration(_batchStopwatch.Elapsed), summary),
            failed > 0 ? ConsoleStatus.Error : ConsoleStatus.Applied);
        EndBatchUi(failed > 0);
        ShowNotification(I18n.T("Lote {0}: {1}", I18n.T("aplicado"), summary), failed > 0 ? "error" : "success");
    }

    /// <summary>
    /// Vuelve a leer el estado de las categorías tocadas por el lote (y de la que
    /// está abierta) para refrescar badges y contadores.
    ///
    /// No alcanza con la categoría abierta: un PERFIL se aplica desde el hub sin
    /// abrir ninguna, y las cards tienen que mostrar el estado nuevo sin reiniciar.
    /// </summary>
    private async Task ReloadTouchedFeaturesAsync()
    {
        var ids = new HashSet<string>(_touchedFeatures, StringComparer.OrdinalIgnoreCase);
        if (_openId is not null) ids.Add(_openId);

        if (ids.Count == 0) return;

        // Lo aplicado acaba de cambiar el sistema: la caché de esas categorías
        // quedó vieja y no debe enseñarse al volver a la pestaña.
        foreach (var id in ids)
            StateCache.TryRemove(id, out _);

        foreach (var id in ids)
        {
            var feature = _catalog.Features
                .FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
            if (feature is null) continue;

            _featureLoads.Remove(feature.Id);
            await LoadFeatureStatesAsync(feature);
        }

        _touchedFeatures.Clear();
    }

    /// <summary>Anota la categoría de un ajuste aplicado, para releer su estado después.</summary>
    private void MarkTouched(string settingId)
    {
        var featureId = FindFeatureId(settingId);
        if (featureId is not null) _touchedFeatures.Add(featureId);
    }

    /// <summary>
    /// Aplica el valor RECOMENDADO del catálogo en un ajuste y relee la fila.
    /// Es el botón de la estrella, al lado del de fábrica.
    /// </summary>
    private async void ApplyRecommendedButton_Click(object sender, RoutedEventArgs e)
    {
        // Un lote en curso usa el mismo applier: no se puede intercalar otro ajuste.
        if (_busy) return;
        if (sender is not FrameworkElement element) return;
        var id = element.Tag as string;
        if (id is null || !_settings.TryGetValue(id, out var setting)) return;

        // El atajo aplica YA: lo que hubiera pendiente en esta fila contradiría al
        // sistema y se re-aplicaría en el próximo "Aplicar cambios".
        ClearPendingFor(id);

        var name = _catalogText.SettingName(setting, I18n.Current);
        var progress = new Progress<string>(line => AppendConsole(line, ConsoleStatus.Command));

        AppendConsole(I18n.T("Aplicando recomendado: {0}...", name), ConsoleStatus.Running);
        var result = await Task.Run(() => _applier.ApplyRecommended(setting, progress));

        if (result.Success)
        {
            AppendConsole(I18n.T("{0} quedó en el valor recomendado", name), ConsoleStatus.Applied);
            ShowNotification(I18n.T("«{0}» quedó en el valor recomendado.", name), "success");
        }
        else
        {
            AppendConsole(I18n.T("{0} falló: {1}", name, result.Message), ConsoleStatus.Error);
            ShowNotification(I18n.T("No se pudo aplicar «{0}».", name), "error");
        }

        RefreshAppearanceAfterSystemChange(new[] { id });
        await ReloadSettingStateAsync(id);
    }

    /// <summary>
    /// Descarta lo que estuviera esperando el "Aplicar" en una fila: se usa cuando
    /// un atajo (recomendado / fábrica) ya escribió el valor real en el sistema.
    /// </summary>
    private void ClearPendingFor(string settingId)
    {
        bool removed = _pending.Remove(settingId);
        removed |= _pendingSelection.Remove(settingId);
        removed |= _pendingNumeric.Remove(settingId);
        if (removed) UpdatePendingUi();
    }

    /// <summary>
    /// Relee la categoría del ajuste para que la fila (y los contadores de la card)
    /// muestren el estado real después de un atajo.
    /// </summary>
    private async Task ReloadSettingStateAsync(string settingId)
    {
        var featureId = FindFeatureId(settingId);
        if (featureId is null) return;

        _featureLoads.Remove(featureId);
        var feature = _catalog.Features.FirstOrDefault(f => string.Equals(f.Id, featureId, StringComparison.OrdinalIgnoreCase));
        if (feature is not null)
            await LoadFeatureStatesAsync(feature);
    }

    /// <summary>
    /// Dos ajustes de la categoría "Tema de Windows" no son una preferencia más: cambian CÓMO
    /// PINTA LA APP. "Efectos de transparencia" (EnableTransparency) decide si Windows puede
    /// renderizar el acrílico —el vidrio de los paneles se elige al arrancar y no se vuelve a
    /// consultar solo— y el modo claro/oscuro es la base del tema "Usar sistema".
    ///
    /// Sin esto, después de aplicarlos el navbar, la barra de título y las barras internas
    /// seguían con el relleno del camino viejo: un tinte sin la copia desenfocada detrás, que se
    /// ve transparente (el reporte "aplico el cambio de apariencia y el navbar queda transparente").
    /// </summary>
    private void RefreshAppearanceAfterSystemChange(IEnumerable<string> settingIds)
    {
        bool transparency = false, windowsTheme = false;

        foreach (var id in settingIds)
        {
            if (!_settings.TryGetValue(id, out var setting)) continue;
            foreach (var entry in setting.Registry)
            {
                if (string.Equals(entry.ValueName, "EnableTransparency", StringComparison.OrdinalIgnoreCase))
                    transparency = true;
                else if (string.Equals(entry.ValueName, "AppsUseLightTheme", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(entry.ValueName, "SystemUsesLightTheme", StringComparison.OrdinalIgnoreCase))
                    windowsTheme = true;
            }
        }

        if (!transparency && !windowsTheme) return;

        if (windowsTheme)
        {
            try
            {
                // Con "Usar sistema" la base sigue al modo de Windows: hay que reaplicar el tema
                // para que la paleta, los pinceles live y los paneles se recalculen.
                if (App.Services.GetRequiredService<IThemeService>().CurrentTheme == AppTheme.SystemDefault)
                    App.Services.GetRequiredService<IThemeApplier>().ApplyTheme(AppTheme.SystemDefault);
            }
            catch (Exception ex)
            {
                try { _loggingService.LogWarning($"Apariencia: no se pudo reaplicar el tema del sistema: {ex.Message}"); } catch { }
            }
        }

        if (transparency) PanelAppearance.RefreshAfterSystemChange();
    }

    private async void RestoreDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        // Un lote en curso usa el mismo applier: no se puede intercalar otro ajuste.
        if (_busy) return;
        if (sender is not FrameworkElement element) return;
        var id = element.Tag as string;
        if (id is null || !_settings.TryGetValue(id, out var setting)) return;

        // Igual que la estrella: el valor de fábrica se escribe ahora, así que un
        // cambio pendiente en esta fila lo pisaría en el próximo "Aplicar".
        ClearPendingFor(id);

        var name = _catalogText.SettingName(setting, I18n.Current);
        var progress = new Progress<string>(line => AppendConsole(line, ConsoleStatus.Command));

        AppendConsole(I18n.T("Restaurando: {0}...", name), ConsoleStatus.Running);
        var result = await Task.Run(() => _applier.ApplyDefault(setting, progress));

        if (result.Success)
        {
            AppendConsole(I18n.T("{0} restaurada al valor de fábrica", name), ConsoleStatus.Applied);
            ShowNotification(I18n.T("«{0}» quedó como lo trae Windows.", name), "success");
        }
        else
        {
            AppendConsole(I18n.T("{0} falló: {1}", name, result.Message), ConsoleStatus.Error);
            ShowNotification(I18n.T("No se pudo restaurar «{0}».", name), "error");
        }

        // El ajuste vuelve al estado de fábrica: se recalcula su badge. (Y si era uno de los dos
        // ajustes de apariencia de Windows, la app vuelve a elegir cómo pinta sus paneles.)
        RefreshAppearanceAfterSystemChange(new[] { id });
        await ReloadSettingStateAsync(id);
    }

    // =====================================================================
    // ATAJOS DE COMPONENTE (todo el componente de una vez)
    // =====================================================================

    private async void FeatureRecommendedButton_Click(object sender, RoutedEventArgs e)
        => await RunFeatureBulkAsync(recommended: true);

    private async void FeatureDefaultButton_Click(object sender, RoutedEventArgs e)
        => await RunFeatureBulkAsync(recommended: false);

    /// <summary>
    /// Muestra los atajos de la cabecera solo con una categoría abierta y solo si el
    /// catálogo define ese valor para al menos un ajuste aplicable en este equipo: en
    /// el hub y en las herramientas no hay nada que aplicar en bloque.
    /// </summary>
    private void UpdateFeatureBulkActions(OptimizationFeature? feature = null)
    {
        if (FeatureBulkActions == null) return;

        // Con los ajustes ocultos (pestañas Gestionar/Comparar de Energía) los
        // atajos de componente tampoco se muestran.
        if (_settingsSuppressed)
        {
            FeatureBulkActions.Visibility = Visibility.Collapsed;
            return;
        }

        if (feature is null && _mode == HubMode.Category && _openId is not null)
            feature = _catalog.Features.FirstOrDefault(f =>
                string.Equals(f.Id, _openId, StringComparison.OrdinalIgnoreCase));

        if (feature is null)
        {
            FeatureBulkActions.Visibility = Visibility.Collapsed;
            return;
        }

        bool canRecommend = feature.Settings.Any(s =>
            HasMechanism(s) && HasRecommendedValue(s)
            && OptimizationStateService.GetUnsupportedReason(s) is null);
        bool canDefault = feature.Settings.Any(s =>
            HasMechanism(s) && HasDefaultValue(s)
            && OptimizationStateService.GetUnsupportedReason(s) is null);

        FeatureBulkActions.Visibility = canRecommend || canDefault
            ? Visibility.Visible
            : Visibility.Collapsed;
        FeatureRecommendedButton.Visibility = canRecommend ? Visibility.Visible : Visibility.Collapsed;
        FeatureDefaultButton.Visibility = canDefault ? Visibility.Visible : Visibility.Collapsed;
        FeatureRecommendedButton.IsEnabled = canRecommend && !_busy;
        FeatureDefaultButton.IsEnabled = canDefault && !_busy;

        FeatureRecommendedText.Text = I18n.T("Recomendado");
        FeatureDefaultText.Text = I18n.T("Restaurar valor de fábrica");
        ToolTipService.SetToolTip(FeatureRecommendedButton,
            I18n.T("Aplicar todo lo recomendado del componente"));
        ToolTipService.SetToolTip(FeatureDefaultButton,
            I18n.T("Restaurar todo el componente a los valores de fábrica"));
    }

    /// <summary>
    /// Aplica el valor recomendado (o el de fábrica) a TODOS los ajustes de la
    /// categoría abierta, uno detrás del otro y con el mismo lote visible que
    /// "Aplicar cambios" (progreso, cronómetro y consola).
    /// </summary>
    private async Task RunFeatureBulkAsync(bool recommended)
    {
        if (_busy || _mode != HubMode.Category || _openId is null) return;
        var feature = _catalog.Features.FirstOrDefault(f =>
            string.Equals(f.Id, _openId, StringComparison.OrdinalIgnoreCase));
        if (feature is null) return;

        var targets = feature.Settings
            .Where(HasMechanism)
            .Where(s => recommended ? HasRecommendedValue(s) : HasDefaultValue(s))
            .ToList();
        if (targets.Count == 0) return;

        // Tomar el candado ANTES de esperar la lectura de estado: si se espera
        // primero, un segundo clic alcanza a entrar y lanza otro lote en paralelo.
        _busy = true;
        SetBatchBusy(true);

        // Si la categoría todavía está leyendo su estado, esperar esa lectura: si
        // no, el pase viejo podría terminar después y pisar los estados aplicados.
        if (_featureLoads.TryGetValue(feature.Id, out var pendingRead))
        {
            try { await pendingRead; } catch { }
            _featureLoads.Remove(feature.Id);
        }

        // Los atajos escriben el valor real ahora: lo pendiente de estas filas ya
        // no tiene sentido y se re-aplicaría en el próximo "Aplicar cambios".
        foreach (var setting in targets) ClearPendingFor(setting.Id);
        UpdatePendingUi();

        var featureName = _catalogText.FeatureName(feature, I18n.Current);
        BeginBatchUi(targets.Count);

        AppendConsole(recommended
            ? I18n.T("Aplicando recomendados de {0}...", featureName)
            : I18n.T("Restaurando valores de fábrica de {0}...", featureName),
            ConsoleStatus.Running);

        int ok = 0, skipped = 0, failed = 0;
        var failures = new List<string>();
        var progress = new Progress<string>(line => AppendConsole(line, ConsoleStatus.Command));

        try
        {
            int index = 0;
            foreach (var setting in targets)
            {
                index++;
                var name = _catalogText.SettingName(setting, I18n.Current);
                var unsupported = OptimizationStateService.GetUnsupportedReason(setting);
                if (unsupported is not null)
                {
                    skipped++;
                    AppendConsole(I18n.T("{0} de {1}: {2} omitida ({3})",
                        index, targets.Count, name, unsupported), ConsoleStatus.Skipped);
                    UpdateBatchUi(index, name);
                    continue;
                }

                if (setting.RequiresConfirmation)
                {
                    bool confirmed = await ConfirmSettingAsync(setting, name);
                    if (!confirmed)
                    {
                        skipped++;
                        AppendConsole(I18n.T("{0} de {1}: {2} cancelada",
                            index, targets.Count, name), ConsoleStatus.Skipped);
                        UpdateBatchUi(index, name);
                        continue;
                    }
                }

                AppendConsole(I18n.T("Ejecutando {0} de {1}: {2}...",
                    index, targets.Count, name), ConsoleStatus.Running);
                var timer = Stopwatch.StartNew();
                var result = await Task.Run(() => recommended
                    ? _applier.ApplyRecommended(setting, progress)
                    : _applier.ApplyDefault(setting, progress));
                timer.Stop();

                if (result.Success)
                {
                    ok++;
                    AppendConsole(I18n.T("{0} de {1}: {2} terminada en {3}",
                        index, targets.Count, name, FormatDuration(timer.Elapsed)), ConsoleStatus.Applied);
                }
                else
                {
                    failed++;
                    failures.Add($"{name}: {result.Message}");
                    AppendConsole(I18n.T("{0} de {1}: {2} falló en {3}",
                        index, targets.Count, name, FormatDuration(timer.Elapsed)), ConsoleStatus.Error);
                }

                UpdateBatchUi(index, name);
            }
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add(ex.Message);
            AppendConsole(I18n.T("ERROR de lote: {0}", ex.Message), ConsoleStatus.Error);
            _loggingService.LogError("Error aplicando un componente de optimizaciones", ex);
        }
        finally
        {
            // La apariencia del propio Windows (transparencia / modo claro-oscuro)
            // se re-aplica antes de releer, como en el lote normal.
            try { RefreshAppearanceAfterSystemChange(targets.Select(s => s.Id)); } catch { }

            // Releer la categoría entera: los badges, los contadores y la cabecera
            // tienen que reflejar lo que quedó en el sistema.
            _featureLoads.Remove(feature.Id);
            try { await LoadFeatureStatesAsync(feature); }
            catch (Exception ex) { try { _loggingService.LogWarning($"Optimizaciones: no se pudo releer {feature.Id}: {ex.Message}"); } catch { } }

            var summary = I18n.T("{0} aplicadas, {1} omitidas, {2} con error.", ok, skipped, failed);
            AppendConsole(I18n.T("Lote completado en {0}: {1}.",
                FormatDuration(_batchStopwatch.Elapsed), summary),
                failed > 0 ? ConsoleStatus.Error : ConsoleStatus.Applied);
            EndBatchUi(failed > 0);

            _busy = false;
            SetBatchBusy(false);
            UpdateFeatureBulkActions(feature);

            if (failures.Count > 0)
            {
                try
                {
                    _loggingService.LogWarning($"Optimizaciones: {failures.Count} ajustes de {feature.Id} fallaron: {string.Join("; ", failures)}");
                }
                catch { }
            }

            ShowNotification(
                I18n.T("Lote {0}: {1}", I18n.T(recommended ? "aplicado" : "restaurado"), summary),
                failed > 0 ? "error" : "success");
        }
    }

    private string? FindFeatureId(string settingId)
    {
        foreach (var feature in _catalog.Features)
            if (feature.Settings.Any(s => string.Equals(s.Id, settingId, StringComparison.OrdinalIgnoreCase)))
                return feature.Id;
        return null;
    }

    private async Task<bool> ConfirmSettingAsync(OptimizationSetting setting, string name)
    {
        if (XamlRoot is null) return false;

        var dialog = new ContentDialog
        {
            Title = name,
            PrimaryButtonText = I18n.T("Aplicar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            Content = new TextBlock
            {
                Text = _catalogText.SettingDescription(setting, I18n.Current),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // =====================================================================
    // CONSOLA
    // =====================================================================

    private enum ConsoleStatus
    {
        Running,
        Applied,
        Skipped,
        Error,
        Neutral,
        Command,
        Detail
    }

    private void AppendConsole(string message, ConsoleStatus status)
    {
        if (ConsolePanel == null || ConsoleText == null || ConsoleScroll == null) return;

        var wasCollapsed = ConsolePanel.Visibility == Visibility.Collapsed;
        ConsolePanel.Visibility = Visibility.Visible;
        if (wasCollapsed) ConsolePanel.StartBringIntoView();

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var (prefix, color, indent) = status switch
        {
            ConsoleStatus.Running => ("▶", AccentBrush, ""),
            ConsoleStatus.Applied => ("✓", SuccessBrush, ""),
            ConsoleStatus.Skipped => ("ℹ", WarningBrush, ""),
            ConsoleStatus.Error => ("✗", ErrorBrush, ""),
            ConsoleStatus.Command => ("›", MutedBrush, "    "),
            ConsoleStatus.Detail => ("│", MutedBrush, "    "),
            _ => ("·", MutedBrush, "")
        };

        if (ConsoleText.Inlines.Count > 0)
            ConsoleText.Inlines.Add(new Run { Text = Environment.NewLine });
        ConsoleText.Inlines.Add(new Run { Text = $"[{stamp}] ", Foreground = MutedBrush, FontSize = 11 });
        ConsoleText.Inlines.Add(new Run { Text = $"{indent}{prefix} {message}", Foreground = color });

        _lastConsoleActivity = DateTime.Now;
        UpdateBatchHeartbeat();

        const int maxInlineCount = 800;
        while (ConsoleText.Inlines.Count > maxInlineCount)
        {
            ConsoleText.Inlines.RemoveAt(0);
            if (ConsoleText.Inlines.Count > 0) ConsoleText.Inlines.RemoveAt(0);
        }

        ConsoleScroll.UpdateLayout();
        ConsoleScroll.ChangeView(null, double.MaxValue, null, true);
    }

    /// <summary>
    /// Cierra la consola sin borrar su contenido: si se vuelve a ejecutar algo, el
    /// primer AppendConsole la vuelve a abrir con la salida nueva.
    /// </summary>
    private void CloseConsoleButton_Click(object sender, RoutedEventArgs e) => HideConsole();

    private void HideConsole()
    {
        if (ConsolePanel == null) return;
        ConsolePanel.Visibility = Visibility.Collapsed;
    }

    private void ClearConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        ConsoleText?.Inlines.Clear();
        if (BatchHeartbeatText != null) BatchHeartbeatText.Visibility = Visibility.Collapsed;
    }

    private async void CopyConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConsoleText == null) return;
        var sb = new System.Text.StringBuilder();
        foreach (var inline in ConsoleText.Inlines)
            if (inline is Run run)
                sb.Append(run.Text);
        try
        {
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetText(sb.ToString());
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error copiando la consola", ex);
        }
    }

    // Estado del lote en curso: el spinner y el cronómetro prueban que la app
    // sigue viva mientras un ajuste tarda (un script de PowerShell puede tardar
    // minutos sin emitir una sola línea).
    private readonly DispatcherTimer _batchTickTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _batchStopwatch = new();
    private DateTime _lastConsoleActivity = DateTime.MinValue;
    private string _batchCurrentName = "";
    private int _batchCurrent = 0;
    private int _batchTotal = 0;

    private void UpdateBatchHeartbeat()
    {
        if (BatchHeartbeatText == null || BatchElapsedText == null || BatchStatusText == null) return;
        if (!_batchStopwatch.IsRunning) return;

        BatchElapsedText.Text = FormatDuration(_batchStopwatch.Elapsed);
        BatchElapsedText.Visibility = Visibility.Visible;

        var idle = _lastConsoleActivity == DateTime.MinValue
            ? TimeSpan.Zero
            : DateTime.Now - _lastConsoleActivity;
        BatchHeartbeatText.Text = I18n.T("Última actualización: hace {0}", FormatDuration(idle));
        BatchHeartbeatText.Visibility = Visibility.Visible;

        if (_batchTotal > 0 && !string.IsNullOrEmpty(_batchCurrentName))
            BatchStatusText.Text = I18n.T("Lote en curso ({0} de {1}) · {2}", _batchCurrent, _batchTotal, _batchStopwatch.Elapsed.ToString(@"m\:ss"));
    }

    private static string FormatDuration(TimeSpan d)
        => d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

    private void BeginBatchUi(int total)
    {
        _batchTotal = total;
        _batchCurrent = 0;
        _batchCurrentName = "";
        _lastConsoleActivity = DateTime.Now;
        _batchStopwatch.Restart();
        _batchTickTimer.Start();

        if (BatchProgressRing != null) { BatchProgressRing.IsActive = true; BatchProgressRing.Visibility = Visibility.Visible; }
        if (BatchProgressBar != null) { BatchProgressBar.Value = 0; BatchProgressBar.ShowError = false; BatchProgressBar.ShowPaused = false; BatchProgressBar.Visibility = Visibility.Visible; }
        if (BatchStatusText != null) { BatchStatusText.Text = I18n.T("En curso"); BatchStatusText.Visibility = Visibility.Visible; }
        if (BatchElapsedText != null) { BatchElapsedText.Text = "0:00"; BatchElapsedText.Visibility = Visibility.Visible; }
        if (BatchHeartbeatText != null) BatchHeartbeatText.Visibility = Visibility.Visible;
    }

    private void UpdateBatchUi(int done, string currentName)
    {
        if (BatchProgressBar != null && _batchTotal > 0)
            BatchProgressBar.Value = done * 100.0 / _batchTotal;
        if (BatchStatusText != null && _batchStopwatch.IsRunning)
            BatchStatusText.Text = _batchTotal > 0 && done < _batchTotal
                ? I18n.T("Lote en curso ({0} de {1}) · {2}", Math.Min(done + 1, _batchTotal), _batchTotal, _batchStopwatch.Elapsed.ToString(@"m\:ss"))
                : I18n.T("En curso");
        _batchCurrent = done;
        _batchCurrentName = currentName;
    }

    private void EndBatchUi(bool hasErrors)
    {
        _batchStopwatch.Stop();
        _batchTickTimer.Stop();
        _batchCurrentName = "";

        if (BatchProgressRing != null) { BatchProgressRing.IsActive = false; BatchProgressRing.Visibility = Visibility.Collapsed; }
        if (BatchProgressBar != null)
        {
            BatchProgressBar.Value = 100;
            BatchProgressBar.ShowError = hasErrors;
            BatchProgressBar.ShowPaused = false;
        }
        if (BatchStatusText != null) BatchStatusText.Visibility = Visibility.Collapsed;
        if (BatchElapsedText != null) BatchElapsedText.Visibility = Visibility.Collapsed;
        if (BatchHeartbeatText != null) BatchHeartbeatText.Visibility = Visibility.Collapsed;
    }

    private void SetBatchBusy(bool busy)
    {
        if (ApplyButton != null)
        {
            ApplyButton.IsEnabled = !busy && TotalPending > 0;
            ApplyButton.Content = busy ? I18n.T("Aplicando...") : I18n.T("Aplicar cambios");
        }
        if (DiscardButton != null) DiscardButton.IsEnabled = !busy;

        // Los atajos de componente comparten el mismo lote: mientras corre uno,
        // no pueden disparar otro encima.
        UpdateFeatureBulkActions();
    }

    // =====================================================================
    // NOTIFICACIONES
    // =====================================================================

    private void ShowNotification(string message, string type)
    {
        try
        {
            var severity = type switch
            {
                "success" => InfoBarSeverity.Success,
                "error" => InfoBarSeverity.Error,
                "warning" => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational
            };

            var notificationBar = new InfoBar
            {
                Message = message,
                IsOpen = true,
                Severity = severity,
                Margin = new Thickness(12),
                CornerRadius = new CornerRadius(8)
            };

            if (NotificationPanel == null) return;

            NotificationPanel.Children.Add(notificationBar);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (s, e) =>
            {
                notificationBar.IsOpen = false;
                NotificationPanel.Children.Remove(notificationBar);
                timer.Stop();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            try { _loggingService.LogError($"Error mostrando notificación: {ex.Message}", ex); } catch { }
        }
    }

    // =====================================================================
    // AYUDAS
    // =====================================================================

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackToHub();

    private static Microsoft.UI.Xaml.Media.FontFamily SymbolFont()
    {
        if (App.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var resource)
            && resource is Microsoft.UI.Xaml.Media.FontFamily ff)
        {
            return ff;
        }
        return new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons");
    }

    // =====================================================================
    // ÍCONOS
    // =====================================================================

    /// <summary>
    /// Glifo de un ajuste. El catálogo guarda el nombre del ícono de Winhance
    /// (Fluent, p. ej. "MonitorOff"): si hay traducción exacta se usa esa y, si
    /// no, se resuelve por palabra clave para que ningún ajuste quede con un
    /// ícono que no tenga nada que ver.
    /// </summary>
    private static string ResolveSettingGlyph(OptimizationSetting setting)
    {
        var name = setting.Icon ?? string.Empty;
        if (name.Length <= 1)
            return ResolveGroupGlyph(setting.Group);

        if (SettingGlyphOverrides.TryGetValue(name, out var exact))
            return exact;

        var n = name.ToLowerInvariant();
        foreach (var (keyword, glyph) in SettingGlyphRules)
        {
            if (n.Contains(keyword, StringComparison.Ordinal))
                return glyph;
        }

        return ResolveGroupGlyph(setting.Group);
    }

    /// <summary>Glifo del grupo de ajustes (la cabecera de cada sección).</summary>
    private static string ResolveGroupGlyph(string? group)
    {
        var g = (group ?? string.Empty).ToLowerInvariant();
        if (g.Length == 0)
            return "\uE713";

        foreach (var (keyword, glyph) in GroupGlyphRules)
        {
            if (g.Contains(keyword, StringComparison.Ordinal))
                return glyph;
        }

        return "\uE713";
    }

    // Traducciones exactas de los íconos más repetidos del catálogo.
    private static readonly Dictionary<string, string> SettingGlyphOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Monitor"] = "\uE7F4", ["MonitorOff"] = "\uE7F4", ["MonitorShimmer"] = "\uE7F4",
        ["MonitorDashboard"] = "\uE7F4", ["MonitorEye"] = "\uE7F4", ["MonitorLock"] = "\uE7F4",
        ["MonitorArrowDown"] = "\uE7F4", ["MonitorScreenshot"] = "\uE7F4", ["MonitorShare"] = "\uE7F4",
        ["MonitorSpeaker"] = "\uE7F4", ["MonitorMultiple"] = "\uE7F4",
        ["Mouse"] = "\uE962", ["MouseVariant"] = "\uE962", ["CursorClick"] = "\uE962",
        ["CursorDefault"] = "\uE962", ["CursorDefaultClickOutline"] = "\uE962",
        ["Keyboard"] = "\uE765", ["KeyboardOutline"] = "\uE765", ["KeyboardOff"] = "\uE765",
        ["Robot"] = "\uE945", ["RobotOff"] = "\uE945", ["BotSparkle"] = "\uE945",
        ["Harddisk"] = "\uEDA2", ["HarddiskPlus"] = "\uEDA2", ["HarddiskRemove"] = "\uEDA2",
        ["ShieldAccount"] = "\uEA18", ["ShieldLock"] = "\uEA18", ["ShieldCheck"] = "\uEA18",
        ["ShieldOff"] = "\uEA18", ["ShieldError"] = "\uEA18", ["ShieldSync"] = "\uEA18",
        ["Security"] = "\uEA18", ["Fingerprint"] = "\uEA18", ["LockClosedKey"] = "\uE72E",
        ["AccountLockOpen"] = "\uE72E", ["EyeLock"] = "\uE72E", ["DocumentLock"] = "\uE72E",
        ["CellphoneLock"] = "\uE72E",
        ["Broom"] = "\uE74D", ["TrashCanOutline"] = "\uE74D", ["EraserVariant"] = "\uE74D",
        ["Cancel"] = "\uE711", ["SelectOff"] = "\uE711", ["TabUnselected"] = "\uE711",
        ["Restart"] = "\uE72C", ["RestartOff"] = "\uE72C", ["RestartAlert"] = "\uE72C",
        ["BackupRestore"] = "\uE895", ["Cached"] = "\uE895", ["DatabaseSync"] = "\uE895",
        ["CloudSync"] = "\uE895", ["ArchiveSync"] = "\uE895", ["CloudOff"] = "\uE753",
        ["CloudQuestion"] = "\uE753", ["CloudSearch"] = "\uE753", ["CloudUpload"] = "\uE898",
        ["CloudUploadOutline"] = "\uE898",
        ["Download"] = "\uE896", ["PackageDown"] = "\uE896", ["MonitorArrowDown"] = "\uE896",
        ["TopSpeed"] = "\uE945", ["Speedometer"] = "\uE945", ["SpeedometerSlow"] = "\uE945",
        ["RocketLaunch"] = "\uE945", ["LightningBolt"] = "\uE945", ["FlashAlert"] = "\uE945",
        ["FlashAuto"] = "\uE945", ["FlashRedEye"] = "\uE945",
        ["LightbulbOn"] = "\uE82F", ["LightbulbOff"] = "\uE82F", ["LightbulbOnOutline"] = "\uE82F",
        ["LightbulbNight"] = "\uE82F", ["PenSparkle"] = "\uE70F", ["SearchSparkle"] = "\uE721",
        ["StarCog"] = "\uE713", ["StarBoxMultipleOutline"] = "\uE734",
        ["Bell"] = "\uE7E7", ["BellOff"] = "\uE7E7", ["BellCog"] = "\uE7E7",
        ["BellPlus"] = "\uE7E7", ["BellAlert"] = "\uE7E7", ["BellCheck"] = "\uE7E7",
        ["BellRingOutline"] = "\uE7E7",
        ["Apps"] = "\uE71D", ["Application"] = "\uE71D", ["ApplicationCog"] = "\uE713",
        ["WindowApps"] = "\uE7C4", ["WindowRestore"] = "\uE7C4", ["WindowAd"] = "\uE7C4",
        ["DockBottom"] = "\uE7C4", ["DockRight"] = "\uE7C4", ["DockWindow"] = "\uE7C4",
        ["Tab"] = "\uE7C4", ["ViewGrid"] = "\uE80A", ["ViewCompact"] = "\uE80A",
        ["ViewDashboard"] = "\uE80A",
        ["NetworkOutline"] = "\uE968", ["NetworkOff"] = "\uE968", ["NetworkOffOutline"] = "\uE968",
        ["Wifi"] = "\uE701", ["WifiOff"] = "\uE701", ["Router"] = "\uE968", ["LanDisconnect"] = "\uE968",
        ["Wan"] = "\uE968", ["Dns"] = "\uE968", ["Vpn"] = "\uE968", ["Connection"] = "\uE968",
        ["Memory"] = "\uE950", ["MemoryArrowDown"] = "\uE950", ["Cpu64Bit"] = "\uE950",
        ["Chip"] = "\uE950",
        ["Battery20"] = "\uE83F", ["BatteryAlert"] = "\uE83F", ["BatteryOutline"] = "\uE83F",
        ["BatteryCharging"] = "\uE83F",
        ["Sleep"] = "\uE7E8", ["PowerSleep"] = "\uE7E8", ["PowerSettings"] = "\uE7E8",
        ["BedClock"] = "\uE7E8", ["WeatherNight"] = "\uE7E8",
        ["Music"] = "\uE767", ["VolumeHigh"] = "\uE767", ["VolumeMedium"] = "\uE767",
        ["VolumeOff"] = "\uE74F", ["DesktopSpeaker"] = "\uE767", ["AccountTieVoice"] = "\uE720",
        ["AccountVoice"] = "\uE720", ["Microphone"] = "\uE720", ["MicrophoneOff"] = "\uE720",
        ["MicrophoneQuestion"] = "\uE720", ["MicrophoneMessage"] = "\uE720",
        ["Camera"] = "\uE722", ["CameraOff"] = "\uE722",
        ["Image"] = "\uE91B", ["ImageOff"] = "\uE91B", ["ImageMultiple"] = "\uE91B",
        ["ImageCircle"] = "\uE91B", ["ImageOutline"] = "\uE91B", ["ImageRemove"] = "\uE91B",
        ["ImageFilterHdr"] = "\uE91B", ["ImageStack"] = "\uE91B", ["ResizeImage"] = "\uE91B",
        ["DocumentImage"] = "\uE91B", ["PaletteOutline"] = "\uE790", ["Color"] = "\uE790",
        ["ContrastCircle"] = "\uE790", ["FormatPaint"] = "\uE790", ["BrushVariant"] = "\uE790",
        ["Video"] = "\uE714", ["Play"] = "\uE768", ["PlayBox"] = "\uE768", ["RecordRec"] = "\uE768",
        ["HighDefinition"] = "\uE714", ["Hdr"] = "\uE7F4",
        ["Usb"] = "\uE88E", ["UsbPort"] = "\uE88E",
        ["Printer"] = "\uE749", ["PrinterOff"] = "\uE749", ["Printer3d"] = "\uE749", ["Fax"] = "\uE749",
        ["XboxControllerError"] = "\uE7FC", ["MicrosoftXbox"] = "\uE7FC",
        ["OfficeBuilding"] = "\uE731", ["SchoolOutline"] = "\uE7BE", ["MedicalBag"] = "\uE95E",
        ["Wallet"] = "\uE8C7", ["SmartCard"] = "\uE8C7", ["CurrencySign"] = "\uE8C7",
        ["Cellphone"] = "\uE717", ["CellphoneLink"] = "\uE717", ["CellphoneWireless"] = "\uE717",
        ["PhoneClassic"] = "\uE717", ["PhoneAlert"] = "\uE717", ["Nfc"] = "\uE717",
        ["Download"] = "\uE896", ["StoreMicrosoft"] = "\uE719", ["StorefrontOutline"] = "\uE719",
        ["PackageVariant"] = "\uE7B8", ["PackageVariantPlus"] = "\uE7B8",
        ["PackageVariantRemove"] = "\uE7B8", ["PackageVariantClosedMinus"] = "\uE7B8",
        ["ArchiveSearch"] = "\uE7B8", ["ArchiveAlert"] = "\uE7BA",
        ["FolderZip"] = "\uE8B7", ["FolderOpen"] = "\uE8B7", ["FolderList"] = "\uE8B7",
        ["FolderMultiple"] = "\uE8B7", ["FolderSearch"] = "\uE721", ["FolderClockOutline"] = "\uE823",
        ["FolderAlert"] = "\uE7BA", ["FolderAccount"] = "\uE8B7", ["FolderTable"] = "\uE8B7",
        ["FileDocument"] = "\uE8A5", ["FileDocumentCheck"] = "\uE8A5", ["FileWord"] = "\uE8A5",
        ["FileExcel"] = "\uE8A5", ["FileEyeOutline"] = "\uE8A5", ["FileHidden"] = "\uE8A5",
        ["FileCloud"] = "\uE753", ["FileTree"] = "\uE8A5", ["FileTableBoxOutline"] = "\uE8A5",
        ["FileStarFourPointsOutline"] = "\uE734", ["DocumentQuestionMark"] = "\uE9CE",
        ["DocumentTextClock"] = "\uE823", ["DocumentEndnote"] = "\uE8A5", ["BookDefault"] = "\uE736",
        ["BookSync"] = "\uE895", ["BookmarkMultiple"] = "\uE8A4",
        ["TableEye"] = "\uE80A", ["TableStar"] = "\uE734", ["ChartBox"] = "\uE9D2",
        ["ChartLine"] = "\uE9D2", ["ChartBar"] = "\uE9D2", ["PulseSquare"] = "\uE9D2",
        ["TrendingUp"] = "\uE9D2", ["TrendingDown"] = "\uE9D2",
        ["Timeline"] = "\uE81C", ["TimelineAlert"] = "\uE7BA", ["History"] = "\uE81C",
        ["CalendarClock"] = "\uE823", ["CalendarMonth"] = "\uE787", ["CalendarWeekBegin"] = "\uE787",
        ["Alarm"] = "\uE823", ["TimerPause"] = "\uE823", ["ClockStart"] = "\uE823",
        ["ClockAlertOutline"] = "\uE823",
        ["ClipboardTextClockOutline"] = "\uE823", ["NotepadEdit"] = "\uE70F",
        ["NotebookEdit"] = "\uE70F", ["NotebookLightning"] = "\uE945",
        ["ScriptText"] = "\uE8A5", ["ScriptTextOutline"] = "\uE8A5", ["PowerShell"] = "\uE8A5",
        ["Powershell"] = "\uE8A5", ["Console"] = "\uE8A5", ["CodeBraces"] = "\uE943",
        ["CodeBracesBox"] = "\uE943", ["Api"] = "\uE943",
        ["MapMarker"] = "\uE707", ["MapMarkerOff"] = "\uE707", ["MapOutline"] = "\uE707",
        ["Navigation"] = "\uE707", ["TransitConnectionVariant"] = "\uE707",
        ["Print"] = "\uE749", ["FormatListBulleted"] = "\uE8FD", ["FormatSize"] = "\uE740",
        ["FormatListBulleted2"] = "\uE8FD", ["ListBox"] = "\uE8FD", ["FormDropdown"] = "\uE8FD",
        ["CheckboxMarked"] = "\uE73E", ["Checkbox"] = "\uE739", ["Select"] = "\uE73E",
        ["SelectionDrag"] = "\uE73E", ["BoxShadow"] = "\uE7F4", ["SlideTextCursor"] = "\uE7C4",
        ["TextShadow"] = "\uE8A5", ["TextBoxRemove"] = "\uE8A5", ["MessageText"] = "\uE715",
        ["MessageAlert"] = "\uE7BA", ["MessageBadge"] = "\uE715", ["ChatRemove"] = "\uE715",
        ["ChatMinus"] = "\uE715", ["HumanGreeting"] = "\uE77B", ["PersonFeedback"] = "\uE715",
        ["AccountCog"] = "\uE713", ["AccountSupervisor"] = "\uE77B", ["AccountOff"] = "\uE77B",
        ["PersonKey"] = "\uE72E", ["BriefcaseSearch"] = "\uE721", ["Advertisements"] = "\uE7BA",
        ["AdvertisementsOff"] = "\uE7BA", ["BullhornVariant"] = "\uE7E7", ["WebOff"] = "\uE774",
        ["Share"] = "\uE72D", ["ShareOff"] = "\uE72D", ["ShareVariant"] = "\uE72D",
        ["ShareAndroid"] = "\uE72D", ["LinkVariant"] = "\uE71B", ["DatabaseOff"] = "\uE8F1",
        ["DatabaseSearch"] = "\uE721", ["ArchiveSearch2"] = "\uE721", ["Bug"] = "\uE7BA",
        ["TestTube"] = "\uE95E", ["Stethoscope"] = "\uE95E", ["Radar"] = "\uE721",
        ["AutoFix"] = "\uE945", ["CreationOutline"] = "\uE70F", ["Animation"] = "\uE790",
        ["Resize"] = "\uE740", ["SizeXxs"] = "\uE740", ["RulerSquare"] = "\uE740",
        ["PanelTopExpand"] = "\uE740", ["ArrowCollapseDown"] = "\uE70D",
        ["ArrowTopLeftBoldOutline"] = "\uE72B", ["SwapHorizontal"] = "\uE8AB",
        ["Translate"] = "\uE774", ["Language"] = "\uE774", ["GlobeSearch"] = "\uE721",
        ["MicrosoftBing"] = "\uE774", ["Newspaper"] = "\uE8A5", ["Widgets"] = "\uE80A",
        ["Pin"] = "\uE718", ["Home"] = "\uE80F", ["BrainCircuit"] = "\uE945",
        ["CogOff"] = "\uE713", ["Cog"] = "\uE713", ["Tune"] = "\uE713", ["ProgressWrench"] = "\uE713",
        ["VectorPolylineRemove"] = "\uE7C4", ["AlphaCBox"] = "\uE8FD", ["LayoutRowTwoFocusTopSettings"] = "\uE7C4",
        ["DesktopClassic"] = "\uE7F4", ["DesktopSpeaker2"] = "\uE767", ["Laptop"] = "\uE770",
        ["VirtualReality"] = "\uE7F4", ["TelevisionAmbientLight"] = "\uE7F4",
        ["StringLightsOff"] = "\uE790", ["Fan"] = "\uE945", ["Speedometer2"] = "\uE945",
        ["ExpansionCard"] = "\uE950", ["DocumentTextClock2"] = "\uE823", ["Lightbulb"] = "\uE82F",
        ["CubeOffOutline"] = "\uE7B8", ["BranchCompare"] = "\uE950", ["Numeric"] = "\uE8FD",
        ["TrayFull"] = "\uE7C4", ["Opacity"] = "\uE790",

        // Correcciones de semántica: el ícono heredado no describía lo que hace
        // el ajuste (un rayo para “Plan de energía”, un tachito para “Mostrar la
        // Papelera en el escritorio”, una X para “Activar Recall”…).
        ["NotebookLightning"] = "\uE7E8", ["FlashAuto"] = "\uE7E8", ["FlashRedEye"] = "\uE7E8",
        ["LightningBolt"] = "\uE7E8", ["SelectOff"] = "\uE7E8", ["LightbulbNight"] = "\uE7E8",
        ["FlashAlert"] = "\uE7C4", ["TabUnselected"] = "\uE7C4", ["ArrowCollapseDown"] = "\uE7C4",
        ["Cancel"] = "\uE7F4", ["Select"] = "\uE790", ["SelectionDrag"] = "\uE790",
        ["DocumentQuestionMark"] = "\uE8A5", ["TrashCanOutline"] = "\uE7F4",
        ["OfficeBuilding"] = "\uE77B", ["SwapHorizontal"] = "\uE950", ["AlphaCBox"] = "\uEDA2",
        ["MedicalBag"] = "\uE713", ["MonitorSpeaker"] = "\uE767", ["Speedometer"] = "\uE950",
        ["SpeedometerSlow"] = "\uE950",
        // Asistentes de IA (Copilot, Cortana, AI de Office): globo de chat, no rayo.
        ["Robot"] = "\uE8BD", ["RobotOff"] = "\uE8BD", ["BotSparkle"] = "\uE8BD",
        ["BrainCircuit"] = "\uE8BD", ["AccountOff"] = "\uE8BD", ["SchoolOutline"] = "\uE8BD",
    };

    // Reglas por palabra clave, de la más específica a la más genérica.
    private static readonly (string Keyword, string Glyph)[] SettingGlyphRules =
    {
        ("microphone", "\uE720"), ("voice", "\uE720"), ("speech", "\uE720"),
        ("camera", "\uE722"), ("keyboard", "\uE765"), ("mouse", "\uE962"),
        ("cursor", "\uE962"), ("pointer", "\uE962"), ("printer", "\uE749"),
        ("usb", "\uE88E"), ("xbox", "\uE7FC"), ("controller", "\uE7FC"),
        ("wifi", "\uE701"), ("wireless", "\uE701"), ("bluetooth", "\uE702"),
        ("network", "\uE968"), ("lan", "\uE968"), ("dns", "\uE968"), ("vpn", "\uE968"),
        ("router", "\uE968"), ("wan", "\uE968"), ("internet", "\uE968"),
        ("cloud", "\uE753"), ("shield", "\uEA18"), ("defender", "\uEA18"),
        ("firewall", "\uEA18"), ("security", "\uEA18"), ("fingerprint", "\uEA18"),
        ("lock", "\uE72E"), ("private", "\uE72E"), ("privacy", "\uE72E"),
        ("battery", "\uE83F"), ("charging", "\uE83F"),
        ("power", "\uE7E8"), ("sleep", "\uE7E8"), ("energy", "\uE7E8"),
        ("fan", "\uE945"), ("thermal", "\uE945"),
        ("harddisk", "\uEDA2"), ("disk", "\uEDA2"), ("storage", "\uEDA2"),
        ("memory", "\uE950"), ("ram", "\uE950"), ("cpu", "\uE950"),
        ("processor", "\uE950"), ("chip", "\uE950"), ("gpu", "\uE950"),
        ("monitor", "\uE7F4"), ("display", "\uE7F4"), ("screen", "\uE7F4"),
        ("resolution", "\uE7F4"), ("hdr", "\uE7F4"), ("refresh", "\uE7F4"),
        ("graphics", "\uE714"), ("video", "\uE714"), ("render", "\uE714"),
        ("image", "\uE91B"), ("photo", "\uE91B"), ("picture", "\uE91B"),
        ("wallpaper", "\uE91B"), ("background", "\uE91B"),
        ("music", "\uE767"), ("audio", "\uE767"), ("sound", "\uE767"),
        ("speaker", "\uE767"), ("volume", "\uE767"),
        ("bell", "\uE7E7"), ("notification", "\uE7E7"), ("alert", "\uE7BA"),
        ("calendar", "\uE787"), ("schedule", "\uE823"), ("clock", "\uE823"),
        ("timer", "\uE823"), ("time", "\uE823"),
        ("history", "\uE81C"), ("timeline", "\uE81C"),
        ("search", "\uE721"), ("magnify", "\uE721"),
        ("download", "\uE896"), ("upload", "\uE898"),
        ("sync", "\uE895"), ("restore", "\uE895"), ("backup", "\uE895"),
        ("cached", "\uE895"), ("reset", "\uE895"),
        ("folder", "\uE8B7"), ("directory", "\uE8B7"), ("explorer", "\uE8B7"),
        ("file", "\uE8A5"), ("document", "\uE8A5"), ("notepad", "\uE8A5"),
        ("text", "\uE8A5"), ("script", "\uE8A5"), ("book", "\uE736"),
        ("database", "\uE8F1"), ("package", "\uE7B8"), ("store", "\uE719"),
        ("software", "\uE7B8"), ("install", "\uE7B8"), ("program", "\uE7B8"),
        ("uninstall", "\uE74D"), ("remove", "\uE74D"), ("delete", "\uE74D"),
        ("trash", "\uE74D"), ("clean", "\uE74D"), ("broom", "\uE74D"),
        ("window", "\uE7C4"), ("taskbar", "\uE7C4"), ("start", "\uE80F"),
        ("dock", "\uE7C4"), ("tab", "\uE7C4"), ("layout", "\uE7C4"),
        ("desktop", "\uE7F4"), ("widget", "\uE80A"),
        ("settings", "\uE713"), ("cog", "\uE713"), ("tune", "\uE713"),
        ("config", "\uE713"), ("policy", "\uE713"), ("wrench", "\uE713"),
        ("robot", "\uE945"), ("sparkle", "\uE945"), ("bot", "\uE945"),
        ("bug", "\uE7BA"), ("error", "\uE7BA"), ("problem", "\uE7BA"),
        ("warning", "\uE7BA"), ("question", "\uE9CE"),
        ("check", "\uE73E"), ("complete", "\uE73E"), ("star", "\uE734"),
        ("favorite", "\uE734"),
        ("speed", "\uE945"), ("rocket", "\uE945"), ("performance", "\uE945"),
        ("boost", "\uE945"), ("top", "\uE945"), ("fast", "\uE945"),
        ("trend", "\uE9D2"), ("chart", "\uE9D2"), ("graph", "\uE9D2"),
        ("eye", "\uE7B3"), ("view", "\uE7B3"), ("visible", "\uE7B3"),
        ("edit", "\uE70F"), ("pen", "\uE70F"), ("pencil", "\uE70F"),
        ("write", "\uE70F"), ("ink", "\uE70F"),
        ("share", "\uE72D"), ("link", "\uE71B"),
        ("language", "\uE774"), ("region", "\uE774"), ("globe", "\uE774"),
        ("translation", "\uE774"), ("locale", "\uE774"), ("web", "\uE774"),
        ("map", "\uE707"), ("location", "\uE707"), ("geo", "\uE707"),
        ("home", "\uE80F"), ("personal", "\uE80F"),
        ("user", "\uE77B"), ("account", "\uE77B"), ("person", "\uE77B"),
        ("profile", "\uE77B"), ("phone", "\uE717"), ("cell", "\uE717"),
        ("mail", "\uE715"), ("message", "\uE715"), ("chat", "\uE715"),
        ("feedback", "\uE715"), ("game", "\uE7FC"), ("xbox", "\uE7FC"),
        ("color", "\uE790"), ("theme", "\uE790"), ("appearance", "\uE790"),
        ("contrast", "\uE790"), ("animation", "\uE790"),
        ("app", "\uE71D"), ("select", "\uE73E"), ("list", "\uE8FD"),
        ("resize", "\uE740"), ("size", "\uE740"), ("expand", "\uE740"),
        ("print", "\uE749"), ("restart", "\uE72C"), ("reboot", "\uE72C"),
        ("cancel", "\uE711"), ("disable", "\uE711"),
    };

    // Reglas por palabra clave para la cabecera de cada grupo del catálogo.
    private static readonly (string Keyword, string Glyph)[] GroupGlyphRules =
    {
        ("security", "\uEA18"), ("firewall", "\uEA18"), ("lock", "\uE72E"),
        ("privacy", "\uE72E"), ("advertis", "\uE7BA"), ("content delivery", "\uE753"),
        ("speech", "\uE720"), ("voice", "\uE720"), ("ink", "\uE70F"),
        ("typing", "\uE765"), ("diagnostic", "\uE95E"), ("feedback", "\uE715"),
        ("activity history", "\uE81C"), ("history", "\uE81C"),
        ("search", "\uE721"), ("app permission", "\uE71D"), ("ai", "\uE945"),
        ("edge", "\uE774"), ("office", "\uE731"),
        ("display", "\uE7F4"), ("screen", "\uE7F4"), ("desktop background", "\uE91B"),
        ("hard disk", "\uEDA2"), ("disk", "\uEDA2"), ("storage", "\uEDA2"),
        ("internet explorer", "\uE774"), ("wireless", "\uE701"), ("network", "\uE968"),
        ("sleep", "\uE7E8"), ("usb", "\uE88E"), ("battery", "\uE83F"),
        ("power", "\uE7E8"), ("processor", "\uE950"), ("graphics", "\uE714"),
        ("gpu", "\uE714"), ("start menu", "\uE80F"), ("xbox", "\uE7FC"),
        ("scheduled task", "\uE823"), ("task", "\uE823"), ("service", "\uE713"),
        ("visual effect", "\uE790"), ("accessibility", "\uE776"),
        ("update", "\uE895"), ("delivery", "\uE896"), ("store", "\uE719"),
        ("notification", "\uE7E7"), ("sound", "\uE767"), ("audio", "\uE767"),
        ("desktop", "\uE7F4"), ("context menu", "\uE712"), ("menu", "\uE712"),
        ("device", "\uE772"), ("peripheral", "\uE772"), ("file", "\uE8B7"),
        ("folder", "\uE8B7"), ("navigation", "\uE8B7"), ("this pc", "\uE977"),
        ("regional", "\uE774"), ("language", "\uE774"),
        ("layout", "\uE7C4"), ("taskbar", "\uE7C4"), ("theme", "\uE790"),
        ("transparency", "\uE790"), ("general", "\uE713"),
    };

    /// <summary>Una herramienta del sistema que el hub muestra como card.</summary>
    private sealed record UtilityInfo(string Id, string Title, string Description, string Glyph, Type PageType);

    /// <summary>Un grupo de ajustes dentro de una categoría.</summary>
    private sealed class SettingGroup
    {
        public string Key { get; }
        public string Name { get; }
        public List<OptimizationSetting> Settings { get; } = new();

        public SettingGroup(string key, string name)
        {
            Key = key;
            Name = name;
        }
    }
}

/// <summary>
/// Páginas del exe que el hub monta como herramientas. Los componentes
/// descargados del Workshop no se usan acá: la app siempre tiene la copia
/// del exe, así que la herramienta funciona aunque el usuario no haya
/// instalado nada.
/// </summary>
internal static class UtilityPageTypes
{
    public static readonly Dictionary<string, Type> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tcp"] = typeof(TcpPage),
        ["debloat"] = typeof(DebloatPage),
        ["sistematweaks"] = typeof(DebloatPage),
        ["herramientas"] = typeof(HerramientasPage),
        ["panelwindows"] = typeof(PanelWindowsPage),
        ["reparacion"] = typeof(ReparacionPage),
        ["actualizaciones"] = typeof(ActualizacionesPage),
        ["limpieza"] = typeof(LimpiezaPage),
        ["arranque"] = typeof(AdministracionInicioPage),
    };
}
