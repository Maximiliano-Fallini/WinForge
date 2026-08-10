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

    // ---- Pinceles por tema (mismos colores que los ThemeResource de la app) ----
    private static readonly Dictionary<ElementTheme, SolidColorBrush> CardBrushes = new()
    {
        [ElementTheme.Dark] = new(Color.FromArgb(255, 0x26, 0x2A, 0x31)),
        [ElementTheme.Light] = new(Color.FromArgb(255, 0xFF, 0xFF, 0xFF))
    };
    private static readonly Dictionary<ElementTheme, SolidColorBrush> CardHoverBrushes = new()
    {
        [ElementTheme.Dark] = new(Color.FromArgb(255, 0x2E, 0x33, 0x3B)),
        [ElementTheme.Light] = new(Color.FromArgb(255, 0xF4, 0xF6, 0xF8))
    };
    private static readonly Dictionary<ElementTheme, SolidColorBrush> BorderBrushes = new()
    {
        [ElementTheme.Dark] = new(Color.FromArgb(255, 0x34, 0x3A, 0x45)),
        [ElementTheme.Light] = new(Color.FromArgb(255, 0xD8, 0xDD, 0xE3))
    };
    private static readonly Dictionary<ElementTheme, SolidColorBrush> MutedBrushes = new()
    {
        [ElementTheme.Dark] = new(Color.FromArgb(255, 0x8A, 0x94, 0xA6)),
        [ElementTheme.Light] = new(Color.FromArgb(255, 0x5C, 0x64, 0x70))
    };

    private static readonly SolidColorBrush AccentBrush = new(Color.FromArgb(255, 0x8A, 0xB4, 0xF8));
    private static readonly SolidColorBrush AccentTintBrush = new(Color.FromArgb(0x22, 0x8A, 0xB4, 0xF8));

    private bool IsLight => ActualTheme == ElementTheme.Light;
    private SolidColorBrush CardBrush => CardBrushes[IsLight ? ElementTheme.Light : ElementTheme.Dark];
    private SolidColorBrush CardHoverBrush => CardHoverBrushes[IsLight ? ElementTheme.Light : ElementTheme.Dark];
    private SolidColorBrush BorderBrush => BorderBrushes[IsLight ? ElementTheme.Light : ElementTheme.Dark];
    private SolidColorBrush MutedBrush => MutedBrushes[IsLight ? ElementTheme.Light : ElementTheme.Dark];

    public PanelWindowsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _winUtilService = App.Services.GetRequiredService<IWinUtilService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        Loaded += OnLoaded;

        // Al cambiar el tema, reconstruir las cards con los colores nuevos.
        ActualThemeChanged += (s, e) =>
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

        // Cuadrícula responsive de 3 columnas: cada fila es un Grid de estrellas,
        // así las cards ocupan todo el ancho y quedan del mismo alto por fila.
        const int columns = 3;
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
            Text = panel.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2
        };

        var descText = new TextBlock
        {
            Text = panel.Description,
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
            Content = content,
            Background = CardBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
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
