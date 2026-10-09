using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// MOTOR de las paletas de los temas con identidad propia (Rosa/Blanco, Negro/Azul,
/// Marea, Aurora y Brasa).
///
/// La CONFIGURACIÓN de cada tema no vive acá: cada uno tiene su archivo en la carpeta
/// <c>Themes\</c>, y este archivo es el que la aplica. Ver <see cref="ThemeCatalog"/>.
///
/// Cada tema hereda la estructura de un diccionario base (Light o Dark) y pisa sus pinceles
/// de identidad en DOS lugares, cada clave en el diccionario donde vive originalmente (así
/// gana la lookup igual que el valor que reemplaza):
/// - Pinceles semánticos (AppBackgroundBrush, AccentBrush, cards...) → ThemeDictionaries
///   de App.xaml.
/// - SystemAccentColor* (como Color), SystemAccentColorBrush, AccentFillColor* y
///   NavigationView* → diccionario mergeado AccentOverrides.xaml.
///
/// Initialize captura los valores originales al arrancar; RestoreBase los devuelve exactos
/// al salir del tema, así Claro/Oscuro/Sistema quedan intactos.
/// </summary>
public static class ThemePalettes
{
    private static Dictionary<AppTheme, ThemeDefinition>? _palettes;

    /// <summary>Definiciones del catálogo indexadas por tema.</summary>
    private static Dictionary<AppTheme, ThemeDefinition> Palettes
    {
        get
        {
            if (_palettes == null)
            {
                var map = new Dictionary<AppTheme, ThemeDefinition>();
                foreach (var definition in ThemeCatalog.All)
                {
                    map[definition.Theme] = definition;
                    // Los colores de fábrica se copian ACÁ, sobre las definiciones recién
                    // construidas y antes de que nadie pueda escribir en los diccionarios.
                    // Ver FactoryColors.
                    CapturePaletteColors(definition);
                }
                _palettes = map;
            }

            return _palettes;
        }
    }

    public static bool HasOwnPalette(AppTheme theme) => Palettes.ContainsKey(theme);

    /// <summary>
    /// Pincel DE LA DEFINICIÓN de un tema (un gradiente, un resplandor o la imagen del fondo): lo que
    /// no se puede reconstruir desde los colores de fábrica, que son colores sueltos. Devuelve null
    /// si el tema no tiene paleta propia o no define esa clave. Lo usa el splash, que pinta el fondo
    /// del tema ANTES de que existan los diccionarios de recursos (y por eso no puede pedírselos).
    /// </summary>
    public static Microsoft.UI.Xaml.Media.Brush? DefinitionBrush(AppTheme theme, string key)
    {
        if (!Palettes.TryGetValue(theme, out var definition)) return null;
        foreach (var entry in definition.Brushes)
            if (entry.Key == key) return entry.Brush;
        return null;
    }

    /// <summary>Tema base que estructura al tema (Light o Dark). El resto es identidad propia.</summary>
    public static AppTheme BaseThemeFor(AppTheme theme)
        => Palettes.TryGetValue(theme, out var definition) ? definition.Base : theme;

    /// <summary>
    /// Colores DE FÁBRICA por tema, como struct <see cref="Windows.UI.Color"/> y no como
    /// pinceles: es la fuente autoritativa del ajuste de transparencia de paneles.
    ///
    /// Por qué copias y no pinceles: las paletas y los snapshots guardan las MISMAS
    /// instancias de <see cref="Microsoft.UI.Xaml.Media.SolidColorBrush"/> que terminan en
    /// los diccionarios (ApplyPalette escribe entry.Brush tal cual y
    /// PanelAppearance.WriteSurfaceColor lo muta EN SITIO), así que leer su Color en cada
    /// aplicación devolvía el alfa ya escalado por la vuelta anterior: el ajuste se acumulaba
    /// hacia abajo y volver a 0 % dejaba las superficies en un tono oscuro (el bug de "los
    /// datos de la card no coinciden con el %"). Un Color es un struct: se copia al
    /// capturarlo y no lo alcanza ninguna escritura posterior.
    ///
    /// Se llena en dos momentos, los dos antes de aplicar el tema guardado: las paletas
    /// dentro del getter de <see cref="Palettes"/> (sobre las definiciones recién armadas) y
    /// los temas base al final de <see cref="Initialize"/>, sobre el snapshot del diccionario.
    /// </summary>
    private static readonly Dictionary<AppTheme, Dictionary<string, Windows.UI.Color>> FactoryColors = new();

    /// <summary>
    /// Copia PRÍSTINA de los colores de fábrica de cada tema con paleta propia, tal como
    /// salen de su definición en Themes\. La usa <see cref="ResetFactoryColors"/>: la
    /// derivación de superficies desde la imagen de fondo pisa entradas de
    /// <see cref="FactoryColors"/> en caliente, y sin esta copia no habría forma de volver
    /// a los valores escritos a mano (p. ej. al cambiar el fondo a "Degradado").
    /// </summary>
    private static readonly Dictionary<AppTheme, Dictionary<string, Windows.UI.Color>> FactoryPristine = new();

