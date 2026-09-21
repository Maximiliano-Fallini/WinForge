using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WHPO.Core.Services.Interfaces;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace WHPO_UI.Overlay;

/// <summary>
/// Configuración del overlay leída desde los settings (claves "overlay.*").
/// </summary>
public sealed record OverlayConfig(
    bool ShowFps,
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
    bool ShowRam,
    double Opacity,
    double FontScale,
    // "vertical" (panel clásico) u "horizontal" (barra compacta de una línea).
    string Layout,
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
    private const int OverlayWidth = 360;
    private const int MinOverlayHeight = 120;

    // LAYOUTS: "vertical" (panel clásico, filas apiladas) y "horizontal" (barra
    // ancha: una sola fila de métricas compactas).
    public const string LayoutVertical = "vertical";
    public const string LayoutHorizontal = "horizontal";

    /// <summary>Verde por defecto de los títulos de familia (CPU/GPU/RAM/FPS) del
    /// overlay: mismo verde del estado "desbloqueado" de la barra.</summary>
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

    /// <summary>¿Mostrar el nombre del juego al final de la barra horizontal?
    /// Obedece el mismo switch "overlay.showGameTitle" que el panel vertical.</summary>
    private bool ShowGameTitleEnabled(WHPO.Core.Services.Interfaces.OverlayMetrics? metrics, bool haveFps)
        => _config.ShowGameTitle && haveFps && metrics != null
            && !string.IsNullOrWhiteSpace(metrics.GameName)
            && metrics.GameName != "WinForge";

    /// <summary>¿El overlay está en el layout horizontal (barra compacta)?</summary>
    private bool IsHorizontal => string.Equals(_config.Layout, LayoutHorizontal, StringComparison.Ordinal);

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
    // Etiqueta ("CPU", "GPU"...) en gris apagado; los valores al 100% del color.
    private const int LabelGray = 155;
    // Separadores verticales entre familias: gris claro con alpha alta — tienen
    // que NOTARSE (antes eran tan tenues que se confundían con el panel).
    private const int DividerAlpha = 150;
    private const int DividerLighten = 90;

    /// <summary>Separador vertical de la barra (línea corta y centrada).</summary>
    private void DrawHorizontalDivider(Graphics g, float x, float y, float height)
    {
        using var pen = new Pen(Color.FromArgb(DividerAlpha,
            15 + DividerLighten, 15 + DividerLighten, 18 + DividerLighten), 1f);
        g.DrawLine(pen, x, y + height * 0.18f, x, y + height * 0.82f);
    }

    // Columnas de la grilla de hardware, alineadas a la derecha (sin escala):
    // usage / mhz / temp / watts. Medidas con la fuente real (Consolas bold 12.5px):
    // "100%"=32.5, "5299 MHz"=60.8, "89°C"=32.5, "120 W"=39.6 → con gaps de 22px
    // entre columnas y 16px de margen derecho (overlay de 360px), la fila completa
    // de 4 valores queda aireada y el peor caso (3 dígitos de watts) nunca se pega
    // al valor anterior ni al borde.
    private const float ColUsageRight = 144;
    private const float ColMhzRight = 227;
    private const float ColTempRight = 282;
    private const float ColWattsRight = 344;

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
                const int m = 16;
                if (IsHorizontal)
                {
                    x = primary.Left + m;               // horizontal: arriba a la IZQUIERDA
                    y = primary.Top + m;
                }
                else
                {
                    x = primary.Right - Width - m;      // vertical: arriba a la DERECHA
                    y = primary.Top + m;
                }
            }

            // Acotar la posición para que la ventana quede COMPLETA dentro del área de
            // trabajo del monitor más cercano (ver ClampToNearestScreen).
            Location = new Point(x, y);
            ClampToNearestScreen();
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
    /// "top-left", "bottom-right", "bottom-left") y guarda la posición.
    /// </summary>
    public void SetCorner(string corner)
    {
        try
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int x, y;
            const int margin = 16;
            switch (corner)
            {
                case "top-left":
                    x = area.Left + margin; y = area.Top + margin; break;
                case "bottom-right":
                    x = area.Right - Width - margin; y = area.Bottom - Height - margin; break;
                case "bottom-left":
                    x = area.Left + margin; y = area.Bottom - Height - margin; break;
                default: // top-right
                    x = area.Right - Width - margin; y = area.Top + margin; break;
            }
            Location = new Point(x, y);
            _settings.Set("overlay.posX", x);
            _settings.Set("overlay.posY", y);
            _settings.Save();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayWindow: no se pudo ubicar en la esquina: {ex.Message}");
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
            ShowRam: allIds.Any(m => m.StartsWith("ram", StringComparison.Ordinal)),
            // La opacidad baja hasta 0 (fondo totalmente transparente).
            Opacity: Math.Clamp(d("overlay.opacity", 0.85), 0.0, 1.0),
            FontScale: Math.Clamp(d("overlay.fontSize", 1.4), 0.6, 2.0),
            Layout: _settings.Get("overlay.layout", LayoutVertical),
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
        "fps" or "low1" or "low01" or FrametimeGraphId or "cpuUsage" or "cpuMhz" or "cpuTemp" or "cpuWatts"
            or "gpuUsage" or "gpuMhz" or "gpuTemp" or "gpuWatts" or "ramMb" or "ramMhz" => true,
        _ => false
    };

    // Id del badge del gráfico de frametime (compartido con OverlayPage).
    public const string FrametimeGraphId = "latencyGraph";

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

    private void Render()
    {
        if (_disposed || !IsHandleCreated || !Visible) return;
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
            if (_configDirty)
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

            EnsureBuffer();

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
            int bgAlpha = (int)(_config.Opacity * 255);
            // Barra horizontal: radio más suave (el alto es chico y un radio
            // grande comería media barra).
            int radius = IsHorizontal
                ? (int)MathF.Max(4f, 8f * (float)_config.FontScale)
                : (int)MathF.Max(6f, 12f * (float)_config.FontScale);
            using (var path = RoundedRect(new Rectangle(0, 0, _buffer!.Width, _buffer.Height), radius))
            using (var bgBrush = new SolidBrush(Color.FromArgb(bgAlpha, 15, 15, 18)))
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
            }

            float y = S(8);
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
            using (var stateBrush = new SolidBrush(_locked
                       ? Color.FromArgb(215, 205, 205, 210)
                       : Color.FromArgb(215, 120, 220, 140)))
            {
                DrawLockGlyph(g, ((float)_buffer!.Width - LockGlyphWidth(g)) / 2f, y + S(7), stateBrush);
            }
            y += S(21);

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
                        y = DrawHardwareLine(g, metrics.GpuName, metrics.GpuUsagePercent, metrics.GpuMhz,
                            metrics.GpuTempCelsius, metrics.GpuWatts,
                            row.Ids.Contains("gpuUsage"), row.Ids.Contains("gpuMhz"),
                            row.Ids.Contains("gpuTemp"), row.Ids.Contains("gpuWatts"),
                            gpuBrush, y, showName);
                        break;
                    case "ram" when metrics != null:
                    {
                        // RAM: el nombre es "RAM" seguido de la configuración de
                        // módulos (RAM 2x16 GB), después el uso en MB y la velocidad.
                        // En sub-filas sin core (ej. "RAM MHz" movida abajo) no hay
                        // nombre: solo la línea de valores.
                        var ramValues = new List<(string Text, float RightX)>();
                        if (row.Ids.Contains("ramMb"))
                            ramValues.Add(($"{metrics.RamUsedMb:F0} MB", S(ColUsageRight)));
                        if (row.Ids.Contains("ramMhz") && metrics.RamMhz > 0)
                            ramValues.Add(($"{metrics.RamMhz:F0} MHz", S(ColMhzRight)));

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
            // switch de la página de apariencia lo habilita.
            if (_config.ShowGameTitle && haveFps && !string.IsNullOrEmpty(gameName) && gameName != "WinForge")
            {
                using var gameBrush = new SolidBrush(Color.FromArgb(180, 180, 180));
                g.DrawString(gameName, LowFont, gameBrush, S(16), y);
            }

            PaintLayered();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayWindow: error de render: {ex.Message}");
        }
    }

    // ===== Layout horizontal: barra compacta =====

    // ===== Grupos de la barra horizontal =====

    private static string IdToHGroup(string id) => id switch
    {
        "fps" or "fpsMax" or "fpsMin" or "low1" or "low01" or FrametimeGraphId => "fps",
        "cpuUsage" or "cpuTemp" or "cpuMhz" or "cpuWatts" => "cpu",
        "gpuUsage" or "gpuTemp" or "gpuMhz" or "gpuWatts" or "gpuMem" => "gpu",
        "ramUsed" or "ramMhz" => "ram",
        _ => ""
    };

    private static bool IsHGroupCore(string id) =>
        id is "fps" or "cpuUsage" or "gpuUsage" or "ramUsed";

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

        // FPS: actual + máximo (↑) y mínimo (↓) de la sesión (si ya hay señal).
        var fpsParts = new List<string>
        {
            haveFps && metrics != null ? metrics.Fps.ToString("F0") : "--"
        };
        if (metrics != null && metrics.FpsMax > 0)
            fpsParts.Add($"↑{metrics.FpsMax:F0}");
        if (metrics != null && metrics.FpsMin > 0)
            fpsParts.Add($"↓{metrics.FpsMin:F0}");
        byKey["fps"] = ("FPS", fpsParts);

        // CPU: % de uso, temperatura y GHz actuales.
        var cpuParts = new List<string>();
        if (metrics != null)
        {
            if (_config.CpuUsage) cpuParts.Add($"{metrics.CpuUsagePercent:F0}%");
            if (_config.CpuTemp && metrics.CpuTempCelsius > 0)
                cpuParts.Add($"{metrics.CpuTempCelsius:F0}°C");
            if (_config.CpuMhz && metrics.CpuMhz > 0)
                cpuParts.Add($"{metrics.CpuMhz / 1000.0:0.#} GHz");
        }
        byKey["cpu"] = ("CPU", cpuParts);

        // GPU: % de uso, temperatura y VRAM usada/total.
        var gpuParts = new List<string>();
        if (metrics != null)
        {
            if (_config.GpuUsage) gpuParts.Add($"{metrics.GpuUsagePercent:F0}%");
            if (_config.GpuTemp && metrics.GpuTempCelsius > 0)
                gpuParts.Add($"{metrics.GpuTempCelsius:F0}°C");
            if (metrics.GpuVramTotalMb > 0)
                gpuParts.Add($"{metrics.GpuMemUsedMb / 1024.0:0.#}/{metrics.GpuVramTotalMb / 1024.0:0.#} GB");
            else if (metrics.GpuMemUsedMb > 0)
                gpuParts.Add($"{metrics.GpuMemUsedMb / 1024.0:0.#} GB");
        }
        byKey["gpu"] = ("GPU", gpuParts);

        // RAM: uso actual / total.
        var ramParts = new List<string>();
        if (metrics != null)
        {
            if (metrics.RamTotalMb > 0)
                ramParts.Add($"{metrics.RamUsedMb / 1024.0:0.#}/{metrics.RamTotalMb / 1024.0:0.#} GB");
            else
                ramParts.Add($"{metrics.RamUsedMb / 1024.0:0.#} GB");
        }
        byKey["ram"] = ("RAM", ramParts);

        // Emitir los grupos en el orden configurado (overlay.hGroupOrder):
        // NormalizeGroupOrder garantiza que los 4 estén presentes sin duplicados.
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
        int w = (int)Math.Round(x);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return Math.Clamp(w, HorizontalMinWidth, Math.Max(HorizontalMinWidth, area.Width - 32));
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
        float padX = S(14);
        float midY = _buffer!.Height / 2f;

        // Candado de estado (VECTOR, ver DrawLockGlyph): cerrado = bloqueado
        // (click-through, los clics van al juego), abierto = desbloqueado
        // (arrastrable). Gris cuando está bloqueado; verde tenue cuando está
        // desbloqueado, como el estado en la página.
        using (var lockBrush = new SolidBrush(_locked
                   ? Color.FromArgb(215, 205, 205, 210)
                   : Color.FromArgb(215, 120, 220, 140)))
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
            using var titleBrush = new SolidBrush(Color.FromArgb(215, 210, 210, 215));
            var tSize = g.MeasureString(HorizontalTitle, LowFont);
            g.DrawString(HorizontalTitle, LowFont, titleBrush, tx, midY - tSize.Height / 2f);
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
        return y + S(25);
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

            maxLabelWidth = minStart - S(12) - S(8);
            if (maxLabelWidth < S(24)) maxLabelWidth = S(24);
        }

        g.DrawString(FitText(g, label, LineFont, maxLabelWidth), LineFont, brush, S(12), y);
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
        bool showLow1 = ids.Contains("low1");
        bool showLow01 = ids.Contains("low01");
        bool showGraph = ids.Contains(FrametimeGraphId);
        if (!showFps && !showLow1 && !showLow01 && !showGraph) return y;

        float left = S(12);
        if (showFps)
        {
            string api = metrics?.GfxApi ?? "";
            string fpsLabel = api.Length > 0 ? $"FPS {api}" : "FPS";
            string fpsText = haveFps ? metrics!.Fps.ToString("F0") : "--";
            // Igual que las líneas de hardware: label a la izquierda, valor
            // alineado a la derecha en la primera columna de la grilla.
            DrawLabeledLine(g, fpsLabel, fpsBrush, y,
                new List<(string Text, float RightX)> { (fpsText, S(ColUsageRight)) });
            y += S(25);
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
            y += S(24);
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
        float x = S(12);
        // Área reservada a la derecha del panel para el valor de ms actual
        // (verticalmente centrado, fuera del gráfico).
        float valueArea = S(56);
        float w = (float)_buffer!.Width - S(24) - valueArea;
        float h = S(50);
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

        return y + h + S(8);
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
        "fps" or "low1" or "low01" or FrametimeGraphId => "fps",
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
        var keep = rows.Select(r => r.Where(id => id is not ("fps" or "low1" or "low01")).ToList())
                       .Where(r => r.Count > 0).ToList();
        if (rows.Any(r => r.Contains("fps"))) keep.Add(new List<string> { "fps" });
        if (rows.Any(r => r.Contains("low1"))) keep.Add(new List<string> { "low1" });
        if (rows.Any(r => r.Contains("low01"))) keep.Add(new List<string> { "low01" });
        return keep;
    }

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

        float y = S(8) + S(21); // margen superior + línea de estado
        foreach (var row in BuildRenderRows(_config.MetricRows))
        {
            switch (row.Family)
            {
                case "fps":
                    if (row.Ids.Contains("fps")) y += S(25); // línea igual a las de hardware
                    if (row.Ids.Contains("low1") || row.Ids.Contains("low01")) y += S(24);
                    if (row.Ids.Contains(FrametimeGraphId)) y += S(58); // gráfico (50) + gap (8)
                    break;
                case "ram":
                case "cpu":
                case "gpu":
                    y += S(25);
                    break;
            }
        }
        y += S(16); // nombre del juego
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
            const int m = 16;
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
            using var holeBrush = new SolidBrush(Color.FromArgb(235, 38, 40, 46));
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
