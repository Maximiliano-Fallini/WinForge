using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Components;
using WHPO.Core.Services;
using WHPO_UI.Components;
using WHPO_UI.Controls;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Workshop: catálogo de componentes de WinForge en grilla de cards. Cada card:
/// ícono + badge de categoría, título, descripción, y footer con estado y acción
/// (core fijo / integrado con switch / instalado con actualizar+desinstalar /
/// disponible con instalar). La descarga es real desde GitHub (SHA-256 + ALC).
/// </summary>
public sealed partial class WorkshopPage : Page
{
    private static readonly ComponentCategory[] CategoryOrder =
    {
        ComponentCategory.Juego, ComponentCategory.Rendimiento, ComponentCategory.Latencia, ComponentCategory.Monitoreo, ComponentCategory.Sistema
    };

    // Ancho de cada card: calculado en UpdateCardWidth() para que la grilla muestre
    // SIEMPRE 3 columnas con el mismo ancho horizontal que las cards de la
    // Biblioteca de juegos (misma fórmula: Max(230, (w - 48 - 14) / 3), donde w es
    // el ancho equivalente del scroll — la Biblioteca resta 48 de su margin, que
    // en el Workshop lo aporta el margin del RootPanel).
    private double _cardWidth = 360;
    private const double ScrollBarReserve = 14;
    private const double GridColumns = 3;
    private const double CardSpacing = 12;

    // Raíces de las cards construidas: se re-medien en cada resize.
    private readonly List<Border> _cardRoots = new();

    private readonly ComponentRegistry _registry;
    private readonly ComponentCatalogService _catalogService;
    private readonly WHPO.Core.Services.Interfaces.ILoggingService _logging;

    private ComponentCatalog? _catalog;
    private bool _catalogFromCache;
    private DateTime? _catalogCachedAt;
    private bool _loaded;
    private CancellationTokenSource? _installCts;

    // Instalados actuales (id → record), refrescado en cada render.
    private Dictionary<string, InstalledComponentRecord> _installed = new(StringComparer.OrdinalIgnoreCase);

    public WorkshopPage()
    {
        InitializeComponent();
        _registry = App.Services.GetRequiredService<ComponentRegistry>();
        _catalogService = App.Services.GetRequiredService<ComponentCatalogService>();
        _logging = App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ILoggingService>();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        I18n.LanguageChanged += OnLanguageChanged;
        RefreshInstalledRecords();
        RebuildSections();

        if (!_loaded)
        {
            _loaded = true;
            _ = FetchCatalogAsync();
        }
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        I18n.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        RebuildSections();
    }

