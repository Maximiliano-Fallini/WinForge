using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Ajustes de apariencia que se aplican EN CALIENTE sobre el tema activo, desde la pestaña
/// Apariencia de Configuración: la transparencia de los paneles, el desenfoque (blur) y el
/// fondo de la ventana.
///
/// Por qué acá y no en Themes\: una definición de tema es "el aspecto que le diseñamos"
/// (colores e imagen fijos que definen su identidad), mientras que esto son preferencias del
/// usuario que se suman encima del tema elegido. Ninguna de las dos toca al fondo del tema
/// como si fuera un panel: el gradiente y el velo son identidad.
///
/// LA ESCALA (esto es lo que cambió en 0.3.1): la transparencia es ABSOLUTA. Antes se
/// multiplicaba el alfa que el tema ya traía —así que en 0 % las superficies volvían al diseño
/// del tema, que en Marea ya era translúcido y la card decía "0 %" con los paneles
/// transparentes—. Ahora 0 % significa paneles OPACOS con el color del tema y 100 % los deja
/// casi invisibles: el número de la card coincide con lo que se ve en todos los temas.
/// LOS DOS NÚMEROS SON POR TEMA (esto es lo que cambió en 0.3.2): cada tema declara con qué
/// transparencia y con qué desenfoque se ve como fue diseñado —90 % y 5 % los de ENTORNO, que traen
/// foto; 0 % y 0 % los PLANOS y los de DEGRADADO, ver <see cref="ThemeCatalog.KindOf"/>— y arranca
/// con esos valores mientras el usuario no los mueva con ese tema puesto. Lo que el usuario mueve se
/// guarda para ESE tema (ver <see cref="Resolve"/>), así cambiar de tema devuelve el punto de partida
/// del tema nuevo en vez de arrastrar el del anterior.
///
/// CLAVE DEL CÁLCULO: el color de cada superficie NO se lee del diccionario vivo (que puede
/// tener el color ya pisado por una aplicación anterior y dejaba el ajuste pegado a un valor
/// oscuro). Se pide a <see cref="ThemePalettes.TryGetFactoryColor"/>, que lo resuelve de la
/// DEFINICIÓN del tema, así que el resultado depende solo del tema y del deslizador, nunca del
/// orden en que se aplicaron las cosas.
/// </summary>
public static class PanelAppearance
{
    /// <summary>
    /// Claves de configuración del ajuste VIEJO (uno solo para toda la app). Hoy cada tema tiene
    /// las suyas —<see cref="TransparencyKeyFor"/> y <see cref="BlurKeyFor"/>, con el nombre del tema
    /// como sufijo— y estas dos quedan solo para la migración: lo que el usuario ya había elegido se
    /// muda al tema vigente una vez (ver <see cref="MigratePerThemeSettings"/>).
    /// </summary>
    public const string TransparencySettingKey = "appearance.panelTransparency";
    public const string BlurSettingKey = "appearance.panelBlur";

    /// <summary>
    /// Marca de la migración al modelo nuevo (transparencia absoluta + desenfoque en %).
    /// Ver <see cref="MigrateLegacySettings"/>.
    /// </summary>
    public const string MigrationSettingKey = "appearance.panelAppearanceV2";

    /// <summary>
    /// Marca VIEJA de "el usuario movió los deslizadores": decía que el valor guardado mandaba
    /// sobre el de diseño del tema. Con los ajustes por tema esa marca es la existencia de la clave
    /// del tema (ver <see cref="Resolve"/>); esta queda solo para saber si hay algo que mudar en
    /// <see cref="MigratePerThemeSettings"/>.
    /// </summary>
    public const string TransparencySetSettingKey = "appearance.panelTransparencySet";

    /// <summary>
    /// Marca de la migración a los ajustes POR TEMA (0.3.2). Ver <see cref="MigratePerThemeSettings"/>.
    /// </summary>
    public const string PerThemeMigrationSettingKey = "appearance.panelAppearanceV3";

    /// <summary>Tope del deslizador de transparencia, en puntos porcentuales.</summary>
    public const double MaxTransparencyPercent = 100.0;

    /// <summary>
    /// Clave de la transparencia de UN tema: cada tema guarda la suya, así mover el deslizador en
    /// Marea no le cambia el arranque a Claro. Va indexada por el tema ya resuelto (Sistema →
    /// Claro/Oscuro) y no por la clave del diccionario (Light/Dark), que es compartida por varios.
    /// </summary>
    private static string TransparencyKeyFor(AppTheme theme) => $"{TransparencySettingKey}.{theme}";

    /// <summary>Igual que <see cref="TransparencyKeyFor"/>, para el desenfoque del tema.</summary>
    private static string BlurKeyFor(AppTheme theme) => $"{BlurSettingKey}.{theme}";

    /// <summary>
    /// Los dos números con los que arranca un tema: lo que el usuario guardó PARA ESE TEMA o, si
    /// nunca los movió con ese tema puesto, los de diseño (90 % y 5 % en los de entorno; 0 % y 0 %
    /// en los planos y de degradado, ver <see cref="ThemeCatalog.KindOf"/>).
    ///
    /// Mientras la migración a los ajustes por tema no haya corrido, el ajuste GLOBAL viejo sigue
    /// valiendo como respaldo del tema vigente: lo necesita el splash, que se pinta ANTES de esa
    /// migración (ver <see cref="SavedAppearance"/>).
    /// </summary>
    private static (double Transparency, double Blur) Resolve(ISettingsService settings, AppTheme theme)
    {
        // Los temas PLANOS no tienen nada detrás de los paneles (ni foto ni gradiente):
        // la transparencia y el blur no tienen efecto visible. Siempre 0 % y 0 %,
        // aunque haya un valor viejo guardado para ese tema.
        if (ThemeCatalog.KindOf(theme) == ThemeCatalog.ThemeKind.Flat)
            return (0.0, 0.0);

        bool legacyStillValid = !settings.Get(PerThemeMigrationSettingKey, false)
            && settings.Get(TransparencySetSettingKey, false);

        double transparency = settings.Get(TransparencyKeyFor(theme),
            legacyStillValid
                ? settings.Get(TransparencySettingKey, ThemeCatalog.DefaultPanelTransparency(theme))
                : ThemeCatalog.DefaultPanelTransparency(theme));
        double blur = settings.Get(BlurKeyFor(theme),
            legacyStillValid
                ? settings.Get(BlurSettingKey, ThemeCatalog.DefaultPanelBlur(theme))
                : ThemeCatalog.DefaultPanelBlur(theme));
        return (transparency, blur);
    }

    /// <summary>
    /// Superficies que responden al ajuste: SOLO los paneles —cards, menú lateral, barra de
    /// título (que comparte el color del menú) y chips—; el resto de la paleta (textos, acento,
    /// gráficas) no se toca.
    ///
    /// El FONDO queda fuera a propósito, ni el de las páginas (AppBackgroundBrush) ni la imagen
    /// del tema ni su velo (WindowWallpaperBrush / WindowWallpaperScrimBrush): son la identidad
    /// del tema, no una superficie que se apila encima.
    /// </summary>
    internal static readonly string[] SurfaceKeys =
    {
        "CardBackgroundBrush",
        "NavigationViewDefaultPaneBackground",
        "ChipBackgroundBrush",
        "CoreCardBackgroundBrush",

        // Estas cuatro faltaban en el censo y por eso había cards, grillas y encabezados que se
        // quedaban afuera del ajuste (sin alfa y, sobre todo, SIN VIDRIO): son las superficies que
        // las páginas y los componentes pintan con sus propias claves.
        "ChartBackgroundBrush",        // el área de los gráficos (Estabilidad, filas de planes de Núcleos)
        "SensorGroupFillBrush",        // la grilla de sensores: su fondo y el de su cabecera
        "SensorCategoryFillBrush",     // el encabezado de cada categoría de sensores
        "DisabledCardBackgroundBrush",  // la card en estado deshabilitado (Actualizaciones)

        // Los CUADROS DE TEXTO (TextBox): su template pinta el fondo con estas CUATRO claves —una por
        // estado—, así que sin ellas el cuadro quedaba con el relleno del sistema, opaco, mientras el
        // resto de la UI respondía a los deslizadores (el reporte "los cuadros de texto no se ven
        // afectados por la transparencia ni el blur"). Son superficies como una card: el color lo
        // define cada paleta —AccentOverrides para Claro/Oscuro y OverrideBrushes para los temas
        // propios, ver Themes\<Tema>Theme.cs— y de ahí en adelante mandan el alfa del ajuste y el vidrio.
        "TextControlBackground",
        "TextControlBackgroundPointerOver",
        "TextControlBackgroundFocused",
        "TextControlBackgroundDisabled"

        // El BORDE de las cards NO va en la lista (estuvo un tiempo): esta fórmula es de los
        // RELLENOS —con el desenfoque encendido el alfa va al tope del vidrio (MaxFrostedAlpha)—
        // y aplicada a un filete de 1 px lo convertía en un contorno marcado: #19000000 ->
        // #9E000000, visto en appearance.log. Las cards hoy no llevan contorno: CardStyle tiene
        // BorderThickness 0 y CardStrokeBrush es transparente por diseño en los seis temas y en
        // los diccionarios base (ver Themes/ y App.xaml).
        //
        // Los ESTADOS de una card (CardHoverBrush / CardSelectedBrush) tampoco: son capas que se
        // apilan ENCIMA de la card cuando el mouse entra o la card queda elegida, no superficies con
        // fondo propio. Si entraran, el resaltado del mouse desenfocaría el texto de la card que
        // tiene debajo (el backdrop de una capa incluye lo que la capa tapa).
    };

