using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Selector de temas de Configuración → Apariencia: la grilla de cards con miniatura que
/// reemplaza al desplegable. Cada card dibuja el tema de verdad —la foto de fondo con su velo,
/// el gradiente del tema o su color plano— con una maqueta mínima de la ventana (barra lateral,
/// barra de título, dos cards y el punto del acento), así el tema se elige mirándolo y no
/// leyendo su nombre en una lista.
///
/// Los temas se agrupan por TIPO DE FONDO, que es la diferencia que se ve de un vistazo:
///   · Entorno   → el tema trae una imagen de fondo (Themes\Backgrounds).
///   · Degradado → el tema dibuja su fondo con un gradiente propio y no tiene imagen.
///   · Planos    → el fondo es un color liso (incluye sistema, claro y oscuro).
/// El grupo se calcula de la DEFINICIÓN del tema (ver <see cref="ThemeDefinition"/>), no de una
/// lista escrita a mano: un tema nuevo aparece en su grupo solo, y un grupo sin temas no se
/// dibuja (así "Degradado" nace el día que exista un tema de fondo degradado).
///
/// Los COLORES de cada miniatura salen de la definición del tema
/// (<see cref="ThemePalettes.TryGetFactoryColor"/>), no de los pinceles del diccionario: el
/// diccionario tiene UNA paleta aplicada a la vez, así que pedirle los colores de los otros temas
/// devolvería el color del tema activo y las nueve cards se verían iguales. Con la definición,
/// cada card muestra su tema; los clásicos (sistema/claro/oscuro), que no tienen definición,
/// caen a la copia de fábrica de su tema base (Light/Dark).
///
/// El filtro Todos / Oscuro / Claro es por BASE del tema (ver <see cref="ThemePalettes.BaseThemeFor"/>):
/// filtra por lo LUMINOSO, que es la otra pregunta que se hace al elegir. "Usar sistema" queda
/// siempre visible: sigue al sistema, así que puede ser cualquiera de los dos.
/// </summary>
public static class ThemePickerUi
{
    // ===== Filtro =====
    public const string FilterAll = "all";
    public const string FilterDark = "dark";
    public const string FilterLight = "light";

    // ===== Medidas =====
    private const int Columns = 4;
    private const double CardWidth = 196;
    private const double PreviewHeight = 104;
    private const double CardSpacing = 14;
    private const double MockInset = 10;

    /// <summary>Orden de los temas en la grilla: primero los del sistema y después los que traen
    /// paleta propia (el mismo orden del catálogo, ver <see cref="ThemeCatalog"/>).</summary>
    private static readonly AppTheme[] Order =
    {
        AppTheme.SystemDefault,
        AppTheme.Light,
        AppTheme.Dark,
        AppTheme.PinkLight,
        AppTheme.BlueBlack,
        AppTheme.Tide,
        AppTheme.Aurora,
        AppTheme.Brasa,
        AppTheme.Twilight
    };

    /// <summary>Nombre visible del tema. Los clásicos no tienen definición: sus nombres son las
    /// claves de traducción de siempre (las mismas del desplegable que esto reemplaza).</summary>
    private static string NameOf(AppTheme theme) => theme switch
    {
        AppTheme.SystemDefault => I18n.T("Usar sistema"),
        AppTheme.Light => I18n.T("Claro"),
        AppTheme.Dark => I18n.T("Oscuro"),
        _ => I18n.T(ThemeCatalog.Find(theme)?.NameKey ?? theme.ToString())
    };

    /// <summary>
    /// Grupo del tema, en el orden en que se dibujan: 0 = Entorno (trae foto), 1 = Degradado (fondo
    /// con gradiente propio y sin foto: Crepúsculo), 2 = Planos (color liso). El tipo lo resuelve la
    /// DEFINICIÓN del tema (ver ThemeCatalog.KindOf), así que un tema nuevo cae solo en su grupo.
    /// </summary>
    private static int GroupOf(AppTheme theme) => ThemeCatalog.KindOf(theme) switch
    {
        ThemeCatalog.ThemeKind.Environment => 0,
        ThemeCatalog.ThemeKind.Gradient => 1,
        _ => 2
    };

