using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
// Alias obligatorio: el componente tiene su propio namespace Graphics (las APIs gráficas) y
// sin esto el nombre pelea con System.Drawing.Graphics dentro de este archivo.
using GdiGraphics = System.Drawing.Graphics;
// OverlayFrame vive en el namespace de las APIs gráficas: es el contrato por el que el host le
// entrega la franja al backend para que la dibuje dentro del frame.
using WinForge.Component.Benchmark.Graphics;

namespace WinForge.Component.Benchmark.Host;

/// <summary>
/// Datos que muestra el HUD durante la corrida: lo que mide el benchmark (FPS, frame, GPU por
/// frame) y lo que reportan los sensores de la app (uso, temperatura, frecuencia y potencia de
/// GPU y CPU, VRAM y RAM). Los campos en 0 que no tengan fuente se dibujan como "--": un
/// sensor que no existe no puede aparecer como un número inventado.
/// </summary>
internal sealed record HudData(
    string SceneName,
    int Frame,
    int TotalFrames,
    double ElapsedSeconds,
    double Fps,
    double FrameMs,
    double GpuFrameMs,
    double GpuUsagePercent,
    double GpuTempCelsius,
    double GpuMhz,
    double GpuWatts,
    double VramUsedGb,
    double VramTotalGb,
    double CpuUsagePercent,
    double CpuTempCelsius,
    double CpuMhz,
    double CpuWatts,
    double RamPercent);

/// <summary>
/// Franja de métricas de la escena, dibujada con GDI+ en una ventana propia siempre arriba
/// (mismo criterio que el overlay de la app: la barra horizontal de FPS/CPU/GPU/RAM).
///
/// Va SIEMPRE visible (no tiene switch) y ABAJO CENTRADA sobre la escena, con el ancho ajustado
/// a su contenido: la franja se mide sola en cada pintada y se reposiciona, así que un número
/// más largo no la desborda ni la deja descentrada.
///
/// Acá se dibuja el FPS MEDIDO POR EL BENCHMARK (los presents de la escena), no el que la app
/// deduce por ETW: durante una corrida el número tiene que salir de la misma fuente que el
/// informe, si no los dos números no coinciden y ninguno sirve.
/// </summary>
internal sealed class MetricsHud : IDisposable
{
    // Paleta fija del HUD: es un panel sobre la escena, no UI del tema de la app.
    private static readonly Color Background = Color.FromArgb(255, 12, 16, 22);
    private static readonly Color Border = Color.FromArgb(255, 44, 54, 66);
    private static readonly Color Accent = Color.FromArgb(255, 76, 175, 80);
    private static readonly Color LabelColor = Color.FromArgb(255, 154, 164, 178);
    private static readonly Color ValueColor = Color.FromArgb(255, 242, 245, 248);

    private const int PaddingX = 14;
    private const int PaddingY = 8;
    private const int LineGap = 4;
    /// <summary>Aire entre la etiqueta de un grupo y su valor.</summary>
    private const int LabelGap = 5;
    /// <summary>Separación entre grupos (ahí va el separador vertical).</summary>
    private const int SegmentGap = 12;
    /// <summary>Margen contra el borde inferior de la escena.</summary>
    private const int BottomMargin = 20;
    /// <summary>Ancho mínimo de la franja (la ventana nunca baja de acá).</summary>
    private const int MinWidth = 420;
    /// <summary>Paso con el que crece el ancho (ver el ajuste al final de Draw).</summary>
    private const int WidthStep = 16;

