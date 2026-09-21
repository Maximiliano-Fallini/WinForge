using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace WHPO_UI.Controls;

/// <summary>
/// Icono del apartado de control de ventiladores, dibujado en el MISMO lenguaje
/// que el resto de los iconos de la app: monocromatico y de REBORDE, sin formas
/// macizas. Antes era una silueta rellena (cuatro aspas solidas pegadas al
/// centro) y al lado de los glifos de linea que usa la app —lápiz, lupa, tildes
/// de Segoe Fluent Icons— desentonaba: parecia un emoji relleno.
///
/// El trazado es un fan clasico —aro exterior, cuatro aspas barridas y buje— pero
/// compuesto como contornos huecos: cada pieza aporta su borde externo y su borde
/// interno, y con FillRule EvenOdd el interior recorta el agujero, asi que el
/// resultado se lee como una linea de grosor constante. Se compone con geometria
/// RELLENA (y no con Stroke) a proposito: PathIcon —el que usan el navbar y las
/// cards del catalogo— solo rellena, no tiene trazo, y asi las tres variantes
/// (titulo, navbar y cards) se ven identicas.
///
/// Geometria construida 100% en codigo: NO usar XamlReader ni reutilizar
/// geometrías entre Paths (en WinUI 3 asignar la misma Geometry a dos Paths
/// revienta con "Value does not fall within the expected range").
/// </summary>
public static class FanIcon
{
    // Espacio de diseno de 24x24, igual que el fan de SensoresPage, para que los
    // dos ventiladores de la app tengan las mismas proporciones.
    private const double DesignBox = 24;
    private const double Fill = 0.95;      // fraccion del cuadro que ocupa el aro
    private const double Line = 1.55;      // grosor de linea, en unidades de diseno
    private const double RimRadius = 11;   // aro exterior
    private const double HubRadius = 2.9;  // buje central
    private const double BladeRoot = 4.6;  // radio de la raiz del aspa (junto al buje)
    private const double BladeGap = 1.1;   // aire entre la punta del aspa y el aro
    private const double BladeHalf = 20;   // media apertura angular del aspa, grados
    private const double BladeSweep = 24;  // barrido del aspa en el sentido de giro

    /// <summary>
    /// Fan de linea: aro, cuatro aspas barridas y buje. La geometria se genera YA
    /// en coordenadas de pixel del tamano final (centrada en size/2): asi el mismo
    /// dato sirve para el Path del titulo/cards y para el PathIcon del navbar sin
    /// depender de que cada contenedor lo escale igual.
    /// </summary>
    private static PathGeometry BuildGeometry(double size)
    {
        double s = size / DesignBox * Fill;
        double cx = size / 2, cy = size / 2;

        double t = Line * s;                       // grosor de linea, en pixeles
        double rim = RimRadius * s;
        double hub = HubRadius * s;
        double rIn = BladeRoot * s;
        double rOut = (RimRadius - Line - BladeGap) * s;

        var geo = new PathGeometry { FillRule = FillRule.EvenOdd };

        // Aro y buje: dos circulos concentricos cada uno; el interno recorta el hueco.
        AddRing(geo, cx, cy, rim, Math.Max(rim - t, rim * 0.75));
        AddRing(geo, cx, cy, hub, Math.Max(hub - t, hub * 0.25));

        // Cuatro aspas huecas, barridas hacia adelante (sentido de giro).
        double dIn = AngleOf(t, rIn);
        double dOut = AngleOf(t, rOut);
        for (int k = 0; k < 4; k++)
        {
            double mid = 45 + k * 90;
            double a0 = mid - BladeHalf, a1 = mid + BladeHalf;

            // Contorno exterior del aspa.
            AddBlade(geo, cx, cy, rIn, rOut,
                     a0, a1, a0 + BladeSweep, a1 + BladeSweep);

            // Contorno interior: retirado hacia adentro del aspa (radial y
            // angularmente) para que el EvenOdd deje el hueco del reborde.
            AddBlade(geo, cx, cy, rIn + t, rOut - t,
                     a0 + dIn, a1 - dIn,
                     a0 + BladeSweep + dOut, a1 + BladeSweep - dOut);
        }

        return geo;
    }

    /// <summary>Anillo: circulo exterior + interior (el EvenOdd recorta el hueco).</summary>
    private static void AddRing(PathGeometry geo, double cx, double cy, double outer, double inner)
    {
        AddCircle(geo, cx, cy, outer);
        if (inner > 0.35) AddCircle(geo, cx, cy, inner);
    }

    private static void AddCircle(PathGeometry geo, double cx, double cy, double r) =>
        geo.Figures.Add(new PathFigure
        {
            StartPoint = new Point(cx + r, cy),
            IsClosed = true,
            IsFilled = true,
            Segments =
            {
                Arc(new Point(cx - r, cy), r, SweepDirection.Clockwise),
                Arc(new Point(cx + r, cy), r, SweepDirection.Clockwise)
            }
        });

