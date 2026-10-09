using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace WHPO_UI;

/// <summary>
/// CAMINO DE RESPALDO. Con el vidrio de la plataforma encendido —el caso normal, ver
/// <see cref="PanelAppearance.PlatformGlassInCharge"/>— esta capa NO se usa: el acrílico in-app
/// desenfoca cada panel desde el compositor, y acá no hay ni copia que recalcular ni parche que
/// recortar ni posición que seguir. Todo lo que sigue vale para el ÚNICO caso en el que la app tiene
/// que pintar el vidrio por su cuenta: que Windows no pueda renderizar el acrílico (el usuario apagó
/// los "Efectos de transparencia" en Configuración > Personalización > Colores, ver
/// <see cref="PlatformGlass"/>).
///
/// El parche de fondo desenfocado de cada panel: una capa que va ENCIMA de la foto del fondo y
/// DEBAJO del velo y del contenido, con un parche por panel (cards, chips y la franja del menÃº).
/// DetrÃ¡s de cada panel queda entonces la foto desenfocada de ESA zona, y el panel la muestra a
/// travÃ©s de su propio relleno translÃºcido â€” el vidrio esmerilado de verdad, y no una foto
/// borroneada estirada dentro de la card (que era lo que se veÃ­a mal).
///
/// CÃ“MO SE ALINEA SIN TOCAR A LOS PANELES: la capa ocupa toda la ventana y cada parche pinta la
/// copia desenfocada con la MISMA geometrÃ­a que la foto nÃ­tida de arriba (las dos van "Fill" sobre
/// la ventana), asÃ­ que el parche queda automÃ¡ticamente en fase con la imagen que tiene detrÃ¡s.
/// El parche es un <c>Rectangle</c> ubicado en el panel y su pincel lleva escalada la copia al
/// tamaÃ±o de la ventana y corrida por -posiciÃ³n: lo que se ve adentro es exactamente el pedazo de
/// foto que corresponde a ese lugar.
///
/// POR QUÃ‰ UN PARCHE POR PANEL Y NO UN RECORTE CON GEOMETRÃA: el <c>UIElement.Clip</c> de WinUI
/// solo acepta UN rectÃ¡ngulo (es un <c>RectangleGeometry</c>, no una geometrÃ­a cualquiera), asÃ­ que
/// el "recorte por geometrÃ­a" no existe en esta plataforma. Es ademÃ¡s lo que permite redondear
/// cada parche con el mismo <c>CornerRadius</c> del panel.
///
/// ES A PRUEBA DE FALLAS: si la lista de paneles quedara vacÃ­a o mal medida, el resultado es que no
/// se ve ningÃºn desenfoque â€” nunca una UI rota, porque los paneles conservan siempre su color del
/// tema (a diferencia de darle la foto desenfocada al relleno de cada card).
///
/// CÃ“MO SE RECONOCE UN PANEL: por el pincel de su relleno â€”el del tema, el live o uno de los
/// extraâ€” y, cuando el tema lo pinta con una COPIA del color, por el COLOR (mismo criterio que usa
/// RevealEffect para reconocer una card). TambiÃ©n cuentan los rellenos de estado de una card
/// (hover/selecciÃ³n) y el MARCO de una card sin relleno propio (los componentes del Workshop arman
/// sus tarjetas asÃ­): ver StateKeys, BorderKeys e IsCardFrame.
///
/// QUÃ‰ NO ES UN PANEL (y por eso no lleva parche): la SUPERFICIE DE FONDO del tema, que se reconoce
/// porque cubre la ventana casi entera (ver <see cref="MaxPanelCoverage"/>). El caso real: el
/// NavigationView pinta su propio `Background` â€”el pincel del menÃº, NavigationViewDefaultPane
/// Backgroundâ€” en TODA su Ã¡rea, porque el template se lo pasa al SplitView interno por
/// TemplateBinding; el control entero (menÃº + contenido) entra entonces en la lista de candidatos y
/// su parche tapaba la ventana completa con la foto desenfocada. Como los parches de las cards
/// caen en fase sobre esa misma foto, todo se leÃ­a como UNA sola capa borrosa y los paneles
/// parecÃ­an unirse entre sÃ­.
///
/// EL VIDRIO SIGUE AL SCROLL: cada parche guarda el ScrollViewer que desplaza a su panel y el cero de
/// ese scroll (ver PatchScroll y ShiftPatches). Con el scroll en movimiento el parche se mueve con la
/// Translation del elemento y la fase de su pincel en el mismo pase —las dos son cambios de render,
/// asi que entran juntas y el layout del Canvas no se toca en medio del scroll— y su recorte se
/// recalcula por frame (ver ApplyPatchClip), asi que una card que sale de la vista deja de pintar y
/// una que entra llega con su vidrio puesto. La unica re-medicion del arbol es cuando el scroll se
/// asienta.
/// </summary>
internal static class PanelBlurLayer
{
    /// <summary>Cada cuÃ¡nto se vuelve a buscar la lista de paneles (ms). Ver RebuildPanels.</summary>
    private const int RebuildIntervalMs = 400;

    /// <summary>
    /// Techo de Ã¡rea de un parche, como fracciÃ³n de la ventana: por encima de esto el candidato es
    /// la superficie de fondo del tema y no un panel â€” desenfocarla detrÃ¡s de todo es exactamente lo
    /// que unÃ­a a todos los paneles en una sola capa borrosa.
    ///
    /// POR QUÃ‰ 85 %: el panel mÃ¡s grande posible es el Ã¡rea de contenido entera, y aun asÃ­ queda
    /// por debajo (en una ventana de 1920x1080 el contenido es el 87 % del ancho por el 96 % del
    /// alto descontando el menÃº y la barra de tÃ­tulo, o sea ~83 % del Ã¡rea; en ventanas normales,
    /// bastante menos), mientras que las superficies de fondo â€”el NavigationView y su SplitView
    /// internoâ€” cubren el ancho completo por todo el alto menos la barra de tÃ­tulo, o sea 95 % o
    /// mÃ¡s. Entre los dos no hay ambigÃ¼edad, y el nÃºmero queda alto a propÃ³sito: ante la duda, un
    /// candidato grande se sigue desenfocando.
    /// </summary>
    private const double MaxPanelCoverage = 0.85;

    /// <summary>
    /// Claves de los ESTADOS de una card: el relleno que el tema le da al pasar el mouse o al quedar
    /// seleccionada. Cuentan como panel igual que el relleno normal â€” si no, la card dejaba de serlo
    /// apenas el cursor entraba en ella (DebloatPage y ActualizacionesPage cambian el Background al
    /// estado hover/selecciÃ³n), justo cuando el usuario la mira para ver el desenfoque, y el parche
    /// desaparecÃ­a.
    /// </summary>
    private static readonly string[] StateKeys = { "CardHoverBrush", "CardSelectedBrush" };

    /// <summary>
    /// Claves del BORDE de una card: marcan una card que NO tiene relleno propio. Es el caso de los
    /// componentes del Workshop â€”su helper Card() arma un Border con el borde de card y esquinas,
    /// sin Backgroundâ€” y de los marcos armados en code-behind: sin esta seÃ±al no habrÃ­a forma de
    /// que el desenfoque los alcance (ver <see cref="IsCardFrame"/>).
    /// </summary>
    private static readonly string[] BorderKeys = { "CardBorderBrush" };

    /// <summary>
    /// TamaÃ±o mÃ­nimo de un marco de card (ver <see cref="IsCardFrame"/>): evita que un chip, un
    /// separador, un campo de texto o un botÃ³n con borde redondeado entren como panel.
    /// </summary>
    private const double MinCardWidth = 120;
    private const double MinCardHeight = 40;

    /// <summary>
    /// Cuánto más allá de la vista se le deja el parche AUTORIZADO a un panel (px, ver
    /// <see cref="UpdatePatches"/>): cubre las cards que el scroll trae en los próximos frames sin
    /// autorizar la lista entera —un parche es un rectángulo con un pincel de imagen, y en una página
    /// con decenas de cards no hace falta tenerlos todos a la vez.
    /// </summary>
    private const double MarginBeyondView = 800;

    private static FrameworkElement? _root;
    private static Canvas? _layer;

    /// <summary>Los paneles vigentes (dÃ©biles a propÃ³sito: las pÃ¡ginas se recrean al navegar).</summary>
    private static readonly List<WeakReference<FrameworkElement>> _panels = new();

    /// <summary>
    /// CuÃ¡ndo se vio por Ãºltima vez a cada panel de <see cref="_panels"/> (mismo Ã­ndice) y si en esa
    /// Ãºltima vez estaba cargado. La lista se ARMA POR FUSIÃ“N (ver <see cref="RebuildPanels"/>): un
    /// pase que no encuentra una card â€”porque la pÃ¡gina la estÃ¡ volviendo a armarâ€” ya no puede
    /// hacerle perder su parche, que es lo que se veÃ­a desde afuera como "se fue el desenfoque".
    /// </summary>
    private static readonly List<int> _panelSeen = new();
    private static readonly List<bool> _panelWasLoaded = new();

    /// <summary>
    /// Ãšltima geometrÃ­a con la que se pintÃ³ el parche de cada panel (mismo Ã­ndice), y con quÃ© radio de
    /// esquina. Viven acÃ¡ y no en <see cref="_panelClips"/> porque tienen que sobrevivir al ELEMENTO:
    /// cuando la pÃ¡gina destruye una card, su referencia se pierde y queda solo esto, que es lo que
    /// mantiene el vidrio puesto mientras la card nueva entra a la lista (ver <see cref="TryGhostBounds"/>).
    /// </summary>
    private static readonly List<Rect> _panelBounds = new();
    private static readonly List<double> _panelRadius = new();

    /// <summary>
    /// Zona VISIBLE de cada panel (ver <see cref="ClipRegionOf"/>) leÃ­da en el Ãºltimo pase con la
    /// ventana quieta. Clave dÃ©bil: el estado se va con el elemento y no retiene pÃ¡ginas enteras.
    /// </summary>
    private static readonly ConditionalWeakTable<FrameworkElement, PanelClip> _panelClips = new();

    /// <summary>Zona visible de un panel tal como estaba en el Ãºltimo pase quieto (ver <see cref="ClipRegionOf"/>).</summary>
    private sealed class PanelClip
    {
        public Rect Region;
        public bool Has;
    }

    /// <summary>
    /// CuÃ¡nto sobrevive en la lista un panel que dejÃ³ de aparecer en los pases (ver
    /// <see cref="PrunePanels"/>): cubre el rato en que la pÃ¡gina rearma sus cards.
    /// </summary>
    private const int PanelGraceMs = 3000;

    /// <summary>Un parche por panel, reutilizado: mover un panel es cambiarle la posiciÃ³n, no crear objetos.</summary>
    private static readonly List<Rectangle> _patches = new();

    /// <summary>
    /// El scroll que mueve a cada parche (mismo índice que <see cref="_patches"/>): el ScrollViewer que
    /// desplaza a su panel —null si el panel no scrollea: el navbar y la barra de título están fijos— y
    /// el cero desde el que se mide su delta (ver <see cref="ShiftPatches"/>).
    ///
    /// ES POR PARCHE Y NO UNO SOLO PARA TODA LA VENTANA: la ventana tiene varios ScrollViewers y solo
    /// uno mueve cada card. El menú lateral tiene el suyo (los ítems del NavigationView desbordan su
    /// panel), y con UN scroller global —el primero con scroll vertical que aparecía en el recorrido—
    /// los parches de las cards medían el scroll del MENÚ: delta cero en cada frame, vidrio quieto
    /// hasta que el scroll se asentaba y el pase en quieto lo re-medía. Ese era el reporte "el blur
    /// recién carga cuando dejo de scrollear y me quedo quieto".
    /// </summary>
    private sealed class PatchScroll
    {
        public ScrollViewer? Scroller;

        /// <summary>VerticalOffset del scroller en el último pase EN QUIETO: el cero del delta.</summary>
        public double OffsetBase;

        /// <summary>
        /// La zona que el panel muestra de verdad (su scroller ∩ la ventana) en el último pase, en
        /// coordenadas de ventana. Es el recorte con el que ShiftPatches apaga el parche cuando sale de
        /// la vista y lo enciende cuando vuelve a entrar (ver <see cref="ApplyPatchClip"/>).
        /// </summary>
        public Rect Viewport;

        /// <summary>El recorte del parche, reutilizado: se le reescribe el rectángulo en cada frame.</summary>
        public RectangleGeometry? Clip;
    }

    private static readonly List<PatchScroll> _patchScrolls = new();

    /// <summary>
    /// Top base (en coordenadas de ventana) con el que se pintÃƒÂ³ cada parche en el ÃƒÂºltimo pase EN
    /// QUIETO, paralela a <see cref="_patches"/>. Es el origen de la aritmÃƒÂ©tica del scroll: con el
    /// scroll en movimiento el parche se ubica en baseTop - (offsetActual - offsetBase), sin acumular
    /// deltas de eventos (que derivaban y hacÃƒÂ­an oscilar el parche alrededor de la card). Se refresca
    /// solo en los pases en quieto (ver <see cref="UpdatePatches"/>); durante el frame-watch no se
    /// toca, y como el ÃƒÂ¡rbol no cambia por scroll, sigue valiendo.
    /// </summary>
    private static readonly List<double> _patchBaseTop = new();

    /// <summary>
    /// La lista que se le pasa al vidrio declarativo en cada pase de medida (ver <see cref="PanelGlass"/>):
    /// es UNA sola y se reusa, porque el pase corre en cada layout.
    /// </summary>
    private static readonly List<PanelGlass.Item> _glassItems = new();

