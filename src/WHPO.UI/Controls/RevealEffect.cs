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
    private const float CardMinHeight = 56f;
    private const float CardMaxWidth = 2200f;   // admite cards de ancho completo (ej. Sistema Operativo)
    private const float CardMaxHeight = 900f;
    private const float CardMinCornerRadius = 6f;

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
        var card = FindCardUp(e.OriginalSource as DependencyObject, CardWalkMaxDepth);
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

    private static Border? FindCardUp(DependencyObject? start, int maxDepth)
    {
        var d = start;
        for (int i = 0; d != null && i < maxDepth; i++)
        {
            if (d is Border b)
            {
                // Exclusión explícita: corta la búsqueda acá (la card marcada y
                // sus hijos quedan sin reveal; tampoco "trepa" a un contenedor
                // ancestro, que daría un halo en el lugar equivocado).
                if (b.Tag as string == NoRevealTag)
                    return null;
                if (IsCard(b))
                    return b;
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

        var card = ThemeBrushes.Get("CardBackgroundBrush").Color;
        var core = ThemeBrushes.Get("CoreCardBackgroundBrush").Color;
        return scb.Color == card || scb.Color == core;
    }

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
            float r = host is Border b
                ? (float)Math.Max(Math.Max(b.CornerRadius.TopLeft, b.CornerRadius.TopRight),
                                  Math.Max(b.CornerRadius.BottomLeft, b.CornerRadius.BottomRight))
                : 6f;
            geo.CornerRadius = new Vector2(r, r);
            var clip = compositor.CreateGeometricClip();
            clip.Geometry = geo;
            st.Visual.Clip = clip;
        }
        catch { }
    }

    private static Color WithAlpha(Color c, byte a) => new() { A = a, R = c.R, G = c.G, B = c.B };
}
