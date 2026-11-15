using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using Windows.Foundation;
using Windows.System;

namespace WHPO_UI.Controls;

/// <summary>Una opción de un ajuste: el valor que se puede elegir (una prioridad, un núcleo, un plan).</summary>
public sealed class PaletteOption
{
    public string Text = "";
    public bool IsChecked;
    public bool IsEnabled = true;
    public Action? Invoke;
}

/// <summary>
/// Un ajuste de la paleta: el título de la columna izquierda y cómo se construyen sus
/// valores en la derecha. Sin <see cref="BuildOptions"/> es una acción simple (Defender,
/// CFG, eliminar reglas): se ejecuta con <see cref="Invoke"/>.
/// <para>
/// <see cref="Scopes"/> agrega el selector "Actual / Siempre" arriba de los valores —
/// la parte de "flyout en columnas" del diseño: el ajuste se elige a la izquierda y el
/// alcance + el valor a la derecha, sin submenús anidados.
/// </para>
/// </summary>
public sealed class PaletteSection
{
    public string Title = "";
    public string Glyph = "\uE713";
    public string? Note;
    public bool IsDestructive;
    public bool IsEnabled = true;
    public bool CloseOnInvoke;

    /// <summary>Valores del ajuste, recalculados en cada refresco (leen la regla vigente).</summary>
    public Func<IReadOnlyList<PaletteOption>>? BuildOptions;

    /// <summary>Acción de una fila sin valores.</summary>
    public Action? Invoke;

    /// <summary>Resumen corto que se ve en la columna izquierda ("Alto", "6 núcleos").</summary>
    public Func<string>? Summary;

    /// <summary>Alcances del ajuste, p. ej. ["Actual", "Siempre"]. Null = sin selector.</summary>
    public string[]? Scopes;
    public Func<int>? GetScope;
    public Action<int>? SetScope;

    /// <summary>Se llama al entrar al ajuste (cargar planes de energía, leer Defender…).</summary>
    public Action<Action>? OnSelected;

    /// <summary>Si es &gt; 0, las opciones se dibujan en una grilla de ese ancho (afinidad).</summary>
    public int OptionsPerRow;
}

/// <summary>
/// Paleta de reglas: un flyout con la lista de ajustes a la izquierda y los valores del
/// ajuste elegido a la derecha. Reemplaza al árbol de MenuFlyout de tres niveles por una
/// sola pantalla: se navega con flechas y el alcance (Actual / Siempre) queda siempre a
/// la vista.
/// </summary>
public sealed class RulePalette : UserControl
{
    private const double PaletteWidth = 620;
    private const double LeftColumnWidth = 240;

    private readonly StackPanel _left;
    private readonly StackPanel _right;
    private readonly TextBlock _rightTitle;
    private readonly TextBlock _rightNote;
    private readonly StackPanel _rightBody;
    private readonly Border _scopeBar;
    private readonly StackPanel _scopePanel;
    private readonly TextBlock _status;
    private readonly TextBlock _empty;

    private List<PaletteSection> _sections = new();
    private PaletteSection? _selected;
    private int _scope;
    private Flyout? _flyout;
    private List<Button> _leftButtons = new();
    private List<Button> _optionButtons = new();

    public RulePalette()
    {
        IsTabStop = false;

        // ---------- cabecera ----------
        var iconTile = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(9),
            Background = ThemeBrushes.Get("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var icon = new FontIcon
        {
            Glyph = "\uE7FC",
            FontSize = 15,
            Foreground = ThemeBrushes.Get("AccentForegroundBrush")
        };
        iconTile.Child = icon;

        TitleText = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        SubtitleText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(TitleText);
        titles.Children.Add(SubtitleText);

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(7),
            VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = false
        };
        close.Click += (_, _) => Hide();

        var header = new Grid { ColumnSpacing = 10, Padding = new Thickness(14, 11, 10, 11) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titles, 1);
        Grid.SetColumn(close, 2);
        header.Children.Add(iconTile);
        header.Children.Add(titles);
        header.Children.Add(close);
        _headerIcon = icon;

        // ---------- columna izquierda ----------
        _left = new StackPanel { Spacing = 1, Padding = new Thickness(6, 5, 6, 6) };
        var leftHost = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = ThemeBrushes.Get("CardBorderBrush"),
            Child = _left
        };

