using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace WHPO_UI.Controls;

/// <summary>
/// Anillo circular con progreso animado (gauge). Construido 100% en código C#.
/// </summary>
public sealed class RingGauge : Grid
{
    private const double Radius = 55; // (120 - StrokeThickness) / 2
    private const double GaugeStrokeThickness = 10;
    private const double Size = 120;

    private readonly Microsoft.UI.Xaml.Shapes.Path _progressPath;
    private readonly TextBlock _valueText;
    private readonly TextBlock _labelText;
    private double _progress;

    public RingGauge()
    {
        Width = 140;
        Height = 140;

        var root = new Grid();

        // Anillo de fondo
        var trackEllipse = new Ellipse
        {
            Width = Size,
            Height = Size,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 60, 60, 60)),
            StrokeThickness = GaugeStrokeThickness,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Anillo de progreso
        _progressPath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = Size,
            Height = Size,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 138, 180, 248)),
            StrokeThickness = GaugeStrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Texto central
        var textPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 0
        };

        _valueText = new TextBlock
        {
            Text = "0",
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _labelText = new TextBlock
        {
            Text = "Label",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        textPanel.Children.Add(_valueText);
        textPanel.Children.Add(_labelText);

        root.Children.Add(trackEllipse);
        root.Children.Add(_progressPath);
        root.Children.Add(textPanel);

        Children.Add(root);

        // Estado inicial
        _progressPath.Data = null;
        _valueText.Text = "0";
        _labelText.Text = "Label";
    }

    /// <summary>
    /// Valor actual mostrado en el centro.
    /// </summary>
    public string Value
    {
        get => _valueText.Text;
        set => _valueText.Text = value;
    }

    /// <summary>
    /// Etiqueta debajo del valor.
    /// </summary>
    public string Label
    {
        get => _labelText.Text;
        set => _labelText.Text = value;
    }

    /// <summary>
    /// Progreso del anillo (0.0 a 1.0).
    /// </summary>
    public double Progress
    {
        get => _progress;
        set
        {
            _progress = Math.Clamp(value, 0.0, 1.0);
            UpdateArc(_progress);
        }
    }

    private void UpdateArc(double progress)
    {
        // Ángulo del arco: 360° * progress, empezando desde arriba (12 en punto)
        double angle = 360.0 * progress;
        double startAngle = -90.0; // arriba

        // Si el progreso es 0, no dibujar arco
        if (progress <= 0)
        {
            _progressPath.Data = null;
            return;
        }

        // Si el progreso es completo, dibujar círculo completo
        if (progress >= 1.0)
        {
            var fullEllipse = new EllipseGeometry
            {
                Center = new Point(Size / 2, Size / 2),
                RadiusX = Radius,
                RadiusY = Radius
            };
            _progressPath.Data = fullEllipse;
            return;
        }

        // Punto inicial (siempre arriba)
        Point start = PointOnCircle(startAngle);

        // Punto final según el progreso
        double endAngle = startAngle + angle;
        Point end = PointOnCircle(endAngle);

        // Determinar si el arco es mayor a 180° (necesita LargeArc)
        bool isLargeArc = angle > 180.0;

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false,
            IsFilled = false
        };

        var arc = new ArcSegment
        {
            Point = end,
            Size = new Size(Radius, Radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = isLargeArc
        };

        figure.Segments.Add(arc);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _progressPath.Data = geometry;
    }

    private Point PointOnCircle(double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        double cx = Size / 2;
        double cy = Size / 2;
        double x = cx + Radius * Math.Cos(radians);
        double y = cy + Radius * Math.Sin(radians);
        return new Point(x, y);
    }
}