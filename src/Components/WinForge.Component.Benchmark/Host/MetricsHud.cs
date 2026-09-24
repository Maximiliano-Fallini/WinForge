using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
// Alias obligatorio: el componente tiene su propio namespace Graphics (las APIs gráficas) y
// sin esto el nombre pelea con System.Drawing.Graphics dentro de este archivo.
using GdiGraphics = System.Drawing.Graphics;

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

    private const int HudHeight = 56;
    private const int PaddingX = 14;
    private const int PaddingY = 8;

    private readonly Win32Window _window;
    private readonly Func<HudData> _data;
    private readonly Font _labelFont = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _valueFont = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly Font _titleFont = new("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Point);
    private readonly SolidBrush _backgroundBrush = new(Background);
    private readonly SolidBrush _labelBrush = new(LabelColor);
    private readonly SolidBrush _valueBrush = new(ValueColor);
    private readonly SolidBrush _accentBrush = new(Accent);
    private readonly Pen _borderPen = new(Border);
    private bool _disposed;

    public MetricsHud(nint sceneHandle, int sceneWidth, Func<HudData> data)
    {
        _data = data;
        _window = new Win32Window("WinForge Benchmark", Math.Max(640, sceneWidth), HudHeight,
            borderless: true, topMost: true, visible: true, clickThrough: true);
        _window.Paint += Draw;
        Position(sceneHandle);
        _window.Invalidate();
    }

    /// <summary>Sigue a la ventana de la escena (por si la mueven en modo ventana).</summary>
    public void Position(nint sceneHandle)
    {
        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (NativeMethods.ClientToScreen(sceneHandle, ref origin))
        {
            _window.Move(origin.X, origin.Y, Math.Max(640, _window.ClientWidth), HudHeight);
        }
    }

    public void Refresh() => _window.Invalidate();

    private void Draw(nint deviceContext)
    {
        HudData data;
        try { data = _data(); }
        catch { return; }

        using var graphics = GdiGraphics.FromHdc(deviceContext);
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int width = _window.ClientWidth;
        int height = _window.ClientHeight;

        graphics.FillRectangle(_backgroundBrush, 0, 0, width, height);

        // Primera línea: qué se está corriendo y cuánto falta (la corrida es por SEGUNDOS).
        string progress = $"{data.ElapsedSeconds:F0}/{data.TotalFrames} s  ·  {data.Frame} frames";
        graphics.DrawString(data.SceneName, _titleFont, _valueBrush, PaddingX, PaddingY);
        var titleSize = graphics.MeasureString(data.SceneName, _titleFont);
        graphics.DrawString(progress, _labelFont, _labelBrush, PaddingX + titleSize.Width + 10, PaddingY + 3);

        // Segunda línea: FPS y frame time (lo que mide el benchmark).
        float x = PaddingX;
        float valueLine = PaddingY + 24;
        x = DrawSegment(graphics, x, valueLine, "FPS", FormatFps(data.Fps), ValueColor);
        x = DrawSegment(graphics, x, valueLine, "frame", $"{data.FrameMs:F2} ms", ValueColor);
        if (data.GpuFrameMs > 0)
            x = DrawSegment(graphics, x, valueLine, "GPU/frame", $"{data.GpuFrameMs:F2} ms", Accent);

        // Tercera línea: el resto de la máquina (sensores de la app).
        float hardwareLine = PaddingY + 40;
        x = DrawSegment(graphics, x: PaddingX, y: hardwareLine, "GPU",
            $"{FormatPercent(data.GpuUsagePercent)}  {FormatCelsius(data.GpuTempCelsius)}  {FormatGhZ(data.GpuMhz)}  {FormatWatts(data.GpuWatts)}", ValueColor);
        x = DrawSegment(graphics, x, hardwareLine, "VRAM", FormatGigabytes(data.VramUsedGb, data.VramTotalGb), ValueColor);
        x = DrawSegment(graphics, x, hardwareLine, "CPU",
            $"{FormatPercent(data.CpuUsagePercent)}  {FormatCelsius(data.CpuTempCelsius)}  {FormatGhZ(data.CpuMhz)}  {FormatWatts(data.CpuWatts)}", ValueColor);
        DrawSegment(graphics, x, hardwareLine, "RAM", FormatPercent(data.RamPercent), ValueColor);

        // El borde se dibuja último para que quede por encima de todo lo demás.
        graphics.DrawRectangle(_borderPen, 0, 0, width - 1, height - 1);
    }

    /// <summary>Dibuja un grupo "etiqueta valor" y devuelve la X donde sigue el próximo.</summary>
    private float DrawSegment(GdiGraphics graphics, float x, float y, string label, string value, Color valueColor)
    {
        graphics.DrawString(label, _labelFont, _labelBrush, x, y);
        float labelWidth = graphics.MeasureString(label, _labelFont).Width;
        float valueX = x + labelWidth + 5;

        var brush = valueColor == ValueColor ? _valueBrush : _accentBrush;
        graphics.DrawString(value, _valueFont, brush, valueX, y);
        float valueWidth = graphics.MeasureString(value, _valueFont).Width;

        float next = valueX + valueWidth + 12;
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
        _window.Dispose();
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
