using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// "Modo juego de WinForge (BETA)": al lanzar un juego, pausa
/// Windows Update, detiene servicios de mantenimiento/diagnóstico (SysMain,
/// DiagTrack, etc.) y baja la prioridad de procesos en segundo plano. Al cerrar
/// el juego, restaura el estado previo exacto: solo re-arranca los servicios que
/// estaban corriendo y devuelve la prioridad original a los procesos cambiados.
/// Si el switch está desactivado, no hace nada. Toda acción se registra en el
/// log (visible cuando «Logs de desarrollo» está activo).
/// </summary>
public interface IGameBoostService
{
    /// <summary>True si la optimización al iniciar un juego está activada.</summary>
    bool IsEnabled { get; }

    /// <summary>Activa/desactiva la optimización (persistida en settings). Al desactivar con un boost activo, restaura.</summary>
    void SetEnabled(bool enabled);

    /// <summary>Aplica la optimización (snapshot del estado previo incluido). No hace nada si el switch está desactivado.</summary>
    Task ApplyAsync();

    /// <summary>Restaura el estado previo (servicios y prioridades). No hace nada si no hay boost activo.</summary>
    Task RestoreAsync();

    /// <summary>Lista de procesos en segundo plano a los que se aplica el boost (configurable; vacía = lista por defecto).</summary>
    List<string> GetBackgroundProcesses();

    /// <summary>Persiste la lista de procesos en segundo plano del boost.</summary>
    void SetBackgroundProcesses(List<string> processes);

    /// <summary>Lista por defecto de procesos en segundo plano (la que aplica si no hay configuración guardada).</summary>
    List<string> GetDefaultBackgroundProcesses();
}
