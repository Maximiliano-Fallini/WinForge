using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WHPO_UI.Controls;

internal static class TooltipStyles
{
    public static Button CreateInfoButton(object tooltipContent)
    {
        var icon = new FontIcon
        {
            Glyph = "\uE946",
            FontSize = 12,
            Foreground = ThemeBrushes.Get("MutedBrush")
        };

        var button = new Button
        {
            Content = icon,
            Width = 20,
            Height = 20,
            MinWidth = 20,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Un string suelto no envuelve dentro del ToolTip: el ContentPresenter lo dibuja sin
        // TextWrapping y el texto se corta al llegar al ancho máximo. Se envuelve acá para que
        // ningún tooltip de la app vuelva a quedar con texto sin ver (los que pasan TextBlock
        // o StackPanel ya traen su propio wrap y no se tocan).
        var content = tooltipContent is string text
            ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }
            : tooltipContent;

        ToolTipService.SetToolTip(button, new ToolTip
        {
            Content = content,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom,
            MaxWidth = 420,
            Padding = new Thickness(10, 7, 10, 7),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center
        });

        return button;
    }
}
