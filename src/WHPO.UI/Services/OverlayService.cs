using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using WHPO.Core.Services.Interfaces;
using WHPO_UI.Overlay;

namespace WHPO_UI.Services;

/// <summary>
/// Orquesta la superposición de métricas de juegos:
///  - Ciclo de vida: crea/oculta la ventana overlay, arranca y detiene el muestreo
///    de métricas (hardware + FPS por ETW) cuando el overlay está activado.
///  - Hotkeys globales configurables: mostrar/ocultar (Ctrl+Alt+X por defecto) y
///    bloquear/desbloquear (Ctrl+Alt+C por defecto), con flanco de subida.
///  - Persistencia del estado (activado, visible, bloqueado) en los settings.
///
/// La ventana overlay es WinForms y vive en el hilo de UI: todo acceso se hace por
/// el DispatcherQueue capturado al crear el servicio (en el constructor de MainWindow).
/// El watcher de hotkeys corre en un hilo de fondo (GetAsyncKeyState, mismo patrón
/// que los atajos de macros) y solo consume CPU mientras el overlay está activado.
/// </summary>
public sealed class OverlayService
{
    private const int VK_X = 0x58;
    private const int VK_C = 0x43;
    private const int MOD_ALT = 0x1;
    private const int MOD_CONTROL = 0x2;
    private const int MOD_SHIFT = 0x4;
    private const int MOD_WIN = 0x8;

    private static readonly int[] HotkeyCandidates = BuildHotkeyCandidates();

    private readonly ISettingsService _settings;
    private readonly ILoggingService _log;
    private readonly IOverlayMetricsService _metrics;
    private readonly DispatcherQueue _dispatcher;

    private readonly object _lock = new();
    private OverlayWindow? _window;
    private Thread? _hotkeyThread;
    private volatile bool _hotkeyStop;
    private bool _started;

    public OverlayService(
        ISettingsService settings,
        ILoggingService log,
        IOverlayMetricsService metrics)
    {
        _settings = settings;
        _log = log;
        _metrics = metrics;
        // El servicio se construye desde el hilo de UI (MainWindow): capturar el
        // dispatcher para marshaling de la ventana overlay.
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>¿El overlay está activado? Persistido en "overlay.enabled".</summary>
    public bool Enabled
    {
        get => _settings.Get("overlay.enabled", false);
        set
        {
            _settings.Set("overlay.enabled", value);
            _settings.Save();
            if (value) EnsureStarted();
            else Stop();
        }
    }

    public bool IsVisible => _window?.Visible ?? false;
    public bool IsLocked => _window?.Locked ?? false;

    /// <summary>Arranca el overlay (idempotente): ventana, métricas y hotkeys.</summary>
    public void EnsureStarted()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }

        if (!_settings.Get("overlay.enabled", false))
        {
            // El overlay estaba desactivado: no arrancar nada hasta que se active.
            lock (_lock) _started = false;
            return;
        }

        _metrics.Start();

