using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace WHPO_UI;

/// <summary>
/// EL VIDRIO DECLARATIVO: los paneles muestran la copia desenfocada sin que nadie la mueva por frame.
///
/// QUÉ REEMPLAZA (el problema que deja atrás): el camino por frame (ver <see cref="PanelBlurLayer"/>)
/// calcula en managed, frame a frame, dónde va cada parche y qué pedazo de foto muestra. Eso obliga a
/// que posición, fase del pincel y recorte entren en el MISMO pase, y con el scroll en movimiento el
/// layout queda diferido: la posición se aplicaba tarde mientras la fase y el recorte —cambios de
/// render— ya estaban puestos, así que el recorte se corría fuera del parche y el vidrio se apagaba.
///
/// ACÁ NO SE CALCULA: SE DECLARA. Cada panel aporta un recorte redondeado (una geometría de
/// composición) sobre una copia desenfocada ANCLADA A LA VENTANA que nunca se mueve: no hay fase que
/// mantener, porque la imagen y el recorte viven en el mismo sistema de coordenadas. Lo único que
/// cambia por frame es el <c>Offset</c> de esa geometría, y lo cambia el COMPOSITOR: una
/// <see cref="ExpressionAnimation"/> que referencia la posición animada del scroll
/// (<c>ScrollPresenter.ExpressionAnimationSources</c>, que es la verdad del InteractionTracker, no el
/// eco tardío de <c>ScrollViewer.VerticalOffset</c>).
///
/// Es la misma propiedad que hace que el vidrio de una app web no se pueda desincronizar con
/// <c>backdrop-filter</c>: el que resuelve el dibujo por frame es el motor de composición, así que del
/// lado managed no queda trabajo que pueda llegar tarde. La zona visible del scroller sale gratis: es
/// el recorte FIJO de la banda que agrupa los recortes de ese scroller.
///
/// LA FUENTE SE CALIBRA ANTES DE ENSEÑAR NADA: el valor animado no se puede leer desde managed, así
/// que un factor o un signo equivocados dejarían el vidrio corrido sin que el log dijera nada. El
/// vidrio arranca apagado, el primer movimiento del scroll mide la relación entre la fuente y
/// <c>VerticalOffset</c>, y recién con eso se enciende. Si la fuente no existe, no expone
/// <c>Position</c> o no acompaña al scroll, el vidrio queda apagado y sigue dibujando el camino por
/// frame: nunca se queda sin vidrio.
/// </summary>
internal static class PanelGlass
{
    /// <summary>
    /// Un recorte a declarar: dónde va (coordenadas de ventana), con qué radio, y de qué scroller y
    /// zona visible sale su scroll. Los paneles que no scrollean —barra de título, franja del menú—
    /// van con <see cref="Scroller"/> en null y no se mueven nunca.
    /// </summary>
    internal struct Item
    {
        /// <summary>El índice del parche en <see cref="PanelBlurLayer"/>: es la llave con la que llega su posición.</summary>
        public int Index;
        public Rect Bounds;
        public double Radius;
        public ScrollViewer? Scroller;
        public Rect Viewport;
    }

    /// <summary>Un recorte: el sprite anclado a la ventana y la geometría que lo recorta.</summary>
    private sealed class Clip
    {
        public SpriteVisual Sprite = null!;
        public CompositionRoundedRectangleGeometry Geometry = null!;
        public ExpressionAnimation? Shift;
        public Band? Band;
        public Rect Bounds;

        /// <summary>El índice del parche al que corresponde (el orden de la declaración, ver Follow).</summary>
        public int Index;

        /// <summary>
        /// La expresión quedó ENGANCHADA: desde ahí las posiciones las resuelve el compositor y el pase por
        /// frame no debe escribirle el valor base (la animación manda y el valor base quedaría de ruido).
        /// </summary>
        public bool Animated;
    }

