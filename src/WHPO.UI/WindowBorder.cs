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

    /// <summary>Preferencia de esquinas redondeadas (Windows 11 build 22000+).</summary>
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>DWMWCP_DONOTROUND: Windows NO redondea las esquinas de la ventana.</summary>
    private const int DwmcpDoNotRound = 1;

    /// <summary>DWMWA_NCRENDERING_POLICY: le dice a DWM si pinta o no el marco de la
    /// ventana (reborde, brillo y sombra) al margen del estilo de la ventana.</summary>
    private const int DwmwaNcRenderingPolicy = 2;

    /// <summary>DWMNCRP_DISABLED: DWM no dibuja NADA del marco: ni el reborde de 1px
    /// blanco/gris, ni su brillo, ni la sombra.</summary>
    private const int DwmNcrpDisabled = 1;

    /// <summary>Valor especial de DWM: "sin borde". Es el que usa el overlay, que
    /// debe llegar hasta el canto sin ningún reborde.</summary>
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    // ===== Región de ventana propia (recorte de la forma) =====

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Radio de las esquinas redondeadas estándar de Windows 11 (DWMWCP_ROUND)
    /// en unidades independientes del DPI: se escala con el DPI real de la ventana.</summary>
    private const int CornerRadiusDip = 8;

    /// <summary>Tope de seguridad de las inserciones del marco: jamás se recorta más que
    /// unas pocas filas/columnas, pase lo que pase con la medición.</summary>
    private const int MaxFrameInsetPixels = 4;

    /// <summary>Piso del recorte en costados y abajo: el reborde que DWM dibuja cuando la
    /// ventana tiene región propia sobrevivió a 1 px (el trazo del splash medía 2-3 px).</summary>
    private const int SideInsetFloorPixels = 3;

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
    /// Le pide a Windows que NO redondee las esquinas de la ventana (DWMWCP_DONOTROUND).
    /// La usa el SPLASH, y la lógica es contraintuitiva: la forma redondeada ya la da el
    /// recorte propio de la región (<see cref="ApplyOwnRoundedRegion"/>), y el redondeo
    /// del SISTEMA dibuja su marco curvo —con su brillo— justo en las esquinas: ahí es
    /// donde sobrevivían los últimos píxeles claros (dos puntos blancos en las esquinas
    /// de arriba) con el resto del marco ya fuera. Sin redondeo del sistema, su marco es
    /// un rectángulo y el recorte de la región lo elimina completo. En Windows 10 el
    /// atributo no existe y la llamada se ignora.
    /// </summary>
    public static void DisableSystemRounding(Microsoft.UI.Xaml.Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            int preference = DwmcpDoNotRound;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Quita el reborde de 1px blanco/gris que Windows 11 pinta alrededor de TODAS
    /// las ventanas —incluidas las borderless— a nivel DWM, por fuera de lo que la
    /// app dibuja. La usa el SPLASH: sobre la tarjeta oscura ese reborde claro se lee
    /// como una "sombra blanca" alrededor del loading. Es el mismo trato que tiene el
    /// overlay (DWMWA_COLOR_NONE). En Windows 10 el atributo no existe: dwmapi
    /// devuelve error y se ignora (en Win10 no hay ese reborde).
    /// </summary>
    public static void RemoveOutline(Microsoft.UI.Xaml.Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            int none = DwmColorNone;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            if (!_outlineLogged)
            {
                _outlineLogged = true;
                App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ILoggingService>()
                    .LogInfo($"Bordes: sin color de borde (COLOR_NONE) — hr 0x{hr:X8}.");
            }
        }
        catch { /* si falla, queda el reborde del sistema: mejor eso que romper la ventana */ }
    }

    /// <summary>El resultado de la primera llamada a RemoveOutline queda en el log.</summary>
    private static bool _outlineLogged;

    /// <summary>El resultado de la primera llamada a BlendBorderWithContent queda en el log.</summary>
    private static bool _blendLogged;

    /// <summary>
    /// Pinta el borde de la ventana con EL MISMO COLOR del contenido que lo rodea, en vez de
    /// pedirle a DWM que no lo dibuje (COLOR_NONE).
    ///
    /// Para qué: con COLOR_NONE, DWM se salta el trazo recto del borde pero igual dibuja —con
    /// el color por defecto, blanco translúcido— los píxeles del arco de las esquinas
    /// redondeadas: son los dos puntos claros de arriba. Igualando el color del borde al fondo
    /// de la barra de título, esos píxeles se dibujan pero no contrastan con nada. Al cambiar
    /// de tema hay que volver a llamarlo con el color nuevo.
    /// </summary>
    public static void BlendBorderWithContent(Microsoft.UI.Xaml.Window window, Windows.UI.Color color)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            int colorref = (color.R << 0) | (color.G << 8) | (color.B << 16);
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
            if (!_blendLogged)
            {
                _blendLogged = true;
                App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ILoggingService>()
                    .LogInfo($"Bordes: borde fundido con el contenido (#{color.R:X2}{color.G:X2}{color.B:X2}) — hr 0x{hr:X8}.");
            }
        }
        catch { /* si falla, queda el borde del sistema */ }
    }

    /// <summary>El resultado de la primera llamada a DisableFrameRendering queda en el log.</summary>
    private static bool _frameRenderingLogged;

    /// <summary>
    /// Apaga el renderizado del marco NATIVO de DWM: ni reborde de 1px, ni su brillo,
    /// ni la sombra. Es el paso siguiente a <see cref="RemoveOutline"/>: en algunas
    /// builds de Windows 11 el reborde blanco vuelve aunque se pida COLOR_NONE
    /// (ese atributo solo cambia EL COLOR del borde, no su existencia), y esta
    /// política le ordena a DWM no pintar el marco para nada. El contenido de la
    /// ventana no se toca (la política alcanza solo el área no cliente).
    /// Contrapartida: si DWM dibujaba las esquinas redondeadas como parte del marco,
    /// la tarjeta puede quedar con esquinas rectas.
    /// </summary>
    public static void DisableFrameRendering(Microsoft.UI.Xaml.Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            int policy = DwmNcrpDisabled;
            int hr = DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref policy, sizeof(int));
            if (!_frameRenderingLogged)
            {
                _frameRenderingLogged = true;
                App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ILoggingService>()
                    .LogInfo($"Bordes: DWM sin renderizado de marco (NCRP disabled) — hr 0x{hr:X8}.");
            }
        }
        catch { /* si falla, queda el marco del sistema */ }
    }

    /// <summary>El resultado de la primera aplicación de región queda en el log.</summary>
    private static bool _regionLogged;

    /// <summary>
    /// Recorta la ventana con una región de rectángulo redondeado PROPIA de la app,
    /// ENTRANDO <paramref name="insetPixels"/> desde cada borde: la forma visible de
    /// la ventana es la tarjeta, y los píxeles exteriores —donde DWM dibuja su
    /// reborde blanco por más que se le pida COLOR_NONE (verificado con captura:
    /// el trazo ocupa los 2-3 px más externos de la ventana, con el redondeo de 8px
    /// del sistema)— quedan fuera del área visible. No le pedimos a Windows que no
    /// lo pinte: le quitamos el lugar donde podría aparecer.
    ///
    /// <paramref name="cornerRadiusFraction"/> es el radio como fracción del ancho
    /// visible (el splash usa 14/448: el mismo estilo Discord a cualquier DPI).
    ///
    /// Contrapartida: el canto del recorte no lleva el suavizado de DWM, así que en
    /// las esquinas puede notarse un escalonado fino, y la tarjeta visible es 2x
    /// inset más chica (nadie la nota). Nota de GDI: tras SetWindowRgn la región es
    /// del sistema, no se libera con DeleteObject.
    /// </summary>
    public static void ApplyOwnRoundedRegion(Microsoft.UI.Xaml.Window window, double cornerRadiusFraction, int insetPixels)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;

            // La región se expresa en píxeles físicos, igual que AppWindow.Size; se
            // re-aplica cada vez que cambia el tamaño (el splash corrige el tamaño
            // por DPI al cargar).
            var size = window.AppWindow.Size;
            if (size.Width <= 0 || size.Height <= 0) return;
            int inset = Math.Max(0, Math.Min(insetPixels, Math.Min(size.Width, size.Height) / 8));
            int width = size.Width - 2 * inset;
            int height = size.Height - 2 * inset;
            if (width <= 0 || height <= 0) return;
            int radius = Math.Max(2, (int)Math.Round(width * cornerRadiusFraction));
            var region = CreateRoundRectRgn(inset, inset, inset + width + 1, inset + height + 1, radius, radius);
            if (region == IntPtr.Zero) return;

            SetWindowRgn(hwnd, region, bRedraw: true);
            if (!_regionLogged)
            {
                _regionLogged = true;
                App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ILoggingService>()
                    .LogInfo($"Bordes: región propia de ventana aplicada ({width}x{height} + inset {inset}px, radio {radius}px).");
            }
        }
        catch { /* si falla, queda la ventana rectangular: mejor eso que romperla */ }
    }

    /// <summary>Grosor (en píxeles) del marco NO CLIENTE en cada lado: la distancia entre el
    /// borde de la ventana y el área de cliente. Es la franja donde Windows 11 dibuja su
    /// reborde de 1 px y donde quedan los restos del arco de las esquinas redondeadas.</summary>
    private struct FrameInsets
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Mide el marco no cliente en vivo —<see cref="GetWindowRect"/> contra
    /// <see cref="ClientToScreen"/> + <see cref="GetClientRect"/>— en vez de asumir 1 px:
    /// es lo que cambia con el DPI y con la política de marco del sistema. Si algo falla
    /// devuelve todo en cero y el llamador aplica sus mínimos.
    /// </summary>
    private static FrameInsets MeasureNonClientFrame(Microsoft.UI.Xaml.Window window)
    {
        var empty = new FrameInsets();
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return empty;
            if (!GetWindowRect(hwnd, out var bounds)) return empty;
            if (!GetClientRect(hwnd, out var client)) return empty;
            var origin = new NativePoint { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref origin)) return empty;

            var insets = new FrameInsets
            {
                Left = origin.X - bounds.Left,
                Top = origin.Y - bounds.Top,
                Right = bounds.Right - (origin.X + client.Right),
                Bottom = bounds.Bottom - (origin.Y + client.Bottom),
            };
            if (insets.Left < 0 || insets.Top < 0 || insets.Right < 0 || insets.Bottom < 0) return empty;
            return insets;
        }
        catch { return empty; }
    }

    /// <summary>
    /// Recorta la forma de la ventana a su ÁREA DE CLIENTE, con las esquinas redondeadas:
    /// deja afuera la franja no cliente de cada lado. Con <paramref name="clip"/> en false
    /// se borra la región y la ventana recupera su forma completa (con la ventana maximizada
    /// hay que limpiarla: ahí dejaría una línea de escritorio en el borde de la pantalla).
    ///
    /// Para qué: Windows 11 dibuja su reborde sobre la SILUETA de la ventana y deja los restos
    /// del arco de las esquinas en la fila de arriba —las dos marcas claras (verificado píxel
    /// por píxel sobre una captura: fila del marco, a ~5 px de cada borde)—. En vez de pedirle
    /// a Windows que no lo pinte —se comprobó que el atributo de color de borde se pierde
    /// cuando DWM recrea el marco—, se le quita a la ventana el lugar donde aparecería.
    ///
    /// Por eso el recorte NUNCA es menor a 1 px en ningún lado, aunque la medición del marco
    /// no cliente dé 0: arriba el marco mide 1 px y ahí está la prueba de que el recorte es lo
    /// que elimina el reborde; a los costados y abajo el área de cliente llega hasta el canto
    /// (marco 0), pero Windows pinta su reborde igual, encima de esos primeros píxeles del
    /// contenido. Recortar 1 px en los cuatro lados es lo que lo deja afuera en los cuatro
    /// lados. Costo: esa primera fila/columna del contenido no se dibuja (1 px, imperceptible
    /// en el tema oscuro; en el tema claro puede leerse como un canto muy fino, y ahí la
    /// alternativa es apagar del todo el renderizado del marco con <see cref="DisableFrameRendering"/>,
    /// que además quita la sombra).
    ///
    /// La región lleva las esquinas REDONDEADAS: al fijar una región propia, la forma de la
    /// ventana pasa a ser la región (Windows deja de redondear por su cuenta), así que se
    /// replica el radio estándar de Win11 escalado por DPI para que la ventana no cambie de
    /// aspecto. Por lo mismo el llamador usa <see cref="DisableSystemRounding"/>: con el
    /// redondeo del sistema activo, su marco curvo se dibuja fuera del recorte y las marcas
    /// de las esquinas sobreviven (la misma lección que dejó el splash).
    ///
    /// Contrapartida: el canto de la región no lleva el suavizado de DWM (el arco se ve
    /// escalonado de muy cerca).
    /// </summary>
    public static void ClipNonClientFrame(Microsoft.UI.Xaml.Window window, bool clip)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;

            var size = window.AppWindow.Size;
            if (size.Width <= 0 || size.Height <= 0) return;

            if (!clip)
            {
                // Sin recorte: región nula = la ventana completa otra vez.
                SetWindowRgn(hwnd, IntPtr.Zero, bRedraw: false);
                return;
            }

            var frame = MeasureNonClientFrame(window);
            int limit = Math.Clamp(MaxFrameInsetPixels, 1, Math.Min(size.Width, size.Height) / 8);

            // Arriba: 1 px alcanza (la fila del marco con las marcas — el único lado que
            // quedó limpio con recorte de 1 px).
            int top = Math.Clamp(frame.Top, 1, limit);

            // Costados y abajo: el reborde que DWM dibuja con región propia sobrevivió al
            // recorte de 1 px, igual que el trazo del splash, que medía 2-3 px.
            int left = Math.Clamp(frame.Left, SideInsetFloorPixels, limit);
            int right = Math.Clamp(frame.Right, SideInsetFloorPixels, limit);
            int bottom = Math.Clamp(frame.Bottom, SideInsetFloorPixels, limit);

            int width = size.Width - right;
            int height = size.Height - bottom;
            if (width - left <= 16 || height - top <= 16) return;

            int dpi = 96;
            try
            {
                uint measured = GetDpiForWindow(hwnd);
                if (measured > 0) dpi = (int)measured;
            }
            catch { }
            int corner = Math.Clamp((int)Math.Round(CornerRadiusDip * dpi / 96.0), 2, Math.Min(width - left, height - top) / 4);

            var region = CreateRoundRectRgn(left, top, width, height, corner * 2, corner * 2);
            if (region == IntPtr.Zero) return;

            // La región pasa a ser del sistema: no se libera con DeleteObject (igual que en
            // ApplyOwnRoundedRegion).
            SetWindowRgn(hwnd, region, bRedraw: false);
        }
        catch { /* si falla, quedan las marcas del sistema: mejor eso que romper la ventana */ }
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