    private static string GroupName(int group) => group switch
    {
        0 => I18n.T("Entorno"),
        1 => I18n.T("Degradado"),
        _ => I18n.T("Planos")
    };

    /// <summary>¿El tema pasa el filtro? Se filtra por base clara/oscura del tema.</summary>
    private static bool MatchesFilter(AppTheme theme, string filter)
    {
        // "Usar sistema" queda siempre: puede ser clara u oscura según Windows.
        if (filter == FilterAll || theme == AppTheme.SystemDefault) return true;
        bool dark = ThemePalettes.BaseThemeFor(theme) != AppTheme.Light;
        return filter == FilterDark ? dark : !dark;
    }

    /// <summary>Card dibujada, con lo necesario para filtrarla y marcarla sin rearmarla.</summary>
    private sealed class Card
    {
        public required AppTheme Theme { get; init; }
        public required Border Outer { get; init; }
        public required Border CheckBadge { get; init; }
        public required int Group { get; init; }
    }

    // Estado de la grilla armada (la página de Configuración queda en caché de navegación, así que
    // se arma una vez y después solo se filtra/marca; se rearma al cambiar de idioma).
    private static readonly List<Card> Cards = new();
    private static StackPanel? _host;
    private static StackPanel? _filterBar;
    private static Action<AppTheme>? _onSelect;
    private static string _filter = FilterAll;

    // =====================================================================
    // Armado
    // =====================================================================

