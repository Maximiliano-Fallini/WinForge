namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Estado del driver PawnIO en este equipo. <see cref="Version"/> es la versión
/// instalada (null si no está) e <see cref="InstallerVersion"/> la que trae
/// WinForge para instalar o actualizar.
/// </summary>
public sealed record PawnIoDriverStatus(
    bool Installed,
    string? Version,
    string InstallerVersion);

/// <summary>
/// Ciclo de vida del driver PawnIO, independiente de los componentes que lo usan.
///
/// Vive en Core a propósito: Sensores y Ventiladores son solo consumidores. Si el
/// driver se instalara desde uno de ellos, desinstalar ese componente dejaría al
/// otro sin la dependencia. Acá la instalación, la actualización, la desinstalación
/// y el estado son únicos para toda la app.
/// </summary>
public interface IPawnIoService
{
    /// <summary>Lee el estado actual del registro; no abre hardware.</summary>
    PawnIoDriverStatus GetStatus();

    /// <summary>
    /// Descarga (o usa la copia local), verifica el SHA-256 y ejecuta el instalador
    /// oficial en silencio. Con <paramref name="force"/> también corre si ya está
    /// instalado, para actualizar o reparar la instalación.
    /// </summary>
    Task<PawnIOInstallResult> InstallAsync(bool force = false);

    /// <summary>Ejecuta el desinstalador oficial de PawnIO en silencio.</summary>
    Task<PawnIOInstallResult> UninstallAsync();

    /// <summary>
    /// True si el componente necesita PawnIO para funcionar completo. La lista vive
    /// acá, no en el Workshop, para que la dependencia sea una sola.
    /// </summary>
    bool IsRequiredBy(string componentId);
}
