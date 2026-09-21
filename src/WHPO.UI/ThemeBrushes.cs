using System;
using System.Collections.Generic;
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
/// Cada clave devuelve una instancia LIVE compartida: al cambiar de tema se
/// muta su Color en sitio (los suscriptores de SolidColorBrush.Color y los
/// bindings/Foreground existentes repintan solos). Antes se devolvía el
/// pincel del diccionario de tema: la UI construida en code-behind (cards,
/// filas, badges) conservaba la referencia del tema VIEJO y quedaba pintada
/// con colores rancios hasta recrear la página.
///
/// NO usar App.Current.Resources["Clave"] para esto: esa búsqueda usa el
/// contexto del Application (tema del SISTEMA), así que cuando el sistema
/// está en oscuro y la app en claro, las cards creadas en code-behind quedaban
/// con los colores oscuros aunque el XAML con {ThemeResource} sí cambiaba.
/// </summary>
public static class ThemeBrushes
{
    // Pinceles live por clave, compartidos por toda la UI.
    private static readonly Dictionary<string, SolidColorBrush> _live = new(StringComparer.Ordinal);

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
    /// Devuelve el pincel live de la clave (p. ej. "CardBackgroundBrush",
    /// "MutedBrush", "AccentBrush"). La instancia se crea una vez y su Color se
    /// re-resuelve del tema activo en cada Refresh() — la UI que la usa repinta
    /// sola al cambiar de tema.
    /// </summary>
    public static SolidColorBrush Get(string key)
    {
        if (_live.TryGetValue(key, out var brush))
            return brush;

        var source = Resolve(key, ActiveThemeKey());
        var live = new SolidColorBrush(source?.Color ?? Microsoft.UI.Colors.Transparent);
        _live[key] = live;
        return live;
    }

    /// <summary>
    /// Pincel puntual para un tema dado (vista previa del Onboarding): crea una
    /// instancia NUEVA con el color del tema pedido, sin tocar los pinceles live.
    /// </summary>
    public static SolidColorBrush Get(string key, AppTheme theme)
    {
        var effectiveTheme = theme == AppTheme.SystemDefault
            ? App.Services.GetRequiredService<IThemeApplier>().GetSystemTheme()
            : theme;
        return new SolidColorBrush(
            Resolve(key, ThemePalettes.BaseThemeFor(effectiveTheme) == AppTheme.Light ? "Light" : "Dark")?.Color
                ?? Microsoft.UI.Colors.Transparent);
    }

    /// <summary>
    /// Re-resuelve el Color de TODOS los pinceles live con el tema activo.
    /// La mutación de Color dispara el repaint de toda la UI que los referencia.
    /// La llama ThemeApplier al final de cada ApplyTheme con la clave del tema
    /// que ACABA de aplicar: usar el parámetro evita leer root.ActualTheme,
    /// que puede tardar un ciclo de layout en reflejar el RequestedTheme nuevo.
    /// </summary>
    public static void Refresh(string? themeKey = null)
    {
        themeKey ??= ActiveThemeKey();
        foreach (var kv in _live)
        {
            var source = Resolve(kv.Key, themeKey);
            if (source != null)
            {
                kv.Value.Opacity = 1;
                kv.Value.Color = source.Color;
            }
            else
            {
                // La clave ya no existe en este tema: ocultar el trazo para no
                // dejar colores rancios de un tema anterior.
                kv.Value.Opacity = 0;
            }
        }
    }

    /// <summary>
    /// Resuelve el pincel fuente de la clave en el diccionario de tema indicado;
    /// si la clave no está ahí (p. ej. ErrorBrush/SuccessBrush/WarningBrush,
    /// iguales en ambos temas), cae a los recursos raíz de la app.
    /// </summary>
    private static SolidColorBrush? Resolve(string key, string themeKey)
    {
        try
        {
            if (App.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dict)
                && dict is ResourceDictionary themeDict
                && themeDict.TryGetValue(key, out var value)
                && value is SolidColorBrush themeBrush)
            {
                return themeBrush;
            }

            if (App.Current.Resources.TryGetValue(key, out var rootValue)
                && rootValue is SolidColorBrush rootBrush)
            {
                return rootBrush;
            }
        }
        catch { }
        return null;
    }
}
