using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
using WHPO_UI.Controls;

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
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _log = App.Services.GetRequiredService<ILoggingService>();
        _overlay = App.Services.GetRequiredService<OverlayService>();

        OverlayEnabledToggle.Toggled += OverlayEnabledToggle_Toggled;
        ShowHotkeyButton.Click += ShowHotkeyButton_Click;
        LockHotkeyButton.Click += LockHotkeyButton_Click;
        OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
        FontSizeSlider.ValueChanged += FontSizeSlider_ValueChanged;
        CornerComboBox.SelectionChanged += CornerComboBox_SelectionChanged;
        LayoutComboBox.SelectionChanged += LayoutComboBox_SelectionChanged;
        ResetOrderButton.Click += ResetOrderButton_Click;
        ShowOverlayOnlyWithGameToggle.Toggled += ShowOverlayOnlyWithGameToggle_Toggled;
        ShowGameTitleToggle.Toggled += ShowGameTitleToggle_Toggled;
        ResetStylesButton.Click += ResetStylesButton_Click;

        // Drag de badges: el pointer se captura en el PANEL (no en el badge). Al
        // reordenar se remueve/inserta el badge del árbol y, si el capturado fuera
        // el badge, WinUI dispara PointerCaptureLost (que limpiaba el estado) y el
        // Insert fallaba con ArgumentException.
        MetricBadgePanel.PointerMoved += OnBadgeMoved;
        MetricBadgePanel.PointerReleased += OnBadgeReleased;
        MetricBadgePanel.PointerCanceled += OnBadgeDragEnd;
        MetricBadgePanel.PointerCaptureLost += OnBadgeDragEnd;
        // Drag de CARDS de grupo (solo horizontal): mismos motivos que el drag de
        // badges — el pointer se captura en el panel, así que move/release/cancel
        // se escuchan acá. El press se conecta en cada card (BuildGroupCard).
        MetricBadgePanel.PointerMoved += OnGroupCardMoved;
        MetricBadgePanel.PointerReleased += OnGroupCardReleased;
        MetricBadgePanel.PointerCanceled += OnGroupCardCancelled;
        MetricBadgePanel.PointerCaptureLost += OnGroupCardCancelled;
        FpsColorButton.Click += (_, _) => ShowColorPicker(FpsColorButton, "overlay.colorFps", OverlayWindow.DefaultFamilyColor);
        CpuColorButton.Click += (_, _) => ShowColorPicker(CpuColorButton, "overlay.colorCpu", OverlayWindow.DefaultFamilyColor);
        GpuColorButton.Click += (_, _) => ShowColorPicker(GpuColorButton, "overlay.colorGpu", OverlayWindow.DefaultFamilyColor);
        RamColorButton.Click += (_, _) => ShowColorPicker(RamColorButton, "overlay.colorRam", OverlayWindow.DefaultFamilyColor);
        MetricColorButton.Click += (_, _) => ShowColorPicker(MetricColorButton, "overlay.colorMetrics", OverlayWindow.DefaultMetricColor);
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
            ShowOverlayOnlyWithGameToggle.IsOn = _settings.Get("overlay.onlyWhenGameDetected", true);
            ShowGameTitleToggle.IsOn = _settings.Get("overlay.showGameTitle", true);
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

            // Layout del overlay: vertical (panel) u horizontal (barra compacta).
            string layout = _settings.Get("overlay.layout", WHPO_UI.Overlay.OverlayWindow.LayoutVertical);
            foreach (var item in LayoutComboBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag as string == layout)
                {
                    LayoutComboBox.SelectedItem = item;
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

            UpdateColorButton(FpsColorButton, _settings.Get("overlay.colorFps", OverlayWindow.DefaultFamilyColor));
            UpdateColorButton(CpuColorButton, _settings.Get("overlay.colorCpu", OverlayWindow.DefaultFamilyColor));
            UpdateColorButton(GpuColorButton, _settings.Get("overlay.colorGpu", OverlayWindow.DefaultFamilyColor));
            UpdateColorButton(RamColorButton, _settings.Get("overlay.colorRam", OverlayWindow.DefaultFamilyColor));
            UpdateColorButton(MetricColorButton, _settings.Get("overlay.colorMetrics", OverlayWindow.DefaultMetricColor));

        }
        finally
        {
            _loading = false;
        }
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        UpdateMetricHint();
        bool enabled = OverlayEnabledToggle.IsOn;
        if (enabled)
        {
            var show = HotkeyLabel("overlay.showHotkeyVk", 0x58, "overlay.showHotkeyMods", ModCtrl | ModAlt);
            var lockH = HotkeyLabel("overlay.lockHotkeyVk", 0x43, "overlay.lockHotkeyMods", ModCtrl | ModAlt);
            // Modo de detección: automático o juego elegido manualmente.
            string targetExe = (_settings.Get("overlay.targetExe", "") ?? "").Trim();
            string modeText = string.Equals(_settings.Get("overlay.targetMode", "automatic"), "manual", StringComparison.OrdinalIgnoreCase) && targetExe.Length > 0
                ? string.Format(I18n.T("Esperando el juego seleccionado: {0}"), targetExe)
                : I18n.T("Selección automática: se buscará el juego activo.");
            OverlayStatusText.Text = string.Format(
                I18n.T("Activo. {0} muestra/oculta · {1} bloquea/desbloquea. {2}"),
                show, lockH, modeText);
        }
        else
        {
            OverlayStatusText.Text = I18n.T("Inactivo. Activá el interruptor para superponer las métricas sobre tus juegos.");
        }
    }

    /// <summary>Texto de ayuda del apartado de métricas según el layout: en
    /// horizontal avisa que lows y gráfico ms no existen en la barra.</summary>
    private void UpdateMetricHint()
    {
        if (_horizontalCards)
        {
            MetricHintTitle.Text = I18n.T("Orden de los datos en el overlay");
            MetricHintText.Text = I18n.T("La barra muestra una sola línea por grupo: FPS con máximo (↑) y mínimo (↓) de la sesión, CPU (uso, temperatura, GHz), GPU (uso, temperatura, VRAM usada/total) y RAM (usada/total). Arrastrá cada tarjeta por su encabezado hacia arriba o abajo para cambiar el orden de los grupos en la barra. 1% low, 0.1% low y el gráfico de ms no existen en horizontal, por eso están ocultos.");
        }
        else
        {
            MetricHintTitle.Text = I18n.T("Orden de los datos en el overlay");
            MetricHintText.Text = I18n.T("Cada grupo (CPU, GPU, RAM y FPS) es una tarjeta: arrastrá una métrica dentro de su tarjeta para cambiarla de línea o de posición (máximo 4 por línea), o hacé clic en ella para mostrar/ocultarla. Las métricas no se mueven entre tarjetas de otras familias.");
        }
    }

    private async void OverlayEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (OverlayEnabledToggle.IsOn)
        {
            // La activación se confirma después de elegir el modo. Así el popup
            // aparece antes de que el servicio empiece a seleccionar procesos.
            OverlayEnabledToggle.IsOn = false;
            await ShowOverlayTargetDialogAsync();
            return;
        }

        _overlay.Enabled = false;
        _settings.Save();
        UpdateStatusText();
    }

    private async Task ShowOverlayTargetDialogAsync()
    {
        if (XamlRoot == null) return;

        var contentPanel = new StackPanel
        {
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var content = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.Children.Add(contentPanel);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var autoButtonContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8
        };
        autoButtonContent.Children.Add(new TextBlock
        {
            Text = I18n.T("Detectar automáticamente"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var recommendedText = new TextBlock
        {
            Text = I18n.T("(Recomendado)"),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 175, 80)),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        autoButtonContent.Children.Add(recommendedText);
        var recommendationButton = TooltipStyles.CreateInfoButton(new TextBlock
        {
            Text = I18n.T("Se detectará con mejor eficacia el juego si se abre desde la biblioteca de juegos de WinForge."),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
        });
        autoButtonContent.Children.Add(recommendationButton);

        Button autoButton = new()
        {
            Content = autoButtonContent,
            MinHeight = 46,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        Button manualButton = new()
        {
            Content = I18n.T("Elegir juego manualmente"),
            MinHeight = 46,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        Button cancelButton = new()
        {
            Content = I18n.T("Cancelar"),
            MinHeight = 46,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        buttons.Children.Add(autoButton);
        buttons.Children.Add(manualButton);
        buttons.Children.Add(cancelButton);
        contentPanel.Children.Add(buttons);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("¿Cómo querés seleccionar el juego?"),
            Content = content,
            DefaultButton = ContentDialogButton.None,
            MinWidth = 0,
            MaxWidth = double.PositiveInfinity
        };
        var resultTcs = new TaskCompletionSource<ContentDialogResult>();
        autoButton.Click += (_, _) => { resultTcs.TrySetResult(ContentDialogResult.Primary); dialog.Hide(); };
        manualButton.Click += (_, _) => { resultTcs.TrySetResult(ContentDialogResult.Secondary); dialog.Hide(); };
        cancelButton.Click += (_, _) => { resultTcs.TrySetResult(ContentDialogResult.None); dialog.Hide(); };

        await dialog.ShowAsync();
        var result = resultTcs.Task.IsCompleted ? resultTcs.Task.Result : ContentDialogResult.None;
        if (result == ContentDialogResult.None)
        {
            OverlayEnabledToggle.IsOn = false;
            return;
        }

        if (result == ContentDialogResult.Primary)
        {
            _settings.Set("overlay.targetMode", "automatic");
            _settings.Remove("overlay.targetExe");
        }
        else
        {
            var path = PickGameExecutable();
            if (string.IsNullOrWhiteSpace(path))
            {
                OverlayEnabledToggle.IsOn = false;
                return;
            }
            _settings.Set("overlay.targetMode", "manual");
            _settings.Set("overlay.targetExe", Path.GetFileName(path));
        }

        _settings.Set("overlay.enabled", true);
        _settings.Save();
        _loading = true;
        try { OverlayEnabledToggle.IsOn = true; }
        finally { _loading = false; }
        _overlay.Enabled = true;
        UpdateStatusText();
    }

    private string? PickGameExecutable()
    {
        try
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = I18n.T("Seleccionar el exe del juego"),
                Filter = "Ejecutables (*.exe)|*.exe|Todos los archivos (*.*)|*.*",
                FilterIndex = 1,
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            var owner = System.Windows.Forms.NativeWindow.FromHandle(hwnd);
            return dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK
                ? dialog.FileName
                : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayPage: no se pudo seleccionar el ejecutable: {ex.Message}");
            return null;
        }
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

    private void ShowOverlayOnlyWithGameToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("overlay.onlyWhenGameDetected", ShowOverlayOnlyWithGameToggle.IsOn);
        _settings.Save();
        _overlay.ApplyWindowConfig();
    }

    private void ShowGameTitleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Set("overlay.showGameTitle", ShowGameTitleToggle.IsOn);
        _settings.Save();
        _overlay.ApplyWindowConfig();
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

    private void LayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (LayoutComboBox.SelectedItem is ComboBoxItem { Tag: string layout })
        {
            _settings.Set("overlay.layout", layout);
            _settings.Save();
            // Las cards se rearman según el layout (2×2 y una línea en horizontal).
            LoadMetricBadges();
            _overlay.ApplyWindowConfig();
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

    /// <summary>
    /// Restablece los ESTILOS del overlay a los valores de fábrica: colores de los
    /// títulos de grupo (FPS/CPU/GPU/RAM) en verde, métricas en blanco, opacidad
    /// al 85% y tamaño de letra al 100%. No toca ni el orden de los datos ni los
    /// atajos de teclado.
    /// </summary>
    private void ResetStylesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            _settings.Set("overlay.colorFps", OverlayWindow.DefaultFamilyColor);
            _settings.Set("overlay.colorCpu", OverlayWindow.DefaultFamilyColor);
            _settings.Set("overlay.colorGpu", OverlayWindow.DefaultFamilyColor);
            _settings.Set("overlay.colorRam", OverlayWindow.DefaultFamilyColor);
            _settings.Set("overlay.colorMetrics", OverlayWindow.DefaultMetricColor);
            _settings.Set("overlay.opacity", 0.85);
            _settings.Set("overlay.fontSize", 1.4);
            _settings.Save();

            OpacitySlider.Value = 0.85;
            FontSizeSlider.Value = 1.4;
            UpdateColorButton(FpsColorButton, OverlayWindow.DefaultFamilyColor);
            UpdateColorButton(CpuColorButton, OverlayWindow.DefaultFamilyColor);
            UpdateColorButton(GpuColorButton, OverlayWindow.DefaultFamilyColor);
            UpdateColorButton(RamColorButton, OverlayWindow.DefaultFamilyColor);
            UpdateColorButton(MetricColorButton, OverlayWindow.DefaultMetricColor);
            UpdateOpacityText();
            UpdateFontSizeText();

            _overlay.ApplyWindowConfig();
            Feedback.Success(OverlayStatusText, "Estilos restablecidos a los predeterminados.");
        }
        finally
        {
            _loading = false;
        }
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
    /// en su propia línea con la métrica core (la %) al inicio, y el FPS con los
    /// lows 1% / 0.1% en líneas separadas al final (el FPS encima de los lows).</summary>
    private void ResetOrderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = new HashSet<string>(_enabledMetrics, StringComparer.Ordinal);

        _enabledMetrics.Clear();

        // Orden por defecto: una fila por grupo, core primero; FPS y lows cada
        // uno en su propia línea, con el FPS encima de los lows. El gráfico de
        // frametime SIEMPRE está presente (su propia línea, dentro del bloque
        // FPS): sin esto, restaurar el orden LO ELIMINABA del overlay.
        _rows = new List<List<string>>
        {
            new() { "cpuUsage", "cpuMhz", "cpuTemp", "cpuWatts" },
            new() { "gpuUsage", "gpuMhz", "gpuTemp", "gpuWatts" },
            new() { "ramMb", "ramMhz" },
            new() { "fps" },
            new() { "low1" },
            new() { "low01" },
            new() { "latencyGraph" }
        };
        _rows = OverlayWindow.NormalizeRows(_rows);
        _enabledMetrics.UnionWith(enabled);
        RebuildPanel();
        SaveMetricOrder();
    }

    // Definición de cada badge: id (se persiste en "overlay.metricRows") y etiqueta.
    // Las etiquetas llevan el prefijo de la familia ("CPU %", "RAM MHz"...) y se
    // traducen con I18n.T en AddBadge: sin prefijo, badges como "%" o "MHz"
    // quedaban ambiguos ("solo aparece %"). En modo horizontal, las placas de
    // los badges se REESCRIBEN con HorizontalBadgeDefs: métricas que la barra
    // no dibuja (1% low, 0.1% low, gráfico ms) se crean OCULTAS, y el resto
    // lleva nombres de lo que la barra muestra (máx/mín, GHz, RAM usada/total).
    private static readonly (string Id, string Label)[] MetricBadgeDefs =
    {
        ("fps", "FPS"),
        ("low1", "1% low"),
        ("low01", "0.1% low"),
        ("latencyGraph", "Gráfico ms"),
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

    /// <summary>Etiquetas de badges SOLO en modo horizontal: reflejan lo que la
    /// barra dibuja de verdad — FPS con el máximo (↑) y el mínimo (↓) de la
    /// sesión, MHz de CPU como GHz, y el uso de RAM como usada/total. Si un id
    /// no está acá, el badge se crea OCULTO en horizontal (su switch no tiene
    /// efecto: la barra no lo dibuja — caso de 1% low, 0.1% low y gráfico ms).</summary>
    private static readonly (string Id, string Label)[] HorizontalBadgeDefs =
    {
        ("fps", "FPS ↑máx ↓mín"),
        ("cpuUsage", "CPU %"),
        ("cpuMhz", "CPU GHz"),
        ("cpuTemp", "CPU °C"),
        ("cpuWatts", "CPU W"),
        ("gpuUsage", "GPU %"),
        ("gpuTemp", "GPU °C"),
        ("ramMb", "RAM usada/total"),
        ("ramMhz", "RAM MHz")
    };

    /// <summary>Etiqueta visible de un badge según el layout: en horizontal, la
    /// placa reescrita de HorizontalBadgeDefs (null = badge sin efecto en la
    /// barra); en vertical, la de MetricBadgeDefs.</summary>
    private string BadgeLabelFor(string id, string fallback)
        => _horizontalCards
            ? HorizontalBadgeDefs.FirstOrDefault(d => d.Id == id).Label
            : fallback;

    private readonly List<(string Id, Border Badge, ToggleSwitch Toggle)> _metricBadges = new();
    private readonly HashSet<string> _enabledMetrics = new(StringComparer.Ordinal);

    // Modelo lógico de filas de badges (cada fila = una línea del overlay). El
    // panel visual se reconstruye desde acá y NormalizeRows garantiza la
    // invariante de grupos (una familia por fila, core primero, cpu→gpu→ram→fps).
    private List<List<string>> _rows = new();

    private Border? _dragBadge;
    private Border? _dragCard;   // card de la familia del badge arrastrado
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

    // Indicador de LÍNEA NUEVA: barra fina horizontal que aparece en el hueco
    // entre filas cuando el destino del drag es crear una línea ahí (no hay
    // skeleton porque la fila todavía no existe).
    private Border? _dropLineIndicator;

    // ===== Drag de CARDS de grupo (solo horizontal) =====
    // En horizontal no se arrastran métricas sueltas: la unidad de orden es el
    // GRUPO (FPS/CPU/GPU/RAM) y su posición en la barra. Arrastrar una card por
    // su encabezado reordena los grupos (arriba/abajo) y el orden queda
    // persistido en "overlay.hGroupOrder" para que la barra lo refleje.
    private Border? _groupDragCard;
    private bool _groupDragActive;
    private bool _groupDragMoved;
    private double _groupDragStartY;
    // Línea de inserción entre cards durante el drag de grupo (feedback visual
    // de dónde va a caer la card al soltar).
    private Border? _groupDropLine;

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
        // Modo de las cards según el layout del overlay: con la barra horizontal,
        // cada card pone en UNA SOLA línea solo lo que la barra dibuja de verdad
        // (los badges sin efecto —lows, gráfico ms— se OCULTAN, sin tocar lo
        // guardado: _rows/metricEnabled quedan intactos para volver a vertical).
        _horizontalCards = string.Equals(
            _settings.Get("overlay.layout", WHPO_UI.Overlay.OverlayWindow.LayoutVertical),
            WHPO_UI.Overlay.OverlayWindow.LayoutHorizontal, StringComparison.OrdinalIgnoreCase);

        // El texto de ayuda cambia con el layout: en horizontal, la barra NO
        // dibuja lows ni gráfico ms (los switches quedan ocultos) y los nombres
        // reflejan lo que la barra muestra (máx/mín, GHz, VRAM, usada/total).
        UpdateMetricHint();

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
            // y lows 1% / 0.1% en líneas separadas por defecto (FPS encima de los lows).
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

        // Solo partir filas que pasen de 4 (ChunkRows). NO NormalizeRows:
        // respetar el orden exacto guardado por el usuario.
        rows = rows.SelectMany(ChunkRows).ToList();

        // Completar badges que falten: cada uno en su propia fila al final.
        foreach (var def in MetricBadgeDefs)
        {
            if (rows.Any(r => r.Contains(def.Id))) continue;
            rows.Add(new List<string> { def.Id });
        }
        // Solo ChunkRows de nuevo por si la fila nueva pasó de 4.
        rows = rows.SelectMany(ChunkRows).ToList();

        _rows = GroupContiguousRows(rows);
        _enabledMetrics.UnionWith(enabled);
        RebuildPanel();
        if (migrated) SaveMetricOrder();
        return migrated;
    }

    /// <summary>
    /// Re-agrupa filas contiguas de la MISMA familia (para que las métricas de
    /// CPU/GPU/RAM/FPS queden juntas, sin filas intercaladas de otra familia, y
    /// en el orden canónico cpu → gpu → ram → fps) y encola las filas sobrantes
    /// de una familia al final del bloque de esa familia. NO reordena métricas
    /// dentro de cada fila ni elimina nada: es solo cosmética de agrupamiento.
    /// </summary>
    private static List<List<string>> GroupContiguousRows(List<List<string>> rows)
    {
        var order = new[] { "cpu", "gpu", "ram", "fps" };
        var result = new List<List<string>>();
        var blocks = new Dictionary<string, List<List<string>>>();
        foreach (var g in order) blocks[g] = new List<List<string>>();
        var tail = new List<List<string>>(); // filas de familias ya cerradas

        foreach (var row in rows)
        {
            if (row.Count == 0) continue;
            var g = OverlayWindow.GroupOf(row[0]);
            if (g.Length > 0 && blocks.ContainsKey(g) && blocks[g].Count > 0)
            {
                // La familia ya tenía bloque y esta fila es de esa familia →
                // cualquier familia intercalada que estuviera “abierta” se cierra
                // (sus filas pendientes van a la cola y se re-acoplan al final).
                foreach (var other in order.Where(o => o != g && blocks[o].Count > 0))
                {
                    tail.AddRange(blocks[other]);
                    blocks[other].Clear();
                }
            }
            if (g.Length > 0 && blocks.ContainsKey(g)) blocks[g].Add(row);
            else tail.Add(row);
        }

        foreach (var g in order)
        {
            result.AddRange(blocks[g]);
            result.AddRange(tail.Where(r => OverlayWindow.GroupOf(r[0]) == g));
        }
        return result;
    }

    /// <summary>Máximo de badges por línea (la grilla de la config es fija).</summary>
    private const int MaxBadgesPerRow = 4;

    // Los lows/FPS en filas propias ahora se resuelven con OverlayWindow.SplitFpsAndLows
    // (compartido con el render del overlay), por eso ya no hay helper local.

    /// <summary>Ancho fijo de cada badge en modo vertical (la grilla es estable, no
    /// depende del ancho de la ventana). En modo horizontal los badges se auto-
    /// dimensionan (Width = NaN): con ancho fijo el texto ("CPU MHz", "Gráfico ms")
    /// se recortaba contra el switch.</summary>
    private const double BadgeWidthVertical = 150;

    // Layout del overlay seleccionado en el combo: con la barra horizontal, las
    // cards de grupos se muestran en fila (grid 2×2) y cada card pone TODAS sus
    // métricas en una sola línea (la barra ignora las filas múltiples).
    private bool _horizontalCards;

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
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = "MetricRow"
        };
    }

    /// <summary>Cards de familia del panel (contenedores con Tag "MetricGroupCard"
    /// en modo vertical y "MetricGroupCardH" en modo horizontal 2×2).</summary>
    private IEnumerable<Border> GroupCards()
        => MetricBadgePanel.Children.OfType<Border>()
            .Where(b => b.Tag as string is "MetricGroupCard" or "MetricGroupCardH");

    /// <summary>Filas de badges de una card (StackPanels con Tag "MetricRow").</summary>
    private static List<StackPanel> CardRows(Border card)
        => (card.Child as StackPanel)?.Children.OfType<StackPanel>()
               .Where(sp => sp.Tag as string == "MetricRow").ToList()
           ?? new List<StackPanel>();

    /// <summary>Devuelve la fila n contando TODAS las filas de TODAS las cards
    /// (n = índice global entre filas), o null si no existe.</summary>
    private StackPanel? RowAt(int rowIndex)
    {
        int seen = 0;
        foreach (var card in GroupCards())
            foreach (var sp in CardRows(card))
            {
                if (seen == rowIndex) return sp;
                seen++;
            }
        return null;
    }

    /// <summary>Garantiza que exista la fila global rowIndex: si falta, la crea
    /// dentro de la card del grupo arrastrado (el overflow de la vista previa
    /// nunca agrega filas fuera de la card de la familia).</summary>
    private StackPanel EnsureRow(int rowIndex)
    {
        var existing = RowAt(rowIndex);
        if (existing != null) return existing;
        var card = OwningCard(_dragBadge) ?? _dragCard ?? GroupCards().LastOrDefault();
        if (card?.Child is not StackPanel body)
            throw new InvalidOperationException("OverlayPage: card de métricas sin cuerpo");
        var row = CreateRowPanel();
        body.Children.Add(row);
        return row;
    }

    /// <summary>Card que contiene a un elemento (sube por el árbol visual hasta
    /// encontrar el contenedor con Tag "MetricGroupCard").</summary>
    private static Border? OwningCard(Microsoft.UI.Xaml.DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Border b && b.Tag as string == "MetricGroupCard") return b;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    /// <summary>
    /// Reconstruye el panel de badges desde el modelo lógico _rows (cada fila =
    /// una línea del overlay), organizado en CARDS por familia (CPU, GPU, RAM y
    /// FPS): cada card es un contenedor con título del color de la familia y sus
    /// badges adentro. El drag solo funciona DENTRO de la card de la familia del
    /// badge (no tiene sentido mover "CPU MHz" a la card de GPU). En modo
    /// horizontal, los badges sin efecto en la barra (lows, gráfico ms) se crean
    /// OCULTOS pero SIGUEN en el árbol: al soltar el drag o al volver a vertical
    /// no se pierde nada (ComputeRows los sigue viendo).
    /// </summary>
    private void RebuildPanel()
    {
        _metricBadges.Clear();
        MetricBadgePanel.Children.Clear();
        var byGroup = new Dictionary<string, List<List<string>>>();
        foreach (var rowIds in _rows)
        {
            if (rowIds.Count == 0) continue;
            var g = OverlayWindow.GroupOf(rowIds[0]);
            if (!byGroup.TryGetValue(g, out var block)) byGroup[g] = block = new List<List<string>>();
            block.Add(rowIds);
        }
        // Cards APILADAS a lo ancho completo en ambos modos (en la grilla 2×2 de
        // antes no entraban los 4 badges de una familia y el texto se recortaba).
        // La diferencia está DENTRO de cada card (BuildGroupCard): en modo
        // horizontal, el grupo se muestra como UNA sola línea de métricas — una
        // tira horizontal, igual a como lo pinta la barra del overlay.
        var colors = new[] { ("cpu", "colorCpu"), ("gpu", "colorGpu"), ("ram", "colorRam"), ("fps", "colorFps") };
        if (_horizontalCards)
        {
            // En horizontal el orden de las CARDS es el orden de los grupos en la
            // barra (drag a nivel card): se arman según "overlay.hGroupOrder".
            var byColor = colors.ToDictionary(c => c.Item1, c => c.Item2);
            foreach (var group in OverlayWindow.NormalizeGroupOrder(
                _settings.Get("overlay.hGroupOrder", new List<string>())))
            {
                if (!byGroup.TryGetValue(group, out var block) || block.Count == 0) continue;
                MetricBadgePanel.Children.Add(BuildGroupCard(group, byColor[group], block));
            }
        }
        else
        {
            foreach (var (group, colorKey) in colors)
            {
                if (!byGroup.TryGetValue(group, out var block) || block.Count == 0) continue;
                MetricBadgePanel.Children.Add(BuildGroupCard(group, colorKey, block));
            }
        }
        UpdateBadgeVisuals();
    }

    /// <summary>Color de acento de una familia: el mismo color que el usuario
    /// configuró para esa métrica (misma fuente que pinta el overlay).</summary>
    private Windows.UI.Color GroupAccentColor(string colorKey)
    {
        try { return ParseHex(_settings.Get("overlay." + colorKey, OverlayWindow.DefaultFamilyColor)); }
        catch { return ParseHex(OverlayWindow.DefaultFamilyColor); }
    }

    /// <summary>
    /// Card de una familia: contenedor con borde del color de la familia, header
    /// (punto + título + contador de métricas activas) y las filas de badges
    /// del grupo adentro.
    /// </summary>
    /// <param name="reuse">Diccionario opcional id→badge para REUSAR instancias
    /// existentes (RestoreSnapshot): crear badges nuevos acá duplicaba entradas
    /// en _metricBadges y huérfanaba los originales (panel roto durante drag).</param>
    private Border BuildGroupCard(string group, string colorKey, List<List<string>> block,
        Dictionary<string, Border>? reuse = null)
    {
        var accent = GroupAccentColor(colorKey);
        var title = I18n.T(group switch
        {
            "cpu" => "CPU",
            "gpu" => "GPU",
            "ram" => "RAM",
            _ => "FPS"
        });
        int total = block.Sum(r => r.Count);
        int on = block.Sum(r => r.Count(id => _enabledMetrics.Contains(id)));

        var header = BuildGroupHeaderPanel(title, accent, on, total);

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(header);

        // En modo horizontal: UNA SOLA línea con lo que la barra dibuja de
        // verdad — las métricas sin efecto (lows, gráfico ms) se crean igual
        // pero OCULTAS (Visibility.Collapsed), así _rows y lo guardado nunca
        // pierden nada. Las visibles llevan la placa de HorizontalBadgeDefs.
        var hIds = _horizontalCards
            ? block.SelectMany(r => r).ToList()
            : null;
        var lines = hIds != null
            ? new List<List<string>> { hIds }
            : block;

            foreach (var rowIds in lines)
            {
                var row = CreateRowPanel();
                bool allHidden = true;
                foreach (var id in rowIds.Take(MaxBadgesPerRow))
                {
                    var def = MetricBadgeDefs.First(d => d.Id == id);
                    var hDef = _horizontalCards
                        ? HorizontalBadgeDefs.FirstOrDefault(d => d.Id == id)
                        : default;
                    // En horizontal, la card es UNA sola línea con lo que la barra
                    // dibuja: los badges sin efecto (lows, gráfico ms) se crean
                    // igual pero van OCULTOS (Visibility.Collapsed) — así el drag,
                    // ComputeRows y el volver a vertical nunca los pierden.
                    bool hidden = _horizontalCards && hDef.Label == null;
                    Border badge;
                    if (reuse != null && reuse.TryGetValue(id, out var existing))
                    {
                        // La placa del badge puede cambiar con el layout (ej. CPU MHz
                        // → CPU GHz en horizontal): refrescarla al reusar.
                        UpdateBadgeLabel(existing, BadgeLabelFor(id, def.Label));
                        badge = existing;
                    }
                    else
                    {
                        badge = AddBadge(def.Id, BadgeLabelFor(id, def.Label), _enabledMetrics.Contains(def.Id));
                    }
                    badge.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
                    if (!hidden) allHidden = false;
                    row.Children.Add(badge);
                }
                if (allHidden) row.Visibility = Visibility.Collapsed;
                body.Children.Add(row);
            }

        var card = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = _horizontalCards ? new Thickness(0) : new Thickness(0, 0, 0, 10),
            Background = ThemeBrushes.Get("CardBackgroundBrush"),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(70, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Tag = _horizontalCards ? "MetricGroupCardH" : "MetricGroupCard",
            Child = body
        };
        if (_horizontalCards)
        {
            // En horizontal la CARD es la unidad de orden: arrastrarla por el
            // encabezado (o los márgenes) reordena los grupos en la barra. Los
            // badges manejan sus propios presses (toggle, sin drag), así que el
            // drag de card entra por el header y los bordes de la card.
            card.PointerPressed += OnGroupCardPressed;
            ToolTipService.SetToolTip(card,
                I18n.T("Arrastrá la tarjeta por su encabezado para cambiar el orden de los grupos en la barra."));
        }
        return card;
    }

    /// <summary>Header de una card de familia: punto de color + título del color
    /// de la familia + contador de métricas activas ("2/4").</summary>
    private static StackPanel BuildGroupHeaderPanel(string title, Windows.UI.Color accent, int on, int total)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(2, 0, 0, 2) };
        header.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{on}/{total}",
            FontSize = 11,
            Foreground = ThemeBrushes.Get("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0)
        });
        return header;
    }

    private Border AddBadge(string id, string label, bool enabled)
    {
        var text = new TextBlock
        {
            Text = I18n.T(label),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            // Red de seguridad: si la ventana es angosta, el texto se corta con
            // elipsis en vez de desbordar la card (con auto-width nunca pasa en
            // uso normal).
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        text.Tag = "BadgeLabel";
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

        bool isCore = OverlayWindow.IsCoreMetric(id);
        // FIJAS (con candado): la métrica core de cada línea y además el gráfico
        // ms (define la fila del grupo FPS y tampoco se puede mover).
        bool hasLock = isCore || id == OverlayWindow.FrametimeGraphId;

        // Contenido en grilla: etiqueta a la izquierda, [candado si fija], switch a la derecha.
        var content = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (hasLock)
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // candado
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // switch
        Grid.SetColumn(text, 0);
        Grid.SetColumn(toggle, hasLock ? 2 : 1);
        content.Children.Add(text);
        content.Children.Add(toggle);

        if (hasLock)
        {
            var lockIcon = new FontIcon
            {
                Glyph = "\uE1F6", // Lock icon
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 180, 180, 180)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            Grid.SetColumn(lockIcon, 1);
            ToolTipService.SetToolTip(lockIcon, I18n.T("No se puede mover"));
            content.Children.Add(lockIcon);
        }

        var badge = new Border
        {
            // Vertical: ancho fijo (grilla pareja). Horizontal: auto para que el
            // texto completo ("CPU MHz", "Gráfico ms") entre sin recortes.
            Width = _horizontalCards ? double.NaN : BadgeWidthVertical,
            Padding = _horizontalCards ? new Thickness(10, 4, 8, 4) : new Thickness(12, 4, 10, 4),
            Background = ThemeBrushes.Get("CardHoverBrush"),
            CornerRadius = new CornerRadius(14),
            Child = content
        };
        if (isCore || id == OverlayWindow.FrametimeGraphId)
            ToolTipService.SetToolTip(badge, I18n.T("No se puede mover"));
        badge.PointerPressed += OnBadgePressed;
        _metricBadges.Add((id, badge, toggle));
        return badge;
    }

    private string BadgeId(Border badge)
        => _metricBadges.First(b => ReferenceEquals(b.Badge, badge)).Id;

    /// <summary>Refresca la placa de un badge existente (cambio Vertical ↔
    /// Horizontal, donde los nombres se reescriben). Localiza el TextBlock de
    /// la etiqueta por su Tag ("BadgeLabel") y lo re-traduce.</summary>
    private static void UpdateBadgeLabel(Border badge, string label)
    {
        var text = (badge.Child as Grid)?.Children
            .OfType<TextBlock>()
            .FirstOrDefault(t => t.Tag as string == "BadgeLabel");
        if (text != null) text.Text = I18n.T(label);
    }

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
        var disabledBg = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        // Acento cálido MUY SUTIL solo en el BORDE de badges core (sin fondo amarillo)
        var coreAccent = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 245, 195, 110));

        foreach (var (id, badge, _) in _metricBadges)
        {
            // El acento de borde marca las métricas FIJAS: las core (CPU %, GPU %,
            // RAM MB) y también el gráfico ms (fijo de la familia FPS).
            bool isCore = OverlayWindow.IsCoreMetric(id) || id == OverlayWindow.FrametimeGraphId;
            bool enabled = _enabledMetrics.Contains(id);
            badge.Opacity = enabled ? 1.0 : 0.45;
            badge.BorderThickness = new Thickness(1);

            if (enabled)
            {
                // Fondo normal para TODOS; solo el BORDE tiene acento cálido sutil en core
                badge.Background = ThemeBrushes.Get("CardHoverBrush");
                badge.BorderBrush = isCore ? coreAccent : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            }
            else
            {
                badge.Background = disabledBg;
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
        // Guardar el orden EXACTO del usuario (sin NormalizeRows que reordena por grupos).
        // Solo asegurar máx 4 por fila con ChunkRows por si acaso.
        rows = rows.SelectMany(ChunkRows).ToList();
        _rows = rows;
        _settings.Set("overlay.metricRows", rows);
        _settings.Set("overlay.metricOrder", rows.SelectMany(r => r).ToList());
        _settings.Set("overlay.metricEnabled", _enabledMetrics.ToList());
        _settings.Save();
        _overlay.ApplyWindowConfig();
    }

    /// <summary>
    /// Calcula las filas actuales leyendo la estructura real: cada card de
    /// familia contiene sus filas de badges; el resultado se aplana en el orden
    /// de las cards (cada fila = una línea del overlay).
    /// </summary>
    private List<List<string>> ComputeRows()
    {
        var rows = new List<List<string>>();
        foreach (var card in GroupCards())
        {
            foreach (var row in CardRows(card))
            {
                var ids = row.Children.OfType<Border>().Select(BadgeId).ToList();
                if (ids.Count > 0) rows.Add(ids);
            }
        }
        return rows;
    }

    // ===== Drag para reordenar (mantener el clic y arrastrar) =====

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
        // En horizontal NO se arrastran métricas sueltas: el orden de la barra lo
        // definen las cards de grupo (drag a nivel card, por el encabezado).
        // Cualquier badge en horizontal se comporta como los fijos: clic = toggle.
        if (_horizontalCards
            || OverlayWindow.IsCoreMetric(BadgeId(_dragBadge))
            || BadgeId(_dragBadge) == OverlayWindow.FrametimeGraphId)
        {
            // Métricas FIJAS (no arrastrables): las core (CPU %, GPU %, RAM MB,
            // FPS) que definen su línea, y el gráfico ms. El clic corto sigue
            // alternando mostrar/ocultar.
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
        _dragCard = OwningCard(_dragBadge);
        var start = e.GetCurrentPoint(MetricBadgePanel).Position;
        _dragStartX = start.X;
        _dragStartY = start.Y;
        // El badge "sigue" al puntero con un RenderTransform. La vista previa del
        // drop (skeleton) se arma sobre un snapshot del estado original para poder
        // restaurarlo al soltar; el reorden real se aplica de una sola vez.
        _dragSnapshot = ComputeRows();
        Canvas.SetZIndex(_dragBadge, 100);
        _dropSkeleton = new Border
        {        // En un StackPanel (no un Grid) el ancho NO viene de la celda: hay que
        // fijarlo, o el skeleton colapsa a 0px y no se ve ni empuja. Se usa el
        // ancho REAL del badge arrastrado (en modo horizontal los badges son
        // auto-dimensionados y cada uno mide distinto).
            Width = _dragBadge.ActualWidth > 0 ? _dragBadge.ActualWidth : BadgeWidthVertical,
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
        if (_dragTargetNewRow)
        {
            // Destino = LÍNEA NUEVA: sin skeleton (la fila no existe todavía);
            // mostrar la barra de inserción al final de la card de la familia.
            RemoveDropSkeleton();
            ShowDropSlotIndicator();
        }
        else
        {
            RemoveDropSlotIndicator();
            UpdateDropSkeleton(_dragTargetRow, _dragTargetCol, _dragTargetNewRow);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Posición destino del badge arrastrado en 2D: (fila, columna) donde cae el
    /// puntero. La métrica SOLO puede soltarse en filas de SU PROPIO grupo
    /// (cpu/gpu/ram/fps) O en un HUECO ENTRE FILAS del mismo grupo (para crear
    /// una nueva línea). Si el puntero cae sobre filas de otro grupo, se ajusta
    /// a la fila/hueco más cercano del grupo. Si cae claramente debajo del panel,
    /// devuelve una fila NUEVA al final. En la fila core (la %) la columna mínima
    /// es 1: no se puede insertar antes del core.
    /// </summary>
    private (int Row, int Col, bool NewRow) ComputeInsertSlot(double x, double y)
    {
        var group = OverlayWindow.GroupOf(BadgeId(_dragBadge!));

        // Detectar SOLO las filas de la CARD del grupo arrastrado: los badges de
        // una familia no se sueltan sobre las cards de otras familias (el drop
        // se calcula dentro de su propia card). "Index" = índice GLOBAL de fila
        // (el mismo que usa ApplyReorder sobre el modelo aplanado por cards).
        var card = OwningCard(_dragBadge) ?? _dragCard;
        var rows = new List<(double Y, double H, int Index)>();
        if (card != null)
        {
            int globalIndex = 0;
            foreach (var c in GroupCards())
            {
                foreach (var sp in CardRows(c))
                {
                    if (c == card)
                    {
                        var origin = sp.TransformToVisual(MetricBadgePanel)
                            .TransformPoint(new Windows.Foundation.Point(0, 0));
                        rows.Add((origin.Y, sp.ActualHeight, globalIndex));
                    }
                    globalIndex++;
                }
            }
        }

        // Todas las filas listadas son del grupo (la card ES el grupo).
        var groupRows = rows;

        // Sin filas del grupo (no debería pasar: la card existe): fila nueva.
        if (groupRows.Count == 0)
            return (-1, 0, true);

        // Calcular huecos entre filas del mismo grupo: (Y superior, Y inferior, índice fila ANTES del hueco)
        var gaps = new List<(double Top, double Bottom, int AfterRow)>();
        for (int i = 0; i < groupRows.Count - 1; i++)
        {
            double gapTop = groupRows[i].Y + groupRows[i].H;
            double gapBottom = groupRows[i + 1].Y;
            if (gapBottom > gapTop + 2) // hueco real > 2px
                gaps.Add((gapTop, gapBottom, groupRows[i].Index));
        }
        // Hueco después de la última fila del grupo
        if (rows.Count > 0)
            gaps.Add((groupRows[^1].Y + groupRows[^1].H, double.MaxValue, groupRows[^1].Index));

        // 1) ¿Puntero está en un hueco del grupo?
        foreach (var gap in gaps)
        {
            if (y >= gap.Top && y <= gap.Bottom)
            {
                // Hueco detectado: crear fila nueva DESPUÉS de gap.AfterRow
                return (gap.AfterRow + 1, 0, true);
            }
        }

        // 2) Puntero sobre una fila del grupo: insertar en esa fila
        int bestRow = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < groupRows.Count; i++)
        {
            var row = groupRows[i];
            double dist = y < row.Y ? row.Y - y : (y > row.Y + row.H ? y - (row.Y + row.H) : 0);
            if (dist < bestDist) { bestDist = dist; bestRow = row.Index; }
        }

        // 3) Puntero claramente debajo de todo → al final
        bool newRow = false;
        if (bestRow < 0)
            newRow = true;
        else if (y > rows[^1].Y + rows[^1].H + 6)
            newRow = true;

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
            // Primera fila del grupo (la del core, la %): no insertar antes del core.
            var cardRows = card != null ? CardRows(card) : new List<StackPanel>();
            if (cardRows.Count > 0 && ReferenceEquals(targetRow, cardRows[0])) col = Math.Max(col, 1);
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
        RemoveDropSlotIndicator();
        _dragActive = false;
        _dragBadge = null;
        _dragCard = null;
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
        RemoveDropSlotIndicator();
        _dragActive = false;
        _dragBadge = null;
        _dragCard = null;
        _dropSkeleton = null;
    }

    // ===== Drag de CARDS de grupo (solo horizontal) =====

    /// <summary>Grupo (familia) de una card: el grupo del primer badge adentro
    /// (en horizontal todos los badges de la card son de la misma familia).</summary>
    private string CardGroup(Border card)
    {
        var first = CardRows(card).SelectMany(r => r.Children.OfType<Border>()).FirstOrDefault();
        return first != null ? OverlayWindow.GroupOf(BadgeId(first)) : "cpu";
    }

    /// <summary>Press sobre una card en horizontal: inicia el drag de grupo.
    /// Los badges manejan (y marcan como manejado) sus propios presses, así que
    /// este handler entra por el encabezado y los márgenes de la card.</summary>
    private void OnGroupCardPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_horizontalCards) return;
        _groupDragCard = (Border)sender;
        _groupDragActive = true;
        _groupDragMoved = false;
        _groupDragStartY = e.GetCurrentPoint(MetricBadgePanel).Position.Y;
        MetricBadgePanel.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnGroupCardMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_groupDragActive || _groupDragCard == null) return;
        var y = e.GetCurrentPoint(MetricBadgePanel).Position.Y;
        if (!_groupDragMoved)
        {
            if (Math.Abs(y - _groupDragStartY) < 8) return;
            _groupDragMoved = true;
            Canvas.SetZIndex(_groupDragCard, 100);
        }
        // Feedback: la card sigue al puntero (solo vertical) y la línea de
        // inserción marca entre qué cards va a caer.
        _groupDragCard.RenderTransform = new TranslateTransform { Y = y - _groupDragStartY };
        ShowGroupDropLine(ComputeGroupInsertIndex(y, _groupDragCard));
        e.Handled = true;
    }

    private void OnGroupCardReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_groupDragActive || _groupDragCard == null) { e.Handled = true; return; }
        var card = _groupDragCard;
        bool moved = _groupDragMoved;
        FinishGroupDrag(card);
        if (moved)
        {
            // Reorden real: remover la card e insertarla en el índice destino
            // (ajustado por la remoción). El panel en horizontal contiene SOLO
            // cards, así que el índice de children = posición entre grupos.
            int target = ComputeGroupInsertIndex(e.GetCurrentPoint(MetricBadgePanel).Position.Y, card);
            int current = MetricBadgePanel.Children.IndexOf(card);
            if (current >= 0) MetricBadgePanel.Children.RemoveAt(current);
            if (target > current) target--;
            target = Math.Clamp(target, 0, MetricBadgePanel.Children.Count);
            MetricBadgePanel.Children.Insert(target, card);
            SaveGroupOrder();
        }
        e.Handled = true;
    }

    /// <summary>Cancelación (Esc / pérdida de captura): limpiar sin reordenar.
    /// Tras un release exitoso _groupDragCard ya es null, así que el
    /// PointerCaptureLost que dispara ReleasePointerCapture no hace nada acá.</summary>
    private void OnGroupCardCancelled(object sender, PointerRoutedEventArgs e)
    {
        if (_groupDragCard != null) FinishGroupDrag(_groupDragCard);
        e.Handled = true;
    }

    /// <summary>Limpia el estado visual del drag de grupo (transform, z, línea).</summary>
    private void FinishGroupDrag(Border card)
    {
        _groupDragActive = false;
        _groupDragCard = null;
        card.RenderTransform = null;
        Canvas.SetZIndex(card, 0);
        RemoveGroupDropLine();
    }

    /// <summary>Índice de inserción de la card arrastrada: cantidad de cards
    /// (excluida la arrastrada) cuyo centro quedó por encima del puntero.</summary>
    private int ComputeGroupInsertIndex(double y, Border exclude)
    {
        int idx = 0;
        foreach (var c in GroupCards())
        {
            if (ReferenceEquals(c, exclude)) continue;
            var top = c.TransformToVisual(MetricBadgePanel)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (y > top + c.ActualHeight / 2) idx++;
        }
        return idx;
    }

    /// <summary>Muestra la línea de inserción entre cards en la posición dada
    /// (el panel en horizontal contiene SOLO cards: índice = posición directa).</summary>
    private void ShowGroupDropLine(int index)
    {
        RemoveGroupDropLine();
        _groupDropLine ??= new Border
        {
            Height = 3,
            Margin = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 120, 220, 140)),
            CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false
        };
        MetricBadgePanel.Children.Insert(
            Math.Clamp(index, 0, MetricBadgePanel.Children.Count), _groupDropLine);
    }

    private void RemoveGroupDropLine()
    {
        if (_groupDropLine == null) return;
        MetricBadgePanel.Children.Remove(_groupDropLine);
    }

    /// <summary>Persiste el orden de grupos de la barra ("overlay.hGroupOrder")
    /// leyendo el orden ACTUAL de las cards del panel, y lo aplica al overlay
    /// en vivo (ApplyWindowConfig re-lee la config con el orden nuevo).</summary>
    private void SaveGroupOrder()
    {
        var order = GroupCards().Select(CardGroup).ToList();
        _settings.Set("overlay.hGroupOrder", OverlayWindow.NormalizeGroupOrder(order));
        _settings.Save();
        _overlay.ApplyWindowConfig();
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
            // no hay skeleton: se usa la barra de inserción (indicador).
            RemoveDropSkeleton();
            if (newRow || row < 0) return;
            PreviewInsert(EnsureRow(row), _dropSkeleton, col);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"OverlayPage: falló la vista previa del drop: {ex.Message}");
            RestoreSnapshot();
            _dragCard = null; // la card vieja quedó fuera del árbol: usar el árbol vivo
        }
    }

    /// <summary>Quita el indicador de línea nueva del árbol (si está visible).</summary>
    private void RemoveDropSlotIndicator()
    {
        if (_dropLineIndicator == null) return;
        // Puede estar en el cuerpo de una card (no en el panel raíz).
        if (_dropLineIndicator.Parent is Panel p) p.Children.Remove(_dropLineIndicator);
        else MetricBadgePanel.Children.Remove(_dropLineIndicator);
        _dropLineIndicator = null;
    }

    /// <summary>
    /// Muestra una BARRA FINA horizontal en el hueco donde se crearía la línea
    /// nueva (feedback visual del drag cuando el destino es una fila que todavía
    /// no existe). slotIndex = cantidad de filas por encima del hueco; la posición
    /// física en el panel [fila, sep, fila, sep, ...] es 2*slotIndex.
    /// </summary>
    private void ShowDropSlotIndicator()
    {
        RemoveDropSlotIndicator();
        _dropLineIndicator ??= new Border
        {
            Height = 4,
            Margin = new Thickness(2, 3, 2, 3),
            CornerRadius = new CornerRadius(2),
            Background = ThemeBrushes.Get("AccentBrush"),
            Opacity = 0.85,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        // La barra de inserción vive SIEMPRE en la card de la familia del badge
        // arrastrado: los badges no pueden crear líneas fuera de su grupo.
        var card = OwningCard(_dragBadge) ?? _dragCard;
        if (card?.Child is not StackPanel body) return;
        body.Children.Add(_dropLineIndicator);
    }

    /// <summary>
    /// Inserta un elemento (el skeleton) en una fila por columna, reasignando las
    /// columnas de esa fila; si se pasa de 4, el excedente fluye a la fila
    /// siguiente (misma regla que al soltar). Mutación local, no toca el resto.
    /// </summary>
    private void PreviewInsert(StackPanel row, Border el, int col)
    {
        // ¿Es la MISMA fila donde está el badge arrastrado?
        if (row.Children.Contains(_dragBadge))
        {
            // MISMA fila: NO se puede reconstruir (al sacar el badge del árbol
            // perdería su padre y desaparecería durante el drag). Se inserta el
            // skeleton DIRECTO entre los hijos, sin tocar al badge arrastrado:
            // el StackPanel re-fluye solo y los demás badges se corren, dando
            // feedback visible de dónde caería.
            int insertAt = row.Children.Count;
            int seen = 0;
            for (int i = 0; i < row.Children.Count; i++)
            {
                if (row.Children[i] is not FrameworkElement fe) continue;
                if (ReferenceEquals(fe, _dragBadge) || ReferenceEquals(fe, _dropSkeleton)) continue;
                if (seen == col) { insertAt = i; break; }
                seen++;
            }
            row.Children.Insert(Math.Min(insertAt, row.Children.Count), el);
            return;
        }

        // Otra fila: reconstruir sus items con el skeleton insertado; si se pasa
        // de 4, el excedente fluye a la fila siguiente (misma regla que al soltar).
        var items = row.Children.OfType<Border>().ToList();
        items.Insert(Math.Clamp(col, 0, items.Count), el);

        if (items.Count <= MaxBadgesPerRow)
        {
            RebuildRow(row, items);
            return;
        }

        // Overflow: el último elemento fluye a la fila siguiente DENTRO DE LA
        // MISMA CARD (los badges de una familia nunca cruzan a la card de otra).
        // Si la última fila de la card quedó vacía de un evento anterior del mismo
        // drag se REUTILIZA (si no, se acumularían filas vacías en cada move).
        var overflow = items[^1];
        items.RemoveAt(items.Count - 1);
        RebuildRow(row, items);
        var overflowCard = OwningCard(row) ?? _dragCard;
        if (overflowCard?.Child is not StackPanel overflowBody)
            throw new InvalidOperationException("OverlayPage: card de métricas sin cuerpo");
        var cardRowsLocal = CardRows(overflowCard);
        int rowIdxLocal = cardRowsLocal.IndexOf(row);
        if (rowIdxLocal >= 0 && rowIdxLocal + 1 < cardRowsLocal.Count)
        {
            cardRowsLocal[rowIdxLocal + 1].Children.Insert(0, overflow);
        }
        else
        {
            var nextRow = cardRowsLocal.LastOrDefault();
            if (nextRow == null || nextRow.Children.Count > 0)
            {
                nextRow = CreateRowPanel();
                overflowBody.Children.Add(nextRow);
            }
            nextRow.Children.Add(overflow);
        }
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
        // El skeleton vive en una fila DENTRO de una card de familia (las filas
        // ya no son hijas directas del panel): buscarlo en las cards. NO se pisa
        // el campo: UpdateDropSkeleton lo reutiliza en el próximo evento de move.
        foreach (var card in GroupCards())
            foreach (var row in CardRows(card))
                if (row.Children.Contains(_dropSkeleton))
                {
                    row.Children.Remove(_dropSkeleton);
                    return;
                }
        MetricBadgePanel.Children.Remove(_dropSkeleton);
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
            RebuildCardsFromSnapshot();
        }
        catch (Exception ex)
        {
            // Última red: un segundo intento tras limpiar TODO el panel.
            _log.LogWarning($"OverlayPage: RestoreSnapshot falló ({ex.Message}); segundo intento");
            try
            {
                MetricBadgePanel.Children.Clear();
                RebuildCardsFromSnapshot();
            }
            catch (Exception ex2)
            {
                _log.LogWarning($"OverlayPage: reconstrucción de cards imposible: {ex2.Message}");
            }
        }
    }

    /// <summary>
    /// Reconstruye las cards de familia desde el snapshot del drag REUSANDO las
    /// instancias de badge existentes (crear badges nuevos acá duplicaba entradas
    /// en _metricBadges y huérfanaba los originales: panel roto, badges que
    /// "desaparecían" al arrastrar).
    /// </summary>
    private void RebuildCardsFromSnapshot()
    {
        // 1) Desprender TODOS los badges de las filas de todas las cards
        // (usando la referencia de sus FILAS, no badge.Parent: en WinUI 3 una
        // fila detachada deja a sus hijos con Parent null pero siguen siendo
        // de esa fila — agregarlos a otra fila lanzaba COMException
        // 0x800F1000 "Element is already the child of another element").
        foreach (var card in GroupCards().ToList())
            foreach (var r in CardRows(card)) r.Children.Clear();
        MetricBadgePanel.Children.Clear();

        // 2) Agrupar las filas del snapshot por familia y reconstruir las cards
        // reusando los badges existentes (diccionario id→badge).
        var byId = _metricBadges.GroupBy(b => b.Id, StringComparer.Ordinal)
                                .ToDictionary(g => g.Key, g => g.First().Badge);
        var blocks = new Dictionary<string, List<List<string>>>();
        foreach (var rowIds in _dragSnapshot)
        {
            if (rowIds.Count == 0) continue;
            var g = OverlayWindow.GroupOf(rowIds[0]);
            if (!blocks.TryGetValue(g, out var block)) blocks[g] = block = new List<List<string>>();
            block.Add(rowIds);
        }
        var colors = new[] { ("cpu", "colorCpu"), ("gpu", "colorGpu"), ("ram", "colorRam"), ("fps", "colorFps") };
        foreach (var (group, colorKey) in colors)
        {
            if (!blocks.TryGetValue(group, out var block) || block.Count == 0) continue;
            MetricBadgePanel.Children.Add(BuildGroupCard(group, colorKey, block, reuse: byId));
        }

        // 3) Red de seguridad: ningún badge puede quedar fuera del árbol. Los
        // huérfanos van a la última fila de SU card; si la card de esa familia no
        // existe, se crea conteniéndolos (siempre dentro de su familia).
        var placed = GroupCards().SelectMany(CardRows)
            .SelectMany(r => r.Children.OfType<Border>()).ToHashSet();
        var strays = byId.Where(kv => !placed.Contains(kv.Value))
                         .GroupBy(kv => OverlayWindow.GroupOf(kv.Key), StringComparer.Ordinal)
                         .Where(g => g.Key.Length > 0)
                         .ToList();
        foreach (var strayGroup in strays)
        {
            var ids = strayGroup.Select(kv => kv.Key).ToList();
            var card = GroupCards().FirstOrDefault(c =>
                CardRows(c).SelectMany(r => r.Children.OfType<Border>())
                    .Any(b => OverlayWindow.GroupOf(BadgeId(b)) == strayGroup.Key));
            if (card == null)
            {
                var colorKey = colors.First(c => c.Item1 == strayGroup.Key).Item2;
                var block = new List<List<string>>();
                for (int i = 0; i < ids.Count; i += MaxBadgesPerRow)
                    block.Add(ids.Skip(i).Take(MaxBadgesPerRow).ToList());
                MetricBadgePanel.Children.Add(BuildGroupCard(strayGroup.Key, colorKey, block, reuse: byId));
                continue;
            }
            var body = card.Child as StackPanel;
            if (body == null) continue;
            foreach (var id in ids)
            {
                var last = CardRows(card).LastOrDefault();
                if (last == null || last.Children.Count >= MaxBadgesPerRow)
                {
                    last = CreateRowPanel();
                    body.Children.Add(last);
                }
                last.Children.Add(byId[id]);
            }
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
                // "Fila nueva" del panel = nueva línea DENTRO de la card del
                // grupo: se agrega al final del modelo y NormalizeRows la
                // re-acopla al bloque de SU familia (nunca al final del overlay).
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