        _dispatcher.TryEnqueue(() =>
        {
            EnsureWindow();
            // Atajos: primero se intenta el registro EXCLUSIVO (RegisterHotKey en la
            // ventana). Si Windows nos deja dueños, BlueStacks/otras apps dejan de
            // recibir la combinación. Si alguna falla (ya en uso), se cae al muestreo
            // por polling (comportamiento anterior) para no perder la función.
            ReapplyHotkeys();

            var show = _settings.Get("overlay.visible", true);
            _window!.Visible = show;
            _window.SetLocked(_settings.Get("overlay.locked", true));
            if (show)
            {
                _window.Show();
                _window.InvalidateConfig();
            }
        });
        _log.LogInfo("OverlayService: overlay iniciado");
    }

    /// <summary>Detiene el overlay: oculta la ventana, detiene métricas y hotkeys.</summary>
    public void Stop()
    {
        StopHotkeys();
        _metrics.Stop();
        _dispatcher.TryEnqueue(() =>
        {
            _window?.UnregisterHotkeys();
            _window?.Hide();
        });
        lock (_lock) _started = false;
        _log.LogInfo("OverlayService: overlay detenido");
    }

    /// <summary>Muestra/oculta el overlay (toggle del hotkey).</summary>
    public void ToggleVisible()
    {
        bool visible = _settings.Get("overlay.visible", true);
        SetVisible(!visible);
    }

    public void SetVisible(bool visible)
    {
        _settings.Set("overlay.visible", visible);
        _settings.Save();
        _dispatcher.TryEnqueue(() =>
        {
            if (_window == null) return;
            if (visible) _window.Show();
            else _window.Hide();
        });
    }

    /// <summary>Bloquea/desbloquea el overlay (toggle del hotkey).</summary>
    public void ToggleLock()
    {
        bool locked = _settings.Get("overlay.locked", true);
        SetLocked(!locked);
    }

    public void SetLocked(bool locked)
    {
        _settings.Set("overlay.locked", locked);
        _settings.Save();
        _dispatcher.TryEnqueue(() => _window?.SetLocked(locked));
    }

    /// <summary>Ubica el overlay en una esquina ("top-right", "top-left", ...).</summary>
    public void SetCorner(string corner)
    {
        _dispatcher.TryEnqueue(() => _window?.SetCorner(corner));
    }

    /// <summary>Fuerza la relectura de configuración de la ventana (colores, opacidad, toggles).</summary>
    public void ApplyWindowConfig()
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_window == null) return;
            _window.InvalidateConfig();
            _window.SetLocked(_settings.Get("overlay.locked", true));
            // El usuario pudo capturar un atajo nuevo en la página: re-registrar.
            ReapplyHotkeys();
        });
    }

    private void EnsureWindow()
    {
        if (_window != null) return;
        try
        {
            _window = new OverlayWindow(_settings, _log, _metrics);
            _window.SetLocked(_settings.Get("overlay.locked", true));
            // WM_HOTKEY llega en el hilo de UI (WndProc de la ventana overlay):
            // se puede invocar el toggle directamente.
            _window.ShowHotkeyPressed += ToggleVisible;
            _window.LockHotkeyPressed += ToggleLock;
        }
        catch (Exception ex)
        {
            _log.LogError("OverlayService: no se pudo crear la ventana overlay", ex);
        }
    }

    /// <summary>
    /// Registra los atajos de forma exclusiva; si no se puede (combinación en uso
    /// por otra app), revierte al muestreo por polling de toda la vida.
    /// </summary>
    private void ReapplyHotkeys()
    {
        if (_window == null) return;
        var (show, lockH) = _window.RegisterHotkeys();
        if (show && lockH)
        {
            StopHotkeys(); // dueños exclusivos: no hace falta muestrear
            _log.LogInfo("OverlayService: atajos registrados de forma exclusiva (RegisterHotKey)");
        }
        else
        {
            _window.UnregisterHotkeys();
            StartHotkeys(); // fallback por muestreo
        }
    }

    // ===== Hotkeys globales (fallback por muestreo) =====

    private void StartHotkeys()
    {
        if (_hotkeyThread != null && _hotkeyThread.IsAlive) return;
        _hotkeyStop = false;
        _hotkeyThread = new Thread(HotkeyLoop) { IsBackground = true, Name = "OverlayHotkeys" };
        _hotkeyThread.Start();
    }

    private void StopHotkeys()
    {
        _hotkeyStop = true;
        _hotkeyThread = null;
    }

    private void HotkeyLoop()
    {
        var previous = new Dictionary<string, bool>();
        while (!_hotkeyStop)
        {
            // Flanco de subida por combinación: mostrar/ocultar y bloquear/desbloquear.
            CheckHotkey(previous, "overlay.showHotkeyVk", VK_X, "overlay.showHotkeyMods", MOD_CONTROL | MOD_ALT, ToggleVisible);
            CheckHotkey(previous, "overlay.lockHotkeyVk", VK_C, "overlay.lockHotkeyMods", MOD_CONTROL | MOD_ALT, ToggleLock);
            Thread.Sleep(40);
        }
    }

    private void CheckHotkey(Dictionary<string, bool> previous,
        string vkKey, int defaultVk, string modsKey, int defaultMods, Action action)
    {
        int vk = _settings.Get(vkKey, defaultVk);
        int mods = _settings.Get(modsKey, defaultMods);
        if (vk <= 0) return;

        bool pressed = HotkeyDown(vk, mods);
        string key = vkKey + "|" + mods;
        bool was = previous.TryGetValue(key, out var w) && w;
        previous[key] = pressed;
        if (pressed && !was)
        {
            try { action(); }
            catch (Exception ex) { _log.LogWarning($"OverlayService: error en hotkey: {ex.Message}"); }
        }
    }

    private static bool HotkeyDown(int vk, int mods)
    {
        if (!IsDown(vk)) return false;
        bool ok = true;
        ok &= (mods & MOD_CONTROL) != 0 ? IsDown(0x11) : !IsDown(0x11);
        ok &= (mods & MOD_SHIFT) != 0 ? IsDown(0x10) : !IsDown(0x10);
        ok &= (mods & MOD_ALT) != 0 ? IsDown(0x12) : !IsDown(0x12);
        ok &= (mods & MOD_WIN) != 0 ? (IsDown(0x5B) || IsDown(0x5C)) : (!IsDown(0x5B) && !IsDown(0x5C));
        return ok;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Teclas candidatas a hotkey (A-Z, 0-9, F1-F12, navegación, símbolos).</summary>
    private static int[] BuildHotkeyCandidates()
    {
        var list = new List<int>();
        for (int i = 0x41; i <= 0x5A; i++) list.Add(i);
        for (int i = 0x30; i <= 0x39; i++) list.Add(i);
        for (int i = 0x70; i <= 0x7B; i++) list.Add(i);
        list.AddRange(new[]
        {
            0x08, 0x09, 0x0D, 0x1B, 0x20, 0x25, 0x26, 0x27, 0x28,
            0x2D, 0x2E, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0,
            0xDB, 0xDC, 0xDD, 0xDE
        });
        return list.ToArray();
    }

    /// <summary>Nombre legible de una tecla VK (para la UI de captura de hotkey).</summary>
    public static string KeyName(int vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2D => "Ins",
        0x2E => "Supr",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Numpad " + (vk - 0x60),
        0xBA => ";", 0xBB => "+", 0xBC => ",", 0xBD => "-", 0xBE => ".",
        0xBF => "/", 0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        >= 0x70 and <= 0x7B => "F" + (vk - 0x70 + 1),
        _ => $"VK({vk})"
    };

    /// <summary>Nombre de los modificadores (para la UI de captura de hotkey).</summary>
    public static string ModsName(int mods)
    {
        var parts = new List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }

    public static int[] HotkeyKeys => HotkeyCandidates;
}
