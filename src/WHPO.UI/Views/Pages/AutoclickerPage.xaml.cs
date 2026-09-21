using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Página "Autoclicker": genera clics con intervalo configurable (h/m/s/ms),
/// límite opcional de clics y posición fija del cursor. La hotkey global (F6 por
/// defecto, configurable) inicia/detiene desde cualquier aplicación.
/// </summary>
public sealed partial class AutoclickerPage : Page
{
    private readonly IAutoClickerService _clicker;
    private readonly ISettingsService _settings;
    private readonly ILoggingService _logging;
    private bool _loaded;
    private bool _capturingKey;
    private bool _limitReached;

    // Hotkey actual (por defecto F6, sin modificadores)
    private VirtualKey _hkKey = VirtualKey.F6;
    private bool _hkAlt;
    private bool _hkCtrl;
    private bool _hkShift;
    private bool _hkWin;

    public AutoclickerPage()
    {
        InitializeComponent();
        _clicker = App.Services.GetRequiredService<IAutoClickerService>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _logging = App.Services.GetRequiredService<ILoggingService>();
        _clicker.StateChanged += OnClickerStateChanged;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Cargar hotkey guardada (F6 por defecto) solo la primera vez.
        if (!_loaded)
        {
            _loaded = true;
            _hkKey = (VirtualKey)_settings.Get("autoclicker.hotkeyVk", (int)VirtualKey.F6);
            _hkAlt = _settings.Get("autoclicker.hotkeyAlt", false);
            _hkCtrl = _settings.Get("autoclicker.hotkeyCtrl", false);
            _hkShift = _settings.Get("autoclicker.hotkeyShift", false);
            _hkWin = _settings.Get("autoclicker.hotkeyWin", false);
        }

        RegisterHotkey();
        UpdateHotkeyUi();
        UpdateStartStopUi();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _clicker.Stop();
        _clicker.UnregisterHotKey();
    }

    // ===================== Intervalo =====================

    private TimeSpan? GetInterval()
    {
        try
        {
            var total = TimeSpan.FromHours(HoursBox.Value)
                        + TimeSpan.FromMinutes(MinutesBox.Value)
                        + TimeSpan.FromSeconds(SecondsBox.Value)
                        + TimeSpan.FromMilliseconds(MillisecondsBox.Value);
            if (total < TimeSpan.FromMilliseconds(1))
            {
                Feedback.Error(StatusText, "El intervalo debe ser de al menos 1 ms.");
                return null;
            }
            return total;
        }
        catch (Exception)
        {
            Feedback.Error(StatusText, "Ingresá valores válidos en el intervalo.");
            return null;
        }
    }

    private int? GetLimit()
    {
        if (!LimitSwitch.IsOn) return null;
        var limit = (int)Math.Round(LimitBox.Value);
        return limit >= 1 ? limit : null;
    }

    private TimeSpan? GetMaxDuration()
    {
        if (!DurationSwitch.IsOn) return null;
        var total = TimeSpan.FromHours(DurationHoursBox.Value)
                    + TimeSpan.FromMinutes(DurationMinutesBox.Value)
                    + TimeSpan.FromSeconds(DurationSecondsBox.Value)
                    + TimeSpan.FromMilliseconds(DurationMillisecondsBox.Value);
        return total >= TimeSpan.FromMilliseconds(1) ? total : null;
    }

    private (int? X, int? Y) GetPosition()
    {
        int? x = TryParseInt(PosXBox.Text);
        int? y = TryParseInt(PosYBox.Text);
        return (x, y);
    }

