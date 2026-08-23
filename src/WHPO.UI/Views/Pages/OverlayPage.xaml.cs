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

    /// <summary>Restaura el orden clásico de las métricas (CPU → GPU → RAM → FPS → lows).</summary>
    private void ResetOrderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = new HashSet<string>(_enabledMetrics, StringComparer.Ordinal);

        _metricBadges.Clear();
        _enabledMetrics.Clear();
        MetricBadgePanel.Children.Clear();

        // Orden clásico (mismo criterio que BuildDefaultMetricOrder, pero con las
        // 4 métricas de hardware fijas): CPU → GPU → RAM → FPS, en filas de 4.
        // Los lows 1% y 0.1% NO se agrupan con el FPS: cada uno va en su propia
        // línea (uno arriba del otro), como orden por defecto del overlay.
        var order = new List<string>
        {
            "cpuUsage", "cpuMhz", "cpuTemp", "cpuWatts",
            "gpuUsage", "gpuMhz", "gpuTemp", "gpuWatts",
            "ramMb", "ramMhz",
            "fps"
        };
        _enabledMetrics.UnionWith(enabled);
        foreach (var rowIds in ChunkRows(order))
        {
            AddRow(rowIds.Select(id => MetricBadgeDefs.First(d => d.Id == id))
                .Select(def => (def.Id, def.Label)).ToList());
        }
        AddRow(new List<(string Id, string Label)> { (MetricBadgeDefs.First(d => d.Id == "low1").Id, "1% low") });
        AddRow(new List<(string Id, string Label)> { (MetricBadgeDefs.First(d => d.Id == "low01").Id, "0.1% low") });
        UpdateBadgeVisuals();
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

    private Border? _dragBadge;
    private bool _dragActive;
    private bool _dragMoved;
    private double _dragStartX;
    private double _dragStartY;
    private int _dragTargetRow = -1;
    private int _dragTargetCol = -1;

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
            // Primera corrida: desde los switches viejos (orden clásico), partido
            // en filas de a 4 (la grilla es fija) y con los lows 1% / 0.1% en
            // líneas SEPARADAS (cada uno arriba del otro, por defecto).
            enabled = BuildDefaultMetricOrder();
            rows = ChunkRows(enabled);
            rows = SplitLowsIntoOwnRows(rows);
        }
        else
        {
            // Versión vieja de badges: orden plano + metricEnabled → filas por familia.
            var savedOrder = _settings.Get("overlay.metricOrder", new List<string>());
            var flat = savedOrder != null ? savedOrder.Where(IsValidMetricId).ToList() : new List<string>();
            var savedEnabled = _settings.Get("overlay.metricEnabled", new List<string>());
            if (savedEnabled != null) enabled.AddRange(savedEnabled.Where(IsValidMetricId));
            rows = OverlayWindow.GroupByFamily(flat);
        }

        // Garantizar la invariante de la grilla: ninguna fila pasa de 4 badges
        // (los guardados de versiones anteriores podrían tener filas más largas).
        rows = rows.SelectMany(ChunkRows).ToList();

        // Completar con los ids que falten (al final de la última fila, sin pasar
        // de 4 por línea — se crea fila nueva si la última está llena).
        foreach (var def in MetricBadgeDefs)
        {
            bool exists = rows.Any(r => r.Contains(def.Id));
            if (exists) continue;
            if (rows.Count == 0 || rows[^1].Count >= MaxBadgesPerRow)
                rows.Add(new List<string>());
            rows[^1].Add(def.Id);
        }

        _enabledMetrics.UnionWith(enabled);
        foreach (var rowIds in rows)
        {
            AddRow(rowIds.Select(id => MetricBadgeDefs.First(d => d.Id == id))
                .Select(def => (def.Id, def.Label)).ToList());
        }
        UpdateBadgeVisuals();
        SyncSeparators();
        return migrated;
    }

    /// <summary>Máximo de badges por línea (la grilla de la config es fija).</summary>
    private const int MaxBadgesPerRow = 4;

    /// <summary>
    /// Separa los lows 1% / 0.1% en filas PROPIAS (cada uno en su línea, uno
    /// arriba del otro) para el orden por defecto; el resto se mantiene en sus
    /// filas de a 4.
    /// </summary>
    private static List<List<string>> SplitLowsIntoOwnRows(List<List<string>> rows)
    {
        var keep = rows.Select(r => r.Where(id => id is not ("low1" or "low01")).ToList())
                       .Where(r => r.Count > 0).ToList();
        if (rows.Any(r => r.Contains("low1"))) keep.Add(new List<string> { "low1" });
        if (rows.Any(r => r.Contains("low01"))) keep.Add(new List<string> { "low01" });
        return keep;
    }

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

    private void AddRow(List<(string Id, string Label)> badges)
    {
        var row = CreateRowPanel();
        for (int i = 0; i < badges.Count && i < MaxBadgesPerRow; i++)
        {
            var badge = AddBadge(badges[i].Id, badges[i].Label, _enabledMetrics.Contains(badges[i].Id));
            row.Children.Add(badge);
        }
        MetricBadgePanel.Children.Add(row);
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
        var rows = ComputeRows();
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

    private void OnBadgePressed(object sender, PointerRoutedEventArgs e)
    {
        // El badge es superficie de arrastre: el switch tiene IsHitTestVisible
        // false, así que el puntero siempre llega acá. Clic corto = toggle;
        // arrastre = reordenar (en 2D: puede cambiar de fila).
        _dragBadge = (Border)sender;
        _dragActive = true;
        _dragMoved = false;
        _dragTargetRow = -1;
        _dragTargetCol = -1;
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
        (_dragTargetRow, _dragTargetCol) = ComputeInsertSlot(pt.X, pt.Y);
        UpdateDropSkeleton(_dragTargetRow, _dragTargetCol);
        e.Handled = true;
    }

    /// <summary>
    /// Posición destino del badge arrastrado en 2D: (fila, columna) donde cae el
    /// puntero. La fila se ubica por Y (los rects reales de los Grids fila) y la
    /// columna por X dentro de esa fila. Si el puntero cae debajo de la última
    /// fila, devuelve una fila nueva (índice = cantidad de filas). Al soltar,
    /// ApplyReorder mueve el badge a esa fila/columna, creando fila si hace falta.
    /// </summary>
    private (int Row, int Col) ComputeInsertSlot(double x, double y)
    {
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

        // Fila del puntero: la que lo contiene, o la más cercana; debajo de la
        // última → fila nueva.
        int rowIdx = rows.Count;
        if (rows.Count > 0)
        {
            double best = double.MaxValue;
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                double dist = y < row.Y ? row.Y - y : (y > row.Y + row.H ? y - (row.Y + row.H) : 0);
                if (dist < best) { best = dist; rowIdx = row.Index; }
            }
            // Si el puntero quedó claramente debajo de la última fila (más allá de
            // la mitad del hueco), crear fila nueva.
            if (rowIdx == rows.Count - 1 && y > rows[^1].Y + rows[^1].H + 6)
                rowIdx = rows.Count;
        }

        // Columna dentro de la fila destino (si la fila existe). Se excluyen el
        // badge arrastrado y el skeleton: sus centros no cuentan, o la inserción
        // queda desplazada.
        int col = 0;
        if (RowAt(rowIdx) is { } targetRow)
        {
            foreach (var child in targetRow.Children)
            {
                if (child is not FrameworkElement el) continue;
                if (ReferenceEquals(el, _dragBadge) || ReferenceEquals(el, _dropSkeleton)) continue;
                var origin = el.TransformToVisual(MetricBadgePanel)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                if (x > origin.X + el.ActualWidth / 2) col++;
            }
        }
        return (rowIdx, col);
    }

    private void OnBadgeReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragActive) return;
        var badge = _dragBadge;
        bool moved = _dragMoved;
        int row = _dragTargetRow;
        int col = _dragTargetCol;
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
                ApplyReorder(badge, row, col);
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
    private void UpdateDropSkeleton(int row, int col)
    {
        if (_dropSkeleton == null) return;
        try
        {
            // Quita el skeleton del árbol pero CONSERVA la referencia del campo:
            // ComputeInsertSlot lo excluye del cálculo de columna mientras dura
            // el drag, y se reinserta en cada move.
            RemoveDropSkeleton();
            if (row < 0) return;
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
    /// Aplica el reorden al soltar: mueve el badge a la fila/columna destino,
    /// creando una fila nueva si hace falta y respetando el máximo de 4 por línea
    /// (un badge que sobra en una fila llena fluye a la fila siguiente).
    /// </summary>
    private void ApplyReorder(Border badge, int targetRow, int targetCol)
    {
        try
        {
            // Quitar el badge de su fila actual (el StackPanel re-fluye solo: sin
            // columnas que reasignar, los demás quedan corridos a la izquierda).
            foreach (var child in MetricBadgePanel.Children)
            {
                if (child is not StackPanel sp) continue;
                if (sp.Children.Contains(badge))
                {
                    sp.Children.Remove(badge);
                    break;
                }
            }

            // Crear filas hasta llegar a la destino.
            EnsureRow(targetRow);
            InsertIntoRow(RowAt(targetRow)!, badge, targetCol, targetRow);

            // Limpiar filas que quedaron vacías (por el flujo de overflow o al
            // vaciar la fila de origen).
            for (int r = MetricBadgePanel.Children.Count - 1; r >= 0; r--)
            {
                if (MetricBadgePanel.Children[r] is StackPanel sp && sp.Children.Count == 0)
                    MetricBadgePanel.Children.RemoveAt(r);
            }

            // Separadores alineados con las filas que quedaron.
            SyncSeparators();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayPage: no se pudo reordenar badge: {ex.Message}");
        }
    }

    /// <summary>
    /// Inserta un badge en una fila por POSICIÓN (el orden de Children ES la
    /// posición en el StackPanel). Si quedan más de 4, el último fluye a la fila
    /// siguiente — la misma regla que usa la vista previa del drag.
    /// </summary>
    private void InsertIntoRow(StackPanel row, Border badge, int col, int rowIndex)
    {
        var items = row.Children.OfType<Border>().ToList();
        items.Insert(Math.Clamp(col, 0, items.Count), badge);

        if (items.Count <= MaxBadgesPerRow)
        {
            row.Children.Clear();
            for (int i = 0; i < items.Count; i++)
                row.Children.Add(items[i]);
            return;
        }

        // Overflow: el último badge de la fila fluye a la fila siguiente.
        var overflow = items[^1];
        items.RemoveAt(items.Count - 1);
        row.Children.Clear();
        for (int i = 0; i < items.Count; i++)
            row.Children.Add(items[i]);

        InsertIntoRow(EnsureRow(rowIndex + 1), overflow, 0, rowIndex + 1);
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
