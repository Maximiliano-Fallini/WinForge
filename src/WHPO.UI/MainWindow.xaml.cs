using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Views.Pages;
using WinFormsApp = System.Windows.Forms.Application;
using DrawingImage = System.Drawing.Image;
using WinUIApp = Microsoft.UI.Xaml.Application;

namespace WHPO_UI;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigationService;
    private readonly ILoggingService _loggingService;
    private readonly ISettingsService _settingsService;
    private readonly ISystemInfoService _systemInfoService;
    private readonly IMemoryService _memoryService;
    private readonly INetworkService _networkService;
    
    private NotifyIcon? _notifyIcon;
    private DispatcherQueueTimer? _trayTooltipTimer;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _gpuCounter;
    private bool _isMinimizingToTray = false;
    private bool _centeredOnFirstActivation;

    /// <summary>
    /// Indica si la ventana principal está visible (no oculta en bandeja).
    /// Las páginas lo usan para pausar sus timers de fondo y ahorrar CPU/RAM.
    /// </summary>
    public bool IsWindowVisible { get; private set; } = true;

    public MainWindow()
    {
        InitializeComponent();

        // Obtener servicios desde DI
        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _systemInfoService = App.Services.GetRequiredService<ISystemInfoService>();
        _memoryService = App.Services.GetRequiredService<IMemoryService>();
        _networkService = App.Services.GetRequiredService<INetworkService>();

        // Configurar ThemeApplier con esta ventana
        var themeApplier = App.Services.GetRequiredService<IThemeApplier>();
        if (themeApplier is ThemeApplier ta)
        {
            ta.SetMainWindow(this);
        }

        // Configurar NavigationView
        NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems[0];
        NavigationViewControl.ItemInvoked += NavigationViewControl_ItemInvoked;

        // Configurar NavigationService con el Frame
        if (_navigationService is NavigationService ns)
        {
            ns.SetFrame(new NavigationFrameAdapter(ContentFrame));
            ns.RegisterPage("sistema", typeof(SistemaPage));
            ns.RegisterPage("red", typeof(RedPage));
            ns.RegisterPage("memoria", typeof(MemoriaPage));
            ns.RegisterPage("temporizador", typeof(TemporizadorPage));
            ns.RegisterPage("nucleos", typeof(NucleosPage));
            ns.RegisterPage("estabilidad", typeof(EstabilidadPage));
            ns.RegisterPage("optimizaciones", typeof(OptimizacionesPage));
            ns.RegisterPage("herramientas", typeof(HerramientasPage));
            ns.RegisterPage("panelwindows", typeof(PanelWindowsPage));
            ns.RegisterPage("reparacion", typeof(ReparacionPage));
            ns.RegisterPage("actualizaciones", typeof(ActualizacionesPage));
            ns.RegisterPage("configuracion", typeof(ConfiguracionPage));
        }

        // Aplicar la visibilidad de apartados según la configuración (claves "nav.*").
        ApplyNavigationVisibility();

        // Navegar directamente a la página de Sistema (sin título de cabecera)
        _navigationService.NavigateTo("sistema");
        NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems[0];

        // Configurar minimize to tray
        this.Closed += MainWindow_Closed;

        // Interceptar botón cerrar (X) para minimizar a bandeja si está activado
        this.AppWindow.Closing += AppWindow_Closing;

        // Garantizar la restauración/centrado en la primera activación: aplicar la
        // posición antes de Activate() puede ser ignorado por Windows, y con el
        // evento queda seguro. (Misma posición que la del constructor: no salta.)
        this.Activated += (_, args) =>
        {
            if (!_centeredOnFirstActivation && args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _centeredOnFirstActivation = true;
                RestoreOrCenterWindow();
            }
        };

        // Barra de título acorde al tema (negra en oscuro, blanca en claro) + icono de WinForge
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow != null)
            {
                var themeService = App.Services.GetRequiredService<IThemeService>();
                ApplyTitleBarTheme(themeService);
                themeService.ThemeChanged += (s, t) => ApplyTitleBarTheme(themeService);

                var winForgeIconPath = GetWinForgeIcoPath();
                if (winForgeIconPath != null)
                    appWindow.SetIcon(winForgeIconPath);

                // Guardar posición/tamaño al mover o redimensionar (debounced).
                InitWindowPositionTracking();

                // OJO: el tamaño/posición se aplica SOLO en el evento Activated (arriba).
                // Aplicarlo aquí (antes de Activate) es el camino poco confiable de
                // MoveAndResize de WinAppSDK y puede dejar la ventana en un tamaño
                // mínimo espurio (el "cuadradito chiquito" al abrir).
            }
        }
        catch { }

        // El tray icon (NotifyIcon de WinForms) y las métricas se inicializan DIFERIDOS
        // para que la ventana aparezca al instante: su creación es costosa la primera vez.
        DispatcherQueue.TryEnqueue(() =>
        {
            // SetupTrayIcon ya invoca UpdateTrayMetricsState internamente.
            SetupTrayIcon();

            // Reaplicar lo que el usuario dejó iniciado en la sesión anterior
            // (resolución del temporizador y limpieza automática de memoria).
            _ = ApplyAutoStartFeaturesAsync();

            // Pre-calentar el sensor de temperatura (carga el driver de LHM en segundo
            // plano) para que la pestaña Núcleos muestre la temperatura enseguida.
            _ = Task.Run(() => { try { _systemInfoService.GetCpuTemperature(); } catch { } });
        });

        _loggingService.LogInfo("MainWindow inicializada");
    }

    private void SetupTrayIcon()
    {
        try
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = CreateWinForgeIcon() ?? SystemIcons.Application,
                Visible = true,
                Text = "WinForge"
            };

            _notifyIcon.DoubleClick += (s, e) => ShowWindow();

            // Actualizar tooltip según configuración inicial
            UpdateTrayMetricsState();

            // Menú contextual visualmente más limpio: renderer oscuro propio (el color
            // table del sistema usa el tema claro → hover blanco + texto blanco ilegible)
            // y esquinas redondeadas vía DWM (Win11).
            var contextMenu = new ContextMenuStrip
            {
                // OJO: RenderMode NO se puede setear a 'Custom' directamente (WinForms lanza
                // NotSupportedException); se asigna el Renderer abajo y eso lo pone en Custom.
                ShowImageMargin = false,
                ShowCheckMargin = false,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(24, 24, 24),
                ForeColor = Color.White,
                Padding = new Padding(8),
                ImageScalingSize = new Size(16, 16),
                AutoClose = true,
                Margin = new Padding(0)
            };

            contextMenu.Renderer = new DarkMenuRenderer();
            contextMenu.HandleCreated += (s, e) => ApplyRoundedMenuCorners(contextMenu.Handle);

            // Item: Mostrar ventana
            var showItem = new ToolStripMenuItem("Mostrar ventana")
            {
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                Padding = new Padding(16, 9, 16, 9),
                Margin = new Padding(0),
                AutoSize = false,
                Width = 200
            };
            showItem.Click += (s, e) => ShowWindow();
            contextMenu.Items.Add(showItem);

            // Separador
            contextMenu.Items.Add(new ToolStripSeparator());

            // Item: Configuración
            var settingsItem = new ToolStripMenuItem("Configuración")
            {
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                Padding = new Padding(16, 9, 16, 9),
                Margin = new Padding(0),
                AutoSize = false,
                Width = 200
            };
            settingsItem.Click += (s, e) =>
            {
                _navigationService.NavigateTo("configuracion");
                ShowWindow();
            };
            contextMenu.Items.Add(settingsItem);

            // Separador
            contextMenu.Items.Add(new ToolStripSeparator());

            // Item: Salir
            var exitItem = new ToolStripMenuItem("Salir")
            {
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                Padding = new Padding(16, 9, 16, 9),
                Margin = new Padding(0),
                AutoSize = false,
                Width = 200
            };
            exitItem.Click += (s, e) =>
            {
                _isMinimizingToTray = false;
                _notifyIcon?.Dispose();
                WinUIApp.Current.Exit();
            };
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = contextMenu;

            // Timer para actualizar tooltip (se inicia en UpdateTrayMetricsState)
            if (_trayTooltipTimer == null)
            {
                _trayTooltipTimer = DispatcherQueue.CreateTimer();
                _trayTooltipTimer.Interval = TimeSpan.FromSeconds(2);
                _trayTooltipTimer.Tick += async (s, e) => await UpdateTrayTooltipAsync();
            }

        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error configurando tray icon", ex);
        }
    }

    private void InitializePerformanceCounters()
    {
        // CPU - siempre disponible
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // Primera llamada para inicializar
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error inicializando contador CPU", ex);
            _cpuCounter = null;
        }

        // GPU - puede no estar disponible
        try
        {
            _gpuCounter = new PerformanceCounter("GPU Engine", "Utilization Percentage", "_Total");
            _gpuCounter.NextValue();
        }
        catch (Exception ex)
        {
            _loggingService.LogInfo("Contador GPU no disponible: " + ex.Message);
            _gpuCounter = null;
        }

    }

    private async Task UpdateTrayTooltipAsync()
    {
        if (_notifyIcon == null) return;

        try
        {
            // Obtener estadísticas - usar 0 si el contador no está disponible
            var cpuUsage = _cpuCounter != null ? _cpuCounter.NextValue() : 0;
            var gpuUsage = _gpuCounter != null ? _gpuCounter.NextValue() : 0;

            // Temperaturas al lado del uso (LHM + fallbacks; fuera del hilo de UI por si WMI tarda)
            var cpuTemp = await Task.Run(() => _systemInfoService.GetCpuTemperature());
            var gpuTemp = await Task.Run(() => _systemInfoService.GetGpuTemperature());

            var memStats = _memoryService.GetMemoryStats();
            var cacheMB = memStats.StandbyMB;
            
            var currentTR = _memoryService.GetCurrentTimerResolution();
            var trMs = currentTR / 10000.0;

            var cpuTempPart = cpuTemp > 0 ? $" · {cpuTemp:F0}°C" : "";
            var gpuTempPart = gpuTemp > 0 ? $" · {gpuTemp:F0}°C" : "";

            var tooltip = $"CPU: {cpuUsage:F0}%{cpuTempPart}\r\n" +
                $"GPU: {gpuUsage:F0}%{gpuTempPart}\r\n" +
                $"RAM: {memStats.UsedPercent:F0}% ({memStats.UsedMB:F0} MB)\r\n" +
                $"Cache: {cacheMB:F0} MB\r\n" +
                $"TR: {trMs:F2} ms";

            _notifyIcon.Text = tooltip;
            _loggingService.LogInfo($"Tooltip actualizado: CPU={cpuUsage:F0}%, RAM={memStats.UsedPercent:F0}%");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error actualizando tooltip de tray", ex);
            // Fallback: mostrar al menos info básica
            try
            {
                var memStats = _memoryService.GetMemoryStats();
                var currentTR = _memoryService.GetCurrentTimerResolution();
                var trMs = currentTR / 10000.0;
                _notifyIcon.Text = $"WinForge\nRAM: {memStats.UsedMB:F0}/{memStats.TotalPhysicalMB:F0} MB\nTR: {trMs:F3} ms";
            }
            catch { }
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var minimizeToTray = _settingsService.Get("window.minimizeToTray", true);
        if (minimizeToTray)
        {
            args.Cancel = true; // Cancelar el cierre
            HideWindow(); // Ocultar a bandeja
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _trayTooltipTimer?.Stop();
        _notifyIcon?.Dispose();
        _cpuCounter?.Dispose();
        _gpuCounter?.Dispose();
    }

    private void HideWindow()
    {
        IsWindowVisible = false;
        // En WinUI 3, ocultar la ventana
        var appWindow = GetAppWindow();
        if (appWindow != null)
        {
            appWindow.Hide();
        }
        _isMinimizingToTray = true;

        // Detener monitoreo del sistema para evitar picos de CPU en background
        _systemInfoService.StopMonitoring();
        _loggingService.LogInfo("Monitoreo de sistema detenido (ventana oculta)");

        UpdateTrayMetricsState();
    }

    /// <summary>
    /// Oculta WHPO en la bandeja tras el inicio cuando el usuario lo configuró así.
    /// </summary>
    internal void HideToTrayAtStartup()
    {
        HideWindow();
    }

    private void ShowWindow()
    {
        IsWindowVisible = true;
        var appWindow = GetAppWindow();
        if (appWindow != null)
        {
            appWindow.Show();
            // Cada vez que se abre (incluido al volver de la bandeja) se restaura
            // la última posición/tamaño (o se centra si aún no hay guardados).
            RestoreOrCenterWindow();
        }
        _isMinimizingToTray = false;

        // Reanudar monitoreo solo si la página activa es Sistema (evita timers sin suscriptores)
        if (ContentFrame.Content is SistemaPage)
        {
            _systemInfoService.StartMonitoring(1000);
            _loggingService.LogInfo("Monitoreo de sistema reanudado (ventana visible, página Sistema)");
        }

        UpdateTrayMetricsState();
    }

    internal void UpdateTrayMetricsState()
    {
        var showMetrics = _settingsService.Get("tray.showMetrics", false);

        // Con la ventana oculta en bandeja, espaciar las consultas de métricas
        // (WMI/LHM) para ahorrar CPU en segundo plano.
        var pollInterval = IsWindowVisible ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);

        if (showMetrics)
        {
            if (_trayTooltipTimer == null)
            {
                _trayTooltipTimer = DispatcherQueue.CreateTimer();
                _trayTooltipTimer.Interval = pollInterval;
                _trayTooltipTimer.Tick += async (s, e) => await UpdateTrayTooltipAsync();
            }
            else
            {
                _trayTooltipTimer.Interval = pollInterval;
            }

            if (_cpuCounter == null && _memoryService != null)
            {
                InitializePerformanceCounters();
            }

            if (!_trayTooltipTimer.IsRunning)
            {
                _trayTooltipTimer.Start();
            }

            _ = UpdateTrayTooltipAsync();
        }
        else
        {
            // Ocultar métricas: detener timer y liberar contadores
            _trayTooltipTimer?.Stop();
            if (_cpuCounter != null || _gpuCounter != null)
            {
                _cpuCounter?.Dispose();
                _gpuCounter?.Dispose();
                _cpuCounter = null;
                _gpuCounter = null;
            }
            if (_notifyIcon != null)
            {
                _notifyIcon.Text = "WinForge";
            }
        }
    }

    /// <summary>
    /// Restaura la posición/tamaño guardados de la ventana; si no hay posición
    /// guardada (primer uso) o quedó fuera de pantalla (cambió el monitor), la
    /// centra en el área de trabajo con el tamaño por defecto (1400x800).
    /// Se llama al arrancar y en cada apertura desde la bandeja.
    /// </summary>
    private void RestoreOrCenterWindow()
    {
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow == null) return;

            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
            var wa = area.WorkArea;

            int w = _settingsService.Get("window.width", 1400);
            int h = _settingsService.Get("window.height", 800);
            int x = _settingsService.Get("window.x", int.MinValue);
            int y = _settingsService.Get("window.y", int.MinValue);

            // Limitar el tamaño al área de trabajo (monitores chicos).
            w = Math.Clamp(w, 800, Math.Max(800, wa.Width));
            h = Math.Clamp(h, 500, Math.Max(500, wa.Height));

            // La posición guardada solo se usa si una parte razonable de la ventana
            // sigue dentro del área visible; si no, se vuelve a centrar.
            bool visible = x != int.MinValue && y != int.MinValue &&
                x + 80 <= wa.X + wa.Width && x + w - 80 >= wa.X &&
                y + 40 <= wa.Y + wa.Height && y + h - 40 >= wa.Y;

            if (visible)
            {
                appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
            }
            else
            {
                int cx = wa.X + Math.Max(0, (wa.Width - w) / 2);
                int cy = wa.Y + Math.Max(0, (wa.Height - h) / 2);
                appWindow.MoveAndResize(new Windows.Graphics.RectInt32(cx, cy, w, h));
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudo restaurar la posición de la ventana: {ex.Message}");
        }
    }

    // ===== Guardado de posición/tamaño de la ventana (con debounce) =====
    private DispatcherQueueTimer? _windowPosSaveTimer;

    private void InitWindowPositionTracking()
    {
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow == null) return;
            appWindow.Changed += (_, args) =>
            {
                if (args.DidPositionChange || args.DidSizeChange)
                    ScheduleWindowPositionSave();
            };
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudo iniciar el guardado de posición: {ex.Message}");
        }
    }

    private void ScheduleWindowPositionSave()
    {
        if (_windowPosSaveTimer == null)
        {
            _windowPosSaveTimer = DispatcherQueue.CreateTimer();
            _windowPosSaveTimer.Interval = TimeSpan.FromMilliseconds(800);
            _windowPosSaveTimer.Tick += (_, _) =>
            {
                _windowPosSaveTimer.Stop();
                SaveWindowPosition();
            };
        }
        _windowPosSaveTimer.Stop();
        _windowPosSaveTimer.Start();
    }

    private void SaveWindowPosition()
    {
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow == null) return;
            var pos = appWindow.Position;
            var size = appWindow.Size;

            // No guardar estados transitorios inválidos: tamaños espurios (ventana aún
            // sin dimensionar en el arranque) o la posición de una ventana minimizada
            // (-32000, -32000). Guardarlos haría que la app abra como un "cuadradito".
            if (size.Width < 400 || size.Height < 300) return;
            if (pos.X < -10000 || pos.Y < -10000) return;

            _settingsService.Set("window.x", pos.X);
            _settingsService.Set("window.y", pos.Y);
            _settingsService.Set("window.width", size.Width);
            _settingsService.Set("window.height", size.Height);
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"No se pudo guardar la posición de la ventana: {ex.Message}");
        }
    }

    private AppWindow? GetAppWindow()
    {
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    // ===== Reaplicación al iniciar (resolución del temporizador / limpieza automática) =====

    /// <summary>
    /// Si el usuario dejó activadas la resolución del temporizador o la limpieza
    /// automática de memoria, las reaplica al arrancar la app. Ambas viven en este
    /// proceso: al cerrarlo, Windows revierte la resolución y la limpieza se detiene.
    /// </summary>
    private async Task ApplyAutoStartFeaturesAsync()
    {
        try
        {
            if (_settingsService.Get("timer.autoStart", false))
            {
                double desiredMs = _settingsService.Get("memory.desiredResolutionMs", 0.5);
                int resolution100ns = (int)(desiredMs * 10000);
                var result = await _memoryService.SetTimerResolutionAsync(resolution100ns);
                if (result.Success)
                    _loggingService.LogInfo("Resolución del temporizador reaplicada al iniciar");
                else
                    _loggingService.LogWarning($"No se pudo reaplicar la resolución al iniciar: {result.Output}");
            }

            if (_settingsService.Get("memory.autoStart", false))
            {
                double minStandby = _settingsService.Get("memory.minStandbyMB", 1024.0);
                double maxFree = _settingsService.Get("memory.maxFreeMB", 4096.0);
                int pollIntervalMs = _settingsService.Get("memory.pollIntervalMs", 1000);
                _memoryService.StartAutoCleanup(minStandby, maxFree, pollIntervalMs);
                _loggingService.LogInfo("Limpieza automática de memoria reiniciada al iniciar");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Error reaplicando funciones automáticas al iniciar: {ex.Message}");
        }
    }

    // ===== Visibilidad de apartados del menú lateral =====

    /// <summary>
    /// Aplica la visibilidad de los apartados del menú según la configuración
    /// (claves "nav.&lt;tag&gt;"; por defecto todas las pestañas son visibles).
    /// </summary>
    internal void ApplyNavigationVisibility()
    {
        try
        {
            foreach (var item in NavigationViewControl.MenuItems.OfType<NavigationViewItem>())
                ApplyNavItemVisibility(item);
            foreach (var item in NavigationViewControl.FooterMenuItems.OfType<NavigationViewItem>())
                ApplyNavItemVisibility(item);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"Error aplicando visibilidad del menú: {ex.Message}");
        }
    }

    private void ApplyNavItemVisibility(NavigationViewItem item)
    {
        if (item.Tag is not string tag) return;
        // La pestaña Configuración no se puede ocultar: siempre visible.
        if (tag == "configuracion")
        {
            item.Visibility = Visibility.Visible;
            return;
        }
        item.Visibility = _settingsService.Get("nav." + tag, true) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NavigationViewControl_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
            {
                _loggingService.LogInfo($"Navegando a: {tag}");
                _navigationService.NavigateTo(tag);
                _loggingService.LogInfo($"Navegación completada: {tag}");
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError($"Error en NavigationViewControl_ItemInvoked: {ex}", ex);
            // No propagar la excepción
        }
    }

    // ===== Icono y barra de título: logo de WinForge + ventana negra =====

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attrValue, int attrSize);

    /// <summary>
    /// Aplica esquinas redondeadas (Windows 11) a la ventana del menú de bandeja.
    /// En Windows 10 el atributo se ignora y el menú queda con esquinas cuadradas (normal).
    /// </summary>
    private static void ApplyRoundedMenuCorners(IntPtr hwnd)
    {
        try
        {
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Carga el logo de WinForge como icono de la bandeja.
    /// </summary>
    private static Icon? CreateWinForgeIcon()
    {
        try
        {
            var pngPath = Path.Combine(AppContext.BaseDirectory, "logos", "WinForge.png");
            if (!File.Exists(pngPath)) return null;
            using var src = DrawingImage.FromFile(pngPath);
            using var bmp = new Bitmap(src, new Size(32, 32));
            var hIcon = bmp.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(hIcon).Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Ruta del .ico de WinForge que viaja con la app (Assets\WinForge.ico), que ya
    /// está embebido en el exe como ApplicationIcon: la barra de tareas usa el mismo.
    /// </summary>
    private static string? GetWinForgeIcoPath()
    {
        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WinForge.ico");
        return File.Exists(icoPath) ? icoPath : null;
    }

    /// <summary>
    /// Aplica los colores de la barra de título según el tema activo.
    /// </summary>
    private void ApplyTitleBarTheme(IThemeService themeService)
    {
        var appWindow = GetAppWindow();
        if (appWindow == null) return;

        bool dark = themeService.CurrentTheme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => App.Services.GetRequiredService<IThemeApplier>().GetSystemTheme() == AppTheme.Dark
        };
        ApplyTitleBarColors(appWindow, dark);
    }

    /// <summary>
    /// Colorea la barra de título: negra con botones claros (tema oscuro) o
    /// blanca con botones oscuros (tema claro).
    /// </summary>
    private static void ApplyTitleBarColors(AppWindow appWindow, bool dark)
    {
        var tb = appWindow.TitleBar;
        var bg = Windows.UI.Color.FromArgb(255, dark ? (byte)0 : (byte)245, dark ? (byte)0 : (byte)246, dark ? (byte)0 : (byte)248);
        var fg = Windows.UI.Color.FromArgb(255, dark ? (byte)255 : (byte)16, dark ? (byte)255 : (byte)20, dark ? (byte)255 : (byte)24);
        var inactiveFg = Windows.UI.Color.FromArgb(255, dark ? (byte)128 : (byte)96, dark ? (byte)128 : (byte)100, dark ? (byte)128 : (byte)104);
        var hover = Windows.UI.Color.FromArgb(255, dark ? (byte)45 : (byte)224, dark ? (byte)45 : (byte)228, dark ? (byte)45 : (byte)232);
        var pressed = Windows.UI.Color.FromArgb(255, dark ? (byte)70 : (byte)200, dark ? (byte)70 : (byte)204, dark ? (byte)70 : (byte)208);
        tb.BackgroundColor = bg;
        tb.ForegroundColor = fg;
        tb.InactiveBackgroundColor = bg;
        tb.InactiveForegroundColor = inactiveFg;
        tb.ButtonBackgroundColor = bg;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverBackgroundColor = hover;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedBackgroundColor = pressed;
        tb.ButtonPressedForegroundColor = fg;
        tb.ButtonInactiveBackgroundColor = bg;
        tb.ButtonInactiveForegroundColor = inactiveFg;
    }
}

/// <summary>
/// Renderer del menú de bandeja: pinta el hover/pressed con color oscuro fijo (si se
/// deja al color table del sistema, en temas claros el hover sale blanco y el texto
/// blanco queda ilegible) y dibuja un borde redondeado acorde al redondeo DWM.
/// </summary>
internal class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color HoverColor = Color.FromArgb(48, 48, 48);
    private static readonly Color PressedColor = Color.FromArgb(64, 64, 64);
    private static readonly Color BorderColor = Color.FromArgb(60, 60, 60);

    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        if (item.Selected || item.Pressed)
        {
            var bounds = new Rectangle(Point.Empty, item.Size);
            using var brush = new SolidBrush(item.Pressed ? PressedColor : HoverColor);
            e.Graphics.FillRectangle(brush, bounds);
            return;
        }
        base.OnRenderMenuItemBackground(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
        bounds.Width -= 1;
        bounds.Height -= 1;
        using var path = RoundedRectangle(bounds, 8);
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawPath(pen, path);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// Tabla de colores personalizada para menú contextual oscuro.
/// </summary>
internal class DarkColorTable : ProfessionalColorTable
{
    private static readonly Color BackColor = Color.FromArgb(32, 32, 32);
    private static readonly Color BorderColor = Color.FromArgb(60, 60, 60);
    private static readonly Color HoverBackColor = Color.FromArgb(48, 48, 48);
    private static readonly Color HoverBorderColor = Color.FromArgb(138, 180, 248);
    private static readonly Color SeparatorColor = Color.FromArgb(60, 60, 60);
    private static readonly Color TextColor = Color.White;

    public override Color MenuBorder => BorderColor;
    public override Color MenuItemBorder => BorderColor;
    public override Color MenuItemSelected => HoverBackColor;
    public override Color MenuItemSelectedGradientBegin => HoverBackColor;
    public override Color MenuItemSelectedGradientEnd => HoverBackColor;
    public override Color MenuItemPressedGradientBegin => HoverBackColor;
    public override Color MenuItemPressedGradientMiddle => HoverBackColor;
    public override Color MenuItemPressedGradientEnd => HoverBackColor;
    public override Color ImageMarginGradientBegin => BackColor;
    public override Color ImageMarginGradientMiddle => BackColor;
    public override Color ImageMarginGradientEnd => BackColor;
    public override Color ImageMarginRevealedGradientBegin => BackColor;
    public override Color ImageMarginRevealedGradientMiddle => BackColor;
    public override Color ImageMarginRevealedGradientEnd => BackColor;
    public override Color SeparatorDark => SeparatorColor;
    public override Color SeparatorLight => SeparatorColor;
    public override Color ToolStripDropDownBackground => BackColor;
    public override Color ToolStripBorder => BorderColor;
    public override Color ToolStripContentPanelGradientBegin => BackColor;
    public override Color ToolStripContentPanelGradientEnd => BackColor;
    public override Color ToolStripPanelGradientBegin => BackColor;
    public override Color ToolStripPanelGradientEnd => BackColor;
}

/// <summary>
/// Adaptador que implementa INavigationFrame usando un Frame de WinUI.
/// </summary>
internal class NavigationFrameAdapter : INavigationFrame
{
    private readonly Frame _frame;

    public NavigationFrameAdapter(Frame frame)
    {
        _frame = frame;
    }

    public bool CanGoBack => _frame.CanGoBack;

    public void Navigate(Type pageType, object? parameter)
    {
        _frame.Navigate(pageType, parameter);
    }

    public void Navigate(Type pageType)
    {
        _frame.Navigate(pageType);
    }

    public void GoBack()
    {
        _frame.GoBack();
    }
}
