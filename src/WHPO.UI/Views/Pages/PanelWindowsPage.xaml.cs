using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

public sealed partial class PanelWindowsPage : Page
{
    private readonly IWinUtilService _winUtilService;
    private readonly ILoggingService _loggingService;
    private bool _dataLoaded;

    // ---- Glifo (Segoe MDL2 Assets) por panel; genérico si no está mapeado ----
    private static readonly Dictionary<string, string> PanelGlyphs = new()
    {
        ["computer"] = "\uE977",   // PC
        ["control"] = "\uE713",    // Engranaje (Configuración)
        ["mouse"] = "\uE962",      // Mouse
        ["network"] = "\uE968",    // Red
        ["power"] = "\uE7E8",      // Botón de encendido
        ["printer"] = "\uE749",    // Imprimir
        ["programs"] = "\uE7B8",   // Paquete (programas)
        ["region"] = "\uE774",     // Globo (región)
        ["security"] = "\uEA18",   // Escudo
        ["sound"] = "\uE767",      // Volumen (sonido)
        ["system"] = "\uE770",     // Sistema
        ["timedate"] = "\uE823",   // Reloj
        ["firewall"] = "\uEA18",   // Escudo
        ["restore"] = "\uE72C"     // Restaurar
    };

    private const string DefaultGlyph = "\uE770";

    // ---- Pinceles desde los recursos de tema de la app: al cambiar de variante la
    // app reinicia y estas propiedades resuelven los colores nuevos (las cards se
    // reconstruyen en ActualThemeChanged al cambiar claro/oscuro). ----
    private static SolidColorBrush CardBrush => ThemeBrushes.Get("CardBackgroundBrush");
    private static SolidColorBrush CardHoverBrush => ThemeBrushes.Get("CardHoverBrush");
    private static SolidColorBrush MutedBrush => ThemeBrushes.Get("SecondaryTextBrush");
    private static SolidColorBrush AccentBrush => ThemeBrushes.Get("AccentBrush");
    private static SolidColorBrush AccentTintBrush => ThemeBrushes.Get("AccentTintBrush");
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    public PanelWindowsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _winUtilService = App.Services.GetRequiredService<IWinUtilService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        Loaded += OnLoaded;

        // Al cambiar el tema o el idioma, reconstruir las cards con los colores/textos nuevos.
        ActualThemeChanged += (s, e) =>
        {
            if (_dataLoaded) BuildPanels();
        };
        I18n.LanguageChanged += () =>
        {
            if (_dataLoaded) BuildPanels();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_dataLoaded) return;
        try
        {
            BuildPanels();
            _dataLoaded = true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error cargando PanelWindowsPage: {ex}", ex);
        }
    }

    private void BuildPanels()
    {
        PanelsHost.Children.Clear();
        var panels = _winUtilService.GetPanels();

        // Cuadrícula de 2 columnas: cada fila es un Grid de estrellas, así las
        // cards ocupan todo el ancho y quedan del mismo alto por fila.
        const int columns = 2;
        for (int i = 0; i < panels.Count; i += columns)
        {
            var row = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 12) };
            for (int c = 0; c < columns; c++)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int c = 0; c < columns && i + c < panels.Count; c++)
            {
                var button = BuildPanelButton(panels[i + c]);
                Grid.SetColumn(button, c);
                row.Children.Add(button);
            }
            PanelsHost.Children.Add(row);
        }
    }

    private Button BuildPanelButton(WindowsPanelInfo panel)
    {
        var glyph = PanelGlyphs.TryGetValue(panel.Id, out var g) ? g : DefaultGlyph;

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 17,
            Foreground = AccentBrush
        };
        var iconBox = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = AccentTintBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon
        };

        var nameText = new TextBlock
        {
            Text = I18n.T(panel.Name),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        };

        var descText = new TextBlock
        {
            Text = I18n.T(panel.Description),
            FontSize = 11.5,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        };

        var textPanel = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(nameText);
        textPanel.Children.Add(descText);

        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(iconBox);
        Grid.SetColumn(textPanel, 1);
        content.Children.Add(textPanel);

        var button = new Button
        {
            // Sin reborde (mismo estilo de cards que Reparación): solo fondo de card.
            // BorderThickness 0 explícito: si se omite, el template por defecto del
            // Button dibuja su propio borde (ButtonBorderBrush) aunque el fondo sea de card.
            Content = content,
            Background = CardBrush,
            BorderBrush = TransparentBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0),
            MinHeight = 72
        };
        button.PointerEntered += (s, e) => button.Background = CardHoverBrush;
        button.PointerExited += (s, e) => button.Background = CardBrush;

        // Sin feedback en la UI: solo abre el panel (los errores van al log).
        button.Click += async (s, e) =>
        {
            var result = await _winUtilService.LaunchPanelAsync(panel.Id);
            if (!result.Success)
                _loggingService.LogWarning($"Panel {panel.Id}: {result.Output}");
        };

        return button;
    }
}
