using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WHPO_UI;

/// <summary>
/// Utilidad común para los feedbacks de la app: paleta de colores estándar,
/// prefijos de estado, comportamiento de colapso (texto vacío = elemento oculto)
/// y autodesvanecido: los mensajes de resultado se ocultan solos a los 4 segundos.
/// Los estados vivos (por ejemplo "Activo", contadores en vivo, progreso de un test)
/// se muestran con persistent: true para que no desaparezcan.
/// </summary>
public static class Feedback
{
    // Paleta estándar (misma en toda la app, legible en tema claro y oscuro)
    public static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xC2, 0xFF));
    public static readonly SolidColorBrush SuccessBrush = new(Windows.UI.Color.FromArgb(255, 0x4C, 0xAF, 0x50));
    public static readonly SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(255, 0xFF, 0xC1, 0x07));
    public static readonly SolidColorBrush ErrorBrush = new(Windows.UI.Color.FromArgb(255, 0xF0, 0x61, 0x6D));
    public static readonly SolidColorBrush MutedBrush = new(Windows.UI.Color.FromArgb(255, 0x9A, 0xA0, 0xA6));

    // Prefijos estándar
    public const string RunningPrefix = "▶";
    public const string SuccessPrefix = "✓";
    public const string ErrorPrefix = "✗";
    public const string InfoPrefix = "ℹ";
    public const string WarningPrefix = "⚠";

    // Tiempo que un feedback de resultado queda visible antes de ocultarse solo
    private const double DismissAfterSeconds = 4.0;

    // Entradas pendientes de ocultar: se barren con un timer del dispatcher de la UI.
    private static readonly List<(TextBlock Tb, DateTime ExpiresAt)> _pendingDismiss = new();
    private static DispatcherQueueTimer? _dismissSweeper;

    /// <summary>
    /// Muestra un mensaje con color opcional. Con texto vacío (o nulo) colapsa el
    /// elemento para no dejar espacio muerto en la card. Salvo persistent: true,
    /// el mensaje se oculta solo a los 4 segundos.
    /// </summary>
    public static void Set(TextBlock tb, string? text, SolidColorBrush? brush = null, bool persistent = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            tb.Visibility = Visibility.Collapsed;
            CancelDismiss(tb);
            return;
        }
        tb.Visibility = Visibility.Visible;
        if (brush != null)
            tb.Foreground = brush;
        tb.Text = text;

        if (persistent)
            CancelDismiss(tb);
        else
            ScheduleDismiss(tb);
    }

    /// <summary>Operación en curso (azul).</summary>
    public static void Running(TextBlock tb, string message, bool persistent = false) => Set(tb, $"{RunningPrefix} {message}", AccentBrush, persistent);

    /// <summary>Éxito (verde).</summary>
    public static void Success(TextBlock tb, string message, bool persistent = false) => Set(tb, $"{SuccessPrefix} {message}", SuccessBrush, persistent);

    /// <summary>Error (rojo).</summary>
    public static void Error(TextBlock tb, string message, bool persistent = false) => Set(tb, $"{ErrorPrefix} {message}", ErrorBrush, persistent);

    /// <summary>Aviso (ámbar, con signo de alerta).</summary>
    public static void Warning(TextBlock tb, string message, bool persistent = false) => Set(tb, $"{WarningPrefix} {message}", WarningBrush, persistent);

    /// <summary>Información / sugerencia (ámbar).</summary>
    public static void Info(TextBlock tb, string message, bool persistent = false) => Set(tb, $"{InfoPrefix} {message}", WarningBrush, persistent);

    // ===================== Autodesvanecido =====================

    private static void ScheduleDismiss(TextBlock tb)
    {
        _pendingDismiss.RemoveAll(e => e.Tb == tb);
        _pendingDismiss.Add((tb, DateTime.UtcNow.AddSeconds(DismissAfterSeconds)));
        EnsureSweeper(tb);
    }

    private static void CancelDismiss(TextBlock tb)
    {
        _pendingDismiss.RemoveAll(e => e.Tb == tb);
    }

    private static void EnsureSweeper(TextBlock tb)
    {
        if (_dismissSweeper != null || tb.DispatcherQueue == null) return;
        _dismissSweeper = tb.DispatcherQueue.CreateTimer();
        _dismissSweeper.Interval = TimeSpan.FromMilliseconds(500);
        _dismissSweeper.Tick += (s, e) => SweepDismissals();
        _dismissSweeper.Start();
    }

    private static void SweepDismissals()
    {
        var now = DateTime.UtcNow;
        for (int i = _pendingDismiss.Count - 1; i >= 0; i--)
        {
            var (tb, expiresAt) = _pendingDismiss[i];
            if (now >= expiresAt)
            {
                _pendingDismiss.RemoveAt(i);
                if (tb.Visibility != Visibility.Collapsed)
                    tb.Visibility = Visibility.Collapsed;
            }
        }

        // Sin entradas pendientes, apagar el barrido para no gastar ciclos de UI.
        if (_pendingDismiss.Count == 0)
        {
            _dismissSweeper?.Stop();
            _dismissSweeper = null;
        }
    }
}
