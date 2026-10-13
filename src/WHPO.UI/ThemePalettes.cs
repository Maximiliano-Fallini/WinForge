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
    /// Claves que los CONTROLES de WinUI piden por su cuenta —el desplegable cerrado, el Expander y
    /// el hover/pressed de un botón— y que la app no declara en ninguna paleta, así que las pintaba
    /// el sistema: rellenos TRANSLÚCIDOS que ni son del tema ni responden a la transparencia (no
    /// están en <see cref="PanelAppearance.SurfaceKeys"/>). Como ese relleno REEMPLAZA el del
    /// control al pasar el mouse, sobre la foto se leía como "se volvió transparente" y el texto se
    /// perdía.
    ///
    /// Cada una copia el COLOR de su clave hermana, que es el tono que la paleta ya eligió para esa
    /// superficie (no se inventa ningún color): el desplegable cerrado es la misma superficie que un
    /// cuadro de texto, así que toma sus cuatro tonos, y el Expander toma el de la card.
    ///
    /// Se resuelven por definición en <see cref="ApplyPalette"/> (y, para los temas clásicos, con los
    /// valores escritos en AccentOverrides.xaml), y <see cref="TryGetFactoryColor"/> las resuelve
    /// también, que es lo que hace que el ajuste de transparencia las alcance.
    /// </summary>
    private static readonly (string Key, string SourceKey)[] InheritedKeys =
    {
        // Desplegable CERRADO (el popup ya lo pinta cada tema con ComboBoxDropDown*).
        ("ComboBoxBackground", "TextControlBackground"),
        ("ComboBoxBackgroundPointerOver", "TextControlBackgroundPointerOver"),
        ("ComboBoxBackgroundPressed", "TextControlBackgroundFocused"),
        ("ComboBoxBackgroundFocused", "TextControlBackgroundFocused"),
        ("ComboBoxBackgroundDisabled", "TextControlBackgroundDisabled"),

        // Expander (los "desplegables" de la Guía): el encabezado es una card y el contenido vive
        // sobre el mismo tono. El Expander NO tiene claves de hover propias: su estado PointerOver
        // solo cambia el filete y el chevron.
        ("ExpanderHeaderBackground", "CardBackgroundBrush"),
        ("ExpanderContentBackground", "CardBackgroundBrush"),

        // Los BOTONES sin fondo propio ("Abrir grupos" del Monitor de sensores, "Actualizar" de las
        // cards, "Cancelar"…) NO van acá: no heredan el tono de una superficie grande porque el
        // resultado es una cajita oscura en vez de un botón. Son capas neutras sutiles: ver OverlayKeys.
    };

    /// <summary>
    /// RELLENO SUTIL de los botones sin fondo propio —el estilo por defecto de WinUI: "Abrir grupos" y
    /// "Cerrar grupos" del Monitor de sensores, los "Actualizar" de las cards, "Cancelar"…—.
    ///
    /// POR QUÉ NO SON PANELES: los paneles se pintan con el color OPACO del tema (navy oscuro), y con
    /// ese tono un botón deja de leerse como botón y se lee como una cajita: es el reporte "los botones
    /// de abrir grupos son negros". El tono de una card no es el tono de un control chico: el botón por
    /// defecto de WinUI es una CAPA NEUTRA encima de lo que tenga detrás.
    ///
    /// EL ALFA NO ES EL QUE USA WinUI, ES MÁS ALTO: WinUI pinta esa capa con blanco al 6 % en reposo, y
    /// con ese alfa —y el deslizador escalándola— el botón quedaba con una película de blanco del 3 %
    /// que no se ve: el vidrio estaba puesto (mismo pincel y mismo radio que las cards, ver el censo)
    /// pero no había material que se leyera como vidrio. Ahora son blanco al 15 % de reposo —el mismo
    /// neutro que los temas de entorno usan para la capa de hover de una card (CardHoverBrush
    /// #26FFFFFF)—, 20 % con el mouse encima y 10 % apretado: el botón se lee como un chip esmerilado,
    /// no es una caja oscura y sigue respondiendo a los dos deslizadores. En los claros el relleno ya
    /// era fuerte (blanco al 70 % de reposo) y se queda como estaba.
    ///
    /// POR QUÉ SU ALFA ES SU PROPIO ALFA: el ajuste ESCALA estas capas en vez de fijarles el alfa
    /// absoluto de los paneles (ver PanelAppearance.SurfaceColorFor). Con la regla de los paneles, en
    /// 0 % de transparencia el botón quedaba BLANCO OPACO en los temas oscuros —una capa sutil con el
    /// alfa reemplazado por 1,00—; escalada, en 0 % queda igual que el botón del sistema y en 100 %
    /// desaparece. Es una capa, no un panel.
    /// </summary>
    private static readonly (string Key, string Dark, string Light)[] OverlayKeys =
    {
        ("ButtonBackground", "#26FFFFFF", "#B3FFFFFF"),
        ("ButtonBackgroundPointerOver", "#33FFFFFF", "#CCFFFFFF"),
        ("ButtonBackgroundPressed", "#1AFFFFFF", "#80FFFFFF"),
        ("ButtonBackgroundDisabled", "#26FFFFFF", "#4DFFFFFF")
    };

    /// <summary>Color de la capa neutra de un botón para un diccionario base (Light/Dark).</summary>
    private static Windows.UI.Color OverlayColor(string key, AppTheme baseTheme)
    {
        foreach (var (overlayKey, dark, light) in OverlayKeys)
            if (overlayKey == key) return ThemePaint.Color(baseTheme == AppTheme.Light ? light : dark);
        return default;
    }

    /// <summary>La clave es una capa neutra de botón (ver <see cref="OverlayKeys"/>).</summary>
    internal static bool IsOverlayKey(string key)
    {
        foreach (var (overlayKey, _, _) in OverlayKeys)
            if (overlayKey == key) return true;
        return false;
    }

    /// <summary>La clave de la que copia el color una clave heredada, o null si no lo es.</summary>
    private static string? SourceOf(string key)
    {
        foreach (var (inherited, source) in InheritedKeys)
            if (inherited == key) return source;
        return null;
    }

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

        // Las CAPAS NEUTRAS de los botones (ver OverlayKeys) no son de la definición del tema —no
        // llevan color propio— pero sí tienen que estar en el mapa: es de donde el ajuste saca el
        // color de fábrica de las claves del censo, y sin entrada quedaban fuera de los deslizadores.
        foreach (var (key, _, _) in OverlayKeys)
            map[key] = OverlayColor(key, definition.Base);

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
            // HEREDADA: su color es SIEMPRE el de la clave que copia (ver InheritedKeys), resuelto
            // para el tema elegido y, si el tema no la declara, para su tema base. Sin esto quedaría
            // fuera del ajuste de transparencia, que solo alcanza a las claves con color de fábrica.
            //
            // Va ANTES de buscarla por su propio nombre a propósito: el snapshot del tema base
            // también tiene entrada para la heredada —AccentOverrides la declara para los temas
            // clásicos— y esa entrada, en un tema con paleta propia, devolvía el tono del CLÁSICO
            // (#151517 en Amatista) en vez del tono del tema elegido. La tabla es de un solo nivel:
            // nunca encadena una heredada con otra.
            if (SourceOf(key) is { } sourceKey) return TryGetOwnFactoryColor(theme, sourceKey, out color);

            if (TryGetOwnFactoryColor(theme, key, out color)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Color de fábrica DE LA CLAVE (definición del tema y, si no está ahí, el de su tema base).</summary>
    private static bool TryGetOwnFactoryColor(AppTheme theme, string key, out Windows.UI.Color color)
    {
        color = default;
        if (FactoryColors.TryGetValue(theme, out var map) && map.TryGetValue(key, out color))
            return true;

        var baseTheme = BaseThemeFor(theme) == AppTheme.Light ? AppTheme.Light : AppTheme.Dark;
        return baseTheme != theme
            && FactoryColors.TryGetValue(baseTheme, out var baseMap)
            && baseMap.TryGetValue(key, out color);
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

            // Claves que piden los controles de WinUI y que el tema no declara: se escriben con el
            // color de su clave hermana (ver InheritedKeys). Instancia NUEVA, no la del pincel
            // hermano: el ajuste de transparencia muta cada clave por separado (ver
            // PanelAppearance.WriteSurfaceBrush).
            foreach (var (key, sourceKey) in InheritedKeys)
            {
                if (!TryGetFactoryColor(theme, sourceKey, out var inherited)) continue;
                overrides[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(inherited);
            }

            // Y las CAPAS NEUTRAS de los botones por defecto, con el alfa del diccionario base del
            // tema (ver OverlayKeys): el valor escrito en AccentOverrides es el de los temas clásicos,
            // así que un tema con paleta propia tiene que traer el suyo.
            foreach (var (key, _, _) in OverlayKeys)
                overrides[key] = new Microsoft.UI.Xaml.Media.SolidColorBrush(OverlayColor(key, definition.Base));
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

        // Las heredadas entran al snapshot para que RestoreBase las devuelva a los valores de
        // AccentOverrides al salir de un tema con paleta propia: si no, el color del tema anterior
        // quedaba pegado en los controles de WinUI de los temas clásicos. Las CAPAS NEUTRAS de los
        // botones también: son las que el tema con paleta propia pisa en ApplyPalette.
        foreach (var (key, _) in InheritedKeys) yield return key;
        foreach (var (key, _, _) in OverlayKeys) yield return key;
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
