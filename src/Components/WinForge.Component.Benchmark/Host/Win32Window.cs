using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinForge.Component.Benchmark.Host;

/// <summary>
/// Ventana Win32 propia (sin WinForms ni XAML): la escena necesita un HWND al que atar su
/// swapchain, y el HUD una ventana chica siempre arriba. Las dos son esto.
///
/// Todas las ventanas viven en el hilo de render: el mensaje va al WndProc del hilo, que lo
/// reparte por HWND a la instancia correspondiente (ver <see cref="Router"/>).
/// </summary>
internal sealed class Win32Window : IDisposable
{
    private const uint CS_HREDRAW = 0x0002;
    private const uint CS_VREDRAW = 0x0001;
    private const long WS_POPUP = unchecked((long)0x80000000);
    private const long WS_VISIBLE = 0x10000000;
    private const long WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const long WS_CLIPCHILDREN = 0x02000000;

    // El delegate tiene que quedar VIVO mientras la ventana exista: si el GC lo colecta,
    // Windows queda con un puntero a código liberado y el proceso se cae.
    private static readonly NativeMethods.WndProc WindowProcedure = Dispatch;
    private static readonly Dictionary<nint, Win32Window> Router = new();
    private static readonly object RouterLock = new();

    private readonly string _className;
    private bool _classRegistered;
    private bool _disposed;

    public nint Handle { get; private set; }
    public bool Visible { get; private set; }
    public int ClientWidth { get; private set; }
    public int ClientHeight { get; private set; }

    public event Action? CloseRequested;
    public event Action<int, int>? Resized;
    public event Action<int>? KeyPressed;

    /// <summary>Dibujo del HUD: recibe el contexto ya listo dentro de BeginPaint/EndPaint.</summary>
    public event Action<nint>? Paint;

    /// <param name="width">Ancho del CLIENTE en una ventana CON bordes (el render mide esto) y de la
    /// ventana entera en una sin bordes, donde las dos cosas son lo mismo.</param>
    /// <param name="x">Posición horizontal; null = la de siempre (0 sin bordes, 80 con bordes).</param>
    public Win32Window(string title, int width, int height, bool borderless, bool topMost, bool visible, bool clickThrough,
        int? x = null, int? y = null)
    {
        _className = "WinForgeBenchmark_" + Guid.NewGuid().ToString("N")[..8];
        RegisterClass();

        long style = borderless ? (WS_POPUP | WS_CLIPCHILDREN) : (WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN);

        // CreateWindowEx pide el tamaño de la VENTANA (cliente + marco), y lo que importa acá es el
        // cliente: es lo que renderiza el backend, lo que mide el informe y lo que el usuario elige
        // cuando pide una resolución. Sin esta corrección, "1920×1080" renderizaba 1904×1041 y la
        // resolución elegida dejaba de ser la resolución de la corrida. Sin bordes no hay marco que
        // compensar (pantalla completa y el HUD van por ese camino).
        if (!borderless)
        {
            var frame = new NativeMethods.RECT { Right = width, Bottom = height };
            // Estilo extendido 0: esta rama es la ventana CON bordes del benchmark, que no usa borde
            // especial (la declaración de extendedStyle viene después, y no aporta al marco).
            if (NativeMethods.AdjustWindowRectEx(ref frame, style, false, 0))
            {
                width = frame.Right - frame.Left;
                height = frame.Bottom - frame.Top;
            }
        }

        if (visible) style |= WS_VISIBLE;

        long extendedStyle = 0;
        if (topMost) extendedStyle |= NativeMethods.WS_EX_TOPMOST;
        if (borderless) extendedStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        if (clickThrough) extendedStyle |= NativeMethods.WS_EX_NOACTIVATE;
        // OJO: NOACTIVATE en la ventana de la ESCENA (fullscreenStyle) se eliminó a propósito:
        // sin foco de teclado no recibe WM_KEYDOWN y el Esc de emergencia no llega nunca.
        // La ventana de la escena puede y debe robar el foco: es lo que el usuario pidió al correr.

        // La posición se pasa SIEMPRE que se sepa: en un escritorio multimonitor las coordenadas
        // pueden ser negativas (monitor a la izquierda o arriba del primario), así que "0,0" no
        // es "donde está el usuario": es la esquina del primario.
        int left = x ?? (borderless ? 0 : 80);
        int top = y ?? (borderless ? 0 : 60);
        Handle = NativeMethods.CreateWindowExW(
            extendedStyle, _className, title, style, left, top, width, height, 0, 0, 0, 0);
        if (Handle == 0)
        {
            throw new InvalidOperationException(
                $"No se pudo crear la ventana de la escena (error {Marshal.GetLastWin32Error()}).");
        }

        if (clickThrough)
        {
            // Click-through REAL: sin WS_EX_LAYERED | WS_EX_TRANSPARENT el mouse queda atrapado
            // sobre la franja (el parámetro solo cambiaba el NOACTIVATE). Con layered+transparent
            // los clicks atraviesan a la escena y el HUD no roba el foco ni el cursor.
            long exStyle = NativeMethods.GetWindowLongW(Handle, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLongW(Handle, NativeMethods.GWL_EXSTYLE,
                exStyle | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT);
            // FALTA IMPERDONABLE de la versión anterior: una ventana LAYERED sin atributos no se
            // dibuja NUNCA (lo define Win32, no es un detalle): por eso el overlay desapareció.
            // Alpha 255 + color key 0 = opacidad completa, pintado normal, clicks atravesando.
            NativeMethods.SetLayeredWindowAttributes(Handle, 0, 255, NativeMethods.LWA_ALPHA);
        }

        lock (RouterLock) Router[Handle] = this;
        Visible = visible;
        RefreshClientSize();

        if (visible) NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOW);
    }

