using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Services;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Gestión de procesos estilo Process Lasso: tabla en vivo con nombre, usuario,
/// estado, reglas, prioridades (CPU/GPU), afinidad, %CPU, %GPU, nombre de la
/// aplicación y ruta del ejecutable. Cada fila permite aplicar las mismas reglas
/// que los juegos (prioridad de CPU/GPU, afinidad, plan de energía) con alcance
/// «Actual» (solo esta ejecución, en memoria) o «Siempre» (guardado).
/// </summary>
public sealed partial class ProcesosPage : Page
{
    private readonly IProcessService _processService;
    private readonly ICpuPowerService _cpuPowerService;
    private readonly ILoggingService _loggingService;

    private DispatcherQueueTimer? _timer;
    private bool _busy;
    private bool _paused;

    // Filtro de procesos: false = todos, true = solo los del usuario actual.
    private bool _onlyUserProcesses;
    // Vista: false = plana (como el Administrador de tareas), true = árbol.
    private bool _treeMode;
    private string? _currentUser;
    // Mismo formato que GetProcessUser (nombre de cuenta SAM), para comparar exacto.
    private string CurrentUser => _currentUser ??= GetProcessUser(Environment.ProcessId) ?? Environment.UserName;

    // Anchos de columna (ajustables arrastrando el borde de la cabecera).
    private readonly double[] _colWidths;
    private int _resizeCol = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private static readonly InputCursor ResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor DefaultCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    // Árbol de procesos: PIDs que el usuario EXPANDIÓ (sus hijos son visibles).
    // Por defecto todo está colapsado: solo se ven los procesos raíz y el chevron
    // abre la rama de hijos. Se limpian solos cuando el proceso desaparece del
    // snapshot.
    private readonly HashSet<int> _expandedPids = new();
    // Último snapshot: se guarda para que el click del chevron pueda reordenar
    // (expandir/colapsar) sin esperar al próximo tick del timer.
    private List<ProcInfo> _lastSnapshot = new();
    // Mapa PID → hijos del último snapshot (para saber qué procesos muestran chevron).
    private Dictionary<int, List<ProcInfo>>? _lastChildMap;
    private readonly Dictionary<int, RowUi> _rows = new();
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _cpuSamples = new();
    private readonly Dictionary<int, string> _userCache = new();
    private readonly Dictionary<int, string?> _pathCache = new();
    private readonly Dictionary<int, (string Priority, int Rank, long Affinity, int? GpuPrio, int? IoPrio, DateTime At)> _staticCache = new();
    private readonly Dictionary<string, string> _appNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _gpuPids = new();
    private DateTime _gpuCounterEnumAt;
    // Pdh: UNA query con todos los contadores de GPU Engine y UNA sola recolección
    // por tick (antes: una recolección Pdh por contador, ~100+ por segundo).
    private IntPtr _gpuQuery = IntPtr.Zero;
    private readonly Dictionary<string, IntPtr> _gpuCounterHandles = new(StringComparer.OrdinalIgnoreCase);

    // La utilidad GPU cambia lento: se lee cada 2 s (el %GPU se actualiza 2 veces
    // por segundo en vez de cada tick) y los contadores se re-enumeran cada 30 s
    // (enumerar instancias es caro y genera picos).
    private static readonly TimeSpan GpuReadInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GpuCounterEnumTtl = TimeSpan.FromSeconds(30);
    private DateTime _gpuReadAt;
    private Dictionary<int, double> _lastGpuByPid = new();


    // Los scrollbars nativos del ScrollViewer son overlay (se dibujan ENCIMA del contenido),
    // así que ambos se ocultan y se dibujan como ScrollBar nativos en canaletas separadas
    // (ver XAML): el vertical a la derecha y el horizontal abajo, FUERA del viewport.
    // Nunca tapan procesos ni columnas. RowsScroll queda sin márgenes ni reservas.

    // Prioridad de CPU, afinidad y prioridad de GPU abren el handle del proceso
    // (+ D3DKMT): con ~200 procesos son caras. Se cachean y refrescan como mucho
    // cada 10 s; el resto del tick solo lee cachés. Se invalidan al aplicar reglas.
    private static readonly TimeSpan StaticCacheTtl = TimeSpan.FromSeconds(10);

    // Valores para el desplegable de reglas (igual que juegos).
    private static readonly int[] CpuPriorityValues = { 0, 1, 2, 3, 4, 5 };
    private static readonly int[] GpuPriorityValues = { 2, 3, 4 };
    private static readonly int[] IoPriorityValues = { 0, 1, 2, 3, 4 };
    private static readonly string[] CpuPriorityNames =
        { "Mínima", "Baja", "Normal", "Por encima de lo normal", "Alta", "Tiempo real" };

    private static string IoLabel(int v) => v switch
    {
        0 => I18n.T("Muy baja"),
        1 => I18n.T("Baja"),
        2 => I18n.T("Normal"),
        3 => I18n.T("Alta"),
        4 => I18n.T("Crítica"),
        _ => v.ToString()
    };

    // Columnas (px por defecto). Los anchos se pueden ajustar arrastrando el borde
    // de la cabecera; se guardan en _colWidths para que cabecera y filas coincidan.
    private static readonly (string Header, double Width)[] Columns =
    {
        ("Nombre del proceso", 230),
        ("Usuario", 130),
        ("Estado", 90),
        ("Reglas", 85),
        ("Prioridad", 75),
        ("Prioridad GPU", 125),
        ("Prioridad E/S", 95),
        ("Afinidad CPU", 110),
        ("%CPU", 80),
        ("%GPU", 80),
        ("Memoria (RAM)", 100),
        ("Nombre de la aplicación", 180),
        ("Nombre de archivo", 230)
    };

    // SIN compresión proporcional: cada columna usa SIEMPRE su ancho natural (el que
    // se ve por defecto y el que ajustás arrastrando). Si el total supera el viewport,
    // aparece el scroll horizontal en su canaleta (debajo de las filas, sin taparlas);
    // si sobra espacio, la tabla queda a la izquierda con aire a la derecha. Ninguna
    // columna se achica para acomodar a otra.
    private double Scaled(int col) => _colWidths[col];

    private void UpdateFitScale()
    {
        // Redimensionado de ventana / reordenar columnas: se resetean los cachés para
        // que la próxima aplicación toque TODAS las filas (nada queda pendiente).
        _appliedWidths = Array.Empty<double>();
        _appliedTotal = -1;
        _fullApplyPending = false;
        _forceRows = true;
    }

    // Últimos anchos aplicados (por posición): solo se tocan las definiciones de
    // columna cuyo ancho cambió de verdad, para no forzar re-layout de las filas
    // cuando nada cambió (lo que laggeaba el redimensionado).
    private double[] _appliedWidths = Array.Empty<double>();
    private double _appliedTotal = -1;

    // Durante el redimensionado solo se actualizan las filas VISIBLES (el grueso
    // de las filas está fuera de pantalla y tocarlas a todas en cada frame es lo
    // que subía el CPU ~10%). Las filas pendientes se completan al scrollear o en
    // cada refresco de procesos (_fullApplyPending -> _forceRows): así ninguna fila
    // queda desalineada cuando se reordena o se vuelve visible.
    private bool _fullApplyPending;
    private bool _forceRows;

    private void ApplyColumnWidths()
    {
        if (_appliedWidths.Length != _colOrder.Length)
            _appliedWidths = new double[_colOrder.Length];

        double total = TotalWidth;
        bool totalChanged = Math.Abs(_appliedTotal - total) >= 0.5;
        // En la aplicación completa (_forceRows) el ancho total se re-aplica siempre:
        // las filas creadas antes del redimensionado podrían tener un Width viejo.
        if (totalChanged || _forceRows)
        {
            _appliedTotal = total;
            HeaderGrid.Width = total;
        }

        for (int pos = 0; pos < _colOrder.Length; pos++)
        {
            double w = Scaled(_colOrder[pos]);
            bool changed = Math.Abs(_appliedWidths[pos] - w) >= 0.5;
            if (changed) _appliedWidths[pos] = w;
            if (!changed && !_forceRows) continue;

            HeaderGrid.ColumnDefinitions[pos].Width = new GridLength(w);
            if (_forceRows)
            {
                foreach (var ui in _rows.Values)
                    ui.Cols.ColumnDefinitions[pos].Width = new GridLength(w);
            }
            else
            {
                _fullApplyPending = true;
                foreach (var ui in VisibleRows())
                    ui.Cols.ColumnDefinitions[pos].Width = new GridLength(w);
            }
        }

        if (totalChanged || _forceRows)
        {
            if (_forceRows)
            {
                foreach (var ui in _rows.Values)
                    ui.Cols.Width = total;
            }
            else
            {
                _fullApplyPending = true;
                foreach (var ui in VisibleRows())
                    ui.Cols.Width = total;
            }
        }
        _forceRows = false;
    }

    // Altura fija de las filas (para saber cuáles están en el viewport al redimensionar).
    private const double RowHeight = 30;

    /// <summary>Filas actualmente visibles (más un poco de overscan).</summary>
    private IEnumerable<RowUi> VisibleRows()
    {
        double off = RowsScroll.VerticalOffset;
        double vp = RowsScroll.ViewportHeight;
        int first = Math.Max(0, (int)(off / RowHeight) - 1);
        int last = Math.Min(RowsPanel.Children.Count - 1, (int)((off + vp) / RowHeight) + 2);
        for (int i = first; i <= last; i++)
        {
            if (RowsPanel.Children[i] is Grid outer && outer.Tag is int pid && _rows.TryGetValue(pid, out var ui))
                yield return ui;
        }
    }

    private double TotalWidth
    {
        get
        {
            double sum = 0;
            for (int p = 0; p < _colOrder.Length; p++)
                sum += Scaled(_colOrder[p]);
            return sum;
        }
    }



    private sealed class RowUi
    {
        public int Pid;
        public string Exe = "";
        public Grid Row = null!;
        public Image Icon = null!;
        public TextBlock Name = null!, User = null!, State = null!, Priority = null!,
            GpuPriority = null!, IoPrio = null!, Affinity = null!, Cpu = null!, Gpu = null!,
            MemRam = null!,
            AppName = null!, Path = null!;
        public TextBlock Rules = null!;
        public Grid Cols = null!;   // grilla interna con las columnas (para redimensionar)
        public Border Container = null!;   // borde de la card (para el resaltado de selección)
        // Chevron de expandir/colapsar hijos (visible solo si tiene hijos).
        public FontIcon Chevron = null!;
        public StackPanel NamePanel = null!;  // panel del nombre (para indentar hijos)
        public bool HasChildren;
    }

    private sealed class ProcInfo
    {
        public int Pid;
        public int ParentPid;         // PID del proceso padre (para árbol)
        public string Name = "";      // nombre del proceso CON .exe
        public string Exe = "";
        public string? Path;
        public string User = "—";
        public double Cpu;
        public double Gpu;
        public string Priority = "—";
        public int PriorityRank = 2;   // 0=Idle .. 5=RealTime (para ordenar)
        public long Affinity;
        public int? GpuPrio;
        public int? IoPrio;
        public long WorkingSet;   // bytes de RAM (working set): columna "Memoria (RAM)"
        public ProcessRule? EffectiveRule;
        public string AppName = "";
    }

    // ===== P/Invoke: usuario del proceso =====
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountSid(string? lpSystemName, IntPtr sid, StringBuilder lpName, ref uint cchName, StringBuilder lpReferencedDomainName, ref uint cchReferencedDomainName, out int peUse);

