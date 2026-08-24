using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace WHPO_UI.Controls;

/// <summary>
/// Velocímetro de progreso estilo tablero de auto: arco de 270° que va de
/// abajo-izquierda a abajo-derecha, con marcas de escala, una aguja que gira
/// según el avance y el porcentaje completado en el centro-inferior del dial.
/// Construido 100% en código C# (igual que RingGauge: este proyecto compila
/// XAML sin generador de fuentes parciales).
/// API compatible con los usos previos de RingGauge en LimpiezaPage:
/// <see cref="Progress"/> (0.0 a 1.0), <see cref="Value"/> ("42%"),
/// <see cref="Label"/> y <see cref="ConfigureSize"/>.
/// </summary>
public sealed class SpeedometerGauge : Grid
{
    // Geometría del dial: el arco arranca abajo a la izquierda (135°) y barre
    // 270° en sentido horario hasta abajo a la derecha (45°).
    private const double StartAngleDeg = 135.0;
    private const double SweepAngleDeg = 270.0;

    // Marcas de escala: 21 posiciones (cada 13.5°); cada 5ta es mayor,
    // quedando las mayores justo en 0%, 25%, 50%, 75% y 100%.
    private const int TickCount = 21;
    private const int MajorTickEvery = 5;

    // Dimensiones del dial; se ajustan según ConfigureSize().
    private double _size = 140;   // diámetro total de la pista
    private double _radius = 65;  // radio medio del arco
    private double _stroke = 10;  // grosor del arco

