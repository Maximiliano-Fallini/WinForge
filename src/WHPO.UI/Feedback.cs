using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services.Interfaces;

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
    // Los pinceles viven en los recursos de la app (ThemeResource): se resuelven en
    // el tema actual, así los feedbacks acompañan al tema sin duplicar colores.
    // AccentBrush y MutedBrush están en los ThemeDictionaries (claro/oscuro): se
    // resuelven con el tema EFECTIVO de la app, no con el del sistema.
    public static SolidColorBrush AccentBrush => ThemeBrushes.Get("AccentBrush");
    public static SolidColorBrush SuccessBrush => (SolidColorBrush)Application.Current.Resources["SuccessBrush"];
    public static SolidColorBrush WarningBrush => (SolidColorBrush)Application.Current.Resources["WarningBrush"];
    public static SolidColorBrush ErrorBrush => (SolidColorBrush)Application.Current.Resources["ErrorBrush"];
    public static SolidColorBrush MutedBrush => ThemeBrushes.Get("MutedBrush");

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
    /// El texto se traduce al idioma actual (I18n.T): si no hay traducción queda igual.
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
        tb.Text = I18n.T(text);

        if (persistent)
            CancelDismiss(tb);
        else
            ScheduleDismiss(tb);
    }

    /// <summary>Operación en curso (azul).</summary>
    public static void Running(TextBlock tb, string message, bool persistent = false) => Set(tb, WithPrefix(RunningPrefix, I18n.T(message)), AccentBrush, persistent);

    /// <summary>Éxito (verde).</summary>
    public static void Success(TextBlock tb, string message, bool persistent = false) => Set(tb, WithPrefix(SuccessPrefix, I18n.T(message)), SuccessBrush, persistent);

    /// <summary>Error (rojo).</summary>
    public static void Error(TextBlock tb, string message, bool persistent = false) => Set(tb, WithPrefix(ErrorPrefix, I18n.T(message)), ErrorBrush, persistent);

    /// <summary>Aviso (ámbar, con signo de alerta).</summary>
    public static void Warning(TextBlock tb, string message, bool persistent = false) => Set(tb, WithPrefix(WarningPrefix, I18n.T(message)), WarningBrush, persistent);

    /// <summary>Información / sugerencia (ámbar).</summary>
    public static void Info(TextBlock tb, string message, bool persistent = false) => Set(tb, WithPrefix(InfoPrefix, I18n.T(message)), WarningBrush, persistent);

    /// <summary>
    /// Muestra el resultado de un comando del sistema (Core). Si el resultado trae
    /// plantilla de traducción (mensajes con valores interpolados), se traduce con
    /// I18n.T(plantilla, args); si no, se traduce el texto directo cuando tenga clave.
    /// </summary>
    public static void Result(TextBlock tb, CommandResult result, bool persistent = false)
    {
        var message = result.MessageTemplate != null
            ? I18n.T(result.MessageTemplate, result.MessageArgs ?? Array.Empty<object?>())
            : I18n.T(result.Output);
        if (result.Success)
            Success(tb, message, persistent);
        else
            Error(tb, message, persistent);
    }

    /// <summary>
    /// Agrega el prefijo de estado al mensaje SOLO si el mensaje no lo trae ya
    /// (algunas claves del diccionario incluyen el símbolo: "✓ Detenido...").
    /// Sin esto, los mensajes duplicaban el símbolo ("✓ ✓ Aplicado...").
    /// </summary>
    private static string WithPrefix(string prefix, string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;
        if (message.StartsWith(prefix, StringComparison.Ordinal))
            return message;
        return $"{prefix} {message}";
    }

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
