using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WHPO_UI;

/// <summary>
/// Efecto "reveal" al pasar el mouse: un halo suave con el acento de la app que
/// sigue al cursor dentro de las cards y del navbar.
///
/// Rendimiento (por qué NO se camina el árbol en cada movimiento):
/// - Se recuerda el elemento ACTIVO bajo el cursor (navbar/card). Cada
///   PointerMoved del área hace primero un chequeo de bounds contra ese
///   elemento (una resta y dos comparaciones): si el cursor sigue adentro,
///   solo se actualiza el centro del gradiente y se corta ahí. El recorrido
///   del árbol visual ocurre únicamente al CRUZAR de un elemento a otro.
/// - El recorrido de descubrimiento tiene tope de profundidad, así que aunque
///   el cursor esté sobre una página muy profunda el costo por evento es acotado.
///
/// Anti "efecto pegado":
/// - El elemento activo se oculta apenas el cursor sale de sus bounds (el
///   chequeo de arriba), aunque el PointerExited se haya perdido (scroll,
///   salida rápida, captura de un control). Es auto-reparable: el primer
///   movimiento posterior limpia el estado.
/// - Además se escuchan PointerCanceled / PointerCaptureLost, y Unloaded
///   suelta el visual para que no queden halos de páginas anteriores.
///
/// Tema: el color del halo se resuelve con ThemeBrushes.Get("AccentBrush") en
/// cada hover, así que cambia de tema/paleta sin código extra. No hay strings
/// visibles: no necesita traducción.
/// </summary>
public static class RevealEffect
{
    // ==== Config del efecto (DIPs / alphas 0-255) ====
    private const byte CenterAlpha = 28;        // opacidad del núcleo del halo
    private const byte MidAlpha = 11;           // caída intermedia del gradiente
    private const float RadiusFactor = 0.28f;   // radio relativo al lado mayor
    private const float MinRadius = 36f;        // tope para elementos chicos
    private const float MaxRadius = 95f;        // tope para contenedores grandes
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(180);

    // Qué cuenta como "card" al descubrir por el árbol (evita chips/badges/contenedores).
    private const float CardMinWidth = 140f;
    // 44 px: las cards de una sola línea de contenido (tweak de Optimizaciones/Debloat: casilla
    // de 32 px + padding de 10 arriba y abajo) quedan en ~52 px, y con el mínimo anterior (56)
    // el detector las descartaba y no recibían el halo.
    private const float CardMinHeight = 44f;
    private const float CardMaxWidth = 4000f;   // admite cards de ancho completo (ej. Sistema Operativo)
    private const float CardMaxHeight = 1600f;  // cards altas (ej. "Secciones del menú"): antes se caían del efecto
    private const float CardMinCornerRadius = 6f;

    // Hosts marcados a mano con Tag="reveal" o RevealEffect.Mark(): filas planas que NO son
    // cards (sin fondo de card ni esquinas) —como la grilla de dispositivos de Overclock USB—
    // y cards bajas creadas en código (las de Optimizaciones quedan ~52 px de alto, por debajo
    // del mínimo de card, así que la heurística las descartaba). Solo se les pide un tamaño
    // mínimo para que el brillo tenga dónde dibujarse.
    private const string RevealTag = "reveal";
    private const float FlatRowMinWidth = 80f;
    private const float FlatRowMinHeight = 22f;

    // Opt-in por código (RevealEffect.Mark): mismo sentido que Tag="reveal" pero sin tocar la
    // propiedad Tag, que en varias páginas ya tiene otro dato (el nombre del tweak, el device
    // instance id) y no se puede pisar. Acepta cualquier FrameworkElement — no solo Border —
    // así también cubre cards/botones con pinta de card que la heurística no reconoce.
    private static readonly ConditionalWeakTable<FrameworkElement, object> _marked = new();
    private static readonly object MarkToken = new();

    /// <summary>Marca un elemento para que reciba el halo sin depender de la heurística (fondo
    /// propio, tipo distinto de Border, o tamaño por debajo del mínimo de card). Solo se le pide
    /// un tamaño mínimo para que el brillo tenga dónde dibujarse.</summary>
    public static void Mark(FrameworkElement element) => _marked.TryAdd(element, MarkToken);

    /// <summary>
    /// Traza opcional del detector: recibe una línea por cada CRUCE de elemento bajo el cursor
    /// (no por cada movimiento), con el host elegido o el motivo del rechazo. La app la conecta
    /// al log de desarrollo, así un halo que no aparece se diagnostica leyendo el log en lugar de
    /// inspeccionar el árbol visual a mano.
    /// </summary>
    public static Action<string>? Trace { get; set; }

