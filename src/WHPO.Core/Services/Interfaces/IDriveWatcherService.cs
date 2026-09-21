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
}