    /// <summary>
    /// Un grupo de recortes que se mueven juntos: los paneles de UN scroller comparten la zona visible
    /// (el recorte de la banda) y la fuente animada del scroll, así que comparten también el factor
    /// medido en la calibración. Las bandas se conservan entre rearmados —ahí vive lo calibrado— y
    /// <see cref="Active"/> dice cuáles usa la declaración vigente.
    /// </summary>
    private sealed class Band
    {
        public ScrollViewer? Scroller;
        public ContainerVisual? Container;
        public CompositionRoundedRectangleGeometry? ViewportGeometry;
        public Rect Viewport;
        public CompositionPropertySet? Sources;
        public bool Looked;
        public int LookAt;
        public bool Calibrated;
        public double K = 1;
        public double Zero;
        public bool Sampled;
        public double SampleOffset;
        public double SampleSource;
        public int Tries;
        public bool Active;
        public bool Lost;
    }

    private static Compositor? _compositor;
    private static ContainerVisual? _root;
    private static Canvas? _layer;
    private static CompositionSurfaceBrush? _brush;
    private static ICompositionSurface? _surface;
    private static readonly List<Clip> _clips = new();
    private static readonly List<Band> _bands = new();
    private static readonly List<Item> _declared = new();
    private static readonly List<Item> _pending = new();
    private static readonly List<Band> _active = new();

    /// <summary>Índice del parche en <see cref="PanelBlurLayer"/> → recorte, para el seguimiento por frame.</summary>
    private static readonly List<Clip?> _byIndex = new();
    private static bool _live;
    private static bool _failed;

    /// <summary>
    /// El vidrio declarativo está DIBUJANDO: el grafo existe y los parches XAML de
    /// <see cref="PanelBlurLayer"/> quedan apagados. Las posiciones las mueve el pase por frame
    /// (<see cref="Follow"/>) y, si alguna vez aparece una fuente animada, las mueve el compositor
    /// (<see cref="Pump"/>): el recorte es el mismo en los dos casos.
    /// </summary>
    internal static bool Live => _live;

    /// <summary>
    /// Lleva el recorte del parche <paramref name="index"/> a su posición de ventana. Es el seguimiento
    /// por frame del vidrio declarativo: UNA propiedad de RENDER por panel —no toca el layout, no hay fase
    /// de pincel, no hay recorte que recalcular—, así que no compite con el pase de layout del scroll ni
    /// puede quedar a medio camino de la imagen. Con expresión enganchada no se llama: manda el compositor.
    /// </summary>
    internal static void Follow(int index, double windowTop)
    {
        if (!_live || index < 0 || index >= _byIndex.Count) return;

        var clip = _byIndex[index];
        if (clip == null || clip.Animated) return;

        try
        {
            var offset = clip.Geometry.Offset;
            var top = (float)windowTop;
            if (offset.Y == top) return;
            clip.Geometry.Offset = new Vector2(offset.X, top);
        }
        catch
        {
            // Un recorte a medio rearmar no puede tumbar el seguimiento: el próximo pase lo deja bien.
        }
    }

    /// <summary>
    /// Hay con qué intentarlo: no falló y ya hay copia desenfocada en una surface de composición. Lo
    /// consulta el pase de medida para no armar la lista de recortes cuando el vidrio declarativo no
    /// está en juego.
    ///
    /// MIRA LA SURFACE DE <see cref="PanelBlur"/> Y NO LA DE ACÁ: la de acá se asigna en
    /// <see cref="Declare"/>, así que preguntando por ella el gate quedaba cerrado para siempre —nadie
    /// declaraba, la surface de acá nunca se llenaba— y el vidrio no se intentaba ni una vez.
    /// </summary>
    internal static bool Wanted => !_failed && PanelBlur.Surface != null;

