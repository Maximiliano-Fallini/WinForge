using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Services;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Página "Teclado": configura la repetición del teclado con tres valores en
    /// milisegundos, los aplica en vivo al instante (SPI_SETFILTERKEYS, igual que
    /// FilterKeysSetter) y los guarda en el registro del sistema para que persistan.
    /// </summary>
public sealed partial class TecladoPage : Page
{
    private readonly IKeyboardService _keyboardService;
    private readonly ILoggingService _loggingService;
    private readonly IMacroService _macroService;
    private readonly MacroHotkeyService _macroHotkeys;
    private bool _loaded;
    private Button? _selectedPreset;

    // ===== Estado de la pestaña Macros =====
    private List<MacroDefinition> _macros = new();
    private List<MacroStep>? _recordedSteps;
    private bool _recording;
    private int _editingIndex = -1;
    private int _editingHotkeyVk;
    private int _editingHotkeyMods;
    private bool _capturingHotkey;
    private int _capturePrevMouseState;
    private DispatcherQueueTimer? _captureTimer;

    // Modificadores de atajo (MOD_* de winuser.h)
    private const int ModAlt = 0x1;
    private const int ModCtrl = 0x2;
    private const int ModShift = 0x4;
    private const int ModWin = 0x8;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private static bool KeyIsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // Valores por defecto de Windows (Keyboard Response): Slow Keys 0 / Retardo 250 / Velocidad 33.
    private const int DefaultIgnoreMs = 0;
    private const int DefaultDelayMs = 250;
    private const int DefaultRateMs = 33;

    // Preset "Optimizada": los valores que yo uso (fijos, NO se leen del registro para
    // que no se igualen al default cuando el registro queda vacío).
    private const int OptimizedIgnoreMs = 0;
    private const int OptimizedDelayMs = 130;
    private const int OptimizedRateMs = 20;