    private readonly Win32Window? _window;
    private readonly Func<HudData> _data;
    private readonly Font _labelFont = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _valueFont = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _titleFont = new("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly SolidBrush _backgroundBrush = new(Background);
    private readonly SolidBrush _labelBrush = new(LabelColor);
    private readonly SolidBrush _valueBrush = new(ValueColor);
    private readonly SolidBrush _accentBrush = new(Accent);
    private readonly Pen _borderPen = new(Border);

    // Alto real de las TRES líneas y la Y de cada una: se miden con las fuentes ya creadas
    // (según el DPI del monitor). Con la altura fija de antes (56px) la tercera línea arrancaba
    // en y=57.7 y caía ENTERA fuera de la franja: GPU, VRAM, CPU y RAM nunca se veían.
    private readonly int _height;
    private readonly float _lineTitle;
    private readonly float _lineValues;
    private readonly float _lineHardware;

    /// <summary>Ancho que necesita el contenido dibujado (se recalcula en cada pintada).</summary>
    private int _contentWidth = MinWidth;

    // Rectángulo del CLIENTE de la escena: la franja se centra y se ancla a su borde inferior.
    private int _sceneX;
    private int _sceneY;
    private int _sceneW = MinWidth;
    private int _sceneH;

    /// <summary>True mientras ApplyBounds mueve la ventana (evita reentrar desde Draw).</summary>
    private bool _placing;
    private bool _disposed;

    /// <param name="sceneHandle">Ventana de la escena: la franja se ubica contra su área cliente.</param>
    /// <param name="data">Datos vivos que se dibujan (se llaman en cada pintada).</param>
    /// <param name="withWindow">
    /// False cuando la franja se dibuja DENTRO del frame (ver <see cref="OverlayFrame"/>): no
    /// hace falta la ventana siempre arriba —de hecho, es la que no se podía componer con
    /// vsync apagado— y solo se usan el layout y el dibujo de esta clase.
    /// </param>
    public MetricsHud(nint sceneHandle, Func<HudData> data, bool withWindow = true)
    {
        _data = data;
        // Alto de la franja = alto real de las tres líneas. Se mide con un DC de pantalla
        // porque las fuentes están en puntos: la franja tiene que crecer con el DPI.
        using (var measure = GdiGraphics.FromHwnd(nint.Zero))
        {
            float titleLine = _titleFont.GetHeight(measure);
            float valueLine = _valueFont.GetHeight(measure);
            _lineTitle = PaddingY;
            _lineValues = _lineTitle + titleLine + LineGap;
            _lineHardware = _lineValues + valueLine + LineGap;
            _height = (int)Math.Ceiling(_lineHardware + valueLine + PaddingY);
        }

        // Ancho inicial con los datos del primer momento: la ventana nace con el ancho que va a
        // necesitar. Naciendo con un ancho fijo, la primera pintada salía recortada y, al
        // redimensionar, el texto viejo quedaba pintado por debajo del nuevo.
        try
        {
            using var measure = GdiGraphics.FromHwnd(nint.Zero);
            var lines = BuildLines(measure, _data());
            _contentWidth = Math.Max(MinWidth, RoundUp((int)Math.Ceiling(lines.Max(l => l.Width)) + PaddingX * 2, WidthStep));
        }
        catch { _contentWidth = MinWidth; }

        // Nace OCULTA y se muestra recién después de ubicarla: con la escena en un monitor
        // secundario, mostrarla en (0,0) dejaba la franja parpadeando en el primario.
        if (withWindow)
        {
            _window = new Win32Window("WinForge Benchmark", _contentWidth, _height,
                borderless: true, topMost: true, visible: false, clickThrough: true);
            _window.Paint += Draw;
            // Ubicar ANTES de mostrar: Position mueve + fuerza el primer WM_PAINT (Show+
            // Invalidate), así la franja nace ya pintada sobre la escena y no dejando ver
            // lo que había debajo.
            Position(sceneHandle);
            _window.Invalidate();
        }
    }

    /// <summary>
    /// Sigue a la ventana de la escena (por si la mueven en modo ventana): relee el rectángulo
    /// del cliente y vuelve a centrar la franja contra su borde inferior. También se llama desde
    /// el constructor: recién NACIDA la ventana no recibió su primer WM_PAINT, así que después
    /// de ubicarla hay que forzar ese repintado (Show+Invalidate); sin eso nació mostrando lo que
    /// había debajo, dejando ver un pedazo de lo que la franja tapa.
    /// </summary>
    public void Position(nint sceneHandle)
    {
        if (_window == null) return;
        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(sceneHandle, ref origin)) return;

        _sceneX = origin.X;
        _sceneY = origin.Y;
        if (NativeMethods.GetClientRect(sceneHandle, out var rect))
        {
            _sceneW = Math.Max(1, rect.Right - rect.Left);
            _sceneH = Math.Max(1, rect.Bottom - rect.Top);
        }

        ApplyBounds();
        _window.Show();
        _window.Invalidate();
    }

