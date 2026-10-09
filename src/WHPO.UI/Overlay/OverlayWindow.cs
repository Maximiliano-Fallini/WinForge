using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace WHPO_UI.Overlay;

/// <summary>
/// Configuración del overlay leída desde los settings (claves "overlay.*").
/// </summary>
public sealed record OverlayConfig(
    bool ShowFps,
    // Máximo (↑) y mínimo (↓) de la sesión: badge propio desde que se separó
    // del FPS (antes iban pegados al badge "FPS").
    bool FpsMaxMin,
    bool ShowLow1,
    bool ShowLow01,
    bool ShowCpu,
    bool CpuUsage,
    bool CpuMhz,
    bool CpuTemp,
    bool CpuWatts,
    bool ShowGpu,
    bool GpuUsage,
    bool GpuMhz,
    bool GpuTemp,
    bool GpuWatts,
    // VRAM usada/total ("gpuMem"): badge propio; antes se dibujaba siempre en
    // la barra horizontal (sin switch) y no existía en el panel vertical.
    bool GpuMem,
    bool ShowRam,
    // Sub-métricas de RAM habilitadas: "ramMb" (usada/total) y "ramMhz"
    // (velocidad de los módulos). La barra horizontal las usa para gatear cada
    // valor; sin estos flags, apagar una no tenía efecto y la otra nunca se
    // dibujaba (solo existía en el panel vertical).
    bool RamMb,
    bool RamMhz,
    // % de uso de RAM ("ramPercent"): badge nuevo.
    bool RamPercent,
    double Opacity,
    double FontScale,
    // "vertical" (panel clásico) u "horizontal" (barra compacta de una línea).
    string Layout,
    // Esquina elegida en la página: "top-right", "top-left", "bottom-right",
    // "bottom-left" o "bottom-center". Sólo "bottom-center" (CornerBottomCenter)
    // se re-aplica en cada render: su posición se deriva del tamaño actual (ver
    // ApplyBottomCenterAnchor), no de la guardada.
    string Corner,
    Color FpsColor,
    Color CpuColor,
    Color GpuColor,
    Color RamColor,
    // Color ÚNICO de los valores de las métricas (números, lows, VRAM, RAM...):
    // setting "overlay.colorMetrics". Los títulos de familia usan los colores
    // por grupo (FpsColor/CpuColor/GpuColor/RamColor); todo lo demás usa este.
    Color MetricColor,
    // Título del juego: línea sutil al final del overlay (abajo de todo). Se
    // muestra/oculta con un switch en la página de apariencia.
    bool ShowGameTitle,
    // FILAS de métricas del overlay (badges arrastrables de la página): cada fila
    // de badges de la configuración es una línea de la superposición. Cada fila
    // contiene ids; dentro de una fila, los ids se dibujan en el orden indicado y
    // solo los habilitados. El overlay dibuja las filas una tras otra.
    List<List<string>> MetricRows,
    // Orden de los GRUPOS en la barra horizontal (de izquierda a derecha):
    // setting "overlay.hGroupOrder", ids de familia ("fps","cpu","gpu","ram").
    // La página de Overlay lo edita arrastrando las cards de grupo; el overlay
    // emite los grupos en este orden. Default: FPS → CPU → GPU → RAM.
    List<string> HGroupOrder);

/// <summary>
/// Ventana del overlay de métricas de juegos: una ventana top-most sin bordes, sin
/// foco y con transparencia por píxel (UpdateLayeredWindow) que se dibuja encima de
/// cualquier juego. Cuando está BLOQUEADA es click-through (los clics pasan al juego);
/// cuando está DESBLOQUEADA recibe el mouse y se puede arrastrar para ubicarla.
///
/// El render corre con un timer propio (16 ms con el gráfico de frametime activo,
/// 250 ms sin él): dibuja el fondo redondeado
/// semi-transparente (opacidad configurable) y las líneas de métricas con el color
/// de cada una. Vive en el hilo de UI de la app (WinForms convive con WinUI 3 en el
/// mismo hilo; NotifyIcon ya lo demuestra).
/// </summary>
public sealed class OverlayWindow : Form
{
    private readonly ISettingsService _settings;
    private readonly ILoggingService _log;
    private readonly IOverlayMetricsService _metrics;

    private Bitmap? _buffer;
    private Graphics? _bufferGraphics;
    private WinFormsTimer? _renderTimer;
    private bool _locked;
    private bool _configDirty = true;
    private OverlayConfig _config;
    private bool _disposed;

    // Tope de escala del gráfico de frametime con HISTÉRESIS: sube al instante
    // ante un pico (el stutter siempre se ve completo) y baja lento (10% por
    // render) cuando vuelve la calma, para que la escala no vibre. Se resetea
    // cuando deja de llegar señal.
    private double _graphCapMs;

    // Dimensiones del overlay: el ancho es fijo y el alto se calcula según la
    // cantidad de líneas de métricas (cada fila de badges = una línea).
    // El ancho dejó de ser 360: ver el comentario de las columnas de hardware
    // (el nombre del CPU/GPU no entraba en 360 y salía cortado).
    private const int OverlayWidth = 396;
    private const int MinOverlayHeight = 120;

    // LAYOUTS: "vertical" (panel clásico, filas apiladas) y "horizontal" (barra
    // ancha: una sola fila de métricas compactas).
    public const string LayoutVertical = "vertical";
    public const string LayoutHorizontal = "horizontal";

    /// <summary>Esquina "abajo centrado": el overlay queda centrado horizontalmente
    /// y pegado al borde inferior del área de trabajo (por encima de la barra de
    /// tareas). A diferencia de las otras esquinas, la posición NO se guarda: se
    /// deriva del tamaño actual en cada render, porque el ancho de la barra
    /// horizontal cambia con el contenido y, si se guardara, quedaría descentrada
    /// (ver ApplyBottomCenterAnchor).</summary>
    public const string CornerBottomCenter = "bottom-center";

    /// <summary>Margen del overlay contra el borde del área de trabajo (sin escalar).</summary>
    private const int CornerMargin = 16;

    /// <summary>Verde por defecto de los títulos de familia (CPU/GPU/RAM/FPS) del
    /// overlay: mismo verde del estado "desbloqueado" de la barra.</summary>
    public const string DefaultFamilyColor = "#78DC8C";

    /// <summary>Blanco por defecto de los valores de las métricas (setting
    /// "overlay.colorMetrics").</summary>
    public const string DefaultMetricColor = "#FFFFFF";
    private const int HorizontalWidth = 640;
    private const int HorizontalHeight = 44;   // alto base de la barra (S() escala con el fontSize)
    private const int HorizontalMinWidth = 240; // piso chico: la barra se ajusta al contenido real
    // Ancho real de la barra horizontal: se ajusta al contenido (se mide en cada
    // render con MeasureHorizontalBarWidth) entre este mínimo y el ancho del monitor.
    private int _horizontalWidth = HorizontalWidth;

    // Nombre del juego como subtítulo AL FINAL de la barra horizontal (toggle
    // "Mostrar título del juego" de la página; igual que el subtítulo del panel
    // vertical). Se computa en BuildHorizontalGroups (lo usan la medición del
    // ancho y el dibujado, que corren en renders distintos).
    private string HorizontalTitle = "";

    // Alto reservado al subtítulo del juego en el panel VERTICAL: se mide el
    // texto ya envuelto en el ancho del panel (nombres largos → varias líneas)
    // antes de crear el buffer. Sin esto el título se recortaba a una línea con
    // "…" (el reporte "si el título es un poco largo se corta").
    private float _gameTitleHeight;

    /// <summary>¿Mostrar el nombre del juego al final de la barra horizontal?
    /// Obedece el mismo switch "overlay.showGameTitle" que el panel vertical.</summary>
    private bool ShowGameTitleEnabled(WHPO.Core.Services.Interfaces.OverlayMetrics? metrics, bool haveFps)
        => _config.ShowGameTitle && haveFps && metrics != null
            && !string.IsNullOrWhiteSpace(metrics.GameName)
            && metrics.GameName != "WinForge";

    /// <summary>¿El overlay está en el layout horizontal (barra compacta)?</summary>
    private bool IsHorizontal => string.Equals(_config.Layout, LayoutHorizontal, StringComparison.Ordinal);

    /// <summary>¿El overlay está anclado al pie, centrado horizontalmente?</summary>
    private bool IsBottomCentered => string.Equals(_config.Corner, CornerBottomCenter, StringComparison.Ordinal);

    /// <summary>Ancho base (sin escala de letra) según el layout activo. En
    /// horizontal el ancho se ajusta al contenido (ver MeasureHorizontalBarWidth).</summary>
    private int OverlayBaseWidth => IsHorizontal ? _horizontalWidth : OverlayWidth;

    /// <summary>
    /// Ancho final de la ventana/buffer en píxeles. En horizontal el ancho medido
    /// YA incluye la escala de letra (se midió con las fuentes escaladas), así que
    /// NO se vuelve a multiplicar por FontScale; en vertical sí (360 base * escala).
    /// </summary>
    private int ComputeOverlayWidth() => IsHorizontal
        ? _horizontalWidth
        : (int)Math.Round(OverlayWidth * _config.FontScale);

    // === Layout horizontal ===
    // Separadores verticales entre familias: el texto secundario del tema con alpha alta —
    // tienen que NOTARSE (antes eran tan tenues que se confundían con el panel) y, al salir de
    // la paleta del tema, dejan de ser un gris fijo que no combina con él.
    private const int DividerAlpha = 150;

    /// <summary>Separador vertical de la barra (línea corta y centrada).</summary>
    private void DrawHorizontalDivider(Graphics g, float x, float y, float height)
    {
        using var pen = new Pen(_dividerColor, 1f);
        g.DrawLine(pen, x, y + height * 0.18f, x, y + height * 0.82f);
    }

    // Columnas de la grilla de hardware, alineadas a la derecha (sin escala):
    // usage / mhz / temp / watts. Medidas con la fuente real (Consolas bold 12.5px):
    // "100%"=32.5, "5299 MHz"=60.8, "89°C"=32.5, "120 W"=39.6 → con gaps de 22px
    // entre columnas y 16px de margen derecho, la fila completa de 4 valores queda
    // aireada y el peor caso (3 dígitos de watts) nunca se pega al valor anterior
    // ni al borde.
    //
    // La grilla y el panel están corridos +36px sobre la original (144/227/282/344
    // sobre 360px) porque el NOMBRE del hardware se dibuja en lo que queda a la
    // izquierda de la primera columna y ese espacio no alcanzaba: el presupuesto
    // era 91px por unidad de escala (128px con letra al 140%) medido contra el
    // valor más ancho de la columna 1 ("100%" = 32.5px), mientras que los nombres
    // que devuelve ShortenName miden hasta ~110px ("Ryzen 7 7800X3D") — el CPU y la
    // GPU salían truncados con "…" (ej. "13th Gen i7-1370…"). Con estas columnas
    // el presupuesto es 127px y los nombres reales entran completos.
    private const float ColUsageRight = 180;
    private const float ColMhzRight = 263;
    private const float ColTempRight = 318;
    private const float ColWattsRight = 380;

    // Fuentes dinámicas según la escala de letra configurada (todo Consolas;
    // el candado NO usa fuente: se dibuja vector con GDI+, ver DrawLockGlyph).
    private Font? _lowFont;
    private Font? _lineFont;
    private float _fontScale = -1f;

    private Font LowFont => _lowFont!;
    private Font LineFont => _lineFont!;

