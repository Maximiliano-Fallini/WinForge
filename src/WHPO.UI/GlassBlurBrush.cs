using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;

namespace WHPO_UI;

/// <summary>
/// EL VIDRIO DEL COMPOSITOR: el <c>backdrop-filter: blur(N px)</c> de la app, hecho por el motor.
///
/// Es un <see cref="XamlCompositionBrushBase"/> —o sea un pincel XAML cualquiera, usable como el
/// <c>Background</c> de una card, del menú o de la barra de título— cuyo relleno es un GRAFO DE
/// COMPOSICIÓN:
///
///     backdrop (lo que el elemento tiene DETRÁS)  ->  desenfoque  ->  tinte del tema encima
///
/// El backdrop lo aporta <c>Compositor.CreateBackdropBrush()</c>: todo lo que hay detrás del
/// elemento DENTRO de la ventana —la foto de fondo y su velo—, muestreado por el compositor en la
/// posición del propio elemento y repintado cada vez que algo se mueve. De ahí sale lo importante:
/// el vidrio NO PUEDE desincronizarse al scrollear, porque no hay nada que seguir — ni copia que
/// recalcular, ni parche que recolocar, ni posición que muestrear por frame. Y el radio y el tinte
/// son propiedades del grafo, así que el deslizador de desenfoque vuelve a ser una intensidad de
/// verdad y cambiarla no cuesta nada: el compositor reusa el resultado.
///
/// POR QUÉ NO EL ACRÍLICO DE LA PLATAFORMA (<see cref="PlatformGlass"/>): el material del sistema cae
/// a un color plano cuando el usuario apaga los "Efectos de transparencia" de Windows, que es
/// justamente la configuración en la que el vidrio no aparecía nunca. Este grafo no consulta ese
/// ajuste: es un efecto sobre el contenido de la propia app, no un material del sistema.
/// </summary>
internal sealed class GlassBlurBrush : XamlCompositionBrushBase
{
    /// <summary>
    /// Nombre del efecto de desenfoque dentro del grafo. Es también la RUTA de sus propiedades
    /// animables (`Blur.BlurAmount`), así que no es decorativo: cambiarlo mueve el radio.
    ///
    /// El desenfoque es un <c>GaussianBlurEffect</c> de Win2D: en WinUI 3 los efectos de composición
    /// viven ahí (el paquete Microsoft.Graphics.Win2D), no en Microsoft.UI.Composition.
    /// </summary>
    private const string BlurName = "Blur";

    /// <summary>Nombre del generador del tinte dentro del grafo.</summary>
    private const string TintName = "Tint";

    /// <summary>
    /// Nombre de la entrada del grafo que recibe el backdrop. Se declara explícitamente con
    /// <see cref="CompositionEffectSourceParameter"/> para que no dependa de cómo se llame la
    /// propiedad del efecto: el nombre lo elige este archivo.
    /// </summary>
    private const string BackdropName = "backdrop";

    /// <summary>Instancias vivas: el deslizador de desenfoque las actualiza a todas de una pasada.</summary>
    private static readonly List<WeakReference<GlassBlurBrush>> _live = new();

    /// <summary>El grafo del vidrio (backdrop -> desenfoque -> tinte). Null mientras el pincel está en modo plano.</summary>
    private CompositionEffectBrush? _graph;

    /// <summary>
    /// El relleno PLANO: un <c>CompositionColorBrush</c> con el tinte. Es lo que pinta este pincel con
    /// radio 0 —el desenfoque apagado— y es lo que hace que el radio y la transparencia sigan siendo
    /// dos ajustes distintos sobre el MISMO pincel: sin esto, apagar el desenfoque obligaría a cambiar
    /// la instancia (de vidrio a sólido), y una superficie creada en code-behind —que no vuelve a
    /// resolver el {ThemeResource}— se quedaría con el pincel viejo. Con esto, encender y apagar es una
    /// escritura, y no hay ningún efecto colgado: un desenfoque de radio 0 no se arma.
    /// </summary>
    private CompositionColorBrush? _flat;

    private GlassBlurBrush(float radius, Windows.UI.Color tint)
    {
        Radius = radius;
        Tint = tint;
    }

    /// <summary>Radio del desenfoque, en píxeles (el mismo valor que usa el camino propio).</summary>
    internal float Radius { get; private set; }

