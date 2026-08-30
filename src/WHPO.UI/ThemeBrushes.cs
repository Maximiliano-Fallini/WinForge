using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Pinceles de los ThemeDictionaries de la app resueltos con el tema EFECTIVO
/// (claro/oscuro) de la ventana.
///
/// NO usar App.Current.Resources["Clave"] para esto: esa búsqueda usa el contexto
/// del Application (tema del SISTEMA), así que cuando el sistema está en oscuro y la
/// app en claro, las cards creadas en code-behind quedaban con los colores oscuros
/// aunque el XAML con {ThemeResource} sí cambiaba.
/// </summary>
public static class ThemeBrushes
{
    /// <summary>Clave del diccionario de tema activo: "Light" o "Dark".</summary>
    public static string ActiveThemeKey()
    {
        // El tema efectivo de la ventana (root element, donde ThemeApplier setea
        // RequestedTheme) es la fuente de verdad: coincide con lo que ve el XAML.
        if (App.MainWindowInstance?.Content is FrameworkElement root)
            return root.ActualTheme == ElementTheme.Light ? "Light" : "Dark";

        // Fallback temprano (sin ventana todavía): resolver con el servicio de tema.
        var themeService = App.Services.GetRequiredService<IThemeService>();
        var theme = themeService.CurrentTheme == AppTheme.SystemDefault
            ? App.Services.GetRequiredService<IThemeApplier>().GetSystemTheme()
            : themeService.CurrentTheme;
        // Los temas con paleta propia se estructuran sobre un diccionario base:
        // el pincel correcto es el del tema EFECTIVO (PinkLight → Light, etc.).
        return ThemePalettes.BaseThemeFor(theme) == AppTheme.Light ? "Light" : "Dark";
    }

    /// <summary>
    /// Devuelve un pincel del diccionario de tema activo (p. ej. "CardBackgroundBrush",
    /// "MutedBrush", "ChartGridBrush", "SensorGroupFillBrush"). Si la clave no está en
    /// los ThemeDictionaries (p. ej. ErrorBrush/SuccessBrush/WarningBrush, iguales en
    /// ambos temas), se resuelve desde los recursos raíz de la app.
    /// </summary>
    public static SolidColorBrush Get(string key)
    {
        return Get(key, ActiveThemeKey());
    }

    /// <summary>
    /// Devuelve un pincel para el tema indicado. Es útil en una vista previa que
    /// cambia de tema localmente sin alterar la ventana principal.
    /// </summary>
    public static SolidColorBrush Get(string key, AppTheme theme)
    {
        var effectiveTheme = theme == AppTheme.SystemDefault
            ? App.Services.GetRequiredService<IThemeApplier>().GetSystemTheme()
            : theme;
        return Get(key, ThemePalettes.BaseThemeFor(effectiveTheme) == AppTheme.Light ? "Light" : "Dark");
    }

    private static SolidColorBrush Get(string key, string themeKey)
    {
        if (App.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dict)
            && dict is ResourceDictionary themeDict
            && themeDict.TryGetValue(key, out var value)
            && value is SolidColorBrush brush)
        {
            return brush;
        }

        return (SolidColorBrush)App.Current.Resources[key];
    }
}
