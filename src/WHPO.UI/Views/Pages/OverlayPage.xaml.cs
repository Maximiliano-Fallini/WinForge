using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Overlay;
using WHPO_UI.Services;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Configuración de la superposición de métricas de juegos: activación, atajos
/// configurables (mostrar/ocultar y bloquear/desbloquear), opacidad, esquina,
/// colores por métrica y qué datos se muestran. Las métricas se configuran como
/// BADGES arrastrables en una GRILLA FIJA (máx. 4 por línea): cada línea de la
/// configuración es una línea del overlay y el switch de cada badge
/// muestra/oculta ese dato (FPS, lows 1%/0.1%, uso/MHz/temp/watts de CPU y GPU,
/// MB/MHz de RAM). Arrastrar en 2D mueve el badge entre líneas.
/// </summary>
public sealed partial class OverlayPage : Page
{
    private readonly ISettingsService _settings;
    private readonly ILoggingService _log;
    private readonly OverlayService _overlay;

    private bool _loading;
    private DispatcherQueueTimer? _saveTimer;

    private const int ModAlt = 0x1;
    private const int ModCtrl = 0x2;
    private const int ModShift = 0x4;
    private const int ModWin = 0x8;

    public OverlayPage()
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _log = App.Services.GetRequiredService<ILoggingService>();
        _overlay = App.Services.GetRequiredService<OverlayService>();

        OverlayEnabledToggle.Toggled += OverlayEnabledToggle_Toggled;
        ShowHotkeyButton.Click += ShowHotkeyButton_Click;
        LockHotkeyButton.Click += LockHotkeyButton_Click;
        OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
        FontSizeSlider.ValueChanged += FontSizeSlider_ValueChanged;
        CornerComboBox.SelectionChanged += CornerComboBox_SelectionChanged;
        ResetOrderButton.Click += ResetOrderButton_Click;

