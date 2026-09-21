using LibreHardwareMonitor.Hardware;

namespace WHPO.Core.Services.Fan;

/// <summary>Modo de control de un canal, sin depender del backend que lo maneje.</summary>
internal enum FanWriteMode
{
    /// <summary>Sin información (el canal no se tocó en esta sesión).</summary>
    Undefined,

    /// <summary>Lo controla el BIOS (placa) o el driver (GPU).</summary>
    Default,

    /// <summary>Lo controla la app escribiendo el duty.</summary>
    Software
}

/// <summary>
/// Escritura del duty de UN canal, abstracta del backend. Existen dos familias:
///
///  - <see cref="LhmFanWriteHandle"/>: envuelve el IControl de LibreHardwareMonitor,
///    que es el camino que ya usan el chip SuperIO de la placa, NVIDIA (NVAPI) y AMD
///    cuando LHM arma el canal de control (ADL Overdrive5).
///  - Los backends propios de GPU (IGCL de Intel, ADL de AMD), para los canales que
///    LHM publica SIN escritura: una Intel Arc solo expone RPM, y una AMD puede
///    quedar en RPM si la lectura que LHM necesita para activar el canal falla.
///
/// El motor de curvas, el control manual, la calibración y "Todos al automático"
/// escriben SIEMPRE por acá: sumar un backend nuevo no toca la UI ni la lógica de
/// curvas, solo agrega canales controlables a la lista que la pestaña ya dibuja.
/// </summary>
internal interface IFanWriteHandle
{
    /// <summary>Modo actual del canal (lo recuerda el backend, no el hardware).</summary>
    FanWriteMode Mode { get; }

    /// <summary>
    /// Duty real leído del hardware (0-100), o null si el backend no puede leerlo.
    /// Se usa para mostrar el "% de uso" de los canales que solo existen por backend.
    /// </summary>
    float? ReadPercent();

    /// <summary>
    /// Pone el canal en modo software y escribe el duty (0-100). Devuelve false si el
    /// backend rechazó la escritura, para que el motor de curvas no dé por aplicado
    /// un valor que no llegó al hardware.
    /// </summary>
    bool SetSoftware(float percent);

    /// <summary>Devuelve el canal al control del BIOS / del driver (auto).</summary>
    bool SetDefault();
}

/// <summary>
/// Handle sobre el IControl de LibreHardwareMonitor: la implementación de siempre.
/// LHM no expone errores (sus métodos son void y tragan la excepción del vendor),
/// así que se asume éxito y cualquier falla la reporta LHM en su propio camino.
/// </summary>
internal sealed class LhmFanWriteHandle : IFanWriteHandle
{
    private readonly IControl _control;

    public LhmFanWriteHandle(IControl control) => _control = control;

    public FanWriteMode Mode => _control.ControlMode switch
    {
        ControlMode.Software => FanWriteMode.Software,
        ControlMode.Default => FanWriteMode.Default,
        _ => FanWriteMode.Undefined
    };

    public float? ReadPercent() => _control.SoftwareValue;

    public bool SetSoftware(float percent)
    {
        _control.SetSoftware(percent);
        return true;
    }

    public bool SetDefault()
    {
        _control.SetDefault();
        return true;
    }
}
