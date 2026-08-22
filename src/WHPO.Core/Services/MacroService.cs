using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Grabador y reproductor de macros estilo MacroGamer.
///
/// Grabación: hilo de fondo que muestrea cada ~10 ms las teclas (GetAsyncKeyState),
/// la posición del mouse (GetCursorPos) y los botones, registrando cada transición
/// con el delay desde el evento anterior. Funciona aunque la app no tenga foco.
///
/// Reproducción: SendInput (teclado + mouse, movimientos en coordenadas absolutas).
///
/// Atajos globales: hilo vigilante que revisa los atajos armados (flanco de subida)
/// y dispara el callback, sin necesidad de hooks ni ventanas ocultas.
///
/// Persistencia: JSON en %LocalAppData%\WHPO\macros.json.
/// </summary>
public sealed class MacroService : IMacroService
{
    private readonly ILoggingService _logging;
    private readonly string _filePath;
    private readonly object _lock = new();

    // ===== P/Invoke =====
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type; // 0 = mouse, 1 = teclado
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint XBUTTON1 = 0x0001; // botón lateral atrás (VK 0x05)
    private const uint XBUTTON2 = 0x0002; // botón lateral adelante (VK 0x06)

    // Modificadores de atajo (MOD_* de winuser.h)
    private const int MOD_ALT = 0x1;
    private const int MOD_CONTROL = 0x2;
    private const int MOD_SHIFT = 0x4;
    private const int MOD_WIN = 0x8;

    // VK de las teclas modificadoras
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    // Tecla por defecto para detener la grabación (F9).
    public const int DefaultRecordStopKey = 0x78;

    // ===== Estado de grabación =====
    private Thread? _recordThread;
    private volatile bool _stopRequested;
    private bool _recording;

    // ===== Vigilante de atajos =====
    private Thread? _watcherThread;
    private volatile bool _watcherStop;
    private Action<MacroDefinition>? _hotkeyTriggered;
    private Func<bool>? _isEnabled;
    private List<MacroDefinition> _armed = new();

    public bool IsRecording => _recording;