        // Drag de badges: el pointer se captura en el PANEL (no en el badge). Al
        // reordenar se remueve/inserta el badge del árbol y, si el capturado fuera
        // el badge, WinUI dispara PointerCaptureLost (que limpiaba el estado) y el
        // Insert fallaba con ArgumentException.
        MetricBadgePanel.PointerMoved += OnBadgeMoved;
        MetricBadgePanel.PointerReleased += OnBadgeReleased;
        MetricBadgePanel.PointerCanceled += OnBadgeDragEnd;
        MetricBadgePanel.PointerCaptureLost += OnBadgeDragEnd;
        FpsColorButton.Click += (_, _) => ShowColorPicker(FpsColorButton, "overlay.colorFps", "#FFFFFF");
        CpuColorButton.Click += (_, _) => ShowColorPicker(CpuColorButton, "overlay.colorCpu", "#FFFFFF");
        GpuColorButton.Click += (_, _) => ShowColorPicker(GpuColorButton, "overlay.colorGpu", "#FFFFFF");
        RamColorButton.Click += (_, _) => ShowColorPicker(RamColorButton, "overlay.colorRam", "#FFFFFF");
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadState();
    }

    private void LoadState()
    {
        _loading = true;
        try
        {
            OverlayEnabledToggle.IsOn = _settings.Get("overlay.enabled", false);
            ShowHotkeyButton.Content = HotkeyLabel("overlay.showHotkeyVk", 0x58, "overlay.showHotkeyMods", ModCtrl | ModAlt);
            LockHotkeyButton.Content = HotkeyLabel("overlay.lockHotkeyVk", 0x43, "overlay.lockHotkeyMods", ModCtrl | ModAlt);

            OpacitySlider.Value = Math.Clamp(_settings.Get("overlay.opacity", 0.85), 0.0, 1.0);
            UpdateOpacityText();
            FontSizeSlider.Value = Math.Clamp(_settings.Get("overlay.fontSize", 1.4), 0.6, 2.0);
            UpdateFontSizeText();

            string corner = _settings.Get("overlay.corner", "top-right");
            foreach (var item in CornerComboBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag as string == corner)
                {
                    CornerComboBox.SelectedItem = item;
                    break;
                }
            }

            // Métricas como badges: orden + estado se cargan desde "overlay.metricOrder"
            // (la primera vez se migran desde los switches viejos y se guardan).
            if (LoadMetricBadges())
            {
                // Migración de la versión de switches → badges: persistir el orden
                // derivado para que el overlay (ya corriendo) lo tome sin reiniciar.
                _settings.Save();
                _overlay.ApplyWindowConfig();
            }

            UpdateColorButton(FpsColorButton, _settings.Get("overlay.colorFps", "#FFFFFF"));
            UpdateColorButton(CpuColorButton, _settings.Get("overlay.colorCpu", "#FFFFFF"));
            UpdateColorButton(GpuColorButton, _settings.Get("overlay.colorGpu", "#FFFFFF"));
            UpdateColorButton(RamColorButton, _settings.Get("overlay.colorRam", "#FFFFFF"));
        }
        finally
        {
            _loading = false;
        }
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        bool enabled = OverlayEnabledToggle.IsOn;
        if (enabled)
        {
            var show = HotkeyLabel("overlay.showHotkeyVk", 0x58, "overlay.showHotkeyMods", ModCtrl | ModAlt);
            var lockH = HotkeyLabel("overlay.lockHotkeyVk", 0x43, "overlay.lockHotkeyMods", ModCtrl | ModAlt);
            OverlayStatusText.Text = string.Format(
                I18n.T("Activo. {0} muestra/oculta · {1} bloquea/desbloquea. El FPS corresponde al juego en primer plano."),
                show, lockH);
        }
        else
        {
            OverlayStatusText.Text = I18n.T("Inactivo. Activá el interruptor para superponer las métricas sobre tus juegos.");
        }
    }

    private void OverlayEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _overlay.Enabled = OverlayEnabledToggle.IsOn;
        _settings.Save();
        UpdateStatusText();
    }

    // ===== Atajos =====

    private void ShowHotkeyButton_Click(object sender, RoutedEventArgs e)
        => _ = CaptureHotkeyAsync("overlay.showHotkeyVk", "overlay.showHotkeyMods", ShowHotkeyButton, UpdateStatusText);

    private void LockHotkeyButton_Click(object sender, RoutedEventArgs e)
        => _ = CaptureHotkeyAsync("overlay.lockHotkeyVk", "overlay.lockHotkeyMods", LockHotkeyButton, UpdateStatusText);

    private string HotkeyLabel(string vkKey, int defaultVk, string modsKey, int defaultMods)
    {
        int vk = _settings.Get(vkKey, defaultVk);
        int mods = _settings.Get(modsKey, defaultMods);
        string name = OverlayService.ModsName(mods);
        return string.IsNullOrEmpty(name) ? OverlayService.KeyName(vk) : $"{name}+{OverlayService.KeyName(vk)}";
    }

    /// <summary>
    /// Captura la próxima combinación de teclas con un diálogo + muestreo por
    /// GetAsyncKeyState (funciona aunque el foco esté en el juego; Esc cancela).
    /// </summary>
    private async Task CaptureHotkeyAsync(string vkKey, string modsKey, Button button, Action? after)
    {
        if (XamlRoot == null) return;
        var status = new TextBlock
        {
            Text = I18n.T("Presioná la nueva tecla... Esc para cancelar."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Atajo de teclado"),
            Content = status,
            CloseButtonText = I18n.T("Cancelar")
        };
        var tcs = new TaskCompletionSource<(int Vk, int Mods)>();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(40);
        timer.Tick += (s, _) =>
        {
            if (KeyIsDown(0x1B))
            {
                timer.Stop();
                tcs.TrySetResult((0, 0));
                dialog.Hide();
                return;
            }
            foreach (var vk in OverlayService.HotkeyKeys)
            {
                if (IsModifierKey(vk)) continue;
                if (!KeyIsDown(vk)) continue;
                timer.Stop();
                int mods = 0;
                if (KeyIsDown(0x11)) mods |= ModCtrl;
                if (KeyIsDown(0x10)) mods |= ModShift;
                if (KeyIsDown(0x12)) mods |= ModAlt;
                if (KeyIsDown(0x5B) || KeyIsDown(0x5C)) mods |= ModWin;
                tcs.TrySetResult((vk, mods));
                dialog.Hide();
                return;
            }
        };
        timer.Start();
        await dialog.ShowAsync();
        timer.Stop();

        (int Vk, int Mods) chosen = tcs.Task.IsCompleted ? tcs.Task.Result : (0, 0);
        if (chosen.Vk <= 0) return;

        _settings.Set(vkKey, chosen.Vk);
        _settings.Set(modsKey, chosen.Mods);
        _settings.Save();
        button.Content = HotkeyLabel(vkKey, chosen.Vk, modsKey, chosen.Mods);
        // Re-registrar el atajo global (RegisterHotKey) con la combinación nueva.
        _overlay.ApplyWindowConfig();
        after?.Invoke();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool KeyIsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;

    // ===== Apariencia =====

    private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("overlay.opacity", OpacitySlider.Value);
        ScheduleSave();
        _overlay.ApplyWindowConfig();
        UpdateOpacityText();
    }

    private void UpdateOpacityText() => OpacityValueText.Text = $"{OpacitySlider.Value * 100:F0}%";

    private void FontSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("overlay.fontSize", FontSizeSlider.Value);
        ScheduleSave();
        _overlay.ApplyWindowConfig();
        UpdateFontSizeText();
    }

    private void UpdateFontSizeText() => FontSizeValueText.Text = $"{FontSizeSlider.Value * 100:F0}%";

    private void CornerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CornerComboBox.SelectedItem is ComboBoxItem { Tag: string corner })
        {
            _settings.Set("overlay.corner", corner);
            _settings.Save();
            _overlay.SetCorner(corner);
        }
    }

    private void ShowColorPicker(Button anchor, string key, string defaultHex)
    {
        var picker = new ColorPicker
        {
            Color = ParseHex(_settings.Get(key, defaultHex)),
            IsColorChannelTextInputVisible = true,
            IsAlphaEnabled = false
        };
        var flyout = new Flyout { Content = picker };
        picker.ColorChanged += (s, e) =>
        {
            var c = e.NewColor;
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _settings.Set(key, hex);
            ScheduleSave();
            UpdateColorButton(anchor, hex);
            _overlay.ApplyWindowConfig();
        };
        flyout.ShowAt(anchor);
    }

    private static void UpdateColorButton(Button button, string hex)
    {
        try
        {
            button.Background = new SolidColorBrush(ParseHex(hex));
        }
        catch
        {
            // Color inválido en settings: dejar el botón con el color por defecto.
        }
    }

    private static Windows.UI.Color ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        byte r = Convert.ToByte(h.Substring(0, 2), 16);
        byte g = Convert.ToByte(h.Substring(2, 2), 16);
        byte b = Convert.ToByte(h.Substring(4, 2), 16);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }

    // ===== Métricas como badges (arrastrar = ordenar, switch = mostrar/ocultar) =====

    /// <summary>Restaura el orden por defecto de las métricas: cada grupo (CPU/GPU/RAM)
    /// en su propia línea con la métrica core (la %) al inicio, y FPS, 1% low y
    /// 0.1% low en líneas separadas al final (el FPS debajo de todo).</summary>
    private void ResetOrderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = new HashSet<string>(_enabledMetrics, StringComparer.Ordinal);

        _enabledMetrics.Clear();

        // Orden por defecto: una fila por grupo, core primero; FPS y lows cada uno
        // en su propia línea, con el FPS abajo de todos.
        _rows = new List<List<string>>
        {
            new() { "cpuUsage", "cpuMhz", "cpuTemp", "cpuWatts" },
            new() { "gpuUsage", "gpuMhz", "gpuTemp", "gpuWatts" },
            new() { "ramMb", "ramMhz" },
            new() { "low1" },
            new() { "low01" },
            new() { "fps" }
        };
        _rows = OverlayWindow.NormalizeRows(_rows);
        _enabledMetrics.UnionWith(enabled);
        RebuildPanel();
        SaveMetricOrder();
    }

    // Definición de cada badge: id (se persiste en "overlay.metricRows") y etiqueta.
    private static readonly (string Id, string Label)[] MetricBadgeDefs =
    {
        ("fps", "FPS"),
        ("low1", "1% low"),
        ("low01", "0.1% low"),
        ("cpuUsage", "CPU %"),
        ("cpuMhz", "CPU MHz"),
        ("cpuTemp", "CPU °C"),
        ("cpuWatts", "CPU W"),
        ("gpuUsage", "GPU %"),
        ("gpuMhz", "GPU MHz"),
        ("gpuTemp", "GPU °C"),
        ("gpuWatts", "GPU W"),
        ("ramMb", "RAM MB"),
        ("ramMhz", "RAM MHz")
    };

    private readonly List<(string Id, Border Badge, ToggleSwitch Toggle)> _metricBadges = new();
    private readonly HashSet<string> _enabledMetrics = new(StringComparer.Ordinal);

    // Modelo lógico de filas de badges (cada fila = una línea del overlay). El
    // panel visual se reconstruye desde acá y NormalizeRows garantiza la
    // invariante de grupos (una familia por fila, core primero, cpu→gpu→ram→fps).
    private List<List<string>> _rows = new();

    private Border? _dragBadge;
    private bool _dragActive;
    private bool _dragMoved;
    private double _dragStartX;
    private double _dragStartY;
    private int _dragTargetRow = -1;
    private int _dragTargetCol = -1;
    private bool _dragTargetNewRow;

    // "Skeleton" de drop: un hueco fantasma que muestra DÓNDE caería el badge
    // arrastrado si se suelta. La vista previa se arma reconstruyendo las filas
    // desde un SNAPSHOT del estado original con el skeleton insertado (empuja a
    // los demás, con overflow a la fila siguiente como al soltar). Al soltar o
    // cancelar se restaura el snapshot y el reorden real se aplica sobre él.
    private Border? _dropSkeleton;
    private List<List<string>> _dragSnapshot = new();

    /// <summary>
    /// Arma las FILAS de badges desde la configuración guardada. Formato actual:
    /// "overlay.metricRows" = lista de filas (cada fila es una lista de ids; la
    /// fila = una línea del overlay) y "overlay.metricEnabled" = ids visibles.
    /// Versión vieja: "overlay.metricOrder" plano + "metricEnabled" → se migra
    /// agrupando por familia (mismo criterio que el render del overlay). Si no hay
    /// nada, se arma desde los switches viejos con el orden clásico. Devuelve true
    /// si migró y conviene persistir (el overlay lo toma sin reiniciar).
    /// </summary>
    private bool LoadMetricBadges()
    {
        _metricBadges.Clear();
        _enabledMetrics.Clear();
        MetricBadgePanel.Children.Clear();

        bool migrated = !_settings.Contains("overlay.metricRows");
        var rows = new List<List<string>>();
        var enabled = new List<string>();

        var savedRows = _settings.Get("overlay.metricRows", new List<List<string>>());
        if (savedRows != null && savedRows.Count > 0)
        {
            // Formato actual: filas explícitas (todas las métricas, activas o no).
            foreach (var row in savedRows)
            {
                var ids = row.Where(IsValidMetricId).ToList();
                if (ids.Count > 0) rows.Add(ids);
            }
            var savedEnabled = _settings.Get("overlay.metricEnabled", new List<string>());
            if (savedEnabled != null) enabled.AddRange(savedEnabled.Where(IsValidMetricId));
        }
        else if (!_settings.Contains("overlay.metricEnabled"))
        {
            // Primera corrida: desde los switches viejos (orden clásico), con FPS
            // y lows 1% / 0.1% en líneas separadas por defecto (FPS abajo de todo).
            enabled = BuildDefaultMetricOrder();
            rows = ChunkRows(enabled);
            rows = OverlayWindow.SplitFpsAndLows(rows);
        }
        else
        {
            // Versión vieja de badges: orden plano + metricEnabled → filas por familia.
            var savedOrder = _settings.Get("overlay.metricOrder", new List<string>());
            var flat = savedOrder != null ? savedOrder.Where(IsValidMetricId).ToList() : new List<string>();
            var savedEnabled = _settings.Get("overlay.metricEnabled", new List<string>());
            if (savedEnabled != null) enabled.AddRange(savedEnabled.Where(IsValidMetricId));
            rows = OverlayWindow.GroupByFamily(flat);
            rows = OverlayWindow.SplitFpsAndLows(rows);
        }

        // Invariante de grupos: NormalizeRows separa filas mezcladas (una familia
        // por fila), ordena cpu → gpu → ram → fps y ubica el core (el %) al inicio
        // de su grupo. Corrige configs viejas con métricas en el grupo equivocado.
        rows = OverlayWindow.NormalizeRows(rows);
        rows = rows.SelectMany(ChunkRows).ToList();

        // Completar con los badges que falten: cada uno en una fila propia y
        // NormalizeRows lo re-ubica dentro de su grupo (nunca se mezcla con otros).
        foreach (var def in MetricBadgeDefs)
        {
            if (rows.Any(r => r.Contains(def.Id))) continue;
            rows.Add(new List<string> { def.Id });
        }
        rows = OverlayWindow.NormalizeRows(rows);

        _rows = rows;
        _enabledMetrics.UnionWith(enabled);
        RebuildPanel();
        if (migrated) SaveMetricOrder();
        return migrated;
    }

    /// <summary>Máximo de badges por línea (la grilla de la config es fija).</summary>
    private const int MaxBadgesPerRow = 4;

    // Los lows/FPS en filas propias ahora se resuelven con OverlayWindow.SplitFpsAndLows
    // (compartido con el render del overlay), por eso ya no hay helper local.

    /// <summary>Ancho fijo de cada badge (la grilla es estable, no depende del ancho de la ventana).</summary>
    private const double BadgeWidth = 150;

    /// <summary>Parte una lista plana de ids en filas de a 4 (última fila puede quedar corta).</summary>
    private static List<List<string>> ChunkRows(List<string> ids)
    {
        var rows = new List<List<string>>();
        for (int i = 0; i < ids.Count; i += MaxBadgesPerRow)
            rows.Add(ids.Skip(i).Take(MaxBadgesPerRow).ToList());
        return rows;
    }

    /// <summary>
    /// Fila de badges: un StackPanel horizontal. El orden de Children ES el orden
    /// visual (Children.Insert empuja a los demás naturalmente, sin columnas que
    /// reasignar — el skeleton empuja así, sin superposición ni reconstrucción).
    /// </summary>
    private static StackPanel CreateRowPanel()
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left
        };
    }

    /// <summary>Línea separadora entre filas de badges (no es una fila: el drag la ignora).</summary>
    private static Border CreateSeparator()
    {
        return new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 128, 128, 128)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false
        };
    }

    /// <summary>Cantidad de filas de badges (ignora separadores).</summary>
    private int RowCount()
        => MetricBadgePanel.Children.OfType<StackPanel>().Count();

    /// <summary>Devuelve la fila n (n = índice ENTRE filas, no entre hijos del panel).</summary>
    private StackPanel? RowAt(int rowIndex)
    {
        int seen = 0;
        foreach (var child in MetricBadgePanel.Children)
        {
            if (child is StackPanel sp)
            {
                if (seen == rowIndex) return sp;
                seen++;
            }
        }
        return null;
    }

    /// <summary>Garantiza que exista la fila rowIndex (creándola con su separador si falta).</summary>
    private StackPanel EnsureRow(int rowIndex)
    {
        while (RowCount() <= rowIndex)
        {
            MetricBadgePanel.Children.Add(CreateRowPanel());
            MetricBadgePanel.Children.Add(CreateSeparator());
        }
        return RowAt(rowIndex)!;
    }

    /// <summary>
    /// Reordena los hijos del panel como [fila, separador, fila, separador, ...]
    /// — separadores SOLO entre filas, nunca al principio ni al final.
    /// </summary>
    private void SyncSeparators()
    {
        var rows = MetricBadgePanel.Children.OfType<StackPanel>().ToList();
        MetricBadgePanel.Children.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            MetricBadgePanel.Children.Add(rows[i]);
            if (i < rows.Count - 1)
                MetricBadgePanel.Children.Add(CreateSeparator());
        }
    }

    /// <summary>
    /// Reconstruye el panel de badges desde el modelo lógico _rows (cada fila =
    /// una línea del overlay). Crea los badges de nuevo y re-sincroniza
    /// separadores y visuales.
    /// </summary>
    private void RebuildPanel()
    {
        _metricBadges.Clear();
        MetricBadgePanel.Children.Clear();
        foreach (var rowIds in _rows)
        {
            var row = CreateRowPanel();
            foreach (var id in rowIds.Take(MaxBadgesPerRow))
            {
                var def = MetricBadgeDefs.First(d => d.Id == id);
                row.Children.Add(AddBadge(def.Id, def.Label, _enabledMetrics.Contains(def.Id)));
            }
            MetricBadgePanel.Children.Add(row);
        }
        SyncSeparators();
        UpdateBadgeVisuals();
    }

    private Border AddBadge(string id, string label, bool enabled)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var toggle = new ToggleSwitch
        {
            IsOn = enabled,
            MinWidth = 40,
            MinHeight = 20,
            OnContent = "",
            OffContent = "",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            // El switch NO captura el puntero: así TODO el badge es superficie de
            // arrastre (clic corto = toggle, arrastre = mover). Si el switch
            // capturara el puntero, agarrar el centro del badge (que es el switch)
            // toggleaba en vez de arrastrar — el bug que impedía mover los badges.
            IsHitTestVisible = false
        };
        toggle.Toggled += (_, _) => OnBadgeSwitchToggled(id, toggle.IsOn);

        // Contenido en grilla: etiqueta a la izquierda, switch a la derecha.
        var content = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(toggle, 1);
        content.Children.Add(text);
        content.Children.Add(toggle);

        var badge = new Border
        {
            Width = BadgeWidth,
            Background = ThemeBrushes.Get("CardHoverBrush"),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 4, 10, 4),
            Child = content
        };
        if (OverlayWindow.IsCoreMetric(id))
            ToolTipService.SetToolTip(badge, I18n.T("Métrica principal de la línea (no se puede mover)"));
        badge.PointerPressed += OnBadgePressed;
        _metricBadges.Add((id, badge, toggle));
        return badge;
    }

    private string BadgeId(Border badge)
        => _metricBadges.First(b => ReferenceEquals(b.Badge, badge)).Id;

    private ToggleSwitch BadgeToggle(Border badge)
        => _metricBadges.First(b => ReferenceEquals(b.Badge, badge)).Toggle;

    private static bool IsValidMetricId(string id)
        => MetricBadgeDefs.Any(d => d.Id == id);

    /// <summary>Orden por defecto migrado desde los switches viejos (orden de render clásico).</summary>
    private List<string> BuildDefaultMetricOrder()
    {
        bool b(string key, bool def) => _settings.Get(key, def);
        var order = new List<string>();
        if (b("overlay.showCpu", true))
        {
            if (b("overlay.cpuUsage", true)) order.Add("cpuUsage");
            if (b("overlay.cpuMhz", true)) order.Add("cpuMhz");
            if (b("overlay.cpuTemp", true)) order.Add("cpuTemp");
            if (b("overlay.cpuWatts", false)) order.Add("cpuWatts");
        }
        if (b("overlay.showGpu", true))
        {
            if (b("overlay.gpuUsage", true)) order.Add("gpuUsage");
            if (b("overlay.gpuMhz", true)) order.Add("gpuMhz");
            if (b("overlay.gpuTemp", true)) order.Add("gpuTemp");
            if (b("overlay.gpuWatts", false)) order.Add("gpuWatts");
        }
        if (b("overlay.showRam", true))
        {
            order.Add("ramMb");
            order.Add("ramMhz");
        }
        if (b("overlay.showFps", true)) order.Add("fps");
        if (b("overlay.low1", true)) order.Add("low1");
        if (b("overlay.low01", false)) order.Add("low01");
        return order;
    }

    private void UpdateBadgeVisuals()
    {
        foreach (var (id, badge, _) in _metricBadges)
        {
            bool enabled = _enabledMetrics.Contains(id);
            badge.Opacity = enabled ? 1.0 : 0.45;
            // El borde SIEMPRE mide 1px (transparente si está activo): si pasara de
            // 0 a 1, el alto/ancho deseado del badge cambia 2px y la fila entera se
            // agranda/achica al switchear (el bug que reportaste).
            badge.BorderThickness = new Thickness(1);
            if (enabled)
            {
                badge.Background = ThemeBrushes.Get("CardHoverBrush");
                badge.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            }
            else
            {
                badge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                var c = ((SolidColorBrush)ThemeBrushes.Get("SecondaryTextBrush")).Color;
                badge.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(130, c.R, c.G, c.B));
            }
        }
    }

    /// <summary>
    /// Persiste las FILAS de badges ("overlay.metricRows" — cada fila es una línea
    /// del overlay), el orden plano por compatibilidad ("overlay.metricOrder") y
    /// los ids visibles ("overlay.metricEnabled"), y lo aplica al overlay en vivo.
    /// </summary>
    private void SaveMetricOrder()
    {
        var rows = _rows.Select(r => r.ToList()).ToList();
        rows = OverlayWindow.NormalizeRows(rows);
        _rows = rows;
        _settings.Set("overlay.metricRows", rows);
        _settings.Set("overlay.metricOrder", rows.SelectMany(r => r).ToList());
        _settings.Set("overlay.metricEnabled", _enabledMetrics.ToList());
        _settings.Save();
        _overlay.ApplyWindowConfig();
    }

    /// <summary>
    /// Calcula las filas actuales del panel leyendo su estructura real: cada hijo
    /// de MetricBadgePanel es una fila (StackPanel) y cada fila = una línea del
    /// overlay. Devuelve solo las filas no vacías.
    /// </summary>
    private List<List<string>> ComputeRows()
    {
        var rows = new List<List<string>>();
        foreach (var child in MetricBadgePanel.Children)
        {
            if (child is not StackPanel row) continue;
            var ids = row.Children.OfType<Border>().Select(BadgeId).ToList();
            if (ids.Count > 0) rows.Add(ids);
        }
        return rows;
    }

    // ===== Drag para reordenar (mantener el clic y arrastrar) =====

    /// <summary>Grupo de la fila n del panel (el de su primer badge; ignora el skeleton).</summary>
    private string RowGroupAt(int rowIndex)
    {
        var row = RowAt(rowIndex);
        if (row != null)
        {
            var first = row.Children.OfType<Border>()
                .FirstOrDefault(b => !ReferenceEquals(b, _dropSkeleton));
            if (first != null) return OverlayWindow.GroupOf(BadgeId(first));
        }
        return "";
    }

    /// <summary>Índice de la primera fila que pertenece al grupo (panel visual).</summary>
    private int FirstRowIndexOfGroup(string group)
    {
        int idx = 0;
        foreach (var child in MetricBadgePanel.Children)
        {
            if (child is StackPanel sp)
            {
                var first = sp.Children.OfType<Border>()
                    .FirstOrDefault(b => !ReferenceEquals(b, _dropSkeleton));
                if (first != null && OverlayWindow.GroupOf(BadgeId(first)) == group) return idx;
                idx++;
            }
        }
        return -1;
    }

    /// <summary>Índice de la primera fila del grupo en un modelo lógico (lista de ids).</summary>
    private static int FirstRowOfGroupIn(List<List<string>> model, string group)
    {
        for (int i = 0; i < model.Count; i++)
            if (model[i].Count > 0 && OverlayWindow.GroupOf(model[i][0]) == group) return i;
        return -1;
    }

    private void OnBadgePressed(object sender, PointerRoutedEventArgs e)
    {
        // El badge es superficie de arrastre: el switch tiene IsHitTestVisible
        // false, así que el puntero siempre llega acá. Clic corto = toggle;
        // arrastre = reordenar (en 2D: puede cambiar de fila).
        _dragBadge = (Border)sender;
        if (OverlayWindow.IsCoreMetric(BadgeId(_dragBadge)))
        {
            // Métricas core (CPU %, GPU %, RAM MB, FPS): definen la línea y no se
            // pueden mover. El clic corto sigue alternando mostrar/ocultar.
            _dragActive = false;
            _dragMoved = false;
            _dragTargetRow = -1;
            _dragTargetCol = -1;
            _dragTargetNewRow = false;
            e.Handled = true;
            return;
        }
        _dragActive = true;
        _dragMoved = false;
        _dragTargetRow = -1;
        _dragTargetCol = -1;
        _dragTargetNewRow = false;
        var start = e.GetCurrentPoint(MetricBadgePanel).Position;
        _dragStartX = start.X;
        _dragStartY = start.Y;
        // El badge "sigue" al puntero con un RenderTransform. La vista previa del
        // drop (skeleton) se arma sobre un snapshot del estado original para poder
        // restaurarlo al soltar; el reorden real se aplica de una sola vez.
        _dragSnapshot = ComputeRows();
        Canvas.SetZIndex(_dragBadge, 100);
        _dropSkeleton = new Border
        {
            // En un StackPanel (no un Grid) el ancho NO viene de la celda: hay que
            // fijarlo, o el skeleton colapsa a 0px y no se ve ni empuja.
            Width = BadgeWidth,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(36, 140, 140, 140)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 180, 180, 180)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        MetricBadgePanel.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnBadgeMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragActive || _dragBadge == null) return;
        var pt = e.GetCurrentPoint(MetricBadgePanel).Position;
        if (!_dragMoved)
        {
            if (Math.Abs(pt.X - _dragStartX) < 8 && Math.Abs(pt.Y - _dragStartY) < 8) return;
            _dragMoved = true;
        }

        // Feedback en vivo: el badge se desplaza junto al puntero (X e Y).
        _dragBadge.RenderTransform = new TranslateTransform { X = pt.X - _dragStartX, Y = pt.Y - _dragStartY };
        (_dragTargetRow, _dragTargetCol, _dragTargetNewRow) = ComputeInsertSlot(pt.X, pt.Y);
        UpdateDropSkeleton(_dragTargetRow, _dragTargetCol, _dragTargetNewRow);
        e.Handled = true;
    }

    /// <summary>
    /// Posición destino del badge arrastrado en 2D: (fila, columna) donde cae el
    /// puntero. La métrica SOLO puede soltarse en filas de SU PROPIO grupo
    /// (cpu/gpu/ram/fps): si el puntero cae sobre filas de otro grupo, se ajusta
    /// a la fila más cercana del grupo (el drag no mezcla grupos). Si el puntero
    /// cae claramente debajo del panel, devuelve una fila NUEVA al final (al
    /// guardar, NormalizeRows la ubica dentro del grupo). En la fila core (la %)
    /// la columna mínima es 1: no se puede insertar antes del core.
    /// </summary>
    private (int Row, int Col, bool NewRow) ComputeInsertSlot(double x, double y)
    {
        var group = OverlayWindow.GroupOf(BadgeId(_dragBadge!));

        // Detectar filas IGNORANDO separadores: no son filas y no deben contar
        // para la posición del puntero.
        var rows = new List<(double Y, double H, int Index)>();
        int rowCount = 0;
        foreach (var child in MetricBadgePanel.Children)
        {
            if (child is not StackPanel sp) continue;
            var origin = sp.TransformToVisual(MetricBadgePanel)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            rows.Add((origin.Y, sp.ActualHeight, rowCount));
            rowCount++;
        }

        // Fila válida más cercana DENTRO del grupo del badge arrastrado.
        int bestRow = -1;
        double bestDist = double.MaxValue;
        for (int r = 0; r < rows.Count; r++)
        {
            if (RowGroupAt(rows[r].Index) != group) continue;
            var row = rows[r];
            double dist = y < row.Y ? row.Y - y : (y > row.Y + row.H ? y - (row.Y + row.H) : 0);
            if (dist < bestDist) { bestDist = dist; bestRow = row.Index; }
        }

        // Si el puntero quedó claramente debajo de la última fila, crear fila
        // nueva al final (NormalizeRows la ubica dentro del grupo al guardar).
        bool newRow = bestRow < 0 ||
                      (rows.Count > 0 && y > rows[^1].Y + rows[^1].H + 6);

        int col = 0;
        if (!newRow && RowAt(bestRow) is { } targetRow)
        {
            foreach (var child in targetRow.Children)
            {
                if (child is not FrameworkElement el) continue;
                if (ReferenceEquals(el, _dragBadge) || ReferenceEquals(el, _dropSkeleton)) continue;
                var origin = el.TransformToVisual(MetricBadgePanel)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                if (x > origin.X + el.ActualWidth / 2) col++;
            }
            // Fila core del grupo: el badge core (la %) es el primero y no se mueve.
            if (bestRow == FirstRowIndexOfGroup(group)) col = Math.Max(col, 1);
        }
        return (bestRow, col, newRow);
    }

    private void OnBadgeReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragBadge == null) { e.Handled = true; return; }
        var badge = _dragBadge;
        var moved = _dragMoved;

        if (!_dragActive)
        {
            // Clic sobre un badge core (CPU %, GPU %, RAM MB, FPS): no hay drag,
            // solo toggle mostrar/ocultar.
            _dragBadge = null;
            badge.RenderTransform = null;
            Canvas.SetZIndex(badge, 0);
            BadgeToggle(badge).IsOn = !BadgeToggle(badge).IsOn;
            e.Handled = true;
            return;
        }

        int row = _dragTargetRow;
        int col = _dragTargetCol;
        bool newRow = _dragTargetNewRow;
        RemoveDropSkeleton();
        _dragActive = false;
        _dragBadge = null;
        try { MetricBadgePanel.ReleasePointerCapture(e.Pointer); } catch { }

        if (badge != null)
        {
            badge.RenderTransform = null;
            Canvas.SetZIndex(badge, 0);
            if (moved)
            {
                // La vista previa mutó el árbol (skeleton + overflow): restaurar
                // el snapshot del press para que el reorden final sea determinista
                // y no arrastre mutaciones de la vista previa.
                RestoreSnapshot();
                ApplyReorder(badge, row, col, newRow);
                SaveMetricOrder();
            }
            else
            {
                _dropSkeleton = null;
                // Clic corto (sin arrastre): toggle del switch integrado.
                BadgeToggle(badge).IsOn = !BadgeToggle(badge).IsOn;
            }
        }
        e.Handled = true;
    }

    private void OnBadgeDragEnd(object sender, PointerRoutedEventArgs e)
    {
        // Solo cancelar si hay un drag activo: al soltar con éxito se llama
        // ReleasePointerCapture, y el PointerCaptureLost que dispara NO debe
        // restaurar el snapshot (sería una restauración espuria que borra el
        // reorden aplicado y "elimina las demás" badges).
        if (!_dragActive) return;
        if (_dragBadge != null)
        {
            _dragBadge.RenderTransform = null;
            Canvas.SetZIndex(_dragBadge, 0);
        }
        // Cancelación (Esc / pérdida de captura): volver al estado original.
        RestoreSnapshot();
        _dragActive = false;
        _dragBadge = null;
        _dropSkeleton = null;
    }

    /// <summary>
    /// Ubica el skeleton en la fila/columna destino mutando SOLO esa fila (con
    /// overflow hacia la siguiente): el skeleton EMPUJA a los demás badges como
    /// pasará al soltar. No se reconstruye el panel completo durante el move — eso
    /// rompía la card (COMException al remover/agregar el árbol en cada evento).
    /// </summary>
    private void UpdateDropSkeleton(int row, int col, bool newRow)
    {
        if (_dropSkeleton == null) return;
        try
        {
            // Quita el skeleton del árbol pero CONSERVA la referencia del campo:
            // ComputeInsertSlot lo excluye del cálculo de columna mientras dura
            // el drag, y se reinserta en cada move. Para filas nuevas (NewRow)
            // no hay preview: el badge simplemente sigue al puntero.
            RemoveDropSkeleton();
            if (newRow || row < 0) return;
            PreviewInsert(EnsureRow(row), _dropSkeleton, col, row);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayPage: falló la vista previa del drop: {ex.Message}");
            RestoreSnapshot();
        }
    }

    /// <summary>
    /// Inserta un elemento (el skeleton) en una fila por columna, reasignando las
    /// columnas de esa fila; si se pasa de 4, el excedente fluye a la fila
    /// siguiente (misma regla que al soltar). Mutación local, no toca el resto.
    /// </summary>
    private void PreviewInsert(StackPanel row, Border el, int col, int rowIndex)
    {
        // Nunca reconstruir la fila que contiene al badge arrastrado: al sacarlo
        // de los items el badge perdería su padre y desaparecería durante el drag.
        // En su propia fila, el badge mismo (transformado al puntero) indica la
        // posición — el skeleton solo aplica cuando caés en OTRA fila.
        if (ReferenceEquals(el, _dropSkeleton) && row.Children.Contains(_dragBadge)) return;

        var items = row.Children.OfType<Border>().ToList();
        items.Insert(Math.Clamp(col, 0, items.Count), el);

        if (items.Count <= MaxBadgesPerRow)
        {
            RebuildRow(row, items);
            return;
        }

        // Overflow: el último elemento fluye al inicio de la fila siguiente.
        var overflow = items[^1];
        items.RemoveAt(items.Count - 1);
        RebuildRow(row, items);
        PreviewInsert(EnsureRow(rowIndex + 1), overflow, 0, rowIndex + 1);
    }

    /// <summary>Reconstruye los hijos de una fila en orden (el orden ES la posición).</summary>
    private static void RebuildRow(StackPanel row, List<Border> items)
    {
        row.Children.Clear();
        for (int i = 0; i < items.Count; i++)
            row.Children.Add(items[i]);
    }

    /// <summary>
    /// Quita el skeleton del árbol (deja el hueco donde estaba). Conserva la
    /// referencia del campo: se reinserta en cada move y ComputeInsertSlot lo
    /// excluye del cálculo de columna mientras dura el drag.
    /// </summary>
    private void RemoveDropSkeleton()
    {
        if (_dropSkeleton == null) return;
        foreach (var child in MetricBadgePanel.Children)
        {
            if (child is StackPanel sp && sp.Children.Contains(_dropSkeleton))
            {
                sp.Children.Remove(_dropSkeleton);
                break;
            }
        }
    }

    /// <summary>
    /// Restaura el snapshot original completo (al cancelar el drag). Reconstruye
    /// todo el panel desde el estado capturado al presionar.
    /// </summary>
    private void RestoreSnapshot()
    {
        _dropSkeleton = null;
        try
        {
            // 1) Desprender TODOS los badges usando la referencia de sus FILAS
            //    (no badge.Parent): en WinUI 3, una fila detachada del panel deja
            //    a sus hijos con Parent null pero SIGUEN siendo de esa fila —
            //    agregarlos a otra fila lanzaba COMException 0x800F1000
            //    "Element is already the child of another element" y el panel
            //    quedaba vacío ("al soltar desaparecen todas").
            var rows = MetricBadgePanel.Children.OfType<StackPanel>().ToList();
            foreach (var r in rows) r.Children.Clear();
            MetricBadgePanel.Children.Clear();

            // 2) Reconstruir desde el snapshot (máx 4 por línea).
            var byId = _metricBadges.ToDictionary(b => b.Id, b => b.Badge);
            foreach (var rowIds in _dragSnapshot)
            {
                var row = CreateRowPanel();
                foreach (var id in rowIds.Take(MaxBadgesPerRow))
                {
                    if (byId.TryGetValue(id, out var badge))
                        row.Children.Add(badge);
                }
                MetricBadgePanel.Children.Add(row);
            }

            // 3) Red de seguridad: ningún badge puede quedar fuera del árbol.
            var placed = MetricBadgePanel.Children.OfType<StackPanel>()
                .SelectMany(r => r.Children.OfType<Border>()).ToHashSet();
            foreach (var badge in byId.Values)
            {
                if (placed.Contains(badge)) continue;
                var last = RowAt(RowCount() - 1);
                if (last == null || last.Children.Count >= MaxBadgesPerRow)
                {
                    last = CreateRowPanel();
                    MetricBadgePanel.Children.Add(last);
                }
                last.Children.Add(badge);
            }

            SyncSeparators();
        }
        catch (Exception ex)
        {
            // Última red: reconstrucción bruta desde el snapshot con filas nuevas.
            _log.LogWarning($"OverlayPage: RestoreSnapshot falló ({ex.Message}); reconstrucción bruta");
            foreach (var child in MetricBadgePanel.Children)
                if (child is StackPanel sp) sp.Children.Clear();
            MetricBadgePanel.Children.Clear();
            var byId = _metricBadges.ToDictionary(b => b.Id, b => b.Badge);
            foreach (var ids in ChunkRows(_dragSnapshot.SelectMany(r => r).Where(byId.ContainsKey).ToList()))
            {
                var row = CreateRowPanel();
                foreach (var id in ids) row.Children.Add(byId[id]);
                MetricBadgePanel.Children.Add(row);
            }
            SyncSeparators();
        }
    }

    /// <summary>
    /// Aplica el reorden al soltar sobre el SNAPSHOT (no sobre el árbol mutado
    /// por la vista previa): mueve el badge a la fila/columna destino del MISMO
    /// grupo, creando una fila nueva si hace falta (NewRow). Después normaliza
    /// los grupos (NormalizeRows) y reconstruye el panel.
    /// </summary>
    private void ApplyReorder(Border badge, int targetRow, int targetCol, bool newRow)
    {
        try
        {
            string id = BadgeId(badge);
            var model = _dragSnapshot.Select(r => r.ToList()).ToList();

            // Quitar el badge de su fila actual y limpiar filas vacías.
            foreach (var r in model) r.Remove(id);
            model.RemoveAll(r => r.Count == 0);

            if (newRow)
            {
                // Fila nueva al final: NormalizeRows la ubica dentro del grupo.
                model.Add(new List<string> { id });
            }
            else
            {
                if (targetRow < 0) targetRow = 0;
                while (model.Count <= targetRow) model.Add(new List<string>());
                var row = model[targetRow];
                int col = Math.Clamp(targetCol, 0, row.Count);
                // Fila core del grupo: no insertar antes del badge core (la %).
                if (targetRow == FirstRowOfGroupIn(model, OverlayWindow.GroupOf(id)))
                    col = Math.Max(col, 1);
                row.Insert(col, id);
            }

            _rows = OverlayWindow.NormalizeRows(model);
            RebuildPanel();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayPage: no se pudo reordenar badge: {ex.Message}");
        }
    }

    /// <summary>Muestra/oculta una métrica desde el switch de su badge (en vivo).</summary>
    private void OnBadgeSwitchToggled(string id, bool on)
    {
        if (_loading) return;
        if (on) _enabledMetrics.Add(id);
        else _enabledMetrics.Remove(id);
        UpdateBadgeVisuals();
        SaveMetricOrder();
    }

    // ===== Guardado con debounce (slider/color picker no spamean el disco) =====

    private void ScheduleSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                try { _settings.Save(); }
                catch (Exception ex) { _log.LogWarning($"OverlayPage: no se pudo guardar: {ex.Message}"); }
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