    /// <summary>
    /// Ubica la franja SOBRE la escena: centrada horizontalmente y pegada a su borde inferior
    /// (con un margen), con el ancho que necesita el contenido. Se llama al nacer, cada vez que
    /// la escena se mueve (modo ventana) y cuando el contenido cambia de ancho.
    /// </summary>
    private void ApplyBounds()
    {
        if (_window == null) return;
        int maxWidth = Math.Max(1, _sceneW);
        int width = Math.Clamp(_contentWidth, Math.Min(MinWidth, maxWidth), maxWidth);
        int x = _sceneX + (_sceneW - width) / 2;
        int y = _sceneY + Math.Max(0, _sceneH - _height - BottomMargin);
        _placing = true;
        try
        {
            _window.Move(x, y, width, _height);
            // Repintado COMPLETO después de mover/redimensionar: Windows invalida solo el área
            // nueva, así que sin esto el texto ya pintado con el ancho viejo se ve por debajo
            // del nuevo (texto superpuesto) hasta el próximo refresco.
            _window.Invalidate();
        }
        finally { _placing = false; }
    }

    public void Refresh() => _window?.Invalidate();

    /// <summary>HWND de la franja (0 cuando la franja se dibuja dentro del frame, sin ventana).</summary>
    public nint Handle => _window?.Handle ?? 0;

    /// <summary>Vuelve a afirmar el TOPMOST de la franja (ver Win32Window.RestickTopMost).</summary>
    public void RestickTopMost() => _window?.RestickTopMost();

    private void Draw(nint deviceContext)
    {
        if (_window == null) return;
        using var graphics = GdiGraphics.FromHdc(deviceContext);
        Paint(graphics, _window.ClientWidth, _window.ClientHeight);
    }

    /// <summary>
    /// Dibuja la franja sobre CUALQUIER Graphics con el layout que esta clase ya midió. Es el
    /// mismo dibujo de la ventana (WM_PAINT) y el del buffer que va dentro del frame: una sola
    /// fuente, para que los dos caminos no puedan divergir.
    /// </summary>
    private void Paint(GdiGraphics graphics, int width, int height)
    {
        HudData data;
        try { data = _data(); }
        catch { return; }

        graphics.SmoothingMode = SmoothingMode.None;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // Se MIDE antes de dibujar: el ancho de la franja es el de la línea más ancha y cada
        // línea se centra dentro de ese ancho.
        var lines = BuildLines(graphics, data);
        float contentWidth = lines.Max(l => l.Width);

        graphics.FillRectangle(_backgroundBrush, 0, 0, width, height);

        foreach (var line in lines)
        {
            // Cada línea va CENTRADA dentro de la franja: si todas arrancaran del mismo borde
            // izquierdo, las cortas (el título con el progreso) quedaban pegadas a la izquierda
            // de una franja del ancho de la línea más larga.
            float x = Math.Max(PaddingX, (width - line.Width) / 2f);
            if (line.Title != null)
            {
                graphics.DrawString(line.Title, _titleFont, _valueBrush, x, line.Y);
                float titleWidth = graphics.MeasureString(line.Title, _titleFont).Width;
                graphics.DrawString(line.Progress ?? "", _labelFont, _labelBrush,
                    x + titleWidth + 10, line.Y + 3);
                continue;
            }
            foreach (var (label, value, color) in line.Segments)
                x = DrawSegment(graphics, x, line.Y, label, value, color);
        }

        // El borde se dibuja último para que quede por encima de todo lo demás.
        graphics.DrawRectangle(_borderPen, 0, 0, width - 1, height - 1);

        // La franja se ajusta a lo que acaba de dibujar: con un ancho fijo, un número más largo
        // (GHz de 4 dígitos, 3 dígitos de watts, GPU/frame de 5 dígitos) se salía del panel.
        // Crece de a pasos y NUNCA se achica durante la corrida (marca de agua): con el ancho
        // exacto, cada valor que cambiaba de largo redimensionaba y recentraba la franja, y se
        // veía vibrar. Como cada línea va centrada, sobrar unos píxeles no se nota.
        int wanted = RoundUp((int)Math.Ceiling(contentWidth) + PaddingX * 2, WidthStep);
        if (wanted > _contentWidth && !_placing)
        {
            _contentWidth = wanted;
            ApplyBounds();
        }
    }

