using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio de macros del teclado: graba secuencias de teclas y clics del mouse
/// con sus tiempos y las reproduce (con atajo global opcional). Funciona como
/// un macro-grabador clásico (estilo MacroGamer): la grabación corre en segundo
/// plano y no depende de que la app tenga el foco.
/// </summary>
public interface IMacroService
{
    /// <summary>Macros guardadas (persistidas en disco, %LocalAppData%\WHPO\macros.json).</summary>
    List<MacroDefinition> Load();

    /// <summary>Persiste la lista completa de macros.</summary>
    void Save(List<MacroDefinition> macros);

    /// <summary>
    /// Empieza a grabar teclas y mouse en segundo plano. Se detiene con
    /// StopRecording() o presionando la tecla stopKeyVk (F9 por defecto).
    /// onStep se invoca en vivo por cada evento capturado (para mostrarlos en la
    /// UI mientras se graba); al terminar se invoca completed (thread de fondo)
    /// con la lista completa. captureMouse: graba clics del mouse.
    /// Los pasos se graban SIN delay (DelayMs = 0): el delay entre eventos lo
    /// define la UI (un valor fijo, modificable) al guardar la macro.
    /// </summary>
    void StartRecording(int stopKeyVk, bool captureMouse,
        Action<MacroStep>? onStep, Action<List<MacroStep>> completed);

    /// <summary>Detiene la grabación actual (si hay una).</summary>
    void StopRecording();

    /// <summary>true mientras hay una grabación en curso.</summary>
    bool IsRecording { get; }

    /// <summary>
    /// Reproduce la macro completa (LoopCount veces; -1 = infinito) hasta que se
    /// cancela. Los pasos se envían con SendInput, respetando los delays grabados.
    /// </summary>
    Task PlayAsync(MacroDefinition macro, CancellationToken ct);

    /// <summary>
    /// Arranca (o reemplaza el callback de) el vigilante de atajos globales:
    /// cada ~50 ms revisa los atajos de las macros armadas y dispara triggered
    /// en cada activación (flanco de subida). El callback corre en thread de fondo.
    /// Si se pasa isEnabled, el vigilante SOLO pollea mientras devuelva true
    /// (con las macros desactivadas duerme sin consumir CPU).
    /// </summary>
    void StartHotkeyWatcher(Action<MacroDefinition> triggered, Func<bool>? isEnabled = null);

    /// <summary>Detiene el vigilante de atajos globales.</summary>
    void StopHotkeyWatcher();

    /// <summary>
    /// Actualiza la lista de macros que el vigilante de atajos monitorea
    /// (solo las que tienen atajo y pasos). Llamar tras cargar/guardar.
    /// </summary>
    void UpdateArmedMacros(List<MacroDefinition> macros);
}

/// <summary>Tipos de paso de una macro.</summary>
public enum MacroStepKind { Key, MouseButton, MouseMove, Delay }

/// <summary>
/// Un paso de la macro. Un evento de tecla/clic representa la pulsación completa
/// (presionar+soltar); los delays entre eventos son pasos explícitos (Kind == Delay)
/// cuyo valor va en DelayMs. En macros viejas, DelayMs de un evento es el tiempo a
/// esperar DESPUÉS de ejecutarlo.
/// </summary>
public record MacroStep(
    MacroStepKind Kind,
    int KeyCode,       // código VK (para Kind == Key)
    bool KeyDown,      // para Key: true = presionar, false = soltar
    int MouseButton,   // 1 = izquierdo, 2 = derecho, 4 = medio (para MouseButton)
    bool MouseDown,    // para MouseButton: true = presionar, false = soltar
    int X,             // coordenadas absolutas de pantalla (MouseMove / MouseButton)
    int Y,
    int DelayMs
);

/// <summary>
/// Definición de una macro. LoopCount: 0 o 1 = una vez, N = N veces, -1 = infinito.
/// HotkeyVk 0 = sin atajo; HotkeyModifiers: bits MOD_* (1=Alt, 2=Ctrl, 4=Shift, 8=Win).
/// </summary>
public record MacroDefinition(
    string Name,
    int HotkeyVk,
    int HotkeyModifiers,
    int LoopCount,
    List<MacroStep> Steps
);
