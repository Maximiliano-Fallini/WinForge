namespace WHPO_UI;

/// <summary>
/// Contrato para páginas con sondeo de UI (timers que refrescan lo visible).
/// MainWindow lo usa al ocultar/mostrar la ventana (bandeja): pausa real de los
/// timers (Stop) en vez de dejarlos tickeando con un guard barato, y reanudación
/// al volver — igual que ya hacen al navegar (OnNavigatedFrom/To).
/// Solo cubre timers DE PÁGINA (estadísticas, refresh). Los features de proceso
/// (curvas, AutoCleanup, timer resolution, WLAN keep-alive, overlay, GameBoost)
/// NO se pausan: se apagan con su propio switch.
/// </summary>
public interface IBackgroundPausable
{
    /// <summary>Pausa los timers de sondeo de la página (Stop real).</summary>
    void PauseBackgroundTimers();

    /// <summary>Reanuda los timers si la página está activa y visible.</summary>
    void ResumeBackgroundTimers();
}