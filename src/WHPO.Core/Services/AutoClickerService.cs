using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Genera clics de ratón con intervalo configurable (h/m/s/ms), límite opcional
/// de clics y posición fija. La hotkey global (F6 por defecto, configurable) se
/// implementa con un hook de teclado de bajo nivel (WH_KEYBOARD_LL), que no
/// requiere una ventana ni un pump de mensajes dedicado.
/// </summary>
public sealed class AutoClickerService : IAutoClickerService
{
    private readonly ILoggingService _log;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Thread? _hookThread;
    private IntPtr _hookId = IntPtr.Zero;
    private HookProc? _hookProc; // mantener referencia para que el GC no lo recolecte

    // Configuración de la hotkey
    private uint _hotkeyVk;
    private bool _hotkeyAlt;
    private bool _hotkeyCtrl;
    private bool _hotkeyShift;
    private bool _hotkeyWin;

    public bool IsRunning { get; private set; }
    public int Clicks { get; private set; }

    public event Action? StateChanged;

    public AutoClickerService(ILoggingService log)
    {
        _log = log;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    // ===================== Control =====================

    public void Start(TimeSpan interval, int? maxClicks, TimeSpan? maxDuration, TimeSpan startDelay, bool doubleClick, bool cornerStop, int? posX, int? posY)
    {
        lock (_sync)
        {
            if (IsRunning) return;

            // Intervalo mínimo razonable (1 ms) para no quemar la CPU con un loop apretado.
            if (interval < TimeSpan.FromMilliseconds(1))
                interval = TimeSpan.FromMilliseconds(1);
            if (startDelay < TimeSpan.Zero)
                startDelay = TimeSpan.Zero;

            // Guardar la configuración para que la hotkey pueda reiniciar con los mismos valores.
            LastInterval = interval;
            LastMaxClicks = maxClicks;
            LastMaxDuration = maxDuration;
            LastStartDelay = startDelay;
            LastDoubleClick = doubleClick;
            LastCornerStop = cornerStop;
            LastPosX = posX;
            LastPosY = posY;

            Clicks = 0;
            _cts = new CancellationTokenSource();
            IsRunning = true;
            RaiseStateChanged();

            var token = _cts.Token;
            _ = Task.Run(() => ClickLoop(interval, maxClicks, maxDuration, startDelay, doubleClick, cornerStop, posX, posY, token));
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!IsRunning) return;
            IsRunning = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            RaiseStateChanged();
        }
    }

    private void ClickLoop(TimeSpan interval, int? maxClicks, TimeSpan? maxDuration, TimeSpan startDelay, bool doubleClick, bool cornerStop, int? posX, int? posY, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Delay inicial: esperar sin generar clics (se puede cancelar con la hotkey
            // o con el corner stop).
            if (startDelay > TimeSpan.Zero)
            {
                var delayStart = Stopwatch.GetTimestamp();
                while (!token.IsCancellationRequested)
                {
                    if (cornerStop && IsNearScreenEdge())
                    {
                        Stop();
                        return;
                    }
                    var elapsed = Stopwatch.GetElapsedTime(delayStart);
                    if (elapsed >= startDelay) break;
                    Thread.Sleep(25);
                }
                if (token.IsCancellationRequested) return;
                sw.Restart();
            }

            while (!token.IsCancellationRequested)
            {
                if (maxClicks.HasValue && Clicks >= maxClicks.Value)
                {
                    Stop();
                    return;
                }
                if (maxDuration.HasValue && sw.Elapsed >= maxDuration.Value)
                {
                    Stop();
                    return;
                }

                // Corner stop: si el cursor toca un borde de la pantalla, detener.
                if (cornerStop && IsNearScreenEdge())
                {
                    Stop();
                    return;
                }

                sw.Restart();
                PerformClick(posX, posY, doubleClick);
                Clicks++;
                RaiseStateChanged();

                // Esperar el resto del intervalo con precisión aproximada por Stopwatch.
                var elapsed = sw.Elapsed;
                var remaining = interval - elapsed;
                if (remaining > TimeSpan.Zero)
                    Thread.Sleep(remaining);
            }
        }
        catch (OperationCanceledException)
        {
            // detención normal
        }
        catch (Exception ex)
        {
            _log.LogError($"AutoClicker: error en el loop: {ex.Message}", ex);
            Stop();
        }
    }