    private static Window? _window;

    /// <summary>
    /// Se dispara al final de cada aplicación del ajuste, cuando las superficies ya están pintadas.
    /// Lo usan las pocas superficies que se pintan a mano y por lo tanto no pueden seguir al ajuste
    /// por sí solas (ver <see cref="ApplyToColor"/> y ConfiguracionPage.RepaintConfigNavBar).
    /// </summary>
    internal static event Action? Applied;

    /// <summary>Transparencia vigente, en puntos porcentuales (0 = paneles opacos).</summary>
    public static double TransparencyPercent { get; private set; }

    /// <summary>Desenfoque vigente, en puntos porcentuales (0 = sin desenfoque).</summary>
    public static double BlurPercent { get; private set; }

    /// <summary>
    /// Qué camino pintó el vidrio en la última aplicación ("compositor", "acrilico" o "propia", ver
    /// <see cref="GlassTier"/>). Se guarda para detectar el CAMBIO —encender o apagar el vidrio, o el
    /// ajuste del sistema cambiando con la app abierta—, que es lo único que obliga a reescribir el
    /// relleno de las superficies: cada camino usa un pincel de TIPO distinto.
    /// </summary>
    private static string _glassTier = "";

    /// <summary>Alfa que les toca a las superficies con la transparencia vigente.</summary>
    private static double PanelAlpha => AlphaFor(TransparencyPercent);

    /// <summary>
    /// Tope de opacidad de un panel con el desenfoque encendido. Un panel completamente opaco taparía
    /// lo que tiene DETRÁS —el parche desenfocado en el camino de respaldo (ver <see cref="PanelBlurLayer"/>)
    /// y, con el vidrio de la plataforma, el propio fondo desenfocado por el acrílico— así que el
    /// deslizador de blur parecería no hacer nada: un vidrio esmerilado necesita dejar pasar algo de luz
    /// para que se vea lo que tiene atrás. Con el acrílico este tope es el `TintOpacity` del panel (ver
    /// <see cref="SurfaceFill"/>).
    /// </summary>
    private const double MaxFrostedAlpha = 0.62;

    /// <summary>
    /// Alfa de las superficies para un valor de transparencia dado. Es LA regla del ajuste: la usan
    /// el relleno de las superficies y la autocomprobación de desarrollo, para que no puedan
    /// separarse.
    /// </summary>
    private static double AlphaFor(double transparencyPercent) => AlphaFor(transparencyPercent, BlurPercent);

    /// <summary>
    /// Lo mismo, con la transparencia y el desenfoque EXPLÍCITOS. Lo necesita quien pide el color
    /// antes de que el ajuste esté aplicado: el splash se construye antes de que OnThemeApplied lea
    /// los ajustes guardados, así que el estado vigente todavía son dos ceros (ver
    /// <see cref="SavedAppearance"/> y SplashWindow.ApplyBackdrop).
    /// </summary>
    private static double AlphaFor(double transparencyPercent, double blurPercent)
    {
        // SIN PISO: 100 % de transparencia es 100 % —la superficie queda sin relleno y solo se ve
        // el fondo que tiene detrás— y el ajuste recorre la escala entera, de 0 a 1. Antes había un
        // piso de 0,25 pensado para que el texto de las cards no quedara flotando sobre la foto,
        // pero el precio era que el tope del deslizador no hacía lo que decía: en 100 % los paneles
        // seguían viéndose. Lo que sostiene la legibilidad ahora es el velo del tema
        // (WindowWallpaperScrimBrush) más el desenfoque, y las cards siguen delimitadas por su
        // borde, que no es una superficie de este ajuste (ver SurfaceKeys).
        double alpha = Math.Clamp(1.0 - transparencyPercent / 100.0, 0.0, 1.0);
        return blurPercent > 0.0 ? Math.Min(alpha, MaxFrostedAlpha) : alpha;
    }

    /// <summary>
    /// La transparencia y el desenfoque GUARDADOS para el tema vigente, resueltos sin ventana y sin
    /// tema aplicado: mismo criterio que <see cref="OnThemeApplied"/> (el valor por tema manda; si no,
    /// el de diseño). Lo usa el splash, que se pinta antes de que ese camino corra.
    /// </summary>
    public static (double Transparency, double Blur) SavedAppearance()
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            return Resolve(settings, EffectiveTheme());
        }
        catch
        {
            return (0.0, 0.0);
        }
    }

    /// <summary>
    /// Color con el alfa de paneles que corresponde a una transparencia y un desenfoque dados. Es la
    /// sobrecarga de <see cref="ApplyToColor(Windows.UI.Color)"/> para los casos en los que el ajuste
    /// todavía no está aplicado (ver <see cref="SavedAppearance"/>).
    /// </summary>
    public static Windows.UI.Color ApplyToColor(Windows.UI.Color color, double transparencyPercent, double blurPercent)
        => Windows.UI.Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(255 * AlphaFor(transparencyPercent, blurPercent)), 0, 255), color.R, color.G, color.B);

    /// <summary>
    /// Enlaza la ventana principal.
    /// Llamarlo una vez, al construir la ventana. NO aplica los ajustes: eso
    /// lo hace <see cref="OnThemeApplied"/> cuando el tema queda aplicado.
    /// </summary>
    public static void Attach(Window window)
    {
        _window = window;

        // Si el tema ya se había aplicado antes de que la ventana existiera (tema
        // aplicado temprano), acá se termina de aplicar lo que depende de la ventana:
        // los ajustes que escriben en los diccionarios de recursos.
        if (_loaded) SetBlurPercent(BlurPercent, persist: false);

        // Lo mismo vale para la derivación de superficies desde la imagen de fondo
        // (ver Wallpaper.RefreshDerivedPanels): sin ventana el resultado se descarta,
        // así que si el tema llegó a aplicarse temprano, corre ahora con la ventana ya
        // enlazada. No-op en los casos donde ya se hizo.
        Wallpaper.RefreshDerivedPanels(EffectiveTheme());
    }

    /// <summary>
    /// La llama <see cref="ThemeApplier.ApplyTheme"/> al terminar de aplicar un tema. Es el
    /// momento correcto para la apariencia del usuario: la paleta del tema ya está en los
    /// diccionarios, así que los colores de fábrica que se usan como base del cálculo son los
    /// del tema elegido. La primera vez lee los ajustes guardados (para que la ventana abra ya
    /// con ellos, sin pasar por Configuración) y las siguientes los vuelve a aplicar sobre el
    /// tema nuevo.
    /// </summary>
    public static void OnThemeApplied()
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            var theme = EffectiveTheme();

            if (!_loaded)
            {
                _loaded = true;
                MigrateLegacySettings(settings, theme);

                MigratePerThemeSettings(settings, theme);

                // Cada tema arranca con SUS números: lo que el usuario guardó para este tema o, si
                // nunca los movió con él puesto, los de diseño (entorno 90 % y 5 %; planos y
                // degradado 0 % y 0 %). Es lo que hace que elegir un tema de entorno ya deje ver la
                // foto sin tocar nada, y que uno plano arranque opaco y sin vidrio.
                var (transparency, blur) = Resolve(settings, theme);
                SetTransparencyPercent(transparency, persist: false);
                SetBlurPercent(blur, persist: false);

                Diag($"apariencia del tema {theme} ({ThemeCatalog.KindOf(theme)}): transparencia {transparency:0} % " +
                    (settings.Contains(TransparencyKeyFor(theme))
                        ? "(elegida para este tema)"
                        : $"(de diseño: el tema declara {ThemeCatalog.DefaultPanelTransparency(theme):0} %)") +
                    $", desenfoque {blur:0} % " +
                    (settings.Contains(BlurKeyFor(theme))
                        ? "(elegido para este tema)"
                        : $"(de diseño: el tema declara {ThemeCatalog.DefaultPanelBlur(theme):0} %)"));
                _appliedTheme = theme;
            }
            else if (_appliedTheme != theme)
            {
                // El tema cambió sin reiniciar el proceso: cada tema arranca con SUS números, así que
                // se vuelven a resolver en vez de arrastrar los del anterior.
                var (transparency, blur) = Resolve(settings, theme);
                SetTransparencyPercent(transparency, persist: false);
                SetBlurPercent(blur, persist: false);
                _appliedTheme = theme;
                Diag($"cambio de tema a {theme} ({ThemeCatalog.KindOf(theme)}): transparencia {transparency:0} % " +
                    $"y desenfoque {blur:0} %, los del tema nuevo");
            }
            else
            {
                SetTransparencyPercent(TransparencyPercent, persist: false);
                SetBlurPercent(BlurPercent, persist: false);
            }

            // El fondo elegido (la foto del tema, un degradado o la imagen del usuario) se
            // aplica al final: pisa la imagen del tema y necesita que la paleta ya esté puesta.
            Wallpaper.Apply(theme);
        }
        catch (Exception ex)
        {
            Log($"PanelAppearance.OnThemeApplied: {ex.Message}");
        }

        // Sin ventana todavía, SetTransparencyPercent solo recuerda el valor: la comprobación es
        // no-op y queda pendiente para la próxima aplicación de tema (que ya tendrá ventana).
