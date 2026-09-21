using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace WHPO_UI;

/// <summary>
/// Borde exterior de las ventanas (Windows 11): lo pinta con el ACENTO del tema
/// activo de la app, en vez del que Windows deriva del acento del sistema. Es
/// la firma visual del mismo estilo que ya tiene la app (el mismo color que ven
/// los botones y el idioma activo), y es visible a la vista: un filete de 1 px
/// casi igual al borde por defecto de Win11 no se puede distinguir — y de hecho
/// la primera versión usaba CardBorderBrush (neutro, oscurísimo) y se veía igual.
///
/// Por qué DWM: el borde lo pinta el compositor POR FUERA del área XAML; no hay
/// forma de dibujarlo desde dentro de la app. DWMWA_BORDER_COLOR (34) existe
/// desde Windows 11 build 22000; en Windows 10 la llamada no hace nada (y el
/// borde de 1 px de Win10 no se puede personalizar): la ventana queda como
/// siempre, sin romper nada. El resultado de la primera llamada queda en el log.
///
/// El color se pide con <see cref="ThemeBrushes.Get"/> (pincel live del tema
/// EFECTIVO de la ventana): al cambiar de tema, ThemeBrushes.Refresh muta el
/// Color en sitio, y este helper lo re-aplica al handler cuando se lo piden
/// (las llamadoras ya re-aplican colores de título en cada cambio de tema).
///
/// El Overlay NO usa este helper: quita el borde a propósito
/// (DWMWA_COLOR_NONE) porque su superficie debe llegar hasta el canto.
/// </summary>
internal static class WindowBorder
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_BORDER_COLOR = 34;

    /// <summary>Color especial de DWM: "dejar que Windows decida" (estado por defecto).</summary>
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);

    /// <summary>El resultado de la primera llamada se informa UNA vez: es lo que permite
    /// diagnosticar desde el log si el SO aceptó el atributo o lo ignoró.</summary>
    private static bool _logged;

    // Del pincel (SolidColorBrush live del tema) al COLORREF 0x00BBGGRR que espera DWM.
    private static int ToColorRef(Microsoft.UI.Xaml.Media.SolidColorBrush brush)
    {
        var c = brush.Color;
        return (c.R << 0) | (c.G << 8) | (c.B << 16);
    }

    /// <summary>
    /// Pinta el borde de la ventana con el acento del tema activo. Se llama después
    /// de crear el handler y en cada cambio de tema. Toma la Window de WinUI (no el
    /// handler): el HWND se resuelve acá, igual que hace el resto de la app con
    /// WindowNative.GetWindowHandle. La primera llamada deja el resultado en el log.
    /// </summary>
    public static void Apply(Microsoft.UI.Xaml.Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            var brush = ThemeBrushes.Get("AccentBrush");
            int color = ToColorRef(brush);
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int));
            if (!_logged)
            {
                _logged = true;
                var log = App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ILoggingService>();
                var c = brush.Color;
                string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                log.LogInfo(hr == 0
                    ? $"Bordes: borde de ventana personalizado aplicado ({hex})"
                    : $"Bordes: Windows no aceptó el color de borde (hr 0x{hr:X8}); queda el del sistema.");
            }
        }
        catch
        {
            if (!_logged)
            {
                _logged = true;
                App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ILoggingService>()
                    .LogInfo("Bordes: este Windows no permite personalizar el borde de la ventana.");
            }
        }
    }

    /// <summary>
    /// Vuelve al borde por defecto de Windows. Pensado para pruebas y para un
    /// futuro "borde del sistema" configurable; las ventanas no lo llaman hoy.
    /// </summary>
    public static void Reset(Microsoft.UI.Xaml.Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            int def = DwmColorDefault;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref def, sizeof(int));
        }
        catch { }
    }
}