    private static readonly Brush TrackBrushDefault = new SolidColorBrush(Color.FromArgb(255, 60, 60, 60));
    private static readonly Brush ProgressBrushDefault = new SolidColorBrush(Color.FromArgb(255, 138, 180, 248));
    private static readonly Brush NeedleBrushDefault = new SolidColorBrush(Color.FromArgb(255, 235, 90, 90));
    private static readonly Brush TickBrushDefault = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));
    private static readonly Brush LabelBrushDefault = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));

    private readonly Microsoft.UI.Xaml.Shapes.Path _trackPath;
    private readonly Microsoft.UI.Xaml.Shapes.Path _progressPath;
    private readonly Canvas _dialCanvas;   // marcas de escala + aguja + cubo central
    private readonly Line _needle;
    private readonly Ellipse _hub;
    private readonly TextBlock _valueText;
    private readonly TextBlock _labelText;
    private double _progress;

    public SpeedometerGauge()
    {
        Width = 160;
        Height = 160;

        var root = new Grid();

        // Arco de fondo (track completo del dial)
        _trackPath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = _size,
            Height = _size,
            Stroke = TrackBrushDefault,
            StrokeThickness = _stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Arco de progreso (se llena según Progress)
        _progressPath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = _size,
            Height = _size,
            Stroke = ProgressBrushDefault,
            StrokeThickness = _stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Lienzo con marcas de escala, aguja y cubo central (coordenadas absolutas)
        _dialCanvas = new Canvas
        {
            Width = _size,
            Height = _size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _needle = new Line
        {
            Stroke = NeedleBrushDefault,
            StrokeThickness = Math.Max(2.5, _stroke * 0.28),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        double hubSize = Math.Max(12, _stroke * 1.5);
        _hub = new Ellipse
        {
            Width = hubSize,
            Height = hubSize,
            Fill = new SolidColorBrush(Color.FromArgb(255, 70, 70, 70))
        };

        _dialCanvas.Children.Add(_needle);
        _dialCanvas.Children.Add(_hub);

        // Texto central-inferior: % completado + etiqueta opcional
        var textPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0,
            Margin = new Thickness(0, 46, 0, 0)
        };

        _valueText = new TextBlock
        {
            Text = "0%",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _labelText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = LabelBrushDefault,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        textPanel.Children.Add(_valueText);
        textPanel.Children.Add(_labelText);

        root.Children.Add(_trackPath);
        root.Children.Add(_progressPath);
        root.Children.Add(_dialCanvas);
        root.Children.Add(textPanel);

        Children.Add(root);

        // Estado inicial: dial vacío, aguja en reposo (0%)
        RebuildTicks();
        ApplyProgress(0);
    }

    /// <summary>
    /// Valor mostrado bajo la aguja (por defecto "0%").
    /// </summary>
    public string Value
    {
        get => _valueText.Text;
        set => _valueText.Text = value;
    }

    /// <summary>
    /// Etiqueta opcional debajo del porcentaje.
    /// </summary>
    public string Label
    {
        get => _labelText.Text;
        set => _labelText.Text = value;
    }

    /// <summary>
    /// Progreso del velocímetro (0.0 a 1.0): llena el arco y gira la aguja.
    /// </summary>
    public double Progress
    {
        get => _progress;
        set => ApplyProgress(value);
    }

    /// <summary>
    /// Color del arco de progreso.
    /// </summary>
    public Brush ProgressBrush
    {
        get => _progressPath.Stroke;
        set => _progressPath.Stroke = value;
    }

    /// <summary>
    /// Color del arco de fondo (track).
    /// </summary>
    public Brush TrackBrush
    {
        get => _trackPath.Stroke;
        set => _trackPath.Stroke = value;
    }

    /// <summary>
    /// Ajusta el tamaño TOTAL del control reescalando pista, arco, marcas,
    /// aguja y tipografías. Sin llamarlo queda el tamaño por defecto (160),
    /// igual que en los usos existentes.
    /// </summary>
    public void ConfigureSize(double controlSize)
    {
        _stroke = Math.Max(7.0, controlSize * 0.075);
        _size = controlSize - _stroke - 10;
        _radius = _size / 2 - _stroke / 2;

        Width = Height = controlSize;
        _trackPath.Width = _trackPath.Height = _size;
        _trackPath.StrokeThickness = _stroke;
        _progressPath.Width = _progressPath.Height = _size;
        _progressPath.StrokeThickness = _stroke;
        _dialCanvas.Width = _dialCanvas.Height = _size;

        _needle.StrokeThickness = Math.Max(2.5, _stroke * 0.28);

        double hubSize = Math.Max(12, _stroke * 1.5);
        _hub.Width = _hub.Height = hubSize;

        _valueText.FontSize = Math.Max(14, controlSize * 0.16);
        _labelText.FontSize = Math.Max(9, controlSize * 0.07);

        RebuildTicks();
        ApplyProgress(_progress);
    }

    /// <summary>
    /// Aplica el progreso: recalcula arco, posición de la aguja y estado interno.
    /// </summary>
    private void ApplyProgress(double value)
    {
        _progress = Math.Clamp(value, 0.0, 1.0);
        UpdateArc(_progress);
        UpdateNeedle(_progress);
    }

    /// <summary>
    /// Dibuja las marcas de escala sobre el dial (las mayores más largas).
    /// </summary>
    private void RebuildTicks()
    {
        // Conservar aguja y cubo: solo se regeneran las líneas de escala.
        for (int i = _dialCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (_dialCanvas.Children[i] is Line) _dialCanvas.Children.RemoveAt(i);
        }

        double tickOuter = _radius - _stroke / 2 - 3;
        double minorLen = Math.Max(4, _radius * 0.09);
        double majorLen = Math.Max(7, _radius * 0.16);

        for (int i = 0; i < TickCount; i++)
        {
            bool major = i % MajorTickEvery == 0;
            double len = major ? majorLen : minorLen;
            double angleRad = (StartAngleDeg + SweepAngleDeg * i / (TickCount - 1)) * Math.PI / 180.0;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);
            double cx = _size / 2;
            double cy = _size / 2;

            var line = new Line
            {
                X1 = cx + tickOuter * cos,
                Y1 = cy + tickOuter * sin,
                X2 = cx + (tickOuter - len) * cos,
                Y2 = cy + (tickOuter - len) * sin,
                Stroke = TickBrushDefault,
                StrokeThickness = major ? Math.Max(1.5, _stroke * 0.22) : Math.Max(1.0, _stroke * 0.13),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _dialCanvas.Children.Add(line);
        }

        // Reposicionar el cubo central (su tamaño pudo haber cambiado)
        double hubLeft = _size / 2 - _hub.Width / 2;
        double hubTop = _size / 2 - _hub.Height / 2;
        Canvas.SetLeft(_hub, hubLeft);
        Canvas.SetTop(_hub, hubTop);
    }

    /// <summary>
    /// Actualiza el arco de progreso según la fracción indicada.
    /// </summary>
    private void UpdateArc(double progress)
    {
        if (progress <= 0)
        {
            _progressPath.Data = null;
            return;
        }

        double endAngle = StartAngleDeg + SweepAngleDeg * progress;
        _progressPath.Data = BuildArcGeometry(StartAngleDeg, endAngle);
    }

    /// <summary>
    /// Gira la aguja hacia el ángulo correspondiente al progreso.
    /// La aguja tiene una cola corta detrás del centro para dar efecto de balanza.
    /// </summary>
    private void UpdateNeedle(double progress)
    {
        double angleRad = (StartAngleDeg + SweepAngleDeg * progress) * Math.PI / 180.0;
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);
        double cx = _size / 2;
        double cy = _size / 2;

        double tipLen = _radius - _stroke * 1.5;
        double tailLen = Math.Max(6, _radius * 0.14);

        _needle.X1 = cx - tailLen * cos;
        _needle.Y1 = cy - tailLen * sin;
        _needle.X2 = cx + tipLen * cos;
        _needle.Y2 = cy + tipLen * sin;
    }

    /// <summary>
    /// Construye el arco entre dos ángulos (grados, sentido horario).
    /// </summary>
    private Geometry BuildArcGeometry(double startAngleDeg, double endAngleDeg)
    {
        Point start = PointOnDial(startAngleDeg);
        Point end = PointOnDial(endAngleDeg);

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false
        };

        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(_radius, _radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = (endAngleDeg - startAngleDeg) > 180.0
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>
    /// Punto sobre el círculo del dial para un ángulo dado en grados
    /// (0° = derecha, positivo = sentido horario en pantalla).
    /// </summary>
    private Point PointOnDial(double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        double cx = _size / 2;
        double cy = _size / 2;
        return new Point(cx + _radius * Math.Cos(radians), cy + _radius * Math.Sin(radians));
    }
}
