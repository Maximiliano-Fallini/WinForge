using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Coordina el relanzamiento de la app después de una actualización con el
/// auto-updater (AppUpdateService).
///
/// Flujo:
///  1) La app arma la línea de comandos actual (exe + argumentos) y se la pasa
///     al MSI como PROPERTY de msiexec (PROPERTY_PATH="...").
///  2) El MSI reemplaza los archivos y su CustomAction (PostUpdateCA) crea de
///     nuevo el proceso de WinForge con esa línea al terminar la instalación.
///
/// El relanzamiento es informativo: si el usuario cancela el UAC o el CustomAction
/// falla, la app simplemente no se reabre sola — el actualizador no toca nada más.
/// </summary>
public sealed class PostUpdateRestartService : IPostUpdateRestartService
{
    /// <summary>
    /// Arma la línea de comandos de relanzamiento ("C:\...\WinForge.exe" --args)
    /// y la devuelve lista para pasar como PROPERTY a msiexec.
    /// </summary>
    public string PrepareRestartArg()
    {
        var exe = Environment.ProcessPath ?? "WinForge.exe";
        var cmd = "\"" + exe + "\"" +
                  string.Join("", Environment.GetCommandLineArgs().Skip(1).Select(a => " \"" + a + "\""));
        return "PROPERTY_PATH=" + cmd;
    }

    /// <summary>No hay marca persistente: el estado vive en la línea de msiexec.</summary>
    public bool IsPendingRestart() => false;

    /// <summary>Nada que limpiar (el CustomAction consume la línea al lanzar).</summary>
    public void ClearPendingRestart() { }
}