    /// <summary>
    /// Declara (o actualiza) los recortes del vidrio. La llama el pase de medida de
    /// <see cref="PanelBlurLayer"/>, en quieto: los rectángulos llegan medidos y acá no se vuelve a
    /// tocar el árbol de XAML.
    ///
    /// ES BARATA SI NADA CAMBIÓ: el pase de medida corre en cada layout, y cuando la declaración es
    /// igual a la anterior —el caso normal— sale sin tocar la composición. Cuando algo cambió
    /// (navegación, ventana redimensionada, cards rearmadas, fin de un scroll) rehace el grafo; eso
    /// pasa siempre con el contenido quieto, así que el reemplazo no se ve.
    /// </summary>
    internal static void Declare(Canvas layer, List<Item> items, ICompositionSurface? surface, double windowWidth, double windowHeight)
    {
        if (_failed)
        {
            Hide();
            return;
        }

        if (surface == null || windowWidth < 1 || windowHeight < 1)
        {
            _surface = null;
            Hide();
            return;
        }

        _pending.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Bounds.Width < 1 || item.Bounds.Height < 1) continue;
            _pending.Add(item);
        }

        try
        {
            if (!Ensure(layer, windowWidth, windowHeight)) { Hide(); return; }

            // La asignación va DESPUÉS de Ensure —si hubo que rearmar el grafo por un cambio de capa,
            // Ensure limpia el estado y se llevaría puesta la copia vigente— y la comparación es contra
            // la copia que el grafo TIENE puesta, no contra la que se acaba de pedir: comparando contra
            // sí misma, cambiar la intensidad del deslizador no se vería hasta el próximo rearmado.
            var built = _surface;
            _surface = surface;
            if (!SameAs(built, surface, windowWidth, windowHeight)) Rebuild(windowWidth, windowHeight);
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        // El grafo existe, pero no se enseña hasta que la fuente esté calibrada (ver Pump): apagado,
        // los parches XAML siguen dibujando y no se nota que hay algo esperando.
        _root!.IsVisible = _live;
    }

    /// <summary>
    /// Un frame con el scroll en movimiento: acá se calibra la fuente y, cuando está, se enciende el
    /// vidrio. Devuelve true cuando el compositor ya se está haciendo cargo —y entonces el seguimiento
    /// por frame de <see cref="PanelBlurLayer"/> no tiene nada que mover.
    /// </summary>
    internal static bool Pump()
    {
        if (_failed) return false;
        if (_live) return true;
        if (_root == null || _active.Count == 0) return false;

        try
        {
            bool ready = true;
            foreach (var band in _active)
            {
                if (band.Scroller == null || band.Calibrated) continue;

                if (!band.Looked) Look(band);
                if (band.Sources == null) { ready = false; continue; }

                double offset = band.Scroller.VerticalOffset;
                if (band.Sources.TryGetVector2("Position", out var position) != CompositionGetValueStatus.Succeeded)
                {
                    Lose(band, "la fuente no expone Position");
                    ready = false;
                    continue;
                }

                if (!band.Sampled)
                {
                    band.Sampled = true;
                    band.SampleOffset = offset;
                    band.SampleSource = position.Y;
                    ready = false;
                    continue;
                }

                double dOffset = offset - band.SampleOffset;
                double dSource = position.Y - band.SampleSource;

                // Con menos de unos pocos puntos de scroll la relación no dice nada: se espera a que el
                // arrastre sea real.
                if (Math.Abs(dOffset) < 3) { ready = false; continue; }

                double factor = dSource / dOffset;
                if (!double.IsFinite(factor) || Math.Abs(factor) < 0.05)
                {
                    // La fuente no acompaña al scroll en este tramo. Se vuelve a muestrear: puede ser el
                    // primer tramo de un rebote, o un scroll que todavía no llegó al InteractionTracker.
                    band.SampleOffset = offset;
                    band.SampleSource = position.Y;
                    if (++band.Tries >= 3) Lose(band, "la fuente no acompaña al scroll");
                    ready = false;
                    continue;
                }

                // factor = cuánto cambia la fuente por punto de scroll. El recorte tiene que SUBIR cuando
                // el contenido sube, o sea al revés del offset: de ahí el signo.
                band.K = -factor;
                band.Calibrated = true;
                Attach(band);
            }

            if (!ready) return false;

            _live = true;
            if (_root != null) _root.IsVisible = true;
            PanelAppearance.Diag(
                $"desenfoque: vidrio en composicion ACTIVO — {_clips.Count} recortes siguen el scroll por " +
                $"expresion (factor {ActiveFactor():0.###}, sin trabajo por frame)");
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
    }

    /// <summary>Apaga el vidrio declarativo (la capa se apagó, cambió el tema o se fue la foto).</summary>
    internal static void Hide()
    {
        _live = false;
        try { if (_root != null) _root.IsVisible = false; } catch { }
    }

    /// <summary>Descarta todo: la capa se apagó y el camino por frame vuelve a mandar.</summary>
    internal static void Reset()
    {
        Hide();
        _bands.Clear();
        _clips.Clear();
        _declared.Clear();
        _active.Clear();
        _byIndex.Clear();
        _surface = null;
        var layer = _layer;
        _root = null;
        _layer = null;
        try
        {
            if (layer != null) ElementCompositionPreview.SetElementChildVisual(layer, null!);
        }
        catch { }
    }

    // ---- armado ---------------------------------------------------------------------------------

    /// <summary>Prepara compositor, root y pincel. Devuelve false si XAML todavía no da un visual.</summary>
    private static bool Ensure(Canvas layer, double windowWidth, double windowHeight)
    {
        if (_root != null && _compositor != null && _brush != null && ReferenceEquals(_layer, layer)) return true;
        if (_layer != null && !ReferenceEquals(_layer, layer)) Reset();

        _layer = layer;
        _compositor = ElementCompositionPreview.GetElementVisual(layer).Compositor;

        _root = _compositor.CreateContainerVisual();
        _root.Size = new Vector2((float)windowWidth, (float)windowHeight);
        _root.Offset = Vector3.Zero;

        // El pincel: la copia desenfocada estirada al tamaño de la ventana, EXACTAMENTE el mismo mapeo
        // que hacía el pincel de XAML (ver PanelBlurLayer.Paint). Al estar anclado a un sprite que no se
        // mueve, no hay fase que mantener.
        _brush = _compositor.CreateSurfaceBrush();
        _brush.Stretch = CompositionStretch.Fill;
        ElementCompositionPreview.SetElementChildVisual(layer, _root);
        return true;
    }

    /// <summary>¿La declaración es la misma que la vigente? Es el caso normal: el pase de medida corre en cada layout.</summary>
    private static bool SameAs(ICompositionSurface? built, ICompositionSurface surface, double windowWidth, double windowHeight)
    {
        if (!ReferenceEquals(surface, built) || _clips.Count != _pending.Count) return false;
        if (Math.Abs(_root!.Size.X - (float)windowWidth) > 0.5 || Math.Abs(_root.Size.Y - (float)windowHeight) > 0.5) return false;

        for (int i = 0; i < _pending.Count; i++)
        {
            var previous = _declared[i];
            var item = _pending[i];
            if (previous.Index != item.Index || previous.Bounds != item.Bounds || previous.Radius != item.Radius) return false;
            if (!ReferenceEquals(previous.Scroller, item.Scroller) || previous.Viewport != item.Viewport) return false;
        }

        return true;
    }

    /// <summary>
    /// Rehace el grafo con la declaración nueva. Las bandas se REUSAN —ahí vive el factor calibrado de
    /// cada scroller— y lo único que se rearma son los sprites con sus geometrías.
    /// </summary>
    private static void Rebuild(double windowWidth, double windowHeight)
    {
        var compositor = _compositor!;
        _root!.Size = new Vector2((float)windowWidth, (float)windowHeight);

        // El pincel apunta a la copia vigente. Si cambió la foto o el deslizador la surface es otra y el
        // pincel se rehace acá (una vez por cambio, nunca por frame).
        if (!ReferenceEquals(_brush!.Surface, _surface)) _brush.Surface = _surface;

        foreach (var band in _bands) band.Active = false;
        _active.Clear();

        var clips = new List<Clip>(_pending.Count);
        for (int index = 0; index < _pending.Count; index++)
        {
            var item = _pending[index];
            var band = BandFor(item);
            var sprite = compositor.CreateSpriteVisual();
            sprite.Size = new Vector2((float)windowWidth, (float)windowHeight);
            sprite.Offset = Vector3.Zero;
            sprite.Brush = _brush;

            // El recorte del panel: redondeado, del tamaño del panel y —lo único que se mueve por
            // frame— con el Offset animado contra la fuente del scroll.
            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.Offset = new Vector2((float)item.Bounds.Left, (float)item.Bounds.Top);
            geometry.Size = new Vector2((float)item.Bounds.Width, (float)item.Bounds.Height);
            geometry.CornerRadius = new Vector2((float)item.Radius, (float)item.Radius);
            sprite.Clip = compositor.CreateGeometricClip(geometry);

            clips.Add(new Clip { Index = item.Index, Sprite = sprite, Geometry = geometry, Band = band, Bounds = item.Bounds });
        }

        // El grafo nuevo, de cero: el root se vacía y cada banda activa vuelve con su recorte de zona
        // visible y sus recortes adentro.
        _root.Children.RemoveAll();
        foreach (var band in _active) band.Container?.Children.RemoveAll();

        foreach (var band in _active)
        {
            if (band.Container == null) continue;
            band.Container.Size = _root.Size;
            band.Container.Offset = Vector3.Zero;
            _root.Children.InsertAtTop(band.Container);
        }

        foreach (var clip in clips)
        {
            if (clip.Band?.Container == null) _root.Children.InsertAtTop(clip.Sprite);
            else clip.Band.Container.Children.InsertAtTop(clip.Sprite);
        }

        _clips.Clear();
        _clips.AddRange(clips);
        _declared.Clear();
        _declared.AddRange(_pending);

        // El índice del parche → su recorte: es lo que usa el seguimiento por frame (ver Follow). La tabla
        // se dimensiona con el índice MÁS ALTO declarado, así un parche degenerado que no llegó a tener
        // recorte deja su lugar vacío en vez de correr el de todos los que vienen detrás.
        int slots = 0;
        foreach (var item in _pending) if (item.Index + 1 > slots) slots = item.Index + 1;
        _byIndex.Clear();
        for (int i = 0; i < slots; i++) _byIndex.Add(null);
        foreach (var clip in clips)
        {
            if (clip.Index >= 0 && clip.Index < _byIndex.Count) _byIndex[clip.Index] = clip;
        }

        // Del grafo a la pantalla: con la copia desenfocada puesta, el vidrio YA se puede dibujar —lo
        // siguen las posiciones por frame—, así que no espera a ninguna calibración para encenderse.
        _live = _clips.Count > 0;

        PanelAppearance.Diag(
            $"desenfoque: vidrio declarado — {_clips.Count} recortes en {_active.Count} bandas " +
            $"(lo pinta la composicion; las posiciones las sigue el pase por frame)");

        // Y recién acá se enganchan las expresiones: necesitan el cero de la fuente (su valor con el
        // contenido quieto, que es contra el que se mide el corrimiento).
        foreach (var band in _active) Bind(band);
    }

    /// <summary>La banda de un item: la de su scroller (con su zona visible) o la de los paneles fijos.</summary>
    private static Band BandFor(Item item)
    {
        foreach (var band in _bands)
        {
            if (item.Scroller == null)
            {
                if (band.Scroller != null) continue;
            }
            else if (!ReferenceEquals(band.Scroller, item.Scroller)) continue;

            // Ya existía (o venía de una declaración anterior): se reactiva, se le refresca la zona
            // visible y —si todavía no tiene fuente— se la busca otra vez, que es cuando el contenido
            // está quieto.
            band.Active = true;
            band.Viewport = item.Viewport;
            SetViewport(band, item.Viewport);
            MaybeLook(band);
            if (!_active.Contains(band)) _active.Add(band);
            return band;
        }

        var fresh = new Band { Scroller = item.Scroller, Active = true, Viewport = item.Viewport };
        if (item.Scroller != null)
        {
            fresh.Container = _compositor!.CreateContainerVisual();
            fresh.Container.Size = _root!.Size;
            fresh.Container.Offset = Vector3.Zero;
            fresh.ViewportGeometry = _compositor.CreateRoundedRectangleGeometry();
            fresh.Container.Clip = _compositor.CreateGeometricClip(fresh.ViewportGeometry);
            SetViewport(fresh, item.Viewport);
        }

        MaybeLook(fresh);
        _bands.Add(fresh);
        _active.Add(fresh);
        return fresh;
    }

    /// <summary>El recorte FIJO de la zona visible: es lo que hace que una card fuera de vista no pinte sobre el navbar.</summary>
    private static void SetViewport(Band band, Rect viewport)
    {
        var geometry = band.ViewportGeometry;
        if (geometry == null || viewport.Width < 1 || viewport.Height < 1) return;

        geometry.Offset = new Vector2((float)viewport.X, (float)viewport.Y);
        geometry.Size = new Vector2((float)viewport.Width, (float)viewport.Height);
        geometry.CornerRadius = Vector2.Zero;
    }

    // ---- la fuente animada ----------------------------------------------------------------------

    /// <summary>
    /// Busca la fuente de la banda si todavía no la tiene. Se llama AL DECLARAR —con el contenido
    /// quieto, que es cuando el cero de la expresión se puede leer— y no en el primer scroll: si la
    /// expresión no existiera al calibrar, el vidrio se encendería sin animación, clavado en su
    /// posición base mientras las cards se van (otra vez "desaparece", sin un solo error).
    ///
    /// Reintenta cada tanto cuando la búsqueda anterior falló: el ScrollPresenter puede no existir
    /// todavía en el primer pase de una página.
    /// </summary>
    private static void MaybeLook(Band band)
    {
        if (band.Scroller == null || band.Sources != null) return;
        if (band.Looked && Environment.TickCount - band.LookAt < 2000) return;
        Look(band);
    }

    /// <summary>Busca la fuente animada del scroller: el property set del ScrollPresenter.</summary>
    private static void Look(Band band)
    {
        band.Looked = true;
        band.LookAt = Environment.TickCount;
        try
        {
            if (band.Scroller == null) return;

            var presenter = PresenterOf(band.Scroller, 0);
            if (presenter == null)
            {
                Lose(band, "el scroller no expone su ScrollPresenter");
                return;
            }

            var sources = presenter.ExpressionAnimationSources;
            if (sources == null) Lose(band, "el ScrollPresenter no expone la fuente animada");
            else band.Sources = sources;
        }
        catch (Exception ex)
        {
            Lose(band, ex.Message);
        }
    }

    /// <summary>
    /// El ScrollPresenter del scroller: la fuente animada vive ahí. Se busca por el ÁRBOL VISUAL porque
    /// el presentador puede estar detrás de partes internas; el recorrido en preorden encuentra el del
    /// propio scroller antes que el de un scroller anidado.
    /// </summary>
    private static ScrollPresenter? PresenterOf(DependencyObject node, int depth)
    {
        if (depth > 16) return null;
        if (node is ScrollPresenter presenter) return presenter;

        int count;
        try { count = VisualTreeHelper.GetChildrenCount(node); } catch { return null; }

        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            var found = PresenterOf(child, depth + 1);
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>
    /// Engancha (o reengancha) la expresión de una banda: <c>recorte = base + (fuente - cero) * k</c>.
    /// Se llama al rearmar el grafo y cuando la calibración cambia el factor —reenganchar es lo que hace
    /// valer el valor nuevo, sin depender de que el motor relea los parámetros de una animación andando.
    /// </summary>
    private static void Bind(Band band)
    {
        if (band.Sources == null) return;

        // El CERO es el de la declaración —el grafo acaba de ponerse con las posiciones medidas, o sea
        // con el contenido quieto—: es el punto contra el que la expresión mide el corrimiento. Se toma
        // acá, UNA vez por declaración, y no en cada enganche: si se releyera con el scroll andando, el
        // recorte volvería de golpe a su posición base justo al encender el vidrio.
        band.Zero = SourceY(band) ?? band.Zero;

        foreach (var clip in _clips)
        {
            if (!ReferenceEquals(clip.Band, band)) continue;

            var shift = _compositor!.CreateExpressionAnimation("base + (scroll.Position.Y - zero) * k");
            shift.SetScalarParameter("base", (float)clip.Bounds.Top);
            shift.SetScalarParameter("zero", (float)band.Zero);
            shift.SetScalarParameter("k", (float)band.K);
            shift.SetReferenceParameter("scroll", band.Sources);
            clip.Shift = shift;
            clip.Animated = false;   // una expresión sin enganchar no manda: el pase por frame sigue escribiendo
        }

        if (band.Calibrated) Attach(band);   // sin calibrar no se engancha: el vidrio está apagado
    }

    /// <summary>
    /// Toma el cero de la fuente con el contenido quieto —el grafo ya está puesto con las posiciones
    /// medidas— y arranca la animación de cada recorte de la banda. Al ser una función pura de la
    /// fuente, la expresión no acumula error: el recorte está siempre donde le toca.
    /// </summary>
    private static void Attach(Band band)
    {
        if (band.Sources == null) return;

        foreach (var clip in _clips)
        {
            if (!ReferenceEquals(clip.Band, band) || clip.Shift == null) continue;

            try
            {
                clip.Shift.SetScalarParameter("zero", (float)band.Zero);
                clip.Shift.SetScalarParameter("k", (float)band.K);
                clip.Geometry.StartAnimation("Offset.Y", clip.Shift);
                clip.Animated = true;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return;
            }
        }
    }

    /// <summary>El valor de la fuente con el contenido quieto; null si no lo expone.</summary>
    private static double? SourceY(Band band)
    {
        if (band.Sources == null) return null;
        try
        {
            return band.Sources.TryGetVector2("Position", out var position) == CompositionGetValueStatus.Succeeded
                ? position.Y
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>La fuente de esa banda no sirve: el vidrio declarativo queda apagado y lo dice el log (una sola vez).</summary>
    private static void Lose(Band band, string reason)
    {
        band.Sources = null;
        band.Calibrated = false;
        if (band.Lost) return;
        band.Lost = true;
        PanelAppearance.Diag(
            $"desenfoque: vidrio en composicion sin fuente para " +
            $"{(band.Scroller == null ? "los paneles fijos" : ScrollerName(band.Scroller))} ({reason}) — sigue el seguimiento por frame");
    }

    private static string ScrollerName(ScrollViewer scroller)
        => string.IsNullOrEmpty(scroller.Name) ? "(sin nombre)" : scroller.Name;

    /// <summary>El factor calibrado de la primera banda que scrollea: es el número que va al log.</summary>
    private static double ActiveFactor()
    {
        foreach (var band in _active)
            if (band.Scroller != null && band.Calibrated) return band.K;
        return 1;
    }

    private static void Fail(Exception ex)
    {
        _failed = true;
        Hide();
        PanelAppearance.Diag($"desenfoque: el vidrio declarativo no se pudo armar ({ex.Message}) — sigue el seguimiento por frame");
    }
}