    public TecladoPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
        _keyboardService = App.Services.GetRequiredService<IKeyboardService>();
        _loggingService = App.Services.GetRequiredService<ILoggingService>();
        _macroService = App.Services.GetRequiredService<IMacroService>();
        _macroHotkeys = App.Services.GetRequiredService<MacroHotkeyService>();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_loaded)
        {
            _loaded = true;
            LoadCurrentValues();
        }

        // Macros: refrescar siempre (pueden cambiar entre visitas). El vigilante de
        // atajos ya corre desde el arranque de la app (MacroHotkeyService); acá solo
        // se asegura de que esté activo y se recargan las armadas.
        try
        {
            _macros = _macroService.Load();
            _macroService.UpdateArmedMacros(_macros);
            _macroHotkeys.EnsureStarted();
            // Estado activado/desactivado persistido.
            if (MacrosEnabledToggle.IsOn != _macroHotkeys.Enabled)
                MacrosEnabledToggle.IsOn = _macroHotkeys.Enabled; // dispara Toggled → ApplyMacrosEnabledState
            else
                ApplyMacrosEnabledState();
            RebuildMacroList();
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"TecladoPage: no se pudieron cargar las macros: {ex.Message}");
        }

        // Contenido construido en código (lista de macros, editor): re-aplicar al
        // cambiar de idioma. La página no usa caché: se suscribe y desuscribe.
        I18n.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        RebuildMacroList();
        if (MacroEditorCard.Visibility == Visibility.Visible)
        {
            MacroEditorTitleText.Text = I18n.T(_editingIndex >= 0 ? "Editar macro" : "Crear macro");
            UpdateHotkeyDisplay();
            if (RecordStatusText.Visibility == Visibility.Visible)
            {
                RecordStatusText.Text = _recording
                    ? I18n.T("Grabando... presioná F9 para detener.")
                    : I18n.T("{0} pasos capturados", _recordedSteps?.Count ?? 0);
            }
            RebuildEventsList();
        }
        UpdateNewMacroButton();
        StopHotkeyCapture();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Al salir de la página: se suelta la grabación (si estaba) y las suscripciones
        // a la reproducción. El vigilante de atajos SIGUE corriendo (MacroHotkeyService)
        // para que los atajos funcionen en toda la app.
        try { _macroService.StopRecording(); }
        catch { }
        I18n.LanguageChanged -= OnLanguageChanged;
        StopHotkeyCapture();
    }

    private void LoadCurrentValues()
    {
        try
        {
            var s = _keyboardService.GetSettings();
            IgnoreUnderBox.Value = s.IgnoreUnderMs;
            RepeatDelayBox.Value = s.RepeatDelayMs;
            RepeatRateBox.Value = s.RepeatRateMs;
        }
        catch (Exception ex)
        {
            _loggingService.LogWarning($"TecladoPage: no se pudo leer la configuración: {ex.Message}");
        }
    }

    /// <summary>
    /// Presets como botones: completan los 3 campos con su configuración. Nunca
    /// aplican por sí solos; el usuario ajusta y da a Aplicar. El seleccionado
    /// queda resaltado hasta que se elija otro.
    /// </summary>
    private void DefaultPresetButton_Click(object sender, RoutedEventArgs e)
    {
        SelectPreset(DefaultPresetButton);
        IgnoreUnderBox.Value = DefaultIgnoreMs;
        RepeatDelayBox.Value = DefaultDelayMs;
        RepeatRateBox.Value = DefaultRateMs;
    }

    private void OptimizedPresetButton_Click(object sender, RoutedEventArgs e)
    {
        SelectPreset(OptimizedPresetButton);
        IgnoreUnderBox.Value = OptimizedIgnoreMs;
        RepeatDelayBox.Value = OptimizedDelayMs;
        RepeatRateBox.Value = OptimizedRateMs;
    }

    private void CurrentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        SelectPreset(CurrentPresetButton);
        try
        {
            var s = _keyboardService.GetSettings();
            IgnoreUnderBox.Value = s.IgnoreUnderMs;
            RepeatDelayBox.Value = s.RepeatDelayMs;
            RepeatRateBox.Value = s.RepeatRateMs;
        }
        catch (Exception ex)
        {
            Feedback.Info(FeedbackText, I18n.T("No se pudieron leer los valores actuales: {0}", ex.Message));
        }
    }

    /// <summary>Resalta el preset seleccionado y deselecciona el anterior.</summary>
    private void SelectPreset(Button button)
    {
        if (_selectedPreset != null && _selectedPreset != button)
            _selectedPreset.Style = (Style)Resources["PresetButtonStyle"];
        button.Style = (Style)Resources["PresetButtonSelectedStyle"];
        _selectedPreset = button;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int? ignore = GetValue(IgnoreUnderBox);
            int? delay = GetValue(RepeatDelayBox);
            int? rate = GetValue(RepeatRateBox);

            if (ignore == null || delay == null || rate == null)
            {
                Feedback.Error(FeedbackText, "Ingresá un número válido en los 3 campos antes de aplicar.");
                return;
            }

            if (_keyboardService.Apply(ignore.Value, delay.Value, rate.Value, SaveToRegistrySwitch.IsOn, out string error))
            {
                Feedback.Success(FeedbackText,
                    SaveToRegistrySwitch.IsOn
                        ? "Aplicado en vivo y guardado en el registro."
                        : "Aplicado en vivo (sin guardar en el registro).");
            }
            else
            {
                Feedback.Error(FeedbackText, I18n.T("No se pudo aplicar: {0}", error));
            }
        }
        catch (Exception ex)
        {
            Feedback.Error(FeedbackText, I18n.T("Error al aplicar: {0}", ex.Message));
            _loggingService.LogWarning($"TecladoPage: error aplicando: {ex.Message}");
        }
    }

    /// <summary>
    /// Devuelve el valor entero del NumberBox, o null si no es un número válido
    /// (campo vacío / sin terminar). Evita escribir basura (p. ej. 30000) en el registro.
    /// </summary>
    private static int? GetValue(NumberBox box)
    {
        double v = box.Value;
        if (double.IsNaN(v) || double.IsInfinity(v)) return null;
        return (int)Math.Clamp(v, 0, 5000);
    }

    // =====================================================================
    // Pestaña Macros
    // =====================================================================

    private void KeyboardTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ConfigTab == null || MacrosTab == null || sender.Items.Count < 2) return;
        bool macros = sender.SelectedItem == sender.Items[1];
        ConfigTab.Visibility = macros ? Visibility.Collapsed : Visibility.Visible;
        MacrosTab.Visibility = macros ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== Lista de macros =====

    private void RebuildMacroList()
    {
        if (MacrosList == null) return;
        MacrosList.Items.Clear();
        foreach (var macro in _macros)
            MacrosList.Items.Add(BuildMacroRow(macro));
        MacrosEmptyText.Visibility = _macros.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ListViewItem BuildMacroRow(MacroDefinition macro)
    {
        var name = new TextBlock
        {
            Text = macro.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var sub = new TextBlock
        {
            Text = MacroSubtitle(macro),
            FontSize = 12,
            Foreground = Feedback.MutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(name);
        info.Children.Add(sub);

        var editBtn = new Button
        {
            Content = I18n.T("Editar"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 12, 7),
            MinWidth = 80,
            VerticalAlignment = VerticalAlignment.Center
        };
        editBtn.Click += (s, e) => OpenEditor(macro);

        var delBtn = new Button
        {
            Content = I18n.T("Eliminar"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 12, 7),
            MinWidth = 80,
            VerticalAlignment = VerticalAlignment.Center
        };
        delBtn.Click += (s, e) => DeleteMacro(macro);

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(info);
        Grid.SetColumn(editBtn, 1); grid.Children.Add(editBtn);
        Grid.SetColumn(delBtn, 2); grid.Children.Add(delBtn);

        return new ListViewItem
        {
            Content = grid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 8, 4, 8)
        };
    }

    private string MacroSubtitle(MacroDefinition macro)
    {
        string loop = macro.LoopCount < 0 ? I18n.T("Infinito")
            : macro.LoopCount <= 1 ? I18n.T("Una vez")
            : I18n.T("{0} veces", macro.LoopCount);
        string hotkey = macro.HotkeyVk == 0 ? I18n.T("Sin atajo") : HotkeyName(macro.HotkeyVk, macro.HotkeyModifiers);
        return I18n.T("Atajo: {0} · {1} pasos · {2}", hotkey, macro.Steps.Count, loop);
    }

    // ===== Editor de macro =====

    private void NewMacroButton_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void OpenEditor(MacroDefinition? existing)
    {
        if (_recording) _macroService.StopRecording();
        StopHotkeyCapture();

        _editingIndex = existing == null ? -1 : _macros.IndexOf(existing);
        MacroEditorTitleText.Text = I18n.T(existing == null ? "Crear macro" : "Editar macro");
        MacroNameBox.Text = existing?.Name ?? "";
        _recordedSteps = existing is { Steps.Count: > 0 } e
            ? new List<MacroStep>(e.Steps)
            : new List<MacroStep>();
        _editingHotkeyVk = existing?.HotkeyVk ?? 0;
        _editingHotkeyMods = existing?.HotkeyModifiers ?? 0;
        var firstDelay = existing?.Steps.FirstOrDefault(s => s.Kind == MacroStepKind.Delay);
        EventDelayBox.Value = firstDelay != null ? Math.Min(10000, firstDelay.DelayMs) : 150;
        if (existing is { LoopCount: < 0 })
        {
            RunOnceRadio.IsChecked = false;
            RunRepeatRadio.IsChecked = true;
            RepeatCountBox.Value = 5;
        }
        else
        {
            RunOnceRadio.IsChecked = true;
            RunRepeatRadio.IsChecked = false;
            RepeatCountBox.Value = existing is { LoopCount: > 1 } m ? Math.Min(999, m.LoopCount) : 5;
        }
        _recording = false;
        SetRecordingButtons(false);
        RecordStatusText.Visibility = Visibility.Collapsed;
        RebuildEventsList();
        UpdateHotkeyDisplay();
        MacroSaveStatusText.Visibility = Visibility.Collapsed;
        MacroEditorCard.Visibility = Visibility.Visible;
        UpdateNewMacroButton();
    }

    private void CancelMacroButton_Click(object sender, RoutedEventArgs e) => CloseEditor();

    private void CloseEditor()
    {
        StopHotkeyCapture();
        MacroEditorCard.Visibility = Visibility.Collapsed;
        _editingIndex = -1;
        UpdateNewMacroButton();
    }

    // El botón "Crear macro" de la cabecera: mientras la vista de creación está
    // abierta muestra "Creando macro..." y queda deshabilitado para no abrir otra.
    private void UpdateNewMacroButton()
    {
        bool editing = MacroEditorCard.Visibility == Visibility.Visible;
        NewMacroButton.Content = editing ? I18n.T("Creando macro...") : I18n.T("Crear macro");
        NewMacroButton.IsEnabled = !editing;
    }

    // ===== Estado activado / desactivado =====

    private void MacrosEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // El Toggled puede dispararse durante InitializeComponent (al setear IsOn), antes
        // de que estén listos los servicios: se guarda solo si ya se resolvieron.
        if (_macroHotkeys != null)
            _macroHotkeys.Enabled = MacrosEnabledToggle.IsOn;
        ApplyMacrosEnabledState();
    }

    // Desactivadas: no se reproduce nada, se cierra el editor y solo queda el
    // interruptor visible. Activadas: aparece toda la lógica de crear macros.
    private void ApplyMacrosEnabledState()
    {
        // Puede invocarse durante InitializeComponent si Toggled se dispara al setear
        // IsOn: los controles posteriores todavía no existen, así que se ignora.
        if (MacrosListCard == null || MacrosDisabledHint == null) return;
        bool on = MacrosEnabledToggle.IsOn;
        if (!on)
        {
            _macroHotkeys?.Stop();
            if (MacroEditorCard.Visibility == Visibility.Visible)
                CloseEditor();
        }
        MacrosListCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MacrosDisabledHint.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        UpdateNewMacroButton();
    }

    private void SaveMacroButton_Click(object sender, RoutedEventArgs e)
    {
        var name = MacroNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Feedback.Error(MacroSaveStatusText, "Ingresá un nombre para la macro.");
            return;
        }
        if (_recordedSteps == null || _recordedSteps.Count == 0)
        {
            Feedback.Error(MacroSaveStatusText, "Primero grabá una macro (al menos un paso).");
            return;
        }

        int loop = RunRepeatRadio.IsChecked == true ? (int)Math.Max(2, RepeatCountBox.Value) : 1;
        var macro = new MacroDefinition(name, _editingHotkeyVk, _editingHotkeyMods, loop,
            _recordedSteps ?? new List<MacroStep>());

        if (_editingIndex >= 0 && _editingIndex < _macros.Count)
            _macros[_editingIndex] = macro;
        else
            _macros.Add(macro);

        try
        {
            _macroService.Save(_macros);
            _macroService.UpdateArmedMacros(_macros);
        }
        catch (Exception ex)
        {
            Feedback.Error(MacroSaveStatusText, I18n.T("No se pudo guardar: {0}", ex.Message));
            return;
        }

        CloseEditor();
        RebuildMacroList();
        Feedback.Success(MacrosListStatusText, I18n.T("Macro guardada: {0}", name));
    }

    private async void DeleteMacro(MacroDefinition macro)
    {
        if (XamlRoot == null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Borrar macro"),
            Content = I18n.T("¿Borrar la macro “{0}”? Esta acción no se puede deshacer.", macro.Name),
            PrimaryButtonText = I18n.T("Borrar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _macros.Remove(macro);
        _macroHotkeys.StopIfPlaying(macro.Name);
        try
        {
            _macroService.Save(_macros);
            _macroService.UpdateArmedMacros(_macros);
        }
        catch (Exception ex)
        {
            Feedback.Error(MacrosListStatusText, I18n.T("No se pudo guardar: {0}", ex.Message));
        }
        RebuildMacroList();
        Feedback.Success(MacrosListStatusText, I18n.T("Macro eliminada."));
    }

    // ===== Grabación =====

    private void StartRecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) return;

        // Una grabación nueva reemplaza los eventos actuales.
        _recordedSteps = new List<MacroStep>();
        RebuildEventsList();
        _recording = true;
        SetRecordingButtons(true);
        RecordStatusText.Text = I18n.T("Grabando... presioná F9 para detener.");
        RecordStatusText.Foreground = Feedback.AccentBrush;
        RecordStatusText.Visibility = Visibility.Visible;

        // Evitar que el NumberBox de delay conserve el foco al comenzar la captura:
        // el texto escrito durante la grabación debe convertirse en eventos, no en
        // una edición accidental del campo.
        // El foco debe quedar fuera de cualquier control editable. EventsList puede
        // no tener contenedores realizados todavía y, en ese caso, WinUI conserva el
        // foco en EventDelayBox. Enviar el foco al botón de detener garantiza que las
        // teclas capturadas no se escriban en el NumberBox.
        StopRecordButton.Focus(FocusState.Programmatic);

        _macroService.StartRecording(MacroService.DefaultRecordStopKey, RecordMouseToggle.IsOn,
            step => DispatcherQueue.TryEnqueue(() => AppendEventStep(step)),
            steps => DispatcherQueue.TryEnqueue(() =>
            {
                _recording = false;
                SetRecordingButtons(false);
                RecordStatusText.Foreground =
                    steps.Count > 0 ? Feedback.SuccessBrush : Feedback.WarningBrush;
                RecordStatusText.Text = steps.Count == 0
                    ? I18n.T("No se capturó ningún paso.")
                    : I18n.T("{0} pasos capturados", steps.Count);
            }));
    }

    private void StopRecordButton_Click(object sender, RoutedEventArgs e)
    {
        _macroService.StopRecording();
    }

    private void SetRecordingButtons(bool recording)
    {
        StartRecordButton.IsEnabled = !recording;
        StopRecordButton.IsEnabled = recording;
    }

    // ===== Lista de eventos en vivo =====

    private void RebuildEventsList()
    {
        if (EventsList == null) return;
        EventsList.Items.Clear();
        if (_recordedSteps == null) return;
        foreach (var step in _recordedSteps)
            EventsList.Items.Add(CreateEventItem(step));
        DeleteEventButton.IsEnabled = false;
    }

    private ListViewItem CreateEventItem(MacroStep step)
    {
        // Las filas de delay se estilizan con el texto secundario para distinguirlas.
        bool isDelayRow = step.Kind == MacroStepKind.Delay;
        var text = new TextBlock { Text = StepText(step), FontSize = 12 };
        if (isDelayRow) text.Foreground = ThemeBrushes.Get("SecondaryTextBrush");
        return new ListViewItem { Content = text, Tag = step };
    }

    private void AppendEventStep(MacroStep step)
    {
        _recordedSteps ??= new List<MacroStep>();
        // Entre cada par de eventos se inserta un paso Delay explícito con el valor
        // global actual; nunca al final (no hay delay de salida del macro).
        if (_recordedSteps.Count > 0)
        {
            var delay = new MacroStep(MacroStepKind.Delay, 0, false, 0, false, 0, 0,
                (int)Math.Max(0, EventDelayBox.Value));
            _recordedSteps.Add(delay);
            EventsList.Items.Add(CreateEventItem(delay));
        }
        _recordedSteps.Add(step);
        var item = CreateEventItem(step);
        EventsList.Items.Add(item);
        EventsList.ScrollIntoView(item);
    }

    private void EventsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DeleteEventButton.IsEnabled = EventsList.SelectedIndex >= 0;
    }

    private void DeleteEventButton_Click(object sender, RoutedEventArgs e)
    {
        int idx = EventsList.SelectedIndex;
        if (idx < 0 || _recordedSteps == null || idx >= _recordedSteps.Count) return;
        _recordedSteps.RemoveAt(idx);
        EventsList.Items.RemoveAt(idx);
        if (idx < EventsList.Items.Count)
            EventsList.SelectedIndex = idx;
        else if (EventsList.Items.Count > 0)
            EventsList.SelectedIndex = EventsList.Items.Count - 1;
        else
            DeleteEventButton.IsEnabled = false;
    }

    // ===== Edición de eventos (doble clic) =====

    // Doble clic en un evento: cambia la tecla; doble clic en una fila "Delay": cambia ese delay.
    private void EventsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_recordedSteps == null || _recordedSteps.Count == 0) return;
        var original = e.OriginalSource as DependencyObject;
        while (original != null && original is not ListViewItem)
            original = VisualTreeHelper.GetParent(original);
        if (original is not ListViewItem item) return;
        int index = EventsList.IndexFromContainer(item);
        if (index < 0 || index >= _recordedSteps.Count) return;
        var step = _recordedSteps[index];
        if (step.Kind == MacroStepKind.Delay)
            _ = EditDelayAsync(index);
        else
            _ = EditKeyAsync(index);
    }

    // Cambiar la tecla del evento: captura la próxima tecla presionada (Esc cancela).
    private async Task EditKeyAsync(int index)
    {
        if (XamlRoot == null || _recordedSteps == null || index >= _recordedSteps.Count) return;
        var status = new TextBlock
        {
            Text = I18n.T("Presioná la nueva tecla... Esc para cancelar."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Cambiar evento"),
            Content = status,
            CloseButtonText = I18n.T("Cancelar")
        };
        var tcs = new TaskCompletionSource<int>();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += (s, _) =>
        {
            if (KeyIsDown(0x1B)) { timer.Stop(); tcs.TrySetResult(0); dialog.Hide(); return; }
            foreach (var vk in CaptureCandidates)
            {
                if (vk == 0x1B || vk == MacroService.DefaultRecordStopKey || IsModifierKey(vk)) continue;
                if (!KeyIsDown(vk)) continue;
                timer.Stop();
                tcs.TrySetResult(vk);
                dialog.Hide();
                return;
            }
        };
        timer.Start();
        await dialog.ShowAsync();
        timer.Stop();
        int chosen = tcs.Task.IsCompleted ? tcs.Task.Result : 0;
        if (chosen <= 0 || _recordedSteps == null || index >= _recordedSteps.Count) return;
        var old = _recordedSteps[index];
        _recordedSteps[index] = old with
        {
            Kind = MacroStepKind.Key,
            KeyCode = chosen,
            KeyDown = true,
            MouseButton = 0,
            MouseDown = false
        };
        RebuildEventsList();
    }

    // Cambiar el delay de una fila "Delay - X ms".
    private async Task EditDelayAsync(int index)
    {
        if (XamlRoot == null || _recordedSteps == null || index >= _recordedSteps.Count) return;
        var step = _recordedSteps[index];
        var box = new NumberBox
        {
            Minimum = 0,
            Maximum = 10000,
            SmallChange = 10,
            LargeChange = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = Math.Max(0, step.DelayMs)
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = I18n.T("Tiempo de espera (ms)"), FontSize = 12 });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = I18n.T("Cambiar delay"),
            Content = panel,
            PrimaryButtonText = I18n.T("Aceptar"),
            CloseButtonText = I18n.T("Cancelar"),
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && _recordedSteps != null && index < _recordedSteps.Count)
        {
            _recordedSteps[index] = _recordedSteps[index] with { DelayMs = (int)Math.Max(0, box.Value) };
            RebuildEventsList();
        }
    }

    /// <summary>
    /// Texto del evento en la lista. Cada pulsación de tecla/clic es un solo paso
    /// ("W", "Click izquierdo") y los delays son pasos explícitos entre eventos
    /// ("Delay - X ms"). Los pasos "soltar" (↑) solo aparecen en macros viejas.
    /// </summary>
    private string StepText(MacroStep step)
    {
        if (step.Kind == MacroStepKind.Delay)
            return I18n.T("Delay - {0} ms", step.DelayMs);
        if (step.Kind == MacroStepKind.Key)
            return step.KeyDown ? KeyName(step.KeyCode) : $"{KeyName(step.KeyCode)} ↑";
        if (step.Kind == MacroStepKind.MouseButton)
            return step.MouseDown ? MouseButtonName(step.MouseButton) : $"{MouseButtonName(step.MouseButton)} ↑";
        return I18n.T("Mouse");
    }

    // Cuando cambia el delay global, se refrescan las filas "Delay - X ms" en vivo.
    private void EventDelayBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (EventsList != null && MacroEditorCard.Visibility == Visibility.Visible)
            RebuildEventsList();
    }

    private static string MouseButtonName(int button) => button switch
    {
        1 => I18n.T("Click izquierdo"),
        2 => I18n.T("Click derecho"),
        4 => I18n.T("Click medio"),
        5 => I18n.T("Click lateral atrás"),
        _ => I18n.T("Click lateral adelante")
    };

    private void RunMode_Checked(object sender, RoutedEventArgs e)
    {
        if (RepeatCountBox == null) return;
        RepeatCountBox.IsEnabled = RunRepeatRadio.IsChecked == true;
    }

    // ===== Atajo de teclado =====

    private void AssignHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey)
        {
            StopHotkeyCapture();
            return;
        }

        _capturingHotkey = true;
        AssignHotkeyButton.Content = I18n.T("Cancelar captura");
        HotkeyCaptureStatusText.Text = I18n.T("Capturando atajo... presioná una tecla o un botón del mouse. Esc para cancelar.");
        HotkeyCaptureStatusText.Visibility = Visibility.Visible;

        // Estado previo de los botones del mouse: si quedó un clic apretado al iniciar
        // la captura (por ejemplo el que abrió el botón), no debe capturarse.
        _capturePrevMouseState = CurrentMouseState();

        _captureTimer = DispatcherQueue.CreateTimer();
        _captureTimer.Interval = TimeSpan.FromMilliseconds(60);
        _captureTimer.Tick += HotkeyCaptureTick;
        _captureTimer.Start();
    }

    private void HotkeyCaptureTick(object sender, object e)
    {
        // Esc cancela la captura.
        if (KeyIsDown(0x1B))
        {
            StopHotkeyCapture();
            return;
        }

        // Botones del mouse (izquierdo, derecho, medio y laterales X1/X2): solo en el
        // flanco de subida, para no capturar el clic que abrió la captura.
        int mouseNow = CurrentMouseState();
        int rising = mouseNow & ~_capturePrevMouseState;
        _capturePrevMouseState = mouseNow;
        if (rising != 0)
        {
            int vk = (rising & 1) != 0 ? 0x01 : (rising & 2) != 0 ? 0x02 : (rising & 4) != 0 ? 0x04
                : (rising & 8) != 0 ? 0x05 : 0x06;
            _editingHotkeyVk = vk;
            _editingHotkeyMods = 0;
            UpdateHotkeyDisplay();
            StopHotkeyCapture();
            return;
        }

        foreach (var vk in CaptureCandidates)
        {
            if (IsModifierKey(vk) || vk == 0x1B || vk == MacroService.DefaultRecordStopKey) continue;
            if (!KeyIsDown(vk)) continue;

            int mods = 0;
            if (KeyIsDown(0x11)) mods |= ModCtrl;
            if (KeyIsDown(0x10)) mods |= ModShift;
            if (KeyIsDown(0x12)) mods |= ModAlt;
            if (KeyIsDown(0x5B) || KeyIsDown(0x5C)) mods |= ModWin;

            _editingHotkeyVk = vk;
            _editingHotkeyMods = mods;
            UpdateHotkeyDisplay();
            StopHotkeyCapture();
            return;
        }
    }

    // Bitmask del estado actual de los botones del mouse: 1=izq, 2=der, 4=medio, 8=X1, 16=X2.
    private static int CurrentMouseState()
    {
        int s = 0;
        if (KeyIsDown(0x01)) s |= 1;
        if (KeyIsDown(0x02)) s |= 2;
        if (KeyIsDown(0x04)) s |= 4;
        if (KeyIsDown(0x05)) s |= 8;
        if (KeyIsDown(0x06)) s |= 16;
        return s;
    }

    private void StopHotkeyCapture()
    {
        _capturingHotkey = false;
        _captureTimer?.Stop();
        _captureTimer = null;
        if (AssignHotkeyButton != null)
            AssignHotkeyButton.Content = I18n.T("Asignar a: {0}",
                _editingHotkeyVk == 0 ? I18n.T("Sin atajo") : HotkeyName(_editingHotkeyVk, _editingHotkeyMods));
        if (HotkeyCaptureStatusText != null)
            HotkeyCaptureStatusText.Visibility = Visibility.Collapsed;
    }

    private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _editingHotkeyVk = 0;
        _editingHotkeyMods = 0;
        UpdateHotkeyDisplay();
    }

    private void UpdateHotkeyDisplay()
    {
        if (AssignHotkeyButton == null) return;
        AssignHotkeyButton.Content = I18n.T("Asignar a: {0}",
            _editingHotkeyVk == 0 ? I18n.T("Sin atajo") : HotkeyName(_editingHotkeyVk, _editingHotkeyMods));
        ClearHotkeyButton.Visibility = _editingHotkeyVk == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string HotkeyName(int vk, int mods)
    {
        var parts = new List<string>();
        if ((mods & ModAlt) != 0) parts.Add(I18n.T("Alt"));
        if ((mods & ModCtrl) != 0) parts.Add(I18n.T("Ctrl"));
        if ((mods & ModShift) != 0) parts.Add(I18n.T("Shift"));
        if ((mods & ModWin) != 0) parts.Add(I18n.T("Win"));
        parts.Add(IsMouseVk(vk) ? MouseButtonName(vk) : KeyName(vk));
        return string.Join(" + ", parts);
    }

    // VK de botones del mouse: 1=izq, 2=der, 4=medio, 5=X1 (atrás), 6=X2 (adelante).
    private static bool IsMouseVk(int vk) => vk is 0x01 or 0x02 or 0x04 or 0x05 or 0x06;

    private static string KeyName(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2D => "Insert",
            0x2E => "Supr",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => I18n.T("Tecla {0}", vk)
        };
    }

    private static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C;

    // Teclas candidatas para capturar un atajo (A-Z, 0-9, F1-F12, numpad, navegación).
    private static readonly int[] CaptureCandidates = BuildCaptureCandidates();

    private static int[] BuildCaptureCandidates()
    {
        var list = new List<int>();
        for (int i = 0x41; i <= 0x5A; i++) list.Add(i);
        for (int i = 0x30; i <= 0x39; i++) list.Add(i);
        for (int i = 0x70; i <= 0x7B; i++) list.Add(i);
        for (int i = 0x60; i <= 0x69; i++) list.Add(i);
        list.AddRange(new[]
        {
            0x08, 0x09, 0x0D, 0x20, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E,
            0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE
        });
        return list.ToArray();
    }
}