#if DEBUG
        VerifyRoundTrip();
#endif
    }

    /// <summary>Los ajustes ya se leyeron y aplicaron al menos una vez en este proceso.</summary>
    private static bool _loaded;

    /// <summary>
    /// Tema al que corresponden los valores vigentes: si <see cref="OnThemeApplied"/> llega con otro
    /// —cambio de tema sin reinicio— se vuelven a resolver los ajustes de ESE tema, porque los
    /// números son por tema (ver <see cref="Resolve"/>).
    /// </summary>
    private static AppTheme? _appliedTheme;

    /// <summary>
    /// Traduce los ajustes de la versión anterior a la escala nueva, UNA vez por instalación.
    ///
    /// El ajuste viejo multiplicaba el alfa que el tema ya traía: su 0 % era "como lo diseñó el
    /// tema", que en los temas con fondo ya era translúcido. Con la escala nueva ese 0 % significa
    /// "paneles opacos", así que heredarlo cambiaría el aspecto de golpe y sin que nadie lo haya
    /// pedido: la transparencia vieja se descarta (queda manda la de diseño de cada tema, ver
    /// TransparencySetSettingKey) y el desenfoque, que era un switch, pasa a 60 % si estaba
    /// prendido.
    /// </summary>
    private static void MigrateLegacySettings(ISettingsService settings, AppTheme theme)
    {
        if (settings.Get(MigrationSettingKey, false)) return;

        double blur = settings.Get("appearance.windowBlur", false) ? 60.0 : 0.0;
        Save(BlurSettingKey, blur);
        Save(MigrationSettingKey, true);
        Diag($"migración al ajuste nuevo de apariencia: desenfoque {blur:0} % (el tema {theme} arranca con {ThemeCatalog.DefaultPanelTransparency(theme):0} % de transparencia)");
    }

    /// <summary>
    /// Muda los DOS ajustes globales (una transparencia y un desenfoque para toda la app) a las
    /// claves POR TEMA, UNA vez por instalación.
    ///
    /// Qué se muda: solo si el usuario los había elegido a mano, y solo al tema que tenía puesto
    /// —es el único del que se puede decir que ese valor era "el suyo"—. Los demás temas arrancan
    /// con los de diseño (entorno 90 % y 5 %; planos y degradado 0 % y 0 %). Después las claves
    /// globales se borran, para que no quede un valor viejo esperando a que alguien lo lea.
    /// </summary>
    private static void MigratePerThemeSettings(ISettingsService settings, AppTheme theme)
    {
        try
        {
            if (settings.Get(PerThemeMigrationSettingKey, false)) return;

            if (settings.Get(TransparencySetSettingKey, false))
            {
                double transparency = settings.Get(TransparencySettingKey, ThemeCatalog.DefaultPanelTransparency(theme));
                double blur = settings.Get(BlurSettingKey, ThemeCatalog.DefaultPanelBlur(theme));
                Save(TransparencyKeyFor(theme), transparency);
                Save(BlurKeyFor(theme), blur);
                Diag($"migración a ajustes por tema: {theme} hereda transparencia {transparency:0} % y desenfoque {blur:0} %; los demás temas arrancan con los suyos");
            }
            else
            {
                Diag("migración a ajustes por tema: no había valores elegidos a mano — cada tema arranca con los suyos (entorno 90 % y 5 %; planos y degradado 0 % y 0 %)");
            }

            settings.Remove(TransparencySettingKey);
            settings.Remove(BlurSettingKey);
            settings.Remove(TransparencySetSettingKey);
            Save(PerThemeMigrationSettingKey, true);
        }
        catch (Exception ex)
        {
            Log($"PanelAppearance.MigratePerThemeSettings: {ex.Message}");
        }
    }

    /// <summary>
    /// Aplica la transparencia de los paneles. <paramref name="percent"/> va de 0 (paneles
    /// opacos, con el color del tema) a <see cref="MaxTransparencyPercent"/>.
    /// Es idempotente: siempre se recalcula desde el color de la definición del tema, así que
    /// mover el deslizador no compone los alfas entre sí.
    /// </summary>
    public static void SetTransparencyPercent(double percent, bool persist = true)
    {
        try
        {
            // Un valor no finito (NaN/Infinity) daría un alfa 0 —todo transparente—: se ignora.
            if (!double.IsFinite(percent)) percent = 0.0;
            double value = Math.Clamp(percent, 0.0, MaxTransparencyPercent);

            // El ajuste se recuerda ANTES de cualquier guarda: el valor vigente es el que puso el
            // usuario (o el que venía guardado), y tiene que sobrevivir aunque la escritura se
            // saltee. Si se asignara después, una aplicación temprana dejaría el valor en 0 y la
            // siguiente pasada —ya con ventana— escribiría 0 % encima de lo guardado: el ajuste se
            // perdía y parecía que "no funcionaba".
            TransparencyPercent = value;

            // Antes de que exista la ventana no se escribe nada: ThemePalettes.Initialize()
            // captura los colores base de los diccionarios justo antes de aplicar el tema
            // guardado, y una escritura temprana contaminaría ese snapshot —el mismo tipo de bug
            // que este archivo documenta—. Cuando la ventana exista, el hook de tema vuelve a
            // llamar con este valor vigente.
            if (_window == null && App.MainWindowInstance == null)
            {
                Diag($"transparencia {value:0} % sin ventana todavía: queda pendiente de aplicar");
                return;
            }

            var theme = EffectiveTheme();
            var themeKey = ThemeKeyFor(theme);
            int applied = 0;

            Diag($"tema={theme} temaBase={themeKey} transparencia={value:0}% alfaPaneles={PanelAlpha:0.00}");
            foreach (var key in SurfaceKeys)
            {
                if (!ThemePalettes.TryGetFactoryColor(theme, key, out var factory))
                {
                    Diag($"    {key}: sin color de fábrica (no está en el tema) — no se toca");
                    continue;
                }

                // Una superficie que el tema define TRANSPARENTE (alfa 0) no es un panel: es una
                // capa que existe para dejar ver lo que hay detrás. Pintarla con el alfa del
                // ajuste la convertiría en un bloque de color (el caso típico: el contenedor de
                // contenido del NavigationView, que en los temas con fondo tiene que seguir
                // dejando ver la foto).
                if (factory.A == 0)
                {
                    Diag($"    {key}: transparente por diseño en este tema — no se toca");
                    continue;
                }

                var color = WithPanelAlpha(factory);

                bool written = WriteSurfaceBrush(key, themeKey, color);
                if (written)
                {
                    applied++;
                    // Los pinceles live (cards y filas creadas en code-behind) se re-resuelven de
                    // a una clave: Refresh() completo apagaría además los pinceles de claves que no
                    // viven en los diccionarios de la app. Se le pasa la clave de tema ya resuelta
                    // (no root.ActualTheme, que en el arranque no está listo).
                    ThemeBrushes.Resync(key, themeKey);
                }

                Diag($"    {key}: {ThemePaint.Hex(factory)} -> {ThemePaint.Hex(color)}{(written ? "" : " (no se pudo escribir)")}");
            }

            // El relleno de cada superficie ya quedó escrito con lo que toca —vidrio del compositor,
            // acrílico con el vidrio de la plataforma encendido, sólido con el desenfoque apagado: ver
            // SurfaceFill—. Acá solo queda poner la capa propia al día: con el vidrio queda colapsada
            // (sin trabajo por frame) y en el camino de respaldo rehace sus parches.
            PanelBlurLayer.Invalidate();

            // Las superficies que la UI creada en código tiene sujetas de ANTES (cards de grillas,
            // de sensores y de páginas armadas en code-behind) no vuelven a resolver el recurso: se
            // repintan en su instancia estable (ver ThemeBrushes.GetSurface).
            // Los elementos que quedaron con una instancia vieja (el árbol creado antes del cambio de
            // camino) se repintan ahora: sin esto, una página/barra que ya existía cuando el vidrio
            // entró en juego se quedaba con el relleno plano para toda la sesión.
            if (_replacedSurfaces.Count > 0) PanelBlurLayer.SwapReplacedSurfaces(_replacedSurfaces);

            int live = ThemeBrushes.LiveSurfaceCount;
            if (live > 0) Diag($"    superficies en código repintadas: {live} ({string.Join(", ", ThemeBrushes.LiveSurfaceKeys)})");

            // Y la auditoría de superficies: por cada card, grilla o navbar interna que el censo
            // reconoce, qué relleno tiene de verdad y si algo opaco la tapa (ver
            // PanelBlurLayer.AuditSurfaces). Es la forma de contestar "esta card no tiene desenfoque"
            // desde el log, sin mirar la pantalla.
            PanelBlurLayer.AuditSurfaces($"apariencia {value:0} %");

            // Las superficies que se pintan A MANO (el navbar interno de Configuración, ver
            // RepaintConfigNavBar) no pasan por el diccionario ni por el árbol de pinceles: quien las
            // tiene se suscribe acá y se repinta con el ajuste ya aplicado.
            Applied?.Invoke();

            // El navbar, la barra de título y el borde de la ventana se pintan a mano (para que el
            // NavigationView no cachee el pincel del tema): se refrescan en su ventana.
            App.MainWindowInstance?.RefreshPanelColors();

            Log($"Apariencia: transparencia {value:0} % (alfa {PanelAlpha:0.00}) sobre {applied} superficies del tema {theme}.");
            // Se guarda PARA ESTE TEMA: la existencia de la clave es la marca de que el usuario lo
            // movió con él puesto (ver Resolve). Mover el deslizador en un tema no toca a los demás.
            if (persist) Save(TransparencyKeyFor(theme), value);
        }
        catch (Exception ex)
        {
            Log($"PanelAppearance.SetTransparencyPercent: {ex.Message}");
        }
    }

    /// <summary>
    /// Aplica el desenfoque de los paneles: 0 % = los paneles se ven sobre la foto NÍTIDA; de 1 %
    /// en adelante cada panel se lee como un vidrio esmerilado y lo que no está tapado por ningún
    /// panel sigue nítido. NADA se desenfoca "para la ventana entera": la superficie de fondo del
    /// tema queda afuera a propósito.
    ///
    /// QUIÉN PONE EL VIDRIO, en orden de preferencia:
    ///
    /// 1. EL COMPOSITOR (el caso normal): el relleno de cada superficie pasa a ser un
    ///    <see cref="GlassBlurBrush"/> —un grafo de composición `backdrop -> desenfoque -> tinte`—
    ///    que desenfoca lo que el panel tiene DETRÁS dentro de la misma ventana y compone encima el
    ///    relleno del tema. El desenfoque lo resuelve el motor por elemento, así que va pegado a su
    ///    panel por construcción: no hay nada que seguir al scrollear, ni nada que se pueda
    ///    desincronizar o desaparecer, y no hay copia que recalcular. El RADIO es nuestro (el
    ///    deslizador es una intensidad de verdad) y no depende de ningún ajuste de Windows.
    ///
    /// 2. LA PLATAFORMA, cuando el compositor no puede (ver <see cref="PlatformGlassInCharge"/> y
    ///    <see cref="SurfaceFill"/>): un `AcrylicBrush` —el `backdrop-filter` de Windows— desenfoca y
    ///    tiñe lo que el panel tiene DETRÁS. La INTENSIDAD la fija el sistema (su radio no es un
    ///    ajuste nuestro), así que ahí el deslizador enciende y apaga el vidrio.
    ///
    /// 3. LA APP, cuando Windows no puede renderizar el acrílico (el usuario apagó los "Efectos de
    ///    transparencia": ver <see cref="PlatformGlass"/>) y tampoco se pudo armar el compositor:
    ///    detrás de cada panel se recorta un parche de la foto desenfocada por la app (ver
    ///    <see cref="PanelBlurLayer"/>), y ahí el número SÍ es la intensidad —elige el radio, de 1 a
    ///    16 px, ver <see cref="PanelBlurAlgorithm.RadiusForPercent"/>, y la copia se recalcula desde
    ///    los píxeles ya decodificados sin volver a leer el archivo—.
    ///
    /// En los dos casos esto es POR TEMA, y en ninguno se rehace trabajo por tick del deslizador: lo
    /// único que reescribe las superficies es el CAMBIO de camino (encender o apagar el vidrio), que
    /// pasa una vez.
    /// </summary>
    public static void SetBlurPercent(double percent, bool persist = true)
    {
        try
        {
            if (!double.IsFinite(percent)) percent = 0.0;
            double value = Math.Clamp(percent, 0.0, 100.0);

            // El ajuste del sistema se relee: el usuario puede haber apagado los "Efectos de
            // transparencia" con la app abierta, y con eso el acrílico deja de renderizar.
            PlatformGlass.Recheck();

            bool wasGlass = BlurPercent > 0.0;
            BlurPercent = value;

            bool native = PlatformGlassInCharge;

            // El CHROME va por la copia COMPARTIDA aunque el vidrio lo pinte el motor (ver
            // ChromeSharedGlass): sus piezas van pegadas y el vidrio por elemento corta la foto en seco
            // en cada junta. El resto de los paneles sigue con el vidrio del motor, así que la capa se
            // pone en MODO CHROME: recorta solo esas piezas y no un segundo vidrio encima de todo.
            bool chrome = HasBackdropToBlur;
            PanelBlurLayer.SetChromeOnly(native && chrome);

            // La capa propia queda para el camino de respaldo —todos los paneles— y para el chrome
            // cuando el vidrio lo pinta el motor: fuera de esos dos casos no debe quedar NI ENCENDIDA,
            // porque sería un segundo desenfoque encima del bueno.
            PanelBlurLayer.SetEnabled(value > 0.0 && (!native || chrome));

            // La copia desenfocada de la foto la necesita el camino de respaldo y el chrome (que la usa
            // para que sus juntas no tengan corte).
            if (!native || chrome) PanelBlur.SetStrength(value);

            // Cambiar de camino es lo único que cambia el TIPO de relleno (sólido, vidrio del
            // compositor o acrílico: ver <see cref="SurfaceFill"/>), así que es lo único que obliga a
            // reescribir las superficies —y a repintar el menú, la barra de título y el borde, que van
            // a mano—. Una escritura, no una por tick: arrastrar el deslizador dentro de 1..100 no
            // reescribe nada.
            string tier = GlassTier;
            bool switched = (wasGlass != value > 0.0) || (tier != _glassTier);
            _glassTier = tier;
            if (switched) SetTransparencyPercent(TransparencyPercent, persist: false);

            // El RADIO, cuando lo pone el compositor: es una propiedad del grafo, así que mover el
            // deslizador es UNA escritura por pincel —sin reescribir superficies ni re-resolver nada— y
            // el cambio se ve al instante. (Con el acrílico no hay radio nuestro: el del sistema es
            // fijo; con la capa propia, el radio lo lleva la copia desenfocada, ver PanelBlur.)
            if (!switched && CompositorGlassInCharge) GlassBlurBrush.SetRadiusForAll(GlassRadius);

            // Igual que la transparencia: el desenfoque es POR TEMA.
            if (persist) Save(BlurKeyFor(EffectiveTheme()), value);

            // Quién pinta el chrome queda asentado en el log: es lo que explica que la barra de título,
            // el menú y las barras internas ya no muestren la foto cortada en cada junta.
            string chromeNote = ChromeSharedGlass
                ? " — el CHROME (barra de título, menú y barras internas) va con la copia COMPARTIDA: una sola imagen desenfocada detrás de las piezas pegadas"
                : native && chrome
                    ? " — el chrome espera la copia COMPARTIDA (todavía no está publicada)"
                    : "";

            Diag(tier switch
            {
                "compositor" => $"desenfoque={value:0} % — vidrio del COMPOSITOR: backdrop desenfocado {GlassRadius:0} px " +
                                $"por elemento, con el relleno del panel compuesto encima (no hay nada que seguir al scrollear){chromeNote}",
                "acrilico" => $"desenfoque={value:0} % — vidrio de la plataforma: el acrílico in-app desenfoca cada panel " +
                              "(radio del sistema, sin copia propia ni trabajo por frame)",
                _ => value > 0
                    ? $"desenfoque={value:0} % (capa propia: radio {PanelBlurAlgorithm.RadiusForPercent(value)} px, " +
                      $"copia {(PanelBlur.IsReady ? "lista" : "todavía no lista")}; vidrio del compositor " +
                      $"{(GlassBlurBrush.Available ? "disponible" : "NO se pudo armar")}; efectos de transparencia " +
                      $"{(PlatformGlass.Available ? "encendidos" : "APAGADOS: el acrílico no renderiza")})"
                    : "desenfoque=0 % (relleno sólido por panel)"
            });
        }
        catch (Exception ex)
        {
            Log($"PanelAppearance.SetBlurPercent: {ex.Message}");
        }
    }

    /// <summary>
    /// Persiste los ajustes vigentes (la página guarda con retardo al arrastrar). Es POR TEMA: los
    /// dos números van a las claves del tema que está puesto, y desde ahí ese tema arranca con ellos
    /// (los demás siguen con los suyos: ver <see cref="Resolve"/>).
    /// </summary>
    public static void SaveCurrent()
    {
        var theme = EffectiveTheme();
        Save(TransparencyKeyFor(theme), TransparencyPercent);
        Save(BlurKeyFor(theme), BlurPercent);
    }

    /// <summary>
    /// Alfa de los paneles aplicado al color de fábrica de una superficie. Es LA fórmula del
    /// ajuste: el alfa lo fija el deslizador (absoluto), nunca el color que hay hoy en el
    /// diccionario; del tema se conserva el color.
    /// </summary>
    private static Windows.UI.Color WithPanelAlpha(Windows.UI.Color factory)
        => Windows.UI.Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(255 * AlphaFor(TransparencyPercent)), 0, 255), factory.R, factory.G, factory.B);

    /// <summary>
    /// Devuelve el color con el alfa de los paneles aplicado. Lo usan los colores que MainWindow
    /// fija a mano (navbar, barra de título y borde) para que el ajuste también los alcance: sin
    /// esto, el panel del menú quedaría siempre opaco aunque las cards sean translúcidas.
    /// </summary>
    public static Windows.UI.Color ApplyToColor(Windows.UI.Color color)
        => Windows.UI.Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(255 * AlphaFor(TransparencyPercent)), 0, 255), color.R, color.G, color.B);

    /// <summary>
    /// Compone un color translúcido sobre el fondo del tema y lo devuelve OPACO: es el tono que
    /// corresponde pintar en el borde de la ventana, porque DWM ignora el alfa (si se le pasa el
    /// color translúcido tal cual, el filete de las esquinas queda de un tono que no coincide con
    /// el panel).
    /// </summary>
    public static Windows.UI.Color ComposeOpaque(Windows.UI.Color color)
    {
        try
        {
            if (color.A == 255) return color;
            var baseColor = BackdropBaseColor();
            double a = color.A / 255.0;
            return Windows.UI.Color.FromArgb(255,
                (byte)Math.Clamp((int)Math.Round(color.R * a + baseColor.R * (1 - a)), 0, 255),
                (byte)Math.Clamp((int)Math.Round(color.G * a + baseColor.G * (1 - a)), 0, 255),
                (byte)Math.Clamp((int)Math.Round(color.B * a + baseColor.B * (1 - a)), 0, 255));
        }
        catch
        {
            return Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);
        }
    }

    /// <summary>Clave de tema ("Light"/"Dark") de un tema ya resuelto.</summary>
    internal static string ThemeKeyFor(AppTheme theme)
        => ThemePalettes.BaseThemeFor(theme) == AppTheme.Light ? "Light" : "Dark";

    /// <summary>
    /// Tema efectivo (el guardado, con "Sistema" ya resuelto). A propósito NO se usa
    /// root.ActualTheme: durante el arranque todavía no está resuelto y devuelve oscuro
    /// aunque el tema guardado sea claro.
    /// </summary>
    internal static AppTheme EffectiveTheme()
    {
        try
        {
            var themeService = App.Services.GetRequiredService<IThemeService>();
            return themeService.CurrentTheme == AppTheme.SystemDefault
                ? App.Services.GetRequiredService<IThemeApplier>().GetSystemTheme()
                : themeService.CurrentTheme;
        }
        catch
        {
            return AppTheme.Dark;
        }
    }

    /// <summary>
    /// Pincel vigente de una superficie (el del diccionario del tema activo). Lo usa
    /// <see cref="PanelBlurLayer"/> para reconocer los paneles recorriendo el árbol de la ventana.
    /// </summary>
    internal static Brush? SurfaceBrush(string key)
    {
        try
        {
            var dict = FindThemeDictionary(key, ThemeKeyFor(EffectiveTheme()));
            return dict != null && dict.TryGetValue(key, out var value) ? value as Brush : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ¿El VIDRIO lo pinta el MOTOR? Hace falta que el ajuste pida desenfoque y que haya una foto
    /// detrás: en los temas planos y en el fondo degradado los paneles están sobre un color liso y no
    /// hay nada que desenfocar (el deslizador tampoco hacía nada ahí).
    /// </summary>
    internal static bool PlatformGlassInCharge => CompositorGlassInCharge || AcrylicGlassInCharge;

    /// <summary>
    /// EL VIDRIO DEL COMPOSITOR (ver <see cref="GlassBlurBrush"/>), que es el camino PREFERIDO: el
    /// motor desenfoca el fondo que cada panel tiene detrás, con NUESTRO radio, y compone encima el
    /// relleno del panel. No depende de ningún ajuste del sistema —el acrílico se apaga con los
    /// "Efectos de transparencia", este no— y el vidrio va pegado a su elemento por construcción, así
    /// que no hay nada que seguir al scrollear.
    /// </summary>
    internal static bool CompositorGlassInCharge => GlassBlurBrush.Available && HasBackdropToBlur;

    /// <summary>
    /// El acrílico in-app de WinUI (ver <see cref="PlatformGlass"/>): respaldo del compositor, y solo
    /// en las máquinas donde Windows pueda renderizarlo.
    /// </summary>
    internal static bool AcrylicGlassInCharge => PlatformGlass.Available && HasBackdropToBlur;

    /// <summary>
    /// Hay con qué desenfocar y algo que desenfocar: el ajuste pide vidrio y el tema trae foto detrás
    /// de los paneles. Es la condición de la que dependen las dos copias del vidrio (la del motor y la
    /// COMPARTIDA) y la consultan también <see cref="PanelBlur"/> —para saber si tiene que armar su
    /// copia— y <see cref="PanelBlurLayer"/> (modo chrome).
    /// </summary>
    internal static bool SharedGlassNeeded => BlurPercent > 0.0 && HasBackdropToBlur;

    /// <summary>
    /// El CHROME —barra de título, panel del menú y barras de pestañas internas de cada página— va con
    /// el vidrio COMPARTIDO de la app en vez del vidrio por elemento del compositor: una sola copia
    /// desenfocada de la foto, anclada a la ventana, recortada por panel (ver <see cref="PanelBlurLayer"/>).
    ///
    /// POR QUÉ: esas tres piezas van PEGADAS unas a otras y el vidrio por elemento desenfoca el fondo de
    /// cada una por separado, con el borde recortado (ver GlassBlurBrush, <c>EffectBorderMode.Hard</c>):
    /// en cada junta la foto quedaba cortada en seco —el reporte "entre la pestaña, el navbar principal
    /// de la app y el navbar del componente se ve recortado"—. Con una sola copia anclada a la ventana
    /// la imagen es la MISMA de los dos lados de la junta, así que las tres se leen como un solo
    /// componente. Necesita la copia ya publicada: hasta entonces el chrome usa el relleno de siempre y
    /// <see cref="OnSharedGlassReady"/> repinta cuando la copia aparece.
    /// </summary>
    internal static bool ChromeSharedGlass => PlatformGlassInCharge && SharedGlassNeeded && PanelBlur.IsReady;

    /// <summary>
    /// ¿Hay algo detrás de los paneles que valga la pena desenfocar? (el ajuste pide vidrio y el tema
    /// trae foto).
    /// </summary>
    private static bool HasBackdropToBlur
    {
        get
        {
            if (BlurPercent <= 0.0) return false;
            try { return Wallpaper.ActiveImagePath(EffectiveTheme()) != null; }
            catch { return false; }
        }
    }

    /// <summary>Techo del radio del compositor, en píxeles de pantalla (ver <see cref="GlassRadius"/>).</summary>
    private const double MaxCompositorRadius = 24.0;

    /// <summary>
    /// Radio del desenfoque del COMPOSITOR, en píxeles DE PANTALLA, para el porcentaje vigente.
    ///
    /// POR QUÉ NO SALE DIRECTO: el mismo porcentaje son DOS números distintos según el camino.
    /// <see cref="PanelBlurAlgorithm.RadiusForPercent"/> da un radio de 1 a 16 en píxeles de LA COPIA
    /// —la foto reducida a <see cref="PanelBlur.PixelWidth"/> de ancho, que después se estira al ancho de
    /// la ventana—, así que ahí 1 px son varios de pantalla. El compositor, en cambio, desenfoca el
    /// fondo del elemento a TAMAÑO REAL: con el número crudo, el mismo 5 % se veía como 1 px (nada) en
    /// las cards y los paneles —que van con el compositor— y como ~4 px en el chrome —que va con la
    /// copia—. Ese era el reporte "el blur a 5 % no afecta a los paneles y cards": las dos mitades del
    /// mismo ajuste estaban en unidades distintas.
    ///
    /// EL TOPE ES POR COSTO: el vidrio del compositor es un gaussiano por elemento a tamaño real, así
    /// que su precio crece con el radio (la copia no: trabaja sobre la foto reducida).
    /// <see cref="MaxCompositorRadius"/> px ya es un vidrio esmerilado de verdad; más que eso no se
    /// distingue y sí se paga.
    /// </summary>
    private static float GlassRadius
    {
        get
        {
            double copyWidth = PanelBlur.PixelWidth;
            double windowWidth = (_window?.Content as Microsoft.UI.Xaml.FrameworkElement)?.ActualWidth ?? 0;
            double scale = copyWidth > 0 && windowWidth > 1 ? windowWidth / copyWidth : 1.0;

            double radius = PanelBlurAlgorithm.RadiusForPercent(BlurPercent) * scale;
            return (float)Math.Clamp(radius, 1.0, MaxCompositorRadius);
        }
    }

    /// <summary>
    /// El radio que le toca a una superficie creada EN CÓDIGO (ver <see cref="ThemeBrushes.GetSurface"/>).
    ///
    /// Es 0 cuando el vidrio no manda —desenfoque apagado, tema sin foto o máquina sin compositor— y
    /// 0 significa RELLENO PLANO para <see cref="GlassBlurBrush"/>: el pincel es el mismo en los dos
    /// casos, así que encender y apagar el desenfoque no cambia de instancia (que es lo que la UI
    /// creada en código NO puede absorber: no vuelve a resolver el {ThemeResource}). Ojo con
    /// <see cref="GlassRadius"/>, que nunca baja de 1: ese es el radio del GRAFO, y con el vidrio
    /// apagado el grafo ni existe.
    /// </summary>
    internal static float SurfaceRadiusPx => CompositorGlassInCharge ? GlassRadius : 0f;

    /// <summary>Qué camino pinta el vidrio: el compositor, el acrílico del sistema o la capa propia de la app.</summary>
    private static string GlassTier
        => CompositorGlassInCharge ? "compositor" : AcrylicGlassInCharge ? "acrilico" : "propia";

    /// <summary>
    /// El relleno de una superficie para el color dado: ACRÍLICO cuando el vidrio está encendido, o el
    /// sólido de siempre (con el alfa del ajuste) cuando no. Reutiliza la instancia que ya está sujeta
    /// por los elementos —mutándola en sitio, que es lo que hace que repinten al instante sin depender
    /// de que WinUI vuelva a resolver el {ThemeResource}— y solo crea otra cuando el TIPO tiene que
    /// cambiar, o sea al encender o apagar el vidrio.
    ///
    /// El TINTE es el color del panel (su alfa pasa a <see cref="AcrylicBrush.TintOpacity"/>) y el
    /// FALLBACK ese mismo color translúcido: si Windows no puede renderizar el acrílico —ahorro de
    /// batería, hardware flojo, o el usuario apagando los efectos de transparencia después de que la
    /// app decidió— la superficie queda como con el desenfoque apagado, nunca rota.
    /// </summary>
    internal static Brush SurfaceFill(Brush? current, Windows.UI.Color color)
    {
        // EL VIDRIO ES EL MISMO PINCEL CON RADIO 0 QUE CON RADIO N (ver GlassBlurBrush: radio 0 es
        // relleno plano, sin efecto colgado), así que una superficie que YA es de vidrio conserva su
        // instancia y solo cambia el radio —la misma regla que <see cref="SurfaceRadiusPx"/>, que es
        // la que usan las cards creadas en código—.
        //
        // POR QUÉ NO ALCANZA CON REEMPLAZAR LA INSTANCIA: al apagar el desenfoque el diccionario
        // recibía un sólido NUEVO, y los elementos que ya tenían puesto el vidrio no vuelven a
        // resolver el {ThemeResource}: el repintado del árbol (ver
        // PanelBlurLayer.SwapReplacedSurfaces) no los alcanzó —en el log no quedó ni una línea de
        // repintado— y la página abierta seguía mostrando el desenfoque hasta salir y volver a
        // entrar. Es el reporte "pongo 0 % y sigue habiendo blur". Con la instancia conservada, el
        // cambio es una escritura y se ve al instante (como el navbar, que se repinta a mano).
        if (GlassBlurBrush.Available)
        {
            // Ya es de vidrio: se conserva la instancia y solo cambia el radio (0 = plano).
            if (current is GlassBlurBrush glassInstance)
            {
                glassInstance.SetRadius(SurfaceRadiusPx);
                glassInstance.SetTint(color);
                return glassInstance;
            }

            // Todavía no lo era (arranque con el desenfoque apagado, o una instancia sólida de antes):
            // nace COMO VIDRIO EN MODO PLANO, el mismo criterio que las cards creadas en código (ver
            // ThemeBrushes). Así, en una máquina donde el grafo se puede armar, el TIPO de relleno de
            // una superficie no cambia nunca y encender el desenfoque tampoco reemplaza la instancia:
            // solo sube el radio, y el cambio se ve al instante.
            if (GlassBlurBrush.TryCreate(SurfaceRadiusPx, color) is { } flatGlass) return flatGlass;
        }

        if (PlatformGlassInCharge)
        {
            // 1) EL VIDRIO DEL COMPOSITOR: el fondo que el panel tiene detrás, desenfocado por el
            //    motor con nuestro radio, y el relleno del panel compuesto encima (ver GlassBlurBrush).
            if (CompositorGlassInCharge)
            {
                if (current is GlassBlurBrush glass)
                {
                    glass.SetRadius(GlassRadius);
                    glass.SetTint(color);
                    return glass;
                }

                if (GlassBlurBrush.TryCreate(GlassRadius, color) is { } created) return created;
            }

            // 2) El acrílico in-app, en las máquinas donde Windows pueda renderizarlo.
            if (PlatformGlass.Available)
            {
                var tint = Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);
                double opacity = color.A / 255.0;

                if (current is AcrylicBrush acrylic)
                {
                    if (acrylic.TintColor != tint) acrylic.TintColor = tint;
                    if (Math.Abs(acrylic.TintOpacity - opacity) > 0.001) acrylic.TintOpacity = opacity;
                    if (acrylic.FallbackColor != color) acrylic.FallbackColor = color;
                    return acrylic;
                }

                return new AcrylicBrush
                {
                    TintColor = tint,
                    TintOpacity = opacity,
                    FallbackColor = color
                };
            }
        }

        return SolidFill(current, color);
    }

    /// <summary>
    /// El TINTE plano de una superficie: el color del panel con el alfa del ajuste. Es el relleno de
    /// todos los paneles cuando el desenfoque está apagado y el del CHROME cuando el desenfoque lo
    /// aporta la copia compartida (ver <see cref="ChromeFill"/>). Reutiliza la instancia que el elemento
    /// ya tiene —mutarla en sitio es lo que hace que repinte al instante, sin depender de que WinUI
    /// vuelva a resolver el {ThemeResource}— y solo crea otra cuando tiene que cambiar el TIPO.
    /// </summary>
    private static Brush SolidFill(Brush? current, Windows.UI.Color color)
    {
        if (current is SolidColorBrush solid)
        {
            if (solid.Color != color) solid.Color = color;
            return solid;
        }

        return new SolidColorBrush(color);
    }

    /// <summary>
    /// El relleno de una pieza del CHROME (barra de título, panel del menú y barras de pestañas
    /// internas): el TINTE SOLO cuando el vidrio compartido se hace cargo de esas piezas —el desenfoque
    /// lo aporta la copia única recortada detrás, ver <see cref="PanelBlurLayer"/> y
    /// <see cref="ChromeSharedGlass"/>— y el relleno de siempre (vidrio por elemento, o sólido con el
    /// desenfoque apagado) en cualquier otro caso.
    /// </summary>
    internal static Brush ChromeFill(Brush? current, Windows.UI.Color color)
        => ChromeSharedGlass ? SolidFill(current, color) : SurfaceFill(current, color);

    /// <summary>
    /// La copia desenfocada quedó lista (la llama <see cref="PanelBlur"/> al publicarla): es el momento en
    /// que el chrome puede pasar al vidrio compartido —hasta acá no había copia y sus piezas llevaban el
    /// relleno del motor—, así que se repintan sus rellenos en vez de esperar al próximo movimiento del
    /// deslizador o al próximo cambio de tema.
    /// </summary>
    internal static void OnSharedGlassReady()
    {
        if (!ChromeSharedGlass) return;

        try { SetTransparencyPercent(TransparencyPercent, persist: false); }
        catch (Exception ex) { Log($"PanelAppearance.OnSharedGlassReady: {ex.Message}"); }
    }

    /// <summary>
    /// El relleno del navbar INTERNO de una página: la barra de pestañas propia que traen Núcleos,
    /// Limpieza y Configuración. Es la MISMA pieza que el panel del menú —mismo color de fábrica, mismo
    /// alfa del ajuste y el mismo vidrio vigente (ver <see cref="SurfaceFill"/>)— así que la franja de
    /// la página se lee igual que el resto de los paneles.
    ///
    /// POR QUÉ SE PINTA A MANO Y NO SE DEJA EL {ThemeResource} DEL XAML: WinUI resuelve el
    /// {ThemeResource} del Border al cargar la página y no lo vuelve a resolver cuando el ajuste
    /// REEMPLAZA la instancia del diccionario —encender o apagar el vidrio cambia el TIPO de relleno—,
    /// así que esas barras se quedaban con el sólido viejo mientras las cards (creadas o repintadas más
    /// tarde por el ajuste) ya tenían vidrio: es el reporte "el navbar de Configuración y de Núcleos no
    /// tiene blur". Es el mismo motivo por el que MainWindow pinta a mano su menú y su barra de título.
    ///
    /// EL LLAMADOR REPINTA SU BARRA con el pincel devuelto, al cargarse la página y en cada
    /// <see cref="Applied"/>: cambiar de camino es lo único que obliga a adoptar una instancia NUEVA
    /// —mutar la vieja no cambiaría el tipo— y es justo lo que el {ThemeResource} no hace.
    /// </summary>
    internal static Brush PaneBarFill(Brush? current)
    {
        var theme = EffectiveTheme();

        if (!ThemePalettes.TryGetFactoryColor(theme, "NavigationViewDefaultPaneBackground", out var paneColor))
        {
            // Tema sin definición propia (los clásicos) o arranque temprano: el color de respaldo de la
            // base, el mismo que usaba el navbar interno de Configuración desde antes de este helper.
            paneColor = ThemePalettes.BaseThemeFor(theme) == AppTheme.Light
                ? Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF)
                : Windows.UI.Color.FromArgb(255, 0x15, 0x15, 0x17);
        }

        var fill = ChromeFill(current, ApplyToColor(paneColor));

        // El camino de respaldo (la capa que recorta parches, ver PanelBlurLayer) reconoce los paneles
        // por la INSTANCIA del pincel del tema; esta barra lleva una copia del color, así que se declara.
        PanelBlurLayer.Watch(fill);
        return fill;
    }

    /// <summary>
    /// Pinta el relleno de una superficie con el color dado (el del tema, con el alfa del ajuste) en el
    /// diccionario del tema: el SÓLIDO de siempre, o el ACRÍLICO in-app con el vidrio encendido (ver
    /// <see cref="SurfaceFill"/>).
    ///
    /// La instancia se muta EN SITIO cuando solo cambia el color —el caso del deslizador de
    /// transparencia, que llama acá en cada tick— así que en ese camino no se toca el diccionario: los
    /// elementos repintan al instante, sin depender de que WinUI vuelva a resolver el {ThemeResource}.
    /// Solo al cambiar el TIPO de relleno (encender o apagar el vidrio) se escribe una instancia nueva.
    /// </summary>
    private static bool WriteSurfaceBrush(string key, string themeKey, Windows.UI.Color color)
    {
        try
        {
            var dict = FindThemeDictionary(key, themeKey);
            if (dict == null) return false;

            dict.TryGetValue(key, out var current);
            var fill = SurfaceFill(current as Brush, color);
            if (!ReferenceEquals(fill, current))
            {
                dict[key] = fill;

                // Cambió el TIPO de relleno (sólido <-> vidrio): los elementos que ya tenían puesta la
                // instancia vieja no vuelven a resolver el {ThemeResource} —son los que existían antes
                // del cambio de camino, o los que el código pintó a mano—, así que quedaban planos para
                // siempre. Se anota el par viejo -> nuevo y el árbol se repinta (ver
                // PanelBlurLayer.SwapReplacedSurfaces).
                if (current is Brush previous) _replacedSurfaces[previous] = fill;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Instancias de superficie que el ajuste REEMPLAZÓ al cambiar de camino (sólido <-> vidrio),
    /// con su reemplazo: lo que el árbol tiene que repintar para que no queden superficies con el
    /// pincel viejo (ver <see cref="PanelBlurLayer.SwapReplacedSurfaces"/>).
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<Brush, Brush> _replacedSurfaces = new();

    /// <summary>
    /// Escribe un valor cualquiera (p. ej. el pincel de imagen del fondo) en el diccionario de
    /// tema que define esa clave. Lo usa <see cref="Wallpaper"/>.
    /// </summary>
    internal static bool WriteThemeValue(string key, string themeKey, object value)
    {
        try
        {
            var dict = FindThemeDictionary(key, themeKey);
            if (dict == null) return false;
            dict[key] = value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Busca el diccionario de tema que define la clave: el raíz de App.xaml o, si ahí no está
    /// (el navbar vive en AccentOverrides.xaml), el último diccionario mergeado que la defina —
    /// el mismo orden con el que WinUI resuelve la clave.
    /// </summary>
    private static ResourceDictionary? FindThemeDictionary(string key, string themeKey)
    {
        try
        {
            if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var root)
                && root is ResourceDictionary rootDict
                && rootDict.ContainsKey(key))
            {
                return rootDict;
            }

            var merged = Application.Current.Resources.MergedDictionaries;
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].ThemeDictionaries.TryGetValue(themeKey, out var dict)
                    && dict is ResourceDictionary mergedDict
                    && mergedDict.ContainsKey(key))
                {
                    return mergedDict;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Color representativo del fondo de la ventana del tema activo, para componer el borde. En
    /// los temas clásicos el fondo es un color plano (se devuelve ese mismo); en los de gradiente
    /// se promedian sus paradas, porque el filete es UNA línea de color y el promedio es lo que
    /// mejor representa a un gradiente que va de oscuro a claro.
    /// </summary>
    private static Windows.UI.Color BackdropBaseColor()
    {
        try
        {
            var themeKey = ThemeKeyFor(EffectiveTheme());
            var dict = FindThemeDictionary("WindowBackdropBrush", themeKey);
            if (dict != null && dict.TryGetValue("WindowBackdropBrush", out var value))
            {
                if (value is SolidColorBrush solid) return solid.Color;
                if (value is LinearGradientBrush gradient && gradient.GradientStops.Count > 0)
                {
                    int r = 0, g = 0, b = 0;
                    foreach (var stop in gradient.GradientStops)
                    {
                        r += stop.Color.R;
                        g += stop.Color.G;
                        b += stop.Color.B;
                    }
                    int n = gradient.GradientStops.Count;
                    return Windows.UI.Color.FromArgb(255, (byte)(r / n), (byte)(g / n), (byte)(b / n));
                }
            }
        }
        catch { }
        return Windows.UI.Color.FromArgb(255, 0x0C, 0x0C, 0x0E);
    }

#if DEBUG
    private static bool _roundTripChecked;

    /// <summary>
    /// Autocomprobación de desarrollo, una vez por proceso y sin persistir nada: aplica 0 %,
    /// después 100 % y después 0 % OTRA VEZ, y verifica leyendo el diccionario del tema que cada
    /// paso quedó con el alfa esperado. El segundo 0 % es el que importa por partida doble: si el
    /// color base del cálculo saliera del diccionario vivo —y no de los colores de fábrica— el
    /// alfa se acumularía y las superficies no volverían al color del tema, y es además la
    /// comprobación de que 0 % deja los paneles OPACOS (el reporte de "dice 0 % pero sigue
    /// transparente"). Al terminar restituye la transparencia del usuario.
    ///
    /// Corre cuando ya existe la ventana (si no, la escritura se saltea y la comprobación no
    /// tendría nada que medir). Resultado en %LocalAppData%\WHPO\appearance.log.
    /// </summary>
    private static void VerifyRoundTrip()
    {
        try
        {
            if (_roundTripChecked) return;
            if (_window == null && App.MainWindowInstance == null) return;
            _roundTripChecked = true;

            var saved = TransparencyPercent;
            var theme = EffectiveTheme();
            var themeKey = ThemeKeyFor(theme);
            int failures = 0, checkedSurfaces = 0, opaqueSurfaces = 0;

            var steps = new[]
            {
                (Value: 0.0, Label: "0 %"),
                (Value: MaxTransparencyPercent, Label: $"{MaxTransparencyPercent:0} %"),
                (Value: 0.0, Label: "0 % (segunda vuelta)")
            };

            try
            {
                foreach (var (value, label) in steps)
                {
                    SetTransparencyPercent(value, persist: false);
                    foreach (var key in SurfaceKeys)
                    {
                        if (!ThemePalettes.TryGetFactoryColor(theme, key, out var factory)) continue;
                        if (factory.A == 0) continue; // transparente por diseño: no participa
                        var actual = ReadSurfaceColor(key, themeKey);
                        if (actual == null) continue;

                        checkedSurfaces++;
                        var expected = Windows.UI.Color.FromArgb(
                            (byte)Math.Round(255 * AlphaFor(value)),
                            factory.R, factory.G, factory.B);
                        if (actual.Value != expected)
                        {
                            failures++;
                            Diag($"verificación {label}: {key} quedó {ThemePaint.Hex(actual.Value)} y se esperaba {ThemePaint.Hex(expected)}");
                        }
                        else if (value == 0.0 && actual.Value.A == 255)
                        {
                            opaqueSurfaces++;
                        }
                    }
                }
            }
            finally
            {
                // El restore va en finally: si un paso tira, el catch de afuera se lleva la
                // excepción y un restore de línea no correría — las superficies quedaban
                // pintadas con el último paso (100 % = paneles casi invisibles) hasta que algo
                // más las repintara: exactamente el reporte "al abrir la app la transparencia
                // es 100 %" con el deslizador guardado en otro valor. (En los arranques
                // auditados la prueba corrió completa; esto es el blindaje para el caso que
                // tire a mitad.)
                SetTransparencyPercent(saved, persist: false);
            }

            Diag(failures == 0
                ? $"verificación de ida y vuelta de la transparencia: OK ({checkedSurfaces} superficies; en 0 % quedaron opacas {opaqueSurfaces} del total)"
                : $"verificación de ida y vuelta de la transparencia: {failures} de {checkedSurfaces} superficies no quedaron con el alfa esperado");
        }
        catch (Exception ex)
        {
            Log($"PanelAppearance.VerifyRoundTrip: {ex.Message}");
        }
    }

    /// <summary>
    /// Color que tiene HOY una superficie en el diccionario del tema. Devuelve null cuando la
    /// superficie no está pintada con un color (el pincel de la foto desenfocada): en ese caso la
    /// comprobación de ida y vuelta no tiene alfa que medir.
    /// </summary>
    private static Windows.UI.Color? ReadSurfaceColor(string key, string themeKey)
    {
        var dict = FindThemeDictionary(key, themeKey);
        if (dict == null || !dict.TryGetValue(key, out var value)) return null;
        return value switch
        {
            SolidColorBrush brush => brush.Color,
            _ => null
        };
    }
#endif

    private static void Save(string key, object value)
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            settings.Set(key, value);
            settings.Save();
        }
        catch (Exception ex)
        {
            Log($"PanelAppearance.Save({key}): {ex.Message}");
        }
    }

    /// <summary>Lectura de un ajuste de apariencia (la usan la página y Wallpaper).</summary>
    internal static T Get<T>(string key, T fallback)
    {
        try
        {
            var value = App.Services.GetRequiredService<ISettingsService>().Get<T>(key, fallback);
            if (value is not null) return value;
        }
        catch { }
        return fallback;
    }

    /// <summary>Escritura de un ajuste de apariencia (la usan la página y Wallpaper).</summary>
    internal static void Set(string key, object value) => Save(key, value);

    private static void Log(string message)
    {
        try { App.Services.GetRequiredService<ILoggingService>().LogInfo(message); } catch { }
    }

    // =====================================================================
    // Diagnóstico: %LocalAppData%\WHPO\appearance.log
    // =====================================================================

    /// <summary>
    /// Bitácora propia del ajuste (mismo patrón que i18n.log): una entrada por aplicación con el
    /// tema, la transparencia y el color que quedó en cada superficie. Sirve para verificar el
    /// resultado sin abrir la app —y para diagnosticar un reporte de "se ve negro" sabiendo
    /// exactamente qué alfa se escribió y con qué color base—. Solo se genera con "Logs de
    /// desarrollo" activo (sin el switch no se crea appearance.log); nunca debe romper la
    /// apariencia, así que todo va en try.
    /// </summary>
    private static bool _diagStarted;

    internal static void Diag(string line)
    {
        try
        {
            // Detrás del switch "Logs de desarrollo": con el ajuste apagado no se
            // genera appearance.log (ver LoggingService).
            if (!LoggingService.IsDeveloperLoggingEnabled()) return;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WHPO");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "appearance.log");

            if (!_diagStarted)
            {
                _diagStarted = true;
                File.WriteAllText(path, $"WinForge — apariencia (arranque {DateTime.Now:yyyy-MM-dd HH:mm:ss}){Environment.NewLine}");
            }

            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch { }
    }
}
