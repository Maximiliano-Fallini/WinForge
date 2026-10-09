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

    /// <summary>
    /// Pinceles de SUPERFICIE por clave (cards, grillas, paneles), también compartidos: el relleno que
    /// le toca a la UI creada en código, que es siempre el MISMO que el de las cards del XAML —el
    /// vidrio del compositor cuando el motor puede pintarlo, el sólido con el alfa del ajuste cuando
    /// no— pero en una instancia ESTABLE: los elementos armados en code-behind no vuelven a resolver
    /// el {ThemeResource}, así que la instancia se muta en sitio en cada pasada (ver
    /// <see cref="GetSurface"/> y <see cref="Resync"/>).
    ///
    /// SIN esto, todo lo que una página crea en código (la grilla de núcleos, la de sensores, las
    /// cards de procesos/limpieza/TCP/Workshop) quedaba con un color plano: el ajuste de paneles
    /// llegaba al XAML y no a ellas, que es exactamente el reporte "hay cards sin desenfoque".
    /// </summary>
    private static readonly Dictionary<string, Brush> _surfaces = new(StringComparer.Ordinal);

    /// <summary>Claves de superficie creadas en código y ya sujetas por algún elemento (para el log).</summary>
    internal static IReadOnlyCollection<string> LiveSurfaceKeys => _surfaces.Keys;

    /// <summary>Cuántas superficies creadas en código están siendo repintadas por el ajuste.</summary>
    internal static int LiveSurfaceCount => _surfaces.Count;

    /// <summary>
    /// El pincel de superficie de una clave, si ya existe (no lo crea). Lo usa
    /// <see cref="PanelBlurLayer"/> para reconocer esos paneles por INSTANCIA al recorrer el árbol.
    /// </summary>
    internal static Brush? SurfaceBrush(string key)
        => _surfaces.TryGetValue(key, out var brush) ? brush : null;

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
        var live = new SolidColorBrush(ColorOf(source) ?? Microsoft.UI.Colors.Transparent);
        _live[key] = live;
        return live;
    }

    /// <summary>
    /// EL PINCREL DE SUPERFICIE de la UI creada en código: una card, una grilla, un panel. Es el
    /// MISMO relleno que el de la clave en el diccionario del tema —o sea el vidrio del compositor
    /// cuando el motor lo pinta (ver <see cref="GlassBlurBrush"/>), el acrílico en su caso o el sólido
    /// con el alfa del ajuste cuando no—, pero en una instancia estable por clave.
    ///
    /// Por qué devuelve <see cref="Brush"/> y no el sólido de <see cref="Get"/>: acá el relleno puede
    /// NO ser un color —el vidrio es un pincel de composición—, y el sólido de <see cref="Get"/> es
    /// justamente lo que dejaba a estas cards sin desenfoque. Lo que sí se garantiza es que la
    /// instancia NO cambia de tipo durante la sesión: con el desenfoque apagado el vidrio baja a radio
    /// 0 (relleno plano, sin efecto), así que encenderlo y apagarlo es una escritura y no un pincel
    /// nuevo que los elementos ya sujetos nunca verían.
    ///
    /// Solo tiene sentido para las claves de <see cref="PanelAppearance.SurfaceKeys"/>: para cualquier
    /// otra clave el relleno es el color del tema y alcanza con <see cref="Get"/>.
    /// </summary>
    public static Brush GetSurface(string key)
    {
        if (_surfaces.TryGetValue(key, out var brush))
        {
            PaintSurface(key, brush, themeKey: null);
            return brush;
        }

        var created = CreateSurface(key, themeKey: null);
        _surfaces[key] = created;
        return created;
    }

    /// <summary>
    /// El relleno que le toca a una clave pedida DESDE AFUERA de la app (un componente del Workshop,
    /// que llega acá por reflexión: ver AppBridge.Brush): si la clave es una superficie de la app
    /// —una card, una grilla, el menú: ver <see cref="PanelAppearance.SurfaceKeys"/>— devuelve el mismo
    /// relleno que tienen las cards del XAML, vidrio incluido; si no, el sólido de <see cref="Get"/>.
    ///
    /// Es lo que hace que una card creada por un componente se vea como las de la casa. La distinción
    /// no es cosmética: para un TEXTO o un BORDE, un pincel de superficie sería un relleno de
    /// composición (no un color), y quien lo use esperando un color se quedaría sin él.
    /// </summary>
    public static Brush Fill(string key)
        => Array.IndexOf(PanelAppearance.SurfaceKeys, key) >= 0 ? GetSurface(key) : Get(key);

    /// <summary>
    /// Arma el relleno de una superficie creada en código: el vidrio si el compositor puede (con radio
    /// 0 cuando el desenfoque está apagado, que es relleno plano) o el sólido si no.
    /// </summary>
    private static Brush CreateSurface(string key, string? themeKey)
    {
        var color = SurfaceColor(key, themeKey ?? ActiveThemeKey());

        if (GlassBlurBrush.Available && GlassBlurBrush.TryCreate(PanelAppearance.SurfaceRadiusPx, color) is { } glass)
            return glass;

        return new SolidColorBrush(color);
    }

    /// <summary>
    /// Pinta la superficie con el color y el radio VIGENTES, mutando la instancia (nunca la reemplaza:
    /// es la que los elementos ya tienen puesta). Si la clave no resuelve a un color —se está
    /// aplicando todavía— se deja como está.
    /// </summary>
    private static void PaintSurface(string key, Brush brush, string? themeKey)
    {
        try
        {
            var source = Resolve(key, themeKey ?? ActiveThemeKey());
            if (ColorOf(source) is not { } color) return;

            switch (brush)
            {
                case GlassBlurBrush glass:
                    glass.SetRadius(PanelAppearance.SurfaceRadiusPx);
                    glass.SetTint(color);
                    break;
                case AcrylicBrush acrylic:
                    var tint = Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);
                    if (acrylic.TintColor != tint) acrylic.TintColor = tint;
                    if (Math.Abs(acrylic.TintOpacity - color.A / 255.0) > 0.001) acrylic.TintOpacity = color.A / 255.0;
                    if (acrylic.FallbackColor != color) acrylic.FallbackColor = color;
                    break;
                case SolidColorBrush solid:
                    if (solid.Color != color) solid.Color = color;
                    break;
            }
        }
        catch { }
    }

    /// <summary>
    /// El COLOR de una superficie: el del relleno que el ajuste ya escribió en el diccionario (ver
    /// <see cref="PanelAppearance.SurfaceFill"/>) con el alfa de la transparencia incluida. Es el mismo
    /// color que ve el XAML, así que una card creada en código se ve igual que una del XAML. Si la
    /// clave no tiene color equivalente —un pincel que no es color (una imagen)— se devuelve
    /// transparente y el llamador conserva el suyo.
    /// </summary>
    private static Windows.UI.Color SurfaceColor(string key, string themeKey)
        => ColorOf(Resolve(key, themeKey)) ?? Microsoft.UI.Colors.Transparent;

    /// <summary>
    /// Re-resuelve el color de UN pincel live (de a una clave, sin tocar su Opacity ni
    /// el resto de los pinceles). Lo usa PanelAppearance al cambiar la transparencia de
    /// las superficies: NO conviene llamar a <see cref="Refresh"/> para eso, porque
    /// Refresh() además pone Opacity = 0 en los pinceles de claves que no viven en los
    /// diccionarios de tema de la app (las del sistema, como TextFillColorPrimaryBrush)
    /// y apagarlas deja textos e íconos invisibles.
    /// </summary>
    /// <remarks>
    /// Lo mismo vale para el VIDRIO de una superficie creada en código: el radio hay que reescribirlo
    /// porque un cambio de camino (encender o apagar el desenfoque con el deslizador) no reemplaza esa
    /// instancia —los elementos ya sujetos seguirían con el radio anterior—.
    /// </remarks>
    /// <summary>
    /// El pincel live de la clave, SOLO si ya existe (no lo crea). Lo usa
    /// <see cref="PanelBlurLayer"/> para reconocer los paneles construidos en code-behind.
    /// </summary>
    internal static bool TryGetLive(string key, out SolidColorBrush brush)
        => _live.TryGetValue(key, out brush!);

    public static void Resync(string key, string? themeKey = null)
    {
        if (!_live.TryGetValue(key, out var live)) return;
        // La clave de tema se puede pasar desde afuera: en el arranque root.ActualTheme
        // todavía no está resuelto y resolver con ActiveThemeKey() traería el color del
        // otro tema. Ver el comentario de PanelAppearance.ActiveThemeKey.
        var source = Resolve(key, themeKey ?? ActiveThemeKey());
        if (source == null) return;
        live.Opacity = 1;

        // La superficie creada en código de la MISMA clave se pinta en la misma pasada: es lo que
        // mantiene al día la grilla de núcleos, la de sensores y las cards armadas en code-behind.
        if (_surfaces.TryGetValue(key, out var surface)) PaintSurface(key, surface, themeKey);

        // La clave puede NO resolver a un color: con el desenfoque encendido la superficie es el
        // acrílico de la plataforma (o, en el camino de respaldo, la foto desenfocada). El pincel
        // live —que es sólido, porque la UI creada en code-behind no puede llevar el vidrio— se
        // queda con el color EQUIVALENTE de ese relleno (el tinte con el alfa del panel, ver
        // ColorOf): mejor el color del diseño que dejarla invisible.
        if (ColorOf(source) is { } resolved) live.Color = resolved;
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
            ColorOf(Resolve(key, ThemePalettes.BaseThemeFor(effectiveTheme) == AppTheme.Light ? "Light" : "Dark"))
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

        // Las superficies creadas en código: misma re-resolución, en su instancia estable.
        foreach (var key in new List<string>(_surfaces.Keys)) PaintSurface(key, _surfaces[key], themeKey);

        foreach (var kv in _live)
        {
            var source = Resolve(kv.Key, themeKey);
            if (ColorOf(source) is { } resolved)
            {
                kv.Value.Opacity = 1;
                kv.Value.Color = resolved;
            }
            else if (source != null)
            {
                // La clave está, pero no es un color (la foto desenfocada del camino de respaldo):
                // el pincel live conserva el suyo y sigue visible.
                kv.Value.Opacity = 1;
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
    /// El COLOR que representa un relleno de superficie para la UI construida en code-behind, que solo
    /// entiende pinceles sólidos:
    ///
    /// - <see cref="SolidColorBrush"/>: su color, tal cual.
    /// - <see cref="GlassBlurBrush"/> (el vidrio del compositor, ver PanelAppearance.SurfaceFill): su
    ///   tinte, que YA es el color del panel con el alfa del ajuste —o sea el equivalente sólido exacto.
    /// - <see cref="AcrylicBrush"/> (el vidrio de la plataforma): su tinte con el alfa de su opacidad,
    ///   que es el mismo color. Sin estos dos casos, las cards creadas en code-behind quedarían
    ///   transparentes: ninguno de los dos es un SolidColorBrush y el color caería a Transparent.
    /// - Cualquier otro pincel (la foto desenfocada del camino de respaldo, un ImageBrush): null, o
    ///   sea "no hay color equivalente" y el pincel live conserva el suyo.
    /// </summary>
    /// <summary>
    /// El color de un relleno de la app, sea del tipo que sea: el de un sólido, el TINTE del vidrio del
    /// compositor o el del acrílico. Null si el pincel no representa un color (una imagen, un gradiente).
    ///
    /// Lo necesita quien reconoce una card POR SU COLOR en vez de por la instancia del pincel —el halo
    /// del mouse (ver RevealEffect)—: desde que las superficies pueden ser vidrio, exigir un
    /// <see cref="SolidColorBrush"/> dejaba a esas cards sin efecto, porque el vidrio no es un sólido.
    /// </summary>
    public static Windows.UI.Color? FillColor(Brush? brush) => ColorOf(brush);

    private static Windows.UI.Color? ColorOf(Brush? brush) => brush switch
    {
        SolidColorBrush solid => solid.Color,
        GlassBlurBrush glass => glass.Tint,
        AcrylicBrush acrylic => Windows.UI.Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(255 * acrylic.TintOpacity), 0, 255),
            acrylic.TintColor.R,
            acrylic.TintColor.G,
            acrylic.TintColor.B),
        _ => null
    };

    /// <summary>
    /// Resuelve el pincel fuente de la clave en el diccionario de tema indicado;
    /// si la clave no está ahí (p. ej. ErrorBrush/SuccessBrush/WarningBrush,
    /// iguales en ambos temas), cae a los recursos raíz de la app.
    ///
    /// Devuelve el pincel tal cual está en el diccionario: normalmente un SolidColorBrush, pero
    /// con el desenfoque de paneles encendido las superficies guardan la foto desenfocada (un
    /// ImageBrush). Los llamadores que necesitan un color comprueban el tipo.
    /// </summary>
    private static Brush? Resolve(string key, string themeKey)
    {
        try
        {
            if (App.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dict)
                && dict is ResourceDictionary themeDict
                && themeDict.TryGetValue(key, out var value)
                && value is Brush themeBrush)
            {
                return themeBrush;
            }

            if (App.Current.Resources.TryGetValue(key, out var rootValue)
                && rootValue is Brush rootBrush)
            {
                return rootBrush;
            }
        }
        catch { }
        return null;
    }
}
