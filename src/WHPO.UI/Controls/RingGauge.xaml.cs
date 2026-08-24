using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace WHPO_UI.Controls;

/// <summary>
/// Anillo circular con progreso (gauge). Construido 100% en código C#.
/// Tiene dos modos:
///  - Modo arco clásico: Progress (0.0 a 1.0) dibuja un arco alrededor del círculo.
///  - Modo casillas: ConfigureCells(n) arma el anillo con casillas (una por paquete).
///    Cada casilla representa un paquete y su color depende del estado del paquete:
///    verde si se envió/recibió correctamente y rojo si llegó tarde o falló.
/// </summary>
public sealed class RingGauge : Grid
{
    /// <summary>Estados posibles de una casilla (paquete). Mayor valor = peor estado.</summary>
    public enum PacketCellState
    {
        Pending = 0, // Aún no enviado
        Sent = 1,    // Enviado correctamente
        Ok = 2,      // Recibido correctamente
        Slow = 3,    // Llegó tarde (latencia alta / late) → rojo
        Lost = 4     // Falló / perdido (timeout) → rojo
    }

    // Dimensiones del anillo; se ajustan según la cantidad de casillas (1 casilla = 1 paquete)
    private double _size = 140;
    private double _radius = 65;
    private double _stroke = 10;

    // Las casillas pendientes (paquetes aún no procesados) son invisibles: solo aparecen
    // las casillas de paquetes realmente enviados/recibidos, así nunca quedan casillas
    // "vacías" que no cambian de color.
    private static readonly Brush PendingCellBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    private static readonly Brush OkCellBrush = new SolidColorBrush(Color.FromArgb(255, 106, 200, 133));
    private static readonly Brush LostCellBrush = new SolidColorBrush(Color.FromArgb(255, 255, 100, 100));

    private readonly Microsoft.UI.Xaml.Shapes.Path _progressPath;
    private readonly Ellipse _trackEllipse;
    private readonly Canvas _cellCanvas;
    private readonly TextBlock _valueText;
    private readonly TextBlock _labelText;
    private double _progress;

    private Rectangle[] _cells = Array.Empty<Rectangle>();
    private int[] _cellStates = Array.Empty<int>();
    private int _cellCount;
    private bool _cellsActive;