    // Último origen trazado: evita inundar el log cuando el cursor se mueve dentro del mismo
    // elemento sin host (el camino rápido solo cubre el caso en que SÍ hay host activo).
    private static DependencyObject? _lastTracedSource;

    // Colores que identifican una card: el fondo de card y los estados que la propia
    // card aplica al pasar el mouse o al quedar seleccionada. Sin los estados de
    // hover/selección el halo no aparecía en las tarjetas de optimizaciones, que
    // cambian su fondo a CardHoverBrush apenas entra el cursor (justo el momento en
    // que el efecto quiere dibujarse).
    private static readonly string[] CardBrushKeys =
        { "CardBackgroundBrush", "CoreCardBackgroundBrush", "CardHoverBrush", "CardSelectedBrush" };

    // Marcador de exclusión: un Border con Tag="no-reveal" (y todo su contenido)
    // no recibe el efecto. Lo usan las cards de juegos, que ya tienen su propio
    // hover (elevación + overlay de lanzar).
    private const string NoRevealTag = "no-reveal";

    // Topes del recorrido de descubrimiento: los ítems del navbar son poco
    // profundos; una card puede estar unos niveles más abajo.
    private const int NavWalkMaxDepth = 14;
    private const int CardWalkMaxDepth = 30;

    private sealed class State
    {
        public SpriteVisual? Visual;
        public CompositionRadialGradientBrush? Brush;
    }

    // Elementos ya activados (referencia débil: no prolonga la vida de la UI).
    private static readonly ConditionalWeakTable<FrameworkElement, State> _attached = new();

    // Elemento activo bajo el cursor (uno por área). La clave del rendimiento:
    // mientras el cursor siga dentro de sus bounds NO se recorre el árbol.
    private static FrameworkElement? _currentNav;
    private static FrameworkElement? _currentCard;

    // Raíz del contenido (la setea AttachCards) para cortar los recorridos.
    private static FrameworkElement? _contentRoot;

    // ===== Instalación (una sola vez, desde MainWindow) =====