    private static int? TryParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // ===================== Acciones =====================

    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clicker.IsRunning)
        {
            _clicker.Stop();
            return;
        }

        var interval = GetInterval();
        if (interval == null) return;

        var (x, y) = GetPosition();
        _limitReached = false;

        var limit = GetLimit();
        var duration = GetMaxDuration();
        _clicker.Start(interval.Value, limit, duration, TimeSpan.FromSeconds(3), DoubleClickSwitch.IsOn, CornerStopSwitch.IsOn, x, y);

        var extra = new System.Text.StringBuilder();
        if (limit.HasValue) extra.Append($", {I18n.T("máx {0} clics", limit)}");
        if (duration.HasValue) extra.Append($", {I18n.T("tiempo límite {0}", FormatInterval(duration.Value))}");
        if (DoubleClickSwitch.IsOn) extra.Append($", {I18n.T("Doble click")}");
        if (CornerStopSwitch.IsOn) extra.Append($", {I18n.T("Parada en esquina")}");
        Feedback.Set(StatusText, I18n.T("▶ Iniciando en 3 segundos — clics cada {0}{1}{2}. Posicioná el mouse y presioná {3} para cancelar.",
            FormatInterval(interval.Value),
            x.HasValue ? I18n.T(" en ({0},{1})", x, y) : "",
            extra,
            HotkeyName()), Feedback.AccentBrush, persistent: true);
    }

    private void UseCurrentPositionButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetCursorPos(out var pt))
        {
            PosXBox.Text = pt.X.ToString(CultureInfo.InvariantCulture);
            PosYBox.Text = pt.Y.ToString(CultureInfo.InvariantCulture);
            Feedback.Set(CurrentPosText, I18n.T("Posición actual capturada: X={0} · Y={1}", pt.X, pt.Y), Feedback.SuccessBrush);
        }
        else
        {
            Feedback.Error(CurrentPosText, "No se pudo obtener la posición del cursor.");
        }
    }

    private void LimitSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        LimitBox.IsEnabled = LimitSwitch.IsOn;
    }

    private void DurationSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        var on = DurationSwitch.IsOn;
        DurationHoursBox.IsEnabled = on;
        DurationMinutesBox.IsEnabled = on;
        DurationSecondsBox.IsEnabled = on;
        DurationMillisecondsBox.IsEnabled = on;
    }

    private void OnClickerStateChanged()
    {
        // El evento llega desde el thread del loop: volver al dispatcher de la UI.
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateStartStopUi();

            if (_clicker.IsRunning)
            {
                _limitReached = false;
                // Ya arrancó a generar clics (terminó el delay de 3 s): avisar.
                if (_clicker.Clicks == 1)
                    Feedback.Set(StatusText, I18n.T("✓ Generando clics... {0} generado — presioná {1} para detener.", _clicker.Clicks, HotkeyName()), Feedback.AccentBrush, persistent: true);
            }
            else if (!_limitReached && _clicker.Clicks > 0)
            {
                Feedback.Set(StatusText, I18n.T("✓ Detenido — {0} clics generados.", _clicker.Clicks), Feedback.SuccessBrush);
            }
        });
    }

    private void UpdateStartStopUi()
    {
        if (StartStopButton == null) return;
        if (_clicker.IsRunning)
        {
            StartStopButton.Content = I18n.T("Detener ({0})", HotkeyName());
        }
        else
        {
            StartStopButton.Content = I18n.T("Iniciar ({0})", HotkeyName());
        }
    }

    private string HotkeyName()
    {
        var sb = new StringBuilder();
        if (_hkCtrl) sb.Append("Ctrl+");
        if (_hkAlt) sb.Append("Alt+");
        if (_hkShift) sb.Append("Shift+");
        if (_hkWin) sb.Append("Win+");
        sb.Append(KeyName(_hkKey));
        return sb.ToString();
    }

    private static string KeyName(VirtualKey key) => key.ToString();

    private static string FormatInterval(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s {t.Milliseconds}ms";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes}m {t.Seconds}s {t.Milliseconds}ms";
        if (t.TotalSeconds >= 1)
            return $"{t.Seconds}s {t.Milliseconds}ms";
        return $"{t.Milliseconds}ms";
    }

    // ===================== Hotkey =====================

    private void HotkeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturingKey) return;
        e.Handled = true;

        // Ignorar solo modificadores (sin tecla principal).
        var k = e.Key;
        if (k == VirtualKey.Control || k == VirtualKey.Shift || k == VirtualKey.Menu || k == VirtualKey.LeftWindows || k == VirtualKey.RightWindows)
            return;

        _hkKey = k;
        _hkCtrl = IsDown(VirtualKey.Control) || IsDown(VirtualKey.LeftControl) || IsDown(VirtualKey.RightControl);
        _hkAlt = IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftMenu) || IsDown(VirtualKey.RightMenu);
        _hkShift = IsDown(VirtualKey.Shift) || IsDown(VirtualKey.LeftShift) || IsDown(VirtualKey.RightShift);
        _hkWin = IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);

        _capturingKey = false;
        HotkeyBox.Text = HotkeyName();

        // Guardar y registrar.
        _settings.Set("autoclicker.hotkeyVk", (int)_hkKey);
        _settings.Set("autoclicker.hotkeyAlt", _hkAlt);
        _settings.Set("autoclicker.hotkeyCtrl", _hkCtrl);
        _settings.Set("autoclicker.hotkeyShift", _hkShift);
        _settings.Set("autoclicker.hotkeyWin", _hkWin);
        _settings.Save();

        RegisterHotkey();
        UpdateHotkeyUi();
        UpdateStartStopUi();
    }

    private static bool IsDown(VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _capturingKey = true;
        HotkeyBox.Text = I18n.T("Presioná una tecla...");
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_capturingKey)
        {
            _capturingKey = false;
            HotkeyBox.Text = HotkeyName();
        }
    }

    private void UpdateHotkeyUi()
    {
        if (HotkeyStatusText == null) return;
        HotkeyStatusText.Text = I18n.T("Hotkey activa: {0} — funciona desde cualquier aplicación", HotkeyName());
    }

    private void RegisterHotkey()
    {
        var ok = _clicker.RegisterHotKey((uint)_hkKey, _hkAlt, _hkCtrl, _hkShift, _hkWin);
        if (!ok)
            Feedback.Warning(StatusText, I18n.T("No se pudo registrar {0}: puede estar en uso por otra aplicación.", HotkeyName()));
    }

    // ===================== P/Invoke =====================

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
