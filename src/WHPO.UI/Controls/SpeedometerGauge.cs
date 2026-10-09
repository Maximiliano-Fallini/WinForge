using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
// Alias: con los usings implícitos del SDK, "Path" choca con System.IO.Path.
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace WHPO_UI.Controls;

/// <summary>
/// Velocímetro de progreso: el MISMO dial que la pantalla de arranque
/// (<see cref="WHPO_UI.SplashWindow"/>) como control reutilizable, para que los
/// loadings de las páginas —los análisis de Limpieza del dispositivo— se vean
/// igual que la carga de la app.
///
/// Es el dial de 240° del splash: 0 % abajo a la izquierda, 100 % abajo a la
/// derecha, marcas cada 10 %, aguja con cola, porcentaje en el centro y los
/// colores del tema activo. La cuenta de ángulos vive en <see cref="SplashGauge"/>,
/// compartida con el splash: la aguja y el arco no pueden desincronizarse.
///
/// El dibujo se arma sobre un lienzo lógico de 300×300 —las medidas del splash—
/// y <see cref="ConfigureSize"/> lo reescala completo (trazos, marcas, aguja y
/// tipografías incluidos): al tamaño por defecto el velocímetro es el del
/// arranque, y a cualquier otro tamaño conserva las proporciones. El % del
/// centro sale de <see cref="Progress"/>: no hay que escribirlo a mano.
///
/// Los colores SIEMPRE salen del tema activo (<see cref="ThemeBrushes"/>): son
/// pinceles live y se repintan solos al cambiar de tema; acá no hay colores
/// fijos. Construido 100 % en código C#, igual que el resto de los controles de
/// este proyecto (el XAML se compila sin generador de fuentes parciales).
/// </summary>
public sealed class SpeedometerGauge : Grid
{
    // ===== Geometría del dial (las medidas del splash, sobre un lienzo de 300) =====
    private const double DialSize = 300;
    private const double DialCenter = DialSize / 2;
    private const double TrackRadius = 118;
    private const double TrackStroke = 9;
    private const double TickInnerRadius = 96;
    private const double TickOuterRadius = 105;
    private const double NeedleLength = 92;
    private const double NeedleHalfWidth = 7;
    private const double NeedleTail = 16;
    private const double HubOuterSize = 22;
    private const double HubInnerSize = 8;
    private const double PercentTop = 178;
    private const double PercentFontSize = 36;

    /// <summary>Una marca cada 10 % (11 en total); la de cada 5ª posición
    /// —0 %, 50 % y 100 %— va más gruesa, como en el splash.</summary>
    private const int TickCount = 11;
    private const int MajorTickEvery = 5;

    /// <summary>Tamaño por defecto: el dial completo del arranque.</summary>
    public const double DefaultSize = DialSize;

    /// <summary>Piso de tamaño: por debajo el dial deja de ser legible.</summary>
    private const double MinSize = 90;

    // ===== Animación de la aguja (mismos tiempos que el splash) =====
    private const double NeedleAnimationMaxMs = 600;
    private const double NeedleAnimationMinMs = 180;

    /// <summary>Medio ciclo del barrido del modo indeterminado.</summary>
    private const double IndeterminateCycleSeconds = 1.4;

    private readonly Canvas _canvas;
    private readonly Canvas _tickLayer;
    private readonly XamlPath _trackArc;
    private readonly XamlPath _progressArc;
    private readonly Polygon _needle;
    private readonly RotateTransform _needleRotate;
    private readonly Ellipse _hubOuter;
    private readonly Ellipse _hubInner;
    private readonly TextBlock _percentText;
    private readonly ScaleTransform _scale;

    /// <summary>Progreso real (0.0 a 1.0): el que se ve al salir del modo indeterminado.</summary>
    private double _progress;

    /// <summary>Barrido continuo en curso: lo prende <see cref="IsIndeterminate"/>.</summary>
    private bool _indeterminate;