        // ---------- columna derecha ----------
        _rightTitle = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold };
        _rightNote = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        _scopePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        _scopeBar = new Border
        {
            Background = ThemeBrushes.GetSurface("ChipBackgroundBrush"),
            BorderBrush = ThemeBrushes.Get("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
            Child = _scopePanel
        };

        _rightBody = new StackPanel { Spacing = 2 };

        _empty = new TextBlock
        {
            Text = I18n.T("Elegí un ajuste de la lista"),
            FontSize = 12.5,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 14, 2, 0),
            Visibility = Visibility.Collapsed
        };

        var rightStack = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(14, 12, 12, 10),
            Children = { _rightTitle, _scopeBar, _rightNote, _rightBody, _empty }
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LeftColumnWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftHost, 0);
        Grid.SetColumn(rightStack, 1);
        body.Children.Add(leftHost);
        body.Children.Add(rightStack);

        // ---------- pie ----------
        _status = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            Visibility = Visibility.Collapsed
        };
        var hints = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        hints.Children.Add(Hint("↑↓", I18n.T("mover")));
        hints.Children.Add(Hint("Enter", I18n.T("abrir")));
        hints.Children.Add(Hint("Esc", I18n.T("cerrar")));
        var footer = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(14, 8, 14, 9),
            BorderBrush = ThemeBrushes.Get("CardBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Children = { _status, hints }
        };

        // Alto automático: la paleta crece con la lista de ajustes y nunca scrollea.
        var root = new Grid { Width = PaletteWidth, MinHeight = 380, MaxHeight = 640 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(body, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(header);
        root.Children.Add(body);
        root.Children.Add(footer);

        var card = new Border
        {
            // Fondo OPACO de fábrica: ni vidrio ni alfa del ajuste de transparencia.
            // La paleta se lee sobre la biblioteca y cualquier transparencia mezclaba
            // el texto con las cards de atrás.
            Background = new SolidColorBrush(OpaqueCardColor()),
            BorderBrush = ThemeBrushes.Get("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = root
        };
        Content = card;
    }

    /// <summary>
    /// Color de card del tema, con alfa 255. Se pide a la DEFINICIÓN del tema y no al
    /// pincel vivo: con el desenfoque de paneles encendido la clave del diccionario guarda
    /// una imagen desenfocada (no un color) y el pincel live queda transparente.
    /// </summary>
    private static Windows.UI.Color OpaqueCardColor()
    {
        try
        {
            var themeService = App.Services.GetRequiredService<IThemeService>();
            var theme = themeService.CurrentTheme == AppTheme.SystemDefault
                ? App.Services.GetRequiredService<IThemeApplier>().GetSystemTheme()
                : themeService.CurrentTheme;
            if (ThemePalettes.TryGetFactoryColor(theme, "CardBackgroundBrush", out var color)
                || ThemePalettes.TryGetFactoryColor(ThemePalettes.BaseThemeFor(theme), "CardBackgroundBrush", out color))
                return Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);
        }
        catch { }

        return ThemeBrushes.ActiveThemeKey() == "Light"
            ? Windows.UI.Color.FromArgb(255, 0xF7, 0xF7, 0xFA)
            : Windows.UI.Color.FromArgb(255, 0x18, 0x18, 0x1E);
    }

    private readonly FontIcon _headerIcon;
    public TextBlock TitleText { get; }
    public TextBlock SubtitleText { get; }

    /// <summary>Abre la paleta anclada al elemento (o al punto, para el clic derecho).</summary>
    public void Show(FrameworkElement target, IEnumerable<PaletteSection> sections, Point? at = null)
    {
        _sections = sections.ToList();
        _selected = null;
        _scope = 0;

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        presenterStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        presenterStyle.Setters.Add(new Setter(Control.MinWidthProperty, 0d));
        presenterStyle.Setters.Add(new Setter(Control.MaxWidthProperty, double.PositiveInfinity));

        _flyout = new Flyout
        {
            Content = this,
            FlyoutPresenterStyle = presenterStyle,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
        };
        _flyout.Closed += (_, _) => _flyout = null;

        ShowEmpty();
        RenderLeft();
        if (at is Point p)
            _flyout.ShowAt(target, new FlyoutShowOptions { Position = p });
        else
            _flyout.ShowAt(target);

        // Sin buscador, el foco arranca en la lista para navegar con ↑↓.
        if (_leftButtons.Count > 0)
            _leftButtons[0].Focus(FocusState.Programmatic);
    }

    public void Hide() => _flyout?.Hide();

    /// <summary>Relee las reglas y repinta resúmenes y valores (después de aplicar o cargar planes).</summary>
    public void Refresh()
    {
        RenderLeft();
        if (_selected != null) RenderRight(_selected);
    }

    /// <summary>Mensaje de resultado visible dentro de la paleta (el StatusText de la página queda detrás).</summary>
    public void SetStatus(string text, bool warning = false)
    {
        _status.Text = text;
        _status.Foreground = warning ? Feedback.WarningBrush : ThemeBrushes.Get("SecondaryTextBrush");
        _status.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ===== columna izquierda =====

    private void RenderLeft()
    {
        _left.Children.Clear();
        _leftButtons.Clear();

        foreach (var section in _sections)
        {
            var row = BuildLeftRow(section);
            _leftButtons.Add(row);
            _left.Children.Add(row);
        }

        // Si el seleccionado ya no está en la lista, se cae al primero.
        if (_selected != null && !_sections.Contains(_selected))
            _selected = null;
        if (_selected == null && _sections.Count > 0)
            Select(_sections[0], focusOptions: false);
        PaintLeftSelection();
    }

    private Button BuildLeftRow(PaletteSection section)
    {
        var glyph = new FontIcon
        {
            Glyph = section.Glyph,
            FontSize = 13,
            Foreground = section.IsDestructive ? Feedback.ErrorBrush : ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var title = new TextBlock
        {
            Text = section.Title,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // Deshabilitado se atenúa; habilitado hereda el color de texto del tema (el del botón).
        if (!section.IsEnabled) title.Foreground = ThemeBrushes.Get("SecondaryTextBrush");
        var summary = new TextBlock
        {
            Text = section.Summary?.Invoke() ?? "",
            FontSize = 10.5,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var texts = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        if (!string.IsNullOrEmpty(summary.Text)) texts.Children.Add(summary);

        var chevron = new FontIcon
        {
            Glyph = section.BuildOptions != null ? "\uE76C" : "",
            FontSize = 10,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var grid = new Grid { ColumnSpacing = 9 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(glyph);
        grid.Children.Add(texts);
        grid.Children.Add(chevron);

        var button = new Button
        {
            Content = grid,
            Tag = section,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(9, 5, 7, 5),
            CornerRadius = new CornerRadius(8),
            IsEnabled = section.IsEnabled
        };
        button.KeyDown += OnLeftKeyDown;
        button.Click += (_, _) =>
        {
            // Las acciones con estado (Defender, CFG) se seleccionan para consultar su
            // estado; las acciones simples se ejecutan directo.
            if (section.BuildOptions == null && section.OnSelected == null)
            {
                section.Invoke?.Invoke();
                if (section.CloseOnInvoke) Hide();
                RenderLeft();
                return;
            }
            Select(section, focusOptions: true);
        };
        return button;
    }

    private void Select(PaletteSection section, bool focusOptions)
    {
        _selected = section;
        _scope = section.GetScope?.Invoke() ?? 0;
        section.OnSelected?.Invoke(Refresh);
        PaintLeftSelection();
        RenderRight(section);
        if (focusOptions && _optionButtons.Count > 0)
            _optionButtons[0].Focus(FocusState.Programmatic);
    }

    private void PaintLeftSelection()
    {
        var accent = ThemeBrushes.Get("AccentBrush").Color;
        foreach (var button in _leftButtons)
        {
            bool active = ReferenceEquals(button.Tag, _selected);
            button.Background = active
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(28, accent.R, accent.G, accent.B))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    // ===== columna derecha =====

    private void ShowEmpty()
    {
        _rightTitle.Text = "";
        _scopeBar.Visibility = Visibility.Collapsed;
        _rightNote.Visibility = Visibility.Collapsed;
        _rightBody.Children.Clear();
        _optionButtons.Clear();
        _empty.Visibility = Visibility.Visible;
    }

    private void RenderRight(PaletteSection section)
    {
        _rightTitle.Text = section.Title;
        _rightNote.Text = section.Note ?? "";
        _rightNote.Visibility = string.IsNullOrEmpty(section.Note) ? Visibility.Collapsed : Visibility.Visible;

        // Selector de alcance: "Actual" (solo esta sesión) / "Siempre" (guardado).
        _scopePanel.Children.Clear();
        if (section.Scopes is { Length: > 1 })
        {
            _scopeBar.Visibility = Visibility.Visible;
            var accent = ThemeBrushes.Get("AccentBrush").Color;
            for (int i = 0; i < section.Scopes.Length; i++)
            {
                int idx = i;
                bool active = idx == _scope;
                var button = new Button
                {
                    Content = section.Scopes[idx],
                    FontSize = 11.5,
                    Padding = new Thickness(10, 3, 10, 3),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(0),
                    Background = active
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(34, accent.R, accent.G, accent.B))
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Foreground = active ? ThemeBrushes.Get("AccentBrush") : ThemeBrushes.Get("SecondaryTextBrush")
                };
                button.Click += (_, _) =>
                {
                    _scope = idx;
                    section.SetScope?.Invoke(idx);
                    RenderRight(section);
                };
                _scopePanel.Children.Add(button);
            }
        }
        else
        {
            _scopeBar.Visibility = Visibility.Collapsed;
        }

        _rightBody.Children.Clear();
        _optionButtons.Clear();
        _empty.Visibility = Visibility.Collapsed;

        if (section.BuildOptions == null)
        {
            var action = new Button
            {
                // Para Defender/CFG el resumen es la acción concreta ("Excluir…").
                Content = section.Summary?.Invoke() is { Length: > 0 } summary ? summary : section.Title,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(7),
                FontSize = 12.5
            };
            action.Click += (_, _) =>
            {
                section.Invoke?.Invoke();
                if (section.CloseOnInvoke) Hide();
                else Refresh();
            };
            _rightBody.Children.Add(action);
            return;
        }

        var options = section.BuildOptions();
        if (section.OptionsPerRow > 1)
            _rightBody.Children.Add(BuildOptionGrid(options, section));
        else
            foreach (var option in options)
                _rightBody.Children.Add(BuildOptionRow(option, section));
    }

    /// <summary>
    /// Opciones en grilla compacta (afinidad): todos los núcleos a la vista, sin scroll.
    /// </summary>
    private Grid BuildOptionGrid(IReadOnlyList<PaletteOption> options, PaletteSection section)
    {
        int cols = Math.Max(1, section.OptionsPerRow);
        int rows = (options.Count + cols - 1) / cols;
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var accent = ThemeBrushes.Get("AccentBrush").Color;
            var button = new Button
            {
                // Número solo: en grilla de 8 columnas "Núcleo 32" no entra.
                Content = (i + 1).ToString(),
                FontSize = 12,
                Padding = new Thickness(0, 5, 0, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = option.IsChecked
                    ? ThemeBrushes.Get("AccentBrush")
                    : ThemeBrushes.Get("CardBorderBrush"),
                Background = option.IsChecked
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(38, accent.R, accent.G, accent.B))
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                IsEnabled = option.IsEnabled
            };
            ToolTipService.SetToolTip(button, option.Text);
            button.Click += (_, _) =>
            {
                option.Invoke?.Invoke();
                RenderLeft();
                RenderRight(section);
            };
            Grid.SetColumn(button, i % cols);
            Grid.SetRow(button, i / cols);
            grid.Children.Add(button);
        }
        return grid;
    }

    private Button BuildOptionRow(PaletteOption option, PaletteSection section)
    {
        var check = new FontIcon
        {
            Glyph = option.IsChecked ? "\uE73E" : "",
            FontSize = 12,
            Foreground = ThemeBrushes.Get("AccentBrush"),
            Width = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        var text = new TextBlock
        {
            Text = option.Text,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(check);
        grid.Children.Add(text);

        var accent = ThemeBrushes.Get("AccentBrush").Color;
        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = option.IsChecked
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(24, accent.R, accent.G, accent.B))
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(7),
            IsEnabled = option.IsEnabled
        };
        button.Click += (_, _) =>
        {
            option.Invoke?.Invoke();
            if (section.CloseOnInvoke)
            {
                Hide();
                return;
            }
            RenderLeft();
            RenderRight(section);
        };
        return button;
    }

    // ===== teclado =====

    private void OnLeftKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                Hide();
                break;
            case VirtualKey.Down:
            case VirtualKey.Up:
            {
                if (_leftButtons.Count == 0) return;
                e.Handled = true;
                int current = _selected == null ? -1 : _leftButtons.FindIndex(b => ReferenceEquals(b.Tag, _selected));
                int step = e.Key == VirtualKey.Down ? 1 : -1;
                int next = current < 0 ? (step > 0 ? 0 : _leftButtons.Count - 1)
                    : (current + step + _leftButtons.Count) % _leftButtons.Count;
                if (_leftButtons[next].Tag is PaletteSection s)
                {
                    Select(s, focusOptions: false);
                    _leftButtons[next].Focus(FocusState.Programmatic);
                }
                break;
            }
            case VirtualKey.Enter:
            {
                // En una acción simple el botón ya dispara su Click con Enter.
                if (_selected?.BuildOptions == null || _optionButtons.Count == 0) return;
                e.Handled = true;
                _optionButtons[0].Focus(FocusState.Programmatic);
                break;
            }
        }
    }

    private static UIElement Hint(string key, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        panel.Children.Add(new Border
        {
            Background = ThemeBrushes.GetSurface("ChipBackgroundBrush"),
            BorderBrush = ThemeBrushes.Get("CardBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Child = new TextBlock
            {
                Text = key,
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = ThemeBrushes.Get("SecondaryTextBrush")
            }
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }
}
