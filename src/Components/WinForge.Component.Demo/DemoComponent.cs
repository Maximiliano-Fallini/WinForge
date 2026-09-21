using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Components;
using WHPO.Core.Services;

namespace WinForge.Component.Demo;

/// <summary>
/// Componente de demostración del Workshop: valida el pipeline completo
/// (catálogo → descarga → SHA-256 → extracción → AssemblyLoadContext → UI).
///
/// Punto clave: la UI se construye 100% en código, SIN XAML compilado — los
/// assemblies cargados por reflexión no traen su propio XamlTypeInfo, y así el
/// componente no depende de nada que no sea este contrato + WinAppSDK (que ya
/// vive en la app).
/// </summary>
public sealed class DemoComponent : IWinForgeComponent
{
    public string Id => "demo";
    public string Name => "Demo del Workshop";
    public string Description => "Componente de ejemplo: prueba el flujo de instalación, actualización y desinstalación del Workshop.";
    public string IconGlyph => "\uE945";
    public ComponentCategory Category => ComponentCategory.Sistema;
    public string Version => "1.0.0";
    public string MinAppVersion => "0.0.0";
    public bool IsCore => false;

    public object CreatePage(IServiceProvider services)
    {
        var root = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(24) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new FontIcon { Glyph = IconGlyph, FontSize = 20 });
        header.Children.Add(new TextBlock { Text = "Demo del Workshop", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(header);

        panel.Children.Add(new TextBlock
        {
            Text = "Si estás viendo esta página, el pipeline completo del Workshop funcionó: el catálogo se leyó, el zip se descargó, el SHA-256 coincidió, el assembly se cargó en un AssemblyLoadContext aislado y este componente construyó su UI en código.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Background = Application.Current.Resources.TryGetValue("CardBackgroundBrush", out var cardBrush)
                ? (Brush)cardBrush
                : new SolidColorBrush(Windows.UI.Color.FromArgb(32, 128, 128, 128))
        };
        var info = new StackPanel { Spacing = 6 };
        info.Children.Add(MakeRow("Componente", $"{Name} ({Id})"));
        info.Children.Add(MakeRow("Versión del componente", Version));
        info.Children.Add(MakeRow("Versión de la app", $"v{AppUpdateService.CurrentVersion()}"));
        info.Children.Add(MakeRow("Cargado en", $"ALC \"{AppDomain.CurrentDomain.FriendlyName}\""));
        info.Children.Add(MakeRow("Hora de carga", DateTime.Now.ToString("HH:mm:ss")));
        card.Child = info;
        panel.Children.Add(card);

        panel.Children.Add(new TextBlock
        {
            Text = "Podés desinstalar este componente desde el Workshop cuando quieras: la pestaña desaparece del navbar al instante y el assembly se libera por completo al reiniciar la app.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        });

        root.Content = panel;
        return root;
    }

    private static StackPanel MakeRow(string label, string value) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Children =
        {
            new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MinWidth = 170, VerticalAlignment = VerticalAlignment.Top },
            new TextBlock { Text = value, FontSize = 12, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap }
        }
    };
}
