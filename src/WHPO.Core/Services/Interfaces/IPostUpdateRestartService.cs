namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Coordina el relanzamiento de la app tras una actualización: arma la PROPERTY
/// de msiexec que el CustomAction del instalador usa para reabrir la app.
/// </summary>
public interface IPostUpdateRestartService
{
    /// <summary>Devuelve la PROPERTY para msiexec con la línea de comandos actual de la app.</summary>
    string PrepareRestartArg();
}