    /// <summary>
    /// Color del tinte que va ENCIMA del fondo desenfocado: es el relleno del panel de siempre —el
    /// color del tema con el alfa del ajuste—, así que el aspecto no cambia: solo cambia quién puso
    /// el fondo que se ve a través de él.
    /// </summary>
    internal Windows.UI.Color Tint { get; private set; }

    /// <summary>
    /// ¿Se puede armar el vidrio del compositor en esta máquina? Se comprueba con un grafo de prueba:
    /// si el compositor no lo acepta, el relleno de los paneles sigue siendo el sólido de siempre (y el
    /// vidrio lo intenta el acrílico o, si no, la capa propia: ver PanelAppearance). Un "no" NO se
    /// cachea para siempre —se reintenta cada <see cref="ProbeIntervalMs"/>, porque la primera consulta
    /// puede caer antes de que el compositor exista— y un "sí" sí (no cambia en toda la sesión).
    /// </summary>
    internal static bool Available
    {
        get
        {
            // SE REINTENTA MIENTRAS NO SE PUEDA, a propósito: la primera consulta puede caer antes de
            // que exista el compositor (arranque temprano), y cachear ese "no" dejaría a la app sin
            // vidrio del compositor durante toda la sesión —el mismo tipo de candado cerrado al revés
            // que ya nos costó un día: un estado que se lee antes de escribirse—. El costo de reintentar
            // solo se paga en máquinas donde el grafo no se puede armar.
            if (_available) return true;

            // …pero NO en ráfaga: quien pregunta lo hace por cada superficie y por cada tick del
            // deslizador (ver ThemeBrushes.PaintSurface), así que en una máquina donde el grafo no se
            // arma cada consulta costaría una prueba nueva. Con el intervalo, el reintento sigue
            // existiendo (cada pocos segundos) y la ráfaga deja de golpear al compositor.
            long now = Environment.TickCount64;
            if (_probed && now - _lastProbe < ProbeIntervalMs) return false;
            _probed = true;
            _lastProbe = now;
            _available = Probe();
            return _available;
        }
    }

    private static bool _available;
    private static bool _probed;
    private static long _lastProbe;

    /// <summary>Cada cuánto se reintenta la prueba del grafo mientras no se pueda armar.</summary>
    private const long ProbeIntervalMs = 5000;

    /// <summary>
    /// Arma una instancia con su grafo ya construido. Devuelve null si el compositor no puede: el
    /// llamador se queda con el relleno que tenía (nunca con un pincel vacío, que dejaría el panel
    /// invisible).
    /// </summary>
    internal static GlassBlurBrush? TryCreate(float radius, Windows.UI.Color tint)
    {
        try
        {
            var brush = new GlassBlurBrush(radius, tint);
            if (!brush.Build()) return null;
            _live.Add(new WeakReference<GlassBlurBrush>(brush));
            return brush;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Radio nuevo (el deslizador o el cambio de camino). Es lo único que decide si este pincel es
    /// vidrio o relleno plano: con radio 0 no hay efecto, así que "sin desenfoque" no cuesta nada.
    /// </summary>
    internal void SetRadius(float radius)
    {
        bool wasFlat = Radius <= 0f;
        Radius = radius;

        // Sigue plano: alcanza con el color (lo que cambió, si cambió, es el tinte).
        if (radius <= 0f)
        {
            if (wasFlat && _flat != null)
            {
                try { _flat.Color = Tint; } catch { }
                return;
            }

            Rebuild();
            return;
        }

        // Vuelve el vidrio (o es el primero): hay que armar el grafo.
        if (wasFlat || _graph == null)
        {
            Rebuild();
            return;
        }

        try { _graph.Properties.InsertScalar($"{BlurName}.BlurAmount", radius); } catch { }
    }

    /// <summary>Tinte nuevo (cambió el tema o la transparencia): misma idea, una escritura.</summary>
    internal void SetTint(Windows.UI.Color tint)
    {
        Tint = tint;

        // Se actualiza el relleno vigente y, si quedara el otro armado (el caso raro de un grafo que
        // no se pudo reconstruir), también: un tinte viejo es un panel del color del tema anterior.
        if (Radius <= 0f)
        {
            try { if (_flat != null) _flat.Color = tint; } catch { }
            return;
        }

        try { if (_graph != null) _graph.Properties.InsertColor($"{TintName}.Color", tint); } catch { }
    }

    /// <summary>
    /// Rearma el relleno para el radio vigente. Si no se puede (el compositor no está listo, o se
    /// pidió vidrio en una máquina que no lo soporta) el pincel CONSERVA el relleno que ya tenía: un
    /// grafo que no se pudo armar no puede dejar el panel sin fondo.
    /// </summary>
    private void Rebuild()
    {
        if (!Build() && _flat == null && _graph == null) CompositionBrush = null;
    }

    /// <summary>
    /// Radio nuevo para TODOS los vidrios vivos. Es lo que hace que el deslizador de desenfoque sea
    /// instantáneo: una escritura por pincel, sin reescribir ninguna superficie ni re-resolver nada.
    /// </summary>
    internal static void SetRadiusForAll(float radius)
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            if (_live[i].TryGetTarget(out var brush)) brush.SetRadius(radius);
            else _live.RemoveAt(i);
        }
    }

