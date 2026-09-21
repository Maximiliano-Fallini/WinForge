namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Configuración actual de repetición del teclado (valores de "Keyboard Response",
/// los mismos que usa Filter Keys de Windows).
/// </summary>
public sealed record KeyboardSettings(
    int IgnoreUnderMs,   // DelayBeforeAcceptance (Slow Keys)
    int RepeatDelayMs,   // AutoRepeatDelay
    int RepeatRateMs,    // AutoRepeatRate
    int BounceMs,        // BounceTime
    int Flags)
{
    /// <summary>Filter Keys activado (bit FKF_FILTERKEYSON): los valores aplican.</summary>
    public bool IsActive => (Flags & 0x1) != 0;
}

/// <summary>
/// Servicio de configuración del comportamiento de repetición del teclado.
/// Escribe los valores en el registro (HKCU\Control Panel\Accessibility\Keyboard Response)
/// y los aplica al instante vía SystemParametersInfo, como hace "Filter Keys Setter".
/// </summary>
public interface IKeyboardService
{
    /// <summary>Lee la configuración actual del registro.</summary>
    KeyboardSettings GetSettings();

    /// <summary>
    /// Estado REAL en vivo de Filter Keys (SPI_GETFILTERKEYS). Windows 11 solo
    /// aplica los valores del registro al iniciar sesión, así que puede estar
    /// "guardado" (registro) pero no "activo" (en vivo) hasta reiniciar sesión.
    /// </summary>
    bool IsActiveLive();

    /// <summary>
    /// Aplica los tres valores (ms) en vivo al instante (SPI_SETFILTERKEYS, igual que
    /// FilterKeysSetter). Si <paramref name="saveToRegistry"/> es true, además los
    /// guarda en el registro del sistema para que persistan al iniciar sesión.
    /// </summary>
    bool Apply(int ignoreUnderMs, int repeatDelayMs, int repeatRateMs, bool saveToRegistry, out string error);

    /// <summary>Restaura los valores por defecto de Windows (Filter Keys desactivado).</summary>
    bool ResetToDefaults(out string error);
}
