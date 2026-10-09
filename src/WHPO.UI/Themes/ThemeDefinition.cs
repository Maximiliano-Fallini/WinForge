using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Configuración COMPLETA de un tema: todo lo que lo define vive acá, en un archivo por
/// tema dentro de <c>Themes\</c>. Es la fuente de la que <see cref="ThemePalettes"/> saca
/// lo que escribe en los diccionarios de recursos, y de la que la pestaña Apariencia saca
/// lo que muestra (paleta, imagen de fondo, transparencia sugerida).
///
/// Un tema se describe con:
/// - <see cref="Brushes"/>: los pinceles semánticos que van a los ThemeDictionaries de
///   App.xaml (fondo, cards, textos secundarios, gráficas...). Acá también van las tres
///   piezas que hacen que un tema tenga identidad propia: el gradiente del fondo de
///   ventana, sus resplandores y la imagen de fondo.
/// - <see cref="OverrideBrushes"/>: los que viven en el diccionario mergeado
///   AccentOverrides.xaml (SystemAccentColorBrush, AccentFillColor*, NavigationView*,
///   desplegables y menús).
/// - <see cref="AccentColors"/>: los SystemAccentColor* como COLOR (no como pincel).
///   Escribirlos así es lo que hace reactivos a ToggleSwitch, CheckBox, ProgressBar y
///   los botones de acento, porque los pinceles internos de WinUI derivan de esos colores
///   en runtime.
///
/// Cada clave se escribe en el diccionario donde vive originalmente, así gana la lookup
/// igual que el valor que reemplaza.
/// </summary>
public sealed class ThemeDefinition
{
    /// <summary>Tema al que pertenece esta definición.</summary>
    public required AppTheme Theme { get; init; }

    /// <summary>Tema base que le da la estructura (Light o Dark). El resto es identidad propia.</summary>
    public required AppTheme Base { get; init; }

    /// <summary>Nombre visible del tema (la clave de traducción, en español).</summary>
    public required string NameKey { get; init; }

    /// <summary>Pinceles semánticos → ThemeDictionaries de App.xaml.</summary>
    public required ThemePaint.Entry[] Brushes { get; init; }

    /// <summary>Pinceles → diccionario mergeado AccentOverrides.xaml.</summary>
    public required (string Key, string Hex)[] OverrideBrushes { get; init; }

    /// <summary>Colores SystemAccentColor* (tipo Color) → AccentOverrides.xaml.</summary>
    public required (string Key, string Hex)[] AccentColors { get; init; }

    /// <summary>
    /// Archivo de imagen del fondo, dentro de <c>Themes\Backgrounds\</c>. Null en los temas
    /// sin imagen: ahí el fondo es el color plano o el gradiente del tema.
    /// </summary>
    public string? Wallpaper { get; init; }

    /// <summary>
    /// Crédito que la card de Fondo muestra debajo de la miniatura (autor y origen de la
    /// imagen). Null si el tema no trae imagen.
    /// </summary>
    public string? WallpaperCredit { get; init; }

    /// <summary>
    /// Transparencia de los paneles con la que el tema se ve como fue diseñado, en puntos
    /// porcentuales (0 = paneles opacos, 90 = casi invisibles; ver la escala en PanelAppearance).
    /// Es el valor con el que el tema arranca MIENTRAS el usuario no lo haya movido, y cada tema
    /// guarda el suyo (ver PanelAppearance y ThemeCatalog.KindOf): los de ENTORNO declaran 90
    /// —la foto se ve por todos lados— y los PLANOS y de DEGRADADO, 0.
    /// </summary>
    public double PanelTransparency { get; init; }

    /// <summary>
    /// Desenfoque (blur) de los paneles con el que el tema arranca, en puntos porcentuales. Igual
    /// que <see cref="PanelTransparency"/>: es el valor de diseño mientras el usuario no lo mueva, y
    /// cada tema guarda el suyo. Los temas de ENTORNO declaran 5 —el vidrio suave que se ve sobre
    /// la foto— y los PLANOS y de DEGRADADO, 0 (no hay foto detrás que desenfocar).
    /// </summary>
    public double PanelBlur { get; init; }

    /// <summary>El tema dibuja su fondo con una imagen en vez de un color plano o un gradiente.</summary>
    public bool HasWallpaper => !string.IsNullOrEmpty(Wallpaper);
}