    /// <summary>
    /// Un aspa entre los radios rIn..rOut: la raiz ocupa el arco a0..a1 y la punta
    /// el arco b0..b1 (mas el barrido). Los bordes laterales son curvas, para que
    /// el aspa se vea barrida y no un trapecio.
    /// </summary>
    private static void AddBlade(PathGeometry geo, double cx, double cy,
        double rIn, double rOut, double a0, double a1, double b0, double b1)
    {
        if (rOut - rIn <= 0.4 || a1 - a0 <= 0.6 || b1 - b0 <= 0.6) return;

        double midR = (rIn + rOut) / 2;
        var fig = new PathFigure
        {
            StartPoint = Polar(cx, cy, rIn, a0),
            IsClosed = true,
            IsFilled = true
        };

        // Raiz junto al buje (de a0 hacia a1: sentido horario = angulo creciente).
        fig.Segments.Add(Arc(Polar(cx, cy, rIn, a1), rIn, SweepDirection.Clockwise));

        // Borde delantero, barrido hacia adelante.
        fig.Segments.Add(new BezierSegment
        {
            Point1 = Polar(cx, cy, midR, Lerp(a1, b1, 0.35)),
            Point2 = Polar(cx, cy, midR, Lerp(a1, b1, 0.75)),
            Point3 = Polar(cx, cy, rOut, b1)
        });

        // Punta (de b1 hacia b0: angulo decreciente = antihorario).
        fig.Segments.Add(Arc(Polar(cx, cy, rOut, b0), rOut, SweepDirection.Counterclockwise));

        // Borde trasero, de vuelta a la raiz.
        fig.Segments.Add(new BezierSegment
        {
            Point1 = Polar(cx, cy, midR, Lerp(b0, a0, 0.35)),
            Point2 = Polar(cx, cy, midR, Lerp(b0, a0, 0.75)),
            Point3 = Polar(cx, cy, rIn, a0)
        });

        geo.Figures.Add(fig);
    }

    private static ArcSegment Arc(Point to, double r, SweepDirection dir) => new()
    {
        Point = to,
        Size = new Size(r, r),
        SweepDirection = dir
    };

    private static Point Polar(double cx, double cy, double r, double degrees)
    {
        double a = degrees * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }

    private static double Lerp(double from, double to, double f) => from + (to - from) * f;

    /// <summary>
    /// Grados que hay que retirar a cada lado para que un contorno quede separado
    /// del otro por el grosor de linea a la distancia r. Se usa para que el reborde
    /// tambien tenga grosor constante en los bordes laterales del aspa, no solo en
    /// la raiz y la punta.
    /// </summary>
    private static double AngleOf(double lineWidth, double r) =>
        r <= 0.01 ? 90 : lineWidth / (2 * r) * 180.0 / Math.PI;

    /// <summary>
    /// Color del icono: el mismo texto PRINCIPAL del tema que heredan el titulo de
    /// la card y los iconos vecinos del navbar (blanco en tema oscuro, casi negro
    /// en claro). Antes usaba SecondaryTextBrush (#9FA0A2) y el ventilador se veia
    /// gris al lado del titulo. Si la clave no estuviera disponible cae al
    /// secundario: nunca a transparente, que dejaria el icono invisible.
    ///
    /// La clave es PrimaryTextBrush, de los ThemeDictionaries PROPIOS de la app, y
    /// NO el TextFillColorPrimaryBrush de WinUI. Motivo: las claves del sistema no
    /// existen en nuestros diccionarios, asi que ThemeBrushes caia al lookup de
    /// Application.Resources — que resuelve con el tema del SISTEMA. Con Windows en
    /// oscuro y la app en claro, el pincel salia BLANCO y el ventilador desaparecia
    /// sobre el fondo claro (el navbar se veia sin el icono de ventiladores).
    /// </summary>
    public static SolidColorBrush IconBrush()
    {
        var primary = ThemeBrushes.Get("PrimaryTextBrush");
        return primary.Color.A == 0 ? ThemeBrushes.Get("SecondaryTextBrush") : primary;
    }

    /// <summary>Icono que sigue el tema, para el titulo de la pagina y las cards.
    /// Stretch=None a proposito: la geometria ya viene en pixeles del tamano final
    /// y centrada, y con Uniform WinUI la estiraria hasta el borde del cuadro
    /// comiendose el margen calculado.</summary>
    public static Viewbox CreateCanvas(double size, SolidColorBrush? brush = null) => new()
    {
        Width = size,
        Height = size,
        Child = new XamlPath
        {
            Data = BuildGeometry(size),
            Stretch = Stretch.None,
            Width = size,
            Height = size,
            Fill = brush ?? IconBrush(),
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    /// <summary>Icono para navbar/menus (IconElement): mismo fan de linea, con la
    /// geometria generada a medida del tamano pedido (nada recortado).
    ///
    /// SIN pincel explicito NO se setea Foreground: asi hereda el mismo que sus vecinos
    /// del navbar (los FontIcon de las otras pestañas, que tampoco lo setean), y ademas
    /// acompania los estados del item —seleccionado, hover— como cualquier otro icono.
    /// Pintarlo con un pincel nuestro lo dejaba fuera de esos estados y atado a un color
    /// que no dependia del tema de la app.</summary>
    public static PathIcon CreateIcon(double size, SolidColorBrush? brush = null)
    {
        var icon = new PathIcon
        {
            Width = size,
            Height = size,
            Data = BuildGeometry(size)
        };
        if (brush is not null) icon.Foreground = brush;
        return icon;
    }

    /// <summary>Compatibilidad con WorkshopPage (cards del catalogo), donde los
    /// iconos hermanos van con el color de acento.</summary>
    public static PathIcon CreatePathIcon(double size, SolidColorBrush? brush = null) =>
        CreateIcon(size, brush);
}
