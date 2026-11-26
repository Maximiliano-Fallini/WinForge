using System;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Detector de unidades conectadas en caliente (USB, discos externos, etc.).
/// Avisa cuándo aparece una unidad lógica nueva en el sistema.
/// </summary>
public interface IDriveWatcherService
{
    /// <summary>
    /// Se dispara cuando se conecta una unidad nueva (ej: "E:\").
    /// Llega desde un thread de WMI: el consumidor debe marshalear al hilo de UI.
    /// </summary>
    event Action<string>? DriveArrived;

    /// <summary>
    /// Arranca la suscripción a los eventos de WMI (idempotente: si ya está
    /// activo no hace nada). Si la suscripción falla, queda inactivo y los
    /// errores se registran en el log.
    /// </summary>
    void EnsureStarted();

    /// <summary>
    /// Corta la suscripción a los eventos de WMI y libera el watcher. Idempotente.
    /// Se usa al salir de la página que lo necesita: la query WITHIN 2 no debe quedar
    /// sondeando para siempre por una lista que nadie está mirando. Un
    /// <see cref="EnsureStarted"/> posterior lo vuelve a levantar.
    /// </summary>
    void Stop();
}