    /// <summary>Activa el reveal en los ítems del navbar (incluye los dinámicos del Workshop).</summary>
    public static void AttachNavbar(FrameworkElement nav)
    {
        nav.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler((s, e) => OnNavMove(e)), true);
    }

    /// <summary>Activa el reveal en las cards del contenido (XAML y code-behind).</summary>
    public static void AttachCards(FrameworkElement contentRoot)
    {
        _contentRoot = contentRoot;
        contentRoot.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler((s, e) => OnContentMove(e)), true);
    }

    // ===== Handlers raíz (corren por captura en CADA movimiento) =====

    private static void OnNavMove(PointerRoutedEventArgs e)
    {
        // 1) Camino rápido: ¿el cursor sigue dentro del ítem activo?
        if (_currentNav is FrameworkElement cur)
        {
            if (IsAlive(cur) && TryCenterIfInside(cur, e))
                return;
            Deactivate(cur, ref _currentNav); // salió de sus bounds: ocultar ya
        }

        // 2) Descubrimiento con tope de profundidad (solo al cruzar elementos).
        var item = FindUp<Microsoft.UI.Xaml.Controls.NavigationViewItem>(
            e.OriginalSource as DependencyObject, NavWalkMaxDepth,
            stopAt: static d => ReferenceEquals(d, _contentRoot));
        if (item == null)
            return;

        EnsureAttached(item);
        if (!ReferenceEquals(_currentNav, item))
        {
            _currentNav = item;
            Show(item);
        }
        SetCenter(item, e);
    }

    private static void OnContentMove(PointerRoutedEventArgs e)
    {
        // 1) Camino rápido contra la card activa.
        if (_currentCard is FrameworkElement cur)
        {
            if (IsAlive(cur) && TryCenterIfInside(cur, e))
                return;
            Deactivate(cur, ref _currentCard);
        }

        // 2) Descubrir la card bajo el cursor (solo al cruzar de una a otra).
        var origin = e.OriginalSource as DependencyObject;
        var card = FindCardUp(origin, CardWalkMaxDepth);
        TraceDiscovery(origin, card);
        if (card == null)
            return;

        EnsureAttached(card);
        if (!ReferenceEquals(_currentCard, card))
        {
            _currentCard = card;
            Show(card);
        }
        SetCenter(card, e);
    }

    /// <summary>Deja una línea en la traza al cruzar a otro elemento (host encontrado o motivo
    /// del rechazo). Sin Trace asignado no hace nada.</summary>
    private static void TraceDiscovery(DependencyObject? origin, FrameworkElement? host)
    {
        var trace = Trace;
        if (trace == null || ReferenceEquals(origin, _lastTracedSource))
            return;
        _lastTracedSource = origin;

        if (host != null)
        {
            trace($"Reveal: halo en {host.GetType().Name} {host.ActualWidth:0}x{host.ActualHeight:0}" +
                  $"{(IsRevealHost(host) ? " [marcado]" : "")} ← origen {origin?.GetType().Name ?? "null"}");
            return;
        }

        trace($"Reveal: sin host bajo el cursor ← origen {origin?.GetType().Name ?? "null"}");
    }

    /// <summary>¿Host marcado a mano? (Mark() o Tag="reveal").</summary>
    private static bool IsRevealHost(FrameworkElement el)
        => _marked.TryGetValue(el, out _) || (el is Border { Tag: string tag } && tag == RevealTag);

    // ===== Descubrimiento =====

    private delegate bool StopPredicate(DependencyObject d);

    /// <summary>Sube por el árbol visual hasta encontrar T, con tope de profundidad.</summary>
    private static T? FindUp<T>(DependencyObject? start, int maxDepth, StopPredicate? stopAt = null) where T : class
    {
        var d = start;
        for (int i = 0; d != null && i < maxDepth; i++)
        {
            if (d is T match)
                return match;
            if (stopAt?.Invoke(d) == true)
                return null;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static FrameworkElement? FindCardUp(DependencyObject? start, int maxDepth)
    {
        var d = start;
        for (int i = 0; d != null && i < maxDepth; i++)
        {
            if (d is Border b)
            {
                // Exclusión explícita: corta la búsqueda acá (la card marcada y
                // sus hijos quedan sin reveal; tampoco "trepa" a un contenedor
                // ancestro, que daría un halo en el lugar equivocado).
                var tag = b.Tag as string;
                if (tag == NoRevealTag)
                    return null;
                // Opt-in explícito (Tag="reveal" o RevealEffect.Mark): gana sobre la heurística.
                if (b.Tag as string == RevealTag || _marked.TryGetValue(b, out _))
                    return IsBigEnoughForReveal(b) ? b : null;
                if (IsCard(b))
                    return b;
            }
            else if (d is FrameworkElement marked && _marked.TryGetValue(marked, out _))
            {
                // Marcado por código y no es un Border (ej. un botón con pinta de card): el halo
                // se dibuja igual porque el efecto trabaja sobre cualquier FrameworkElement.
                if (ReferenceEquals(d, _contentRoot))
                    return null;
                return IsBigEnoughForReveal(marked) ? marked : null;
            }
            if (ReferenceEquals(d, _contentRoot))
                return null;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>¿Tiene pinta de card? (fondo de card del tema + esquinas + tamaño razonable)</summary>
    private static bool IsCard(Border b)
    {
        if (b.Background is not SolidColorBrush scb)
            return false;

        double w = b.ActualWidth, h = b.ActualHeight;
        if (w < CardMinWidth || h < CardMinHeight || w > CardMaxWidth || h > CardMaxHeight)
            return false;

        var cr = b.CornerRadius;
        float r = (float)Math.Max(Math.Max(cr.TopLeft, cr.TopRight), Math.Max(cr.BottomLeft, cr.BottomRight));
        if (r < CardMinCornerRadius)
            return false;

        return IsCardBrushColor(scb.Color);
    }

    /// <summary>¿El color corresponde al fondo (o al estado hover/selección) de una card?
    ///
    /// Se compara contra el pincel live del tema activo Y contra el color que el XAML
    /// resuelve de verdad para esa clave: una card con {ThemeResource CardBackgroundBrush}
    /// toma el pincel del diccionario de tema (o del override de paleta), que puede tener
    /// otro color que el pincel live — y en ese caso la card quedaba sin efecto aunque
    /// tuviera el mismo nombre de recurso (pasaba en las cards de Configuración).</summary>
    private static bool IsCardBrushColor(Windows.UI.Color color)
    {
        foreach (var key in CardBrushKeys)
        {
            try
            {
                if (ThemeBrushes.Get(key).Color == color) return true;
            }
            catch { /* un key ausente no puede romper el efecto */ }
        }
        return IsCardBrushColorInResources(color);
    }

    /// <summary>Busca el color entre los pinceles que la app tiene publicados: el diccionario
    /// raíz de App.xaml, sus diccionarios de tema y los diccionarios mergeados (los overrides
    /// de paleta/acento viven ahí).</summary>
    private static bool IsCardBrushColorInResources(Windows.UI.Color color)
    {
        try
        {
            if (Application.Current?.Resources is not ResourceDictionary root) return false;
            if (DictionaryHasCardColor(root, color)) return true;
            foreach (var merged in root.MergedDictionaries)
                if (merged is ResourceDictionary dict && DictionaryHasCardColor(dict, color)) return true;
        }
        catch { /* un diccionario raro no puede romper el efecto */ }
        return false;
    }

    /// <summary>¿Algún pincel de card tiene este color en el diccionario, en sus temas o en los
    /// diccionarios que mergea? (Los tres lugares donde puede vivir la clave.)</summary>
    private static bool DictionaryHasCardColor(ResourceDictionary dict, Windows.UI.Color color)
    {
        if (HasCardColor(dict, color)) return true;
        foreach (var theme in dict.ThemeDictionaries.Values)
            if (theme is ResourceDictionary td && HasCardColor(td, color)) return true;
        foreach (var merged in dict.MergedDictionaries)
            if (merged is ResourceDictionary m && HasCardColor(m, color)) return true;
        return false;
    }

    private static bool HasCardColor(ResourceDictionary dict, Windows.UI.Color color)
    {
        foreach (var key in CardBrushKeys)
        {
            if (dict.TryGetValue(key, out var value) && value is SolidColorBrush brush && brush.Color == color)
                return true;
        }
        return false;
    }

    /// <summary>Tamaño mínimo de un host marcado a mano (filas planas o cards bajas creadas en
    /// código): alcanza con que haya superficie para dibujar el halo.</summary>
    private static bool IsBigEnoughForReveal(FrameworkElement el)
        => el.ActualWidth >= FlatRowMinWidth && el.ActualHeight >= FlatRowMinHeight;

    // ===== Estado activo =====

    private static bool IsAlive(FrameworkElement el) => el.IsLoaded;

    /// <summary>Si el cursor está dentro de el: centra el halo y devuelve true (camino rápido).</summary>
    private static bool TryCenterIfInside(FrameworkElement el, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(el).Position;
        if (p.X < 0 || p.Y < 0 || p.X > el.ActualWidth || p.Y > el.ActualHeight)
            return false;
        SetCenter(el, e);
        return true;
    }

    private static void Deactivate(FrameworkElement el, ref FrameworkElement? field)
    {
        Hide(el);
        if (ReferenceEquals(field, el))
            field = null;
    }

    private static void EnsureAttached(FrameworkElement host)
    {
        if (_attached.TryGetValue(host, out _))
            return;
        if (!_attached.TryAdd(host, new State()))
            return;

        host.SizeChanged += OnSizeChanged;
        host.Unloaded += OnUnloaded;
        host.PointerCanceled += OnPointerLost;
        host.PointerCaptureLost += OnPointerLost;
    }

    private static void OnPointerLost(object sender, PointerRoutedEventArgs e)
    {
        var host = (FrameworkElement)sender;
        Hide(host);
        // El ListView interno del NavigationView captura/suelta el puntero al
        // clickear (PointerCanceled/PointerCaptureLost). Sin limpiar el elemento
        // activo, el halo quedaba oculto hasta sacar el cursor: _currentNav seguía
        // apuntando al mismo item, el camino rápido del PointerMoved re-centraba
        // y hacía return sin volver a llamar Show(). Reseteando las referencias,
        // el próximo movimiento re-descubre el item y re-dibuja el halo al instante.
        if (ReferenceEquals(_currentNav, host)) _currentNav = null;
        if (ReferenceEquals(_currentCard, host)) _currentCard = null;
    }
    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateGeometry((FrameworkElement)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var host = (FrameworkElement)sender;
        host.SizeChanged -= OnSizeChanged;
        host.Unloaded -= OnUnloaded;
        host.PointerCanceled -= OnPointerLost;
        host.PointerCaptureLost -= OnPointerLost;

        if (ReferenceEquals(_currentNav, host)) _currentNav = null;
        if (ReferenceEquals(_currentCard, host)) _currentCard = null;

        if (_attached.TryGetValue(host, out var st) && st.Visual != null)
        {
            try { ElementCompositionPreview.SetElementChildVisual(host, null); } catch { }
        }
        _attached.Remove(host);
    }

    // ===== Efecto =====

    private static void Show(FrameworkElement host)
    {
        try
        {
            var st = _attached.GetOrCreateValue(host);
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;

            if (st.Brush == null)
            {
                st.Brush = compositor.CreateRadialGradientBrush();
                // Coordenadas del centro/radio en píxeles del elemento (no relativos).
                st.Brush.MappingMode = CompositionMappingMode.Absolute;
                st.Brush.ColorStops.Add(compositor.CreateColorGradientStop(0f, Microsoft.UI.Colors.Transparent));
                st.Brush.ColorStops.Add(compositor.CreateColorGradientStop(0.45f, Microsoft.UI.Colors.Transparent));
                st.Brush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Microsoft.UI.Colors.Transparent));
            }

            // Color resuelto en cada hover: si cambió el tema/la paleta, el próximo
            // reveal ya sale con el acento vigente.
            var accent = ThemeBrushes.Get("AccentBrush").Color;
            st.Brush.ColorStops[0].Color = WithAlpha(accent, CenterAlpha);
            st.Brush.ColorStops[1].Color = WithAlpha(accent, MidAlpha);
            st.Brush.ColorStops[2].Color = Microsoft.UI.Colors.Transparent;

            if (st.Visual == null)
            {
                st.Visual = compositor.CreateSpriteVisual();
                st.Visual.Brush = st.Brush;
                st.Visual.Opacity = 0f;
                st.Visual.IsVisible = false;
                ElementCompositionPreview.SetElementChildVisual(host, st.Visual);
                UpdateGeometry(host);
            }

            st.Visual.IsVisible = true;
            var anim = compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, 1f);
            anim.Duration = FadeIn;
            st.Visual.StartAnimation("Opacity", anim);
        }
        catch { /* el efecto es cosmético: nunca debe romper la UI */ }
    }

    private static void Hide(FrameworkElement host)
    {
        try
        {
            if (!_attached.TryGetValue(host, out var st) || st.Visual == null)
                return;
            var anim = st.Visual.Compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(1f, 0f);
            anim.Duration = FadeOut;
            st.Visual.StartAnimation("Opacity", anim);
        }
        catch { }
    }

    private static void SetCenter(FrameworkElement host, PointerRoutedEventArgs e)
    {
        if (!_attached.TryGetValue(host, out var st) || st.Brush == null)
            return;
        try
        {
            var p = e.GetCurrentPoint(host).Position;
            st.Brush.EllipseCenter = new Vector2((float)p.X, (float)p.Y);
        }
        catch { }
    }

    private static void UpdateGeometry(FrameworkElement host)
    {
        if (!_attached.TryGetValue(host, out var st) || st.Visual == null || st.Brush == null)
            return;
        try
        {
            var compositor = st.Visual.Compositor;
            float w = (float)host.ActualWidth, h = (float)host.ActualHeight;
            if (w <= 0 || h <= 0)
                return;

            st.Visual.Size = new Vector2(w, h);

            // Halo centrado (se mueve con el primer movimiento) y con radio
            // proporcional al elemento, con topes para que no se vuelva gigante.
            float radius = Math.Clamp(Math.Max(w, h) * RadiusFactor, MinRadius, MaxRadius);
            st.Brush.EllipseCenter = new Vector2(w / 2f, h / 2f);
            st.Brush.EllipseRadius = new Vector2(radius, radius);

            // Recorte con las esquinas del elemento: Border usa su CornerRadius
            // real; los ítems del navbar usan el radio estándar del control.
            var geo = compositor.CreateRoundedRectangleGeometry();
            geo.Size = new Vector2(w, h);
            // Radio de recorte: el real del elemento (Border o control con CornerRadius, que es
            // el caso de los hosts marcados por código) y 6 como respaldo.
            float r = host switch
            {
                Border b => (float)Math.Max(Math.Max(b.CornerRadius.TopLeft, b.CornerRadius.TopRight),
                                            Math.Max(b.CornerRadius.BottomLeft, b.CornerRadius.BottomRight)),
                Control c => (float)Math.Max(Math.Max(c.CornerRadius.TopLeft, c.CornerRadius.TopRight),
                                             Math.Max(c.CornerRadius.BottomLeft, c.CornerRadius.BottomRight)),
                _ => 6f
            };
            geo.CornerRadius = new Vector2(r, r);
            var clip = compositor.CreateGeometricClip();
            clip.Geometry = geo;
            st.Visual.Clip = clip;
        }
        catch { }
    }

    private static Color WithAlpha(Color c, byte a) => new() { A = a, R = c.R, G = c.G, B = c.B };
}