    /// <summary>
    /// Copia los colores de una definición de tema a <see cref="FactoryColors"/> (pinceles de
    /// identidad + pinceles/colores de overrides).
    /// </summary>
    private static void CapturePaletteColors(ThemeDefinition definition)
    {
        var map = new Dictionary<string, Windows.UI.Color>(StringComparer.Ordinal);
        foreach (var entry in definition.Brushes)
            if (entry.Brush is Microsoft.UI.Xaml.Media.SolidColorBrush brush) map[entry.Key] = brush.Color;
        foreach (var (key, hex) in definition.OverrideBrushes) map[key] = ThemePaint.Color(hex);
        foreach (var (key, hex) in definition.AccentColors) map[key] = ThemePaint.Color(hex);
        FactoryColors[definition.Theme] = map;
        FactoryPristine[definition.Theme] = new Dictionary<string, Windows.UI.Color>(map, StringComparer.Ordinal);
    }

    /// <summary>
    /// Copia los colores de los snapshots de un tema base (Light/Dark) a
    /// <see cref="FactoryColors"/>. Los valores pueden ser pinceles (claves semánticas) o
    /// colores sueltos (SystemAccentColor*): los dos se copian.
    /// </summary>
    private static void CaptureBaseColors(AppTheme baseTheme, params Dictionary<string, object>[] snapshots)
    {
        var map = new Dictionary<string, Windows.UI.Color>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            foreach (var (key, value) in snapshot)
            {
                if (value is Microsoft.UI.Xaml.Media.SolidColorBrush brush) map[key] = brush.Color;
                else if (value is Windows.UI.Color color) map[key] = color;
            }
        }