    /// <summary>
    /// Rasteriza la franja a un buffer RGBA (premultiplicado, fila 0 = arriba) para que el
    /// backend la dibuje DENTRO del frame (ver <see cref="OverlayFrame"/>). Devuelve null si no
    /// se pudo dibujar (sin datos o con un área de la escena inválida).
    ///
    /// Si al dibujar la franja crece (marca de agua), se rasteriza de nuevo con el ancho nuevo:
    /// así buffer y posición salen del MISMO ancho y el panel no queda corrido.
    /// </summary>
    public OverlayFrame? RenderOverlay(int sceneWidth, int sceneHeight, long version)
    {
        if (sceneWidth <= 0 || sceneHeight <= 0) return null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            int width = Math.Clamp(_contentWidth, Math.Min(MinWidth, sceneWidth), sceneWidth);
            int height = _height;
            if (width <= 0 || height <= 0) return null;

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = GdiGraphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                Paint(graphics, width, height);
            }

            if (_contentWidth > width) continue;   // creció al pintar: repetir con el ancho nuevo

            var pixels = ToRgbaPremultiplied(bitmap);
            int x = (sceneWidth - width) / 2;
            int y = Math.Max(0, sceneHeight - height - BottomMargin);
            return new OverlayFrame(pixels, width, height, x, y, version);
        }

        return null;
    }

    /// <summary>
    /// Copia el bitmap (BGRA en memoria) a RGBA premultiplicado: ese es el orden que lee el
    /// shader (un uint por píxel con el byte menos significativo en ROJO) y el premultiplicado
    /// es lo que espera el blend One / InverseSourceAlpha.
    /// </summary>
    private static byte[] ToRgbaPremultiplied(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var row = new byte[stride];
            var result = new byte[bitmap.Width * bitmap.Height * 4];
            for (int y = 0; y < bitmap.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    new IntPtr(data.Scan0.ToInt64() + (long)y * stride), row, 0, stride);
                int offset = y * bitmap.Width * 4;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    byte b = row[x * 4 + 0];
                    byte g = row[x * 4 + 1];
                    byte r = row[x * 4 + 2];
                    byte a = row[x * 4 + 3];
                    result[offset + x * 4 + 0] = (byte)(r * a / 255);
                    result[offset + x * 4 + 1] = (byte)(g * a / 255);
                    result[offset + x * 4 + 2] = (byte)(b * a / 255);
                    result[offset + x * 4 + 3] = a;
                }
            }
            return result;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Redondea hacia arriba al múltiplo indicado.</summary>
    private static int RoundUp(int value, int step) => (int)Math.Ceiling(value / (double)step) * step;

    /// <summary>Línea del HUD ya armada y medida.</summary>
    private sealed class HudLine
    {
        public float Y { get; init; }
        /// <summary>Título (solo la primera línea) con su progreso; en las de grupos quedan en null.</summary>
        public string? Title { get; init; }
        public string? Progress { get; init; }
        public List<(string Label, string Value, Color Color)> Segments { get; } = new();
        /// <summary>Ancho real que ocupa la línea (sin la separación que sigue al último grupo).</summary>
        public float Width { get; set; }
    }

    /// <summary>
    /// Arma las tres líneas —título y progreso, lo que mide el benchmark y el resto de la
    /// máquina— y las mide. Es la única fuente de los textos y de los anchos: el centrado, el
    /// ancho de la franja y el dibujo salen todos de acá.
    /// </summary>
    private List<HudLine> BuildLines(GdiGraphics graphics, HudData data)
    {
        // Primera línea: qué se está corriendo y cuánto falta (la corrida es por SEGUNDOS).
        string progress = $"{data.ElapsedSeconds:F0}/{data.TotalFrames} s  ·  {data.Frame} frames";
        var title = new HudLine { Y = _lineTitle, Title = data.SceneName, Progress = progress };
        title.Width = graphics.MeasureString(data.SceneName, _titleFont).Width + 10
                    + graphics.MeasureString(progress, _labelFont).Width;

        // Segunda línea: FPS y frame time (lo que mide el benchmark).
        var values = new HudLine { Y = _lineValues };
        values.Segments.Add(("FPS", FormatFps(data.Fps), ValueColor));
        values.Segments.Add(("frame", $"{data.FrameMs:F2} ms", ValueColor));
        if (data.GpuFrameMs > 0)
            values.Segments.Add(("GPU/frame", $"{data.GpuFrameMs:F2} ms", Accent));
        values.Width = MeasureSegments(graphics, values.Segments);

        // Tercera línea: el resto de la máquina (sensores de la app).
        var hardware = new HudLine { Y = _lineHardware };
        hardware.Segments.Add(("GPU",
            $"{FormatPercent(data.GpuUsagePercent)}  {FormatCelsius(data.GpuTempCelsius)}  {FormatGhZ(data.GpuMhz)}  {FormatWatts(data.GpuWatts)}", ValueColor));
        hardware.Segments.Add(("VRAM", FormatGigabytes(data.VramUsedGb, data.VramTotalGb), ValueColor));
        hardware.Segments.Add(("CPU",
            $"{FormatPercent(data.CpuUsagePercent)}  {FormatCelsius(data.CpuTempCelsius)}  {FormatGhZ(data.CpuMhz)}  {FormatWatts(data.CpuWatts)}", ValueColor));
        hardware.Segments.Add(("RAM", FormatPercent(data.RamPercent), ValueColor));
        hardware.Width = MeasureSegments(graphics, hardware.Segments);

        return new List<HudLine> { title, values, hardware };
    }

    /// <summary>
    /// Ancho que ocupa una línea de grupos, con la MISMA cuenta que usa DrawSegment
    /// (etiqueta + aire + valor + separación). La separación que sigue al ÚLTIMO grupo no es
    /// contenido: descontarla deja los márgenes izquierdo y derecho iguales — sin eso el
    /// bloque quedaba corrido a la izquierda dentro de la franja.
    /// </summary>
    private float MeasureSegments(GdiGraphics graphics, List<(string Label, string Value, Color Color)> segments)
    {
        if (segments.Count == 0) return 0;
        float width = 0;
        foreach (var (label, value, _) in segments)
            width += graphics.MeasureString(label, _labelFont).Width + LabelGap
                   + graphics.MeasureString(value, _valueFont).Width + SegmentGap;
        return width - SegmentGap;
    }

    /// <summary>Dibuja un grupo "etiqueta valor" y devuelve la X donde sigue el próximo.</summary>
    private float DrawSegment(GdiGraphics graphics, float x, float y, string label, string value, Color valueColor)
    {
        graphics.DrawString(label, _labelFont, _labelBrush, x, y);
        float labelWidth = graphics.MeasureString(label, _labelFont).Width;
        float valueX = x + labelWidth + LabelGap;

        var brush = valueColor == ValueColor ? _valueBrush : _accentBrush;
        graphics.DrawString(value, _valueFont, brush, valueX, y);
        float valueWidth = graphics.MeasureString(value, _valueFont).Width;

        float next = valueX + valueWidth + SegmentGap;
        // Separador entre grupos: una línea vertical fina.
        using var separator = new Pen(Border);
        graphics.DrawLine(separator, next - 6, y + 1, next - 6, y + 16);
        return next;
    }

    private static string FormatFps(double fps) => fps > 0 ? $"{fps:F0}" : "--";
    private static string FormatPercent(double value) => value > 0 ? $"{value:F0}%" : "--";
    private static string FormatCelsius(double value) => value > 0 ? $"{value:F0} °C" : "-- °C";
    private static string FormatWatts(double value) => value > 0 ? $"{value:F0} W" : "-- W";
    private static string FormatGhZ(double mhz) => mhz > 0 ? $"{mhz / 1000.0:F2} GHz" : "-- GHz";

    private static string FormatGigabytes(double usedGb, double totalGb)
    {
        if (usedGb <= 0) return "--";
        return totalGb > 0 ? $"{usedGb:F1}/{totalGb:F1} GB" : $"{usedGb:F1} GB";
    }

    private static string FormatSeconds(double seconds)
    {
        if (seconds < 60) return $"{seconds:F0} s";
        return $"{(int)(seconds / 60)}:{(int)(seconds % 60):D2}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // La franja puede vivir sin ventana (rasterizada directo para el overlay del backend).
        _window?.Dispose();
        _labelFont.Dispose();
        _valueFont.Dispose();
        _titleFont.Dispose();
        _backgroundBrush.Dispose();
        _labelBrush.Dispose();
        _valueBrush.Dispose();
        _accentBrush.Dispose();
        _borderPen.Dispose();
    }
}
