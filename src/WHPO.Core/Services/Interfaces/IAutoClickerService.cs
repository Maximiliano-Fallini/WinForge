using System;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio de autoclicker: genera clics con un intervalo configurable (h/m/s/ms),
/// límite opcional de clics y posición fija del cursor, con una hotkey global
/// (por defecto F6) para iniciar/detener.
/// </summary>
public interface IAutoClickerService
{
    /// <summary>True mientras el autoclicker está generando clics.</summary>
    bool IsRunning { get; }

    /// <summary>Cantidad de clics generados en la corrida actual.</summary>
    int Clicks { get; }

    /// <summary>Se dispara cuando cambia el estado (iniciando/detenido) o el contador.</summary>
    event Action? StateChanged;

    /// <summary>
    /// Inicia la generación de clics con la configuración actual. Espera
    /// <paramref name="startDelay"/> antes del primer clic (para que alcances a
    /// posicionar el cursor).
    /// </summary>
    void Start(TimeSpan interval, int? maxClicks, TimeSpan? maxDuration, TimeSpan startDelay, bool doubleClick, bool cornerStop, int? posX, int? posY);

    /// <summary>Detiene la generación de clics.</summary>
    void Stop();

    /// <summary>
    /// Registra la hotkey global (tecla + modificadores opcionales). Devuelve false
    /// si la tecla ya está en uso por otra aplicación.
    /// </summary>
    bool RegisterHotKey(uint vk, bool alt, bool ctrl, bool shift, bool win);

    /// <summary>Desregistra la hotkey global actual.</summary>
    void UnregisterHotKey();
}