        FactoryColors[baseTheme] = map;
    }

    /// <summary>
    /// Color DE FÁBRICA de una clave para un tema: el de la definición del tema si tiene una
    /// (Rosa/Blanco, Negro/Azul, Marea, Aurora, Brasa) y, si la clave no está ahí,
    /// el del diccionario base (Light o Dark) tal como estaba al arrancar. Nunca lee el
    /// diccionario vivo, así que el resultado depende solo del tema, no del orden en que se
    /// aplicaron los ajustes. Devuelve false si la clave no pertenece al tema.
    /// </summary>
    public static bool TryGetFactoryColor(AppTheme theme, string key, out Windows.UI.Color color)
    {
        color = default;
        try
        {
            if (FactoryColors.TryGetValue(theme, out var map) && map.TryGetValue(key, out color))
                return true;

            var baseTheme = BaseThemeFor(theme) == AppTheme.Light ? AppTheme.Light : AppTheme.Dark;
            if (baseTheme != theme
                && FactoryColors.TryGetValue(baseTheme, out var baseMap)
                && baseMap.TryGetValue(key, out color))
            {
                return true;
            }
        }
        catch { }
        return false;
    }

    // =====================================================================
    // Derivación desde la imagen de fondo (ver Wallpaper.RefreshDerivedPanels)
    // =====================================================================

    /// <summary>
    /// Pisa el color DE FÁBRICA de una clave para un tema. Lo usa la derivación de
    /// superficies desde la imagen de fondo: las superficies dejan de salir de la
    /// definición y salen del promedio de la foto, y la transparencia de paneles —que
    /// calcula SIEMPRE desde el color de fábrica— sigue funcionando sin cambios. Solo
    /// temas con paleta propia tienen entrada: para los demás es no-op.
    /// </summary>
    internal static void SetFactoryColor(AppTheme theme, string key, Windows.UI.Color color)
    {
        if (FactoryColors.TryGetValue(theme, out var map)) map[key] = color;
    }

    /// <summary>
    /// Devuelve los colores de fábrica de un tema a los de su definición (la copia
    /// prístina capturada al arrancar). Lo llama la derivación cuando el fondo activo
    /// no tiene imagen (degradado o tema sin foto): ahí vuelven a mandar los valores
    /// escritos a mano en Themes\.
    /// </summary>
    internal static void ResetFactoryColors(AppTheme theme)
    {
        if (!FactoryColors.TryGetValue(theme, out var map)) return;
        if (!FactoryPristine.TryGetValue(theme, out var pristine)) return;
        foreach (var (key, color) in pristine)
            map[key] = color;
    }

    // =====================================================================
    // Aplicación / restauración
    // =====================================================================

    private static readonly Dictionary<string, Dictionary<string, object>> _originalsRoot = new();
    private static readonly Dictionary<string, Dictionary<string, object>> _originalsOverrides = new();
    private static ResourceDictionary? _overridesHost;
    private static bool _initialized;

    /// <summary>Debe llamarse UNA vez al arrancar, antes de aplicar el tema guardado.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _overridesHost = FindOverridesHost();
        foreach (var themeKey in new[] { "Light", "Dark" })
        {
            _originalsRoot[themeKey] = Capture(GetThemeDictionary(themeKey), AllSemanticKeys());
            _originalsOverrides[themeKey] = Capture(GetOverridesThemeDictionary(themeKey), AllOverrideKeys());

            // Copia inmutable para el ajuste de transparencia (ver FactoryColors): el
            // snapshot de arriba guarda pinceles VIVOS, que WriteSurfaceColor muta.
            CaptureBaseColors(
                themeKey == "Light" ? AppTheme.Light : AppTheme.Dark,
                _originalsRoot[themeKey],
                _originalsOverrides[themeKey]);
        }
    }

    /// <summary>Pisa los pinceles de identidad del tema. No-op si el tema no tiene paleta propia.</summary>
    public static void ApplyPalette(AppTheme theme)
    {
        try
        {
            if (!Palettes.TryGetValue(theme, out var definition)) return;
            var root = GetThemeDictionary(definition.Base == AppTheme.Light ? "Light" : "Dark");
            if (root == null) return;
            var overrides = GetOverridesThemeDictionary(definition.Base == AppTheme.Light ? "Light" : "Dark");

            foreach (var entry in definition.Brushes)
                root[entry.Key] = entry.Brush;
            if (overrides == null) return;
            foreach (var (key, hex) in definition.OverrideBrushes)
                overrides[key] = ThemePaint.Brush(hex);
            foreach (var (key, hex) in definition.AccentColors)
                overrides[key] = ThemePaint.Color(hex);
        }
        catch
        {
            // Nunca debe tirar la app: peor caso, el tema queda sin paleta propia.
        }
    }

    /// <summary>Restaura los valores originales de un diccionario base. Idempotente.</summary>
    public static void RestoreBase(AppTheme baseTheme)
    {
        try
        {
            var key = baseTheme == AppTheme.Light ? "Light" : "Dark";
            Restore(GetThemeDictionary(key), _originalsRoot[key]);
            Restore(GetOverridesThemeDictionary(key), _originalsOverrides[key]);
        }
        catch { }
    }

    private static void Restore(ResourceDictionary? dict, Dictionary<string, object> snapshot)
    {
        if (dict == null) return;
        foreach (var (key, value) in snapshot)
        {
            // Reescribir la MISMA referencia no invalida los {ThemeResource}
            // que la consumen (WinUI ve que el valor del diccionario no cambió).
            // Crear una NUEVA instancia del pincel con el color original fuerza
            // a los controles (NavigationView, cards) a re-resolver.
            if (value is Microsoft.UI.Xaml.Media.SolidColorBrush sb)
                dict[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(sb.Color);
            else if (value is Windows.UI.Color c)
                dict[key] = c;
            else
                dict[key] = value;
        }
    }

    private static Dictionary<string, object> Capture(ResourceDictionary? dict, IEnumerable<string> keys)
    {
        var snapshot = new Dictionary<string, object>();
        if (dict == null) return snapshot;
        foreach (var key in keys)
            if (dict.TryGetValue(key, out var value))
                snapshot[key] = value;
        return snapshot;
    }

    private static IEnumerable<string> AllSemanticKeys()
    {
        // Se recorre el catálogo (no una lista escrita a mano) para que un tema nuevo quede
        // con su snapshot de fábrica sin que nadie tenga que acordarse de sumarlo acá.
        foreach (var definition in ThemeCatalog.All)
            foreach (var entry in definition.Brushes)
                yield return entry.Key;
    }

    private static IEnumerable<string> AllOverrideKeys()
    {
        foreach (var definition in ThemeCatalog.All)
        {
            foreach (var (_, key) in definition.OverrideBrushes) yield return key;
            foreach (var (key, _) in definition.AccentColors) yield return key;
        }
    }

    // =====================================================================
    // Resolución de diccionarios
    // =====================================================================

    private static ResourceDictionary? GetThemeDictionary(string themeKey)
    {
        try
        {
            return Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dict)
                ? dict as ResourceDictionary
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// ThemeDictionary de AccentOverrides para una clave de tema. Si no se
    /// encontró el host mergeado, cae al diccionario raíz (mejor que nada).
    /// </summary>
    private static ResourceDictionary? GetOverridesThemeDictionary(string themeKey)
    {
        var host = _overridesHost;
        if (host == null) return GetThemeDictionary(themeKey);
        return host.ThemeDictionaries.TryGetValue(themeKey, out var dict) ? dict as ResourceDictionary : null;
    }

    /// <summary>
    /// Devuelve el diccionario mergeado que define los SystemAccentColor*.
    /// Se queda con el ÚLTIMO que los define (AccentOverrides va después de
    /// XamlControlsResources en App.xaml, igual que el orden de lookup).
    /// </summary>
    private static ResourceDictionary? FindOverridesHost()
    {
        try
        {
            ResourceDictionary? last = null;
            foreach (var merged in Application.Current.Resources.MergedDictionaries)
            {
                foreach (var value in merged.ThemeDictionaries.Values)
                {
                    if (value is ResourceDictionary rd && rd.ContainsKey("SystemAccentColor"))
                    {
                        last = merged;
                        break;
                    }
                }
            }
            return last;
        }
        catch { return null; }
    }
}