    public RingGauge()
    {
        Width = 160;
        Height = 160;

        var root = new Grid();

        // Anillo de fondo
        _trackEllipse = new Ellipse
        {
            Width = _size,
            Height = _size,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 60, 60, 60)),
            StrokeThickness = _stroke,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Anillo de progreso (arco, usado en modo clásico)
        _progressPath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = _size,
            Height = _size,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 138, 180, 248)),
            StrokeThickness = _stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Lienzo donde se dibujan las casillas (modo casillas)
        _cellCanvas = new Canvas
        {
            Width = _size,
            Height = _size,
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

        root.Children.Add(_trackEllipse);
        root.Children.Add(_progressPath);
        root.Children.Add(_cellCanvas);
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
    /// Progreso del anillo (0.0 a 1.0). Solo aplica en modo arco; si el anillo
    /// tiene casillas configuradas, el progreso se muestra por casillas.
    /// </summary>
    public double Progress
    {
        get => _progress;
        set
        {
            _progress = Math.Clamp(value, 0.0, 1.0);
            if (_cellsActive) return;
            UpdateArc(_progress);
        }
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
    /// Color del anillo de fondo (track).
    /// </summary>
    public Brush TrackBrush
    {
        get => _trackEllipse.Stroke;
        set => _trackEllipse.Stroke = value;
    }

    /// <summary>
    /// Ajusta el diámetro TOTAL del control reescalando pista, arco, casillas y
    /// tipografías. Sin llamarlo queda el tamaño por defecto (160), igual que en
    /// los usos existentes.
    /// </summary>
    public void ConfigureSize(double controlSize)
    {
        _stroke = Math.Max(7.0, controlSize * 0.075);
        _size = controlSize - _stroke - 10;
        _radius = _size / 2 - _stroke / 2;

        Width = Height = controlSize;
        _trackEllipse.Width = _trackEllipse.Height = _size;
        _trackEllipse.StrokeThickness = _stroke;
        _progressPath.Width = _progressPath.Height = _size;
        _progressPath.StrokeThickness = _stroke;
        _cellCanvas.Width = _cellCanvas.Height = _size;

        _valueText.FontSize = Math.Max(13, controlSize * 0.16);
        _labelText.FontSize = Math.Max(9, controlSize * 0.075);

        if (_cellsActive)
            RebuildCells();
        else
            UpdateArc(_progress);
    }

    /// <summary>
    /// Dibuja marcas de escala fijas alrededor del anillo (estilo velocímetro/reloj).
    /// Las marcas son finitas (rayitas redondeadas), NO se rellenan: el progreso sigue siendo
    /// el arco continuo de <see cref="Progress"/> (se llena como un cronómetro).
    /// </summary>
    public void ConfigureTicks(int tickCount)
    {
        _cellsActive = false;
        _progressPath.Visibility = Visibility.Visible;
        _cellCanvas.Children.Clear();

        tickCount = Math.Max(4, tickCount);
        double innerR = _radius - _stroke / 2 + 1;
        double outerR = _radius + _stroke / 2 - 1;
        double cx = _size / 2, cy = _size / 2;

        for (int i = 0; i < tickCount; i++)
        {
            double deg = -90.0 + 360.0 * i / tickCount;
            double rad = deg * Math.PI / 180.0;

            var pOuter = new Point(cx + outerR * Math.Cos(rad), cy + outerR * Math.Sin(rad));
            var pInner = new Point(cx + innerR * Math.Cos(rad), cy + innerR * Math.Sin(rad));

            var line = new Line
            {
                X1 = pOuter.X, Y1 = pOuter.Y,
                X2 = pInner.X, Y2 = pInner.Y,
                Stroke = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
                StrokeThickness = Math.Max(1.2, (_stroke * 0.18)),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _cellCanvas.Children.Add(line);
        }
    }

    /// <summary>
    /// Arma el anillo de casillas para un test de <paramref name="packetCount"/> paquetes.
    /// Cada casilla corresponde EXACTAMENTE a un paquete (sin agrupar). El anillo mantiene
    /// su tamaño fijo: con muchos paquetes las casillas se achican (puede perderse la
    /// distinción visual entre una y otra), pero nunca se superponen. Las casillas de
    /// paquetes aún no procesados son invisibles; solo se ven las de paquetes enviados
    /// o recibidos.
    /// </summary>
    public void ConfigureCells(int packetCount)
    {
        _cellCount = Math.Max(1, packetCount);
        _cellsActive = true;
        _progressPath.Visibility = Visibility.Collapsed;
        RebuildCells();
    }

    private void RebuildCells()
    {
        _cellCanvas.Children.Clear();
        _cells = new Rectangle[_cellCount];
        _cellStates = new int[_cellCount];

        double pitch = 2 * Math.PI * _radius / _cellCount;
        // La casilla ocupa como máximo el 72% del espacio entre casillas (pitch),
        // así nunca se superponen por más paquetes que haya.
        double cellSize = Math.Min(6.0, pitch * 0.72);

        for (int i = 0; i < _cellCount; i++)
        {
            // Empezar arriba (12 en punto) y avanzar en sentido horario
            double angleDeg = -90.0 + 360.0 * (i + 0.5) / _cellCount;
            double radians = angleDeg * Math.PI / 180.0;
            double cx = _size / 2 + _radius * Math.Cos(radians);
            double cy = _size / 2 + _radius * Math.Sin(radians);

            var cell = new Rectangle
            {
                Width = cellSize,
                Height = cellSize,
                RadiusX = cellSize * 0.25,
                RadiusY = cellSize * 0.25,
                Fill = PendingCellBrush
            };
            Canvas.SetLeft(cell, cx - cellSize / 2);
            Canvas.SetTop(cell, cy - cellSize / 2);

            _cells[i] = cell;
            _cellCanvas.Children.Add(cell);
        }
    }

    /// <summary>
    /// Vuelve todas las casillas al estado pendiente (inicio del test).
    /// </summary>
    public void ResetCells()
    {
        if (!_cellsActive) return;
        Array.Clear(_cellStates, 0, _cellStates.Length);
        foreach (var cell in _cells)
        {
            cell.Fill = PendingCellBrush;
        }
    }

    /// <summary>
    /// Marca el estado del paquete <paramref name="packetIndex"/> (0-based).
    /// Cada casilla corresponde a un único paquete.
    /// </summary>
    public void SetPacketState(int packetIndex, PacketCellState state)
    {
        if (!_cellsActive || _cells.Length == 0) return;

        int cellIndex = Math.Clamp(packetIndex, 0, _cellCount - 1);
        if ((int)state > _cellStates[cellIndex])
        {
            _cellStates[cellIndex] = (int)state;
            _cells[cellIndex].Fill = BrushForState(state);
        }
    }

    // Verde si se envió/recibió correctamente; rojo si llegó tarde o falló
    private static Brush BrushForState(PacketCellState state) => state switch
    {
        PacketCellState.Sent or PacketCellState.Ok => OkCellBrush,
        PacketCellState.Slow or PacketCellState.Lost => LostCellBrush,
        _ => PendingCellBrush
    };

    /// <summary>
    /// Modo casillas: enciende proporcionalmente las casillas según
    /// <paramref name="fraction"/> (0.0 a 1.0), estilo velocímetro. Las casillas
    /// no alcanzadas vuelven a invisible, así el indicador también retrocede si
    /// hace falta.
    /// </summary>
    public void SetCellsProgress(double fraction)
    {
        if (!_cellsActive || _cells.Length == 0) return;

        fraction = Math.Clamp(fraction, 0.0, 1.0);
        int lit = (int)Math.Round(fraction * _cellCount);

        for (int i = 0; i < _cellCount; i++)
        {
            var target = i < lit ? PacketCellState.Ok : PacketCellState.Pending;
            if ((int)target != _cellStates[i])
            {
                _cellStates[i] = (int)target;
                _cells[i].Fill = BrushForState(target);
            }
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
                Center = new Point(_size / 2, _size / 2),
                RadiusX = _radius,
                RadiusY = _radius
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
            Size = new Size(_radius, _radius),
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
        double cx = _size / 2;
        double cy = _size / 2;
        double x = cx + _radius * Math.Cos(radians);
        double y = cy + _radius * Math.Sin(radians);
        return new Point(x, y);
    }
}