    private static void PerformClick(int? posX, int? posY, bool doubleClick)
    {
        // Mover el cursor solo si se fijó una posición.
        if (posX.HasValue && posY.HasValue)
            SetCursorPos(posX.Value, posY.Value);

        // Eventos de botón izquierdo (down + up) con SendInput.
        var down = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } }
        };
        var up = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } }
        };
        var inputs = new[] { down, up };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        if (doubleClick)
        {
            Thread.Sleep(30); // pausa estándar entre clics de un doble clic
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
    }

    private static bool IsNearScreenEdge()
    {
        if (!GetCursorPos(out var pt)) return false;
        const int margin = 2;
        var w = GetSystemMetrics(SM_CXSCREEN);
        var h = GetSystemMetrics(SM_CYSCREEN);
        return pt.X <= margin || pt.Y <= margin || pt.X >= w - margin || pt.Y >= h - margin;
    }

    // ===================== Hotkey global =====================

    public bool RegisterHotKey(uint vk, bool alt, bool ctrl, bool shift, bool win)
    {
        UnregisterHotKey();

        _hotkeyVk = vk;
        _hotkeyAlt = alt;
        _hotkeyCtrl = ctrl;
        _hotkeyShift = shift;
        _hotkeyWin = win;

        // El hook se instala en un thread propio: el callback del hook se ejecuta
        // en el thread que lo instaló, y este thread debe mantener un pump de mensajes.
        var started = new ManualResetEventSlim(false);
        Exception? installError = null;

        _hookThread = new Thread(() =>
        {
            try
            {
                _hookProc = HookCallback;
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
                if (_hookId == IntPtr.Zero)
                {
                    installError = new InvalidOperationException($"SetWindowsHookEx falló (código {Marshal.GetLastWin32Error()})");
                    started.Set();
                    return;
                }
            }
            catch (Exception ex)
            {
                installError = ex;
                started.Set();
                return;
            }
            started.Set();

            // Pump de mensajes para que el hook de bajo nivel reciba eventos.
            var msg = new MSG();
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        })
        { IsBackground = true, Name = "AutoClickerHotkey" };
        _hookThread.Start();

        started.Wait(2000);
        if (installError != null)
        {
            _log.LogWarning($"AutoClicker: no se pudo instalar la hotkey: {installError.Message}");
            _hookId = IntPtr.Zero;
            return false;
        }
        if (_hookId == IntPtr.Zero)
            return false;

        _log.LogInfo($"AutoClicker: hotkey registrada (VK {vk})");
        return true;
    }

    public void UnregisterHotKey()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        // Terminar el pump del thread del hook (PostThreadMessage WM_QUIT).
        if (_hookThread != null && _hookThread.IsAlive)
        {
            PostThreadMessage((uint)_hookThread.ManagedThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread = null;
        }
        _hookProc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var isKeyDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;

            if (isKeyDown && data.vkCode == _hotkeyVk && ModifiersMatch())
            {
                // Toggle: si está corriendo, detener; si no, iniciar con la configuración actual.
                if (IsRunning) Stop();
                else Start(LastInterval, LastMaxClicks, LastMaxDuration, LastStartDelay, LastDoubleClick, LastCornerStop, LastPosX, LastPosY);
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool ModifiersMatch()
    {
        bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        bool win = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
        return alt == _hotkeyAlt && ctrl == _hotkeyCtrl && shift == _hotkeyShift && win == _hotkeyWin;
    }

    // Última configuración para que la hotkey pueda reiniciar con los mismos valores.
    private TimeSpan LastInterval = TimeSpan.FromMilliseconds(100);
    private int? LastMaxClicks;
    private TimeSpan? LastMaxDuration;
    private TimeSpan LastStartDelay = TimeSpan.FromSeconds(3);
    private bool LastDoubleClick;
    private bool LastCornerStop;
    private int? LastPosX;
    private int? LastPosY;

    // ===================== P/Invoke =====================

    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_QUIT = 0x0012;
    private const int VK_MENU = 0x12;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