    /// <summary>
    /// El ScrollViewer que desplaza a este elemento (el más cercano hacia arriba con scroll vertical),
    /// o null si no lo mueve ninguno. Es el que decide si el parche del panel acompaña al scroll y con
    /// qué delta sale (ver <see cref="_patchScrolls"/> y <see cref="ShiftPatches"/>).
    /// </summary>
    private static ScrollViewer? ScrollAncestorOf(FrameworkElement element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, _root)) break;
            if (node is ScrollViewer sv && sv.ScrollableHeight > 0) return sv;
        }
        return null;
    }

    private static readonly List<Brush> _extra = new();

    /// <summary>
    /// Pinceles del CHROME vigentes: los que la ventana y las páginas declaran a mano (ver
    /// <see cref="SetExtraBrushes"/> y <see cref="Watch"/>). En modo chrome (ver <see cref="_chromeOnly"/>) son
    /// la ÚNICA forma de entrar como panel: ni el emparejamiento por color ni el marco de card
    /// participan, porque acá solo interesan las piezas que van PEGADAS unas a otras.
    /// </summary>
    private static readonly HashSet<Brush> _chromeBrushes = new();

    /// <summary>
    /// El pincel con el que el tema pinta el panel del menú (la clave
    /// <c>NavigationViewDefaultPaneBackground</c> del diccionario vigente). La plantilla de
    /// NavigationView pinta su franja con ESA instancia —el código de la ventana se la sustituye
    /// recién cuando encuentra el SplitView—, así que en modo chrome también identifica a un panel.
    /// </summary>
    private static Brush? _chromeKeyBrush;

    /// <summary>
    /// MODO CHROME: la capa atiende SOLO las piezas del chrome —barra de título, panel del menú y
    /// barras de pestañas internas de cada página— y deja el resto de los paneles (cards, grillas,
    /// cuadros de texto) al vidrio por elemento del compositor.
    ///
    /// POR QUÉ EXISTE: el vidrio del compositor desenfoca el fondo de CADA panel por separado y con el
    /// borde recortado (ver GlassBlurBrush, <c>EffectBorderMode.Hard</c>), así que en cada junta entre
    /// dos paneles pegados la foto quedaba cortada en seco: el reporte "entre la pestaña, el navbar
    /// principal y el navbar del componente se ve recortado". Con la copia ÚNICA anclada a la ventana
    /// (la de acá) la imagen es la MISMA de los dos lados de la junta, así que no hay corte que ver y
    /// las tres piezas se leen como una sola. El resto de los paneles no pierde nada: van separados y
    /// el vidrio por elemento les queda pegado por construcción.
    /// </summary>
    private static bool _chromeOnly;

    /// <summary>
    /// Pinceles de superficie que una PÃGINA pinta a mano con una COPIA del color del tema (ver
    /// <see cref="Watch"/>). Van en referencias DÃ‰BILES a propÃ³sito: la pÃ¡gina que los creÃ³ se
    /// recrea al navegar y esos pinceles tienen que poder recolectarse solos.
    /// </summary>
    private static readonly List<WeakReference<Brush>> _watched = new();

    /// <summary>Pinceles que identifican un panel por INSTANCIA (los del tema, los live y los extra).</summary>
    private static readonly HashSet<Brush> _match = new();

    /// <summary>Pinceles que identifican el MARCO de una card (ver BorderKeys y IsCardFrame).</summary>
    private static readonly HashSet<Brush> _borderMatch = new();

    /// <summary>
    /// Los COLORES de esos pinceles: el segundo camino para reconocer un panel. Hay superficies que
    /// el tema pinta con una COPIA del color y no con la instancia vigilada â€”la barra de pestaÃ±as
    /// internas de ConfiguraciÃ³n se repinta con un pincel nuevo en ConfiguracionPage.RepaintConfigNavBarâ€”
    /// y con el emparejamiento solo por instancia esas superficies se quedaban sin desenfoque. Es la
    /// misma comparaciÃ³n por color que usa <see cref="RevealEffect"/> para reconocer una card.
    /// </summary>
    private static readonly HashSet<Windows.UI.Color> _matchColors = new();
    private static readonly HashSet<Windows.UI.Color> _borderColors = new();

    private static bool _enabled;
    private static bool _hooked;
    private static int _lastRebuild;

    /// <summary>
    /// Hasta cuÃ¡ndo la bÃºsqueda de paneles corre en CADA pase. La encienden las seÃ±ales de que la
    /// pÃ¡gina estÃ¡ rearmando sus cards (una referencia muerta, un panel que dejÃ³ de estar cargado) y
    /// se renueva con cada seÃ±al nueva.
    ///
    /// POR QUÃ‰ HACE FALTA: el barrido periÃ³dico tiene un intervalo, y una card reciÃ©n creada que caÃ­a
    /// en el medio del intervalo se quedaba sin parche hasta el prÃ³ximo barrido. Con la transparencia
    /// alta, esos huecos se ven como "el blur desaparece" mientras se scrollea. Con la ventana
    /// abierta, la card nueva se recorta en el pase en que aparece, no hasta 400 ms despuÃ©s.
    /// </summary>
    private static int _churnUntil;

    /// <summary>
    /// Hasta cuÃ¡ndo un panel que desapareciÃ³ conserva su parche, con la Ãºltima geometrÃ­a que tuvo (ver
    /// <see cref="TryGhostBounds"/>). La abre <see cref="MarkRearm"/> junto con la de rearme.
    /// </summary>
    private static int _ghostUntil;

    /// <summary>CuÃ¡nto dura la ventana de rearme de <see cref="_churnUntil"/> (ms).</summary>
    private const int PanelChurnMs = 800;

    /// <summary>
    /// Hasta cuÃ¡ndo se deja puesto el parche de un panel que desapareciÃ³ (ver <see cref="TryGhostBounds"/>).
    ///
    /// POR QUÃ‰ ES MÃS LARGA QUE LA DE REARME: la ventana de rearme acelera la bÃºsqueda, pero no crea
    /// pases â€”con la ventana quieta los pases los trae el refresco por layout, uno por segundoâ€”, asÃ­
    /// que el parche de repuesto tiene que cubrir el hueco hasta ese prÃ³ximo pase, no solo hasta que
    /// venza la rebÃºsqueda.
    /// </summary>
    private const int GhostMs = 1500;

    /// <summary>
    /// Cada cuÃ¡nto se rehace la bÃºsqueda mientras la ventana de rearme estÃ¡ abierta (ms). Es corto a
    /// propÃ³sito â€”a esta cadencia el hueco de una card reciÃ©n creada no llega a verseâ€” pero no cero:
    /// recorrer el Ã¡rbol en cada frame de un scroll largo, sin necesidad, es trabajo tirado.
    /// </summary>
    private const int ChurnIntervalMs = 50;

    /// <summary>
    /// ScrollViewers ya enganchados a <see cref="HookScrollViewer"/>. DÃ©biles a propÃ³sito: las pÃ¡ginas
    /// se recrean al navegar y sus ScrollViewers tienen que poder recolectarse solos.
    /// </summary>
    private static readonly List<WeakReference<ScrollViewer>> _scrollHooks = new();

    /// <summary>Hay un pase de parches corriendo (ver <see cref="UpdatePatches"/>).</summary>
    private static bool _updatingPatches;

    /// <summary>
    /// Hay un scroll en curso y la capa se estÃ¡ refrescando por FRAME (ver <see cref="ScrollActivity"/>).
    /// Mientras dura, el refresco no espera al prÃ³ximo ViewChanged ni al prÃ³ximo pase de layout: la
    /// animaciÃ³n del scroll la mueve la capa de composiciÃ³n, asÃ­ que el managed code se entera reciÃ©n
    /// al final de cada paso y el vidrio se quedaba atrÃ¡s durante toda la animaciÃ³n â€” con la
    /// transparencia alta, que es cuando el panel se lee por el vidrio que tiene detrÃ¡s, eso se ve
    /// como que el blur se fue y la card quedÃ³ flotando sobre la foto nÃ­tida.
    /// </summary>
    private static bool _frameWatch;

    /// <summary>Ãšltima seÃ±al de scroll (ms): con esto el refresco por frame se corta al quedarse quieto.</summary>
    private static int _lastScrollSignal;

    /// <summary>CuÃ¡nto sigue vivo el refresco por frame despuÃ©s de la Ãºltima seÃ±al de scroll (ms).</summary>
    private const int ScrollSettleMs = 250;

    /// <summary>
    /// La Ãºltima bÃºsqueda de paneles se hizo con la ventana todavÃ­a sin medir (arranque), asÃ­ que la
    /// regla de la superficie de fondo no se pudo evaluar: UpdatePatches la rehace en cuanto hay
    /// tamaÃ±o (ver <see cref="MaxPanelCoverage"/>).
    /// </summary>
    private static bool _rebuildWithoutSize;

    /// <summary>
    /// Fuerza la prÃ³xima bÃºsqueda de paneles aunque el intervalo no se haya cumplido (ver
    /// <see cref="PanelsChanged"/>): al cambiar de pÃ¡gina la lista tiene que rehacerse YA, y no en
    /// el prÃ³ximo turno de comprobaciÃ³n.
    /// </summary>
    private static bool _forceRebuild;

    /// <summary>Ãšltima vez que se anotÃ³ la cadena de capas sobre un panel (ver <see cref="LogOccluders"/>).</summary>
    private static int _lastOccluderLog;

    /// <summary>
    /// La Ãºltima lista de paneles que se anotÃ³ en el log (ver <see cref="LogPanels"/>): el detalle se
    /// escribe solo cuando CAMBIA, porque la bÃºsqueda corre cada 400 ms y si no la misma lÃ­nea
    /// taparÃ­a el log.
    /// </summary>
    private static string _lastPanelDetail = "";

    /// <summary>Ãšltima geometrÃ­a de parches anotada, y cuÃ¡ndo (ver <see cref="LogPatches"/>).</summary>
    private static string _lastPatchDetail = "";
    private static int _lastPatchLog;

    /// <summary>Ãšltima auditorÃ­a de superficies anotada, y cuÃ¡ndo (ver <see cref="AuditSurfaces"/>).</summary>
    private static string _lastAudit = "";
    private static int _lastAuditLog;

    /// <summary>Ãšltimo conteo de paneles anotado, y cuÃ¡ndo (ver <see cref="LogPanelCount"/>).</summary>
    private static string _lastPanelCountDetail = "";
    private static int _lastPanelCountLog;

    /// <summary>
    /// Paneles que se quedaron sin parche en el Ãºltimo pase, por causa (ver <see cref="LogPatches"/>):
    /// 0 desprendidos, 1 sin medir, 2 fuera de vista, 3 cubren la ventana, 4 con error al medir,
    /// 5 sin referencia (el elemento ya fue recolectado: la pÃ¡gina lo destruyÃ³ y su reemplazo
    /// todavÃ­a no estÃ¡ en la lista).
    /// </summary>
    private static readonly int[] _skipped = new int[6];

    /// <summary>Ya se anotó el primer movimiento de la ráfaga de scroll en curso (ver <see cref="LogShift"/>).</summary>
    private static bool _shiftLogged;

    /// <summary>Frames de la ráfaga en curso en los que algún parche se movió (ver <see cref="StopFrameWatch"/>).</summary>
    private static int _shiftFrames;

    /// <summary>
    /// Mayor delta aplicado en la ráfaga de scroll en curso (px). Es el dato que distingue un
    /// <c>VerticalOffset</c> que avanza en cada frame —muchos frames con saltos chicos— de uno que
    /// salta directo al destino: pocos frames con un salto grande (ver <see cref="StopFrameWatch"/>).
    /// </summary>
    private static double _shiftMaxDelta;

    /// <summary>
    /// Hay un parche re-anclado que pide layout (ver <see cref="WritePatch"/>): el <c>Canvas.Top</c>
    /// nuevo y la Translation en cero tienen que valer en el MISMO frame. Sin el empujón de layout,
    /// durante un frame el parche se pintaría en la posición vieja —un parpadeo al cerrar el scroll.
    /// </summary>
    private static bool _rebasePending;

    /// <summary>Ya se dejó en el log que el vidrio pasó a pintarse desde la composición (una vez por sesión).</summary>
    private static bool _glassLogged;

    /// <summary>Enlaza el Ã¡rbol de la ventana y la capa del XAML. Una vez, al construir la ventana.</summary>
    internal static void Attach(FrameworkElement root, Canvas layer)
    {
        _root = root;
        _layer = layer;
    }

    /// <summary>
    /// Pinceles EXTRA que tambiÃ©n se desenfocan: la barra de tÃ­tulo y la franja del menÃº no llevan
    /// los pinceles del tema (se pintan a mano, ver MainWindow.ApplyPanelColors), asÃ­ que hay que
    /// declarÃ¡rselos para que el desenfoque los alcance igual que a las cards.
    /// </summary>
    internal static void SetExtraBrushes(params Brush?[] brushes)
    {
        // Sin capa no hay nada que declararle: con el vidrio de la plataforma (ver
        // PanelAppearance.PlatformGlassInCharge) la capa está apagada y rearmar sus paneles sería
        // trabajo —y un recorrido del árbol— por nada.
        // Se anotan SIEMPRE, aunque la capa esté apagada: el modo chrome (ver _chromeOnly) reconoce sus
        // paneles por estos pinceles, y las declaraciones de la ventana llegan ANTES de que el ajuste
        // encienda la capa —si se descartaran, al prender el desenfoque el chrome quedaría sin vidrio
        // detrás y con el relleno plano en su lugar—.
        _extra.Clear();
        foreach (var brush in brushes)
            if (brush != null) _extra.Add(brush);

        if (!_enabled) return;
        _forceRebuild = true;
        RebuildPanels();
    }

    /// <summary>
    /// Enciende o apaga el MODO CHROME (ver <see cref="_chromeOnly"/>): lo maneja PanelAppearance según
    /// quién pinta el vidrio. Al cambiar de modo se rehace la búsqueda de paneles, que es lo único que
    /// cambia (la copia y la declaración de recortes son las mismas).
    /// </summary>
    internal static void SetChromeOnly(bool chromeOnly)
    {
        if (_chromeOnly == chromeOnly) return;
        _chromeOnly = chromeOnly;
        _forceRebuild = true;
        Invalidate();
    }

    /// <summary>
    /// Declara un pincel de superficie pintado a mano por una pÃ¡gina â€”una COPIA del color del panel,
    /// no la instancia del temaâ€” para que el desenfoque lo reconozca. El caso real es la barra de
    /// pestaÃ±as interna de ConfiguraciÃ³n (ver ConfiguracionPage.RepaintConfigNavBar).
    ///
    /// POR QUÃ‰ HACE FALTA: a esas copias las reconocÃ­a el emparejamiento por COLOR, y el color lleva
    /// el alfa de la transparencia vigente. En 100 % ese alfa es 0, y un color con alfa 0 no
    /// identifica nada (es el candado que impide que cualquier elemento sin relleno pase por panel),
    /// asÃ­ que justo en la transparencia mÃ¡xima esa superficie se quedaba sin desenfoque. Por
    /// INSTANCIA eso no puede pasar: el pincel es el mismo objeto, tenga el alfa que tenga.
    /// </summary>
    internal static void Watch(params Brush?[] brushes)
    {
        bool added = false;
        foreach (var brush in brushes)
        {
            if (brush == null) continue;

            bool known = false;
            for (int i = _watched.Count - 1; i >= 0; i--)
            {
                if (!_watched[i].TryGetTarget(out var previous)) _watched.RemoveAt(i);
                else if (ReferenceEquals(previous, brush)) { known = true; break; }
            }

            if (known) continue;
            _watched.Add(new WeakReference<Brush>(brush));
            added = true;
        }

        // El conjunto de pinceles vigilados cambiÃ³: se rearma en el acto en vez de esperar al
        // intervalo de RebuildPanels. Con la capa apagada solo queda anotado (y hace falta: el modo
        // chrome reconoce sus paneles por ESTOS pinceles, ver SetExtraBrushes).
        if (!added || !_enabled) return;
        _forceRebuild = true;
        Invalidate();
    }

    /// <summary>
    /// Pone la capa al dÃ­a: la llama PanelAppearance cuando cambia el tema, la transparencia o el
    /// desenfoque, y PanelBlur cuando termina de armar la copia desenfocada.
    /// </summary>
    internal static void Invalidate()
    {
        try
        {
            if (_layer == null) return;

            if (!_enabled || PanelBlur.Source == null)
            {
                // La capa se apaga: el vidrio declarativo se apaga con ella (si quedara encendido, el
                // pase de medida no volvería a declararlo y quedaría un vidrio viejo sobre la ventana).
                PanelGlass.Hide();
                _layer.Visibility = Visibility.Collapsed;
                return;
            }

            _layer.Visibility = Visibility.Visible;
            Hook();
            RebuildPanels();
            UpdatePatches();
        }
        catch (Exception ex)
        {
            PanelAppearance.Diag($"desenfoque: no se pudo recortar la capa ({ex.Message})");
        }
    }

    /// <summary>
    /// Enciende o apaga la capa (el deslizador de desenfoque en 0 %). Si el estado no cambiÃ³, no se
    /// hace nada: el deslizador llama acÃ¡ en CADA tick que se mueve, y rehacer el recorte entero en
    /// cada uno (recorriendo el Ã¡rbol para juntar los paneles) era trabajo tirado.
    /// </summary>
    internal static void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        Invalidate();
    }

    /// <summary>
    /// La PÃGINA cambiÃ³ (navegaciÃ³n, o la pÃ¡gina terminÃ³ de armarse): la lista de paneles se rehace
    /// en el acto, sin esperar al intervalo de <see cref="RebuildPanels"/>. Sin esto, navegar dejaba
    /// a la capa buscando las cards de la pÃ¡gina vieja â€”que ya no existenâ€” y la nueva se quedaba sin
    /// ningÃºn parche: el desenfoque "desaparecÃ­a" al cambiar de pestaÃ±a y volvÃ­a reciÃ©n al mover un
    /// deslizador, que era lo que forzaba una bÃºsqueda nueva.
    /// </summary>
    internal static void PanelsChanged()
    {
        _forceRebuild = true;

        // La pÃ¡gina vieja se va entera: sus paneles no pueden quedar en la lista esperando el plazo de
        // gracia â€”dejarÃ­an parches (y ahora tambiÃ©n sus geometrÃ­as de repuesto) sobre la pÃ¡gina nuevaâ€”
        // ni hace falta abrir la ventana de rearme: la bÃºsqueda forzada encuentra la nueva en el acto.
        _panels.Clear();
        _panelSeen.Clear();
        _panelWasLoaded.Clear();
        _panelBounds.Clear();
        _panelRadius.Clear();
        // Los scrollers son de la página vieja: se sueltan (un parche que quede de la página anterior no
        // tiene por qué seguir a un ScrollViewer que ya no está).
        foreach (var state in _patchScrolls) state.Scroller = null;
        // Queda en el log una lÃ­nea por navegaciÃ³n: es la forma de comprobar, desde afuera, que el
        // desenfoque se enterÃ³ del cambio de pÃ¡gina (y cuÃ¡ntos paneles encontrÃ³ en la nueva).
        PanelAppearance.Diag("desenfoque: pÃ¡gina nueva â€” se rehace la bÃºsqueda de paneles");
        Invalidate();

        // AuditorÃ­a (ver AuditSurfaces): se corre DESPUÃ‰S del pase de medida, cuando la pÃ¡gina nueva ya
        // tiene tamaÃ±os; en el acto, las superficies que todavÃ­a no se midieron saldrÃ­an en 0x0.
        AuditLater("pÃ¡gina nueva");
    }

    /// <summary>
    /// El CONTENIDO de la pÃ¡gina vigente cambiÃ³: una secciÃ³n que arma cards en caliente â€”los
    /// resultados de la comparaciÃ³n de planes, ver NucleosPage.BuildComparisonâ€” pide acÃ¡ que la
    /// bÃºsqueda de paneles se rehaga YA. Es la misma mecÃ¡nica que <see cref="PanelsChanged"/>, pero
    /// sin cambio de pÃ¡gina: el barrido periÃ³dico igual las habrÃ­a encontrado, y solo cuando otro
    /// cambio de layout lo disparara â€” con la ventana quieta, las cards nuevas se quedaban sin vidrio.
    /// </summary>
    /// <summary>
    /// Repinta las superficies que quedaron con una INSTANCIA VIEJA. Cuando el ajuste cambia de camino
    /// (encender o apagar el vidrio) reemplaza el pincel en el diccionario, y los elementos que ya
    /// existían —o que el código pintó a mano— no vuelven a resolver el {ThemeResource}: se quedaban con
    /// el relleno plano para siempre (el caso típico: la página que se abrió antes de que el vidrio
    /// entrara en juego). Acá se recorre el árbol y se cambia la instancia vieja por la nueva.
    ///
    /// El mapa se limpia SIEMPRE que se pudo recorrer: son pinceles que ya no existen en ninguna parte
    /// del ajuste, así que volver a intentarlo en la próxima pasada no cambiaría nada.
    /// </summary>
    internal static void SwapReplacedSurfaces(System.Collections.Generic.Dictionary<Brush, Brush> replaced)
    {
        try
        {
            if (_layer == null) return;   // todavía no hay ventana: se reintenta en la próxima pasada

            DependencyObject root = _layer;
            for (var parent = VisualTreeHelper.GetParent(root); parent != null; parent = VisualTreeHelper.GetParent(root))
                root = parent;

            int swapped = 0;
            int replacedCount = replaced.Count;
            SwapIn(root, 0, replaced, ref swapped);
            replaced.Clear();

            if (swapped > 0)
                PanelAppearance.Diag($"desenfoque: {swapped} superficies repintadas con el pincel nuevo al cambiar de camino (tenían el viejo)");
            else
                // Nadie del árbol tenía puesto el pincel viejo, así que quedan superficies con el
                // relleno anterior —el caso típico: la página que ya estaba abierta— hasta que su
                // elemento se recree. Queda en el log porque desde afuera esto se ve como "el ajuste
                // no se aplica hasta cambiar de página": si vuelve a aparecer, es acá donde se ve.
                PanelAppearance.Diag($"desenfoque: {replacedCount} pincel(es) reemplazados y NINGÚN elemento del árbol tenía el viejo (nada que repintar)");
        }
        catch
        {
            // Un árbol a medio armar no puede romper la apariencia: se reintenta en la próxima pasada.
        }
    }

    private static void SwapIn(DependencyObject parent, int depth,
        System.Collections.Generic.Dictionary<Brush, Brush> replaced, ref int swapped)
    {
        if (depth > 40 || replaced.Count == 0) return;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is not FrameworkElement element || ReferenceEquals(element, _layer)) continue;

            if (BackgroundOf(element) is { } fill && replaced.TryGetValue(fill, out var next))
            {
                switch (element)
                {
                    case Border border: border.Background = next; swapped++; break;
                    case Panel panel: panel.Background = next; swapped++; break;
                    case Control control: control.Background = next; swapped++; break;
                }
            }

            SwapIn(child, depth + 1, replaced, ref swapped);
        }
    }

    /// <summary>
    /// Corre la auditoría después del pase de medida en curso (una línea por cambio de página o de
    /// contenido): sin esto, la foto de la auditoría sería la del árbol a medio medir, con las
    /// superficies nuevas en 0x0.
    /// </summary>
    private static void AuditLater(string reason)
    {
        try { _layer?.DispatcherQueue.TryEnqueue(() => AuditSurfaces(reason)); } catch { }
    }

    internal static void ContentChanged(string detail)
    {
        _forceRebuild = true;
        AuditLater(detail);
        PanelAppearance.Diag($"desenfoque: {detail} â€” se rehace la bÃºsqueda de paneles");
        Invalidate();
    }

    /// <summary>Reengancha el refresco por layout (solo mientras el desenfoque estÃ¡ encendido).</summary>
    private static void Hook()
    {
        if (_hooked || _root == null) return;
        _hooked = true;
        _root.LayoutUpdated += (_, _) => UpdatePatches();
    }

    /// <summary>
    /// Engancha el scroll de un ScrollViewer al refresco de los parches, UNA vez por control.
    ///
    /// POR QUÃ‰ HACE FALTA: mover un ScrollViewer es una traslaciÃ³n de la capa de composiciÃ³n â€”el
    /// contenido no se vuelve a medirâ€”, asÃ­ que el refresco por layout no se entera y los parches se
    /// quedaban donde estaban: el vidrio no acompaÃ±aba a las cards durante el scroll y, con la
    /// transparencia alta (que es cuando el panel se lee justo por el vidrio que tiene detrÃ¡s), el
    /// panel parecÃ­a desaparecer hasta el prÃ³ximo pase de layout.
    ///
    /// Los dos eventos marcan la ACTIVIDAD del scroll â€”su principio y el final de cada pasoâ€”, que es
    /// lo que prende y estira el refresco por frame (ver <see cref="ScrollActivity"/>): el seguimiento
    /// fino lo hace el frame, no el aviso.
    ///
    /// SOLO EL SCROLL VERTICAL MUEVE PARCHES: un ScrollViewer interno de scroll horizontal â€”como el
    /// del grÃ¡fico de temperatura de NÃºcleos, que hace ChangeView CADA SEGUNDO para seguir la Ãºltima
    /// muestraâ€” no desplaza ninguna card, y sin este filtro cada uno de esos pasos prendÃ­a el refresco
    /// por frame de TODA la ventana (250 ms por paso, o sea modo "scroll" permanente a 1 Hz). Con los
    /// transforms a medio camino del auto-scroll, cada pase medÃ­a fetas de 1-3 px y las cards
    /// parpadeaban sin que nadie tocara nada.
    ///
    /// El handler solo captura un double por valor (el Ãºltimo offset vertical): no retiene al
    /// ScrollViewer, que muere solo, y la lista de enganchados se limpia sola. Se enganchan los DOS
    /// eventos: ViewChanging avisa antes de
    /// que arranque el movimiento y ViewChanged despuÃ©s de cada paso, asÃ­ el refresco por frame estÃ¡
    /// prendido desde el primer frame de la animaciÃ³n.
    /// </summary>
    private static void HookScrollViewer(ScrollViewer viewer)
    {
        for (int i = _scrollHooks.Count - 1; i >= 0; i--)
        {
            if (!_scrollHooks[i].TryGetTarget(out var known)) _scrollHooks.RemoveAt(i);
            else if (ReferenceEquals(known, viewer)) return;
        }

        _scrollHooks.Add(new WeakReference<ScrollViewer>(viewer));

        // Acá NO se elige ningún scroller global: con varios ScrollViewers en la ventana, el elegido
        // podía ser el del menú lateral y el delta de las cards quedaba en cero (ver _patchScrolls).
        // Cada parche guarda su scroller en el pase que lo pinta.

        double lastVertical = double.NaN;
        viewer.ViewChanging += (_, e) =>
        {
            double next = e.NextView.VerticalOffset;
            if (next != lastVertical)
            {
                lastVertical = next;
                ScrollActivity();
            }
        };
        viewer.ViewChanged += (_, _) =>
        {
            double now = viewer.VerticalOffset;
            if (now != lastVertical)
            {
                lastVertical = now;
                ScrollActivity();
            }
        };
    }

    /// <summary>
    /// Avisa que hay actividad de scroll y se asegura de que la capa se refresque por FRAME mientras
    /// dure (ver <see cref="OnRendering"/>) mÃ¡s un ratito despuÃ©s, para el Ãºltimo paso de la animaciÃ³n.
    ///
    /// POR QUÃ‰ POR FRAME: el scroll no pasa por un pase de layout ni por un pase de texto â€”lo hace la
    /// capa de composiciÃ³nâ€”, asÃ­ que los avisos de <see cref="ScrollViewer.ViewChanged"/> llegan al
    /// final de cada paso, no en cada frame. Con la transparencia alta el panel se lee por el vidrio
    /// de atrÃ¡s: un parche que llega un paso tarde deja a la card sobre la foto nÃ­tida y se ve como
    /// que el blur desapareciÃ³. Refrescando por frame, el parche va con el contenido.
    /// </summary>
    private static void ScrollActivity()
    {
        _lastScrollSignal = Environment.TickCount;
        if (_frameWatch) return;

        try
        {
            // Ráfaga nueva: la próxima vez que un parche se mueva queda su línea en el log (ver LogShift).
            _shiftLogged = false;
            CompositionTarget.Rendering += OnRendering;
            _frameWatch = true;
            PanelAppearance.Diag("desenfoque: scroll â€” los parches se refrescan por frame");
        }
        catch (Exception ex)
        {
            // Sin enganche por frame queda el refresco de ViewChanged y de layout, que es lo que
            // habÃ­a antes: peor cadencia, pero nunca sin vidrio.
            PanelAppearance.Diag($"desenfoque: no se pudo enganchar el refresco por frame ({ex.Message})");
        }
    }

    /// <summary>
    /// Un frame con el scroll en movimiento: los parches se reubican con la MISMA cadencia a la que la
    /// composiciÃ³n mueve el contenido, asÃ­ el vidrio no se despega de su card. Se corta solo cuando el
    /// scroll lleva <see cref="ScrollSettleMs"/> sin moverse â€” sin este corte, el evento corre en cada
    /// frame que la app renderiza y el refresco quedarÃ­a prendido para siempre.
    /// </summary>
    private static void OnRendering(object? sender, object e)
    {
        if (Environment.TickCount - _lastScrollSignal > ScrollSettleMs)
        {
            StopFrameWatch();
            return;
        }

        // Durante el scroll en movimiento NO se re-mide. TransformToVisual devuelve la posiciÃ³n del
        // LAYOUT, que va un paso atrÃ¡s de la animaciÃ³n de composiciÃ³n: re-medir acÃ¡ â€”y sobre todo
        // re-medir DESPUÃ‰S de haber desplazado el parche, pisÃ¡ndoloâ€” era justo lo que hacÃ­a que el
        // vidrio se despegara de la card, se encogiera a fetas y parpadeara. Los parches siguen al
        // contenido con el delta REAL del scroll (ShiftPatches), que es exacto; el residuo lo corrige
        // la Ãºnica re-mediciÃ³n de cuando el scroll se asienta (ver StopFrameWatch).
        // El vidrio declarativo intenta pasar al compositor por expresión (si apareciera una fuente
        // animada); el pase por frame se corre igual, porque es el que mueve los recortes del vidrio
        // mientras no haya expresión (ver PanelGlass.Follow).
        PanelGlass.Pump();
        ShiftPatches();
    }

    /// <summary>
    /// Desplaza verticalmente cada parche visible con el scroll de SU scroller (ver
    /// <see cref="_patchScrolls"/>): mueve el rectángulo Y la fase del pincel juntos, así el vidrio
    /// sigue mostrando el pedazo de foto que le toca.
    ///
    /// LA CUENTA ES POR PARCHE: posición = baseMedida - (offsetAhora - suCero). No se acumulan deltas
    /// de eventos (llegan agrupados y a destiempo) y no hay un scroller "de la ventana": el navbar y
    /// la barra de título no tienen ninguno y por eso no se mueven nunca.
    ///
    /// EL MOVIMIENTO VA POR LA TRANSLATION DEL ELEMENTO, NO POR EL LAYOUT. Escribir <c>Canvas.Top</c>
    /// es un cambio de LAYOUT, y con el scroll corriendo el layout queda diferido: la posición se
    /// aplicaba tarde mientras la fase del pincel —un cambio de render, no de layout— ya estaba puesta.
    /// Separadas, la fase deja de muestrear la foto en el pedazo que le toca y, fuera de la foto, el
    /// pincel no pinta NADA: eso es el "el blur desaparece al scrollear". La Translation entra en el
    /// mismo pase que la fase (ver <see cref="SetPatchTranslation"/>), así que las dos viajan juntas y
    /// el layout no se toca en medio del scroll.
    /// </summary>
    private static void ShiftPatches()
    {
        if (_layer == null || _patches.Count == 0) return;

        try
        {
            int moved = 0;
            for (int i = 0; i < _patches.Count; i++)
            {
                if (i >= _patchScrolls.Count || i >= _patchBaseTop.Count) continue;

                var state = _patchScrolls[i];
                var scroller = state.Scroller;
                if (scroller == null) continue;

                var patch = _patches[i];

                double delta = scroller.VerticalOffset - state.OffsetBase;
                if (delta == 0) continue;

                double baseTop = _patchBaseTop[i];
                double next = baseTop - delta;

                // CON EL VIDRIO DECLARATIVO EN MARCHA EL PARCHE XAML NO SE TOCA: la posición va al recorte
                // de composición, que es una propiedad de RENDER —sin layout, sin fase de pincel y sin
                // recorte que recalcular—. Es el mismo next de siempre, aplicado donde no puede quedar a
                // medio camino de la imagen (ver PanelGlass.Follow).
                if (PanelGlass.Live)
                {
                    PanelGlass.Follow(i, next);
                    moved++;
                    if (Math.Abs(delta) > _shiftMaxDelta) _shiftMaxDelta = Math.Abs(delta);
                    continue;
                }

                // POSICIÓN Y FASE TIENEN QUE ENTRAR EN EL MISMO PASE, y para eso el desplazamiento va
                // por la Translation del elemento (post-layout) y NO por Canvas.Top: el top es un
                // cambio de LAYOUT, y con el scroll corriendo el layout queda diferido, así que la
                // posición se aplicaba tarde mientras la fase —un cambio de render, no de layout— ya
                // estaba puesta. Separadas, la fase deja de muestrear la foto en el pedazo que le
                // toca; fuera de la foto el pincel no pinta NADA, y eso es lo que se veía como "el
                // blur desaparece al scrollear". La Translation entra en el mismo pase que la fase
                // (ver SetPatchTranslation), así que las dos vuelven a viajar juntas.
                SetPatchTranslation(patch, next - baseTop);
                if (Canvas.GetTop(patch) != baseTop) Canvas.SetTop(patch, baseTop);
                if (patch.Fill is ImageBrush brush
                    && brush.Transform is TransformGroup group
                    && group.Children.Count == 2
                    && group.Children[1] is TranslateTransform translate)
                {
                    translate.Y = -next;
                }

                // El parche puede venir APAGADO (una card que estaba fuera de la vista) y entrar al
                // recorte con este desplazamiento: el recorte se recalcula por frame, así que el vidrio
                // aparece en el acto y no recién cuando el scroll se asienta (ver ApplyPatchClip).
                ApplyPatchClip(patch, state, Canvas.GetLeft(patch), next, patch.Width, patch.Height, baseTop);

                moved++;
                if (Math.Abs(delta) > _shiftMaxDelta) _shiftMaxDelta = Math.Abs(delta);
            }

            if (moved > 0)
            {
                _shiftFrames++;
                LogShift(moved);
            }
        }
        catch
        {
            // Cosmético: si un parche está a medio construir, la re-medición de abajo lo deja bien.
        }
    }

    /// <summary>
    /// Deja la Translation del parche en <paramref name="shift"/> puntos verticales. Es EL canal del
    /// seguimiento del scroll (ver <see cref="ShiftPatches"/>): un cambio post-layout, que entra al
    /// render en el mismo pase que la fase del pincel y no invalida la disposición del Canvas.
    ///
    /// Se escribe la DP del elemento —<c>UIElement.Translation</c>, habilitada en <see cref="AddPatch"/>—
    /// y NO la propiedad suelta del visual: XAML reescribe esa propiedad desde su DP en cada pase, así
    /// que escribiéndola a mano el valor quedaba en cero y el parche clavado en su lugar base mientras
    /// el contenido se iba (el seguimiento "andaba" en el log y no en la pantalla).
    /// Devuelve true si el valor cambió, que es cuando el pase pide el empujón de layout.
    /// </summary>
    private static bool SetPatchTranslation(Rectangle patch, double shift)
    {
        if (patch.Translation.Y == (float)shift) return false;
        patch.Translation = new Vector3(0, (float)shift, 0);
        return true;
    }

    /// <summary>
    /// Aplica al parche su ZONA VISIBLE: lo recorta a lo que el panel muestra de verdad —la zona del
    /// scroller guardada en el último pase, ver <see cref="PatchScroll.Viewport"/>— y lo apaga cuando el
    /// recorte queda vacío. Una card que salió de la vista no puede seguir pintando vidrio fuera de su
    /// scroller: eso se ve como una franja borroneada sobre el navbar y el menú.
    ///
    /// Se recalcula con el parche ya DESPLAZADO, así que una card que entra a la vista durante el
    /// scroll se enciende sola: su parche ya estaba autorizado con la caja completa (ver UpdatePatches).
    /// Un parche sin zona conocida —los de repuesto, ver TryGhostBounds— se deja como está.
    /// </summary>
    private static void ApplyPatchClip(Rectangle patch, PatchScroll state, double left, double top, double width, double height, double localTop)
    {
        var viewport = state.Viewport;
        if (viewport.Width < 1 || viewport.Height < 1) return;

        var visible = Intersect(new Rect(left, top, width, height), viewport);
        if (visible.Width < 1 || visible.Height < 1)
        {
            if (patch.Visibility != Visibility.Collapsed) patch.Visibility = Visibility.Collapsed;
            return;
        }

        var clip = state.Clip ??= new RectangleGeometry();
        // El recorte se aplica en el espacio LOCAL del parche, y con el scroll en movimiento el parche
        // ya no está en su top de layout: el rectángulo visible se mide en pantalla (top) y se expresa
        // contra el top de layout (localTop), que es el origen del elemento antes de la Translation
        // (ver ShiftPatches).
        var rect = new Rect(visible.Left - left, visible.Top - localTop, visible.Width, visible.Height);
        if (clip.Rect != rect) clip.Rect = rect;

        if (!ReferenceEquals(patch.Clip, clip)) patch.Clip = clip;
        if (patch.Visibility != Visibility.Visible) patch.Visibility = Visibility.Visible;
    }

    /// <summary>El rectángulo agrandado <paramref name="amount"/> px por lado (ver <see cref="MarginBeyondView"/>).</summary>
    private static Rect Inflate(Rect rect, double amount)
        => new Rect(rect.X - amount, rect.Y - amount, rect.Width + amount * 2, rect.Height + amount * 2);

    /// <summary>
    /// Deja UNA línea por ráfaga de scroll, en el primer frame que mueve parches: qué scroller está
    /// siguiendo cada parche delantero, con qué offset y —como comprobación— qué dice el property set
    /// de la manipulación del mismo scroller.
    ///
    /// POR QUÉ ES NECESARIO: "los parches no se mueven" no dejaba ningún rastro en el log (el
    /// seguimiento por frame no anotaba nada), así que desde afuera no había forma de saber si el
    /// problema era un scroller que no scrolleaba, un delta en cero o la falta de scroller.
    /// </summary>
    private static void LogShift(int moved)
    {
        if (_shiftLogged) return;
        _shiftLogged = true;

        try
        {
            for (int i = 0; i < _patchScrolls.Count; i++)
            {
                var scroller = _patchScrolls[i].Scroller;
                if (scroller == null) continue;

                string name = string.IsNullOrEmpty(scroller.Name) ? "(sin nombre)" : scroller.Name;
                string props = "property set sin Translation";
                try
                {
                    var set = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scroller);
                    if (set != null)
                    {
                        var status = set.TryGetVector3("Translation", out var translation);
                        if (status == CompositionGetValueStatus.Succeeded)
                            props = $"translation {translation.Y:0.##}";
                    }
                }
                catch { props = "property set no disponible"; }

                // El EFECTO es lo que se anota, no la intención: el top del parche en el layout y la
                // fase que quedó escrita en su pincel. Con esos dos números —más el offset del
                // scroller— se ve si el parche está realmente siguiendo al contenido; contar parches
                // "movidos" ya nos mintió una vez.
                PanelAppearance.Diag(
                    $"desenfoque: scroll en movimiento — {moved} parches siguen a su scroller " +
                    $"[{name} {scroller.ActualWidth:0}x{scroller.ActualHeight:0}, scrollable {scroller.ScrollableHeight:0}, " +
                    $"offset {scroller.VerticalOffset:0.##} (cero {_patchScrolls[i].OffsetBase:0.##}), {props}, " +
                    $"parche base {Canvas.GetTop(_patches[i]):0.#} translation {_patches[i].Translation.Y:0.#} " +
                    $"en composicion {EffectTranslationY(_patches[i]):0.#} fase {PhaseY(_patches[i]):0.#}]");
                return;
            }
        }
        catch
        {
            // El detalle es diagnóstico: nunca puede tumbar el seguimiento.
        }
    }

    /// <summary>Suelta el refresco por frame (el scroll se quedó quieto).</summary>
    private static void StopFrameWatch()
    {
        if (!_frameWatch) return;
        _frameWatch = false;
        try { CompositionTarget.Rendering -= OnRendering; } catch { }

        // Resumen de la ráfaga: cuántos frames movieron parches y cuál fue el mayor corrimiento. Es el dato
        // que distingue un VerticalOffset que avanza en cada frame —muchos frames, saltos chicos— de
        // uno que salta directo al destino: pocos frames con un salto grande (ver _shiftMaxDelta).
        if (_shiftFrames > 0)
            PanelAppearance.Diag($"desenfoque: scroll — la ráfaga movió parches en {_shiftFrames} frames (corrimiento máximo {_shiftMaxDelta:0.#} px)");
        _shiftFrames = 0;
        _shiftMaxDelta = 0;

        // El scroll se asentó: es el ÚNICO momento en que se re-mide. Con el contenido quieto,
        // TransformToVisual ya da la posición real y el pase corrige de una vez el residuo que
        // dejó el seguimiento por delta. Sin este pase final, una card que entró a la vista
        // durante el scroll se quedaba sin vidrio hasta el próximo cambio de layout.
        UpdatePatches();
        PanelAppearance.Diag("desenfoque: scroll quieto — los parches vuelven al refresco por layout");
    }

    /// <summary>
    /// Â¿AlgÃºn panel que estaba cargado dejÃ³ de estarlo? Es lo que pasa cuando la pÃ¡gina RECREA sus
    /// cards â€”los refrescos periÃ³dicos de datos (Sistema, NÃºcleosâ€¦) vuelven a armar sus tarjetasâ€”: la
    /// card nueva todavÃ­a no estÃ¡ en la lista, asÃ­ que conviene rebuscar YA (ver
    /// <see cref="RebuildPanels"/>) en vez de esperar al intervalo.
    ///
    /// Avisa UNA sola vez por desprendimiento (deja anotado que ese panel ya no estaba cargado): si
    /// no, una card que se fue de la pÃ¡gina quedarÃ­a pidiendo un recorrido del Ã¡rbol en cada pase â€”y
    /// con el refresco por frame eso es un recorrido por frameâ€” hasta que venza su plazo de gracia.
    /// </summary>
    private static bool HasDetachedPanels()
    {
        bool detached = false;
        for (int i = _panels.Count - 1; i >= 0; i--)
        {
            if (!_panels[i].TryGetTarget(out var element))
            {
                RemovePanelAt(i);
                continue;
            }

            if (element.IsLoaded)
            {
                _panelWasLoaded[i] = true;
            }
            else if (_panelWasLoaded[i])
            {
                _panelWasLoaded[i] = false;
                // La pÃ¡gina estÃ¡ rearmando cards: se abre la ventana para que la bÃºsqueda corra en
                // cada pase y la card nueva entre a la lista apenas exista (ver MarkRearm).
                MarkRearm();
                detached = true;
            }
        }
        return detached;
    }

    /// <summary>
    /// Rebusca los paneles en el Ã¡rbol: son los elementos cuyo Background es alguno de los pinceles
    /// de superficie del tema (o uno de los extra). Se refresca cada tanto y no en cada layout
    /// â€”navegar entre pÃ¡ginas destruye y crea cards todo el tiempoâ€”, y la llaman tanto el cambio de
    /// tema o de deslizador (ver <see cref="Invalidate"/>) como el refresco por layout (ver
    /// <see cref="UpdatePatches"/>), que es el que cubre la navegaciÃ³n.
    /// </summary>
    private static void RebuildPanels()
    {
        if (_root == null || _layer == null || !_enabled) return;

        int now = Environment.TickCount;

        // El intervalo vale SIEMPRE (antes solo cuando la lista ya tenÃ­a paneles): la bÃºsqueda ahora
        // tambiÃ©n corre desde el refresco por layout, y ahÃ­ no puede costar un recorrido del Ã¡rbol
        // por cada pase. <see cref="PanelsChanged"/> la saltea con _forceRebuild al navegar.
        //
        // Y la saltea TAMBIÃ‰N cuando alguno de los paneles vigentes ya no estÃ¡ en el Ã¡rbol (ver
        // HasDetachedPanels) o cuando la ventana de rearme estÃ¡ abierta (ver _churnUntil): ahÃ­ la
        // lista quedÃ³ vieja y esperar al intervalo dejaba a la card sin vidrio justo cuando la pÃ¡gina
        // se repinta.
        bool churning = now < _churnUntil;
        int interval = churning ? ChurnIntervalMs : RebuildIntervalMs;
        if (!_forceRebuild && !HasDetachedPanels() && now - _lastRebuild < interval) return;
        _forceRebuild = false;
        _lastRebuild = now;

        RefreshMatchSet();

        var found = new List<FrameworkElement>();
        Collect(_root, 0, found);

        // Un candidato que cubre la ventana casi entera es la superficie de fondo del tema (ver
        // MaxPanelCoverage) y se descarta: su parche no recortarÃ­a un panel, taparÃ­a todo con la
        // foto desenfocada y los parches de las cards se fundirÃ­an con Ã©l.
        double windowArea = _root.ActualWidth * _root.ActualHeight;
        _rebuildWithoutSize = windowArea <= 1;
        int backgrounds = 0;
        string discarded = "ninguna";
        string biggest = "â€”";
        double biggestArea = 0;
        int seen = 0;
        foreach (var element in found)
        {
            // Desprendido de la ventana (el Frame se queda con la pÃ¡gina anterior entera): sigue
            // vivo como objeto pero su posiciÃ³n ya no existe, asÃ­ que no puede ser un panel.
            if (!element.IsLoaded) continue;

            double area = element.ActualWidth * element.ActualHeight;
            if (windowArea > 1 && area > MaxPanelCoverage * windowArea)
            {
                // Queda anotado en el log QUIÃ‰N es: es la comprobaciÃ³n de que el filtro saca la
                // superficie de fondo (el NavigationView y su SplitView) y no una card grande.
                string name = $"{element.GetType().Name} {element.ActualWidth:0}x{element.ActualHeight:0}";
                backgrounds++;
                discarded = backgrounds == 1 ? name : discarded + ", " + name;
                continue;
            }

            // FUSIÃ“N, no reemplazo: la lista NO se vacÃ­a antes del recorrido. Vaciarla era lo que
            // apagaba los parches de las cards que este pase no alcanzÃ³ a ver â€”la pÃ¡gina las estÃ¡
            // rearmando mientras se scrolleaâ€” y se veÃ­a como que el desenfoque se iba: el log de la
            // sesiÃ³n 07:04 muestra pases de 13 parches seguidos de pases de 4 sin ningÃºn panel
            // anotado como "sin parche", o sea que los paneles directamente habÃ­an salido de la
            // lista. Los que ya no existen se van solos: desprendidos quedan fuera del pase (ver
            // UpdatePatches) y PrunePanels los saca al vencer su plazo de gracia.
            MergePanel(element, now);
            seen++;

            if (area > biggestArea)
            {
                biggestArea = area;
                biggest = $"{element.GetType().Name} {element.ActualWidth:0}x{element.ActualHeight:0}";
            }
        }

        PrunePanels(now);

        LogPanelCount(seen, biggest, discarded, churning);

        LogPanels();
        LogOccluders();
    }

    /// <summary>
    /// Anota cuÃ¡ntos paneles encontrÃ³ la bÃºsqueda, cuÃ¡ntos hay en la lista y cuÃ¡l es el mÃ¡s grande:
    /// es la forma de distinguir, sin mirar la ventana, si lo que se desenfoca son las cards o un
    /// contenedor grande. La lista es por FUSIÃ“N (ver <see cref="MergePanel"/>) asÃ­ que el segundo
    /// nÃºmero puede ser mayor: son paneles viejos que esperan su plazo de gracia. Se anota solo cuando
    /// el resumen CAMBIA, y como mucho cada 300 ms: durante el rearme de una pÃ¡gina la bÃºsqueda corre
    /// en cada pase y si no la misma lÃ­nea taparÃ­a el log.
    /// </summary>
    private static void LogPanelCount(int seen, string biggest, string discarded, bool churning)
    {
        try
        {
            string signature = $"{seen}/{_panels.Count}/{biggest}/{discarded}/{churning}/{_match.Count}/{_matchColors.Count}/{_borderMatch.Count}";
            if (signature == _lastPanelCountDetail) return;

            int now = Environment.TickCount;
            if (now - _lastPanelCountLog < 300) return;
            _lastPanelCountDetail = signature;
            _lastPanelCountLog = now;

            PanelAppearance.Diag(
                $"desenfoque: {seen} paneles en este pase (de {_panels.Count} en la lista{(churning ? ", rearme" : "")}; mayor: {biggest}); fondo descartado: {discarded}; pinceles vigilados: {_match.Count} ({_matchColors.Count} colores, {_borderMatch.Count} bordes)");
        }
        catch
        {
            // El detalle es diagnÃ³stico: nunca puede tumbar el desenfoque.
        }
    }

    /// <summary>
    /// Marca que la pÃ¡gina estÃ¡ rearmando sus cards: abre la ventana de rebÃºsqueda y la de parches de
    /// repuesto (ver <see cref="_churnUntil"/> y <see cref="_ghostUntil"/>). La llaman las tres seÃ±ales
    /// que lo delatan: una referencia muerta, un panel que dejÃ³ de estar cargado, y el propio recorrido
    /// cuando se topa con cualquiera de las dos.
    ///
    /// POR QUÃ‰ ES UNA MARCA EXPLÃCITA: el parche de repuesto no puede quedar puesto siempre â€”en una
    /// pÃ¡gina quieta, un panel que se va tiene que apagarse, y una pestaÃ±a interna que se oculta no
    /// puede dejar sus cards desenfocadas sobre la pestaÃ±a nuevaâ€”. La marca distingue "la pÃ¡gina estÃ¡
    /// rearmando" de "el contenido cambiÃ³ porque el usuario quiso".
    /// </summary>
    private static void MarkRearm()
    {
        int now = Environment.TickCount;
        _churnUntil = now + PanelChurnMs;
        _ghostUntil = now + GhostMs;
    }

    /// <summary>
    /// Saca de TODAS las listas paralelas al panel de esa posiciÃ³n (ver <see cref="_panels"/>): la
    /// lista de paneles y sus tres acompaÃ±antes tienen que quedar siempre alineadas por Ã­ndice.
    /// </summary>
    private static void RemovePanelAt(int index)
    {
        _panels.RemoveAt(index);
        _panelSeen.RemoveAt(index);
        _panelWasLoaded.RemoveAt(index);
        _panelBounds.RemoveAt(index);
        _panelRadius.RemoveAt(index);
    }

    /// <summary>
    /// La geometrÃ­a del parche que hay que DEJAR PUESTO cuando un panel acaba de desaparecer.
    ///
    /// POR QUÃ‰: los refrescos de datos de una pÃ¡gina destruyen y rearman sus cards; entre que la vieja
    /// se va y la nueva entra a la lista, el parche se apagaba y la card quedaba sin vidrio â€” con la
    /// transparencia alta, un parpadeo bien visible (el reporte "desaparece y aparece varias veces").
    /// La card nueva cae en el mismo lugar que la vieja, asÃ­ que durante el rearme se sigue pintando la
    /// Ãºltima geometrÃ­a: el vidrio no se apaga y, cuando la bÃºsqueda encuentra la card nueva, el parche
    /// pasa a medirse de verdad.
    ///
    /// Solo aplica con la ventana de rearme abierta (ver <see cref="_churnUntil"/>) y dentro del plazo
    /// de gracia del panel: en una pÃ¡gina quieta, un panel que se va tiene que apagarse, no quedarse
    /// como una mancha desenfocada sobre lo que ahora hay en su lugar.
    /// </summary>
    private static bool TryGhostBounds(int index, int now, out Rect bounds, out double radius)
    {
        bounds = default;
        radius = 0;
        if (now >= _ghostUntil) return false;
        if (now - _panelSeen[index] >= PanelGraceMs) return false;

        var last = _panelBounds[index];
        if (last.Width < 1 || last.Height < 1) return false;

        bounds = last;
        radius = _panelRadius[index];
        return true;
    }

    /// <summary>
    /// Saca de la lista los paneles que el GC ya se llevÃ³, y avisa que hubo rearme.
    ///
    /// POR QUÃ‰ ES UN AVISO Y NO SOLO UNA LIMPIEZA: una referencia muerta significa que la pÃ¡gina
    /// DESTRUYÃ“ cards â€”los refrescos de datos rearman sus tarjetasâ€” y que sus reemplazos pueden estar
    /// ya en el Ã¡rbol. Si no se avisa, esas cards se quedan sin parche hasta el prÃ³ximo barrido del
    /// intervalo, y con la transparencia alta eso se ve como que el desenfoque se fue. Es el caso que
    /// el log deja ver como "13 paneles en la lista, 6 parches, sin parche: ninguno": los 7 que
    /// faltaban eran entradas muertas que nadie contaba.
    /// </summary>
    private static bool DropDeadPanels()
    {
        bool dropped = false;
        for (int i = _panels.Count - 1; i >= 0; i--)
        {
            if (_panels[i].TryGetTarget(out _)) continue;
            RemovePanelAt(i);
            dropped = true;
        }
        return dropped;
    }

    /// <summary>
    /// Suma un panel a la lista o refresca el que ya estaba (ver <see cref="RebuildPanels"/>). Se
    /// reconoce por INSTANCIA: la misma card de un pase al otro sigue siendo la misma entrada, asÃ­
    /// que no se pierde el estado que cuelga de ella (ver <see cref="_panelClips"/>).
    /// </summary>
    private static void MergePanel(FrameworkElement element, int now)
    {
        for (int i = 0; i < _panels.Count; i++)
        {
            if (!_panels[i].TryGetTarget(out var known) || !ReferenceEquals(known, element)) continue;
            _panelSeen[i] = now;
            _panelWasLoaded[i] = true;
            return;
        }

        _panels.Add(new WeakReference<FrameworkElement>(element));
        _panelSeen.Add(now);
        _panelWasLoaded.Add(true);
        _panelBounds.Add(default);
        _panelRadius.Add(0);
    }

    /// <summary>
    /// Saca de la lista a los paneles que ya no existen: los recolectados y los que llevan
    /// <see cref="PanelGraceMs"/> sin aparecer en un pase. El plazo es a propÃ³sito largo â€”una card que
    /// la pÃ¡gina rearma tarda un rato en volver, y sacarla antes era justo lo que la dejaba sin
    /// vidrioâ€” y las entradas que quedan de mÃ¡s no cuestan: desprendidas no entran al pase.
    /// </summary>
    private static void PrunePanels(int now)
    {
        for (int i = _panels.Count - 1; i >= 0; i--)
        {
            if (_panels[i].TryGetTarget(out _) && now - _panelSeen[i] < PanelGraceMs) continue;
            RemovePanelAt(i);
        }
    }

    /// <summary>
    /// Deja constancia de QUÃ‰ elemento es cada panel, de quÃ© tamaÃ±o y por quÃ© entrÃ³ (ver
    /// <see cref="MatchReason"/>). Con los tamaÃ±os se ve, sin mirar la ventana, si lo que se
    /// desenfoca son las cards, los chips, la franja del menÃº y la barra de tÃ­tulo â€”lo que pinta la
    /// transparenciaâ€” o si se colÃ³ un contenedor grande, que es el que borronea tambiÃ©n el espacio
    /// ENTRE las cards (y hace que se lean como una sola pieza).
    /// </summary>
    private static void LogPanels()
    {
        try
        {
            var detail = new List<string>();
            int extra = 0;
            foreach (var reference in _panels)
            {
                if (detail.Count >= 30) { extra++; continue; }
                if (!reference.TryGetTarget(out var element)) continue;
                string name = string.IsNullOrEmpty(element.Name) ? "" : $" [{element.Name}]";
                detail.Add($"{element.GetType().Name}{name} {element.ActualWidth:0}x{element.ActualHeight:0} ({MatchReason(element) ?? "?"})");
            }

            string signature = string.Join(" | ", detail);
            if (signature == _lastPanelDetail) return;
            _lastPanelDetail = signature;
            PanelAppearance.Diag($"desenfoque: paneles ({detail.Count}{(extra > 0 ? $" + {extra}" : "")}) = {signature}");
        }
        catch
        {
            // El detalle es diagnÃ³stico: nunca puede tumbar el desenfoque.
        }
    }

    /// <summary>
    /// Deja constancia del RECTÃNGULO de cada parche visible. Es el complemento de
    /// <see cref="LogPanels"/>: con las dos listas se ve exactamente quÃ© zona se estÃ¡ borroneando y
    /// a quÃ© elemento corresponde â€” por ejemplo, si los parches son mÃ¡s grandes que las cards (y
    /// por eso el espacio entre ellas tambiÃ©n queda desenfocado, que es lo que las hace leer como
    /// una sola pieza). Se anota solo cuando la geometrÃ­a CAMBIA, y como mucho cada 300 ms: esto
    /// corre en cada pase de layout.
    ///
    /// El resumen de SIN PARCHE va en la MISMA lÃ­nea que la geometrÃ­a: antes eran dos avisos con su
    /// propio reloj cada uno, asÃ­ que las dos lÃ­neas del log no describÃ­an el mismo pase y no se
    /// podÃ­a saber por quÃ© faltaba un parche (ver <see cref="_skipped"/>).
    /// </summary>
    private static void LogPatches(double windowWidth, double windowHeight)
    {
        try
        {
            int now = Environment.TickCount;
            if (now - _lastPatchLog < 300) return;

            var detail = new List<string>();
            foreach (var patch in _patches)
            {
                if (patch.Visibility != Visibility.Visible) continue;
                if (detail.Count >= 30) break;
                detail.Add($"{Canvas.GetLeft(patch):0},{Canvas.GetTop(patch):0} {patch.Width:0}x{patch.Height:0}");
            }

            var skipped = new List<string>();
            if (_skipped[0] > 0) skipped.Add($"{_skipped[0]} desprendidos");
            if (_skipped[1] > 0) skipped.Add($"{_skipped[1]} sin medir");
            if (_skipped[2] > 0) skipped.Add($"{_skipped[2]} fuera de vista");
            if (_skipped[3] > 0) skipped.Add($"{_skipped[3]} cubren la ventana");
            if (_skipped[4] > 0) skipped.Add($"{_skipped[4]} con error al medir");
            if (_skipped[5] > 0) skipped.Add($"{_skipped[5]} sin referencia");
            string without = skipped.Count == 0 ? "ninguno" : string.Join(", ", skipped);

            string geometry = string.Join(" | ", detail);
            string signature = geometry + " â€– " + without;
            if (signature == _lastPatchDetail) return;
            _lastPatchDetail = signature;
            _lastPatchLog = now;
            PanelAppearance.Diag($"desenfoque: {detail.Count} parches sobre ventana {windowWidth:0}x{windowHeight:0} = {geometry}; sin parche: {without}");
        }
        catch
        {
            // El detalle es diagnÃ³stico: nunca puede tumbar el desenfoque.
        }
    }

    /// <summary>
    /// AUDITORÃA DE SUPERFICIES: recorre la ventana y deja en el log, por cada superficie que el ajuste
    /// deberÃ­a estar pintando, QUÃ‰ relleno tiene de verdad â€”vidrio del compositor, acrÃ­lico o el sÃ³lido
    /// del temaâ€” y si hay una capa opaca encima. Es la respuesta a "tal card / tal navbar interna no
    /// tiene desenfoque", y distingue las dos Ãºnicas causas posibles:
    ///
    /// - <b>relleno plano</b>: la instancia que el elemento tiene puesta es la vieja. Pasa con las
    ///   superficies creadas ANTES de que el vidrio entrara en juego (el cambio de sÃ³lido a vidrio
    ///   reemplaza la instancia en el diccionario, y un elemento creado antes no vuelve a resolver el
    ///   {ThemeResource}) y con lo que se pinta a mano con un sÃ³lido (ver ThemeBrushes.GetSurface).
    /// - <b>relleno vidrio + capa opaca encima</b>: el problema no es el pincel sino lo que hay detrÃ¡s;
    ///   el backdrop que el motor desenfoca es esa capa, asÃ­ que no se ve ningÃºn cambio.
    ///
    /// Se anota como mucho cada 1,5 s y solo cuando el resultado CAMBIA: con el Ã¡rbol quieto no repite
    /// la misma lÃ­nea. Solo se listan las superficies PLANAS (las que tienen vidrio no necesitan
    /// explicaciÃ³n) mÃ¡s un resumen con el total.
    /// </summary>
    internal static void AuditSurfaces(string reason)
    {
        try
        {
            if (_layer == null) return;
            int now = Environment.TickCount;
            if (_lastAuditLog != 0 && now - _lastAuditLog < 1500) return;

            // La raÃ­z de la ventana: se sube desde la capa (que cuelga del fondo de la ventana) hasta
            // arriba del todo. AsÃ­ la auditorÃ­a funciona tambiÃ©n con la capa APAGADA (el caso del
            // vidrio del compositor), que es justo cuando mÃ¡s falta hace.
            DependencyObject root = _layer;
            for (var parent = VisualTreeHelper.GetParent(root); parent != null; parent = VisualTreeHelper.GetParent(root))
                root = parent;

            var surfaces = new List<(FrameworkElement Element, string Why, Brush Fill)>();
            CollectSurfaces(root, 0, surfaces);
            if (surfaces.Count == 0) return;

            int glass = 0;
            var flat = new List<string>();
            foreach (var (element, why, fill) in surfaces)
            {
                string kind = FillKind(fill);
                if (kind.StartsWith("vidrio", StringComparison.Ordinal) || kind == "acrÃ­lico")
                {
                    glass++;
                    continue;
                }

                flat.Add($"[{NameOf(element)}] {element.GetType().Name} {element.ActualWidth:0}x{element.ActualHeight:0} " +
                         $"clave={KeyOf(fill) ?? "?"} ({why}) â†’ {kind}{Occluder(element)}");
            }

            string signature = $"{surfaces.Count}|{glass}|{string.Join(";", flat)}";
            if (signature == _lastAudit) return;
            _lastAudit = signature;
            _lastAuditLog = now;

            PanelAppearance.Diag($"auditorÃ­a ({reason}): {surfaces.Count} superficies â€” {glass} con vidrio, {flat.Count} sin vidrio");
            foreach (var line in flat.Take(12)) PanelAppearance.Diag($"    {line}");

            // Las BARRAS INTERNAS de las páginas (Núcleos, Limpieza, Configuración) se anotan SIEMPRE,
            // con vidrio o sin él: son las superficies del reporte "tal navbar no tiene desenfoque", así
            // que su relleno tiene que poder leerse en el log sin depender de que entren a la lista de
            // las planas. Ver PanelAppearance.PaneBarFill.
            foreach (var (element, _, fill) in surfaces)
            {
                if (element.Name is "ConfigNavBar" or "PlanNavBar" or "LimpiezaNavBar")
                {
                    PanelAppearance.Diag($"    barra interna [{element.Name}]: {FillKind(fill)} " +
                                         $"{element.ActualWidth:0}x{element.ActualHeight:0}{Occluder(element)}");
                }
            }
        }
        catch
        {
            // Un Ã¡rbol a medio armar no puede romper nada: esto es diagnÃ³stico.
        }
    }

    /// <summary>
    /// Junta las superficies de un Ã¡rbol: las que reconoce el censo de paneles (por pincel, por color o
    /// por marco) mÃ¡s las que llevan un pincel de vidrio, que son superficies por definiciÃ³n aunque el
    /// censo no las conozca.
    /// </summary>
    private static void CollectSurfaces(DependencyObject parent, int depth,
        List<(FrameworkElement Element, string Why, Brush Fill)> found)
    {
        if (depth > 40 || found.Count > 4000) return;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is not FrameworkElement element || ReferenceEquals(element, _layer)) continue;

            var fill = BackgroundOf(element);
            string? why = MatchReason(element);
            if (why == null && fill is not GlassBlurBrush && fill is not AcrylicBrush) why = null;
            else if (why == null) why = "vidrio propio";

            if (why != null && fill != null) found.Add((element, why, fill));
            CollectSurfaces(child, depth + 1, found);
        }
    }

    /// <summary>La clave de diccionario que estÃ¡ pintando ese relleno, si se puede saber (pincel del tema, live o de superficie).</summary>
    private static string? KeyOf(Brush? fill)
    {
        if (fill == null) return null;
        foreach (var key in PanelAppearance.SurfaceKeys)
        {
            if (PanelAppearance.SurfaceBrush(key) is { } fromTheme && ReferenceEquals(fromTheme, fill)) return key;
            if (ThemeBrushes.TryGetLive(key, out var live) && ReferenceEquals(live, fill)) return key;
            if (ThemeBrushes.SurfaceBrush(key) is { } surface && ReferenceEquals(surface, fill)) return key;
        }
        return null;
    }

    /// <summary>QuÃ© es el relleno de una superficie, en una palabra (lo que el usuario ve o no ve).</summary>
    private static string FillKind(Brush? fill) => fill switch
    {
        GlassBlurBrush { Radius: <= 0f } => "vidrio con radio 0 (plano, sin efecto)",
        GlassBlurBrush => "vidrio",
        AcrylicBrush => "acrÃ­lico",
        SolidColorBrush => "plano (sÃ³lido)",
        null => "sin relleno",
        _ => fill.GetType().Name
    };

    /// <summary>La primera capa ANCESTRA que pinte algo opaco: es lo que el backdrop del panel desenfoca en lugar de la foto.</summary>
    private static string Occluder(FrameworkElement element)
    {
        for (var node = VisualTreeHelper.GetParent(element); node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not FrameworkElement parent) continue;
            if (BackgroundOf(parent) is not { } brush) continue;
            byte alpha = ColorOf(brush) is { } color ? color.A : (byte)255;
            if (alpha >= 250) return $" | TAPADA por [{NameOf(parent)}] {parent.GetType().Name} alfa {alpha}";
        }
        return "";
    }

    private static string NameOf(FrameworkElement element)
        => string.IsNullOrEmpty(element.Name) ? "(sin nombre)" : element.Name;

    /// <summary>
    /// Por quÃ© un elemento entrÃ³ como panel: por el pincel de superficie del tema (instancia), por
    /// su COLOR (una copia del color), o por ser el MARCO de una card sin relleno propio. Es el
    /// mismo orden en que decide <see cref="IsPanel"/>.
    /// </summary>
    private static string? MatchReason(FrameworkElement element)
    {
        var background = BackgroundOf(element);
        if (background != null)
        {
            if (_match.Contains(background)) return "pincel";
            if (ColorOf(background) is { } color && color.A != 0 && _matchColors.Contains(color)) return "color";
        }
        return IsCardFrame(element) ? "marco" : null;
    }

    /// <summary>
    /// Deja constancia de QUIÃ‰N podrÃ­a tapar los parches: recorre los ancestros del primer panel y
    /// anota cada capa que pinte algo, con su alfa. El parche vive detrÃ¡s de TODO el contenido, asÃ­
    /// que una capa opaca entre los dos lo vuelve invisible â€” y desde afuera (menÃº, barra de tÃ­tulo)
    /// no se nota: ese es justo el reporte "el desenfoque no llega a los paneles ni a las barras
    /// internas de las pÃ¡ginas". Un alfa â‰¥ 250 se marca como TAPA. Se anota como mucho cada 2 s
    /// (una bÃºsqueda de paneles puede correr cada 400 ms y no hace falta repetir el mismo Ã¡rbol).
    /// </summary>
    private static void LogOccluders()
    {
        try
        {
            int now = Environment.TickCount;
            if (now - _lastOccluderLog < 2000) return;
            _lastOccluderLog = now;

            // El panel mÃ¡s grande es el que mejor representa lo que ve el usuario (una card de la
            // pÃ¡gina que estÃ¡ mirando), y evita depender de cuÃ¡l se encontrÃ³ primero.
            FrameworkElement? panel = null;
            double best = 0;
            foreach (var reference in _panels)
            {
                if (!reference.TryGetTarget(out var element)) continue;
                double area = element.ActualWidth * element.ActualHeight;
                if (area > best && !IsCardFrame(element))
                {
                    best = area;
                    panel = element;
                }
            }
            if (panel == null && !_panels[0].TryGetTarget(out panel)) return;
            if (panel == null) return;

            var layers = new List<string>();
            for (var node = VisualTreeHelper.GetParent(panel); node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is not FrameworkElement element) continue;
                var brush = BackgroundOf(element);
                if (brush == null) continue;
                byte alpha = brush is SolidColorBrush solid ? solid.Color.A : (byte)255;
                string name = string.IsNullOrEmpty(element.Name) ? "" : $"[{element.Name}] ";
                layers.Add($"{name}{element.GetType().Name} {element.ActualWidth:0}x{element.ActualHeight:0} alfa {alpha}{(alpha >= 250 ? " TAPA" : "")}");
            }

            layers.Reverse();   // de afuera hacia adentro: se lee como se apilan las capas
            PanelAppearance.Diag(layers.Count == 0
                ? "desenfoque: ninguna capa con relleno entre el parche y los paneles"
                : $"desenfoque: capas sobre el panel {panel.GetType().Name} {panel.ActualWidth:0}x{panel.ActualHeight:0} (de afuera hacia adentro): {string.Join(" | ", layers)}");
        }
        catch
        {
            // Un Ã¡rbol a medio construir no puede romper el desenfoque.
        }
    }

    private static void RefreshMatchSet()
    {
        _match.Clear();
        _borderMatch.Clear();
        _matchColors.Clear();
        _borderColors.Clear();

        foreach (var key in PanelAppearance.SurfaceKeys) AddBrush(_match, key);
        foreach (var key in StateKeys) AddBrush(_match, key);
        foreach (var key in BorderKeys) AddBrush(_borderMatch, key);
        foreach (var brush in _extra) _match.Add(brush);

        // Los pinceles que las pÃ¡ginas declaran a mano (ver Watch), descartando los de pÃ¡ginas que
        // ya no existen.
        for (int i = _watched.Count - 1; i >= 0; i--)
        {
            if (_watched[i].TryGetTarget(out var watched)) _match.Add(watched);
            else _watched.RemoveAt(i);
        }

        // El conjunto del CHROME (ver _chromeOnly): los declarados a mano —la barra de título y el
        // panel del menú, que la ventana pinta con una COPIA del color del tema, más la barra interna
        // de cada página— y el pincel vigente de la clave del panel del menú, que es con el que el
        // tema pinta esa franja.
        _chromeBrushes.Clear();
        foreach (var brush in _extra) _chromeBrushes.Add(brush);
        foreach (var weak in _watched)
            if (weak.TryGetTarget(out var watchedBrush)) _chromeBrushes.Add(watchedBrush);
        _chromeKeyBrush = PanelAppearance.SurfaceBrush("NavigationViewDefaultPaneBackground");

        // Los colores se leen de los pinceles vigentes DESPUÃ‰S de juntarlos: el alfa de las
        // superficies cambia con el deslizador de transparencia y el emparejamiento por color tiene
        // que hablar del color de AHORA. Un color transparente (alfa 0) no identifica nada: si
        // entrara, cualquier elemento sin relleno pasarÃ­a por panel.
        foreach (var brush in _match)
            if (ColorOf(brush) is { } color && color.A != 0) _matchColors.Add(color);
        // Los BORDES sÃ­ se comparan con alfa 0 incluido: el color del borde de card es una
        // superficie del ajuste (CardBorderBrush) y con el desenfoque encendido su alfa cambia, asÃ­
        // que exigir alfa distinto de cero dejarÃ­a sin desenfoque a las cards que no tienen relleno
        // (las de los componentes del Workshop) en cuanto la transparencia sube. El candado contra
        // falsos positivos acÃ¡ es otro: ver IsCardFrame (borde de los cuatro lados, esquinas
        // redondeadas y tamaÃ±o de card).
        foreach (var brush in _borderMatch)
            if (ColorOf(brush) is { } border) _borderColors.Add(border);
    }

    /// <summary>
    /// Suma a un conjunto TODO lo que puede estar pintando una superficie de esa clave: el pincel del
    /// diccionario de tema, el live de siempre y el de superficie que usan los elementos creados en
    /// código (ver ThemeBrushes.GetSurface). Sin el tercero, las cards de las grillas y de las páginas
    /// armadas en code-behind solo se reconocerían por color, que es el camino más frágil.
    /// </summary>
    private static void AddBrush(HashSet<Brush> set, string key)
    {
        if (PanelAppearance.SurfaceBrush(key) is { } fromTheme) set.Add(fromTheme);
        if (ThemeBrushes.TryGetLive(key, out var live)) set.Add(live);
        if (ThemeBrushes.SurfaceBrush(key) is { } surface) set.Add(surface);
    }

    private static void Collect(DependencyObject parent, int depth, List<FrameworkElement> found)
    {
        if (depth > 40) return;   // cinturÃ³n contra Ã¡rboles patolÃ³gicos
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && !ReferenceEquals(element, _layer))
            {
                // Cada ScrollViewer que aparece se engancha al refresco de parches (ver
                // HookScrollViewer): scrollear no dispara un pase de layout.
                if (child is ScrollViewer viewer) HookScrollViewer(viewer);
                if (IsPanel(element) && IsShownInTree(element)) found.Add(element);
            }
            Collect(child, depth + 1, found);
        }
    }

    private static Brush? BackgroundOf(FrameworkElement element) => element switch
    {
        Border border => border.Background,
        Panel panel => panel.Background,
        Control control => control.Background,
        _ => null
    };

    /// <summary>
    /// Â¿El elemento se estÃ¡ VIENDO? Recorre la cadena hacia arriba porque <c>Visibility</c> no se
    /// hereda: una card dentro de una pestaÃ±a oculta sigue teniendo el tamaÃ±o y la posiciÃ³n de la
    /// Ãºltima vez que se mostrÃ³, asÃ­ que entraba como panel y su parche pintaba sobre la pestaÃ±a que
    /// sÃ­ se estÃ¡ viendo â€”justo encima de los huecos entre sus cardsâ€”, que es lo que hacÃ­a que las
    /// cards se leyeran unidas por una franja borroneada. La pestaÃ±a oculta no es una superficie que
    /// el usuario estÃ© mirando, asÃ­ que no tiene nada que desenfocar.
    /// </summary>
    private static bool IsShownInTree(FrameworkElement element)
    {
        for (var node = (DependencyObject)element; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement current && current.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(node, _root)) break;
        }

        return true;
    }

    /// <summary>Â¿Este elemento es una superficie de panel? Por el relleno o por su marco de card.</summary>
    private static bool IsPanel(FrameworkElement element)
        => _chromeOnly
            ? IsChromeFill(BackgroundOf(element))
            : MatchesSurface(BackgroundOf(element)) || IsCardFrame(element);

    /// <summary>
    /// Â¿El relleno es el de una pieza del CHROME? (ver <see cref="_chromeOnly"/>). Solo por INSTANCIA: la
    /// declarada a mano por la ventana o la página, o la del pincel vigente del panel del menú. El
    /// emparejamiento por COLOR queda afuera a propósito: el color del panel del menú lo comparten los
    /// cuadros de texto de algunos temas, y en este modo solo entran las piezas que van pegadas.
    /// </summary>
    private static bool IsChromeFill(Brush? brush)
    {
        if (brush == null) return false;

        // Un relleno que YA desenfoca por su cuenta —el vidrio por elemento, ver GlassBlurBrush— no
        // entra: quedaría desenfocado dos veces (la del elemento y la copia de acá detrás). Pasa en el
        // rato en que el ajuste ya pidió el modo chrome y el relleno todavía es el del motor: ahí la
        // pieza conserva su vidrio y no se le suma un parche.
        if (brush is GlassBlurBrush) return false;

        if (_chromeBrushes.Contains(brush)) return true;
        return _chromeKeyBrush != null && ReferenceEquals(brush, _chromeKeyBrush);
    }

    /// <summary>
    /// Â¿El pincel identifica un panel? Primero por INSTANCIA (exacto y gratis) y despuÃ©s por COLOR,
    /// que es lo que rescata a las superficies pintadas con una copia del color del tema.
    /// </summary>
    private static bool MatchesSurface(Brush? brush)
    {
        if (brush == null) return false;
        if (_match.Contains(brush)) return true;
        return ColorOf(brush) is { } color && color.A != 0 && _matchColors.Contains(color);
    }

    /// <summary>
    /// Â¿Es el MARCO de una card? (borde de card del tema, contorno de verdad â€”los cuatro ladosâ€”,
    /// esquinas redondeadas y tamaÃ±o de card). Reconoce las cards que no traen relleno propio, que
    /// de otro modo no tendrÃ­an ninguna seÃ±al para entrar como panel.
    /// </summary>
    private static bool IsCardFrame(FrameworkElement element)
    {
        var brush = BorderBrushOf(element);
        if (brush == null) return false;
        if (!_borderMatch.Contains(brush)
            && !(ColorOf(brush) is { } color && color.A != 0 && _borderColors.Contains(color)))
        {
            return false;
        }

        var thickness = BorderThicknessOf(element);
        if (thickness.Left <= 0 || thickness.Top <= 0 || thickness.Right <= 0 || thickness.Bottom <= 0) return false;
        if (CornerRadiusOf(element) <= 0) return false;
        return element.ActualWidth >= MinCardWidth && element.ActualHeight >= MinCardHeight;
    }

    /// <summary>
    /// El color con el que se reconoce un relleno por COLOR: el del sÃ³lido y tambiÃ©n el TINTE del vidrio
    /// y del acrÃ­lico (ver <see cref="ThemeBrushes.FillColor"/>). Antes exigÃ­a un <see cref="SolidColorBrush"/>,
    /// y desde que las superficies pueden ser vidrio eso dejaba sin reconocer â€”y sin alfa medibleâ€” a
    /// todas las superficies con vidrio: el censo, los colores del match y la auditorÃ­a.
    /// </summary>
    private static Windows.UI.Color? ColorOf(Brush brush) => ThemeBrushes.FillColor(brush);

    private static Brush? BorderBrushOf(FrameworkElement element) => element switch
    {
        Border border => border.BorderBrush,
        Grid grid => grid.BorderBrush,
        Control control => control.BorderBrush,
        _ => null
    };

    private static Thickness BorderThicknessOf(FrameworkElement element) => element switch
    {
        Border border => border.BorderThickness,
        Grid grid => grid.BorderThickness,
        Control control => control.BorderThickness,
        _ => default
    };

    /// <summary>
    /// Reubica los parches sobre los paneles vigentes y los alinea con la foto de fondo. La llaman el
    /// refresco por layout, cada paso del scroll (ver <see cref="HookScrollViewer"/>) y los cambios de
    /// tema, transparencia y desenfoque (ver <see cref="Invalidate"/>).
    /// </summary>
    private static void UpdatePatches()
    {
        // Un pase por vez: mover un parche vuelve a pedir layout y el scroll puede llegar en medio de
        // un pase, asÃ­ que sin esta guarda podÃ­an mezclarse geometrÃ­as a medio calcular.
        if (_updatingPatches) return;

        // Con el scroll en movimiento NO se re-mide (ver OnRendering): TransformToVisual da la
        // posiciÃ³n del LAYOUT, un paso atrÃ¡s de la composiciÃ³n, y un pase a mitad del scroll vuelve a
        // desalinear el vidrio que ShiftPatches acababa de dejar en su lugar. El seguimiento fino lo
        // hace el frame por delta; la re-mediciÃ³n espera a que el scroll se asiente (StopFrameWatch).
        if (_frameWatch) return;
        _updatingPatches = true;
        try
        {
            if (_layer == null || _root == null) return;
            if (_layer.Visibility != Visibility.Visible) return;

            var source = PanelBlur.Source;
            if (source == null) return;

            double windowWidth = _layer.ActualWidth;
            double windowHeight = _layer.ActualHeight;
            if (windowWidth < 1 || windowHeight < 1) return;

            // Este pase es EN QUIETO (si no, saliÃƒÂ³ en el guard de arriba): se fija el CERO del scroll
            // desde el que se medirÃƒÂ¡ el delta en los frames (ver ShiftPatches). Con el contenido
            // detenido, TransformToVisual da la posiciÃƒÂ³n real y este offset es el que le corresponde.

            // LA LISTA DE PANELES SE REHACE CADA TANTO, y no solo cuando cambia el tema o alguno de
            // los deslizadores: navegar entre pestaÃ±as destruye las cards de una pÃ¡gina y crea las
            // de la otra, asÃ­ que con la lista vieja la capa se quedaba sin nada que recortar y el
            // desenfoque no aparecÃ­a en la pÃ¡gina nueva. Va acÃ¡, en el refresco por layout, que es el
            // Ãºnico que corre todo el tiempo; el ritmo lo pone el intervalo de RebuildPanels.
            //
            // Las entradas que el GC ya se llevÃ³ son la firma de una pÃ¡gina que acaba de rearmar sus
            // cards: se sacan ACÃ y la bÃºsqueda se hace en el acto, no al vencer el intervalo (ver
            // DropDeadPanels y _churnUntil).
            if (DropDeadPanels())
            {
                _forceRebuild = true;
                MarkRearm();
            }

            RebuildPanels();

            // La ventana reciÃ©n ahora tiene tamaÃ±o: la bÃºsqueda de paneles que corriÃ³ durante el
            // arranque no pudo aplicar la regla de la superficie de fondo (ver MaxPanelCoverage), asÃ­
            // que se repite UNA vez acÃ¡, con la ventana ya medida.
            if (_rebuildWithoutSize && _root.ActualWidth > 1 && _root.ActualHeight > 1)
            {
                _forceRebuild = true;
                RebuildPanels();
            }
            // El CERO del scroll ya no es uno solo para toda la ventana: cada parche guarda el de su
            // propio scroller al pintarse (ver _patchScrolls y ShiftPatches).


            Array.Clear(_skipped, 0, _skipped.Length);
            int used = 0;
            int now = Environment.TickCount;

            // Deja puesto el parche del panel que acaba de desaparecer, con su Ãºltima geometrÃ­a (ver
            // TryGhostBounds): es lo que evita el parpadeo mientras la pÃ¡gina rearma sus cards.
            void KeepLastPatch(int index)
            {
                if (!TryGhostBounds(index, now, out var ghost, out var ghostRadius)) return;
                var ghostPatch = used < _patches.Count ? _patches[used] : AddPatch();
                _patchScrolls[used].Scroller = null;   // un parche de repuesto no sigue el scroll
                WritePatch(ghostPatch, ghost, ghostRadius, source, windowWidth, windowHeight);
                used++;
            }

            // Panel VIVO pero mal medido en este pase (a medio rearmar por un refresco de la pÃ¡gina,
            // o con los transforms de los ancestros un paso atrÃ¡s de la animaciÃ³n): se conserva la
            // Ãºltima geometrÃ­a buena en vez de apagar el parche. Es el parpadeo "la card desaparece y
            // vuelve" con la transparencia alta: la card sigue ahÃ­ â€”el texto no se mueveâ€”, solo pierde
            // el vidrio un pase. No abre ni necesita la ventana de rearme (ver TryGhostBounds): la
            // condiciÃ³n es que el panel siga vivo y visto hace poco (ver PanelGraceMs). Devuelve false
            // si no habÃ­a geometrÃ­a que conservar (ahÃ­ sÃ­ se cuenta el motivo en _skipped).
            bool KeepMeasuredGeometry(int index)
            {
                if (index < 0 || index >= _panelBounds.Count || index >= _panelSeen.Count
                    || index >= _panelRadius.Count) return false;
                if (now - _panelSeen[index] >= PanelGraceMs) return false;
                var last = _panelBounds[index];
                if (last.Width < 1 || last.Height < 1) return false;
                var keptPatch = used < _patches.Count ? _patches[used] : AddPatch();
                _patchScrolls[used].Scroller = null;   // un parche de repuesto no sigue el scroll
                WritePatch(keptPatch, last, _panelRadius[index], source, windowWidth, windowHeight);
                used++;
                return true;
            }

            for (int i = 0; i < _panels.Count; i++)
            {
                // Una entrada que el GC ya se llevÃ³ no puede pintar nada, y hasta hace poco esto se
                // salteaba en SILENCIO: era el caso "13 paneles en la lista, 6 parches, sin parche:
                // ninguno" del log â€” nueve cards sin vidrio y ningÃºn motivo anotado.
                if (!_panels[i].TryGetTarget(out var element))
                {
                    _skipped[5]++;
                    // La entrada muriÃ³ DESPUÃ‰S de la poda â€”el GC corre en medio del paseâ€”: la seÃ±al de
                    // rearme es esta misma, asÃ­ que se abre acÃ¡ y el parche queda puesto.
                    MarkRearm();
                    KeepLastPatch(i);
                    continue;
                }

                // Un panel por iteraciÃ³n, cada uno en su propio try: si un elemento raro â€”una card a
                // medio construir durante un cambio de pÃ¡ginaâ€” tira al medirse, los demÃ¡s conservan
                // su parche. Con un Ãºnico try afuera, UNA excepciÃ³n dejaba sin desenfoque a todos
                // los paneles que venÃ­an despuÃ©s en la lista.
                try
                {
                    // Desprendido de la ventana (el Frame se queda con la pÃ¡gina anterior entera):
                    // su posiciÃ³n ya no existe, asÃ­ que no hay parche posible hasta que vuelva.
                    if (!element.IsLoaded)
                    {
                        _skipped[0]++;
                        MarkRearm();
                        KeepLastPatch(i);
                        continue;
                    }
                    // Panel OCULTO (una pestaÃ±a interna que se cerrÃ³, el contenido que queda detrÃ¡s en un
                    // cambio de pÃ¡gina): sigue cargado y medido, asÃ­ que sin esta comprobaciÃ³n seguÃ­a
                    // pintando su franja desenfocada sobre lo que sÃ­ se estÃ¡ viendo. Es la misma que usa
                    // la bÃºsqueda para decidir quiÃ©n es panel (ver IsShownInTree), y acÃ¡ va porque la
                    // lista es por fusiÃ³n: un panel deja de estar en el Ã¡rbol bastante despuÃ©s de
                    // ocultarse.
                    if (!IsShownInTree(element)) { _skipped[2]++; continue; }
                    // A medio medir por un re-layout (ver KeepMeasuredGeometry): se conserva el vidrio.
                    if (element.ActualWidth < 1 || element.ActualHeight < 1)
                    {
                        if (!KeepMeasuredGeometry(i)) _skipped[1]++;
                        continue;
                    }

                    var full = element.TransformToVisual(_root)
                        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    if (full.Width < 1 || full.Height < 1)
                    {
                        if (!KeepMeasuredGeometry(i)) _skipped[1]++;
                        continue;
                    }

                    // CinturÃ³n de RebuildPanels (que decide con el tamaÃ±o del arranque): si la ventana
                    // se achicÃ³ y un candidato pasÃ³ a cubrirla casi entera, es la superficie de fondo
                    // del tema (ver MaxPanelCoverage) y se queda sin parche. Se mide con el tamaÃ±o
                    // REAL del elemento: un contenedor de fondo scrolleado a medias sigue siendo fondo.
                    if (full.Width * full.Height > MaxPanelCoverage * windowWidth * windowHeight) { _skipped[3]++; continue; }

                    // Y el parche se recorta a lo que el panel MUESTRA (ver ClipRegionOf): si el
                    // panel se fue scrolleando fuera de su contenedor, no puede seguir pintando.
                    //
                    // CON SCROLL EN CURSO se reusa la zona visible del Ãºltimo pase quieto: en plena
                    // animaciÃ³n los ancestros pueden venir de la disposiciÃ³n vieja, la intersecciÃ³n
                    // se caÃ­a a cero y el panel quedaba sin vidrio en medio del movimiento â€” con la
                    // transparencia alta, eso es exactamente "cuando scrolleo se va el blur". La
                    // zona visible vive fija en coordenadas de la ventana mientras el ScrollViewer
                    // se desplaza, asÃ­ que recortar contra la guardada da lo mismo y no depende de
                    // que los transforms de los ancestros ya estÃ©n al dÃ­a.
                    var clip = _panelClips.GetValue(element, static _ => new PanelClip());
                    Rect region;
                    if (_frameWatch && clip.Has)
                    {
                        region = clip.Region;
                    }
                    else
                    {
                        region = ClipRegionOf(element);

                        // Solo se guarda una zona BUENA. Y si la medida de este pase saliÃ³ vacÃ­a
                        // â€”contenedores a medio rearmar por el refresco de datos, o ancestros un
                        // paso atrÃ¡s de la animaciÃ³nâ€” se sigue usando la que ya se sabÃ­a: dejarla en
                        // vacÃ­o era tirar la Ãºnica referencia buena y quedarse sin recorte confiable
                        // justo en el prÃ³ximo scroll, que es cuando mÃ¡s se nota.
                        if (region.Width >= 1 && region.Height >= 1)
                        {
                            if (!_frameWatch)
                            {
                                clip.Region = region;
                                clip.Has = true;
                            }
                        }
                        else if (clip.Has)
                        {
                            region = clip.Region;
                        }
                    }

                    // El panel puede estar FUERA de la vista y aun así cerca del borde (una card que el
                    // scroll trae en los próximos frames). Se le deja el parche AUTORIZADO —con su caja
                    // completa y el recorte vacío, así que queda apagado— para que, al desplazarse, el
                    // recorte lo encienda solo (ver ApplyPatchClip): sin esto, la card que entra a la
                    // vista durante el scroll recién tenía vidrio cuando el scroll se asentaba, que es
                    // el "el blur carga cuando dejo de scrollear" reportado.
                    var inView = Intersect(full, region);
                    if ((inView.Width < 1 || inView.Height < 1)
                        && region.Width >= 1 && region.Height >= 1
                        && Intersect(full, Inflate(region, MarginBeyondView)) is { Width: >= 1, Height: >= 1 })
                    {
                        var nearPatch = used < _patches.Count ? _patches[used] : AddPatch();
                        var nearState = _patchScrolls[used];
                        nearState.Scroller = ScrollAncestorOf(element);
                        nearState.OffsetBase = nearState.Scroller?.VerticalOffset ?? 0;
                        nearState.Viewport = region;
                        _patchBaseTop[used] = full.Top;
                        var nearRadius = CornerRadiusOf(element);
                        WritePatch(nearPatch, full, nearRadius, source, windowWidth, windowHeight);
                        ApplyPatchClip(nearPatch, nearState, full.Left, full.Top, full.Width, full.Height, full.Top);
                        _panelBounds[i] = full;
                        _panelRadius[i] = nearRadius;
                        used++;
                        continue;
                    }

                    var bounds = Intersect(full, region);
                    if (bounds.Width < 1 || bounds.Height < 1)
                    {
                        // Si la caja FRESCA sigue en pantalla, el que fallÃ³ es el recorte (ancestros
                        // atrasados): se conserva la geometrÃ­a. Si la caja ya estÃ¡ afuera, el panel se
                        // fue de vista y el parche se apaga.
                        if (full.Right >= 0 && full.Bottom >= 0 && full.Left <= windowWidth && full.Top <= windowHeight)
                        {
                            if (!KeepMeasuredGeometry(i)) _skipped[2]++;
                        }
                        else
                        {
                            _skipped[2]++;
                        }
                        continue;
                    }
                    if (bounds.Right < 0 || bounds.Bottom < 0) { _skipped[2]++; continue; }
                    if (bounds.Left > windowWidth || bounds.Top > windowHeight) { _skipped[2]++; continue; }

                    // MEDICIÃ“N A MEDIO REARMAR: la card COMPLETA mide como card (ancho y alto sanos),
                    // pero el recorte saliÃ³ achatado â€”el 534x16 del log, debajo del 534x150 realâ€”. No es
                    // un panel achatado de verdad: es un ancestro (un Grid/StackPanel que la pÃ¡gina
                    // estÃ¡ rearmando) capturado un frame antes de que el layout lo estire, y recorta la
                    // card a una franja. Pintar esa franja es el "la card se expande hacia abajo".
                    //
                    // Se descarta ESTE pase (no se pinta nada): un frame sin vidrio es mejor que una
                    // franja mal recortada, y el pase siguiente â€”el layout ya estirÃ³ al ancestroâ€” la
                    // pinta completa. La referencia es `full`, la caja real de la card, no el recorte.
                    if (full.Width >= MinCardWidth && full.Height >= MinCardHeight
                        && bounds.Height < MinCardHeight)
                    {
                        _skipped[2]++;
                        continue;
                    }

                    // Y al revÃ©s: una card que ya existÃ­a con alto sano y este pase la recorta muy por
                    // debajo (rearme con la card a medio crear) conserva su Ãºltima geometrÃ­a buena.
                    if (i < _panelBounds.Count && i < _panelSeen.Count
                        && now - _panelSeen[i] < PanelGraceMs)
                    {
                        var lastGood = _panelBounds[i];
                        if (lastGood.Height >= MinCardHeight
                            && bounds.Height < lastGood.Height * 0.6
                            && Math.Abs(bounds.Width - lastGood.Width) <= 2)
                        {
                            if (KeepMeasuredGeometry(i)) continue;
                        }
                    }

                    var patch = used < _patches.Count ? _patches[used] : AddPatch();
                    var scroll = _patchScrolls[used];
                    scroll.Scroller = ScrollAncestorOf(element);
                    scroll.OffsetBase = scroll.Scroller?.VerticalOffset ?? 0;
                    scroll.Viewport = region;
                    _patchBaseTop[used] = bounds.Top;
                    var radius = CornerRadiusOf(element);
                    WritePatch(patch, bounds, radius, source, windowWidth, windowHeight);
                    ApplyPatchClip(patch, scroll, bounds.Left, bounds.Top, bounds.Width, bounds.Height, bounds.Top);

                    // La geometrÃ­a queda guardada para el panel: si la pÃ¡gina lo destruye, el parche
                    // siguiente se pinta con esto en vez de apagarse (ver TryGhostBounds).
                    _panelBounds[i] = bounds;
                    _panelRadius[i] = radius;
                    used++;
                }
                catch
                {
                    // Un panel a medio construir no puede tumbar el desenfoque de los demÃ¡s; y si ya
                    // tenÃ­a vidrio, lo conserva un pase en vez de parpadear (ver KeepMeasuredGeometry).
                    if (!KeepMeasuredGeometry(i)) _skipped[4]++;
                }
            }

            // Los parches que sobran se sacan de escena: sin esto quedarÃ­an pegados a una pÃ¡gina vieja.
            for (int i = used; i < _patches.Count; i++)
            {
                _patches[i].Visibility = Visibility.Collapsed;
                // Un parche que este pase no autorizó no puede quedar con el scroll de un panel que ya no
                // existe: era el único camino por el que podía reaparecer en medio de un scroll con
                // geometría vieja (ahora el recorte por frame lo volvería a encender).
                _patchScrolls[i].Scroller = null;
                _patches[i].Clip = null;
            }
            // Re-anclaje del scroll (ver WritePatch): el Canvas.Top nuevo y la Translation en cero tienen
            // que valer en el mismo frame. Sin empujar el layout acá, el parche podría pintarse un frame
            // en la posición vieja — un parpadeo al cerrar cada scroll.
            if (_rebasePending)
            {
                _rebasePending = false;
                try { _layer?.UpdateLayout(); } catch { }
            }

            // EL VIDRIO DECLARATIVO: con los parches ya medidos (este pase corre en quieto), se le pasan a
            // la capa de composición para que el recorte de cada panel lo siga el compositor con una
            // expresión — sin trabajo managed por frame (ver PanelGlass). Cuando está activo, los parches
            // de este camino se apagan: no se dibuja dos veces lo mismo.
            if (PanelGlass.Wanted)
            {
                _glassItems.Clear();
                for (int i = 0; i < used && i < _patches.Count; i++)
                {
                    var glassPatch = _patches[i];
                    var glassState = _patchScrolls[i];
                    _glassItems.Add(new PanelGlass.Item
                    {
                        // El índice va declarado: es la llave con la que el seguimiento por frame le
                        // encuentra su recorte (ver PanelGlass.Follow).
                        Index = i,
                        Bounds = new Rect(Canvas.GetLeft(glassPatch), Canvas.GetTop(glassPatch), glassPatch.Width, glassPatch.Height),
                        Radius = glassPatch.RadiusX,
                        Scroller = glassState.Scroller,
                        Viewport = glassState.Viewport
                    });
                }

                PanelGlass.Declare(_layer!, _glassItems, PanelBlur.Surface, windowWidth, windowHeight);
            }
            else if (PanelGlass.Live)
            {
                // Se quedó sin copia (cambio de tema o de foto, o falló la surface): el vidrio declarativo
                // se apaga y este camino vuelve a dibujar, que es lo que deja los parches encendidos.
                PanelGlass.Hide();
            }

            if (PanelGlass.Live)
            {
                for (int i = 0; i < used && i < _patches.Count; i++) _patches[i].Visibility = Visibility.Collapsed;

                // Una sola línea por sesión: es la que distingue "lo pinta la composición" de "no se ve
                // nada", sin tener que adivinar desde afuera cuál de las dos cosas está pasando.
                if (!_glassLogged)
                {
                    _glassLogged = true;
                    PanelAppearance.Diag("desenfoque: los parches de XAML se apagan — el vidrio lo pinta la composicion");
                }
            }

            LogPatches(windowWidth, windowHeight);
        }
        catch
        {
            // Un panel puede estar a medio construir durante un cambio de pÃ¡gina.
        }
        finally
        {
            _updatingPatches = false;
        }
    }

    /// <summary>
    /// La ZONA VISIBLE para un elemento: la intersecciÃ³n de la caja de CADA contenedor suyo (sobre
    /// todo la del ScrollViewer de la pÃ¡gina) con la ventana.
    ///
    /// POR QUÃ‰ HACE FALTA: <c>TransformToVisual</c> devuelve la caja COMPLETA del elemento aunque
    /// estÃ© fuera de la vista â€”lo que se sale del ScrollViewer queda recortado en pantalla, pero la
    /// caja noâ€”, y los parches viven en la capa de fondo de la ventana, que no la recorta nadie. Un
    /// panel scrolleado hacia afuera seguÃ­a pintando su franja desenfocada ENCIMA de lo que tenÃ­a al
    /// lado: los huecos entre cards, las cards vecinas y hasta el navbar quedaban borroneados y todo
    /// se leÃ­a como una sola pieza con una sombra en el medio. Con el recorte, cada parche pinta
    /// exactamente lo que su panel muestra.
    ///
    /// Devuelve la ZONA y no la caja ya recortada (ver <see cref="UpdatePatches"/>): asÃ­ el pase del
    /// scroll la puede reusar contra la caja nueva del panel sin volver a leer los ancestros, que en
    /// plena animaciÃ³n pueden estar un paso atrÃ¡s.
    /// </summary>
    private static Rect ClipRegionOf(FrameworkElement element)
    {
        var region = _root != null && _root.ActualWidth > 1 && _root.ActualHeight > 1
            ? new Rect(0, 0, _root.ActualWidth, _root.ActualHeight)
            : new Rect(0, 0, double.MaxValue, double.MaxValue);

        for (var node = VisualTreeHelper.GetParent(element); node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, _root)) break;
            if (node is not FrameworkElement parent) continue;
            if (parent.ActualWidth < 1 || parent.ActualHeight < 1) continue;

            var clip = parent.TransformToVisual(_root!)
                .TransformBounds(new Rect(0, 0, parent.ActualWidth, parent.ActualHeight));

            region = Intersect(region, clip);
            if (region.Width < 1 || region.Height < 1) return new Rect(0, 0, 0, 0);
        }

        return region;
    }

    /// <summary>IntersecciÃ³n de dos rectÃ¡ngulos; vacÃ­a (0,0,0,0) cuando no se tocan.</summary>
    private static Rect Intersect(Rect a, Rect b)
    {
        double left = Math.Max(a.X, b.X);
        double top = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.X + a.Width, b.X + b.Width);
        double bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right <= left || bottom <= top ? new Rect(0, 0, 0, 0) : new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Deja un parche con esa geometrÃ­a, escribiendo SOLO lo que cambiÃ³ y encendiÃ©ndolo si venÃ­a de una
    /// pÃ¡gina con menos paneles. Lo usan el camino normal y el del panel que acaba de desaparecer (ver
    /// <see cref="TryGhostBounds"/>): los dos tienen que dejar el parche en el mismo estado.
    /// </summary>
    private static void WritePatch(Rectangle patch, Rect bounds, double radius, ImageSource source, double windowWidth, double windowHeight)
    {
        // Se escribe solo lo que CAMBIÃ“: con el refresco por frame (ver OnRendering) los mismos
        // valores vuelven en cada frame mientras la animaciÃ³n estÃ¡ quieta, y cada escritura invalida
        // la disposiciÃ³n del Canvas â€” layout tirado en medio del scroll.
        if (Canvas.GetLeft(patch) != bounds.Left) Canvas.SetLeft(patch, bounds.Left);
        if (Canvas.GetTop(patch) != bounds.Top) Canvas.SetTop(patch, bounds.Top);
        if (patch.Width != bounds.Width) patch.Width = bounds.Width;
        if (patch.Height != bounds.Height) patch.Height = bounds.Height;

        // El parche se REUSA por Ã­ndice, asÃ­ que puede venir apagado de una pÃ¡gina con menos paneles:
        // sin volverlo a encender, ese panel se quedaba sin desenfoque para siempre â€” y como cada
        // navegaciÃ³n apaga un poco mÃ¡s al final de la lista, cambiar de pestaÃ±a iba dejando cada vez
        // mÃ¡s cards sin vidrio.
        patch.Visibility = Visibility.Visible;

        // El parche se REUSA por índice y puede traer el recorte de la zona visible del panel anterior
        // (ver ApplyPatchClip): acá se limpia, y el camino que corresponda lo vuelve a poner.
        if (patch.Clip != null) patch.Clip = null;

        if (patch.RadiusX != radius) patch.RadiusX = radius;
        if (patch.RadiusY != radius) patch.RadiusY = radius;

        if (patch.Fill is ImageBrush brush) Paint(brush, source, bounds.Left, bounds.Top, windowWidth, windowHeight);
        // El top recién escrito es la posición REAL del pase en quieto (el pase ya re-midió), así que la
        // Translation vuelve a cero: el layout es la referencia y el scroll se aplica encima. Sin esto,
        // un parche que venía desplazado quedaría corrido el doble. Cuando el valor cambia, el pase pide
        // un empujón de layout al final (ver UpdatePatches): el Canvas.Top nuevo y este cero tienen que
        // valer en el MISMO frame.
        if (SetPatchTranslation(patch, 0)) _rebasePending = true;
    }

    /// <summary>
    /// Deja el pincel del parche mostrando el pedazo de foto que le toca: la copia desenfocada se
    /// dibuja a tamaÃ±o natural anclada arriba a la izquierda, escalada al tamaÃ±o de la ventana y
    /// corrida por la posiciÃ³n del parche (asÃ­ queda en fase con la foto nÃ­tida, que tambiÃ©n va
    /// estirada sobre la ventana).
    /// </summary>
    private static void Paint(ImageBrush brush, ImageSource source, double left, double top, double windowWidth, double windowHeight)
    {
        if (!ReferenceEquals(brush.ImageSource, source)) brush.ImageSource = source;

        double bitmapWidth = PanelBlur.PixelWidth;
        double bitmapHeight = PanelBlur.PixelHeight;
        if (bitmapWidth < 1 || bitmapHeight < 1) return;

        var transform = brush.Transform as TransformGroup;
        if (transform == null || transform.Children.Count != 2)
        {
            transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform());
            transform.Children.Add(new TranslateTransform());
            brush.Transform = transform;
        }

        ((ScaleTransform)transform.Children[0]).ScaleX = windowWidth / bitmapWidth;
        ((ScaleTransform)transform.Children[0]).ScaleY = windowHeight / bitmapHeight;
        ((TranslateTransform)transform.Children[1]).X = -left;
        ((TranslateTransform)transform.Children[1]).Y = -top;

        brush.Opacity = 1;
    }

    /// <summary>
    /// La fase con la que quedó el pincel de un parche: el corrimiento vertical que lo ancla a la
    /// ventana (ver <see cref="Paint"/>). Solo para el diagnóstico del seguimiento del scroll.
    /// </summary>
    private static double PhaseY(Rectangle patch)
    {
        if (patch.Fill is ImageBrush brush
            && brush.Transform is TransformGroup group
            && group.Children.Count == 2
            && group.Children[1] is TranslateTransform translate)
            return translate.Y;
        return double.NaN;
    }

    /// <summary>
    /// Lo que dice la COMPOSICIÓN del desplazamiento del parche: el valor con el que se va a pintar, no
    /// el que dejamos en la DP (ver <see cref="SetPatchTranslation"/>). Si los dos números del log no
    /// coinciden, el que manda es este — y esa diferencia es la que separa "el log dice que se movió"
    /// de "el parche se movió de verdad".
    /// </summary>
    private static double EffectTranslationY(Rectangle patch)
    {
        try
        {
            var properties = ElementCompositionPreview.GetElementVisual(patch).Properties;
            if (properties.TryGetVector3("Translation", out var value) == CompositionGetValueStatus.Succeeded)
                return value.Y;
        }
        catch { }
        return double.NaN;
    }

    private static Rectangle AddPatch()
    {
        var patch = new Rectangle
        {
            Fill = new ImageBrush
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            },
            IsHitTestVisible = false
        };
        _patches.Add(patch);
        _patchScrolls.Add(new PatchScroll());
        _patchBaseTop.Add(0);
        _layer!.Children.Add(patch);
        // Sin esto la Translation del elemento no llega a la composición, y la Translation es EL canal
        // con el que el scroll mueve el parche sin tocar el layout (ver ShiftPatches).
        ElementCompositionPreview.SetIsTranslationEnabled(patch, true);
        return patch;
    }

    private static double CornerRadiusOf(FrameworkElement element) => element switch
    {
        Border border => border.CornerRadius.TopLeft,
        Grid grid => grid.CornerRadius.TopLeft,
        Control control => control.CornerRadius.TopLeft,
        _ => 0
    };
}
