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
    Color FpsColor,
    Color CpuColor,
    Color GpuColor,
    Color RamColor,
    // FILAS de métricas del overlay (badges arrastrables de la página): cada fila
    // de badges de la configuración es una línea de la superposición. Cada fila
    // contiene ids; dentro de una fila, los ids se dibujan en el orden indicado y
    // solo los habilitados. El overlay dibuja las filas una tras otra.
    List<List<string>> MetricRows);

/// <summary>
/// Ventana del overlay de métricas de juegos: una ventana top-most sin bordes, sin
/// foco y con transparencia por píxel (UpdateLayeredWindow) que se dibuja encima de
/// cualquier juego. Cuando está BLOQUEADA es click-through (los clics pasan al juego);
/// cuando está DESBLOQUEADA recibe el mouse y se puede arrastrar para ubicarla.
///
/// El render corre con un timer propio de 250 ms: dibuja el fondo redondeado
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

    // Dimensiones del overlay: el ancho es fijo y el alto se calcula según la
    // cantidad de líneas de métricas (cada fila de badges = una línea).
    private const int OverlayWidth = 330;
    private const int MinOverlayHeight = 120;

    // Columnas de la grilla de hardware, alineadas a la derecha (sin escala):
    // usage / mhz / temp / watts. Medidas con la fuente real (Consolas bold 12.5px):
    // "100%"=32.5, "5299 MHz"=60.8, "89°C"=32.5, "120 W"=39.6 → con gaps de 14px
    // entre columnas y 10px de margen derecho, el peor caso (3 dígitos de watts)
    // no se pega ni al valor anterior ni al borde.
    private const float ColUsageRight = 144;
    private const float ColMhzRight = 219;
    private const float ColTempRight = 266;
    private const float ColWattsRight = 320;

    // Fuentes dinámicas según la escala de letra configurada (todo Consolas).
    private Font? _fpsFont;
    private Font? _lowFont;
    private Font? _lineFont;
    private float _fontScale = -1f;

    private Font FpsFont => _fpsFont!;
    private Font LowFont => _lowFont!;
    private Font LineFont => _lineFont!;

    private void EnsureFonts(float scale)
    {
        if (Math.Abs(scale - _fontScale) < 0.001f) return;
        _fpsFont?.Dispose();
        _lowFont?.Dispose();
        _lineFont?.Dispose();
        _fpsFont = new Font("Consolas", 20f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
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
        _renderTimer = new WinFormsTimer { Interval = 250 };
        _renderTimer.Tick += (_, _) => Render();
        _renderTimer.Start();
    }

    public void StopRendering()
    {
        _renderTimer?.Stop();
        _renderTimer?.Dispose();
        _renderTimer = null;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            // Fijar el tamaño final (escala de letra + líneas) ANTES de restaurar la
            // posición: si el primer render cambia el tamaño, EnsureBuffer re-ancla a
            // la esquina y se pierde la posición que arrastró el usuario.
            Size = new Size(
                (int)Math.Round(OverlayWidth * _config.FontScale),
                ComputeOverlayHeight());

            // Posición guardada (o esquina por defecto: arriba a la derecha).
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int x = _settings.Get("overlay.posX", int.MinValue);
            int y = _settings.Get("overlay.posY", int.MinValue);
            if (x == int.MinValue || y == int.MinValue ||
                x < area.X - 200 || y < area.Y - 200 ||
                x > area.Right - 50 || y > area.Bottom - 50)
            {
                x = area.Right - Width - 16;
                y = area.Top + 16;
            }
            Location = new Point(x, y);
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
        base.WndProc(ref m);
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
            // Orden por defecto: FPS y lows en filas propias (FPS abajo de todo).
            metricRows = SplitFpsAndLows(metricRows);
        }

        // Invariante de grupos: una sola familia por fila, core primero al inicio
        // de su grupo y grupos en orden cpu → gpu → ram → fps. Separa filas
        // mezcladas de configs viejas y devuelve métricas mal ubicadas a su grupo.
        metricRows = NormalizeRows(metricRows);

        // La grilla de la config es fija: ninguna línea pasa de 4 métricas.
        metricRows = metricRows.SelectMany(ChunkRows).ToList();

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
            FpsColor: c("overlay.colorFps", "#FFFFFF"),
            CpuColor: c("overlay.colorCpu", "#FFFFFF"),
            GpuColor: c("overlay.colorGpu", "#FFFFFF"),
            RamColor: c("overlay.colorRam", "#FFFFFF"),
            MetricRows: metricRows);
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
        "fps" or "low1" or "low01" or "cpuUsage" or "cpuMhz" or "cpuTemp" or "cpuWatts"
            or "gpuUsage" or "gpuMhz" or "gpuTemp" or "gpuWatts" or "ramMb" or "ramMhz" => true,
        _ => false
    };

    private void Render()
    {
        if (_disposed || !IsHandleCreated || !Visible) return;
        try
        {
            if (_configDirty)
            {
                _config = ReadConfig();
                _configDirty = false;
            }

            var metrics = _metrics.Latest;
            EnsureBuffer();
            EnsureFonts((float)_config.FontScale);

            var g = _bufferGraphics!;
            // Transparente NEGRO: Color.Transparent es blanco con alpha 0 y, según
            // cómo lo convierta GetHbitmap, dejaba un borde blanco en los bordes del
            // fondo redondeado.
            g.Clear(Color.FromArgb(0, 0, 0, 0));

            using var fpsBrush = new SolidBrush(_config.FpsColor);
            using var cpuBrush = new SolidBrush(_config.CpuColor);
            using var gpuBrush = new SolidBrush(_config.GpuColor);
            using var ramBrush = new SolidBrush(_config.RamColor);

            // Fondo: panel con ESQUINAS REDONDEADAS. La opacidad controla SOLO el
            // fondo; el texto se dibuja siempre al 100%. Antes eran rectangulares
            // porque Color.Transparent dejaba "puntas blancas"; hoy el buffer es
            // transparente NEGRO (FromArgb(0,0,0,0)) y PaintLayered copia los
            // píxeles premultiplicados tal cual, así las esquinas fuera del path
            // quedan realmente transparentes.
            int bgAlpha = (int)(_config.Opacity * 255);
            int radius = (int)MathF.Max(6f, 12f * (float)_config.FontScale);
            using (var path = RoundedRect(new Rectangle(0, 0, _buffer!.Width, _buffer.Height), radius))
            using (var bgBrush = new SolidBrush(Color.FromArgb(bgAlpha, 15, 15, 18)))
            {
                g.FillPath(bgBrush, path);
                // Borde sólido del mismo color y SIN anti-aliasing: cubre el halo
                // claro del contorno y da el acabado "panel" tipo RTSS. El borde
                // usa la MISMA opacidad que el fondo (a 0 desaparece por completo).
                var prevSmoothing = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.None;
                using (var rimBrush = new SolidBrush(Color.FromArgb(bgAlpha, 15, 15, 18)))
                using (var rimPen = new Pen(rimBrush, 1.5f * (float)_config.FontScale))
                    g.DrawPath(rimPen, path);
                g.SmoothingMode = prevSmoothing;

                // Borde visible alrededor del panel: línea fina gris claro dibujada
                // 1px ADENTRO y SIGUIENDO el path redondeado (así no se recorta en
                // el borde de la ventana). Sigue la opacidad del fondo: a opacidad
                // 0 desaparece junto con el panel.
                using (var borderPen = new Pen(
                           Color.FromArgb((int)(190 * _config.Opacity), 185, 190, 200), 1f))
                {
                    var borderRect = new Rectangle(1, 1, _buffer!.Width - 3, _buffer.Height - 3);
                    g.DrawPath(borderPen, RoundedRect(borderRect, Math.Max(1, radius - 1)));
                }
            }

            float y = S(8);
            string? gameName = metrics?.GameName;
            bool haveFps = metrics != null && metrics.Fps > 0;

            // Estado bloqueado/desbloqueado, centrado horizontalmente (traducido).
            string stateText = _locked ? I18n.T("Bloqueado") : I18n.T("Desbloqueado");
            using (var stateBrush = new SolidBrush(_locked
                       ? Color.FromArgb(150, 150, 150)
                       : Color.FromArgb(225, 225, 225)))
            {
                var stateSize = g.MeasureString(stateText, LowFont);
                float stateX = ((float)_buffer!.Width - stateSize.Width) / 2f;
                g.DrawString(stateText, LowFont, stateBrush, stateX, y);
            }
            y += S(21);

            // Métricas en las FILAS de badges de la configuración (arrastrables):
            // cada fila de la página es una línea de la superposición, dibujada en
            // el mismo orden, con SOLO los valores habilitados y en las columnas
            // fijas de la grilla (el nombre no corre a los %, los % no corren a los
            // MHz, etc.).
            foreach (var row in BuildRenderRows(_config.MetricRows))
            {
                switch (row.Family)
                {
                    case "cpu" when metrics != null:
                        y = DrawHardwareLine(g, metrics.CpuName, metrics.CpuUsagePercent, metrics.CpuMhz,
                            metrics.CpuTempCelsius, metrics.CpuWatts,
                            row.Ids.Contains("cpuUsage"), row.Ids.Contains("cpuMhz"),
                            row.Ids.Contains("cpuTemp"), row.Ids.Contains("cpuWatts"),
                            cpuBrush, y);
                        break;
                    case "gpu" when metrics != null:
                        y = DrawHardwareLine(g, metrics.GpuName, metrics.GpuUsagePercent, metrics.GpuMhz,
                            metrics.GpuTempCelsius, metrics.GpuWatts,
                            row.Ids.Contains("gpuUsage"), row.Ids.Contains("gpuMhz"),
                            row.Ids.Contains("gpuTemp"), row.Ids.Contains("gpuWatts"),
                            gpuBrush, y);
                        break;
                    case "ram" when metrics != null:
                    {
                        // RAM: el nombre es "RAM" seguido de la configuración de
                        // módulos (RAM 2x16 GB), después el uso en MB y la velocidad.
                        // El nombre se recorta al ancho disponible para que nunca
                        // invada y pise los "xx MB" de la derecha.
                        float firstRamValueRight = row.Ids.Contains("ramMb") ? ColUsageRight
                            : row.Ids.Contains("ramMhz") && metrics.RamMhz > 0 ? ColMhzRight
                            : ColUsageRight;
                        float maxRamLabelWidth = S(firstRamValueRight) - S(12) - S(10);
                        if (maxRamLabelWidth < S(40)) maxRamLabelWidth = S(40);

                        string ramName = string.IsNullOrWhiteSpace(metrics.RamConfig)
                            ? "RAM"
                            : "RAM " + metrics.RamConfig;
                        g.DrawString(FitText(g, ramName, LineFont, maxRamLabelWidth), LineFont, ramBrush, S(12), y);
                        if (row.Ids.Contains("ramMb"))
                            DrawRight(g, $"{metrics.RamUsedMb:F0} MB", LineFont, ramBrush, S(ColUsageRight), y);
                        if (row.Ids.Contains("ramMhz") && metrics.RamMhz > 0)
                            DrawRight(g, $"{metrics.RamMhz:F0} MHz", LineFont, ramBrush, S(ColMhzRight), y);
                        y += S(25);
                        break;
                    }
                    case "fps":
                        y = DrawFpsBlock(g, metrics, haveFps, row.Ids, fpsBrush, y);
                        break;
                }
            }

            // Nombre del juego (subtítulo sutil, abajo a la izquierda).
            if (haveFps && !string.IsNullOrEmpty(gameName) && gameName != "WinForge")
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

    /// <summary>
    /// Línea de hardware en grilla: nombre en la columna 1, y cada valor en su
    /// columna fija (alineado a la derecha). Mostrar/ocultar un valor NO mueve
    /// a los demás. El nombre se recorta al ancho disponible para no pisar los
    /// valores de las columnas de la derecha.
    /// </summary>
    private float DrawHardwareLine(Graphics g, string label,
        double usage, double mhz, double temp, double watts,
        bool showUsage, bool showMhz, bool showTemp, bool showWatts,
        Brush brush, float y)
    {
        // Primera columna de valor visible: hasta ahí puede llegar el nombre.
        float firstValueRight = showUsage ? ColUsageRight
            : showMhz && mhz > 0 ? ColMhzRight
            : showTemp && temp > 0 ? ColTempRight
            : ColWattsRight;
        float maxLabelWidth = S(firstValueRight) - S(12) - S(10);
        if (maxLabelWidth < S(40)) maxLabelWidth = S(40);

        string text = string.IsNullOrWhiteSpace(label) ? "—" : label;
        g.DrawString(FitText(g, text, LineFont, maxLabelWidth), LineFont, brush, S(12), y);
        if (showUsage) DrawRight(g, $"{usage:F0}%", LineFont, brush, S(ColUsageRight), y);
        if (showMhz && mhz > 0) DrawRight(g, $"{mhz:F0} MHz", LineFont, brush, S(ColMhzRight), y);
        if (showTemp && temp > 0) DrawRight(g, $"{temp:F0}°C", LineFont, brush, S(ColTempRight), y);
        if (showWatts && watts > 0) DrawRight(g, $"{watts:F0} W", LineFont, brush, S(ColWattsRight), y);
        return y + S(25);
    }

    /// <summary>
    /// Bloque de FPS: número grande con la etiqueta "FPS {api}" AL LADO (misma
    /// línea, para no romper la grilla vertical) y, debajo, los lows 1% / 0.1%
    /// habilitados. Cada métrica dibuja su texto desde la MISMA coordenada Y que
    /// las líneas de hardware, así todo queda alineado como si estuviese en una
    /// grilla. Solo se dibuja si algún badge del bloque (fps/low1/low01) está
    /// activo.
    /// </summary>
    private float DrawFpsBlock(Graphics g, WHPO.Core.Services.Interfaces.OverlayMetrics? metrics,
        bool haveFps, List<string> ids, Brush fpsBrush, float y)
    {
        bool showFps = ids.Contains("fps");
        bool showLow1 = ids.Contains("low1");
        bool showLow01 = ids.Contains("low01");
        if (!showFps && !showLow1 && !showLow01) return y;

        float left = S(12);
        if (showFps)
        {
            string fpsText = haveFps ? metrics!.Fps.ToString("F0") : "--";
            g.DrawString(fpsText, FpsFont, fpsBrush, left, y - S(2));

            // Etiqueta "FPS" + API gráfica en pequeño, al lado derecho del número
            // y verticalmente centrada en la misma línea (no debajo): así la fila
            // no ocupa doble alto y el resto de la grilla no se desacomoda.
            string api = metrics?.GfxApi ?? "";
            string fpsLabel = api.Length > 0 ? $"FPS {api}" : "FPS";
            float numW = g.MeasureString(fpsText, FpsFont).Width;
            g.DrawString(fpsLabel, LowFont, fpsBrush, left + numW + S(6), y + S(3));
            y += S(34);
        }

        // 1% low / 0.1% low (chico, sobre la MISMA base vertical que las filas de
        // hardware). Cuando están en filas propias de badges (default) cada fila
        // dibuja su línea; si comparten fila con el FPS quedan debajo del número.
        var lows = new List<string>();
        if (showLow1 && metrics != null && metrics.FpsLow1 > 0)
            lows.Add($"1% {metrics.FpsLow1:F0}");
        if (showLow01 && metrics != null && metrics.FpsLow01 > 0)
            lows.Add($"0.1% {metrics.FpsLow01:F0}");
        if (lows.Count > 0)
        {
            g.DrawString(string.Join("  ", lows), LowFont, fpsBrush, left, y);
            y += S(24);
        }
        return y;
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
        "fps" or "low1" or "low01" => "fps",
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

    /// <summary>
    /// Normaliza las filas de métricas a la invariante del overlay:
    ///  - cada fila es de UN solo grupo (cpu/gpu/ram/fps),
    ///  - los grupos aparecen en orden fijo cpu → gpu → ram → fps,
    ///  - la métrica core (la %) encabeza la primera fila de su grupo,
    ///  - las sub-filas del grupo siguen a la fila core (preservando el orden).
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
    /// FPS queda debajo de todo y cada low arriba de él.
    /// </summary>
    public static List<List<string>> SplitFpsAndLows(List<List<string>> rows)
    {
        var keep = rows.Select(r => r.Where(id => id is not ("fps" or "low1" or "low01")).ToList())
                       .Where(r => r.Count > 0).ToList();
        if (rows.Any(r => r.Contains("low1"))) keep.Add(new List<string> { "low1" });
        if (rows.Any(r => r.Contains("low01"))) keep.Add(new List<string> { "low01" });
        if (rows.Any(r => r.Contains("fps"))) keep.Add(new List<string> { "fps" });
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
        float y = S(8) + S(21); // margen superior + línea de estado
        foreach (var row in BuildRenderRows(_config.MetricRows))
        {
            switch (row.Family)
            {
                case "fps":
                    if (row.Ids.Contains("fps")) y += S(34);
                    if (row.Ids.Contains("low1") || row.Ids.Contains("low01")) y += S(24);
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
        int w = (int)Math.Round(OverlayWidth * _config.FontScale);
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
            // Al cambiar el tamaño (escala de letra), se re-ancla a la esquina
            // elegida para que el overlay no se desacomode.
            SetCorner(_settings.Get("overlay.corner", "top-right"));
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
                _fpsFont?.Dispose();
                _lowFont?.Dispose();
                _lineFont?.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