    private void RefreshInstalledRecords()
    {
        _installed = new Dictionary<string, InstalledComponentRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in _catalogService.GetInstalled())
            _installed[record.Id] = record;
    }

    /// <summary>True si un integrado de fábrica tiene entrada en el catálogo.</summary>
    private bool BuiltinHasCatalogEntry(string id)
        => _registry.IsBuiltin(id) && _catalog?.Components.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) == true;

    // =====================================================================
    // Catálogo
    // =====================================================================

    private async Task FetchCatalogAsync()
    {
        SetStatus(I18n.T("Cargando catálogo..."), false);
        var result = await _catalogService.FetchCatalogAsync();
        _catalog = result.Catalog;
        _catalogFromCache = result.FromCache;
        _catalogCachedAt = result.CachedAt;

        if (result.Catalog == null)
        {
            // 404 ≠ sin conexión: si el repo todavía no publica components.json, el
            // usuario tiene internet igual y el mensaje "sin conexión" confunde.
            if (result.Error?.Contains("404") == true)
                SetStatus(I18n.T("El catálogo de componentes todavía no fue publicado en el repo. Se muestran los componentes instalados."), true);
            else
                SetStatus(I18n.T("No se pudo cargar el catálogo de componentes (sin conexión). Se muestran los componentes instalados."), true);
        }
        else if (result.FromCache)
            SetStatus(I18n.T("Sin conexión: se muestra la copia guardada del catálogo."), true);
        else
            SetStatus(null, false);

        RebuildSections();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshInstalledRecords();
        _ = FetchCatalogAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildSections();

    private void SetStatus(string? text, bool warning)
    {
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Visibility = Visibility.Collapsed;
            return;
        }
        StatusText.Text = text;
        StatusText.Foreground = warning ? Feedback.WarningBrush : ThemeBrushes.Get("SecondaryTextBrush");
        StatusText.Visibility = Visibility.Visible;
    }

    // =====================================================================
    // Render de secciones (grilla de cards por categoría)
    // =====================================================================

    private void RebuildSections()
    {
        SectionsHost.Children.Clear();
        _cardRoots.Clear();

        var search = (SearchBox.Text ?? "").Trim();
        var cards = BuildCardModels();

        foreach (var category in CategoryOrder)
        {
            var inCategory = cards.Where(c => c.Category == category).ToList();
            if (inCategory.Count == 0) continue;

            var visible = new List<UIElement>();
            foreach (var card in inCategory)
            {
                var element = BuildCard(card, search);
                if (element != null) visible.Add(element);
            }
            if (visible.Count == 0) continue;

            var section = new StackPanel { Spacing = 12 };

            // Encabezado de sección: nombre de la categoría + cantidad.
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(MakeSectionHeader(I18n.T(CategoryName(category))));
            header.Children.Add(new TextBlock
            {
                Text = I18n.T("{0} componentes", visible.Count),
                FontSize = 11,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3)
            });
            section.Children.Add(header);

            // Grilla de cards: el WrapPanel envuelve a la fila siguiente.
            var grid = new WrapPanel { Spacing = CardSpacing };
            foreach (var element in visible)
                grid.Children.Add(element);
            section.Children.Add(grid);

            SectionsHost.Children.Add(section);
        }

        if (SectionsHost.Children.Count == 0)
        {
            SectionsHost.Children.Add(new TextBlock
            {
                Text = I18n.T("No hay componentes que coincidan con la búsqueda."),
                FontSize = 12,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush")
            });
        }

        // Ajusta el ancho de las cards al contenedor (3 columnas como la Biblioteca).
        UpdateCardWidth();
    }

    /// <summary>
    /// Mismo cálculo que la Biblioteca de juegos: 3 columnas exactas y cada card
    /// con el ancho que sale de repartir el ancho disponible en 3. El WrapPanel
    /// envuelve a la 4ª card, así que la grilla queda fija en 3 columnas.
    /// </summary>
    private void UpdateCardWidth()
    {
        // Ancho usable real: el ScrollViewer ocupa toda la página; se restan los
        // 48 del margin del RootPanel (24 por lado) y la reserva de la canaleta
        // del scrollbar (14). Las cards + el espaciado llenan ESE ancho justo,
        // así que el hueco derecho queda igual al margen izquierdo (simétrico).
        double w = WorkshopScroll.ActualWidth - 48 - ScrollBarReserve;
        if (w <= 0) return;
        double cardWidth = Math.Max(230, (w - 2 * CardSpacing) / GridColumns);
        _cardWidth = cardWidth;
        foreach (var root in _cardRoots)
            root.Width = cardWidth;
    }

    private void WorkshopScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardWidth();

    // =====================================================================
    // Helpers de estilo (compartidos por todas las cards)
    // =====================================================================

    private static TextBlock MakeSectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold
    };

    private const double SectionIconSize = 26;
    private const double CardIconSize = 34;
    private const double CardTitleSize = 18;
    private const double CardDescSize = 13.5;
    private const double CardDescMinHeight = 62;
    private const double BadgeFontSize = 11.5;
    private const double BadgeHPadding = 13;
    private const double BadgeVPadding = 4;
    private const double BadgeCornerRadius = 12;
    private const double ActionFontSize = 13.5;
    private const double FooterFontSize = 12.5;

    // Componentes marcados como "En desarrollo": la card muestra un badge amarillo
    // adicional junto al de categoría hasta que el componente salga de esa etapa.
    private static readonly HashSet<string> InDevelopmentIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "latencia",
        "overlay",
        "overclockusb"
    };

    // Fábrica de badges de la card: mismo formato para estado y categoría.
    private static Border MakeBadge(string text, SolidColorBrush tint, SolidColorBrush textColor) => new()
    {
        Background = new SolidColorBrush(tint.Color) { Opacity = 0.14 },
        CornerRadius = new CornerRadius(BadgeCornerRadius),
        Padding = new Thickness(BadgeHPadding, BadgeVPadding, BadgeHPadding, BadgeVPadding + 1),
        VerticalAlignment = VerticalAlignment.Top,
        HorizontalAlignment = HorizontalAlignment.Right,
        Child = new TextBlock
        {
            Text = text,
            FontSize = BadgeFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = textColor
        }
    };

    private static string CategoryName(ComponentCategory category) => category switch
    {
        ComponentCategory.Juego => "Juego",
        ComponentCategory.Rendimiento => "Rendimiento",
        ComponentCategory.Latencia => "Latencia",
        ComponentCategory.Monitoreo => "Monitoreo",
        _ => "Sistema"
    };

    private sealed class CardModel
    {
        public required string Id;
        public required string Name;
        public required string Description;
        public required string IconGlyph;
        public required ComponentCategory Category;
        public IWinForgeComponent? Instance;          // componente cargado (built-in o descargado)
        public ComponentCatalogEntry? Entry;          // entrada del catálogo (si existe)
        public InstalledComponentRecord? Installed;   // registro de instalación (descargados)
        public bool IsCore;
    }

    private List<CardModel> BuildCardModels()
    {
        var models = new Dictionary<string, CardModel>(StringComparer.OrdinalIgnoreCase);

        // 1) Componentes en el registro (integrados y descargados cargados). Los
        // core (Sistema, Red, Núcleos, Biblioteca, Workshop) NO se listan: son
        // parte de la app, no se pueden desinstalar ni ocultar.
        foreach (var comp in _registry.All)
        {
            if (comp.IsCore) continue;
            models[comp.Id] = new CardModel
            {
                Id = comp.Id,
                Name = comp.Name,
                Description = comp.Description,
                IconGlyph = comp.IconGlyph,
                Category = comp.Category,
                Instance = comp,
                Entry = null,
                IsCore = comp.IsCore
            };
        }

        // 2) Entradas del catálogo que no están instaladas (descargables).
        if (_catalog != null)
        {
            foreach (var entry in _catalog.Components)
            {
                if (models.ContainsKey(entry.Id)) continue;
                models[entry.Id] = new CardModel
                {
                    Id = entry.Id,
                    Name = entry.Name,
                    Description = entry.Description,
                    IconGlyph = Glyph(entry.Icon),
                    Category = ParseCategory(entry.Category),
                    Entry = entry
                };
            }
        }

        // 3) Registros de instalación para los descargados cargados.
        foreach (var model in models.Values)
            if (_installed.TryGetValue(model.Id, out var record))
                model.Installed = record;

        // 4) Componentes que el catálogo ofrece pero que NO cargaron (instalados
        // según modules.json): cards "Reinstalar" (dll roto/eliminado a mano).
        if (_catalog != null)
        {
            foreach (var entry in _catalog.Components)
            {
                if (models.ContainsKey(entry.Id)) continue;
                if (!_installed.TryGetValue(entry.Id, out var rec)) continue;
                models[entry.Id] = new CardModel
                {
                    Id = entry.Id,
                    Name = entry.Name,
                    Description = entry.Description,
                    IconGlyph = Glyph(entry.Icon),
                    Category = ParseCategory(entry.Category),
                    Entry = entry,
                    Installed = rec
                };
            }

            // 5) Fallback de integrados: si un integrado de fábrica tiene versión
            // en el catálogo pero no cargó la copia descargada del repo (por ejemplo
            // arranque sin internet con la copia corrupta), la card del Workshop
            // permite volver a instalarla; la pestaña sigue funcionando con la
            // copia del exe (nunca se borra, solo hace de fallback).
            foreach (var entry in _catalog.Components)
            {
                if (models.ContainsKey(entry.Id)) continue;
                if (!_registry.IsBuiltin(entry.Id)) continue;
                models[entry.Id] = new CardModel
                { 
                    Id = entry.Id,
                    Name = entry.Name,
                    Description = entry.Description,
                    IconGlyph = Glyph(entry.Icon),
                    Category = ParseCategory(entry.Category),
                    Entry = entry
                }; 
            }

            // 6) Instalados según modules.json cuyo componente NO cargó ni es
            // integrado: card "Reinstalar" (dll roto o carpeta borrada a mano).
            foreach (var kv in _installed)
            {
                if (models.ContainsKey(kv.Key)) continue;
                var brokenEntry = _catalog.Components.FirstOrDefault(e => string.Equals(e.Id, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (brokenEntry == null) continue; // sin entrada no hay nada que reinstalar
                models[kv.Key] = new CardModel
                {
                    Id = kv.Key,
                    Name = kv.Key,
                    Description = "El componente no se pudo cargar en esta sesión.",
                    IconGlyph = "\uE7BA",
                    Category = ComponentCategory.Sistema,
                    Entry = brokenEntry
                };
            }
        }

        return models.Values.ToList();
    }

    private static string Glyph(string glyph) =>
        string.IsNullOrWhiteSpace(glyph) ? "\uE7C3" : glyph;

    private static ComponentCategory ParseCategory(string category) => category?.ToLowerInvariant() switch
    {
        "juego" => ComponentCategory.Juego,
        "rendimiento" => ComponentCategory.Rendimiento,
        "latencia" => ComponentCategory.Latencia,
        "monitoreo" => ComponentCategory.Monitoreo,
        _ => ComponentCategory.Sistema
    };

    // =====================================================================
    // Modo desarrollo (builds adelantadas a la release)
    // =====================================================================

    /// <summary>
    /// True si ESTA build de la app está por delante de la última release
    /// publicada en GitHub (AppUpdateStatus.DevelopmentBuild), o si el setting
    /// "workshop.devMode" lo fuerza (para probar sin depender del chequeo).
    /// Los componentes marcados "inDevelopment" solo se pueden instalar en este modo.
    /// </summary>
    private static bool IsDevBuild()
    {
        var settings = App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ISettingsService>();
        if (settings.Get("workshop.devMode", false)) return true;
        var update = App.MainWindowInstance?.LatestUpdate;
        return update?.Status == AppUpdateStatus.DevelopmentBuild;
    }

    /// <summary>
    /// El componente es "en desarrollo" si el id está en la lista interna (built-ins
    /// en progreso) o la entrada del catálogo lo marca ("inDevelopment": true).
    /// </summary>
    private static bool IsInDevelopment(CardModel card)
        => InDevelopmentIds.Contains(card.Id) || (card.Entry?.InDevelopment ?? false);

    // =====================================================================
    // Card (grilla)
    // =====================================================================

    private UIElement? BuildCard(CardModel card, string search)
    {
        if (search.Length > 0
            && !(card.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                 || card.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
                 || card.Id.Contains(search, StringComparison.OrdinalIgnoreCase)))
            return null;

        var accent = ThemeBrushes.Get("AccentBrush");
        var transparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var root = new Border
        {
            Width = _cardWidth,
            // Sin reborde en reposo (mismo estilo que la Biblioteca), pero con
            // BorderThickness 1 y pincel transparente para que al pasar el mouse
            // aparezca el borde de acento sin mover el layout.
            Background = ThemeBrushes.Get("CardBackgroundBrush"),
            BorderBrush = transparentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16)
        };
        _cardRoots.Add(root);

        // Hover: borde azul de acento; al salir vuelve a transparente.
        root.PointerEntered += (s, e) => root.BorderBrush = accent;
        root.PointerExited += (s, e) => root.BorderBrush = transparentBrush;

        var panel = new StackPanel { Spacing = 10 };

        // ===== Header: ícono a la izquierda + badges de estado y categoría a la derecha =====
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new FontIcon
        {
            Glyph = card.IconGlyph,
            FontSize = CardIconSize,
            Foreground = accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        var badgeBg = new SolidColorBrush(accent.Color) { Opacity = 0.14 };
        // Badge de estado ("En desarrollo", solo para componentes marcados) en la
        // columna del medio y badge de categoría a la derecha.
        if (IsInDevelopment(card))
        {
            var devBadge = MakeBadge(I18n.T("En desarrollo"), accent, Feedback.WarningBrush);
            Grid.SetColumn(devBadge, 1);
            header.Children.Add(devBadge);
        }
        var badge = MakeBadge(I18n.T(CategoryName(card.Category)).ToUpperInvariant(), accent, accent);
        Grid.SetColumn(badge, 2);
        header.Children.Add(badge);
        panel.Children.Add(header);

        // ===== Título =====
        panel.Children.Add(new TextBlock
        {
            Text = I18n.T(card.Name),
            FontSize = CardTitleSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        // ===== Descripción (máximo 3 líneas, altura mínima uniforme) =====
        panel.Children.Add(new TextBlock
        {
            Text = I18n.T(card.Description),
            FontSize = CardDescSize,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 3,
            MinHeight = CardDescMinHeight
        });

        // ===== Separador =====
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = ThemeBrushes.Get("CardBorderBrush"),
            Margin = new Thickness(0, 2, 0, 0)
        });

        // ===== Footer: estado a la izquierda + acción a la derecha =====
        var footer = BuildFooter(card);
        panel.Children.Add(footer);

        root.Child = panel;
        return root;
    }

    private FrameworkElement BuildFooter(CardModel card)
    {
        var footer = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement left = new TextBlock
        {
            Text = "",
            FontSize = FooterFontSize,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 1) Core: incluido siempre, sin acciones.
        if (card.IsCore)
        {
            left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            ((StackPanel)left).Children.Add(new FontIcon
            {
                Glyph = "\uE73E", // CheckMark
                FontSize = 14,
                Foreground = Feedback.SuccessBrush
            });
            ((StackPanel)left).Children.Add(new TextBlock
            {
                Text = I18n.T("Incluido siempre"),
                FontSize = FooterFontSize,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        // 2) Descargado e instalado: versión + actualizar + desinstalar.
        else if (card.Instance != null && card.Installed != null)
        {
            left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            ((StackPanel)left).Children.Add(new TextBlock
            {
                Text = I18n.T("Instalado v{0}", card.Installed.Version),
                FontSize = FooterFontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Feedback.SuccessBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (card.Entry != null && ComponentCatalogService.CompareVersions(card.Entry.Version, card.Installed.Version) > 0)
            {
                var updateBtn = CreateActionButton(I18n.T("Actualizar"));
                // Componente en desarrollo: actualizar solo en builds de desarrollo.
                if (IsInDevelopment(card) && !IsDevBuild())
                    updateBtn.IsEnabled = false;
                else
                    updateBtn.Click += async (s, e) => await InstallAsync(card, updateBtn);
                right.Children.Add(updateBtn);
            }

            var uninstallBtn = CreateActionButton(I18n.T("Desinstalar"));
            uninstallBtn.Foreground = Feedback.ErrorBrush; // acción destructiva: en rojo
            uninstallBtn.Click += async (s, e) => await UninstallAsync(card, uninstallBtn);
            right.Children.Add(uninstallBtn);
        }
        // 3) Integrado (de fábrica, no core): desinstalable (la pestaña sale del
        // navbar hasta reinstalarlo desde acá) y, mientras está instalado, switch de
        // visibilidad en el navbar.
        else if (card.Instance != null)
        {
            var settings = App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ISettingsService>();
            // Desde la 0.3.0 los integrados no core nacen "no instalados": si nunca
            // se tocaron, la card muestra "Disponible" + "Instalar" (como un
            // descargable); "Desinstalado" queda solo para lo desinstalado explícito.
            bool everTouched = settings.Contains("builtin.removed." + card.Id);
            bool removed = settings.Get("builtin.removed." + card.Id, _registry.RequiresInstall(card.Id));
            if (removed)
            {
                bool gated = InDevelopmentIds.Contains(card.Id) && !IsDevBuild();
                bool neverInstalled = !everTouched;
                left = new TextBlock
                {
                    // Componente en desarrollo fuera de build dev: sin texto de
                    // estado, solo el botón deshabilitado (no se revela el motivo).
                    Text = gated ? "" : I18n.T(neverInstalled ? "Disponible" : "Desinstalado"),
                    FontSize = FooterFontSize,
                    Foreground = neverInstalled && !gated ? ThemeBrushes.Get("SecondaryTextBrush") : Feedback.WarningBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var reinstallBtn = CreateActionButton(I18n.T(neverInstalled ? "Instalar" : "Reinstalar"));
                if (gated) reinstallBtn.IsEnabled = false;
                else reinstallBtn.Click += (s, e) => ReinstallBuiltin(card);
                right.Children.Add(reinstallBtn);
            }
            else
            {
                left = new TextBlock
                {
                    Text = I18n.T("Instalado v{0}", card.Instance.Version),
                    FontSize = FooterFontSize,
                    Foreground = Feedback.SuccessBrush, // estado OK: en verde
                    VerticalAlignment = VerticalAlignment.Center
                };
                // La visibilidad de la pestaña se gestiona solo desde el menú ⋮ del
                // navbar ("Ocultar") y desde el switch general de Configuración;
                // en la card queda únicamente el botón Desinstalar.
                var uninstallBtn = CreateActionButton(I18n.T("Desinstalar"));
                uninstallBtn.Foreground = Feedback.ErrorBrush; // acción destructiva: en rojo
                uninstallBtn.Click += async (s, e) => await UninstallBuiltinAsync(card);
                right.Children.Add(uninstallBtn);

                // Canal de actualización INDIVIDUAL (0.1.0 por componente): si el
                // catálogo tiene una versión más nueva que el integrado, se instala
                // la copia del repo, que PISA la copia del exe en el registro. La
                // copia del exe NO se toca: queda como fallback offline.
                if (card.Entry != null
                    && ComponentCatalogService.CompareVersions(card.Entry.Version, card.Instance.Version) > 0)
                {
                    var updateBtn = CreateActionButton(I18n.T("Actualizar"));
                    if (IsInDevelopment(card) && !IsDevBuild())
                        updateBtn.IsEnabled = false;
                    else
                        updateBtn.Click += async (s, e) => await InstallAsync(card, updateBtn);
                    right.Children.Add(updateBtn);
                }
            }
        }
        // 4) Disponible para instalar (entrada del catálogo, no instalado).
        else if (card.Entry != null)
        {
            // "Reinstalar": estaba instalado según modules.json pero no cargó
            // (dll roto, carpeta borrada a mano) o es un integrado cuya copia
            // del repo no cargó en este arranque.
            bool isRepair = card.Installed != null || _registry.IsBuiltin(card.Id);
            if (card.Entry.SizeBytes > 0)
            {
                left = new TextBlock
                {
                Text = I18n.T("{0} KB", Math.Max(1, (int)Math.Round(card.Entry.SizeBytes / 1024.0))),
                FontSize = FooterFontSize,
                    Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var installBtn = CreateActionButton(I18n.T(isRepair ? "Reinstalar" : "Instalar"));
            if (!ComponentCatalogService.IsVersionCompatible(card.Entry.MinAppVersion))
            {
                installBtn.IsEnabled = false;
                right.Children.Add(new TextBlock
                {
                    Text = I18n.T("Requiere WinForge {0} o superior", card.Entry.MinAppVersion),
                    FontSize = FooterFontSize,
                    Foreground = Feedback.WarningBrush,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 170,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            // Componente "en desarrollo": solo instalable desde una build adelantada
            // a la release (modo desarrollo). En estable el botón queda deshabilitado,
            // sin texto explicativo.
            else if (IsInDevelopment(card) && !IsDevBuild())
            {
                installBtn.IsEnabled = false;
            }
            else
            {
                installBtn.Click += async (s, e) => await InstallAsync(card, installBtn);
            }
            right.Children.Add(installBtn);
        }

        Grid.SetColumn(left, 0);
        footer.Children.Add(left);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);
        return footer;
    }

    private static Button CreateActionButton(string text) => new()
    {
        Content = text,
        FontSize = ActionFontSize,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(16, 8, 16, 8),
        VerticalAlignment = VerticalAlignment.Center
    };

    // =====================================================================
    // Instalar / desinstalar
    // =====================================================================

    private async Task InstallAsync(CardModel card, Button button)
    {
        var entry = card.Entry;
        if (entry == null)
        {
            if (card.Installed == null) return;
            // Actualizar sin entry (no debería pasar): nada que hacer.
            return;
        }

        var owner = button.Parent as Panel;
        button.IsEnabled = false;

        // Estado de progreso: ring + texto dentro de la card.
        var ring = new ProgressRing { Width = 16, Height = 16, IsActive = true };
        var progressText = new TextBlock
        {
            Text = I18n.T("Descargando..."),
            FontSize = FooterFontSize,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush")
        };
        var busy = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        busy.Children.Add(ring);
        busy.Children.Add(progressText);

        int actionsIndex = owner == null ? -1 : owner.Children.IndexOf(button);
        if (owner != null && actionsIndex >= 0)
            owner.Children[actionsIndex] = busy;

        _installCts = new CancellationTokenSource();
        var progress = new Progress<ComponentProgress>(p =>
        {
            progressText.Text = p.Stage switch
            {
                ComponentStage.Downloading => I18n.T("Descargando... {0}%", p.Percent),
                ComponentStage.Verifying => I18n.T("Verificando integridad..."),
                ComponentStage.Extracting => I18n.T("Extrayendo..."),
                ComponentStage.Loading => I18n.T("Cargando componente..."),
                _ => I18n.T("Listo.")
            };
        });

        var outcome = await _catalogService.InstallAsync(entry, progress, _installCts.Token);

        if (owner != null && actionsIndex >= 0)
            owner.Children[actionsIndex] = button;

        if (outcome.Success && outcome.Component != null)
        {
            _registry.Register(outcome.Component); // dispara Changed → el navbar se reconcilia
            Feedback.Success(StatusText, I18n.T("Componente instalado: {0}", I18n.T(outcome.Component.Name)));
        }
        else
        {
            Feedback.Error(StatusText, I18n.T(outcome.Error ?? "No se pudo instalar el componente."));
        }

        button.IsEnabled = true;
        RebuildSections();
    }

    // =====================================================================
    // Desinstalar / reinstalar integrados (built-in no core)
    // =====================================================================

    /// <summary>
    /// Desinstala un componente integrado de fábrica: la pestaña sale del navbar
    /// ("builtin.removed.<id>" + "nav.<id>" = false) y la card pasa a mostrar
    /// "Desinstalado" con el botón "Reinstalar". No borra nada del exe (los
    /// integrados viven dentro de WinForge.exe), solo lo quita de la UI.
    /// </summary>
    private async Task UninstallBuiltinAsync(CardModel card)
    {
        if (XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Desinstalar componente"),
            Content = I18n.T("¿Desinstalar “{0}”? La pestaña desaparece del navbar. Podés volver a instalarla desde el Workshop cuando quieras.", I18n.T(card.Name)),
            PrimaryButtonText = I18n.T("Desinstalar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBuiltinRemoved(card.Id, removed: true);
        Feedback.Success(StatusText, I18n.T("Componente desinstalado: {0}", I18n.T(card.Name)));
        RebuildSections();
    }

    /// <summary>Vuelve a instalar un integrado: la pestaña reaparece en el navbar.</summary>
    private void ReinstallBuiltin(CardModel card)
    {
        try
        {
            SetBuiltinRemoved(card.Id, removed: false);
            Feedback.Success(StatusText, I18n.T("Componente instalado: {0}", I18n.T(card.Name)));
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Workshop: no se pudo reinstalar {card.Id}: {ex.Message}");
        }
        RebuildSections();
    }

    private static void SetBuiltinRemoved(string id, bool removed)
    {
        var settings = App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ISettingsService>();
        settings.Set("builtin.removed." + id, removed);
        // "nav.<id>" siempre acompaña: oculto al desinstalar, visible al reinstalar.
        settings.Set("nav." + id, !removed);
        settings.Save();
        App.MainWindowInstance?.ApplyNavigationVisibility();
    }

    private async Task UninstallAsync(CardModel card, Button button)
    {
        if (XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Desinstalar componente"),
            Content = I18n.T("¿Desinstalar “{0}”? La pestaña desaparece del navbar y el componente se libera por completo al reiniciar la app. Podés reinstalarlo desde acá cuando quieras.", I18n.T(card.Name)),
            PrimaryButtonText = I18n.T("Desinstalar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _catalogService.Uninstall(card.Id);
        _registry.Unregister(card.Id); // dispara Changed → el navbar se reconcilia
        RefreshInstalledRecords();
        Feedback.Success(StatusText, I18n.T("Componente desinstalado: {0}", I18n.T(card.Name)));
        RebuildSections();
    }
}