    public MacroService(ILoggingService logging)
    {
        _logging = logging;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WHPO");
        try { Directory.CreateDirectory(dir); } catch { }
        _filePath = Path.Combine(dir, "macros.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ===================== Persistencia =====================

    public List<MacroDefinition> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new List<MacroDefinition>();
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<MacroDefinition>>(json, JsonOptions);
            return list ?? new List<MacroDefinition>();
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"No se pudo leer macros.json: {ex.Message}");
            return new List<MacroDefinition>();
        }
    }

    public void Save(List<MacroDefinition> macros)
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(macros, JsonOptions));
        }
        catch (Exception ex)
        {
            _logging.LogError($"No se pudo guardar macros.json: {ex.Message}", ex);
            throw;
        }
    }

    // ===================== Grabación =====================

    public void StartRecording(int stopKeyVk, bool captureMouse,
        Action<MacroStep>? onStep, Action<List<MacroStep>> completed)
    {
        lock (_lock)
        {
            if (_recording) return;
            _recording = true;
            _stopRequested = false;
        }
        _recordThread = new Thread(() => RecordLoop(stopKeyVk, captureMouse, onStep, completed))
        {
            IsBackground = true,
            Name = "MacroRecording"
        };
        _recordThread.Start();
    }

    public void StopRecording()
    {
        _stopRequested = true;
    }

    private void RecordLoop(int stopKeyVk, bool captureMouse,
        Action<MacroStep>? onStep, Action<List<MacroStep>> completed)
    {
        var steps = new List<MacroStep>();
        var downKeys = new HashSet<int>();
        bool prevLeft = false, prevRight = false, prevMiddle = false, prevX1 = false, prevX2 = false;

        void Add(MacroStep step)
        {
            steps.Add(step);
            try { onStep?.Invoke(step); } catch { }
        }

        try
        {
            while (!_stopRequested)
            {
                // Tecla de detener: no se graba (y se espera a que se suelte).
                if (IsDown(stopKeyVk))
                {
                    Thread.Sleep(150);
                    break;
                }

                // Clics del mouse (solo si está activado): cada clic es UN paso
                // (presionar+soltar) con la posición, emitido en el flanco de bajada.
                if (captureMouse)
                {
                    GetCursorPos(out var pos);
                    bool left = IsDown(0x01), right = IsDown(0x02), middle = IsDown(0x04);
                    bool x1 = IsDown(0x05), x2 = IsDown(0x06);
                    if (left && !prevLeft)
                        Add(new MacroStep(MacroStepKind.MouseButton, 0, false, 1, true, pos.X, pos.Y, 0));
                    if (right && !prevRight)
                        Add(new MacroStep(MacroStepKind.MouseButton, 0, false, 2, true, pos.X, pos.Y, 0));
                    if (middle && !prevMiddle)
                        Add(new MacroStep(MacroStepKind.MouseButton, 0, false, 4, true, pos.X, pos.Y, 0));
                    if (x1 && !prevX1)
                        Add(new MacroStep(MacroStepKind.MouseButton, 0, false, 5, true, pos.X, pos.Y, 0));
                    if (x2 && !prevX2)
                        Add(new MacroStep(MacroStepKind.MouseButton, 0, false, 6, true, pos.X, pos.Y, 0));
                    prevLeft = left;
                    prevRight = right;
                    prevMiddle = middle;
                    prevX1 = x1;
                    prevX2 = x2;
                }

                // Teclas: cada pulsación completa (presionar+soltar) es UN paso,
                // emitido en el flanco de bajada. Soltar solo cierra el paso, sin
                // emitir uno nuevo: así el orden queda según se pulsan las teclas,
                // sin que un "soltar" se desfase respecto al siguiente "presionar".
                foreach (var vk in CandidateKeys)
                {
                    if (vk == stopKeyVk) continue;
                    bool down = IsDown(vk);
                    if (down && !downKeys.Contains(vk))
                    {
                        downKeys.Add(vk);
                        Add(new MacroStep(MacroStepKind.Key, vk, true, 0, false, 0, 0, 0));
                    }
                    else if (!down && downKeys.Contains(vk))
                    {
                        downKeys.Remove(vk);
                    }
                }

                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Grabación de macro interrumpida: {ex.Message}");
        }
        finally
        {
            _recording = false;
            try { completed?.Invoke(steps); } catch { }
        }
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // Teclas candidatas a grabar: A-Z, 0-9, F1-F12, numpad, navegación y puntuación.
    private static readonly int[] CandidateKeys = BuildCandidateKeys();

    private static int[] BuildCandidateKeys()
    {
        var list = new List<int>();
        for (int i = 0x41; i <= 0x5A; i++) list.Add(i);   // A-Z
        for (int i = 0x30; i <= 0x39; i++) list.Add(i);   // 0-9
        for (int i = 0x70; i <= 0x7B; i++) list.Add(i);   // F1-F12
        for (int i = 0x60; i <= 0x69; i++) list.Add(i);   // Numpad 0-9
        list.AddRange(new[]
        {
            0x08, 0x09, 0x0D, 0x1B, 0x20,            // Backspace Tab Enter Esc Espacio
            0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E,      // Flechas Inicio/Fin RePág/AvPág Supr
            0x10, 0x11, 0x12,                        // Shift Ctrl Alt
            0x2C, 0x2F,                              // ImprPant, Ayuda?
            0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, // ;: =+ ,< -_ .> /? `~
            0xDB, 0xDC, 0xDD, 0xDE                   // [{ \| ]} '"
        });
        return list.ToArray();
    }

    // ===================== Reproducción =====================

    public Task PlayAsync(MacroDefinition macro, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            int iterations = macro.LoopCount < 0 ? int.MaxValue : Math.Max(1, macro.LoopCount);
            for (int i = 0; i < iterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var step in macro.Steps)
                {
                    ct.ThrowIfCancellationRequested();
                    ExecuteStep(step);
                    // El paso Delay ya espera su propio tiempo; los demás esperan su DelayMs.
                    if (step.Kind != MacroStepKind.Delay && step.DelayMs > 0)
                        Thread.Sleep(Math.Min(step.DelayMs, 2000));
                }
            }
        }, ct);
    }

    private void ExecuteStep(MacroStep step)
    {
        try
        {
            switch (step.Kind)
            {
                case MacroStepKind.Key:
                    if (step.KeyDown)
                    {
                        // Pulsación completa: presionar, sostener un instante y soltar.
                        SendKey(step.KeyCode, true);
                        Thread.Sleep(20);
                        SendKey(step.KeyCode, false);
                    }
                    else
                    {
                        SendKey(step.KeyCode, false);
                    }
                    break;
                case MacroStepKind.MouseButton:
                    // Clic en la posición grabada (si la tiene).
                    if (step.X != 0 || step.Y != 0)
                        SendMouseMove(step.X, step.Y);
                    if (step.MouseDown)
                    {
                        SendMouseButton(step.MouseButton, true);
                        Thread.Sleep(20);
                        SendMouseButton(step.MouseButton, false);
                    }
                    else
                    {
                        SendMouseButton(step.MouseButton, false);
                    }
                    break;
                case MacroStepKind.MouseMove:
                    SendMouseMove(step.X, step.Y);
                    break;
                case MacroStepKind.Delay:
                    Thread.Sleep(Math.Min(step.DelayMs, 2000));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"Error ejecutando paso de macro: {ex.Message}");
        }
    }

    private static void SendKey(int vk, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = 0,
                    dwFlags = down ? 0 : KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseButton(int button, bool down)
    {
        // 1 = izquierdo, 2 = derecho, 4 = medio (ruedita), 5 = lateral atrás (X1), 6 = lateral adelante (X2).
        uint flag;
        uint data = 0;
        switch (button)
        {
            case 1: flag = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
            case 2: flag = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case 4: flag = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case 5: flag = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = XBUTTON1; break;
            default: flag = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = XBUTTON2; break;
        }
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dwFlags = flag, mouseData = data, time = 0, dwExtraInfo = IntPtr.Zero }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseMove(int x, int y)
    {
        // Coordenadas absolutas normalizadas a 0..65535 (MOUSEEVENTF_ABSOLUTE).
        int screenW = Math.Max(1, GetSystemMetrics(0));
        int screenH = Math.Max(1, GetSystemMetrics(1));
        int absX = (int)(x * 65535.0 / (screenW - 1));
        int absY = (int)(y * 65535.0 / (screenH - 1));
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ===================== Vigilante de atajos globales =====================

    public void StartHotkeyWatcher(Action<MacroDefinition> triggered, Func<bool>? isEnabled = null)
    {
        _hotkeyTriggered = triggered;
        _isEnabled = isEnabled;
        if (_watcherThread != null && _watcherThread.IsAlive)
            return;
        _watcherStop = false;
        _watcherThread = new Thread(HotkeyLoop) { IsBackground = true, Name = "MacroHotkeyWatcher" };
        _watcherThread.Start();
    }

    public void StopHotkeyWatcher()
    {
        _watcherStop = true;
        _hotkeyTriggered = null;
        _isEnabled = null;
        _watcherThread = null;
    }

    public void UpdateArmedMacros(List<MacroDefinition> macros)
    {
        _armed = macros ?? new List<MacroDefinition>();
    }

    private void HotkeyLoop()
    {
        var previous = new Dictionary<string, bool>();
        while (!_watcherStop)
        {
            // Con las macros desactivadas NO se pollea: el hilo solo duerme y
            // re-chequea el estado cada 500 ms, sin consumir CPU en segundo plano.
            bool enabled = _isEnabled == null || _isEnabled();
            if (enabled)
            {
                foreach (var macro in _armed)
                {
                    if (macro.HotkeyVk == 0 || macro.Steps.Count == 0) continue;

                    bool pressed = HotkeyDown(macro.HotkeyVk, macro.HotkeyModifiers);
                    string key = macro.HotkeyVk + "|" + macro.HotkeyModifiers;
                    bool was = previous.TryGetValue(key, out var w) && w;
                    previous[key] = pressed;

                    // Flanco de subida: solo una vez por pulsación (no repetición).
                    if (pressed && !was)
                    {
                        try { _hotkeyTriggered?.Invoke(macro); }
                        catch { }
                    }
                }
                Thread.Sleep(50);
            }
            else
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// La combinación se considera presionada cuando la tecla está abajo Y los
    /// modificadores coinciden exactamente (los no pedidos no deben estar abajo,
    /// para que un "Ctrl + K" no dispare un atajo "K" a secas).
    /// </summary>
    private static bool HotkeyDown(int vk, int mods)
    {
        if (!IsDown(vk)) return false;
        bool ok = true;
        ok &= (mods & MOD_CONTROL) != 0 ? IsDown(VK_CONTROL) : !IsDown(VK_CONTROL);
        ok &= (mods & MOD_SHIFT) != 0 ? IsDown(VK_SHIFT) : !IsDown(VK_SHIFT);
        ok &= (mods & MOD_ALT) != 0 ? IsDown(VK_MENU) : !IsDown(VK_MENU);
        ok &= (mods & MOD_WIN) != 0 ? (IsDown(VK_LWIN) || IsDown(VK_RWIN)) : (!IsDown(VK_LWIN) && !IsDown(VK_RWIN));
        return ok;
    }
}
