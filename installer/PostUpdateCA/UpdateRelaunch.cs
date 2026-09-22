using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using WixToolset.Dtf.WindowsInstaller;

namespace PostUpdateCA
{
    /// <summary>
    /// CustomActions del flujo de actualización.
    ///
    /// El problema que resuelven: el actualizador de WinForge descarga el MSI, cierra la app,
    /// lo instala en silencio y quiere que la app vuelva a abrirse al terminar, con la misma
    /// línea de comandos con la que estaba corriendo (por ejemplo con una pestaña abierta).
    /// El instalador no puede saber eso por su cuenta, así que el actualizador se lo pasa en
    /// la propiedad PROPERTY_PATH.
    ///
    /// Por qué son DOS custom actions: una CA diferida (la que puede tocar el sistema) no
    /// puede leer las propiedades de la sesión; lo único que recibe es su propio
    /// CustomActionData. Entonces la CA inmediata SetLaunchCommandData copia PROPERTY_PATH a
    /// la propiedad que lleva el nombre de la CA diferida (RunWinForgeAfterUpgrade), que es
    /// exactamente de donde el motor saca ese CustomActionData. Es el único canal válido.
    ///
    /// Las dos devuelven Success siempre: un fallo al reabrir la app no puede hacer que la
    /// instalación falle o se revierta. Lo que sí hacen es registrarlo en el log del MSI.
    /// </summary>
    public static class UpdateRelaunch
    {
        /// <summary>Espera antes de crear el proceso.</summary>
        private const int LaunchDelayMs = 1200;

        [CustomAction]
        public static ActionResult SetLaunchCommandData(Session session)
        {
            try
            {
                var requested = session["PROPERTY_PATH"];
                session["RunWinForgeAfterUpgrade"] = requested ?? string.Empty;
                session.Log("PostUpdateCA: linea de comandos recibida: " +
                            (string.IsNullOrWhiteSpace(requested) ? "(vacia: la app no se reabre)" : requested));
            }
            catch (Exception ex)
            {
                session.Log("PostUpdateCA: no se pudo copiar PROPERTY_PATH: " + ex);
            }

            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult LaunchAfterUpgrade(Session session)
        {
            try
            {
                // CustomActionData es el único dato que llega a una CA diferida, y viene en
                // un tipo propio de DTF: se pasa a texto sin asumir conversiones implícitas.
                var data = session.CustomActionData;
                var commandLine = data == null ? string.Empty : (data.ToString() ?? string.Empty);

                var exe = ExtractExecutable(commandLine, out var arguments);

                if (string.IsNullOrWhiteSpace(exe))
                {
                    session.Log("PostUpdateCA: no hay linea de comandos para reabrir la app.");
                    return ActionResult.Success;
                }

                if (!File.Exists(exe))
                {
                    session.Log("PostUpdateCA: el ejecutable no existe, no se reabre: " + exe);
                    return ActionResult.Success;
                }

                // El MSI cierra WinForge con taskkill y el proceso puede seguir vivo unos
                // milisegundos: si se lanza antes, la app arranca y el taskkill la mata.
                Thread.Sleep(LaunchDelayMs);

                var startInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments ?? string.Empty,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty
                };
                var process = Process.Start(startInfo);
                session.Log("PostUpdateCA: WinForge reabierto (PID " + (process != null ? process.Id.ToString() : "?") +
                            ") con argumentos '" + arguments + "'.");
            }
            catch (Exception ex)
            {
                // Nunca se propaga: la instalación ya terminó bien.
                session.Log("PostUpdateCA: no se pudo reabrir WinForge: " + ex);
            }

            return ActionResult.Success;
        }

        /// <summary>
        /// Separa el ejecutable de sus argumentos en una línea de comandos de Windows.
        /// Acepta las dos formas que puede escribir el actualizador:
        ///   "C:\Program Files\WinForge\WinForge.exe" --tab sensores
        ///   C:\WinForge\WinForge.exe --tab sensores
        /// </summary>
        internal static string ExtractExecutable(string commandLine, out string arguments)
        {
            arguments = string.Empty;
            var text = (commandLine ?? string.Empty).Trim();
            if (text.Length == 0) return string.Empty;

            if (text[0] == '"')
            {
                var closing = text.IndexOf('"', 1);
                if (closing > 0)
                {
                    arguments = text.Substring(closing + 1).Trim();
                    return text.Substring(1, closing - 1);
                }
            }

            var space = text.IndexOf(' ');
            if (space < 0) return text;

            arguments = text.Substring(space + 1).Trim();
            return text.Substring(0, space);
        }
    }
}