    /// <summary>Animación de la aguja en curso: se mantiene referenciada mientras
    /// corre (un Storyboard sin referencia viva puede terminar antes de tiempo).</summary>
    private Storyboard? _needleAnimation;

    /// <summary>Barrido del modo indeterminado (bucle infinito).</summary>
    private Storyboard? _indeterminateAnimation;

    /// <summary>Ángulo al que apunta la animación de la aguja: la duración de cada
    /// tramo se calcula contra este valor y no contra el ángulo dibujado, que va
    /// atrás persiguiendo la animación.</summary>
    private double _needleTargetAngle = SplashGauge.AngleForPercent(0);

    public SpeedometerGauge()
    {
        Width = Height = DefaultSize;

        // Lienzo lógico de 300×300 anclado arriba a la izquierda: el
        // ConfigureSize lo reescala desde ahí (origen 0,0), así el dial ocupa
        // exactamente el tamaño pedido sin descuadres por el centrado.
        _canvas = new Canvas
        {
            Width = DialSize,
            Height = DialSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        _scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        _canvas.RenderTransform = _scale;
        _canvas.RenderTransformOrigin = new Point(0, 0);

        // Las marcas van primero (y en su propia capa) para que la aguja pase por
        // encima de ellas.
        _tickLayer = new Canvas { Width = DialSize, Height = DialSize };

        _trackArc = NewArc();
        _progressArc = NewArc();

        _needle = new Polygon();
        _needleRotate = new RotateTransform { CenterX = DialCenter, CenterY = DialCenter };
        _needle.RenderTransform = _needleRotate;

        _hubOuter = new Ellipse { Width = HubOuterSize, Height = HubOuterSize };
        _hubInner = new Ellipse { Width = HubInnerSize, Height = HubInnerSize };
        Canvas.SetLeft(_hubOuter, DialCenter - HubOuterSize / 2);
        Canvas.SetTop(_hubOuter, DialCenter - HubOuterSize / 2);
        Canvas.SetLeft(_hubInner, DialCenter - HubInnerSize / 2);
        Canvas.SetTop(_hubInner, DialCenter - HubInnerSize / 2);

        _percentText = new TextBlock
        {
            Width = DialSize,
            Text = "0%",
            FontSize = PercentFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetLeft(_percentText, 0);
        Canvas.SetTop(_percentText, PercentTop);

        _canvas.Children.Add(_tickLayer);
        _canvas.Children.Add(_trackArc);
        _canvas.Children.Add(_progressArc);
        _canvas.Children.Add(_needle);
        _canvas.Children.Add(_hubOuter);
        _canvas.Children.Add(_hubInner);
        _canvas.Children.Add(_percentText);

        Children.Add(_canvas);

        BuildDial();
        ApplyTheme();
        ApplyProgress(0, animateNeedle: false);
    }

    /// <summary>
    /// Progreso (0.0 a 1.0): llena el arco y actualiza el porcentaje del centro
    /// al instante, y lleva la aguja hasta ahí con la animación propia — el
    /// mismo movimiento suave del arranque, aunque los pasos lleguen a saltos.
    /// </summary>
    public double Progress
    {
        get => _progress;
        set => ApplyProgress(value, animateNeedle: true);
    }

    /// <summary>
    /// Modo indeterminado: la aguja barre el dial de punta a punta en bucle y el
    /// porcentaje queda en "…" (no hay avance que mostrar). Al apagarlo vuelve al
    /// progreso real, sin animación.
    /// </summary>
    public bool IsIndeterminate
    {
        get => _indeterminate;
        set
        {
            if (_indeterminate == value) return;
            _indeterminate = value;
            if (value) StartIndeterminate();
            else StopIndeterminate();
        }
    }

    /// <summary>
    /// Ajusta el tamaño TOTAL del control reescalando el dial completo (pista,
    /// arco, marcas, aguja, cubo y tipografías) con un solo factor. Sin llamarlo
    /// queda el tamaño por defecto: el mismo de la pantalla de arranque.
    /// </summary>
    public void ConfigureSize(double controlSize)
    {
        double size = Math.Max(MinSize, controlSize);
        Width = Height = size;
        _scale.ScaleX = _scale.ScaleY = size / DialSize;
    }

    /// <summary>
    /// Aplica el progreso al dial. La aguja se mueve animada o se coloca directo
    /// según <paramref name="animateNeedle"/>.
    /// </summary>
    private void ApplyProgress(double value, bool animateNeedle)
    {
        _progress = Math.Clamp(double.IsNaN(value) ? 0.0 : value, 0.0, 1.0);

        // Modo indeterminado: manda el barrido. El progreso igual queda guardado
        // para cuando se lo apague.
        if (_indeterminate) return;

        double percent = _progress * 100.0;
        _progressArc.Data = BuildArcGeometry(0, percent);
        _percentText.Text = $"{(int)Math.Round(percent)}%";

        if (animateNeedle) AnimateNeedleTo(_progress);
        else SetNeedle(_progress);
    }

    /// <summary>Coloca la aguja en el ángulo del progreso, sin animación.</summary>
    private void SetNeedle(double progress)
    {
        double angle = SplashGauge.AngleForPercent(progress * 100.0);
        _needleTargetAngle = angle;
        _needleRotate.Angle = angle;
    }

    /// <summary>
    /// Mueve la aguja al porcentaje pedido con una animación PROPIA sobre el
    /// ángulo (DoubleAnimation + Storyboard). La ejecuta el compositor, así que
    /// la aguja sigue barriendo aunque el hilo de la UI esté ocupado — el caso
    /// típico durante un análisis, que corre pesado en segundo plano. La duración
    /// es proporcional al tramo que falta, con piso y techo, para que un paso
    /// corto no se arrastre ni uno largo se eternice.
    /// </summary>
    private void AnimateNeedleTo(double progress)
    {
        double angle = SplashGauge.AngleForPercent(Math.Clamp(progress, 0.0, 1.0) * 100.0);
        double delta = Math.Abs(angle - _needleTargetAngle);
        _needleTargetAngle = angle;

        int duration = (int)Math.Clamp(
            Math.Round(delta / 120.0 * NeedleAnimationMaxMs),
            NeedleAnimationMinMs,
            NeedleAnimationMaxMs);

        try
        {
            var animation = new DoubleAnimation
            {
                To = angle,
                Duration = new Duration(TimeSpan.FromMilliseconds(duration)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, _needleRotate);
            Storyboard.SetTargetProperty(animation, nameof(RotateTransform.Angle));

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
            _needleAnimation = storyboard;
        }
        catch
        {
            // Sin compositor (o sin árbol): al menos que la aguja no quede atrás.
            _needleRotate.Angle = angle;
        }
    }

    /// <summary>Arranca el barrido continuo del modo indeterminado.</summary>
    private void StartIndeterminate()
    {
        try { _needleAnimation?.Stop(); } catch { }
        _needleAnimation = null;

        // Sin arco de progreso y sin número: no hay avance que mostrar.
        _progressArc.Data = null;
        _percentText.Text = "…";

        try
        {
            var sweep = new DoubleAnimation
            {
                From = SplashGauge.AngleForPercent(0),
                To = SplashGauge.AngleForPercent(100),
                Duration = new Duration(TimeSpan.FromSeconds(IndeterminateCycleSeconds)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(sweep, _needleRotate);
            Storyboard.SetTargetProperty(sweep, nameof(RotateTransform.Angle));

            var storyboard = new Storyboard();
            storyboard.Children.Add(sweep);
            storyboard.Begin();
            _indeterminateAnimation = storyboard;
        }
        catch
        {
            // Sin animación: queda la aguja quieta y el texto de carga, que ya
            // comunica que el trabajo está en curso.
        }
    }

    /// <summary>Corta el barrido y vuelve al progreso real del dial.</summary>
    private void StopIndeterminate()
    {
        try { _indeterminateAnimation?.Stop(); } catch { }
        _indeterminateAnimation = null;

        // La aguja vuelve a su lugar de una: el barrido dejó el ángulo donde
        // caía y animarlo desde ahí parecería un retroceso del progreso.
        ApplyProgress(_progress, animateNeedle: false);
    }

    /// <summary>Arco del dial entre dos porcentajes (el trazo va sobre el radio).</summary>
    private static Geometry BuildArcGeometry(double fromPercent, double toPercent)
    {
        double fromAngle = SplashGauge.AngleForPercent(fromPercent);
        double toAngle = SplashGauge.AngleForPercent(toPercent);
        var (x1, y1) = SplashGauge.PointOnDial(DialCenter, DialCenter, TrackRadius, fromAngle);
        var (x2, y2) = SplashGauge.PointOnDial(DialCenter, DialCenter, TrackRadius, toAngle);

        var figure = new PathFigure
        {
            StartPoint = new Point(x1, y1),
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(x2, y2),
            Size = new Size(TrackRadius, TrackRadius),
            IsLargeArc = Math.Abs(toAngle - fromAngle) > 180,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>Arco del dial con el trazo redondeado en las dos puntas.</summary>
    private static XamlPath NewArc() => new()
    {
        StrokeThickness = TrackStroke,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round
    };

    /// <summary>
    /// Marcas de escala, aguja, pista completa y estado inicial del dial.
    /// </summary>
    private void BuildDial()
    {
        for (int i = 0; i < TickCount; i++)
        {
            double angle = SplashGauge.AngleForPercent(i * 10);
            var (x1, y1) = SplashGauge.PointOnDial(DialCenter, DialCenter, TickInnerRadius, angle);
            var (x2, y2) = SplashGauge.PointOnDial(DialCenter, DialCenter, TickOuterRadius, angle);
            _tickLayer.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                StrokeThickness = i % MajorTickEvery == 0 ? 3 : 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        // Aguja: punta hacia arriba (ángulo 0 = 12 en punto) y una cola corta que
        // equilibra el centro, como un velocímetro real.
        _needle.Points = new PointCollection
        {
            new Point(DialCenter, DialCenter - NeedleLength),
            new Point(DialCenter + NeedleHalfWidth, DialCenter),
            new Point(DialCenter, DialCenter + NeedleTail),
            new Point(DialCenter - NeedleHalfWidth, DialCenter)
        };

        _trackArc.Data = BuildArcGeometry(0, 100);
        _needleRotate.Angle = SplashGauge.AngleForPercent(0);
    }

    /// <summary>
    /// Colores del tema activo. Igual que el splash: pista apagada, arco y aguja
    /// con el acento, cubo exterior con el acento y el interior con el fondo de
    /// las cards (el "agujero" del centro), marcas tenues y el % en el color
    /// primario de texto. Los pinceles son live: al cambiar de tema se repintan
    /// solos, sin volver a llamar a esto.
    /// </summary>
    private void ApplyTheme()
    {
        try
        {
            var accent = ThemeBrushes.Get("AccentBrush");

            _trackArc.Stroke = ThemeBrushes.Get("MutedBrush");
            _trackArc.Opacity = 0.35;
            _progressArc.Stroke = accent;
            _needle.Fill = accent;
            _hubOuter.Fill = accent;
            _hubInner.Fill = ThemeBrushes.Get("CardBackgroundBrush");
            _percentText.Foreground = ThemeBrushes.Get("PrimaryTextBrush");

            var tick = ThemeBrushes.Get("ChartGridBrush");
            foreach (var child in _tickLayer.Children)
            {
                if (child is Shape shape)
                {
                    shape.Stroke = tick;
                    shape.Opacity = 0.6;
                }
            }
        }
        catch
        {
            // Sin App/tema todavía (construcción muy temprana): el dial queda con
            // los pinceles por defecto en vez de romper la página que lo usa.
        }
    }
}
