using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Catálogo de temas: la lista de todo lo que hay en la carpeta <c>Themes\</c>, un archivo
/// por tema.
///
/// Para AGREGAR UN TEMA alcanza con: crear su archivo en <c>Themes\</c> (una clase con
/// <c>Create()</c> que devuelva su <see cref="ThemeDefinition"/>), sumar el tema al enum
/// <see cref="AppTheme"/> y agregar su entrada acá y en el combo de temas de Configuración.
/// El resto —snapshots de fábrica, restauración, transparencia de paneles, resolución de
/// los pinceles live— lo hace el resto del motor solo, porque todo se deriva de este
/// catálogo y no de listas escritas a mano.
/// </summary>
public static class ThemeCatalog
{
    private static IReadOnlyList<ThemeDefinition>? _all;

    /// <summary>
    /// Todos los temas con identidad propia. El orden es el de la tabla de Configuración
    /// (los clásicos primero, después los de fondo propio).
    /// </summary>
    public static IReadOnlyList<ThemeDefinition> All
    {
        get
        {
            // Inicialización perezosa: cada definición arma pinceles de WinUI, así que se
            // construyen recién cuando alguien las pide (antes de que haya Application).
            _all ??= new List<ThemeDefinition>
            {
                RosaBlancoTheme.Create(),
                NegroAzulTheme.Create(),
                MareaTheme.Create(),
                AuroraTheme.Create(),
                BrasaTheme.Create(),
                CrepusculoTheme.Create()
            };
            return _all;
        }
    }

    /// <summary>Definición de un tema, o null si el tema no tiene paleta propia.</summary>
    public static ThemeDefinition? Find(AppTheme theme)
    {
        foreach (var definition in All)
            if (definition.Theme == theme) return definition;
        return null;
    }

    /// <summary>
    /// Cómo pinta el tema su fondo. Es lo que agrupa el selector de temas (Entorno / Degradado /
    /// Planos) y lo que decide con qué ajustes de apariencia arranca cada uno: los de entorno traen
    /// foto (90 % de transparencia y 5 % de blur de fábrica) y los planos o de degradado, no
    /// (0 % y 0 %).
    /// </summary>
    public enum ThemeKind
    {
        /// <summary>Sin foto: un color liso (los clásicos, Rosa/Blanco, Negro/Azul).</summary>
        Flat,

        /// <summary>Sin foto: un gradiente propio en WindowBackdropBrush (Crepúsculo).</summary>
        Gradient,

        /// <summary>Con foto de fondo, de Themes\Backgrounds (Marea, Aurora, Brasa).</summary>
        Environment
    }

    /// <summary>
    /// Tipo de un tema, derivado de su definición (no hay una lista que mantener): con foto es
    /// Entorno; sin foto pero con un gradiente en WindowBackdropBrush es Degradado; el resto, plano.
    /// Los clásicos (Claro/Oscuro/Sistema) no tienen definición: son planos.
    /// </summary>
    public static ThemeKind KindOf(AppTheme theme)
    {
        var definition = Find(theme);
        if (definition == null) return ThemeKind.Flat;
        if (definition.HasWallpaper) return ThemeKind.Environment;

        foreach (var entry in definition.Brushes)
        {
            if (entry.Key == "WindowBackdropBrush"
                && entry.Brush is LinearGradientBrush or RadialGradientBrush)
            {
                return ThemeKind.Gradient;
            }
        }

        return ThemeKind.Flat;
    }

    /// <summary>
    /// El tema deja ver el FONDO DE LA VENTANA (la foto o el gradiente) detrás del contenido: sus
    /// páginas son transparentes y el NavigationView no pinta una capa propia. Lo consultan
    /// MainWindow (para no tapar el fondo con la capa del menú) y la pestaña Apariencia.
    /// </summary>
    public static bool ShowsWindowBackdrop(AppTheme theme)
        => KindOf(theme) is ThemeKind.Environment or ThemeKind.Gradient;

    /// <summary>
    /// Transparencia de paneles con la que el tema se ve como fue diseñado, en puntos porcentuales:
    /// 90 % en los de entorno (deja ver la foto), 0 % en los planos y de degradado.
    /// </summary>
    public static double DefaultPanelTransparency(AppTheme theme)
        => Find(theme)?.PanelTransparency ?? 0.0;

    /// <summary>
    /// Desenfoque de paneles con el que el tema se ve como fue diseñado: 5 % en los de entorno (el
    /// vidrio suave sobre la foto), 0 % en los planos y de degradado.
    /// </summary>
    public static double DefaultPanelBlur(AppTheme theme)
        => Find(theme)?.PanelBlur ?? 0.0;

    /// <summary>Archivo de la imagen de fondo del tema (dentro de Themes\Backgrounds), o null.</summary>
    public static string? WallpaperFile(AppTheme theme) => Find(theme)?.Wallpaper;

    /// <summary>Crédito de la imagen de fondo del tema, o null si no tiene.</summary>
    public static string? WallpaperCredit(AppTheme theme) => Find(theme)?.WallpaperCredit;
}