    private void EnsureFonts(float scale)
    {
        if (Math.Abs(scale - _fontScale) < 0.001f) return;
        _lowFont?.Dispose();
        _lineFont?.Dispose();
        _lowFont = new Font("Consolas", 11f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _lineFont = new Font("Consolas", 12.5f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        _fontScale = scale;
    }

    /// <summary>Escala de layout proporcional al tamaño de letra configurado.</summary>
    private float S(float v) => v * (float)_config.FontScale;

    // =====================================================================
    // Paleta del TEMA y ritmo vertical
    // =====================================================================

    // El overlay es una ventana GDI+ fuera del árbol de XAML: no ve los pinceles del diccionario
    // de tema, así que hasta ahora el panel era SIEMPRE un gris fijo (#0F0F12) y sobre un tema con
    // identidad (Marea, Brasa, Aurora…) quedaba como un bloque ajeno. Ahora el CROMO del overlay
    // —panel, texto secundario, divisores y candado— sale de la definición del tema activo
    // (ThemePalettes, la misma fuente que el splash y el ajuste de transparencia). Los colores de
    // las MÉTRICAS no se tocan: siguen siendo los que elige el usuario en la página de Overlay.
    private Color _panelColor = Color.FromArgb(15, 15, 18);
    private Color _secondaryColor = Color.FromArgb(180, 180, 180);
    private Color _dividerColor = Color.FromArgb(DividerAlpha, 105, 105, 108);
    private Color _lockedGlyphColor = Color.FromArgb(215, 205, 205, 210);
    private Color _unlockedGlyphColor = Color.FromArgb(215, 120, 220, 140);

    // Filete INTERIOR de 1 px con el color de BORDE del tema (CardBorderBrush: los temas lo
    // definen como su acento al 20 %). Es lo que define el borde del panel cuando el vidrio del
    // tema es muy transparente: sin él, un panel al 10 % de alfa no tiene dónde terminar y sobre
    // un juego claro se lee como texto flotando. Se dibuja RECORTADO al path (ver DrawPanelRim),
    // no como pluma del relleno: la pluma sin recortar dejaba la fila del borde con alfa parcial
    // y se veía un reborde gris (medido con tools/OverlayPixelProbe).
    private Color _rimColor = Color.FromArgb(38, 255, 255, 255);

    /// <summary>Tema con el que se resolvió la paleta: si cambia, Render la vuelve a leer.</summary>
    private AppTheme? _paletteTheme;

    /// <summary>¿La paleta vigente salió del tema? False = el HUD oscuro de siempre (ver RefreshPalette).</summary>
    private bool _paletteFromTheme;

    // Ritmo vertical y sangrías del panel (valores base; S() los escala con la letra). El alto del
    // panel (ComputeOverlayHeight) calcula con ESTOS MISMOS números: antes eran literales repetidos
    // en los dos lados (25, 24, 12, 16) y alcanzaba con tocar uno para que el alto dejara de
    // coincidir con lo dibujado. El ritmo de las sub-líneas (LowFont, 11px) es más corto que el de
    // las líneas con nombre y valores (LineFont, 12.5px) para que cada sub-línea se lea pegada a su
    // línea madre.
    private const float PadTop = 8f;
    private const float StateLineHeight = 21f;    // línea del candado
    private const float MetricLineHeight = 25f;   // línea con nombre + valores (LineFont)
    private const float SubLineHeight = 22f;      // sub-línea chica (LowFont): lows, máx/mín, VRAM
    private const float PanelPadX = 12f;          // sangría izquierda de todo el contenido
    private const float LabelValueGap = 8f;       // aire mínimo entre el nombre y el primer valor
    private const float GraphHeight = 50f;
    private const float GraphGap = 8f;
    private const float BarPadX = 14f;            // sangría de la barra horizontal

    /// <summary>
    /// Resuelve el cromo del overlay con el tema activo. La llama ReadConfig (y por lo tanto
    /// InvalidateConfig) y Render cuando detecta que el tema cambió, así el cambio se ve sin
    /// reiniciar la app.
    /// </summary>
    private void RefreshPalette()
    {
        var theme = PanelAppearance.EffectiveTheme();
        _paletteTheme = theme;

        // El overlay es un HUD sobre el juego y su texto es CLARO (los colores de las métricas los
        // elige el usuario; por defecto blanco y verde claro): con un tema de base clara —Claro y
        // Rosa/Blanco, cuya card es blanca— un panel blanco dejaría el texto invisible. Ahí el
        // overlay se queda con el HUD oscuro de siempre; los temas con identidad propia son todos
        // oscuros (Marea #062430, Brasa #240F0C, Aurora #07242A, Negro/Azul
        // #0E1524, ~0,1 de luminancia), así que en ésos sí toma el color del tema.
        var themeCard = ThemeColor(theme, "CardBackgroundBrush");
        _paletteFromTheme = themeCard is { } card && Luminance(card) <= 0.45;

        _panelColor = _paletteFromTheme ? themeCard!.Value : Color.FromArgb(15, 15, 18);
        var secondary = _paletteFromTheme
            ? (ThemeColor(theme, "SecondaryTextBrush") ?? Color.FromArgb(180, 180, 180))
            : Color.FromArgb(180, 180, 180);
        _secondaryColor = secondary;
        _dividerColor = Color.FromArgb(DividerAlpha, secondary.R, secondary.G, secondary.B);
        _lockedGlyphColor = Color.FromArgb(215, secondary.R, secondary.G, secondary.B);
        var accent = _paletteFromTheme
            ? (ThemeColor(theme, "AccentBrush") ?? Color.FromArgb(120, 220, 140))
            : Color.FromArgb(120, 220, 140);
        _unlockedGlyphColor = Color.FromArgb(215, accent.R, accent.G, accent.B);

        // El filete: el color de BORDE del tema (que los temas definen como su acento al 20 %), o
        // un blanco al 15 % si el tema no lo define (los clásicos) o si el overlay se quedó con el
        // HUD oscuro por ser un tema claro.
        _rimColor = (_paletteFromTheme ? ThemeColor(theme, "CardBorderBrush") : null)
            ?? Color.FromArgb(38, 255, 255, 255);

        // Verificación sin pantalla: la misma bitácora que el splash y el ajuste de paneles
        // (el overlay es una ventana GDI+ que ninguna sonda puede mirar desde afuera).
        PanelAppearance.Diag(
            $"overlay: tema {theme} — panel {Hex(_panelColor)} " +
            $"({(_paletteFromTheme ? "del tema" : "HUD oscuro: el tema es claro")}) con el vidrio del tema " +
            $"{ThemePanelAlpha() * 100:0} % (transparencia de paneles vigente), filete {Hex(_rimColor)}, " +
            $"secundario {Hex(_secondaryColor)}, divisor {Hex(_dividerColor)}, candado libre {Hex(_unlockedGlyphColor)}");
    }

    /// <summary>
    /// Alfa (0..1) que el TEMA le da a sus paneles con el ajuste de transparencia vigente: es el
    /// mismo cálculo que pinta las cards, el menú y la barra de título de la app
    /// (<see cref="PanelAppearance.ApplyToColor(Windows.UI.Color)"/>), así que el overlay se ve tan
    /// de vidrio como los paneles del tema activo — 10 % en los temas de entorno (90 % de
    /// transparencia), 100 % en los planos y de degradado — y sigue EN VIVO el deslizador de la
    /// pestaña Apariencia. Se lee en cada render: es una cuenta, no un estado.
    ///
    /// Antes el alfa salía solo del ajuste de opacidad del overlay (85 %), así que el panel quedaba
    /// como un bloque sólido idéntico en todos los temas: el reporte "el componente es sólido, no
    /// reacciona al tema que le pongamos a la app".
    /// </summary>
    private static double ThemePanelAlpha()
    {
        try
        {
            // Blanco opaco de prueba: ApplyToColor le devuelve el MISMO color con el alfa de
            // paneles del tema, así que el alfa del resultado es el que buscamos.
            var probe = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            return PanelAppearance.ApplyToColor(probe).A / 255.0;
        }
        catch
        {
            // Sin app (una sonda, un arranque temprano): paneles opacos, como los clásicos.
            return 1.0;
        }
    }

    /// <summary>
    /// Luminancia relativa del color (0 = negro, 1 = blanco). Decide si el panel del tema sirve
    /// como fondo de un HUD de texto claro (ver RefreshPalette).
    /// </summary>
    private static double Luminance(Color color)
        => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    /// <summary>
    /// Color de una clave del tema activo, o null si el tema no la define en ningún lado. Los
    /// llamadores tienen su respaldo: NUNCA se quedan con un color transparente (una clave sin
    /// color de fábrica dejaba el texto invisible).
    /// </summary>
    private static Color? ThemeColor(AppTheme theme, string key)
    {
        try
        {
            if (ThemePalettes.TryGetFactoryColor(theme, key, out var color))
                return Color.FromArgb(color.A, color.R, color.G, color.B);
        }
        catch { }
        return null;
    }

    private static string Hex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Filete interior de 1 px del panel, con el color de BORDE del tema (CardBorderBrush, que los
    /// temas definen como su acento al 20 %). Es lo que le da final al panel cuando el vidrio del
    /// tema es muy transparente: sin él, un panel al 10 % de alfa no tiene dónde terminar.
    ///
    /// Va RECORTADO al propio path, con la pluma de 2 px: el recorte deja ver solo la mitad de
    /// adentro, así el contorno queda de 1 px nítido y no se corre hacia afuera —la pluma sin
    /// recortar era el reborde gris que se veía sobre fondos claros (ver el comentario del
    /// FillPath). Su alfa escala con la opacidad del ajuste: en 0 % desaparece con el panel.
    /// </summary>
    private void DrawPanelRim(Graphics g, GraphicsPath path)
    {
        int alpha = (int)Math.Round(_rimColor.A * _config.Opacity);
        if (alpha <= 0) return;

        var state = g.Save();
        try
        {
            g.SetClip(path, CombineMode.Replace);
            using var pen = new Pen(Color.FromArgb(alpha, _rimColor.R, _rimColor.G, _rimColor.B), 2f);
            g.DrawPath(pen, path);
        }
        catch
        {
            // El filete es decorativo: si el recorte falla, el panel se dibuja igual.
        }
        finally
        {
            g.Restore(state);
        }
    }

    // ===== P/Invoke: estilos extendidos + UpdateLayeredWindow =====

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCAPTION = 0x0002;
    private const int HTCLIENT = 0x0001;

    /// <summary>Windows avisa a TODAS las ventanas cuando cambia la resolución o la
    /// disposición de monitores (entrar/salir de pantalla completa, dock, etc.).</summary>
    private const int WM_DISPLAYCHANGE = 0x007E;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;
    private const uint BI_BITFIELDS = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>
    /// BITMAPINFO de 32 bpp con máscaras RGB (BI_BITFIELDS): con BI_RGB el byte de
    /// alpha se ignora y los píxeles semi-transparentes se renderizan opacos. Con
    /// las máscaras de 24 bits, el byte alto (0xFF000000) queda como canal alpha.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // ===== Borde de ventana de Windows 11 (DWM) =====

    // Windows 11 dibuja un borde de 1px blanco/gris alrededor de TODAS las
    // ventanas —incluidas las borderless y layered como esta— a nivel DWM, por
    // fuera de lo que la app pinta con UpdateLayeredWindow. Es el "reborde"
    // que sobrevive a cualquier cambio de dibujo. DWMWA_BORDER_COLOR con el
    // valor especial DWMWA_COLOR_NONE lo elimina. En Win10 el atributo no
    // existe: dwmapi devuelve error y se ignora (en Win10 no hay ese borde).
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    // ===== Hotkeys globales exclusivos (RegisterHotKey) =====

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyIdShow = 0x5101;
    private const int HotkeyIdLock = 0x5102;
    // Evita que WM_HOTKEY se repita mientras la tecla queda sostenida.
    private const uint MOD_NOREPEAT = 0x4000;

    /// <summary>Se dispara cuando el usuario presiona el atajo mostrar/ocultar.</summary>
    public event Action? ShowHotkeyPressed;

    /// <summary>Se dispara cuando el usuario presiona el atajo bloquear/desbloquear.</summary>
    public event Action? LockHotkeyPressed;

    private int _showHotkeyVk = -1;
    private int _lockHotkeyVk = -1;

    /// <summary>
    /// Registra los atajos globales en ESTA ventana (RegisterHotKey). Cuando el
    /// registro tiene éxito, Windows consume la combinación y solo esta app la
    /// recibe (otras apps que la usen por mensajes dejan de verla). Devuelve si
    /// cada atajo se pudo registrar; si uno falla es porque otra app ya es dueña.
    /// </summary>
    public (bool Show, bool Lock) RegisterHotkeys()
    {
        UnregisterHotkeys();
        if (!IsHandleCreated) return (false, false);

        int showVk = _settings.Get("overlay.showHotkeyVk", 0x58);
        int showMods = _settings.Get("overlay.showHotkeyMods", 0x3);
        int lockVk = _settings.Get("overlay.lockHotkeyVk", 0x43);
        int lockMods = _settings.Get("overlay.lockHotkeyMods", 0x3);

        bool show = showVk > 0 && RegisterHotKey(Handle, HotkeyIdShow, (uint)(showMods | (int)MOD_NOREPEAT), (uint)showVk);
        bool lockH = lockVk > 0 && RegisterHotKey(Handle, HotkeyIdLock, (uint)(lockMods | (int)MOD_NOREPEAT), (uint)lockVk);
        if (!show)
            _log.LogWarning($"OverlayWindow: el atajo mostrar/ocultar (VK {showVk}) no se pudo registrar: ya está en uso por otra aplicación.");
        if (!lockH)
            _log.LogWarning($"OverlayWindow: el atajo bloquear/desbloquear (VK {lockVk}) no se pudo registrar: ya está en uso por otra aplicación.");
        _showHotkeyVk = show ? showVk : -1;
        _lockHotkeyVk = lockH ? lockVk : -1;
        return (show, lockH);
    }

    public void UnregisterHotkeys()
    {
        if (IsHandleCreated)
        {
            if (_showHotkeyVk > 0) UnregisterHotKey(Handle, HotkeyIdShow);
            if (_lockHotkeyVk > 0) UnregisterHotKey(Handle, HotkeyIdLock);
        }
        _showHotkeyVk = -1;
        _lockHotkeyVk = -1;
    }

    public OverlayWindow(
        ISettingsService settings,
        ILoggingService log,
        IOverlayMetricsService metrics)
    {
        _settings = settings;
        _log = log;
        _metrics = metrics;
        _config = ReadConfig();

        Text = "WinForgeOverlay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(OverlayWidth, ComputeOverlayHeight());
        BackColor = Color.Black;

        // Sin activación (nunca roba el foco del juego) + tool window + layered.
        var ex = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE,
            ex | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);

        // El render arranca ya en el constructor (OnLoad lo vuelve a llamar; el
        // método es idempotente): así la autocorrección de visibilidad corre
        // también cuando la ventana nace oculta porque los ajustes la esconden.
        StartRendering();
    }

    public bool Locked => _locked;

    /// <summary>Marca la configuración como sucia para que el próximo render la relea.</summary>
    public void InvalidateConfig() => _configDirty = true;

    /// <summary>
    /// Bloquea/desbloquea el overlay: bloqueado = click-through (los clics pasan al
    /// juego); desbloqueado = recibe el mouse y se puede arrastrar.
    /// </summary>
    public void SetLocked(bool locked)
    {
        if (_locked == locked) return;
        _locked = locked;
        try
        {
            var ex = GetWindowLong(Handle, GWL_EXSTYLE);
            if (locked)
                ex |= WS_EX_TRANSPARENT;
            else
                ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(Handle, GWL_EXSTYLE, ex);
        }
        catch (Exception ex2)
        {
            _log.LogWarning($"OverlayWindow: no se pudo cambiar el estado de bloqueo: {ex2.Message}");
        }
    }

    /// <summary>Arranca el render (llamar al mostrar por primera vez).</summary>
    public void StartRendering()
    {
        if (_renderTimer != null) return;
        _renderTimer = new WinFormsTimer { Interval = RenderInterval() };
        _renderTimer.Tick += (_, _) => Render();
        _renderTimer.Start();
    }

    public void StopRendering()
    {
        _renderTimer?.Stop();
        _renderTimer?.Dispose();
        _renderTimer = null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            // Quitar el borde de 1px que Windows 11 pinta alrededor de la ventana
            // (el reborde blanco/gris que ve el usuario sobre el panel del overlay).
            int none = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref none, sizeof(int));
        }
        catch { /* Win10: el atributo no existe y no hay borde que quitar. */ }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            // Fijar el tamaño final (escala de letra + líneas) ANTES de restaurar la
            // posición: si el primer render cambia el tamaño, EnsureBuffer re-ancla a
            // la esquina y se pierde la posición que arrastró el usuario.
            Size = new Size(ComputeOverlayWidth(), ComputeOverlayHeight());

            // Posición segura por defecto según el LAYOUT (sin posición guardada
            // o guardada fuera de toda pantalla):
            // - Vertical: ARRIBA A LA DERECHA del monitor primario.
            // - Horizontal: ARRIBA A LA IZQUIERDA del monitor primario.
            // - "Abajo centrado": la deriva ApplyBottomCenterAnchor al final de
            //   este método (acá sólo se apunta al pie del monitor primario).
            var primary = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int x = _settings.Get("overlay.posX", int.MinValue);
            int y = _settings.Get("overlay.posY", int.MinValue);
            bool haveSaved = x != int.MinValue && y != int.MinValue;
            if (haveSaved)
            {
                // "Fuera de toda pantalla" = el punto no cae dentro de ningún
                // monitor (raro, pero posible tras cambiar la config de pantallas).
                var probe = new Point(x + Width / 2, y + Height / 2);
                bool onAnyScreen = Screen.AllScreens.Any(s => s.WorkingArea.Contains(probe));
                if (!onAnyScreen) haveSaved = false;
            }
            if (!haveSaved)
            {
                var pos = IsBottomCentered
                    ? CornerPosition(CornerBottomCenter, primary)   // abajo centrado
                    : IsHorizontal
                        ? new Point(primary.Left + CornerMargin, primary.Top + CornerMargin)   // horizontal: IZQUIERDA
                        : new Point(primary.Right - Width - CornerMargin, primary.Top + CornerMargin); // vertical: DERECHA
                x = pos.X;
                y = pos.Y;
            }

            // Acotar la posición para que la ventana quede COMPLETA dentro del área de
            // trabajo del monitor más cercano (ver ClampToNearestScreen).
            Location = new Point(x, y);
            ClampToNearestScreen();
            // Con la esquina "Abajo centrado" la posición se deriva del tamaño
            // recién aplicado (manda sobre la guardada y sobre el acotado).
            ApplyBottomCenterAnchor();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayWindow: no se pudo restaurar la posición: {ex.Message}");
        }
        StartRendering();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // El overlay se oculta (Hide), nunca se cierra; si se intenta cerrar, cancelar.
        e.Cancel = true;
        Hide();
        base.OnFormClosing(e);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        SchedulePositionSave();
    }

    // Guardado de posición con debounce (el evento LocationChanged se dispara en cada píxel al arrastrar).
    private WinFormsTimer? _posSaveTimer;

    private void SchedulePositionSave()
    {
        if (_posSaveTimer == null)
        {
            _posSaveTimer = new WinFormsTimer { Interval = 600 };
            _posSaveTimer.Tick += (_, _) =>
            {
                _posSaveTimer.Stop();
                _settings.Set("overlay.posX", Location.X);
                _settings.Set("overlay.posY", Location.Y);
                _settings.Save();
            };
        }
        _posSaveTimer.Stop();
        _posSaveTimer.Start();
    }

    /// <summary>
    /// Ubica el overlay en la esquina pedida del monitor primario ("top-right",
    /// "top-left", "bottom-right", "bottom-left" o "bottom-center") y guarda la
    /// posición. Con "bottom-center" la ventana recuerda la esquina y la vuelve a
    /// aplicar en cada render: esa posición se deriva del tamaño actual (ver
    /// ApplyBottomCenterAnchor), así que no se puede arrastrar.
    /// </summary>
    public void SetCorner(string corner)
    {
        try
        {
            // Recordar la esquina sin esperar a releer los settings: el próximo
            // render ya tiene que saber si el ancla del pie está activa.
            _config = _config with { Corner = corner };

            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            var pos = CornerPosition(corner, area);
            Location = pos;
            _settings.Set("overlay.posX", pos.X);
            _settings.Set("overlay.posY", pos.Y);
            _settings.Save();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayWindow: no se pudo ubicar en la esquina: {ex.Message}");
        }
    }

    /// <summary>Posición de una esquina dentro del área de trabajo indicada.</summary>
    private Point CornerPosition(string corner, Rectangle area) => corner switch
    {
        "top-left" => new Point(area.Left + CornerMargin, area.Top + CornerMargin),
        "bottom-right" => new Point(area.Right - Width - CornerMargin, area.Bottom - Height - CornerMargin),
        "bottom-left" => new Point(area.Left + CornerMargin, area.Bottom - Height - CornerMargin),
        CornerBottomCenter => new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - CornerMargin),
        // Por defecto: arriba a la derecha (esquina con la que nace el panel vertical).
        _ => new Point(area.Right - Width - CornerMargin, area.Top + CornerMargin)
    };

    /// <summary>
    /// Vuelve a anclar el overlay al pie centrado cuando la esquina elegida es
    /// "Abajo centrado". La posición se DERIVA del tamaño actual en vez de
    /// guardarse: el ancho de la barra horizontal se mide en cada render y cambia
    /// con el contenido (FPS de 2 o 3 dígitos, MHz, título del juego), y el ancho
    /// del panel vertical cambia con las filas y la escala de letra. Si la posición
    /// fuera la guardada, la barra quedaría descentrada en cuanto creciera el
    /// contenido. Consecuencia buscada: con esta esquina el arrastre no mueve la
    /// ventana (el ancla la recoloca en el próximo render).
    /// </summary>
    private void ApplyBottomCenterAnchor()
    {
        if (!IsBottomCentered || !IsHandleCreated) return;
        try
        {
            // El monitor se resuelve por el centro ACTUAL: si el usuario la había
            // llevado a otra pantalla, se centra en ESA (y no en la primaria).
            var probe = new Point(Location.X + Width / 2, Location.Y + Height / 2);
            var area = Screen.FromPoint(probe).WorkingArea;
            var pos = CornerPosition(CornerBottomCenter, area);
            if (pos.X != Location.X || pos.Y != Location.Y)
            {
                Location = pos;
                // Rastro para diagnosticar (solo cuando cambia, no en cada render): si el
                // usuario reporta que "no se ancla", el log dice si el ancla corrió.
                _log.LogInfo($"Overlay: anclado abajo al centro en ({pos.X},{pos.Y}) [{Width}x{Height}] en " +
                             $"{area.Width}x{area.Height}.");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayWindow: no se pudo anclar abajo al centro: {ex.Message}");
        }
    }

    // Arrastre: cuando está desbloqueado, cualquier clic arrastra la ventana (HTCAPTION).
    protected override void WndProc(ref Message m)
    {
        // Atajos globales registrados en esta ventana (RegisterHotKey).
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HotkeyIdShow)
            {
                try { ShowHotkeyPressed?.Invoke(); } catch { }
                m.Result = IntPtr.Zero;
                return;
            }
            if (id == HotkeyIdLock)
            {
                try { LockHotkeyPressed?.Invoke(); } catch { }
                m.Result = IntPtr.Zero;
                return;
            }
        }

        if (!_locked && m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)HTCLIENT)
                m.Result = (IntPtr)HTCAPTION;
            return;
        }

        // Cambió la pantalla (resolución, disposición de monitores, dock, entrar o salir de
        // pantalla completa): la posición guardada puede haber quedado FUERA de toda
        // pantalla, y el overlay "desaparece" hasta que se sale del juego. Se reencuadra y
        // se reafirma el z-order.
        if (m.Msg == WM_DISPLAYCHANGE)
        {
            base.WndProc(ref m);
            try
            {
                _log.LogInfo($"Overlay: cambió la pantalla: reencuadre desde ({Location.X},{Location.Y}).");
                ClampToNearestScreen();
                // "Abajo centrado" depende del área de trabajo: se recalcula acá
                // mismo (si no, quedaría con la posición del monitor viejo hasta
                // el próximo render).
                ApplyBottomCenterAnchor();
                AssertTopMost();
                _log.LogInfo($"Overlay: posición tras el reencuadre: ({Location.X},{Location.Y}).");
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Overlay: cambio de pantalla: {ex.Message}");
            }
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Deja la ventana ENTERA dentro del área de trabajo del monitor más cercano. Se usa al
    /// restaurar la posición y cuando cambia la pantalla: una posición guardada con el panel
    /// angosto (vertical) deja la barra ancha (horizontal) afuera, y un cambio de resolución
    /// puede dejar al overlay fuera de todo monitor.
    /// </summary>
    private void ClampToNearestScreen()
    {
        var nearArea = Screen.FromPoint(new Point(Location.X, Location.Y)).WorkingArea;
        int x = Math.Max(nearArea.Left, Math.Min(Location.X, nearArea.Right - Width));
        int y = Math.Max(nearArea.Top, Math.Min(Location.Y, nearArea.Bottom - Height));
        if (Width > nearArea.Width) x = nearArea.Left;
        if (Height > nearArea.Height) y = nearArea.Top;
        if (x != Location.X || y != Location.Y) Location = new Point(x, y);
    }

    /// <summary>Momento (TickCount64) de la última reafirmación del z-order.</summary>
    private long _lastTopMostAssert;

    /// <summary>
    /// Vuelve a poner la ventana arriba de todo. `TopMost = true` se aplica UNA sola vez y,
    /// entre ventanas topmost, gana la que se activó última: como esta NUNCA se activa
    /// (WS_EX_NOACTIVATE), la ventana topmost de un juego la puede tapar. Repetirlo la
    /// devuelve al frente sin robarle el foco al juego (SWP_NOACTIVATE).
    /// </summary>
    private void AssertTopMost()
    {
        try
        {
            if (!IsHandleCreated || !Visible) return;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        catch
        {
            // El overlay nunca debe romper por el z-order.
        }
    }

    // ===== Render =====

    private OverlayConfig ReadConfig()
    {
        bool b(string key, bool def) => _settings.Get(key, def);
        double d(string key, double def) => _settings.Get(key, def);
        Color c(string key, string defHex)
        {
            try
            {
                var hex = _settings.Get(key, defHex);
                if (string.IsNullOrWhiteSpace(hex)) return ColorTranslator.FromHtml(defHex);
                return ColorTranslator.FromHtml(hex);
            }
            catch { return ColorTranslator.FromHtml(defHex); }
        }

        // FILAS de métricas (badges): "overlay.metricRows" guarda la lista de filas
        // (cada fila = una línea de la superposición) y "overlay.metricEnabled" el
        // subconjunto visible; el overlay dibuja cada fila y, dentro de ella, SOLO
        // los ids habilitados. Versión vieja: "overlay.metricOrder" plano se migra
        // agrupando por familia (CPU/GPU/RAM/FPS). Si no hay ninguna clave (primera
        // corrida) se migra desde los switches viejos: CPU → GPU → RAM → FPS → lows.
        var metricRows = new List<List<string>>();
        bool hasAny = false;
        try
        {
            if (_settings.Contains("overlay.metricRows"))
            {
                hasAny = true;
                var saved = _settings.Get("overlay.metricRows", new List<List<string>>());
                if (saved != null)
                {
                    foreach (var row in saved)
                        metricRows.Add(row.Where(IsValidMetricId).ToList());
                }
            }
            else if (_settings.Contains("overlay.metricOrder"))
            {
                hasAny = true;
                var flat = _settings.Get("overlay.metricOrder", new List<string>()) ?? new List<string>();
                metricRows = GroupByFamily(flat.Where(IsValidMetricId));
            }

            // Filtrar los ids visibles (si la clave existe: respetar "todo apagado").
            if (_settings.Contains("overlay.metricEnabled"))
            {
                var enabled = _settings.Get("overlay.metricEnabled", new List<string>());
                if (enabled != null)
                {
                    metricRows = metricRows
                        .Select(row => row.Where(id => enabled.Contains(id)).ToList())
                        .Where(row => row.Count > 0)
                        .ToList();
                }
            }
        }
        catch { }
        // Primera corrida (sin ningún dato nuevo): migrar desde los switches viejos.
        if (!hasAny)
        {
            var metricOrder = new List<string>();
            var showFps = b("overlay.showFps", true);
            var low1 = b("overlay.low1", true);
            var low01 = b("overlay.low01", false);
            var showCpu = b("overlay.showCpu", true);
            var showGpu = b("overlay.showGpu", true);
            var showRam = b("overlay.showRam", true);
            if (showCpu)
            {
                if (b("overlay.cpuUsage", true)) metricOrder.Add("cpuUsage");
                if (b("overlay.cpuMhz", true)) metricOrder.Add("cpuMhz");
                if (b("overlay.cpuTemp", true)) metricOrder.Add("cpuTemp");
                if (b("overlay.cpuWatts", false)) metricOrder.Add("cpuWatts");
            }
            if (showGpu)
            {
                if (b("overlay.gpuUsage", true)) metricOrder.Add("gpuUsage");
                if (b("overlay.gpuMhz", true)) metricOrder.Add("gpuMhz");
                if (b("overlay.gpuTemp", true)) metricOrder.Add("gpuTemp");
                if (b("overlay.gpuWatts", false)) metricOrder.Add("gpuWatts");
            }
            if (showRam)
            {
                metricOrder.Add("ramMb");
                metricOrder.Add("ramMhz");
            }
            if (showFps) metricOrder.Add("fps");
            if (low1) metricOrder.Add("low1");
            if (low01) metricOrder.Add("low01");
            metricRows = GroupByFamily(metricOrder);
            // Orden por defecto: FPS y lows en filas propias (FPS arriba, lows debajo).
            metricRows = SplitFpsAndLows(metricRows);
        }

        // Respetar el orden exacto de filas del usuario (overlay.metricRows).
        // Solo partir filas que pasen de 4 métricas (ChunkRows).
        metricRows = metricRows.SelectMany(ChunkRows).ToList();

        // Orden de los GRUPOS de la barra horizontal (drag de cards en la página):
        // ids de familia validados, sin duplicados y completados con el default.
        List<string>? savedGroupOrder = null;
        try
        {
            if (_settings.Contains("overlay.hGroupOrder"))
                savedGroupOrder = _settings.Get("overlay.hGroupOrder", new List<string>());
        }
        catch { }
        var hGroupOrder = NormalizeGroupOrder(savedGroupOrder);

        var allIds = metricRows.SelectMany(r => r).ToList();
        return new OverlayConfig(
            ShowFps: allIds.Contains("fps"),
            FpsMaxMin: allIds.Contains(FpsMaxMinId),
            ShowLow1: allIds.Contains("low1"),
            ShowLow01: allIds.Contains("low01"),
            ShowCpu: allIds.Any(m => m.StartsWith("cpu", StringComparison.Ordinal)),
            CpuUsage: allIds.Contains("cpuUsage"),
            CpuMhz: allIds.Contains("cpuMhz"),
            CpuTemp: allIds.Contains("cpuTemp"),
            CpuWatts: allIds.Contains("cpuWatts"),
            ShowGpu: allIds.Any(m => m.StartsWith("gpu", StringComparison.Ordinal)),
            GpuUsage: allIds.Contains("gpuUsage"),
            GpuMhz: allIds.Contains("gpuMhz"),
            GpuTemp: allIds.Contains("gpuTemp"),
            GpuWatts: allIds.Contains("gpuWatts"),
            GpuMem: allIds.Contains(GpuMemId),
            ShowRam: allIds.Any(m => m.StartsWith("ram", StringComparison.Ordinal)),
            RamMb: allIds.Contains("ramMb"),
            RamMhz: allIds.Contains("ramMhz"),
            RamPercent: allIds.Contains(RamPercentId),
            // La opacidad baja hasta 0 (fondo totalmente transparente).
            Opacity: Math.Clamp(d("overlay.opacity", 0.85), 0.0, 1.0),
            FontScale: Math.Clamp(d("overlay.fontSize", 1.4), 0.6, 2.0),
            Layout: _settings.Get("overlay.layout", LayoutVertical),
            Corner: _settings.Get("overlay.corner", "top-right"),
            FpsColor: c("overlay.colorFps", DefaultFamilyColor),
            CpuColor: c("overlay.colorCpu", DefaultFamilyColor),
            GpuColor: c("overlay.colorGpu", DefaultFamilyColor),
            RamColor: c("overlay.colorRam", DefaultFamilyColor),
            MetricColor: c("overlay.colorMetrics", DefaultMetricColor),
            ShowGameTitle: b("overlay.showGameTitle", true),
            MetricRows: metricRows,
            HGroupOrder: hGroupOrder);
    }

    /// <summary>Parte una fila en líneas de a 4 (la grilla de la config es fija).</summary>
    private static List<List<string>> ChunkRows(List<string> ids)
    {
        var rows = new List<List<string>>();
        for (int i = 0; i < ids.Count; i += 4)
            rows.Add(ids.Skip(i).Take(4).ToList());
        return rows;
    }

    /// <summary>Id de métrica válido (de los badges configurables).</summary>
    private static bool IsValidMetricId(string id) => id switch
    {
        "fps" or FpsMaxMinId or "low1" or "low01" or FrametimeGraphId or "cpuUsage" or "cpuMhz" or "cpuTemp" or "cpuWatts"
            or "gpuUsage" or "gpuMhz" or "gpuTemp" or "gpuWatts" or GpuMemId
            or "ramMb" or "ramMhz" or RamPercentId => true,
        _ => false
    };

    // Id del badge del gráfico de frametime (compartido con OverlayPage).
    public const string FrametimeGraphId = "latencyGraph";

    // Ids de badges NUEVOS (compartidos con OverlayPage): el máx/mín del FPS se
    // separó en su propio badge, y VRAM / RAM % son switches propios.
    public const string FpsMaxMinId = "fpsMaxMin";
    public const string GpuMemId = "gpuMem";
    public const string RamPercentId = "ramPercent";

    // Ventana de tiempo que muestra el gráfico (estilo RTSS) y muestras pedidas
    // al monitor de FPS: 900 frames cubren 3.5 s hasta ~257 fps (el buffer de
    // FpsMonitor guarda 900).
    private const double FrametimeWindowSeconds = 3.5;
    private const int FrametimeGraphSamples = 900;

    // Gracias de señal del gráfico: si el último evento llegó hace más de esto,
    // el juego dejó de presentar y la señal se drena (la línea se limpia y el
    // valor ms se oculta). La entrega ETW llega en ráfagas de hasta ~1 s, así que
    // la gracia debe cubrirlas sin ocultar datos con el juego corriendo.
    private const double SignalGraceMs = 2500;

    // Hueco máximo tolerable entre el último punto y el borde derecho del panel
    // (~2% de la ventana de 3.5 s): con el flush ETW de 100 ms la entrega nunca
    // se retrasa mucho, así que el último punto se mantiene casi pegado al borde
    // y el espacio vacío entre la línea y la card es mínimo.
    private const double MaxHuecoMs = 60;

    /// <summary>Intervalo de render: 16 ms con el gráfico activo (~60 actualizaciones/seg:
    /// el scroll del eje de tiempo real se ve fluido y cada frame nuevo se dibuja en
    /// cuanto llega) y 250 ms sin él (el overlay es estático salvo los valores, y así
    /// se ahorra CPU).</summary>
    private int RenderInterval() =>
        !IsHorizontal && _config.MetricRows.Any(r => r.Contains(FrametimeGraphId)) ? 16 : 250;

    /// <summary>
    /// ¿La ventana debería estar visible AHORA, según los ajustes? Es la ÚLTIMA
    /// palabra sobre la visibilidad (activación + mostrar/ocultar + la regla de
    /// "solo cuando hay juego"): el render la evalúa en cada tick, así que un
    /// cambio de switch o del atajo se aplica solo aunque el callback del servicio
    /// se haya perdido — el bug de "desactivé el overlay y siguió encima del juego".
    /// </summary>
    private bool ShouldBeVisibleNow()
    {
        if (!_settings.Get("overlay.enabled", false)) return false;
        if (!_settings.Get("overlay.visible", true)) return false;
        if (_settings.Get("overlay.onlyWhenGameDetected", true) && !(_metrics.Latest?.GamePid > 0)) return false;
        return true;
    }

    private void Render()
    {
        if (_disposed || !IsHandleCreated) return;

        // Autocorrección de visibilidad (ver ShouldBeVisibleNow): corre ANTES de
        // dibujar y vale en los dos sentidos (ocultar y volver a mostrar). Es una
        // red de seguridad: el servicio oculta/muestra por su cuenta y esto lo
        // confirma en cada tick, sin depender de un callback del DispatcherQueue.
        if (!ShouldBeVisibleNow())
        {
            if (Visible)
            {
                Hide();
                _log.LogInfo("OverlayWindow: ocultada por configuración (activación, visibilidad o detección de juego).");
            }
            return;
        }
        if (!Visible)
        {
            InvalidateConfig();
            Show();
            _log.LogInfo("OverlayWindow: mostrada por configuración.");
        }

        try
        {
            // Z-order: vale repetirlo (ver AssertTopMost). Una vez por segundo alcanza y no
            // le agrega nada al costo del render, que corre a 60 fps cuando hay gráfico.
            long now = Environment.TickCount64;
            if (now - _lastTopMostAssert >= 1000)
            {
                _lastTopMostAssert = now;
                AssertTopMost();
            }
            // La paleta se relee también cuando cambió el TEMA: el usuario puede cambiar de tema con
            // el overlay andando (el cambio se aplica en vivo), y hasta ahora el panel quedaba del
            // color viejo hasta reiniciar la app.
            if (_configDirty || _paletteTheme != PanelAppearance.EffectiveTheme())
            {
                _config = ReadConfig();
                _configDirty = false;
                // Prender/apagar el gráfico cambia la frecuencia de render necesaria.
                if (_renderTimer != null) _renderTimer.Interval = RenderInterval();
            }

            var metrics = _metrics.Latest;
            EnsureFonts((float)_config.FontScale);

            using var fpsBrush = new SolidBrush(_config.FpsColor);
            using var cpuBrush = new SolidBrush(_config.CpuColor);
            using var gpuBrush = new SolidBrush(_config.GpuColor);
            using var ramBrush = new SolidBrush(_config.RamColor);

            // Ancho de la barra horizontal: se ajusta al contenido. Se mide con las
            // fuentes reales ANTES de crear el buffer (EnsureBuffer aplica
            // OverlayBaseWidth, que devuelve este ancho en modo horizontal).
            if (IsHorizontal)
            {
                Graphics mg = _bufferGraphics ?? CreateGraphics();
                try
                {
                    _horizontalWidth = MeasureHorizontalBarWidth(mg, metrics,
                        metrics != null && metrics.Fps > 0);
                }
                finally { if (!ReferenceEquals(mg, _bufferGraphics)) mg.Dispose(); }
            }
            else
            {
                // Panel vertical: el subtítulo del juego reserva alto según el
                // texto envuelto (ver MeasureGameTitleHeight).
                _gameTitleHeight = MeasureGameTitleHeight(metrics);
            }

            EnsureBuffer();

            // "Abajo centrado": el ancla manda sobre la posición guardada. Se aplica
            // acá porque recién ahora se conoce el tamaño real (arriba se midió el
            // ancho de la barra y EnsureBuffer lo aplicó a la ventana).
            ApplyBottomCenterAnchor();

            var g = _bufferGraphics!;
            // Transparente NEGRO: Color.Transparent es blanco con alpha 0 y, según
            // cómo lo convierta GetHbitmap, dejaba un borde blanco en los bordes del
            // fondo redondeado.
            g.Clear(Color.FromArgb(0, 0, 0, 0));

            // Fondo: panel con ESQUINAS REDONDEADAS. La opacidad controla SOLO el
            // fondo; el texto se dibuja siempre al 100%. Antes eran rectangulares
            // porque Color.Transparent dejaba "puntas blancas"; hoy el buffer es
            // transparente NEGRO (FromArgb(0,0,0,0)) y PaintLayered copia los
            // píxeles premultiplicados tal cual, así las esquinas fuera del path
            // quedan realmente transparentes.
            // El alfa final = VIDRIO DEL TEMA × ajuste de opacidad del overlay. El vidrio lo fija
            // el tema (ver ThemePanelAlpha: 10 % en los de entorno, 100 % en los planos y de
            // degradado) y sigue en vivo el deslizador de transparencia de la pestaña Apariencia;
            // el ajuste de opacidad escala dentro de eso, así que 0 % = panel invisible y 100 % =
            // el vidrio pleno del tema. Antes el alfa salía solo del ajuste (85 % siempre), y por
            // eso el overlay era el mismo bloque sólido en todos los temas.
            int bgAlpha = Math.Clamp((int)Math.Round(ThemePanelAlpha() * _config.Opacity * 255), 0, 255);
            // Radio: el panel vertical usa el de las cards de la app (12 DIP con la letra al 100 %),
            // acotado para que la escala de letra no lo deforme; la barra horizontal, la mitad (el
            // alto es chico y un radio grande comería media barra).
            int radius = IsHorizontal
                ? (int)Math.Clamp(8f * (float)_config.FontScale, 4f, 14f)
                : (int)Math.Clamp(12f * (float)_config.FontScale, 6f, 18f);
            using (var path = RoundedRect(new Rectangle(0, 0, _buffer!.Width, _buffer.Height), radius))
            using (var bgBrush = new SolidBrush(Color.FromArgb(bgAlpha, _panelColor.R, _panelColor.G, _panelColor.B)))
            {
                // SOLO FillPath anti-aliased, SIN pluma de contorno. La pluma del
                // reborde (aunque sea del mismo color del fondo) cruza el límite del
                // path y deja la fila de píxeles del borde con alpha PARCIAL (~43%,
                // medido con tools/OverlayPixelProbe); sobre contenido claro esa fila
                // se compone como un REBORDE GRIS alrededor del panel —y su alpha es
                // la del fondo, por eso "sube y baja" con la opacidad. El fill AA
                // puro cubre el píxel del borde al ~91% y compone casi igual al
                // interior: sin reborde visible sobre ningún fondo.
                g.FillPath(bgBrush, path);
                DrawPanelRim(g, path);
            }

            float y = S(PadTop);
            string? gameName = metrics?.GameName;
            bool haveFps = metrics != null && metrics.Fps > 0;

            // ===== Layout horizontal (barra FPS | CPU | GPU | RAM) =====
            // Una sola fila con grupos separados por divisores verticales: FPS
            // (actual/máx/mín de la sesión), CPU, GPU y RAM. El ancho se ajusta al
            // contenido, el texto queda centrado verticalmente y el candado de la
            // izquierda muestra el estado (cerrado = bloqueado, abierto = libre).
            if (IsHorizontal)
            {
                DrawHorizontalBar(g, metrics, haveFps);
                PaintLayered();
                return;
            }

            // Estado con CANDADO VECTOR centrado (igual que la barra horizontal):
            // cerrado y gris = bloqueado (click-through), abierto y verde =
            // desbloqueado (arrastrable). Ya no va el texto traducido.
            using (var stateBrush = new SolidBrush(_locked ? _lockedGlyphColor : _unlockedGlyphColor))
            {
                DrawLockGlyph(g, ((float)_buffer!.Width - LockGlyphWidth(g)) / 2f, y + S(7), stateBrush);
            }
            y += S(StateLineHeight);

            // Métricas en las FILAS de badges de la configuración (arrastrables):
            // cada fila de la página es una línea de la superposición, dibujada en
            // el mismo orden, con SOLO los valores habilitados y en las columnas
            // fijas de la grilla (el nombre no corre a los %, los % no corren a los
            // MHz, etc.).
            foreach (var row in BuildRenderRows(_config.MetricRows))
            {
                // Las filas con la métrica CORE del grupo (el uso %, o "FPS" para
                // su bloque) llevan el nombre del hardware; las sub-filas creadas
                // al mover una métrica abajo (ej. "CPU MHz" sola) van SIN título.
                bool showName = row.Ids.Any(OverlayWindow.IsCoreMetric);
                switch (row.Family)
                {
                    case "cpu" when metrics != null:
                        y = DrawHardwareLine(g, metrics.CpuName, metrics.CpuUsagePercent, metrics.CpuMhz,
                            metrics.CpuTempCelsius, metrics.CpuWatts,
                            row.Ids.Contains("cpuUsage"), row.Ids.Contains("cpuMhz"),
                            row.Ids.Contains("cpuTemp"), row.Ids.Contains("cpuWatts"),
                            cpuBrush, y, showName);
                        break;
                    case "gpu" when metrics != null:
                    {
                        // Línea de hardware del GPU: solo si la fila trae alguna de
                        // sus métricas (una fila con solo "VRAM" va directo a la
                        // sub-línea y no gasta una línea vacía).
                        if (row.Ids.Any(id => id != GpuMemId))
                            y = DrawHardwareLine(g, metrics.GpuName, metrics.GpuUsagePercent, metrics.GpuMhz,
                                metrics.GpuTempCelsius, metrics.GpuWatts,
                                row.Ids.Contains("gpuUsage"), row.Ids.Contains("gpuMhz"),
                                row.Ids.Contains("gpuTemp"), row.Ids.Contains("gpuWatts"),
                                gpuBrush, y, showName);
                        // VRAM usada/total ("gpuMem"): sub-línea chica, mismo estilo
                        // que los lows del FPS. Antes no existía en vertical.
                        if (row.Ids.Contains(GpuMemId))
                        {
                            string? vram = metrics.GpuVramTotalMb > 0
                                ? $"{metrics.GpuMemUsedMb / 1024.0:0.#}/{metrics.GpuVramTotalMb / 1024.0:0.#} GB"
                                : metrics.GpuMemUsedMb > 0 ? $"{metrics.GpuMemUsedMb / 1024.0:0.#} GB" : null;
                            if (vram != null)
                            {
                                using var vramBrush = new SolidBrush(_config.MetricColor);
                                g.DrawString($"VRAM {vram}", LowFont, vramBrush, S(PanelPadX), y);
                                y += S(SubLineHeight);
                            }
                        }
                        break;
                    }
                    case "ram" when metrics != null:
                    {
                        // RAM: el nombre es "RAM" seguido de la configuración de
                        // módulos (RAM 2x16 GB), después el uso en MB y la velocidad.
                        // En sub-filas sin core (ej. "RAM MHz" movida abajo) no hay
                        // nombre: solo la línea de valores.
                        // Cada valor tiene su COLUMNA FIJA: mostrar u ocultar uno no
                        // mueve a los demás. El % de uso ("ramPercent") usa la
                        // tercera columna, libre para RAM.
                        var ramValues = new List<(string Text, float RightX)>();
                        if (row.Ids.Contains("ramMb"))
                            ramValues.Add(($"{metrics.RamUsedMb:F0} MB", S(ColUsageRight)));
                        if (row.Ids.Contains("ramMhz") && metrics.RamMhz > 0)
                            ramValues.Add(($"{metrics.RamMhz:F0} MHz", S(ColMhzRight)));
                        if (row.Ids.Contains(RamPercentId) && metrics.RamPercent > 0)
                            ramValues.Add(($"{metrics.RamPercent:F0}%", S(ColTempRight)));

                        string ramName = showName
                            ? (string.IsNullOrWhiteSpace(metrics.RamConfig) ? "RAM" : "RAM " + metrics.RamConfig)
                            : "";
                        DrawLabeledLine(g, ramName, ramBrush, y, ramValues);
                        y += S(25);
                        break;
                    }
                    case "fps":
                        y = DrawFpsBlock(g, metrics, haveFps, row.Ids, fpsBrush, y);
                        break;
                }
            }

            // Nombre del juego (subtítulo sutil, abajo de todo) — SOLO si el
            // switch de la página de apariencia lo habilita. Se dibuja en un
            // rectángulo: con un nombre largo se reparte en varias líneas (el
            // alto lo reserva ComputeOverlayHeight con _gameTitleHeight) en vez
            // de cortarse con "…" a mitad de palabra.
            if (ShowGameTitleEnabled(metrics, haveFps))
            {
                // El gris del texto secundario sale del tema (con Marea es el celeste apagado del
                // tema, no un gris neutro que no combina con el panel).
                using var gameBrush = new SolidBrush(_secondaryColor);
                g.DrawString(gameName!, LowFont, gameBrush,
                    new RectangleF(S(PanelPadX), y, _buffer!.Width - S(PanelPadX * 2), _gameTitleHeight));
            }

            PaintLayered();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayWindow: error de render: {ex.Message}");
        }
    }

    /// <summary>
    /// Grupos de la barra horizontal con los
    /// valores formateados del diseño: FPS (actual, con ↑ para el máximo y ↓ para
    /// el mínimo de la sesión), CPU
    /// (% de uso, °C y GHz actuales), GPU (% de uso, °C y VRAM usada/total en GB)
    /// y RAM (uso actual/total). Los valores se dibujan SIEMPRE en blanco; el
    /// color de la familia es solo para el título del grupo. Los valores sin
    /// señal se omiten. El ORDEN de los grupos es el configurado por el usuario
    /// (setting "overlay.hGroupOrder", editable arrastrando las cards en la
    /// página); sin configuración: FPS → CPU → GPU → RAM.
    /// </summary>
    private List<(string Label, List<string> Parts)> BuildHorizontalGroups(
        WHPO.Core.Services.Interfaces.OverlayMetrics? metrics, bool haveFps)
    {
        var byKey = new Dictionary<string, (string Label, List<string> Parts)>();

        // Nombre del juego para el final de la barra (subtítulo sutil): SOLO si
        // el switch de la página lo habilita y hay señal.
        HorizontalTitle = ShowGameTitleEnabled(metrics, haveFps)
            ? (metrics?.GameName ?? "").Trim()
            : "";

        // FPS: actual, y máximo (↑) / mínimo (↓) de la sesión. Son DOS switches
        // independientes: el valor actual sale del badge "fps" y la pareja ↑/↓
        // del badge "fpsMaxMin" (antes iban juntos en un solo badge).
        if (_config.ShowFps || _config.FpsMaxMin)
        {
            var fpsParts = new List<string>();
            if (_config.ShowFps)
                fpsParts.Add(haveFps && metrics != null ? metrics.Fps.ToString("F0") : "--");
            if (_config.FpsMaxMin)
            {
                if (metrics != null && metrics.FpsMax > 0)
                    fpsParts.Add($"↑{metrics.FpsMax:F0}");
                if (metrics != null && metrics.FpsMin > 0)
                    fpsParts.Add($"↓{metrics.FpsMin:F0}");
            }
            if (fpsParts.Count > 0) byKey["fps"] = ("FPS", fpsParts);
        }

        // CPU: % de uso, temperatura, GHz y vatios actuales.
        var cpuParts = new List<string>();
        if (metrics != null)
        {
            if (_config.CpuUsage) cpuParts.Add($"{metrics.CpuUsagePercent:F0}%");
            if (_config.CpuTemp && metrics.CpuTempCelsius > 0)
                cpuParts.Add($"{metrics.CpuTempCelsius:F0}°C");
            if (_config.CpuMhz && metrics.CpuMhz > 0)
                cpuParts.Add($"{metrics.CpuMhz / 1000.0:0.#} GHz");
            // Los vatios del CPU FALTABAN en la barra: con el switch encendido no
            // aparecían nunca (solo existían en el panel vertical).
            if (_config.CpuWatts && metrics.CpuWatts > 0)
                cpuParts.Add($"{metrics.CpuWatts:F0} W");
        }
        if (cpuParts.Count > 0) byKey["cpu"] = ("CPU", cpuParts);

        // GPU: % de uso, temperatura y VRAM usada/total.
        var gpuParts = new List<string>();
        if (metrics != null)
        {
            if (_config.GpuUsage) gpuParts.Add($"{metrics.GpuUsagePercent:F0}%");
            if (_config.GpuTemp && metrics.GpuTempCelsius > 0)
                gpuParts.Add($"{metrics.GpuTempCelsius:F0}°C");
            // VRAM usada/total: badge propio "gpuMem" (antes se dibujaba siempre).
            if (_config.GpuMem)
            {
                if (metrics.GpuVramTotalMb > 0)
                    gpuParts.Add($"{metrics.GpuMemUsedMb / 1024.0:0.#}/{metrics.GpuVramTotalMb / 1024.0:0.#} GB");
                else if (metrics.GpuMemUsedMb > 0)
                    gpuParts.Add($"{metrics.GpuMemUsedMb / 1024.0:0.#} GB");
            }
        }
        if (gpuParts.Count > 0) byKey["gpu"] = ("GPU", gpuParts);

        // RAM: usada/total (switch propio) y velocidad de los módulos (switch
        // propio). Antes la usada/total se dibujaba SIEMPRE (apagarla no hacía
        // nada) y los MHz no se dibujaban NUNCA (encenderla no hacía nada).
        var ramParts = new List<string>();
        if (metrics != null)
        {
            // % de uso: badge propio "ramPercent".
            if (_config.RamPercent && metrics.RamPercent > 0)
                ramParts.Add($"{metrics.RamPercent:F0}%");
            if (_config.RamMb)
            {
                if (metrics.RamTotalMb > 0)
                    ramParts.Add($"{metrics.RamUsedMb / 1024.0:0.#}/{metrics.RamTotalMb / 1024.0:0.#} GB");
                else
                    ramParts.Add($"{metrics.RamUsedMb / 1024.0:0.#} GB");
            }
            if (_config.RamMhz && metrics.RamMhz > 0)
                ramParts.Add($"{metrics.RamMhz:F0} MHz");
        }
        if (ramParts.Count > 0) byKey["ram"] = ("RAM", ramParts);

        // Emitir los grupos en el orden configurado (overlay.hGroupOrder):
        // NormalizeGroupOrder garantiza que los 4 estén presentes sin duplicados.
        // Los grupos sin ningún valor habilitado/con dato no se emiten: si no,
        // la barra dibujaba el título solo ("CPU", "RAM") sin ningún valor.
        var ordered = new List<(string Label, List<string> Parts)>();
        foreach (var key in _config.HGroupOrder)
            if (byKey.TryGetValue(key, out var grp))
                ordered.Add(grp);
        return ordered;
    }

    /// <summary>
    /// Ancho de la barra horizontal: se mide TODO lo que se va a dibujar (candado,
    /// grupos, separadores y márgenes) con las fuentes reales, y se acota entre el
    /// mínimo y el ancho del monitor. Se llama en cada render ANTES de crear el
    /// buffer (EnsureBuffer aplica OverlayBaseWidth, que devuelve este ancho).
    /// El resultado YA viene escalado (las fuentes y S() escalan con FontScale):
    /// NO se multiplica otra vez por FontScale ni por el DPI — todo el dibujo del
    /// overlay trabaja en píxeles crudos (AutoScaleMode = None).
    /// </summary>
    private int MeasureHorizontalBarWidth(Graphics g, WHPO.Core.Services.Interfaces.OverlayMetrics? metrics,
        bool haveFps)
    {
        float padX = S(14);
        var groups = BuildHorizontalGroups(metrics, haveFps);
        float x = padX + S(18) + LockGlyphWidth(g) + S(10);
        for (int i = 0; i < groups.Count; i++)
        {
            float runW = MeasureRunWidth(g, groups[i].Label, groups[i].Parts);
            if (runW <= 0) continue;
            // El divisor se dibuja DENTRO de este gap (en el medio): no consume
            // ancho propio. Antes se sumaban 33px por divisor y el dibujo solo
            // usaba 16 — eso dejaba la barra más larga que su contenido.
            x += runW + S(16);
        }
        // Nombre del juego al final de la barra: cuenta para el ancho medido.
        if (HorizontalTitle.Length > 0)
        {
            x += S(12); // gap separador del último grupo
            x += g.MeasureString(HorizontalTitle, LowFont).Width;
            x += S(16); // aire a la derecha del título (no pegado al borde)
        }
        x += padX - S(16); // margen derecho (el último grupo no agrega gap extra)
        // Techo, no redondeo: al dibujar, el título se recorta con FitText contra
        // maxW = ancho - padX - tx, que es el texto medido ± 0.5 px. Redondeando
        // hacia abajo, maxW quedaba una fracción por debajo del texto y FitText
        // cambiaba el último carácter por "…" aunque entrara: el reporte "si el
        // título es un poco largo se corta".
        int w = (int)Math.Ceiling(x);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return Math.Clamp(w, HorizontalMinWidth, Math.Max(HorizontalMinWidth, area.Width - 32));
    }

    /// <summary>
    /// Alto (px) que necesita el subtítulo del nombre del juego en el panel
    /// vertical: se mide el texto ya envuelto en el ancho del panel con la
    /// fuente real. Sin título (o en horizontal, donde lo mide la barra) devuelve
    /// el hueco de siempre. El ancho del panel no cambia: el texto se reparte en
    /// varias líneas en vez de cortarse.
    /// </summary>
    private float MeasureGameTitleHeight(WHPO.Core.Services.Interfaces.OverlayMetrics? metrics)
    {
        float baseHeight = S(16);
        if (!ShowGameTitleEnabled(metrics, metrics != null && metrics.Fps > 0)) return baseHeight;
        Graphics? own = null;
        try
        {
            var g = _bufferGraphics ?? (own = CreateGraphics());
            float width = Math.Max(1f, ComputeOverlayWidth() - S(32));
            var size = g.MeasureString(metrics!.GameName, LowFont, (int)width);
            return Math.Max(baseHeight, (float)Math.Ceiling(size.Height) + S(4));
        }
        catch { return baseHeight; }
        finally { own?.Dispose(); }
    }

    /// <summary>Ancho reservado al candado vector (ver DrawLockGlyph).</summary>
    private float LockGlyphWidth(Graphics g) => S(12);

    /// <summary>Ancho de un grupo completo: título + valores.</summary>
    private float MeasureRunWidth(Graphics g, string label, List<string> parts)
    {
        float w = g.MeasureString(label + " ", LineFont).Width;
        foreach (var text in parts)
            w += g.MeasureString(text + " ", LineFont).Width;
        return w;
    }

    /// <summary>
    /// Barra horizontal: una sola fila con el candado de estado a la izquierda y
    /// los grupos FPS | CPU | GPU | RAM separados por divisores verticales. Cada
    /// grupo dibuja su TÍTULO al color de la familia y sus valores en blanco, y
    /// TODO el texto queda centrado verticalmente dentro de la barra. El ancho de
    /// la ventana ya fue ajustado al contenido (MeasureHorizontalBarWidth).
    /// </summary>
    private void DrawHorizontalBar(Graphics g, WHPO.Core.Services.Interfaces.OverlayMetrics? metrics,
        bool haveFps)
    {
        float padX = S(BarPadX);
        float midY = _buffer!.Height / 2f;

        // Candado de estado (VECTOR, ver DrawLockGlyph): cerrado = bloqueado
        // (click-through, los clics van al juego), abierto = desbloqueado
        // (arrastrable). Gris cuando está bloqueado; verde tenue cuando está
        // desbloqueado, como el estado en la página.
        using (var lockBrush = new SolidBrush(_locked ? _lockedGlyphColor : _unlockedGlyphColor))
        {
            DrawLockGlyph(g, padX, midY, lockBrush);
        }
        float x = padX + S(18) + LockGlyphWidth(g) + S(10);

        var groups = BuildHorizontalGroups(metrics, haveFps);
        int last = groups.Count - 1;
        for (int i = 0; i < groups.Count; i++)
        {
            var (label, parts) = groups[i];
            float runW = MeasureRunWidth(g, label, parts);
            if (runW <= 0) continue;

            if (i > 0)
                DrawHorizontalDivider(g, x - S(8), S(4), _buffer.Height - S(8));

            // El color de la familia va SOLO en el título del grupo; los valores
            // (las métricas) usan el color general de métricas (setting).
            Color familyColor = label switch
            {
                "FPS" => _config.FpsColor,
                "CPU" => _config.CpuColor,
                "GPU" => _config.GpuColor,
                _ => _config.RamColor
            };
            using var titleBrush = new SolidBrush(familyColor);
            using var white = new SolidBrush(_config.MetricColor);
            var labelSize = g.MeasureString(label, LineFont);
            g.DrawString(label, LineFont, titleBrush, x, midY - labelSize.Height / 2f);
            float cx = x + g.MeasureString(label + " ", LineFont).Width;
            foreach (var text in parts)
            {
                var valueSize = g.MeasureString(text, LineFont);
                g.DrawString(text, LineFont, white, cx, midY - valueSize.Height / 2f);
                cx += g.MeasureString(text + " ", LineFont).Width;
            }
            x += runW + S(16);
        }

        // Nombre del juego AL FINAL de la barra (subtítulo sutil a la derecha,
        // igual que el subtítulo inferior del panel vertical: mismo switch).
        // Lleva su separador vertical, como entre grupos.
        if (HorizontalTitle.Length > 0)
        {
            float tx = x + S(12);
            DrawHorizontalDivider(g, tx - S(8), S(4), _buffer.Height - S(8));
            // Mismo texto secundario del tema que el subtítulo del panel vertical.
            using var titleBrush = new SolidBrush(Color.FromArgb(215, _secondaryColor.R, _secondaryColor.G, _secondaryColor.B));
            // Recortado a lo que sobra hasta el borde derecho: el ancho de la barra
            // se acota al del monitor (MeasureHorizontalBarWidth), así que un nombre
            // de juego larguísimo se salía del buffer y quedaba cortado.
            float maxW = _buffer.Width - padX - tx;
            string title = FitText(g, HorizontalTitle, LowFont, maxW);
            var tSize = g.MeasureString(title, LowFont);
            g.DrawString(title, LowFont, titleBrush, tx, midY - tSize.Height / 2f);
        }
    }
    /// <summary>
    /// Línea de hardware en grilla: nombre en la columna 1, y cada valor en su
    /// columna fija (alineado a la derecha). Mostrar/ocultar un valor NO mueve
    /// a los demás.
    /// </summary>
    private float DrawHardwareLine(Graphics g, string label,
        double usage, double mhz, double temp, double watts,
        bool showUsage, bool showMhz, bool showTemp, bool showWatts,
        Brush brush, float y,
        bool hasLabel = true)
    {
        if (IsHorizontal) return y; // el layout horizontal no dibuja líneas apiladas
        var values = new List<(string Text, float RightX)>();
        if (showUsage) values.Add(($"{usage:F0}%", S(ColUsageRight)));
        if (showMhz && mhz > 0) values.Add(($"{mhz:F0} MHz", S(ColMhzRight)));
        if (showTemp && temp > 0) values.Add(($"{temp:F0}°C", S(ColTempRight)));
        if (showWatts && watts > 0) values.Add(($"{watts:F0} W", S(ColWattsRight)));

        // Sin label (sub-fila movida con el drag, ej. "CPU MHz" sola): NO se
        // reinventa el título del procesador, la línea queda sin nombre.
        string text = hasLabel ? (string.IsNullOrWhiteSpace(label) ? "—" : label) : "";
        DrawLabeledLine(g, text, brush, y, values);
        return y + S(MetricLineHeight);
    }

    /// <summary>
    /// Dibuja un nombre a la izquierda + valores en columnas fijas alineados a
    /// la derecha. El nombre (el TÍTULO de la línea) va al color de la familia
    /// (el brush recibido, que se configura con el selector de color) y los
    /// valores SIEMPRE en blanco. El nombre se recorta según dónde EMPIEZA el
    /// valor más a la izquierda (sus glifos, no el borde de su columna): así
    /// nunca hay texto encima de los valores, ni siquiera con nombres largos.
    /// </summary>
    private void DrawLabeledLine(Graphics g, string label, Brush brush, float y,
        List<(string Text, float RightX)> values)
    {
        // Sin label (sub-fila sin título): los valores NO quedan flotando en sus
        // columnas originales (con el hueco del nombre a la izquierda); el grupo
        // entero se corre a la izquierda para que el primer valor alinee en la
        // COLUMNA 1 (la del uso). La distancia es la misma para todos, así el
        // espaciado entre valores no cambia.
        if (string.IsNullOrEmpty(label))
        {
            float delta = values.Count > 0 ? values[0].RightX - S(ColUsageRight) : 0f;
            using var whiteOnly = new SolidBrush(_config.MetricColor);
            foreach (var (text, rightX) in values)
                DrawRight(g, text, LineFont, whiteOnly, rightX - delta, y);
            return;
        }

        float maxLabelWidth = float.MaxValue;
        if (values.Count > 0)
        {
            // Borde izquierdo del valor más a la izquierda de la línea.
            float minStart = float.MaxValue;
            foreach (var (text, rightX) in values)
                minStart = Math.Min(minStart, rightX - g.MeasureString(text, LineFont).Width);

            maxLabelWidth = minStart - S(PanelPadX) - S(LabelValueGap);
            if (maxLabelWidth < S(24)) maxLabelWidth = S(24);
        }

        g.DrawString(FitText(g, label, LineFont, maxLabelWidth), LineFont, brush, S(PanelPadX), y);
        // Los valores van con el color general de métricas (no blanco fijo).
        using var white = new SolidBrush(_config.MetricColor);
        foreach (var (text, rightX) in values)
            DrawRight(g, text, LineFont, white, rightX, y);
    }

    /// <summary>
    /// Bloque de FPS: línea igual a las de hardware — label "FPS" (con la API
    /// gráfica si está disponible) a la izquierda y el valor alineado a la derecha
    /// en la primera columna de la grilla, con la MISMA fuente y alto de línea.
    /// Debajo, los lows 1% / 0.1% habilitados y el gráfico de frametime. Solo se
    /// dibuja si algún badge del bloque (fps/low1/low01) está activo.
    /// </summary>
    private float DrawFpsBlock(Graphics g, WHPO.Core.Services.Interfaces.OverlayMetrics? metrics,
        bool haveFps, List<string> ids, Brush fpsBrush, float y)
    {
        bool showFps = ids.Contains("fps");
        bool showMaxMin = ids.Contains(FpsMaxMinId);
        bool showLow1 = ids.Contains("low1");
        bool showLow01 = ids.Contains("low01");
        bool showGraph = ids.Contains(FrametimeGraphId);
        if (!showFps && !showMaxMin && !showLow1 && !showLow01 && !showGraph) return y;

        float left = S(PanelPadX);
        if (showFps)
        {
            string api = metrics?.GfxApi ?? "";
            string fpsLabel = api.Length > 0 ? $"FPS {api}" : "FPS";
            string fpsText = haveFps ? metrics!.Fps.ToString("F0") : "--";
            // Igual que las líneas de hardware: label a la izquierda, valor
            // alineado a la derecha en la primera columna de la grilla.
            DrawLabeledLine(g, fpsLabel, fpsBrush, y,
                new List<(string Text, float RightX)> { (fpsText, S(ColUsageRight)) });
            y += S(MetricLineHeight);
        }

        // Máximo (↑) y mínimo (↓) de la sesión: badge propio ("fpsMaxMin"),
        // separado del valor actual del FPS. Línea chica, mismo estilo que los lows.
        if (showMaxMin && metrics != null)
        {
            var stats = new List<string>();
            if (metrics.FpsMax > 0) stats.Add($"↑máx: {metrics.FpsMax:F0}");
            if (metrics.FpsMin > 0) stats.Add($"↓mín: {metrics.FpsMin:F0}");
            if (stats.Count > 0)
            {
                using var statsBrush = new SolidBrush(_config.MetricColor);
                g.DrawString(string.Join("  ", stats), LowFont, statsBrush, left, y);
                y += S(SubLineHeight);
            }
        }

        // 1% low / 0.1% low (chico, sobre la MISMA base vertical que las filas de
        // hardware). Cuando están en filas propias de badges (default) cada fila
        // dibuja su línea; si comparten fila con el FPS quedan debajo del número.
        var lows = new List<string>();
        if (showLow1 && metrics != null && metrics.FpsLow1 > 0)
            lows.Add($"1% low: {metrics.FpsLow1:F0}");
        if (showLow01 && metrics != null && metrics.FpsLow01 > 0)
            lows.Add($"0,1% low: {metrics.FpsLow01:F0}");
        // Los lows y el gráfico son MÉTRICAS: usan el color general de métricas
        // (el color de la familia es solo para el título "FPS ...").
        using var white = new SolidBrush(_config.MetricColor);
        if (lows.Count > 0)
        {
            g.DrawString(string.Join("  ", lows), LowFont, white, left, y);
            y += S(SubLineHeight);
        }

        // Gráfico de frametime (ms) debajo de los lows,
        // dentro del mismo bloque FPS. La serie se lee EN VIVO del monitor de FPS
        // en cada tick de render (16 ms con el gráfico activo), no del snapshot
        // de 500 ms — por eso el gráfico corre fluido y sin saltos.
        if (showGraph)
        {
            var series = _metrics.GetLiveFrametimes(FrametimeGraphSamples);
            y = DrawFrametimeGraph(g, series, white, y);
        }
        return y;
    }

    /// <summary>
    /// Gráfico de frametime (ms): panel inset con la línea del tiempo de frame con
    /// EJE DE TIEMPO REAL DE PARED — cada muestra lleva el timestamp ETW de su
    /// evento (espaciado uniforme, sin apiñarse en ráfagas) calibrado al reloj de
    /// pared, así que X es literalmente "hace cuánto se presentó ese frame": la
    /// línea scrollea continua hacia la izquierda y, si el juego deja de presentar,
    /// el gráfico SE DRENA SOLO en vez de quedar congelado mostrando data vieja.
    /// Incluye relleno bajo la curva con anti-aliasing y el valor actual en ms a la
    /// DERECHA del panel, verticalmente centrado (mientras la señal sea reciente).
    /// Sin grillas ni etiquetas de escala: la línea es la única referencia visual.
    /// La escala vertical tiene histéresis (sube al instante ante picos, baja
    /// lento; múltiplo de 10 ms, mínimo 20): los picos de stutter se ven sin
    /// aplastar la línea ni hacer vibrar la escala.
    /// </summary>
    private float DrawFrametimeGraph(Graphics g, FrametimeSample[] series, Brush lineBrush, float y)
    {
        float x = S(PanelPadX);
        // Área reservada a la derecha del panel para el valor de ms actual
        // (verticalmente centrado, fuera del gráfico).
        float valueArea = S(56);
        float w = (float)_buffer!.Width - S(PanelPadX * 2) - valueArea;
        float h = S(GraphHeight);
        if (w < 40 || h < 12) return y;
        if (IsHorizontal) return y; // el gráfico completo solo existe en el layout vertical

        var rect = new RectangleF(x, y, w, h);

        // Sin card: ni fondo oscuro ni borde — el área del gráfico queda 100%
        // transparente y solo se dibuja la línea sobre el fondo del overlay.

        // Eje X: el frame más nuevo se ancla al borde derecho del panel (como
        // RTSS) y el resto se espacia con el reloj ETW del evento (uniforme entre
        // presents — no se apiña en ráfagas como el reloj de llegada). El hueco
        // entre el último punto y el borde se acota a MaxHuecoMs: cuando una
        // ráfaga llega tarde, el último punto se mantiene a esa distancia del
        // borde y el gráfico nunca se corta. El drenado usa el reloj de pared:
        // si el último evento llegó hace más de SignalGraceMs, el juego dejó de
        // presentar y la señal se limpia.
        double windowMs = FrametimeWindowSeconds * 1000.0;
        int n = series.Length;
        if (n == 0) return y;
        long nowWall = DateTime.UtcNow.Ticks;
        long latestEtw = series[n - 1].EtwTicks;
        long latestWall = series[n - 1].TicksUtc;

        // Edad del último present en reloj de pared (cuánto hace que llegó el
        // evento). Con el juego corriendo, la entrega ETW llega en ráfagas de
        // hasta ~1 s; superada la gracia, la señal se drena.
        double wallAgeMs = (nowWall - latestWall) / 10000.0;
        bool haveSignal = n >= 2 && wallAgeMs <= SignalGraceMs;

        // El ancla del borde derecho: avanza con el reloj de pared mientras la
        // entrega es fresca (scroll fluido) y se detiene a MaxHuecoMs del último
        // punto cuando la ráfaga tarda (sin cortes visibles).
        double anchorAgeMs = Math.Min(wallAgeMs, MaxHuecoMs);

        int start = 0;
        while (start < n && (latestEtw - series[start].EtwTicks) / 10000.0 + anchorAgeMs > windowMs) start++;
        int count = n - start;

        // Escala vertical 0..cap ms con histéresis: "needed" es el máximo visible
        // redondeado a múltiplo de 10 (mínimo 20 ms). Si hace falta más, el tope
        // sube AL INSTANTE (el pico se ve completo); si sobra, baja un 10% por
        // render para que la escala no vibre. Sin señal se resetea.
        double needed = 0;
        for (int i = start; i < n; i++)
            if (series[i].FrameMs > needed) needed = series[i].FrameMs;
        needed = Math.Max(20.0, Math.Ceiling(needed / 10.0) * 10.0);
        if (!haveSignal) _graphCapMs = 0;
        else if (_graphCapMs < needed) _graphCapMs = needed;
        else _graphCapMs = needed + (_graphCapMs - needed) * 0.9;
        double cap = _graphCapMs > 0 ? _graphCapMs : needed;
        float ToY(double ms) => rect.Bottom - (float)(Math.Min(ms, cap) / cap * rect.Height);
        // X = hace cuánto se presentó ese frame (espaciado ETW + ancla del borde).
        float ToX(double agoMs) => rect.Right - (float)(agoMs / windowMs * rect.Width);

        // Anti-aliasing en línea y relleno: sin él la curva se ve dentada a esta
        // cadencia de render. Sin grillas ni etiquetas de escala (la línea es la
        // única referencia visual).
        var prevSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            if (haveSignal)
            {
                var pts = new PointF[count];
                for (int k = 0; k < count; k++)
                {
                    double agoMs = (latestEtw - series[start + k].EtwTicks) / 10000.0 + anchorAgeMs;
                    pts[k] = new PointF(ToX(agoMs), ToY(series[start + k].FrameMs));
                }

                var c = lineBrush is SolidBrush sb ? sb.Color : Color.White;

                // Solo la línea (sin relleno bajo la curva: el área debajo queda
                // 100% transparente).
                using (var line = new Pen(c, MathF.Max(1f, S(1.6f))))
                    g.DrawLines(line, pts);
            }
        }
        finally
        {
            g.SmoothingMode = prevSmoothing;
        }

        // Valor actual: frametime del frame más nuevo, a la DERECHA del panel y
        // verticalmente centrado. Se muestra mientras la señal sea reciente
        // (misma gracia que la línea): la entrega ETW llega en ráfagas de hasta
        // ~1 s, así que un umbral menor hacía parpadear el valor con el juego
        // corriendo; al pausar el juego, la edad del último present crece y el
        // valor se oculta solo.
        if (wallAgeMs <= SignalGraceMs)
        {
            string txt = $"{series[n - 1].FrameMs:F1} ms";
            var size = g.MeasureString(txt, LowFont);
            float vx = rect.Right + S(6);
            float vy = rect.Top + (rect.Height - size.Height) / 2f;
            g.DrawString(txt, LowFont, lineBrush, vx, vy);
        }

        return y + h + S(GraphGap);
    }

    /// <summary>
    /// Agrupa los ids de métricas en líneas de render (cpu/gpu/ram/fps) preservando
    /// las FILAS de badges de la configuración: cada fila de badges es una línea de
    /// la superposición. Dentro de una fila, cada familia se convierte en su propia
    /// línea de render en el orden de aparición (una fila mixta se dibuja como
    /// varias líneas, en el orden de sus badges).
    /// </summary>
    private static List<(string Family, List<string> Ids)> BuildRenderRows(List<List<string>> rows)
    {
        var render = new List<(string Family, List<string> Ids)>();
        foreach (var row in rows)
        {
            // Separar la fila por familia, en el orden de aparición dentro de ella.
            var byFamily = new List<(string Family, List<string> Ids)>();
            foreach (var id in row)
            {
                string family = FamilyOf(id);
                if (family.Length == 0) continue;
                var existing = byFamily.FindIndex(r => r.Family == family);
                if (existing < 0)
                {
                    var r = (Family: family, Ids: new List<string>());
                    r.Ids.Add(id);
                    byFamily.Add(r);
                }
                else
                {
                    byFamily[existing].Ids.Add(id);
                }
            }
            render.AddRange(byFamily);
        }
        return render;
    }

    /// <summary>
    /// Agrupa un orden plano de ids en filas por familia (CPU/GPU/RAM/FPS)
    /// preservando el orden de aparición. Se usa para migrar el formato viejo
    /// ("overlay.metricOrder" plano) al nuevo ("overlay.metricRows") tanto en la
    /// página de configuración como acá.
    /// </summary>
    public static List<List<string>> GroupByFamily(IEnumerable<string> order)
    {
        var rows = new List<List<string>>();
        foreach (var id in order)
        {
            string family = FamilyOf(id);
            if (family.Length == 0) continue;
            int existing = rows.FindIndex(r => r.Count > 0 && FamilyOf(r[0]) == family);
            if (existing < 0)
            {
                var row = new List<string>();
                row.Add(id);
                rows.Add(row);
            }
            else
            {
                rows[existing].Add(id);
            }
        }
        return rows;
    }

    private static string FamilyOf(string id) => id switch
    {
        "fps" or FpsMaxMinId or "low1" or "low01" or FrametimeGraphId => "fps",
        _ when id.StartsWith("cpu", StringComparison.Ordinal) => "cpu",
        _ when id.StartsWith("gpu", StringComparison.Ordinal) => "gpu",
        _ when id.StartsWith("ram", StringComparison.Ordinal) => "ram",
        _ => ""
    };

    /// <summary>Grupo de una métrica: "cpu", "gpu", "ram" o "fps".</summary>
    public static string GroupOf(string id) => FamilyOf(id);

    /// <summary>Métrica core que define la línea de un grupo (no se puede mover).
    /// null si el grupo no existe.</summary>
    public static string? CoreOf(string group) => group switch
    {
        "cpu" => "cpuUsage",
        "gpu" => "gpuUsage",
        "ram" => "ramMb",
        "fps" => "fps",
        _ => null
    };

    /// <summary>¿Es la métrica core de su grupo (la %, por ej. CPU %)?</summary>
    public static bool IsCoreMetric(string id) => id == CoreOf(GroupOf(id));

    /// <summary>Orden por defecto de los grupos de la barra horizontal
    /// (de izquierda a derecha), igual al orden clásico de render.</summary>
    public static readonly string[] DefaultHGroupOrder = { "fps", "cpu", "gpu", "ram" };

    /// <summary>Normaliza el orden de grupos guardado ("overlay.hGroupOrder"):
    /// conserva solo ids de familia válidos, sin duplicados, y agrega al final
    /// los grupos que falten en el orden por defecto. Lo usan ReadConfig (al
    /// leer el overlay) y la página (al guardar tras el drag de las cards).</summary>
    public static List<string> NormalizeGroupOrder(IEnumerable<string>? order)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        if (order != null)
        {
            foreach (var g in order)
                if (DefaultHGroupOrder.Contains(g, StringComparer.Ordinal) && seen.Add(g))
                    result.Add(g);
        }
        foreach (var g in DefaultHGroupOrder)
            if (seen.Add(g))
                result.Add(g);
        return result;
    }

    /// <summary>
    /// Normaliza las filas de métricas a la invariante del overlay:
    /// - cada fila es de UN solo grupo (cpu/gpu/ram/fps),
    /// - los grupos aparecen en orden fijo cpu → gpu → ram → fps,
    /// - la métrica core (la %) encabeza la primera fila de su grupo,
    /// - las sub-filas del grupo siguen a la fila core (preservando el orden).
    /// Separa filas mezcladas de configs viejas y corrige métricas mal ubicadas
    /// (ej. cpuUsage que quedó "abajo" en la zona de la GPU se devuelve arriba).
    /// </summary>
    public static List<List<string>> NormalizeRows(List<List<string>> rows)
    {
        var order = new[] { "cpu", "gpu", "ram", "fps" };
        var byGroup = new Dictionary<string, List<(string Id, int R, int C)>>();
        foreach (var g in order) byGroup[g] = new List<(string, int, int)>();
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < rows[r].Count; c++)
            {
                var id = rows[r][c];
                if (!IsValidMetricId(id)) continue;
                byGroup[GroupOf(id)].Add((id, r, c));
            }

        var result = new List<List<string>>();
        foreach (var g in order)
        {
            var items = byGroup[g];
            if (items.Count == 0) continue;
            items.Sort((a, b) => a.R != b.R ? a.R.CompareTo(b.R) : a.C.CompareTo(b.C));

            // El grupo FPS no se reordena con el core adelante: se respeta el
            // orden de filas configurado (por defecto el FPS queda debajo de
            // todo, después de los lows). Solo se garantiza una familia por fila.
            if (g == "fps")
            {
                foreach (var grp in items.GroupBy(i => i.R).OrderBy(k => k.Key))
                    result.Add(grp.Select(i => i.Id).ToList());
                continue;
            }

            var core = CoreOf(g);
            if (core != null && items.Any(i => i.Id == core))
            {
                var coreItem = items.First(i => i.Id == core);
                // Primera fila: el core primero + los que estaban en su misma fila.
                var first = new List<string> { core };
                foreach (var it in items)
                    if (it.R == coreItem.R && it.Id != core) first.Add(it.Id);
                result.Add(first);
                // Sub-filas: el resto, agrupado por su fila original en orden.
                foreach (var grp in items.Where(i => i.R != coreItem.R)
                                        .GroupBy(i => i.R).OrderBy(k => k.Key))
                    result.Add(grp.Select(i => i.Id).ToList());
                continue;
            }

            // Sin core (grupo sin su métrica principal): filas planas de a 4.
            var flat = items.Select(i => i.Id).ToList();
            for (int i = 0; i < flat.Count; i += 4)
                result.Add(flat.Skip(i).Take(4).ToList());
        }
        return result;
    }

    /// <summary>
    /// Separa FPS, 1% low y 0.1% low en filas propias (orden por defecto): el
    /// FPS encabeza el bloque y los lows quedan debajo de él.
    /// </summary>
    public static List<List<string>> SplitFpsAndLows(List<List<string>> rows)
    {
        var keep = rows.Select(r => r.Where(id => id is not ("fps" or FpsMaxMinId or "low1" or "low01")).ToList())
                       .Where(r => r.Count > 0).ToList();
        if (rows.Any(r => r.Contains("fps"))) keep.Add(new List<string> { "fps" });
        if (rows.Any(r => r.Contains(FpsMaxMinId))) keep.Add(new List<string> { FpsMaxMinId });
        if (rows.Any(r => r.Contains("low1"))) keep.Add(new List<string> { "low1" });
        if (rows.Any(r => r.Contains("low01"))) keep.Add(new List<string> { "low01" });
        return keep;
    }

    /// <summary>
    /// Migración a los badges NUEVOS (FPS máx/mín separado del FPS, VRAM usada/total
    /// y RAM %): un id que no está en las filas guardadas es de una configuración
    /// previa a que el badge existiera → se activa por defecto si su familia ya
    /// estaba activa (mantiene lo que el overlay venía mostrando). Una vez que el
    /// usuario guarda —o apaga el badge— el id queda en las filas y no se vuelve a
    /// inyectar. La usan la página (al cargar los badges) y la sonda de tests.
    /// </summary>
    public static List<string> MigrateNewBadgeDefaults(IReadOnlyList<List<string>> rows, IEnumerable<string> enabled)
    {
        var list = enabled.Where(IsValidMetricId).Distinct(StringComparer.Ordinal).ToList();
        foreach (var (id, family) in NewBadgeDefaults)
        {
            if (list.Contains(id) || rows.Any(r => r.Contains(id))) continue;
            if (list.Any(x => GroupOf(x) == family)) list.Add(id);
        }
        return list;
    }

    // Badges nuevos con su familia: se activan en la migración si la familia ya
    // estaba activa (ver MigrateNewBadgeDefaults).
    private static readonly (string Id, string Family)[] NewBadgeDefaults =
    {
        (FpsMaxMinId, "fps"),
        (GpuMemId, "gpu"),
        (RamPercentId, "ram")
    };

    /// <summary>Recorta un texto con elipsis si excede el ancho disponible.</summary>
    private static string FitText(Graphics g, string text, Font font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return text;
        if (g.MeasureString(text, font).Width <= maxWidth) return text;

        const string ellipsis = "…";
        var t = text;
        while (t.Length > 1 && g.MeasureString(t + ellipsis, font).Width > maxWidth)
            t = t[..^1];
        return t + ellipsis;
    }

    /// <summary>Dibuja texto alineado a la derecha en la columna indicada.</summary>
    private void DrawRight(Graphics g, string text, Font font, Brush brush, float rightX, float y)
    {
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush, rightX - size.Width, y);
    }

    /// <summary>
    /// Alto necesario para dibujar el estado + todas las líneas de métricas
    /// (según las filas de badges configuradas) + el nombre del juego, con la
    /// escala de letra actual.
    /// </summary>
    private int ComputeOverlayHeight()
    {
        // Barra horizontal: alto fijo (una sola línea).
        if (IsHorizontal) return (int)Math.Round(HorizontalHeight * _config.FontScale);

        // Los avances salen de las MISMAS constantes que usa el dibujado (ver el bloque
        // "Paleta del TEMA y ritmo vertical"): si acá hubiera literales, tocar el dibujo dejaba
        // el alto desincronizado (texto recortado o franja vacía al pie).
        float y = S(PadTop) + S(StateLineHeight); // margen superior + línea de estado
        foreach (var row in BuildRenderRows(_config.MetricRows))
        {
            switch (row.Family)
            {
                case "fps":
                    if (row.Ids.Contains("fps")) y += S(MetricLineHeight); // línea igual a las de hardware
                    if (row.Ids.Contains(FpsMaxMinId)) y += S(SubLineHeight); // sub-línea máx/mín
                    if (row.Ids.Contains("low1") || row.Ids.Contains("low01")) y += S(SubLineHeight);
                    if (row.Ids.Contains(FrametimeGraphId)) y += S(GraphHeight + GraphGap);
                    break;
                case "gpu":
                    // Línea de hardware (si la fila trae métricas del GPU) + VRAM.
                    if (row.Ids.Any(id => id != GpuMemId)) y += S(MetricLineHeight);
                    if (row.Ids.Contains(GpuMemId)) y += S(SubLineHeight);
                    break;
                case "ram":
                case "cpu":
                    y += S(MetricLineHeight);
                    break;
            }
        }
        y += Math.Max(S(16), _gameTitleHeight); // nombre del juego (puede ocupar varias líneas)
        return Math.Max(MinOverlayHeight, (int)Math.Round(y + S(10)));
    }

    private void EnsureBuffer()
    {
        // En horizontal el ancho medido ya viene escalado (ver ComputeOverlayWidth).
        int w = ComputeOverlayWidth();
        int h = ComputeOverlayHeight();
        if (_buffer != null && _buffer.Width == w && _buffer.Height == h) return;
        _buffer?.Dispose();
        _bufferGraphics?.Dispose();
        _buffer = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        _bufferGraphics = Graphics.FromImage(_buffer);
        _bufferGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _bufferGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Size.Width != w || Size.Height != h)
        {
            Size = new Size(w, h);
            // Cambio de layout o de escala de letra: la posición vieja puede no
            // servir más. Se re-acota al monitor más cercano y, si la ventana no
            // entra completa, se ancla a la esquina SEGURA del layout (horizontal
            // → arriba a la izquierda; vertical → arriba a la derecha).
            var area = Screen.FromPoint(Location).WorkingArea;
            int nx = Math.Max(area.Left, Math.Min(Location.X, area.Right - Width));
            int ny = Math.Max(area.Top, Math.Min(Location.Y, area.Bottom - Height));
            const int m = CornerMargin;
            if (Width > area.Width)
                nx = IsHorizontal ? area.Left + m : area.Right - (int)Math.Round(OverlayWidth * _config.FontScale) - m;
            if (Height > area.Height)
                ny = area.Top + m;
            Location = new Point(nx, ny);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            // Panel rectangular: sin esquinas transparentes (las "puntas blancas"
            // que aparecían sobre contenido brillante con las esquinas redondeadas).
            path.AddRectangle(bounds);
            return path;
        }
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Rectángulo redondeado en float (para glifos chicos como el candado).</summary>
    private static GraphicsPath RoundedRectF(RectangleF b, float r)
    {
        var path = new GraphicsPath();
        float d = r * 2;
        path.AddArc(b.X, b.Y, d, d, 180, 90);
        path.AddArc(b.Right - d, b.Y, d, d, 270, 90);
        path.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        path.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Candado de estado dibujado con primitivas GDI+ (cuerpo + gancho en arco).
    /// NO va con fuente: los glifos emoji 🔒/🔓 son caracteres astrales (fuera del
    /// BMP) y GDI+ los renderiza como "?" con Consolas, con fallback poco
    /// confiable incluso apuntando a Segoe UI Symbol. Un candado vector es
    /// determinista y escala con la letra (S()). Cerrado = gancho apoyado sobre
    /// el cuerpo con ambas patas enganchadas; abierto = gancho alzado con la pata
    /// derecha suelta (no llega al cuerpo).
    /// </summary>
    private void DrawLockGlyph(Graphics g, float x, float centerY, Brush brush)
    {
        float w = S(10);
        float totalH = S(12);
        float y = centerY - totalH / 2f;
        float bodyTop = y + S(5.5f);
        float shackleW = S(5.5f);
        float shackleH = S(5.5f);

        var prevSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var pen = new Pen(brush, MathF.Max(1f, S(1.4f)));
            if (_locked)
            {
                // Cerrado: arco apoyado sobre el cuerpo, ambas patas bajan hasta él.
                var arc = new RectangleF(x + (w - shackleW) / 2f, y, shackleW, shackleH);
                g.DrawArc(pen, arc, 180, 180);
                float cy = arc.Top + arc.Height / 2f;
                g.DrawLine(pen, arc.Left, cy, arc.Left, bodyTop);
                g.DrawLine(pen, arc.Right, cy, arc.Right, bodyTop);
            }
            else
            {
                // Abierto: el arco sube y corre a la derecha; la pata izquierda
                // sigue llegando al cuerpo, la derecha queda suelta en el aire.
                var arc = new RectangleF(x + (w - shackleW) / 2f + S(1), y - S(2), shackleW, shackleH);
                g.DrawArc(pen, arc, 180, 180);
                float cy = arc.Top + arc.Height / 2f;
                g.DrawLine(pen, arc.Left, cy, arc.Left, bodyTop);
                g.DrawLine(pen, arc.Right, cy, arc.Right, cy + S(1.2f));
            }

            // Cuerpo redondeado relleno + keyhole (punto) al color oscuro del panel.
            var body = new RectangleF(x, bodyTop, w, totalH - S(5.5f));
            using var bodyPath = RoundedRectF(body, S(1.3f));
            g.FillPath(brush, bodyPath);
            // Keyhole: un agujero del COLOR DEL PANEL (antes un gris fijo que no coincidía con el
            // tema).
            using var holeBrush = new SolidBrush(Color.FromArgb(235, _panelColor.R, _panelColor.G, _panelColor.B));
            float hole = S(1.9f);
            g.FillEllipse(holeBrush,
                x + w / 2f - hole / 2f, bodyTop + body.Height / 2f - hole / 2f, hole, hole);
        }
        finally
        {
            g.SmoothingMode = prevSmoothing;
        }
    }

    private void PaintLayered()
    {
        IntPtr hdcScreen = IntPtr.Zero, hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero, hOld = IntPtr.Zero;
        try
        {
            hdcScreen = GetDC(IntPtr.Zero);
            hdcMem = CreateCompatibleDC(hdcScreen);

            // DIB de 32 bits creado a mano: GetHbitmap rellenaba los píxeles
            // semi-transparentes con el color de fondo (Color.Transparent = BLANCO),
            // así que al bajar la opacidad el fondo se volvía blanco en vez de
            // transparentarse. Acá se copian los píxeles premultiplicados tal cual
            // (el buffer es Format32bppPArgb) y UpdateLayeredWindow los compone bien.
            int w = _buffer!.Width, h = _buffer.Height;
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_BITFIELDS // 32bpp con alpha
                },
                RedMask = 0x00FF0000,
                GreenMask = 0x0000FF00,
                BlueMask = 0x000000FF
            };
            hBitmap = CreateDIBSection(hdcMem, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return;
            hOld = SelectObject(hdcMem, hBitmap);

            var data = _buffer.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int len = Math.Abs(data.Stride) * h;
                var pixels = new byte[len];
                Marshal.Copy(data.Scan0, pixels, 0, len);
                Marshal.Copy(pixels, 0, bits, len);
            }
            finally
            {
                _buffer.UnlockBits(data);
            }

            var dst = new POINT { X = Location.X, Y = Location.Y };
            var size = new SIZE { Width = w, Height = h };
            var src = new POINT { X = 0, Y = 0 };
            // El texto se dibuja al 100% de alpha en el bitmap y la opacidad está
            // horneada en el fondo → sin atenuación global (siempre legible).
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };
            UpdateLayeredWindow(Handle, hdcScreen, ref dst, ref size, hdcMem, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (hOld != IntPtr.Zero) SelectObject(hdcMem, hOld);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                try { UnregisterHotkeys(); } catch { }
                StopRendering();
                _posSaveTimer?.Dispose();
                _bufferGraphics?.Dispose();
                _buffer?.Dispose();
                _lowFont?.Dispose();
                _lineFont?.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
