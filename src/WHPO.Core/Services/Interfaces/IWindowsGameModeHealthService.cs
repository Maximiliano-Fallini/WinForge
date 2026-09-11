using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>Estado de salud del Modo Juego de Windows.</summary>
public enum WindowsGameModeHealth
{
    /// <summary>Todo listo: el Modo Juego se activará correctamente al iniciar un juego.</summary>
    Ok = 0,
    /// <summary>Solo está deshabilitado por reglas del registro (estilo AtlasOS): reactivable al instante.</summary>
    DisabledByRules = 1,
    /// <summary>Huérfano: faltan archivos/componentes del Modo Juego (Appx XboxGamingOverlay o GameBarPresenceWriter.dll). No se puede activar por registro.</summary>
    Orphaned = 2,
    /// <summary>No se pudo determinar el estado (error de consulta).</summary>
    Unknown = 3
}

/// <summary>Detalle del chequeo de salud del Modo Juego de Windows.</summary>
public sealed record WindowsGameModeHealthInfo(
    WindowsGameModeHealth Status,
    /// <summary>Estado del interruptor maestro (AutoGameModeEnabled/AllowAutoGameMode). Null = clave ausente (habilitado por defecto).</summary>
    bool? MasterSwitchEnabled,
    /// <summary>Paquete Appx Microsoft.XboxGamingOverlay presente (infra de Game Bar / Modo Juego).</summary>
    bool GameOverlayPackagePresent,
    /// <summary>System32\GameBarPresenceWriter.dll presente.</summary>
    bool GameBarPresenceWriterPresent,
    /// <summary>Resumen legible del estado (español, pasable por I18n.T).</summary>
    string Summary,
    /// <summary>Detalle técnico por componente (para el tooltip/log).</summary>
    IReadOnlyList<(string Component, bool Present, string Detail)> Checks);

/// <summary>
/// Salud del Modo Juego de Windows: verifica que se vaya a activar correctamente
/// y repara el caso "solo deshabilitado" (reglas de registro, como AtlasOS).
/// También asegura que el Modo Juego esté activo DURANTE la partida, tomando
/// snapshot del valor previo y restaurándolo al cerrar.
/// </summary>
public interface IWindowsGameModeHealthService
{
    /// <summary>Chequea el estado completo (registro + archivos + Appx). El resultado se cachea un tiempo corto (la consulta del paquete Appx es lenta).</summary>
    Task<WindowsGameModeHealthInfo> CheckAsync();

    /// <summary>
    /// Reactiva el Modo Juego si solo está deshabilitado por reglas de registro
    /// (borra/clarea AutoGameModeEnabled y AllowAutoGameMode en GameBar). Devuelve
    /// el estado re-verificado; si estaba huérfano, no cambia nada (falta files).
    /// </summary>
    Task<WindowsGameModeHealthInfo> EnableGameModeAsync();

    /// <summary>
    /// Asegura que el Modo Juego de Windows esté habilitado para la partida:
    /// snapshot del valor previo, activación (regla de registro) y autoreparación
    /// si el bloqueo venía de AtlasOS u otro debloater. Devuelve el snapshot para
    /// restaurar al cerrar el juego (null = estaba habilitado por defecto).
    /// </summary>
    Task<int?> EnsureEnabledForSessionAsync();

    /// <summary>
    /// Restaura el estado previo del Modo Juego al cerrar la partida (complemento
    /// de <see cref="EnsureEnabledForSessionAsync"/>): re-escribe el valor
    /// guardado o borra las claves si antes no existían.
    /// </summary>
    void RestoreForSessionEnd(int? snapshot);
}