    private static string? GetProcessUser(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(h, TokenQuery, out var tok))
                return null;
            try
            {
                GetTokenInformation(tok, TokenUser, IntPtr.Zero, 0, out var len);
                if (len <= 0) return null;
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!GetTokenInformation(tok, TokenUser, buf, len, out _))
                        return null;
                    // TOKEN_USER.User.Sid es el primer campo del buffer.
                    IntPtr sid = Marshal.ReadIntPtr(buf);
                    var name = new StringBuilder(256);
                    var domain = new StringBuilder(256);
                    uint n = 256, d = 256;
                    if (LookupAccountSid(null, sid, name, ref n, domain, ref d, out _))
                        return name.Length > 0 ? name.ToString() : null;
                    return null;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(tok); }
        }
        finally { CloseHandle(h); }
    }

    public ProcesosPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _processService = App.Services.GetRequiredService<IProcessService>();
        _cpuPowerService = App.Services.GetRequiredService<ICpuPowerService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();

        _colWidths = Columns.Select(c => c.Width).ToArray();
        _colOrder = Enumerable.Range(0, Columns.Length).ToArray();
        BuildHeader();

        // La cabecera se desplaza con un transform (sin ScrollViewer: la rueda no
        // la scrollea sola) y se recorta al ancho visible de las filas.
        HeaderGrid.RenderTransform = new TranslateTransform();
        HeaderHost.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(HeaderHost_PointerWheelChanged), true);
        HeaderHost.SizeChanged += (_, _) => UpdateHeaderClip();
        RowsScroll.SizeChanged += (_, _) =>
        {
            UpdateFitScale();
            ApplyColumnWidths();
            UpdateScrollbarInsets();
        };
        RowsScroll.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, OnScrollableChanged);
        RowsScroll.RegisterPropertyChangedCallback(ScrollViewer.ScrollableWidthProperty, OnScrollableChanged);

        // Scrollbars nativos en sus canaletas (mismo estilo que los del ScrollViewer,
        // pero fuera del viewport para que nunca tapen contenido).
        HScrollBar.Scroll += (_, e) =>
        {
            double maxOff = TotalWidth - RowsScroll.ViewportWidth;
            RowsScroll.ChangeView(Math.Clamp(e.NewValue, 0, maxOff), null, null, true);
        };
        VScrollBar.Scroll += (_, e) =>
        {
            double maxOff = RowsScroll.ScrollableHeight;
            RowsScroll.ChangeView(null, Math.Clamp(e.NewValue, 0, maxOff), null, true);
        };
        UpdateScrollbarInsets();
    }

    private void OnScrollableChanged(DependencyObject sender, DependencyProperty dp)
    {
        UpdateScrollbarInsets();
    }

    private void OnLanguageChanged()
    {
        // Re-construir cabecera y filas con los textos traducidos.
        BuildHeader();
        UpdatePauseButton();
        UpdateFilterButtons();
        UpdateTreeButton();
        ClearRows();
        _ = RefreshAsync();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        I18n.LanguageChanged += OnLanguageChanged;
        if (_timer == null)
        {
            _timer = DispatcherQueue.CreateTimer();
            // Intervalo fijo de 1 s (el desplegable se quitó: no hay motivo para
            // actualizar más lento la tabla de procesos).
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (_, _) => _ = RefreshAsync();
        }
        _timer.Start();
        UpdatePauseButton();
        UpdateFilterButtons();
        UpdateTreeButton();
        _ = RefreshAsync();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        I18n.LanguageChanged -= OnLanguageChanged;
        _timer?.Stop();
        _timer = null;
        ClearRows();
        // Liberar la query Pdh de los contadores GPU.
        if (_gpuQuery != IntPtr.Zero)
        {
            PdhCloseQuery(_gpuQuery);
            _gpuQuery = IntPtr.Zero;
            _gpuCounterHandles.Clear();
            _gpuPids.Clear();
        }
    }

    private void ClearRows()
    {
        RowsPanel.Children.Clear();
        _rows.Clear();
        _cpuSamples.Clear();
        _staticCache.Clear();
        _pathCache.Clear();
        _selectedPids.Clear();
        // Tras el layout siguiente, re-evaluar el espacio que reservan los scrollbars.
        _ = DispatcherQueue.TryEnqueue(UpdateScrollbarInsets);
    }

    // ===== Cabecera de columnas (ordenable, como Process Lasso) =====

    private void AddColumns(Grid grid)
    {
        // Los anchos siguen a la columna: definición posicional = ancho de la columna en esa posición.
        for (int p = 0; p < _colOrder.Length; p++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Scaled(_colOrder[p])) });
    }

    private readonly HashSet<int> _selectedPids = new();

    private int _sortColumn;          // columna activa (índice de Columns)
    private bool _sortDesc;
    private int[] _colOrder = null!;  // orden de visualización (índices de Columns)
    private readonly List<TextBlock> _headerTexts = new();

    // Estado del arrastre de cabecera (mover columnas con el mouse).
    private int _pressPos = -1;
    private bool _dragHappened;
    private double _pressX, _pressY;

    private void BuildHeader()
    {
        HeaderGrid.Children.Clear();
        HeaderGrid.ColumnDefinitions.Clear();
        AddColumns(HeaderGrid);
        HeaderGrid.Width = TotalWidth;
        HeaderGrid.HorizontalAlignment = HorizontalAlignment.Left;
        _headerTexts.Clear();

        for (int pos = 0; pos < Columns.Length; pos++)
        {
            int col = _colOrder[pos];

            // Separador visual entre columnas (borde izquierdo, salvo la primera).
            if (pos > 0)
            {
                var sep = new Rectangle
                {
                    Width = 1,
                    Fill = (Brush)ThemeBrushes.Get("SensorGridLineBrush"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsHitTestVisible = false
                };
                Grid.SetColumn(sep, pos);
                HeaderGrid.Children.Add(sep);
            }

            // Celda de cabecera: clic ordena, arrastrar mueve la columna.
            var tb = new TextBlock
            {
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)ThemeBrushes.Get("SecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                // Estilo monitor de sensores: la cabecera alinea con el dato (derecha),
                // salvo la primera columna (nombre) que queda a la izquierda.
                TextAlignment = pos > 0 ? TextAlignment.Right : TextAlignment.Left,
                Margin = new Thickness(8, 0, 8, 0)
            };
            var cell = new Grid { Tag = pos };
            cell.Children.Add(tb);
            cell.PointerPressed += HeaderCell_PointerPressed;
            cell.PointerMoved += HeaderCell_PointerMoved;
            cell.PointerReleased += HeaderCell_PointerReleased;
            cell.PointerCaptureLost += HeaderCell_PointerCaptureLost;
            Grid.SetColumn(cell, pos);
            HeaderGrid.Children.Add(cell);
            _headerTexts.Add(tb);

            // Manija de redimensionado en el borde derecho (arrastrar para ajustar).
            if (pos < Columns.Length - 1)
            {
                var handle = new Rectangle
                {
                    Width = 6,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Tag = pos
                };
                handle.PointerPressed += Handle_PointerPressed;
                handle.PointerMoved += Handle_PointerMoved;
                handle.PointerReleased += Handle_PointerReleased;
                handle.PointerCaptureLost += Handle_PointerCaptureLost;
                handle.PointerEntered += (_, _) => ProtectedCursor = ResizeCursor;
                handle.PointerExited += (_, _) => ProtectedCursor = DefaultCursor;
                Grid.SetColumn(handle, pos);
                HeaderGrid.Children.Add(handle);
            }
        }
        UpdateHeaderIndicators();
    }

    private void SortBy(int col)
    {
        if (_sortColumn == col) _sortDesc = !_sortDesc;
        else { _sortColumn = col; _sortDesc = false; }
        UpdateHeaderIndicators();
        _ = RefreshAsync();
    }

    private void UpdateHeaderIndicators()
    {
        for (int pos = 0; pos < _headerTexts.Count; pos++)
        {
            int col = _colOrder[pos];
            string text = I18n.T(Columns[col].Header);
            if (col == _sortColumn)
                text += _sortDesc ? " ▼" : " ▲";
            _headerTexts[pos].Text = text;
        }
    }

    // ===== Mover columnas arrastrando la cabecera =====

    private void HeaderCell_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressPos = (int)((Grid)sender).Tag;
        _pressX = e.GetCurrentPoint(this).Position.X;
        _pressY = e.GetCurrentPoint(this).Position.Y;
        _dragHappened = false;
        ((Grid)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void HeaderCell_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pressPos < 0) return;
        var pt = e.GetCurrentPoint(this).Position;
        // Superar el umbral convierte el clic en un arrastre (mover columna).
        if (!_dragHappened && (Math.Abs(pt.X - _pressX) > 8 || Math.Abs(pt.Y - _pressY) > 8))
            _dragHappened = true;
        if (_dragHappened) e.Handled = true;
    }

    private void HeaderCell_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var cell = (Grid)sender;
        int pos = (int)cell.Tag;
        // Ordenar/mover ANTES de soltar la captura: ReleasePointerCapture dispara
        // PointerCaptureLost (que resetea _pressPos) y si se soltaba primero el
        // clic nunca llegaba a ordenar.
        if (_pressPos >= 0)
        {
            if (_dragHappened)
            {
                int target = PositionFromX(e.GetCurrentPoint(HeaderGrid).Position.X);
                if (target >= 0 && target != _pressPos)
                    MoveColumn(_pressPos, target);
            }
            else
            {
                SortBy(_colOrder[pos]);
            }
        }
        _pressPos = -1;
        _dragHappened = false;
        cell.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void HeaderCell_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _pressPos = -1;
        _dragHappened = false;
    }

    private int PositionFromX(double x)
    {
        double acc = 0;
        for (int p = 0; p < _colOrder.Length; p++)
        {
            double w = Scaled(_colOrder[p]);
            if (x < acc + w / 2) return p;   // mitad de la columna = destino
            acc += w;
        }
        return _colOrder.Length - 1;
    }

    private void MoveColumn(int fromPos, int toPos)
    {
        if (fromPos == toPos) return;
        int col = _colOrder[fromPos];
        if (fromPos < toPos)
        {
            for (int p = fromPos; p < toPos; p++) _colOrder[p] = _colOrder[p + 1];
            _colOrder[toPos] = col;
        }
        else
        {
            for (int p = fromPos; p > toPos; p--) _colOrder[p] = _colOrder[p - 1];
            _colOrder[toPos] = col;
        }
        // Al reordenar cambian las posiciones: resetear los cachés de anchos y
        // re-aplicar a todas las filas (UpdateFitScale).
        UpdateFitScale();
        BuildHeader();
        ClearRows();
        _ = RefreshAsync();
    }

    // ===== Barra de herramientas: filtro de procesos + pausa =====

    private void FilterAllButton_Click(object sender, RoutedEventArgs e) => SetUserFilter(false);

    private void FilterUserButton_Click(object sender, RoutedEventArgs e) => SetUserFilter(true);

    private void SetUserFilter(bool onlyUser)
    {
        if (_onlyUserProcesses == onlyUser) return;
        _onlyUserProcesses = onlyUser;
        UpdateFilterButtons();
        ClearRows();
        _ = RefreshAsync();
    }

    private void UpdateFilterButtons()
    {
        if (FilterAllButton == null || FilterUserButton == null) return;
        FilterAllLabel.Text = I18n.T("Todos los procesos");
        FilterUserLabel.Text = I18n.T("Procesos del usuario");
        ApplyFilterStyle(FilterAllButton, FilterAllIcon, FilterAllLabel, !_onlyUserProcesses);
        ApplyFilterStyle(FilterUserButton, FilterUserIcon, FilterUserLabel, _onlyUserProcesses);
    }

    // Valores por defecto (tema) capturados antes de aplicar el primer estilo, para
    // restaurarlos al desactivar el filtro. No se usan claves del framework
    // (TextFillColorPrimaryBrush) porque ThemeBrushes.Get solo garantiza las de la app.
    private static readonly Dictionary<Button, (Brush Bg, Brush Fg, Brush IconFg, Brush LabelFg)> FilterDefaults = new();

    private static void ApplyFilterStyle(Button button, FontIcon icon, TextBlock label, bool active)
    {
        if (!FilterDefaults.TryGetValue(button, out var def))
        {
            def = (button.Background, button.Foreground ?? ThemeBrushes.Get("MutedBrush"),
                   icon.Foreground ?? ThemeBrushes.Get("MutedBrush"),
                   label.Foreground ?? ThemeBrushes.Get("MutedBrush"));
            FilterDefaults[button] = def;
        }
        button.Background = active ? ThemeBrushes.Get("AccentBrush") : def.Bg;
        button.Foreground = active ? ThemeBrushes.Get("AccentForegroundBrush") : def.Fg;
        icon.Foreground = active ? ThemeBrushes.Get("AccentForegroundBrush") : def.IconFg;
        label.Foreground = active ? ThemeBrushes.Get("AccentForegroundBrush") : def.LabelFg;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
        {
            _timer?.Stop();
            StatusText.Text = I18n.T("Actualización pausada");
            StatusText.Foreground = Feedback.MutedBrush;
            StatusText.Visibility = Visibility.Visible;
        }
        else
        {
            StatusText.Visibility = Visibility.Collapsed;
            _timer?.Start();
            _ = RefreshAsync();
        }
        UpdatePauseButton();
    }

    private void UpdatePauseButton()
    {
        PauseIcon.Glyph = _paused ? "\uE768" : "\uE769";  // ▶ / ⏸
        PauseLabel.Text = I18n.T(_paused ? "Reanudar" : "Pausar");
    }

    private void TreeButton_Click(object sender, RoutedEventArgs e)
    {
        _treeMode = !_treeMode;
        // Al entrar a árbol se arranca colapsado; al salir no se usan expansiones.
        _expandedPids.Clear();
        UpdateTreeButton();
        ClearRows();
        _ = RefreshAsync();
    }

    private void UpdateTreeButton()
    {
        if (TreeButton == null) return;
        // La etiqueta dice lo que hace el clic: en plano → "Vista árbol", en árbol → "Vista plana".
        TreeLabel.Text = I18n.T(_treeMode ? "Vista plana" : "Vista árbol");
        ApplyFilterStyle(TreeButton, TreeIcon, TreeLabel, _treeMode);
    }

    // ===== Redimensionado de columnas (arrastrar el borde de la cabecera) =====

    // Coalescing: el mouse emite decenas/cientos de eventos por segundo y aplicar el
    // ancho a TODAS las filas en cada uno es lo que laggea. Se aplica como mucho una
    // vez por frame (~16 ms) y el último ancho pendiente se aplica al soltar, para
    // que la columna quede exactamente donde se soltó.
    private const long ResizeApplyMs = 16;
    private long _lastResizeApply;
    private double _pendingResizeWidth = double.NaN;

    private void Handle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var rect = (Rectangle)sender;
        int pos = rect.Tag is int c ? c : -1;
        if (pos < 0 || pos >= _colOrder.Length - 1) return;
        _resizeCol = pos;
        _resizeStartX = e.GetCurrentPoint(this).Position.X;
        _resizeStartWidth = _colWidths[_colOrder[pos]];
        _pendingResizeWidth = double.NaN;
        rect.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Handle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeCol < 0) return;
        double delta = e.GetCurrentPoint(this).Position.X - _resizeStartX;
        double w = Math.Clamp(_resizeStartWidth + delta, 50, 700);
        long now = Environment.TickCount64;
        if (now - _lastResizeApply >= ResizeApplyMs)
        {
            _lastResizeApply = now;
            SetColumnWidth(_colOrder[_resizeCol], w);
        }
        else
        {
            _pendingResizeWidth = w;
        }
    }

    private void Handle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var rect = (Rectangle)sender;
        rect.ReleasePointerCapture(e.Pointer);
        FlushPendingResizeWidth();
        _resizeCol = -1;
        ProtectedCursor = DefaultCursor;
    }

    private void Handle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        FlushPendingResizeWidth();
        _resizeCol = -1;
        ProtectedCursor = DefaultCursor;
    }

    private void FlushPendingResizeWidth()
    {
        if (!double.IsNaN(_pendingResizeWidth))
        {
            SetColumnWidth(_colOrder[_resizeCol], _pendingResizeWidth);
            _pendingResizeWidth = double.NaN;
        }
    }

    private void SetColumnWidth(int col, double width)
    {
        _colWidths[col] = width;
        // Solo cambia esta columna: la izquierda queda estática y la derecha conserva
        // su ancho (si el total desborda el viewport aparece el scroll horizontal).
        ApplyColumnWidths();
    }

    private void RowsScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Cabecera sincronizada con las filas en cada frame (eventos intermedios
        // incluidos, mientras se arrastra el scrollbar).
        ((TranslateTransform)HeaderGrid.RenderTransform).X = -RowsScroll.HorizontalOffset;
        UpdateHeaderClip();
        UpdateHorizontalBar();
        // Si el redimensionado dejó filas fuera de pantalla con ancho pendiente,
        // completarlas ahora que scrolleamos (las recién visibles deben tener su ancho).
        if (_fullApplyPending)
        {
            _fullApplyPending = false;
            _forceRows = true;
            ApplyColumnWidths();
        }
    }

    // ===== Scrollbars propios en sus canaletas (fuera del viewport, nunca tapan nada) =====

    private void UpdateScrollbarInsets()
    {
        UpdateHeaderClip();
        UpdateHorizontalBar();
        UpdateVerticalBar();
    }

    private void UpdateHorizontalBar()
    {
        double total = TotalWidth;
        double vp = RowsScroll.ViewportWidth;
        if (vp <= 0 || total <= vp)
        {
            HScrollBar.Visibility = Visibility.Collapsed;
            return;
        }
        HScrollBar.Visibility = Visibility.Visible;
        double maxOff = total - vp;
        HScrollBar.Minimum = 0;
        HScrollBar.Maximum = maxOff;
        HScrollBar.ViewportSize = vp;
        HScrollBar.Value = Math.Min(Math.Max(0, RowsScroll.HorizontalOffset), maxOff);
    }

    private void UpdateVerticalBar()
    {
        double vp = RowsScroll.ViewportHeight;
        double maxOff = RowsScroll.ScrollableHeight;
        if (vp <= 0 || maxOff <= 0)
        {
            VScrollBar.Visibility = Visibility.Collapsed;
            return;
        }
        VScrollBar.Visibility = Visibility.Visible;
        VScrollBar.Minimum = 0;
        VScrollBar.Maximum = maxOff;
        VScrollBar.ViewportSize = vp;
        VScrollBar.Value = Math.Min(Math.Max(0, RowsScroll.VerticalOffset), maxOff);
    }

    private void UpdateHeaderClip()
    {
        double w = RowsScroll.ViewportWidth;
        if (w <= 0) w = HeaderHost.ActualWidth;
        if (w < 0) w = 0;
        HeaderHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, w, HeaderHost.ActualHeight) };
    }

    private void HeaderHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // La rueda sobre la cabecera no la scrollea (no es scrolleable): se reenvía
        // al área de filas, para que el gesto siga funcionando sobre los nombres.
        var props = e.GetCurrentPoint(HeaderHost).Properties;
        e.Handled = true;
        double delta = props.MouseWheelDelta * 0.4;   // ~48 px por muesca
        if (props.IsHorizontalMouseWheel)
            RowsScroll.ChangeView(RowsScroll.HorizontalOffset - delta, null, null, true);
        else
            RowsScroll.ChangeView(null, RowsScroll.VerticalOffset - delta, null, true);
    }

    // ===== Snapshot nativo de procesos (NtQuerySystemInformation) =====
    // Una sola llamada al kernel devuelve todos los procesos con su tiempo de CPU
    // (user+kernel). Es lo que usa el Administrador de tareas y Process Lasso:
    // cero objetos Process y casi cero syscalls por tick en estado estable.

    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    // ===== Snapshot Toolhelp (fallback: obtener ParentPid cuando el nativo falla) =====

    private const uint Th32csSnapprocess = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    /// <summary>
    /// Mapa PID → ParentPid usando CreateToolhelp32Snapshot. Se usa cuando el
    /// snapshot nativo (NtQuerySystemInformation) falla: el fallback managed de
    /// Process.GetProcesses() no trae el PID del padre, así que sin esto los
    /// chevrons de árbol nunca aparecen (todos los procesos quedan con ParentPid=0).
    /// </summary>
    private static Dictionary<int, int>? GetParentPidMap()
    {
        try
        {
            var map = new Dictionary<int, int>();
            IntPtr snap = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
            if (snap == IntPtr.Zero) return null;
            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (!Process32First(snap, ref entry)) return map;
                do
                {
                    int pid = (int)entry.th32ProcessID;
                    int parent = (int)entry.th32ParentProcessID;
                    if (parent > 0 && parent != pid)
                        map[pid] = parent;
                } while (Process32Next(snap, ref entry));
            }
            finally { CloseHandle(snap); }
            return map;
        }
        catch { return null; }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

    private sealed class NativeProcInfo
    {
        public int Pid;
        public int ParentPid;       // InheritedFromUniqueProcessId (para árbol padre→hijos)
        public string Name = "";   // sin .exe
        public int Threads;         // subprocesos del proceso
        public long CpuTime;       // ticks de 100 ns (user+kernel)
        public long WorkingSet;
    }

    private static List<NativeProcInfo>? GetNativeSnapshot()
    {
        try
        {
            int len = 1 << 20;   // 1 MB inicial
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buf, len, out int needed);
                if (status == unchecked((int)StatusInfoLengthMismatch) && needed > len)
                {
                    Marshal.FreeHGlobal(buf);
                    len = needed + 64;
                    buf = Marshal.AllocHGlobal(len);
                    status = NtQuerySystemInformation(SystemProcessInformation, buf, len, out needed);
                }
                if (status != 0) return null;

                var list = new List<NativeProcInfo>(256);
                long baseAddr = buf.ToInt64();
                long offset = 0;
                while (true)
                {
                    long p = baseAddr + offset;
                    int next = Marshal.ReadInt32((IntPtr)p);
                    // Layout x64 de SYSTEM_PROCESS_INFORMATION (Win10/11), con
                    // alineación a 8 bytes en campos HANDLE/SIZE_T:
                    //   +0 NextEntryOffset        +4 NumberOfThreads
                    //   +8 WorkingSetPrivateSize  +16 HardFaultCount
                    //   +20 ThreadsHighWatermark  +24 CycleTime
                    //   +32 CreateTime            +40 UserTime
                    //   +48 KernelTime            +56 ImageName.Length
                    //   +58 ImageName.MaxLength   +64 ImageName.Buffer
                    //   +72 BasePriority          +80 UniqueProcessId
                    //   +88 InheritedFromUniqueProcessId (PID padre)
                    //   +96 HandleCount           +100 SessionId
                    //   +104 UniqueProcessKey     +112 PeakVirtualSize
                    //   +120 VirtualSize          +128 PageFaultCount
                    //   +136 PeakWorkingSetSize   +144 WorkingSetSize
                    int threads = Marshal.ReadInt32((IntPtr)(p + 4));
                    ushort nameLen = (ushort)Marshal.ReadInt16((IntPtr)(p + 56));
                    IntPtr namePtr = Marshal.ReadIntPtr((IntPtr)(p + 64));
                    long parentPid = Marshal.ReadInt64((IntPtr)(p + 88));
                    long pid = Marshal.ReadInt64((IntPtr)(p + 80));
                    if (pid > 0 && pid <= int.MaxValue)
                    {
                        string name = nameLen > 0 && namePtr != IntPtr.Zero
                            ? Marshal.PtrToStringUni(namePtr, nameLen / 2) ?? ""
                            : "";
                        // El ImageName del kernel ya trae la extensión (ej.
                        // "chrome.exe"): normalizar a nombre SIN extensión para
                        // que CollectSnapshot le agregue ".exe" una sola vez
                        // (igual que Process.ProcessName en el fallback managed).
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            name = name[..^4];
                        list.Add(new NativeProcInfo
                        {
                            Pid = (int)pid,
                            ParentPid = parentPid > 0 && parentPid <= int.MaxValue ? (int)parentPid : 0,
                            Name = name,
                            Threads = Math.Max(0, threads),
                            CpuTime = Marshal.ReadInt64((IntPtr)(p + 40)) + Marshal.ReadInt64((IntPtr)(p + 48)),
                            WorkingSet = Marshal.ReadInt64((IntPtr)(p + 144))   // WorkingSetSize (SIZE_T en x64)
                        });
                    }
                    if (next == 0) break;
                    offset += next;
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
    }

    // ===== Pdh: contadores de GPU Engine en lote (una recolección por tick) =====

    [DllImport("pdh.dll")]
    private static extern int PdhOpenQuery(IntPtr szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll")]
    private static extern int PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PdhFmtCounterValue pValue);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr hQuery);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public long CTime;
        public double CValue;
    }

    private const uint PdhFmtDouble = 0x00000200;

    // ===== Recolección (fondo) =====

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            int col = _sortColumn;
            bool desc = _sortDesc;
            var snapshot = await Task.Run(() => CollectSnapshot(col, desc));
            UpdateRows(snapshot);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ProcesosPage: refresco: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    // Conteos del último snapshot (para el contador de la barra de herramientas).
    // Salen del MISMO snapshot nativo (una sola llamada al kernel por tick): no hay
    // polling adicional.
    private int _procTotal;
    private int _threadTotal;

    private List<ProcInfo> CollectSnapshot(int sortColumn, bool desc)
    {
        var result = new List<ProcInfo>();
        _procTotal = 0;
        _threadTotal = 0;
        Dictionary<string, ProcessRule> persistent;
        try { persistent = _processService.GetRulesCached(); } catch { persistent = new(StringComparer.OrdinalIgnoreCase); }

        UpdateGpuCounters();
        var now = DateTime.UtcNow;
        int cpuCount = Math.Max(1, Environment.ProcessorCount);

        // %GPU: se relee cada 2 s (no cada tick); entre lecturas se reutiliza el
        // último mapa para que la columna no parpadee a 0.
        var gpuByPid = _lastGpuByPid;
        if ((now - _gpuReadAt) >= GpuReadInterval)
        {
            _gpuReadAt = now;
            _lastGpuByPid = ReadGpuPerPid();
            gpuByPid = _lastGpuByPid;
        }

        // Snapshot nativo: UNA llamada devuelve todos los procesos con su tiempo de
        // CPU (user+kernel), sin crear objetos Process por tick. Fallback a la
        // enumeración managed si la llamada nativa falla.
        var natives = GetNativeSnapshot();
        if (natives != null)
        {
            foreach (var np in natives)
            {
                try
                {
                    // Conteos del sistema (antes del filtro de usuario).
                    _procTotal++;
                    _threadTotal += np.Threads;

                    int pid = np.Pid;
                    string exe = np.Name + ".exe";
                    var info = new ProcInfo
                    {
                        Pid = pid,
                        ParentPid = np.ParentPid,
                        Name = exe,
                        Exe = exe,
                        WorkingSet = np.WorkingSet
                    };

                    // %CPU (delta del tiempo nativo entre muestras).
                    try
                    {
                        var cpu = new TimeSpan(np.CpuTime);
                        if (_cpuSamples.TryGetValue(pid, out var prev) && prev.At != default)
                        {
                            double spanMs = (now - prev.At).TotalMilliseconds;
                            double deltaMs = (cpu - prev.Cpu).TotalMilliseconds;
                            if (spanMs > 0)
                                info.Cpu = Math.Clamp(deltaMs / (spanMs * cpuCount) * 100.0, 0, 999);
                        }
                        _cpuSamples[pid] = (cpu, now);
                    }
                    catch { }

                    FillCachedFields(info, pid, now, null);

                    // %GPU (contadores GPU Engine por pid).
                    if (gpuByPid.TryGetValue(pid, out var gpu)) info.Gpu = Math.Clamp(gpu, 0, 999);

                    FillRuleAndAppName(info, exe, persistent);

                    if (_onlyUserProcesses && !string.Equals(info.User, CurrentUser, StringComparison.OrdinalIgnoreCase))
                        continue;

                    result.Add(info);
                }
                catch { }
            }
        }
        else
        {
            // Fallback (enumeración managed): el snapshot nativo falló. Se obtiene
            // ParentPid con CreateToolhelp32Snapshot para que el árbol funcione igual.
            _loggingService.LogWarning("ProcesosPage: snapshot nativo falló, usando fallback managed con Toolhelp");
            var parentMap = GetParentPidMap();
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.HasExited) continue;
                        // Conteos del sistema (antes del filtro de usuario).
                        _procTotal++;
                        try { _threadTotal += p.Threads.Count; } catch { }

                        int pid = p.Id;
                        string exe = p.ProcessName + ".exe";
                        var info = new ProcInfo
                        {
                            Pid = pid,
                            ParentPid = parentMap != null && parentMap.TryGetValue(pid, out var pp) ? pp : 0,
                            Name = exe,
                            Exe = exe,
                            WorkingSet = p.WorkingSet64
                        };

                        // %CPU (delta de TotalProcessorTime entre muestras).
                        try
                        {
                            if (_cpuSamples.TryGetValue(pid, out var prev) && prev.At != default)
                            {
                                double spanMs = (now - prev.At).TotalMilliseconds;
                                double deltaMs = (p.TotalProcessorTime - prev.Cpu).TotalMilliseconds;
                                if (spanMs > 0)
                                    info.Cpu = Math.Clamp(deltaMs / (spanMs * cpuCount) * 100.0, 0, 999);
                            }
                            _cpuSamples[pid] = (p.TotalProcessorTime, now);
                        }
                        catch { }

                        FillCachedFields(info, pid, now, p);

                        // %GPU (contadores GPU Engine por pid).
                        if (gpuByPid.TryGetValue(pid, out var gpu)) info.Gpu = Math.Clamp(gpu, 0, 999);

                        FillRuleAndAppName(info, exe, persistent);

                        if (_onlyUserProcesses && !string.Equals(info.User, CurrentUser, StringComparison.OrdinalIgnoreCase))
                            continue;

                        result.Add(info);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _loggingService.LogWarning($"ProcesosPage: enumerar procesos: {ex.Message}");
            }
        }

        result.Sort((a, b) => CompareBy(a, b, sortColumn));
        if (desc) result.Reverse();
        return result;
    }

    private void FillRuleAndAppName(ProcInfo info, string exe, Dictionary<string, ProcessRule> persistent)
    {
        // Regla efectiva: sesión (Actual) gana campo por campo sobre la guardada.
        try
        {
            var session = _processService.GetSessionRule(exe);
            persistent.TryGetValue(exe, out var pr);
            info.EffectiveRule = Merge(session, pr);
        }
        catch { }
        info.AppName = GetAppName(info.Path ?? exe);
    }

    /// <summary>
    /// Campos que abren handles del proceso (ruta, usuario, prioridad, afinidad,
    /// prioridad GPU): todos cacheados por pid. En estado estable el tick solo
    /// lee cachés; lo costoso se refresca como mucho cada 10 s.
    /// </summary>
    private void FillCachedFields(ProcInfo info, int pid, DateTime now, Process? p)
    {
        // Usuario (cacheado por pid: no cambia).
        if (!_userCache.TryGetValue(pid, out var user))
        {
            try { user = GetProcessUser(pid) ?? "—"; } catch { user = "—"; }
            _userCache[pid] = user;
        }
        info.User = user;

        // Ruta del exe (cacheada por pid: no cambia mientras el proceso vive).
        if (!_pathCache.TryGetValue(pid, out var path))
        {
            try
            {
                if (p != null) path = _processService.GetProcessPath(p);
                else { using var pp = Process.GetProcessById(pid); path = _processService.GetProcessPath(pp); }
            }
            catch { path = null; }
            _pathCache[pid] = path;
        }
        info.Path = path;

        // Prioridad de CPU, afinidad y prioridad de GPU: abren el handle del proceso
        // (+ D3DKMT). Cacheados, se refrescan como mucho cada 10 s.
        if (_staticCache.TryGetValue(pid, out var st) && (now - st.At) < StaticCacheTtl)
        {
            info.Priority = st.Priority;
            info.PriorityRank = st.Rank;
            info.Affinity = st.Affinity;
            info.GpuPrio = st.GpuPrio;
            info.IoPrio = st.IoPrio;
        }
        else
        {
            string prio = "—"; int rank = 2; long aff = 0; int? gpu = null; int? io = null;
            try
            {
                using var pp = p ?? Process.GetProcessById(pid);
                try
                {
                    var pc = pp.PriorityClass;
                    prio = PriorityName(pc);
                    rank = pc switch
                    {
                        ProcessPriorityClass.Idle => 0,
                        ProcessPriorityClass.BelowNormal => 1,
                        ProcessPriorityClass.Normal => 2,
                        ProcessPriorityClass.AboveNormal => 3,
                        ProcessPriorityClass.High => 4,
                        ProcessPriorityClass.RealTime => 5,
                        _ => 2
                    };
                }
                catch { }
                // Lectura con PROCESS_QUERY_LIMITED_INFORMATION: el getter managed
                // falla en procesos protegidos por anti-cheat; este muestra la
                // máscara real igual (la misma que ve el Administrador de tareas).
                aff = _processService.GetAffinity(pid);
            }
            catch { }
            try { gpu = _processService.GetGpuPriority(pid); } catch { }
            try { io = _processService.GetIoPriority(pid); } catch { }
            _staticCache[pid] = (prio, rank, aff, gpu, io, now);
            info.Priority = prio;
            info.PriorityRank = rank;
            info.Affinity = aff;
            info.GpuPrio = gpu;
            info.IoPrio = io;
        }
    }

    /// <summary>Comparador por columna (mismo índice que Columns).</summary>
    private static int CompareBy(ProcInfo a, ProcInfo b, int col) => col switch
    {
        1 => string.Compare(a.User, b.User, StringComparison.OrdinalIgnoreCase),
        2 => 0,
        3 => string.Compare(RuleSummary(a.EffectiveRule), RuleSummary(b.EffectiveRule), StringComparison.OrdinalIgnoreCase),
        4 => a.PriorityRank.CompareTo(b.PriorityRank),
        5 => (a.GpuPrio ?? -1).CompareTo(b.GpuPrio ?? -1),
        6 => (a.IoPrio ?? -1).CompareTo(b.IoPrio ?? -1),
        7 => a.Affinity.CompareTo(b.Affinity),
        8 => a.Cpu.CompareTo(b.Cpu),
        9 => a.Gpu.CompareTo(b.Gpu),
        10 => a.WorkingSet.CompareTo(b.WorkingSet),
        11 => string.Compare(a.AppName, b.AppName, StringComparison.OrdinalIgnoreCase),
        12 => string.Compare(a.Path ?? "", b.Path ?? "", StringComparison.OrdinalIgnoreCase),
        _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
    };

    private static string PriorityName(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.Idle => I18n.T("Mínima"),
        ProcessPriorityClass.BelowNormal => I18n.T("Baja"),
        ProcessPriorityClass.Normal => I18n.T("Normal"),
        ProcessPriorityClass.AboveNormal => I18n.T("Por encima de lo normal"),
        ProcessPriorityClass.High => I18n.T("Alta"),
        ProcessPriorityClass.RealTime => I18n.T("Tiempo real"),
        _ => c.ToString()
    };

    private string GetAppName(string pathOrExe)
    {
        try
        {
            string exe = Path.GetFileName(pathOrExe);
            if (_appNameCache.TryGetValue(exe, out var cached)) return cached;
            string app = exe;
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(pathOrExe);
                if (!string.IsNullOrWhiteSpace(fv.FileDescription))
                    app = fv.FileDescription.Trim();
                else if (!string.IsNullOrWhiteSpace(fv.ProductName))
                    app = fv.ProductName.Trim();
                else if (!string.IsNullOrWhiteSpace(fv.CompanyName))
                    app = fv.CompanyName.Trim();
            }
            catch { }
            _appNameCache[exe] = app;
            return app;
        }
        catch { return pathOrExe; }
    }

    private void UpdateGpuCounters()
    {
        try
        {
            // Enumerar instancias y (re)construir la query Pdh es caro: como mucho
            // cada 30 s. La LECTURA (PdhCollectQueryData) es una sola llamada por tick.
            var now = DateTime.UtcNow;
            if ((now - _gpuCounterEnumAt) < GpuCounterEnumTtl) return;
            _gpuCounterEnumAt = now;

            var category = new PerformanceCounterCategory("GPU Engine");
            string[] instances = category.GetInstanceNames();

            // Reconstruir la query con los contadores actuales.
            if (_gpuQuery != IntPtr.Zero) { PdhCloseQuery(_gpuQuery); _gpuQuery = IntPtr.Zero; }
            _gpuCounterHandles.Clear();
            _gpuPids.Clear();

            if (instances.Length == 0 || PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out _gpuQuery) != 0)
                return;

            foreach (var inst in instances)
            {
                var m = Regex.Match(inst, @"pid_(\d+)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[1].Value, out var pid)) continue;
                if (PdhAddEnglishCounter(_gpuQuery, @"\GPU Engine(" + inst + @")\Utilization Percentage", IntPtr.Zero, out var handle) != 0)
                    continue;
                _gpuCounterHandles[inst] = handle;
                _gpuPids[inst] = pid;
            }
            // Primera recolección: siembra el estado inicial (la utilidad es un rate,
            // necesita dos muestras; la primera da 0).
            PdhCollectQueryData(_gpuQuery);
        }
        catch { /* sin contadores GPU (máquina sin GPU Engine): %GPU queda en 0 */ }
    }

    private Dictionary<int, double> ReadGpuPerPid()
    {
        var map = new Dictionary<int, double>();
        if (_gpuQuery == IntPtr.Zero || _gpuCounterHandles.Count == 0) return map;
        try
        {
            // Una sola recolección para TODOS los contadores, y luego formato por
            // contador (barato). Antes: una recolección Pdh por contador por tick.
            if (PdhCollectQueryData(_gpuQuery) != 0) return map;
            foreach (var (inst, handle) in _gpuCounterHandles)
            {
                if (!_gpuPids.TryGetValue(inst, out var pid)) continue;
                if (PdhGetFormattedCounterValue(handle, PdhFmtDouble, out _, out var v) != 0) continue;
                map[pid] = map.GetValueOrDefault(pid) + Math.Clamp(v.CValue, 0, 999);
            }
        }
        catch { }
        return map;
    }

    // ===== Filas =====

    private void UpdateRows(List<ProcInfo> items)
    {
        // Guardar el snapshot para que el click del chevron pueda reordenar.
        _lastSnapshot = items;
        // Contador de procesos/subprocesos activos (del snapshot recién tomado).
        ProcCountText.Text = I18n.T("{0} procesos · {1} subprocesos", _procTotal, _threadTotal);

        // Construir el mapa de hijos ANTES de actualizar las filas: así el chevron
        // de cada fila refleja el snapshot actual sin el retraso de un tick.
        BuildChildMap(items);

        var seen = new HashSet<int>();
        bool setChanged = items.Count != _rows.Count;
        foreach (var info in items)
        {
            seen.Add(info.Pid);
            if (_rows.TryGetValue(info.Pid, out var row))
                UpdateRow(row, info);
            else
            {
                _rows[info.Pid] = CreateRow(info);
                setChanged = true;
            }
        }

        foreach (var stale in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            if (_rows.Remove(stale, out var row))
                RowsPanel.Children.Remove(row.Row);
            _cpuSamples.Remove(stale);
            _userCache.Remove(stale);
            _pathCache.Remove(stale);
            _staticCache.Remove(stale);
            _selectedPids.Remove(stale);
            _expandedPids.Remove(stale);
            setChanged = true;
        }

        // Reordenar según el orden del snapshot (la columna ordenada). Solo se toca
        // el panel si el orden cambió de verdad (evita re-layout innecesario).
        ReorderRows(items);

        // Completar los anchos pendientes (filas que estuvieron fuera de pantalla
        // durante un redimensionado manual): este refresco converge el estado, así
        // ninguna fila queda desalineada cuando se reordena o se vuelve visible.
        if (_fullApplyPending)
        {
            _fullApplyPending = false;
            _forceRows = true;
            ApplyColumnWidths();
        }

        // La visibilidad de los scrollbars depende del layout recién terminado:
        // re-evaluar el espacio reservado en el próximo turno del dispatcher.
        _ = DispatcherQueue.TryEnqueue(UpdateScrollbarInsets);
    }

    private void ReorderRows(List<ProcInfo> items)
    {
        // Construir siempre el mapa de hijos (lo usan los chevrons en modo árbol).
        // En modo PLANO (Administrador de tareas) se listan todos sin árbol; en
        // modo ÁRBOL se muestra colapsado: solo raíces y los hijos al expandir.
        BuildChildMap(items);
        List<Grid> expected;
        if (_treeMode)
        {
            expected = BuildTreeOrder(items);
        }
        else
        {
            expected = new List<Grid>(items.Count);
            foreach (var info in items)
                if (_rows.TryGetValue(info.Pid, out var row))
                {
                    row.NamePanel.Margin = new Thickness(8, 0, 8, 0);
                    expected.Add(row.Row);
                }
        }

        bool same = expected.Count == RowsPanel.Children.Count;
        if (same)
        {
            for (int i = 0; i < expected.Count; i++)
                if (!ReferenceEquals(RowsPanel.Children[i], expected[i])) { same = false; break; }
        }
        if (same) return;

        RowsPanel.Children.Clear();
        foreach (var g in expected)
            RowsPanel.Children.Add(g);
    }

    /// <summary>
    /// Construye el mapa PID → hijos directos a partir del snapshot actual.
    /// Se guarda en _lastChildMap para que UpdateRow sepa qué procesos tienen
    /// hijos (y muestren el chevron).
    /// </summary>
    private void BuildChildMap(List<ProcInfo> items)
    {
        var childrenMap = new Dictionary<int, List<ProcInfo>>();
        foreach (var info in items)
        {
            if (info.ParentPid == 0 || info.ParentPid == info.Pid) continue;
            if (!childrenMap.TryGetValue(info.ParentPid, out var list))
            {
                list = new List<ProcInfo>();
                childrenMap[info.ParentPid] = list;
            }
            list.Add(info);
        }
        _lastChildMap = childrenMap;
    }

    /// <summary>
    /// Construye el orden de filas en árbol colapsado: cada proceso raíz seguido
    /// de sus hijos directos (recursivo) solo si está expandido. Los hijos se
    /// indentan según profundidad; los hijos de padres no expandidos quedan
    /// ocultos hasta abrir su rama con el chevron.
    /// </summary>
    private List<Grid> BuildTreeOrder(List<ProcInfo> items)
    {
        // El childMap ya fue construido por BuildChildMap en ReorderRows.
        var childrenMap = _lastChildMap!;

        // Raíces: procesos cuyo padre no está en el snapshot (o no tiene padre).
        var pidsInSnapshot = new HashSet<int>(items.Select(i => i.Pid));
        var result = new List<Grid>(items.Count);
        var added = new HashSet<int>();

        foreach (var info in items)
        {
            if (added.Contains(info.Pid)) continue;
            // Es raíz si su padre no está en el snapshot (o no tiene padre).
            if (info.ParentPid == 0 || !pidsInSnapshot.Contains(info.ParentPid))
            {
                AppendWithChildren(info, 0, childrenMap, pidsInSnapshot, result, added);
            }
        }
        // Los hijos de padres no expandidos NO se agregan: quedan ocultos hasta
        // que el usuario abra su rama con el chevron (árbol colapsado).
        return result;
    }

    private void AppendWithChildren(ProcInfo info, int depth,
        Dictionary<int, List<ProcInfo>> childrenMap,
        HashSet<int> pidsInSnapshot,
        List<Grid> result, HashSet<int> added)
    {
        if (added.Contains(info.Pid)) return;
        added.Add(info.Pid);
        if (_rows.TryGetValue(info.Pid, out var row))
        {
            // Indentación: margen izquierdo proporcional a la profundidad del árbol
            // (cada nivel de hijo se empuja 16px a la derecha).
            row.NamePanel.Margin = new Thickness(8 + depth * 16, 0, 8, 0);
            result.Add(row.Row);
        }
        // Hijos: solo si el padre está expandido.
        if (_expandedPids.Contains(info.Pid) && childrenMap.TryGetValue(info.Pid, out var children))
        {
            foreach (var child in children)
                AppendWithChildren(child, depth + 1, childrenMap, pidsInSnapshot, result, added);
        }
    }

    private RowUi CreateRow(ProcInfo info)
    {
        var row = new Grid { Width = TotalWidth, HorizontalAlignment = HorizontalAlignment.Left, Height = RowHeight };
        AddColumns(row);

        // Columna 1: chevron de árbol + ícono del exe + nombre del proceso (con .exe).
        var chevron = new FontIcon
        {
            Glyph = "\uE76C",   // ChevronRight (▸)
            FontSize = 12,
            Foreground = (Brush)ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 16,
            Visibility = Visibility.Collapsed   // visible solo si tiene hijos
        };
        var icon = new Image
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var name = NewCell(info.Name);
        name.Margin = new Thickness(0);
        var namePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0)
        };
        namePanel.Children.Add(chevron);
        namePanel.Children.Add(icon);
        namePanel.Children.Add(name);

        // Estilo monitor de sensores: todos los datos alineados a la derecha de su
        // columna (solo el nombre queda a la izquierda, con su ícono).
        var user = NewCell(info.User, right: true);
        var state = NewCell(I18n.T("Activo"), right: true);
        var rules = NewCell("", right: true);
        var priority = NewCell("", right: true);
        var gpuPrio = NewCell("", right: true);
        var ioPrio = NewCell("", right: true);
        var affinity = NewCell("", right: true);
        // Celdas numéricas: margen horizontal reducido para que valores como
        // "999.0" o "1,2 GB" entren completos aunque la tabla se escale.
        var numMargin = new Thickness(4, 0, 4, 0);
        var cpu = NewCell("", numMargin, right: true);
        var gpu = NewCell("", numMargin, right: true);
        var memRam = NewCell("", numMargin, right: true);
        var appName = NewCell("", right: true);
        var path = NewCell("", right: true);

        var cells = new FrameworkElement[Columns.Length];
        cells[0] = namePanel;
        cells[1] = user;
        cells[2] = state;
        cells[3] = rules;
        cells[4] = priority;
        cells[5] = gpuPrio;
        cells[6] = ioPrio;
        cells[7] = affinity;
        cells[8] = cpu;
        cells[9] = gpu;
        cells[10] = memRam;
        cells[11] = appName;
        cells[12] = path;

        for (int pos = 0; pos < Columns.Length; pos++)
        {
            // Separador visual entre columnas (borde izquierdo, salvo la primera).
            if (pos > 0)
            {
                var sep = new Rectangle
                {
                    Width = 1,
                    Fill = (Brush)ThemeBrushes.Get("SensorGridLineBrush"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsHitTestVisible = false
                };
                Grid.SetColumn(sep, pos);
                row.Children.Add(sep);
            }

            // La celda se ubica según el orden actual de columnas (arrastrable).
            var el = cells[_colOrder[pos]];
            Grid.SetColumn(el, pos);
            row.Children.Add(el);
        }

        // Fila plana estilo monitor de sensores: sin card, solo una línea inferior
        // que separa las filas (la selección la pinta ApplySelectionVisual).
        var container = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = (Brush)ThemeBrushes.Get("SensorGridLineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
            Padding = new Thickness(0)
        };
        var outer = new Grid { Tag = info.Pid };
        // Clic izquierdo: seleccionar (Ctrl = agregar/quitar de la selección).
        outer.Tapped += OnRowTapped;
        // Clic derecho: selecciona la fila (si no estaba) y abre el menú de reglas,
        // anclado a la posición del cursor, no al centro de la fila.
        outer.RightTapped += (s, e) =>
        {
            if (!_selectedPids.Contains(info.Pid))
                SelectSingle(info.Pid);
            // Anclar al ScrollViewer, no a la fila: si el refresco del intervalo
            // reordena/reconstruye las filas con el menú abierto, la fila se
            // re-parenta y el MenuFlyout se cierra solo. RowsScroll nunca se quita.
            ShowProcessRuleMenu(RowsScroll, info.Pid, info.Exe, info.Name, e.GetPosition(RowsScroll), info.Path);
        };
        outer.Children.Add(container);

        // Click del chevron: expandir/colapsar hijos del proceso.
        chevron.Tapped += (s, e) =>
        {
            e.Handled = true;
            if (_expandedPids.Add(info.Pid))
            {
                // Expandir: chevron hacia abajo, reconstruir orden.
                chevron.Glyph = "\uE70E"; // ChevronDown (▾)
            }
            else
            {
                // Colapsar: chevron hacia la derecha, reconstruir orden.
                _expandedPids.Remove(info.Pid);
                chevron.Glyph = "\uE76C"; // ChevronRight (▸)
            }
            // Forzar reorden para mostrar/ocultar hijos.
            _forceRows = true;
            ReorderRows(_lastSnapshot);
        };

        var ui = new RowUi
        {
            Pid = info.Pid,
            Exe = info.Exe,
            Row = outer,
            Cols = row,
            Icon = icon,
            Chevron = chevron,
            NamePanel = namePanel,
            Name = name,
            User = user,
            State = state,
            Rules = rules,
            Priority = priority,
            GpuPriority = gpuPrio,
            IoPrio = ioPrio,
            Affinity = affinity,
            Cpu = cpu,
            Gpu = gpu,
            MemRam = memRam,
            AppName = appName,
            Path = path,
            Container = container
        };
        UpdateRow(ui, info);
        ApplySelectionVisual(ui, _selectedPids.Contains(info.Pid));

        // Ícono del proceso: se extrae una vez (en caché) y se carga desde archivo.
        if (!string.IsNullOrEmpty(info.Path))
            _ = LoadProcessIconAsync(icon, info.Path);

        return ui;
    }

    // ===== Selección de filas (clic izquierdo, estilo Process Lasso) =====

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        var outer = (Grid)sender;
        int pid = outer.Tag is int p ? p : -1;
        if (pid < 0) return;
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl)
        {
            if (!_selectedPids.Add(pid))
            {
                _selectedPids.Remove(pid);
                if (_rows.TryGetValue(pid, out var unsel)) ApplySelectionVisual(unsel, false);
            }
            else if (_rows.TryGetValue(pid, out var sel))
            {
                ApplySelectionVisual(sel, true);
            }
        }
        else
        {
            SelectSingle(pid);
        }
        e.Handled = true;
    }

    private void SelectSingle(int pid)
    {
        foreach (var old in _selectedPids.ToList())
        {
            if (old == pid) continue;
            _selectedPids.Remove(old);
            if (_rows.TryGetValue(old, out var r)) ApplySelectionVisual(r, false);
        }
        _selectedPids.Add(pid);
        if (_rows.TryGetValue(pid, out var row)) ApplySelectionVisual(row, true);
    }

    private void ApplySelectionVisual(RowUi ui, bool selected)
    {
        if (selected)
        {
            var accent = ThemeBrushes.Get("AccentBrush").Color;
            ui.Container.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, accent.R, accent.G, accent.B));
            ui.Container.BorderBrush = ThemeBrushes.Get("AccentBrush");
            ui.Container.BorderThickness = new Thickness(1);
        }
        else
        {
            // Fila plana: transparente, con la línea inferior de separación de filas.
            ui.Container.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ui.Container.BorderBrush = (Brush)ThemeBrushes.Get("SensorGridLineBrush");
            ui.Container.BorderThickness = new Thickness(0, 0, 0, 1);
        }
    }

    private static TextBlock NewCell(string text, Thickness? margin = null, bool right = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
            Margin = margin ?? new Thickness(8, 0, 8, 0)
        };
        // Texto completo al hover: las celdas truncan con ellipsis, el tooltip
        // devuelve el valor completo al pasar el mouse.
        ToolTipService.SetToolTip(tb, text);
        return tb;
    }

    // ===== Ícono del proceso (cacheado en disco, carga desde archivo) =====

    private static readonly Dictionary<string, string> _iconCacheFile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Invalida la caché en memoria de íconos de procesos (el botón
    /// "Limpiar caché" borra la carpeta exeicons; sin esto, los íconos quedaban
    /// ocultos hasta reiniciar porque el mapa seguía apuntando a archivos borrados).</summary>
    public static void ClearIconCache() => _iconCacheFile.Clear();

    private static async Task LoadProcessIconAsync(Image img, string exePath)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;
            if (!_iconCacheFile.TryGetValue(exePath, out var png))
            {
                png = await Task.Run(() =>
                {
                    string dir = Path.Combine(GestionarProcesosPage.BannerCacheDir, "exeicons-v2");
                    string file = Path.Combine(dir, HashString(exePath) + "-proc.png");
                    if (!File.Exists(file))
                    {
                        try
                        {
                            Directory.CreateDirectory(dir);
                            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                            if (ico != null)
                            {
                                using var bmp = new System.Drawing.Bitmap(32, 32);
                                using (var g = System.Drawing.Graphics.FromImage(bmp))
                                {
                                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                    g.Clear(System.Drawing.Color.Transparent);
                                    g.DrawImage(ico.ToBitmap(), 0, 0, 32, 32);
                                }
                                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                            }
                        }
                        catch { }
                    }
                    return file;
                });
                _iconCacheFile[exePath] = png;
            }
            if (File.Exists(png))
            {
                img.Source = new BitmapImage(new Uri(png));
                img.Visibility = Visibility.Visible;
            }
        }
        catch { }
    }

    private static string HashString(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private void UpdateRow(RowUi ui, ProcInfo info)
    {
        // Chevron: visible si el proceso tiene hijos en el snapshot actual.
        // El estado de "tiene hijos" se calcula en UpdateRows (BuildTreeOrder)
        // y se guarda acá para que el chevron lo refleje en cada tick.
        bool hasChildren = _treeMode && _lastChildMap?.ContainsKey(info.Pid) == true;
        if (hasChildren != ui.HasChildren)
        {
            ui.HasChildren = hasChildren;
            ui.Chevron.Visibility = hasChildren ? Visibility.Visible : Visibility.Collapsed;
        }
        // Dirección del chevron: ▾ si está expandido, ▸ si colapsado.
        var expectedGlyph = _expandedPids.Contains(info.Pid) ? "\uE70E" : "\uE76C";
        if (hasChildren && ui.Chevron.Glyph != expectedGlyph)
            ui.Chevron.Glyph = expectedGlyph;

        // Solo se toca el TextBlock si el texto cambió: evita ~2000 asignaciones y
        // re-layouts por tick cuando nada cambió (la mayor parte del costo de UI).
        SetText(ui.Name, info.Name);
        SetText(ui.User, info.User);
        SetText(ui.Priority, info.Priority);
        SetText(ui.GpuPriority, info.GpuPrio is int g ? GpuLabel(g) : "—");
        SetText(ui.IoPrio, info.IoPrio is int io ? IoLabel(io) : "—");
        SetText(ui.Affinity, info.Affinity > 0 ? $"0x{info.Affinity:X}" : "—");
        SetText(ui.Cpu, info.Cpu >= 0 ? info.Cpu.ToString("F1") : "—");
        SetText(ui.Gpu, info.Gpu >= 0 ? info.Gpu.ToString("F1") : "—");
        SetText(ui.MemRam, FormatMB(info.WorkingSet));
        SetText(ui.AppName, info.AppName);
        SetText(ui.Path, info.Path ?? "—");
        SetText(ui.Rules, RuleSummary(info.EffectiveRule));
    }

    private static void SetText(TextBlock tb, string value)
    {
        if (!string.Equals(tb.Text, value, StringComparison.Ordinal))
        {
            tb.Text = value;
            // El tooltip sigue al texto actualizado (las celdas se refrescan en vivo).
            ToolTipService.SetToolTip(tb, value);
        }
    }

    private static string FormatMB(long bytes)
    {
        if (bytes <= 0) return "—";
        return $"{bytes / 1048576.0:F0} MB";
    }

    private static string GpuLabel(int g) => g switch
    {
        2 => I18n.T("Baja"),
        3 => I18n.T("Normal"),
        4 => I18n.T("Alta"),
        _ => g.ToString()
    };

    /// <summary>True si el proceso con ese pid sigue existiendo (para distinguir
    /// "acceso denegado" — proceso vivo pero protegido — de "se cerró").</summary>
    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string RuleSummary(ProcessRule? r)
    {
        if (r == null || (r.CpuPriority == null && r.AffinityMask == null && r.GpuPriority == null
            && string.IsNullOrEmpty(r.PowerPlanGuid) && r.IoPriority == null))
            return "—";

        var parts = new List<string>();
        if (r.CpuPriority is int c && c >= 0 && c < CpuPriorityNames.Length)
            parts.Add(I18n.T(CpuPriorityNames[c]));
        if (r.AffinityMask is long a && a > 0)
            parts.Add($"0x{a:X}");
        if (r.GpuPriority is int g)
            parts.Add("GPU " + GpuLabel(g));
        if (r.IoPriority is int io)
            parts.Add("E/S " + IoLabel(io));
        if (!string.IsNullOrEmpty(r.PowerPlanGuid))
            parts.Add(I18n.T("Plan"));
        return string.Join(" · ", parts);
    }

    private static ProcessRule? Merge(ProcessRule? session, ProcessRule? persistent)
    {
        if (session == null) return persistent;
        if (persistent == null) return session;
        return new ProcessRule(
            session.CpuPriority ?? persistent.CpuPriority,
            session.AffinityMask ?? persistent.AffinityMask,
            session.GpuPriority ?? persistent.GpuPriority,
            session.PowerPlanGuid ?? persistent.PowerPlanGuid,
            session.IoPriority ?? persistent.IoPriority);
    }

    private static bool RuleIsEmpty(ProcessRule? r)
        => r == null
        || (r.CpuPriority == null && r.AffinityMask == null && r.GpuPriority == null
            && string.IsNullOrEmpty(r.PowerPlanGuid)
            && r.IoPriority == null);

    // ===== Desplegable de reglas (el mismo que en juegos: Actual / Siempre) =====

    private void ShowProcessRuleMenu(FrameworkElement target, int pid, string exe, string name, Windows.Foundation.Point position, string? exePath = null)
    {
        try
        {
            // Procesos kernel/sistema (System, lsass, csrss, servicios…): Windows no los
            // deja abrir para modificación ni elevado, así que ninguna regla en vivo puede
            // aplicar. No se ofrecen (además, el "Siempre" por registro sobre svchost/lsass
            // afectaría a TODOS los procesos con ese nombre). Se muestra la info y el motivo.
            if (!_processService.CanOpenForModify(pid))
            {
                var infoMenu = new MenuFlyout();
                infoMenu.Items.Add(new MenuFlyoutItem { Text = name, IsEnabled = false });
                infoMenu.Items.Add(new MenuFlyoutItem { Text = I18n.T("Proceso del sistema: Windows no permite modificar sus reglas."), IsEnabled = false });
                infoMenu.ShowAt(target, position);
                return;
            }

            var rules = _processService.GetRules();
            var rule = rules.TryGetValue(exe, out var r) ? r : new ProcessRule(null, null, null);
            var sessionRule = _processService.GetSessionRule(exe) ?? new ProcessRule(null, null, null);
            int procCount = _processService.ProcessorCount;
            long fullMask = procCount >= 64 ? -1L : ((1L << procCount) - 1);

            // Placement por defecto: con ShowAt(target, posición) el menú se ancla al cursor.
            var menu = new MenuFlyout();

            bool ApplyEffectiveToRunning(bool sessionScope)
            {
                // Aplicar a TODOS los procesos que matchean la regla (launcher +
                // proceso real + stub de anti-cheat): si la regla está sobre el
                // launcher, la afinidad/prioridad también llega al juego real.
                var apps = _processService.FindRunningProcessesForRule(exe);
                if (apps.Count == 0)
                {
                    StatusText.Text = sessionScope
                        ? I18n.T("Reglas de sesión listas. Se aplicarán cuando el proceso se abra.")
                        : I18n.T("Reglas guardadas. Se aplicarán cuando el proceso esté en ejecución.");
                    StatusText.Foreground = Feedback.MutedBrush;
                    StatusText.Visibility = Visibility.Visible;
                    return true;
                }

                var effective = _processService.GetEffectiveRule(exe);
                var anyFailed = new RuleApplyFeedback(false, false, false);
                foreach (var app in apps)
                {
                var fb = RuleIsEmpty(effective)
                    ? _processService.ApplyRuleWithFeedback(app, new ProcessRule(2, fullMask, 3, null, 2))
                    : _processService.ApplyRuleWithFeedback(app, effective);
                anyFailed = anyFailed with
                {
                    CpuFailed = anyFailed.CpuFailed || fb.CpuFailed,
                    AffinityFailed = anyFailed.AffinityFailed || fb.AffinityFailed,
                    GpuFailed = anyFailed.GpuFailed || fb.GpuFailed,
                    IoFailed = anyFailed.IoFailed || fb.IoFailed
                };
                }

                // Plan de energía: una sola vez (idempotente), no por cada proceso.
                if (RuleIsEmpty(effective))
                    _processService.RevertPowerPlanIfApplied(exe);
                else if (!string.IsNullOrEmpty(effective.PowerPlanGuid))
                    _processService.ApplyPowerPlanIfRunning(exe, effective.PowerPlanGuid);
                else
                    _processService.RevertPowerPlanIfApplied(exe);

                if (anyFailed.AnyFailed)
                {
                    var parts = new List<string>();
                    if (anyFailed.CpuFailed) parts.Add(I18n.T("prioridad de CPU"));
                    if (anyFailed.AffinityFailed) parts.Add(I18n.T("afinidad"));
                    if (anyFailed.GpuFailed) parts.Add(I18n.T("prioridad de GPU"));
                    if (anyFailed.IoFailed) parts.Add(I18n.T("prioridad de E/S"));
                    // El destino del "Siempre" depende del tipo de proceso: para un
                    // juego normal la prioridad se fija al nacer por registro (PerfOptions)
                    // y aplica en la próxima apertura; para un componente del sistema eso
                    // NO se escribe (afectaría a todos los procesos con ese nombre), así
                    // que solo puede cambiar EN VIVO — y si Windows lo bloquea, no aplica.
                    string note = _processService.IsSystemProcessName(exe)
                        ? I18n.T("Los procesos del sistema solo pueden cambiarse en vivo; si Windows lo bloquea, no hay alternativa.")
                        : I18n.T("La prioridad de CPU queda fijada al nacer por registro y aplicará en la próxima apertura.");
                    // Acceso denegado (proceso aún vivo pero protegido: anti-cheat,
                    // permisos del sistema) vs. proceso que se cerró en el medio.
                    bool stillAlive = apps.Any(a => IsPidAlive(a.Id));
                    string reason = stillAlive
                        ? I18n.T("acceso denegado (proceso protegido)")
                        : I18n.T("el proceso se cerró");
                    StatusText.Text = I18n.T("No se pudo aplicar {0} en vivo a {1}: {2}. {3}",
                        string.Join(", ", parts), name, reason, note);
                    StatusText.Foreground = Feedback.WarningBrush;
                    return false;
                }
                else
                {
                    StatusText.Text = RuleIsEmpty(_processService.GetEffectiveRule(exe))
                        ? I18n.T("Valores por defecto restaurados en {0}", exe)
                        : sessionScope
                            ? I18n.T("Reglas de sesión aplicadas a {0}", exe)
                            : I18n.T("Reglas aplicadas a {0}", exe);
                    StatusText.Foreground = Feedback.SuccessBrush;
                }
                StatusText.Visibility = Visibility.Visible;
                // La regla cambió: invalidar la caché estática para que la tabla
                // muestre la prioridad/afinidad reales en el próximo refresco.
                _staticCache.Clear();
                _ = RefreshAsync();
                return true;
            }

            bool ApplyAndSave(ProcessRule newRule)
            {
                // Guardar la regla; si la aplicación en vivo falla por acceso denegado
                // y es un proceso del sistema (que NO se fija por registro), revertir
                // la regla guardada al estado anterior para que la casilla del menú
                // vuelva a marcarse con el valor real.
                var prev = rule;
                _processService.SaveRule(exe, newRule);
                rule = newRule;
                if (!ApplyEffectiveToRunning(sessionScope: false)
                    && _processService.IsSystemProcessName(exe))
                {
                    rule = prev;
                    _processService.SaveRule(exe, prev);
                    return false;
                }
                return true;
            }

            bool ApplySessionAndNotify()
            {
                // Sesión "Actual": si la aplicación en vivo falla, revertir la regla
                // de sesión al estado anterior (la casilla vuelve a marcarse con el
                // valor real la próxima vez que se abra el menú).
                var prev = sessionRule;
                _processService.SetSessionRule(exe, sessionRule);
                if (!ApplyEffectiveToRunning(sessionScope: true))
                {
                    sessionRule = prev;
                    _processService.SetSessionRule(exe, prev);
                    return false;
                }
                return true;
            }

            MenuFlyoutSubItem BuildScope(string scopeLabel, string[] labels, int selected, Func<int, bool> onPick)
            {
                var sub = new MenuFlyoutSubItem { Text = scopeLabel };
                var items = new List<ToggleMenuFlyoutItem>();
                for (int i = 0; i < labels.Length; i++)
                {
                    int idx = i;
                    var item = new ToggleMenuFlyoutItem { Text = I18n.T(labels[i]), IsChecked = idx == selected };
                    item.Click += (s, e) =>
                    {
                        foreach (var it in items) it.IsChecked = it == item;
                        // Si la aplicación falló (acceso denegado), la casilla vuelve
                        // a marcarse con el valor real (el que estaba seleccionado).
                        if (!onPick(idx))
                        {
                            foreach (var it in items) it.IsChecked = it == items[selected];
                        }
                    };
                    items.Add(item);
                    sub.Items.Add(item);
                }
                return sub;
            }

            MenuFlyoutSubItem BuildAffinityScope(string scopeLabel, long? mask, Func<long?, bool> onPick)
            {
                var sub = new MenuFlyoutSubItem { Text = scopeLabel };
                bool allCores = mask == null || mask == fullMask;
                var coreItems = new List<ToggleMenuFlyoutItem>();
                // Estado real de referencia para revertir si la aplicación falla.
                long? original = mask;
                for (int i = 0; i < procCount; i++)
                {
                    int ci = i;
                    var item = new ToggleMenuFlyoutItem
                    {
                        Text = I18n.T("Núcleo {0}", ci + 1),
                        IsChecked = allCores || (mask!.Value & (1L << ci)) != 0
                    };
                    item.Click += (s, e) =>
                    {
                        long m = 0;
                        for (int k = 0; k < coreItems.Count; k++)
                            if (coreItems[k].IsChecked == true)
                                m |= 1L << k;
                        // Todos marcados (o ninguno) = máscara completa EXPLÍCITA: restaura
                        // todos los núcleos (antes se guardaba null = "no tocar" y no
                        // deshacía una afinidad restringida de una regla previa).
                        long? affinity = (m == 0 || m == fullMask) ? fullMask : m;
                        // Si la aplicación falló (acceso denegado), los núcleos vuelven
                        // a marcarse según la máscara real original.
                        if (!onPick(affinity))
                        {
                            bool all = original == null || original == fullMask;
                            for (int k = 0; k < coreItems.Count; k++)
                                coreItems[k].IsChecked = all || (original!.Value & (1L << k)) != 0;
                        }
                    };
                    coreItems.Add(item);
                    sub.Items.Add(item);
                }
                return sub;
            }

            // ===== Prioridad de CPU =====
            var cpuSub = new MenuFlyoutSubItem { Text = I18n.T("Prioridad de CPU") };
            // Índice 0 = "Por defecto" (sin regla para este campo); el resto
            // mapea 1:1 con CpuPriorityValues. Igual que la biblioteca de juegos.
            string[] cpuNames = { "Por defecto", "Mínima", "Baja", "Normal", "Por encima de lo normal", "Alta", "Tiempo real" };
            int cpuPerSel = rule.CpuPriority is int cp ? Array.IndexOf(CpuPriorityValues, cp) + 1 : 0;
            int cpuSesSel = sessionRule.CpuPriority is int scp ? Array.IndexOf(CpuPriorityValues, scp) + 1 : 0;
            cpuSub.Items.Add(BuildScope(I18n.T("Actual"), cpuNames, cpuSesSel, idx =>
            {
                sessionRule = new ProcessRule(idx <= 0 ? null : CpuPriorityValues[idx - 1], sessionRule.AffinityMask, sessionRule.GpuPriority, sessionRule.PowerPlanGuid, sessionRule.IoPriority);
                return ApplySessionAndNotify();
            }));
            cpuSub.Items.Add(BuildScope(I18n.T("Siempre"), cpuNames, cpuPerSel, idx =>
            {
                rule = new ProcessRule(idx <= 0 ? null : CpuPriorityValues[idx - 1], rule.AffinityMask, rule.GpuPriority, rule.PowerPlanGuid, rule.IoPriority);
                return ApplyAndSave(rule);
            }));
            menu.Items.Add(cpuSub);

            // ===== Afinidad de CPU =====
            var affSub = new MenuFlyoutSubItem { Text = I18n.T("Afinidad de CPU") };
            affSub.Items.Add(BuildAffinityScope(I18n.T("Actual"), sessionRule.AffinityMask, aff =>
            {
                sessionRule = new ProcessRule(sessionRule.CpuPriority, aff, sessionRule.GpuPriority, sessionRule.PowerPlanGuid, sessionRule.IoPriority);
                return ApplySessionAndNotify();
            }));
            affSub.Items.Add(BuildAffinityScope(I18n.T("Siempre"), rule.AffinityMask, aff =>
            {
                rule = new ProcessRule(rule.CpuPriority, aff, rule.GpuPriority, rule.PowerPlanGuid, rule.IoPriority);
                return ApplyAndSave(rule);
            }));
            menu.Items.Add(affSub);

            // ===== Prioridad de GPU =====
            var gpuSub = new MenuFlyoutSubItem { Text = I18n.T("Prioridad de GPU") };
            // Índice 0 = "Por defecto" (sin regla), igual que la biblioteca.
            string[] gpuNames = { "Por defecto", "Baja", "Normal", "Alta" };
            int gpuPerSel = rule.GpuPriority is int gp ? Array.IndexOf(GpuPriorityValues, gp) + 1 : 0;
            int gpuSesSel = sessionRule.GpuPriority is int sgp ? Array.IndexOf(GpuPriorityValues, sgp) + 1 : 0;
            gpuSub.Items.Add(BuildScope(I18n.T("Actual"), gpuNames, gpuSesSel, idx =>
            {
                sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, idx <= 0 ? null : GpuPriorityValues[idx - 1], sessionRule.PowerPlanGuid, sessionRule.IoPriority);
                return ApplySessionAndNotify();
            }));
            gpuSub.Items.Add(BuildScope(I18n.T("Siempre"), gpuNames, gpuPerSel, idx =>
            {
                rule = new ProcessRule(rule.CpuPriority, rule.AffinityMask, idx <= 0 ? null : GpuPriorityValues[idx - 1], rule.PowerPlanGuid, rule.IoPriority);
                return ApplyAndSave(rule);
            }));
            menu.Items.Add(gpuSub);

            // ===== Prioridad de E/S =====
            var ioSub = new MenuFlyoutSubItem { Text = I18n.T("Prioridad de E/S") };
            // Índice 0 = "Por defecto" (sin regla), igual que los demás ajustes.
            string[] ioNames = { "Por defecto", "Muy baja", "Baja", "Normal", "Alta", "Crítica" };
            int ioPerSel = rule.IoPriority is int io ? Array.IndexOf(IoPriorityValues, io) + 1 : 0;
            int ioSesSel = sessionRule.IoPriority is int sio ? Array.IndexOf(IoPriorityValues, sio) + 1 : 0;
            ioSub.Items.Add(BuildScope(I18n.T("Actual"), ioNames, ioSesSel, idx =>
            {
                sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, sessionRule.GpuPriority, sessionRule.PowerPlanGuid, idx <= 0 ? null : IoPriorityValues[idx - 1]);
                return ApplySessionAndNotify();
            }));
            ioSub.Items.Add(BuildScope(I18n.T("Siempre"), ioNames, ioPerSel, idx =>
            {
                rule = new ProcessRule(rule.CpuPriority, rule.AffinityMask, rule.GpuPriority, rule.PowerPlanGuid, idx <= 0 ? null : IoPriorityValues[idx - 1]);
                return ApplyAndSave(rule);
            }));
            menu.Items.Add(ioSub);

            // ===== Verificación en vivo de la prioridad de GPU =====
            var runningApp = _processService.FindRunningProcess(exe);
            if (runningApp != null)
            {
                var realGpu = _processService.GetGpuPriority(runningApp.Id);
                string infoText;
                if (realGpu == null)
                {
                    int st = _processService.LastGpuPriorityStatus;
                    infoText = st == unchecked((int)0xC0000022)
                        ? I18n.T("Prioridad GPU actual: no se pudo leer (proceso protegido: anti-cheat)")
                        : I18n.T("Prioridad GPU actual: no se pudo leer (error 0x{0:X8})", st);
                }
                else
                {
                    infoText = I18n.T("Prioridad GPU actual: {0} ({1})", GpuLabel(realGpu.Value), realGpu.Value);
                }
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(new MenuFlyoutItem { Text = infoText, IsEnabled = false });

                var realIo = _processService.GetIoPriority(runningApp.Id);
                string ioText = realIo == null
                    ? I18n.T("Prioridad E/S actual: no se pudo leer (proceso protegido: anti-cheat)")
                    : I18n.T("Prioridad E/S actual: {0} ({1})", IoLabel(realIo.Value), realIo.Value);
                menu.Items.Add(new MenuFlyoutItem { Text = ioText, IsEnabled = false });
            }

            // ===== Plan de energía: desplegable directo de los planes instalados.
            // Se activa al correr el juego y se revierte al cerrar (por proceso
            // solo aplica en la sesión actual; el plan permanente se maneja en el
            // apartado "Núcleos y Plan de energía"). =====
            var planSub = new MenuFlyoutSubItem { Text = I18n.T("Plan de energía") };
            var plans = _cpuPowerService.GetPowerPlans();
            var planItems = new List<ToggleMenuFlyoutItem>();

            // "Por defecto" = sin regla de plan (el proceso no cambia el plan del sistema).
            var noneItem = new ToggleMenuFlyoutItem
            {
                Text = I18n.T("Por defecto"),
                IsChecked = string.IsNullOrEmpty(sessionRule.PowerPlanGuid)
            };
            noneItem.Click += (s, e) =>
            {
                foreach (var it in planItems) it.IsChecked = it == noneItem;
                sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, sessionRule.GpuPriority, null, sessionRule.IoPriority);
                ApplySessionAndNotify();
            };
            planItems.Add(noneItem);
            planSub.Items.Add(noneItem);
            planSub.Items.Add(new MenuFlyoutSeparator());

            for (int pi = 0; pi < plans.Count; pi++)
            {
                var plan = plans[pi];
                var item = new ToggleMenuFlyoutItem
                {
                    Text = plan.Name,
                    IsChecked = string.Equals(sessionRule.PowerPlanGuid, plan.Guid, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (s, e) =>
                {
                    foreach (var it in planItems) it.IsChecked = it == item;
                    // NO se cambia el plan en el momento: se activa cuando el
                    // proceso corre y se revierte al plan por defecto al cerrar.
                    sessionRule = new ProcessRule(sessionRule.CpuPriority, sessionRule.AffinityMask, sessionRule.GpuPriority, plan.Guid, sessionRule.IoPriority);
                    ApplySessionAndNotify();
                };
                planItems.Add(item);
                planSub.Items.Add(item);
            }
            if (plans.Count > 0)
                menu.Items.Add(planSub);

            // ===== Windows Defender =====
            // Excepción de Windows Defender: alterna la exclusión. Si se conoce la
            // ruta del exe se excluye la CARPETA (cubre proceso + subprocesos); si
            // no, se excluye por nombre de proceso. La app corre elevada, así que
            // los cmdlets de Defender no piden UAC.
            // Empieza en "Consultando Windows Defender..." mientras se consulta el
            // estado real ("Excluir" ↔ "Quitar exclusión"). MinWidth fijo: al
            // alternar el texto el item no cambia de tamaño ni envuelve, así el
            // menú no se mueve.
            var defenderItem = new MenuFlyoutItem
            {
                Text = I18n.T("Consultando Windows Defender..."),
                MinWidth = 300
            };
            // El texto refleja el estado real mientras el menú está abierto
            // ("Excluir" si no está excluido, "Quitar exclusión" si ya lo está):
            // se consulta en background, sin retrasar la apertura del menú.
            _ = RefreshDefenderItemStateAsync(defenderItem, exe, exePath);
            defenderItem.Click += async (s, e) =>
            {
                try
                {
                    string? target = !string.IsNullOrEmpty(exePath)
                        ? Path.GetDirectoryName(exePath)?.TrimEnd('\\')
                        : null;
                    bool excluded;
                    bool ok;
                    if (!string.IsNullOrEmpty(target))
                    {
                        excluded = await DefenderService.IsPathExcludedAsync(target);
                        (ok, _) = excluded
                            ? await DefenderService.RemovePathExclusionAsync(target)
                            : await DefenderService.AddPathExclusionAsync(target);
                    }
                    else
                    {
                        excluded = await DefenderService.IsProcessExcludedAsync(exe);
                        (ok, _) = excluded
                            ? await DefenderService.RemoveProcessExclusionAsync(exe)
                            : await DefenderService.AddProcessExclusionAsync(exe);
                    }
                    if (!ok)
                    {
                        StatusText.Text = I18n.T("No se pudo cambiar la excepción de Windows Defender: {0}", target ?? exe);
                        StatusText.Foreground = Feedback.ErrorBrush;
                    }
                    else
                    {
                        StatusText.Text = I18n.T(excluded
                            ? "Exclusión de Windows Defender quitada"
                            : "Proceso excluido de Windows Defender");
                        StatusText.Foreground = Feedback.SuccessBrush;
                    }
                    StatusText.Visibility = Visibility.Visible;
                }
                catch (Exception ex2)
                {
                    _loggingService.LogWarning($"ProcesosPage: excepción de Defender {exe}: {ex2.Message}");
                }
            };
            menu.Items.Add(defenderItem);

            // ===== Acciones =====
            menu.Items.Add(new MenuFlyoutSeparator());

            var resetItem = new MenuFlyoutItem { Text = I18n.T("Eliminar reglas") };
            resetItem.Click += async (s, e) =>
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = I18n.T("Eliminar reglas"),
                    Content = I18n.T("¿Eliminar las reglas de {0}? También se quitará la prioridad de nacimiento del registro.", name),
                    PrimaryButtonText = I18n.T("Eliminar"),
                    CloseButtonText = I18n.T("Cancelar"),
                    DefaultButton = ContentDialogButton.Close
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

                _processService.RemoveRule(exe);
                _processService.ClearSessionRule(exe);
                var app = _processService.FindRunningProcess(exe);
                if (app != null)
                {
                    _processService.ApplyCpuPriority(app.Id, 2);
                    _processService.ApplyAffinity(app.Id, fullMask);
                    _processService.ApplyGpuPriority(app.Id, 3);
                    _processService.ApplyIoPriority(app.Id, 2);
                }
                _processService.RevertPowerPlanIfApplied(exe);
                StatusText.Text = I18n.T("Reglas eliminadas para {0}", exe);
                StatusText.Foreground = Feedback.MutedBrush;
                StatusText.Visibility = Visibility.Visible;
                _staticCache.Clear();
                _ = RefreshAsync();
            };
            menu.Items.Add(resetItem);

            // ===== Acciones sobre el proceso =====
            menu.Items.Add(new MenuFlyoutSeparator());

            var closeItem = new MenuFlyoutItem { Text = I18n.T("Cerrar") };
            closeItem.Click += (_, _) => CloseProcess(exe);
            menu.Items.Add(closeItem);

            var killItem = new MenuFlyoutItem { Text = I18n.T("Terminar") };
            killItem.Click += (_, _) => TerminateProcess(exe);
            menu.Items.Add(killItem);

            var locateSub = new MenuFlyoutSubItem { Text = I18n.T("Localizar") };
            var searchItem = new MenuFlyoutItem { Text = I18n.T("Buscar en internet") };
            searchItem.Click += (_, _) => SearchOnInternet(name);
            locateSub.Items.Add(searchItem);
            var openDiskItem = new MenuFlyoutItem { Text = I18n.T("Abrir en disco") };
            openDiskItem.Click += (_, _) => OpenInDisk(exe, exePath);
            locateSub.Items.Add(openDiskItem);
            menu.Items.Add(locateSub);

            menu.ShowAt(target, position);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ProcesosPage: menú de reglas {exe}: {ex.Message}");
        }
    }

    /// <summary>
    /// Actualiza el texto del item de Defender según el estado actual (en background,
    /// sin retrasar la apertura del menú): "Excluir" si no está excluido, "Quitar
    /// exclusión" si ya lo está.
    /// </summary>
    private static async Task RefreshDefenderItemStateAsync(MenuFlyoutItem item, string exe, string? exePath)
    {
        try
        {
            string? target = !string.IsNullOrEmpty(exePath)
                ? Path.GetDirectoryName(exePath)?.TrimEnd('\\')
                : null;
            bool excluded = !string.IsNullOrEmpty(target)
                ? await DefenderService.IsPathExcludedAsync(target)
                : await DefenderService.IsProcessExcludedAsync(exe);
            item.Text = I18n.T(excluded ? "Quitar exclusión de Windows Defender" : "Excluir de Windows Defender");
        }
        catch { }
    }

    // ===== Acciones del menú de clic derecho =====

    private void ShowStatus(string text, Brush brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush;
        StatusText.Visibility = Visibility.Visible;
    }

    private void CloseProcess(string exe)
    {
        try
        {
            var app = _processService.FindRunningProcess(exe);
            if (app == null)
            {
                ShowStatus(I18n.T("Proceso no encontrado: {0}", exe), Feedback.WarningBrush);
                return;
            }
            using var p = Process.GetProcessById(app.Id);
            // WM_CLOSE a la ventana principal: cierre ordenado (guarda datos).
            if (p.CloseMainWindow())
                ShowStatus(I18n.T("Se envió la orden de cerrar a {0}.", exe), Feedback.MutedBrush);
            else
                ShowStatus(I18n.T("No se pudo cerrar {0}: el proceso no tiene ventana principal.", exe), Feedback.WarningBrush);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ProcesosPage: cerrar {exe}: {ex.Message}");
            ShowStatus(I18n.T("No se pudo cerrar {0}: {1}", exe, ex.Message), Feedback.WarningBrush);
        }
    }

    private void TerminateProcess(string exe)
    {
        try
        {
            var app = _processService.FindRunningProcess(exe);
            if (app == null)
            {
                ShowStatus(I18n.T("Proceso no encontrado: {0}", exe), Feedback.WarningBrush);
                return;
            }
            using var p = Process.GetProcessById(app.Id);
            p.Kill();
            ShowStatus(I18n.T("Proceso terminado: {0}", exe), Feedback.SuccessBrush);
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ProcesosPage: terminar {exe}: {ex.Message}");
            ShowStatus(I18n.T("No se pudo terminar {0}: {1}", exe, ex.Message), Feedback.WarningBrush);
        }
        _ = RefreshAsync();
    }

    private void SearchOnInternet(string name)
    {
        try
        {
            string query = Uri.EscapeDataString(Path.GetFileNameWithoutExtension(name));
            Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + query) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ProcesosPage: buscar {name}: {ex.Message}");
        }
    }

    private void OpenInDisk(string exe, string? exePath)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                ShowStatus(I18n.T("No se encontró el archivo: {0}", exePath ?? exe), Feedback.WarningBrush);
                return;
            }
            // Abre el Explorador con el archivo seleccionado.
            Process.Start("explorer.exe", $"/select,\"{exePath}\"");
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"ProcesosPage: abrir en disco {exe}: {ex.Message}");
        }
    }
}
