using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WHPO_UI;

/// <summary>
/// Piezas compartidas de las filas de idioma: el botón de descarga y su anillo de
/// avance, que se ven igual en el menú de idioma del navbar y en el paso de idioma del
/// onboarding. Viven acá y no duplicadas en cada ventana porque las dos filas tienen
/// que medir igual: cuando cada lugar armaba la suya, terminaban con geometrías
/// distintas y la diferencia se notaba al abrir el menú.
///
/// Por qué el botón es chico a propósito: la fila no tiene MinHeight, la da el
/// contenido —el texto (14 px en el menú, 13 en el onboarding) más el padding del
/// contenedor—. El estilo por defecto del Button trae 11,5,11,6 y se iría solo a ~32 px,
/// así que acá se anulan sus mínimos y queda en 12 px de glifo + 1 px de padding ≈ 18:
/// nunca estira la fila que lo hospeda.
/// </summary>
internal static class LanguageRowUi
{
    /// <summary>Glifo E896 (Download) de la fuente de símbolos del tema.</summary>
    public const string DownloadGlyph = "\uE896";

    /// <summary>Glifo E711 (Cancel) de la fuente de símbolos del tema: la ✕ de quitar, la misma
    /// que usa el control de ventiladores para borrar un punto de la curva.</summary>
    public const string RemoveGlyph = "\uE711";

    /// <summary>
    /// Botón de descarga: el glifo SOLO, sin fondo ni borde, en el color de acento. Es una
    /// fila de menú: una pastilla rellena pesa más que el propio nombre del idioma y rompe
    /// la lectura de la lista. El hover lo pone el estilo del sistema (la animación de
    /// PointerOver del Button va al fondo del ContentPresenter), así que la fila se siente
    /// clickeable igual.
    ///
    /// No recibe el toque (<c>IsHitTestVisible = false</c>): el que descarga es la fila
    /// entera, así hay un solo camino y no hay doble descarga.
    /// </summary>
    public static Button CreateDownloadButton()
    {
        return new Button
        {
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(6, 1, 6, 1),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Content = new FontIcon
            {
                Glyph = DownloadGlyph,
                FontSize = 12,
                FontFamily = SymbolFontFamily(),
                Foreground = ThemeBrushes.Get("AccentBrush")
            }
        };
    }

    /// <summary>
    /// Botón de desinstalar de un idioma ya instalado: la ✕ sola, sin fondo y en color
    /// secundario, con el mismo tamaño que el de descarga para que la fila no cambie de alto
    /// según tenga uno u otro.
    ///
    /// A diferencia del de descarga, ESTE sí recibe el toque: quitar el idioma y activarlo no
    /// pueden salir del mismo gesto, así que el que llama le engancha el Click y evita que el
    /// toque siga hasta la fila.
    /// </summary>
    public static Button CreateRemoveButton()
    {
        return new Button
        {
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(6, 1, 6, 1),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                Glyph = RemoveGlyph,
                FontSize = 12,
                FontFamily = SymbolFontFamily(),
                Foreground = ThemeBrushes.Get("SecondaryTextBrush")
            }
        };
    }

    /// <summary>
    /// Anillo de avance de la descarga. Es el ÚNICO indicador de progreso: la fila no cambia
    /// su texto, así el nombre del idioma sigue a la vista mientras se baja. Nace invisible
    /// pero EN EL ÁRBOL (así no mueve la fila cuando aparece) y reemplaza al botón en la misma
    /// columna, por lo que mide menos que él y nunca estira la fila.
    ///
    /// Es el arco del velocímetro de la app (Limpieza es el otro uso) sin las marcas ni la
    /// aguja: a este tamaño no entrarían, y el arco solo ya dice el avance.
    /// </summary>
    public static ProgressRing CreateProgressRing()
    {
        return new ProgressRing
        {
            Width = 16,
            Height = 16,
            Opacity = 0,
            IsActive = false,
            IsIndeterminate = false,
            IsHitTestVisible = false,
            Foreground = ThemeBrushes.Get("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// Fuente de símbolos del tema (Segoe Fluent Icons) para los íconos creados en
    /// código, con respaldo por si el recurso no está.
    /// </summary>
    public static FontFamily SymbolFontFamily()
    {
        if (Application.Current.Resources.TryGetValue("SymbolThemeFontFamily", out var resource)
            && resource is FontFamily ff)
        {
            return ff;
        }
        return new FontFamily("Segoe Fluent Icons");
    }
}