    /// <summary>
    /// El grafo: desenfoque del backdrop, con el tinte del panel compuesto encima. Los nombres de las
    /// propiedades animables son <c>&lt;NombreDelEfecto&gt;.&lt;Propiedad&gt;</c>, y la entrada del
    /// backdrop es la que declara <see cref="BackdropName"/>.
    /// </summary>
    private bool Build()
    {
        var compositor = CompositionTarget.GetCompositorForCurrentThread();

        // RADIO 0 = RELLENO PLANO, a propósito: es el desenfoque apagado, y ahí el pincel tiene que
        // costar lo mismo que un color sólido. El tinte es el mismo en los dos modos, así que el
        // cambio de uno al otro no mueve un píxel visible.
        if (Radius <= 0f)
        {
            _flat = compositor.CreateColorBrush(Tint);
            _graph = null;
            CompositionBrush = _flat;
            return true;
        }

        var blur = new GaussianBlurEffect
        {
            Name = BlurName,
            BlurAmount = Radius,
            BorderMode = EffectBorderMode.Hard,
            // Velocidad antes que calidad: es un desenfoque de fondo que va debajo de contenido, no una
            // imagen que se mira de cerca, y hay un vidrio por panel (ver el mismo criterio en PanelBlurAlgorithm).
            Optimization = EffectOptimization.Speed,
            Source = new CompositionEffectSourceParameter(BackdropName)
        };

        var tint = new ColorSourceEffect { Name = TintName, Color = Tint };

        var glass = new CompositeEffect { Name = "Glass", Mode = CanvasComposite.SourceOver };
        glass.Sources.Add(blur);    // el fondo: el backdrop desenfocado
        glass.Sources.Add(tint);    // encima: el relleno del panel

        var factory = compositor.CreateEffectFactory(
            glass,
            new[] { $"{BlurName}.BlurAmount", $"{TintName}.Color" });

        var brush = factory.CreateBrush();
        brush.SetSourceParameter(BackdropName, compositor.CreateBackdropBrush());

        _graph = brush;
        _flat = null;
        CompositionBrush = brush;
        return true;
    }

    /// <summary>
    /// Al conectar el pincel, XAML espera el pincel de composición puesto. El grafo ya está construido
    /// (ver <see cref="TryCreate"/>), así que acá solo se asegura de que esté: asignarlo recién en este
    /// momento no sirve (si el grafo no se pudo armar, el panel quedaría vacío), y no asignarlo deja al
    /// elemento sin relleno.
    /// </summary>
    protected override void OnConnected()
    {
        // El relleno vigente es el plano (radio 0) o el grafo: se conecta el que corresponda.
        var fill = (CompositionBrush?)_flat ?? _graph;
        if (fill != null && !ReferenceEquals(CompositionBrush, fill)) CompositionBrush = fill;
    }

    /// <summary>
    /// El grafo es compartido por muchos elementos (una card, el menú, la barra de título), así que
    /// NO se libera al desconectarse uno: los demás lo siguen usando. Vive lo que vive el proceso.
    /// </summary>
    protected override void OnDisconnected()
    {
    }

    private static bool Probe()
    {
        try
        {
            var probe = TryCreate(1f, Windows.UI.Color.FromArgb(255, 0, 0, 0));
            return probe != null;
        }
        catch
        {
            return false;
        }
    }
}