    /// <summary>
    /// Arma la grilla y la barra de filtro. Se llama al cargar la página y de nuevo al cambiar de
    /// idioma (rearmar es barato: nueve cards y ningún estado que perder).
    /// </summary>
    public static void Build(StackPanel host, StackPanel filterBar, AppTheme current,
        Action<AppTheme> onSelect, Action<string>? onFilter = null)
    {
        _host = host;
        _filterBar = filterBar;
        _onSelect = onSelect;
        host.Children.Clear();
        Cards.Clear();

        for (int group = 0; group <= 2; group++)
        {
            var themes = Order.Where(t => GroupOf(t) == group).ToList();
            if (themes.Count == 0) continue;

            host.Children.Add(new TextBlock
            {
                Text = GroupName(group),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.8,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
                Tag = $"group:{group}"
            });

            var grid = new Grid { ColumnSpacing = CardSpacing, RowSpacing = CardSpacing, Tag = $"grid:{group}" };
            for (int column = 0; column < Columns; column++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int row = 0; row < (themes.Count + Columns - 1) / Columns; row++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int index = 0;
            foreach (var theme in themes)
            {
                var card = BuildCard(theme, onSelect, group);
                Grid.SetColumn(card.Outer, index % Columns);
                Grid.SetRow(card.Outer, index / Columns);
                grid.Children.Add(card.Outer);
                Cards.Add(card);
                index++;
            }

            host.Children.Add(grid);
        }

        BuildFilter(filterBar, onFilter);
        ApplyFilter(host, _filter);
        MarkSelected(current);

        PanelAppearance.Diag(
            $"selector de temas: {Cards.Count} cards ({Cards.Count(c => c.Group == 0)} de entorno, " +
            $"{Cards.Count(c => c.Group == 1)} de degradado, {Cards.Count(c => c.Group == 2)} planas); " +
            $"tema vigente {current}; filtro {_filter}");
    }

    /// <summary>
    /// Una card: la miniatura del tema (fondo + velo + maqueta) con el nombre debajo, borde de
    /// acento y tilde cuando está elegida. El clic elige el tema; el hover levanta la card, como en
    /// el asistente de primera configuración.
    /// </summary>
    private static Card BuildCard(AppTheme theme, Action<AppTheme> onSelect, int group)
    {
        var preview = new Border
        {
            Height = PreviewHeight,
            CornerRadius = new CornerRadius(8),
            Background = BackdropBrush(theme)
        };

        var layers = new Grid();
        // El velo del tema sobre la foto: es parte de su identidad (WindowWallpaperScrimBrush) y
        // sin él las miniaturas de Entorno se verían más claras que la app.
        if (ThemeCatalog.KindOf(theme) == ThemeCatalog.ThemeKind.Environment)
            layers.Children.Add(new Border { Background = Themed(theme, "WindowWallpaperScrimBrush") });
        layers.Children.Add(BuildMock(theme));
        preview.Child = layers;

        var checkBadge = new Border
        {
            Background = Themed(theme, "AccentBrush"),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(5, 3, 5, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 8, 8),
            Visibility = Visibility.Collapsed,
            Child = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 11,
                Foreground = ContrastOn(Themed(theme, "AccentBrush"))
            }
        };

        var previewHost = new Grid();
        previewHost.Children.Add(preview);
        previewHost.Children.Add(checkBadge);

        var outer = new Border
        {
            Width = CardWidth,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 6, 6, 8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };

        var content = new StackPanel();
        content.Children.Add(previewHost);
        content.Children.Add(new TextBlock
        {
            Text = NameOf(theme),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(2, 8, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrushes.Get("PrimaryTextBrush")
        });
        outer.Child = content;

        var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        outer.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        outer.RenderTransform = scale;
        outer.PointerEntered += (_, _) => { scale.ScaleX = 1.02; scale.ScaleY = 1.02; };
        outer.PointerExited += (_, _) => { scale.ScaleX = 1; scale.ScaleY = 1; };
        outer.Tapped += (_, _) => onSelect(theme);

        return new Card { Theme = theme, Outer = outer, CheckBadge = checkBadge, Group = group };
    }

    /// <summary>
    /// Pincel del fondo de la miniatura: la foto del tema (decodificada al ancho de la card, no a
    /// resolución completa), el gradiente del tema o su color plano. Si la foto no está en disco,
    /// cae al fondo del tema: la card nunca queda vacía.
    /// </summary>
    private static Brush BackdropBrush(AppTheme theme)
    {
        try
        {
            var file = ThemeCatalog.WallpaperFile(theme);
            if (file != null)
            {
                var path = System.IO.Path.Combine(ThemePaint.BackgroundsDirectory, file);
                if (System.IO.File.Exists(path))
                {
                    return new ImageBrush
                    {
                        ImageSource = new BitmapImage
                        {
                            UriSource = new Uri(path),
                            // Decodificación chica: la miniatura no necesita más y así cuatro
                            // fotos completas no cuestan memoria al abrir la página.
                            DecodePixelWidth = (int)(CardWidth * 2)
                        },
                        Stretch = Stretch.UniformToFill
                    };
                }
            }

            // Temas con gradiente propio: se reusa el pincel de la definición (acá es de solo
            // lectura, y sigue al tema si alguna vez cambia).
            var definition = ThemeCatalog.Find(theme);
            if (definition != null)
            {
                foreach (var entry in definition.Brushes)
                    if (entry.Key == "WindowBackdropBrush" && entry.Brush is Brush backdrop)
                        return backdrop;
            }
        }
        catch { }

        return Themed(theme, "AppBackgroundBrush");
    }

    /// <summary>
    /// La maqueta de la ventana: barra lateral, barra de título, dos cards y el punto del acento,
    /// con los colores del tema de la card. Va con margen, así no pelea con las esquinas
    /// redondeadas de la miniatura.
    /// </summary>
    private static FrameworkElement BuildMock(AppTheme theme)
    {
        var cardBrush = Themed(theme, "CardBackgroundBrush");
        var secondary = Themed(theme, "SecondaryTextBrush");
        var accent = Themed(theme, "AccentBrush");

        var sidebar = new Border
        {
            Background = Themed(theme, "NavigationViewDefaultPaneBackground"),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(MockInset, MockInset, 0, MockInset)
        };
        Grid.SetColumn(sidebar, 0);

        var lines = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(LineBar(secondary, 0.75, 62));
        lines.Children.Add(LineBar(secondary, 0.5, 40));

        var cardBody = new Grid();
        cardBody.Children.Add(lines);
        cardBody.Children.Add(new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = accent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        });

        var content = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(MockInset + 30, MockInset, MockInset, MockInset)
        };
        content.Children.Add(new Border
        {
            Height = 7,
            Width = 54,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(3),
            Background = Fade(secondary, 0.55)
        });
        content.Children.Add(new Border
        {
            Height = 34,
            CornerRadius = new CornerRadius(5),
            Background = cardBrush,
            Padding = new Thickness(8, 0, 8, 0),
            Child = cardBody
        });
        content.Children.Add(new Border
        {
            Height = 18,
            CornerRadius = new CornerRadius(5),
            Background = cardBrush
        });

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(sidebar);
        Grid.SetColumn(content, 1);
        root.Children.Add(content);
        return root;
    }

    /// <summary>Una "línea de texto" de la maqueta.</summary>
    private static Border LineBar(Brush source, double opacity, double width) => new()
    {
        Height = 4,
        Width = width,
        HorizontalAlignment = HorizontalAlignment.Left,
        CornerRadius = new CornerRadius(2),
        Background = Fade(source, opacity)
    };

    // =====================================================================
    // Colores
    // =====================================================================

    /// <summary>
    /// Pincel sólido de una clave para UN tema. Sale de la definición del tema (y, si la clave no
    /// está ahí, del color de fábrica de su tema base); si no hay color —tema sin definición y
    /// copia de fábrica todavía sin capturar— cae al pincel del tema activo, nunca a transparente:
    /// una miniatura sin color deja la card vacía y el texto invisible.
    /// </summary>
    private static SolidColorBrush Themed(AppTheme theme, string key)
    {
        Windows.UI.Color color;
        if (theme != AppTheme.SystemDefault
            && ThemePalettes.TryGetFactoryColor(theme, key, out color))
        {
            return new SolidColorBrush(color);
        }

        // Los clásicos: el color de fábrica del tema base (light/dark) según corresponda.
        var resolved = Resolve(theme);
        if (ThemePalettes.TryGetFactoryColor(resolved, key, out color))
            return new SolidColorBrush(color);

        var live = ThemeBrushes.Get(key) as SolidColorBrush;
        return new SolidColorBrush(live?.Color ?? Microsoft.UI.Colors.Transparent);
    }

    /// <summary>"Usar sistema" resuelto a lo que el sistema tiene puesto: la card de Auto muestra
    /// el fondo que la app va a usar de verdad.</summary>
    private static AppTheme Resolve(AppTheme theme)
    {
        if (theme != AppTheme.SystemDefault) return theme;
        try { return App.Services.GetRequiredService<IThemeApplier>().GetSystemTheme(); }
        catch { return AppTheme.Dark; }
    }

    /// <summary>Copia translúcida de un pincel, para las líneas y bordes de la maqueta.</summary>
    private static SolidColorBrush Fade(Brush source, double opacity)
    {
        var color = (source as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.White;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            (byte)Math.Clamp(color.A * opacity, 0, 255), color.R, color.G, color.B));
    }

    /// <summary>Texto legible (casi negro o blanco) sobre un color de fondo.</summary>
    private static SolidColorBrush ContrastOn(Brush background)
    {
        var color = (background as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.White;
        double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        return new SolidColorBrush(luminance > 0.55
            ? Windows.UI.Color.FromArgb(255, 0x10, 0x12, 0x16)
            : Microsoft.UI.Colors.White);
    }

    // =====================================================================
    // Filtro y selección
    // =====================================================================

    /// <summary>Aspecto de las pastillas: SIN el fondo gris del Button de WinUI (eso era el
    /// "box" feo) —fondo transparente y borde sutil del tema—, así se ven igual en claro
    /// y en oscuro. Se cachea por sesión: los pinceles del tema se resuelven una vez y
    /// el estado activo lo pinta Refresh encima (Background/Foreground/BorderBrush),
    /// igual que en las cards.</summary>
    private static Style? _pillStyle;

    private static Style PillStyle()
    {
        if (_pillStyle != null) return _pillStyle;
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))));
        style.Setters.Add(new Setter(Control.ForegroundProperty, ThemeBrushes.Get("SecondaryTextBrush")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Fade(ThemeBrushes.Get("SecondaryTextBrush"), 0.35)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 5, 14, 5)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(20)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        style.Setters.Add(new Setter(Control.FontWeightProperty, Microsoft.UI.Text.FontWeights.SemiBold));
        style.Setters.Add(new Setter(Control.MinWidthProperty, 0.0));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 0.0));
        _pillStyle = style;
        return style;
    }

    /// <summary>Barra de filtro: tres pastillas (Todos / Oscuro / Claro) con la activa en acento.
    /// Un solo estilo compartido: fondo transparente, borde sutil del tema y texto secundario;
    /// el estado activo se repinta en Refresh (fondo de acento + texto de contraste), igual
    /// que las cards de la grilla.</summary>
    private static void BuildFilter(StackPanel host, Action<string>? onFilter)
    {
        host.Children.Clear();
        var buttons = new List<(string Filter, Button Button)>();
        var pillStyle = PillStyle();

        void Refresh()
        {
            foreach (var (filter, button) in buttons)
            {
                if (button.Style != pillStyle) button.Style = pillStyle;
                bool active = filter == _filter;
                var accent = ThemeBrushes.Get("AccentBrush");
                button.Background = active ? accent : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                button.Foreground = active ? ContrastOn(accent) : ThemeBrushes.Get("SecondaryTextBrush");
                button.BorderBrush = active
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
                    : Fade(ThemeBrushes.Get("SecondaryTextBrush"), 0.35);
            }
        }

        foreach (var (filter, label) in new[]
                 {
                     (FilterAll, I18n.T("Todos")),
                     (FilterDark, I18n.T("Oscuro")),
                     (FilterLight, I18n.T("Claro"))
                 })
        {
            var button = new Button
            {
                Content = label,
                Style = pillStyle,
                FontSize = 12,
                Padding = new Thickness(14, 5, 14, 5),
                CornerRadius = new CornerRadius(20),
                BorderThickness = new Thickness(1),
                MinWidth = 0,
                MinHeight = 0,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            button.Click += (_, _) =>
            {
                _filter = filter;
                Refresh();
                if (_host != null) ApplyFilter(_host, filter);
                onFilter?.Invoke(filter);
            };
            host.Children.Add(button);
            buttons.Add((filter, button));
        }
        Refresh();
    }

    /// <summary>Muestra u oculta las cards según el filtro y esconde los grupos que quedan vacíos.</summary>
    public static void ApplyFilter(StackPanel host, string filter)
    {
        _filter = filter;
        foreach (var group in Cards.Select(c => c.Group).Distinct())
        {
            int visible = 0;
            foreach (var card in Cards.Where(c => c.Group == group))
            {
                bool show = MatchesFilter(card.Theme, filter);
                card.Outer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show) visible++;
            }

            foreach (var child in host.Children.OfType<FrameworkElement>())
            {
                var tag = child.Tag as string;
                if (tag == null || !tag.EndsWith($":{group}", StringComparison.Ordinal)) continue;
                child.Visibility = visible > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>Marca la card del tema vigente (borde y fondo de acento + tilde) y desmarca el resto.</summary>
    public static void MarkSelected(AppTheme theme)
    {
        foreach (var card in Cards)
        {
            bool selected = card.Theme == theme;
            card.Outer.BorderBrush = selected
                ? ThemeBrushes.Get("AccentBrush")
                : Fade(ThemeBrushes.Get("SecondaryTextBrush"), 0.25);
            card.Outer.BorderThickness = new Thickness(selected ? 2 : 1);
            card.Outer.Background = selected
                ? Fade(ThemeBrushes.Get("AccentBrush"), 0.12)
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            card.CheckBadge.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Rearma la grilla con el idioma vigente (la llama la página al cambiar de idioma).</summary>
    public static void Relabel(AppTheme current)
    {
        if (_host == null || _filterBar == null || _onSelect == null) return;
        Build(_host, _filterBar, current, _onSelect);
    }
}