    private void RegisterClass()
    {
        var windowClass = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            hbrBackground = 0,   // el fondo lo pinta la escena (D3D) o el HUD (GDI+)
            // El icono del componente (ver BenchmarkIcon): los dos tamaños los pide Windows, no
            // nosotros —el grande para Alt+Tab y la barra de tareas, el chico para el título—.
            hIcon = BenchmarkIcon.Large,
            hIconSm = BenchmarkIcon.Small,
            lpszClassName = _className
        };
        if (NativeMethods.RegisterClassExW(ref windowClass) == 0)
        {
            throw new InvalidOperationException(
                $"No se pudo registrar la clase de ventana (error {Marshal.GetLastWin32Error()}).");
        }
        _classRegistered = true;
    }

    public void RefreshClientSize()
    {
        if (NativeMethods.GetClientRect(Handle, out var rect))
        {
            ClientWidth = Math.Max(1, rect.Right - rect.Left);
            ClientHeight = Math.Max(1, rect.Bottom - rect.Top);
        }
    }

    public void SetTitle(string title) => NativeMethods.SetWindowTextW(Handle, title);

    /// <summary>Hace visible una ventana que nació oculta (el HUD se ubica antes de mostrarse).</summary>
    public void Show()
    {
        if (Handle != 0) NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOW);
        Visible = true;
    }

    public void Move(int x, int y, int width, int height) =>
        NativeMethods.SetWindowPos(Handle, 0, x, y, width, height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

    /// <summary>
    /// Vuelve a afirmar el TOPMOST. En pantalla completa lo reafirmamos cada medio segundo:
    /// cualquier otra ventana topmost creada DESPUÉS (otro overlay, una notificación) se pone
    /// encima y la franja de métricas desaparece de la vista sin estar rota.
    /// </summary>
    public void RestickTopMost()
    {
        if (Handle != 0) NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public void Invalidate() => NativeMethods.InvalidateRect(Handle, 0, false);

    public (int X, int Y) ScreenOrigin()
    {
        var point = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(Handle, ref point)) return (0, 0);
        return (point.X, point.Y);
    }

    // =====================================================================
    // Mensajes
    // =====================================================================

    private static nint Dispatch(nint hWnd, uint message, nint wParam, nint lParam)
    {
        Win32Window? window;
        lock (RouterLock) Router.TryGetValue(hWnd, out window);
        return window != null
            ? window.HandleMessage(message, wParam, lParam)
            : NativeMethods.DefWindowProcW(hWnd, message, wParam, lParam);
    }

    private nint HandleMessage(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_CLOSE:
                CloseRequested?.Invoke();
                return 0;

            case NativeMethods.WM_SIZE:
            {
                int width = (int)(lParam & 0xFFFF);
                int height = (int)((lParam >> 16) & 0xFFFF);
                ClientWidth = Math.Max(1, width);
                ClientHeight = Math.Max(1, height);
                if (ClientWidth > 1 && ClientHeight > 1) Resized?.Invoke(ClientWidth, ClientHeight);
                return 0;
            }

            case NativeMethods.WM_KEYDOWN:
            case NativeMethods.WM_SYSKEYDOWN:
                KeyPressed?.Invoke((int)wParam);
                return 0;

            case NativeMethods.WM_ERASEBKGND:
                // El fondo lo pinta el HUD en WM_PAINT: borrar acá produce parpadeo.
                return 1;

            case NativeMethods.WM_PAINT:
            {
                nint deviceContext = NativeMethods.BeginPaint(Handle, out var paint);
                try
                {
                    Paint?.Invoke(deviceContext);
                }
                finally
                {
                    NativeMethods.EndPaint(Handle, ref paint);
                }
                return 0;
            }
        }
        return NativeMethods.DefWindowProcW(Handle, message, wParam, lParam);
    }

    /// <summary>
    /// Procesa los mensajes pendientes del hilo. Se llama desde el bucle de la escena: es un
    /// peek (no bloquea), así el render no se detiene esperando mensajes.
    /// </summary>
    public static void PumpMessages()
    {
        while (NativeMethods.PeekMessageW(out var message, 0, 0, 0, NativeMethods.PM_REMOVE))
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
    }

    /// <summary>Tamaño del monitor principal (para los modos de pantalla completa).</summary>
    public static (int Width, int Height) PrimaryScreenSize() =>
        (Math.Max(640, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN)),
         Math.Max(480, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)));

    /// <summary>
    /// Rectángulo del monitor donde está el cursor. Los modos de pantalla completa salen ahí y no
    /// siempre en el primario: el componente vive en el AssemblyLoadContext de la app (no es su UI)
    /// y no tiene su ventana, así que el cursor es el único dato fiable de dónde está el usuario
    /// — y es el monitor que está mirando cuando aprieta Iniciar.
    /// </summary>
    public static (int X, int Y, int Width, int Height) MonitorUnderCursor()
    {
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (monitor != 0 && NativeMethods.GetMonitorInfoW(monitor, ref info))
            {
                // rcMonitor y no rcWork: la pantalla completa sin bordes tapa también la barra de tareas.
                return (info.rcMonitor.Left, info.rcMonitor.Top,
                        Math.Max(640, info.rcMonitor.Right - info.rcMonitor.Left),
                        Math.Max(480, info.rcMonitor.Bottom - info.rcMonitor.Top));
            }
        }

        // Sin monitor (caso raro): el primario, que es lo que hacía la versión anterior.
        var (width, height) = PrimaryScreenSize();
        return (0, 0, width, height);
    }

    /// <summary>
    /// Área ÚTIL del monitor donde está el cursor (sin la barra de tareas). Es con ESTA que se encaja
    /// una resolución de ventana pedida a mano: una ventana con bordes del tamaño del monitor entero
    /// deja el título arriba y el borde de abajo fuera de la pantalla.
    /// </summary>
    public static (int Width, int Height) WorkAreaUnderCursor()
    {
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (monitor != 0 && NativeMethods.GetMonitorInfoW(monitor, ref info))
            {
                return (Math.Max(640, info.rcWork.Right - info.rcWork.Left),
                        Math.Max(480, info.rcWork.Bottom - info.rcWork.Top));
            }
        }

        return PrimaryScreenSize();
    }

    /// <summary>Mantiene la pantalla y el equipo despiertos mientras dura una corrida.</summary>
    public static void KeepAwake() => NativeMethods.SetThreadExecutionState(
        NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED | NativeMethods.ES_DISPLAY_REQUIRED);

    /// <summary>Suelta el pedido de mantener despierto (se llama SIEMPRE al terminar la corrida).</summary>
    public static void ReleaseAwake() => NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Handle != 0)
        {
            lock (RouterLock) Router.Remove(Handle);
            NativeMethods.DestroyWindow(Handle);
            Handle = 0;
        }
        if (_classRegistered)
        {
            NativeMethods.UnregisterClassW(_className, 0);
            _classRegistered = false;
        }
    }
}
