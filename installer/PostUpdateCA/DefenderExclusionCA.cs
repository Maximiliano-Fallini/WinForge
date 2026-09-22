using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

namespace PostUpdateCA
{
    /// <summary>
    /// Exclusiones de Windows Defender, aplicadas ANTES de copiar los archivos.
    ///
    /// Por qué existe (y por qué no alcanza con el script Add-DefenderExclusion.ps1):
    /// el instalador ya corría ese script, pero lo hacía DESPUÉS de InstallFiles. En el
    /// medio, Windows Defender (o el antivirus de turno) escanea cada uno de los ~690
    /// archivos que se escriben —286 MB, ejecutable sin firmar— y un bloqueo o una
    /// cuarentena en el medio deja la copia en un ida y vuelta de reintentos que en
    /// pantalla se ve como una barra de progreso congelada. Aplicar la exclusión ANTES
    /// de la copia evita ese forcejeo.
    ///
    /// Cómo: estas dos CustomActions administradas (C#) llaman al método Add de la clase
    /// WMI MSFT_MpPreference, que es lo que hace Add-MpPreference por dentro, pero sin
    /// arrancar PowerShell (que en una máquina con el antivirus en mal estado puede tardar
    /// o quedarse colgado sin límite).
    ///
    /// Reglas de diseño, para que esto no pueda trabar una instalación:
    ///   * La llamada WMI corre en un hilo con TOPE de tiempo: si el proveedor de Defender
    ///     no responde, la CustomAction sigue (y lo deja dicho en el log). Una CA colgada
    ///     deja al usuario mirando una barra de progreso quieta para siempre; una CA que se
    ///     rinde, no.
    ///   * Nunca devuelve Failure: la instalación no se revierte por una exclusión.
    ///   * Verifica contra el REGISTRO de Defender (la fuente de verdad, la misma que usa el
    ///     script) y lo registra en el log del MSI, en vez de dar por hecho que el cambio se
    ///     aplicó. La protección contra alteraciones activada descarta el cambio en silencio:
    ///     eso queda escrito.
    ///   * El script Add-DefenderExclusion.ps1 sigue corriendo después de copiar los archivos
    ///     (verifica, escribe su propio log y es el que se puede correr a mano): esto es la
    ///     pasada temprana que protege la copia, no su reemplazo.
    /// </summary>
    public static class DefenderExclusionCA
    {
        /// <summary>Espacio WMI del proveedor de Defender (el que usan los cmdlets MpPreference).</summary>
        private const string WmiScope = @"root\Microsoft\Windows\Defender";

        private const string WmiClass = "MSFT_MpPreference";

        /// <summary>Ejecutable a excluir (Defender compara solo el nombre del archivo).</summary>
        private const string ProcessName = "WinForge.exe";

        /// <summary>Tope para la llamada WMI: pasado esto, se sigue con la instalación.</summary>
        private const int WmiTimeoutSeconds = 20;

        /// <summary>Tope para la relectura de verificación (Defender asienta el cambio con retraso).</summary>
        private const int VerifyTimeoutSeconds = 4;

        /// <summary>
        /// CustomAction inmediata: arma el CustomActionData de la CA diferida.
        /// Una CA diferida no puede leer propiedades de la sesión (solo recibe su propio
        /// CustomActionData), y la carpeta de instalación y la de datos de la app son
        /// justamente lo que hay que excluir.
        /// </summary>
        [CustomAction]
        public static ActionResult SetDefenderExclusionData(Session session)
        {
            try
            {
                var paths = new List<string>();

                var installFolder = Normalize(session["INSTALLFOLDER"]);
                if (installFolder != null) paths.Add(installFolder);

                // Carpeta de datos de la app: de ahí salen los componentes del Workshop y
                // los instaladores que la app baja después. Es de ESTE usuario, por eso la
                // CA diferida va impersonated.
                var localAppData = Normalize(session["LocalAppDataFolder"]);
                if (localAppData != null) paths.Add(Path.Combine(localAppData, "WHPO"));

                if (paths.Count == 0)
                {
                    session.Log("DefenderExclusion (temprana): no hay rutas para excluir; no se hace nada.");
                    session["AddDefenderExclusionEarly"] = string.Empty;
                    return ActionResult.Success;
                }

                // Formato del dato: primera línea = rutas separadas por '|', segunda = procesos.
                session["AddDefenderExclusionEarly"] =
                    string.Join("|", paths) + Environment.NewLine + ProcessName;
                session.Log("DefenderExclusion (temprana): a excluir: " + string.Join(" | ", paths)
                            + " | proceso " + ProcessName);
            }
            catch (Exception ex)
            {
                session.Log("DefenderExclusion (temprana): no se pudo armar el CustomActionData: " + ex);
                session["AddDefenderExclusionEarly"] = string.Empty;
            }

            return ActionResult.Success;
        }

        /// <summary>
        /// CustomAction diferida (antes de InstallFiles): agrega las exclusiones por WMI,
        /// con tope de tiempo, y verifica contra el registro de Defender.
        /// </summary>
        [CustomAction]
        public static ActionResult AddDefenderExclusionEarly(Session session)
        {
            List<string> paths;
            List<string> processes;
            if (!TryParseData(session, out paths, out processes))
            {
                session.Log("DefenderExclusion (temprana): sin datos; no se toca nada.");
                return ActionResult.Success;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                var task = Task.Run(() => AddViaWmi(paths, processes));

                if (!task.Wait(TimeSpan.FromSeconds(WmiTimeoutSeconds)))
                {
                    // El proveedor de Defender no respondió: se sigue igual. La pasada del
                    // script (después de la copia) y el chequeo de salud de la app vuelven a
                    // intentarlo, así que no esperar acá no pierde nada.
                    session.Log($"DefenderExclusion (temprana): la llamada WMI no respondió en {WmiTimeoutSeconds} s; "
                                + "se continúa con la instalación (lo reintentan el script posterior y la app).");
                    return ActionResult.Success;
                }

                if (task.Status == TaskStatus.Faulted && task.Exception != null)
                {
                    session.Log("DefenderExclusion (temprana): falló (no bloquea la instalación): "
                                + task.Exception.GetBaseException().Message);
                }
                else
                {
                    session.Log($"DefenderExclusion (temprana): WMI respondió en {sw.ElapsedMilliseconds} ms.");
                }

                VerifyAndLog(session, paths, processes);
            }
            catch (Exception ex)
            {
                session.Log("DefenderExclusion (temprana): error inesperado (no bloquea la instalación): " + ex);
            }

            return ActionResult.Success;
        }

        // =====================================================================
        // Implementación (privada)
        // =====================================================================

        private static void AddViaWmi(List<string> paths, List<string> processes)
        {
            var scope = new ManagementScope(@"\\" + Environment.MachineName + @"\" + WmiScope);
            scope.Connect();

            using (var cls = new ManagementClass(scope, new ManagementPath(WmiClass), null))
            using (var inParams = cls.GetMethodParameters("Add"))
            {
                // Los parámetros que no se usan (extensiones, acciones por amenaza) van
                // vacíos: es el mismo método que Add-MpPreference y acepta subconjuntos.
                inParams["ExclusionPath"] = paths.ToArray();
                inParams["ExclusionProcess"] = processes.ToArray();
                inParams["Force"] = true;

                using (var outParams = cls.InvokeMethod("Add", inParams, null))
                {
                    var result = outParams == null ? null : outParams["ReturnValue"];
                    var code = result == null ? -1 : Convert.ToInt32(result);
                    if (code != 0)
                        throw new InvalidOperationException("MSFT_MpPreference.Add devolvió " + code + ".");
                }
            }
        }

        /// <summary>
        /// Relee las exclusiones del registro de Defender y deja en el log del MSI cuáles
        /// quedaron. Con la protección contra alteraciones activada el cambio se descarta en
        /// silencio: sin esta verificación el log diría que se aplicó y no habría forma de
        /// saber que no.
        /// </summary>
        private static void VerifyAndLog(Session session, List<string> paths, List<string> processes)
        {
            const string baseKey = @"SOFTWARE\Microsoft\Windows Defender\Exclusions";
            var missing = new List<string>();
            var deadline = DateTime.UtcNow.AddSeconds(VerifyTimeoutSeconds);

            do
            {
                missing.Clear();
                var presentPaths = ReadValues(baseKey + @"\Paths");
                var presentProcesses = ReadValues(baseKey + @"\Processes");

                foreach (var path in paths)
                    if (!presentPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                        missing.Add(path);
                foreach (var process in processes)
                    if (!presentProcesses.Contains(process, StringComparer.OrdinalIgnoreCase))
                        missing.Add(process);

                if (missing.Count == 0) break;
                System.Threading.Thread.Sleep(500);
            }
            while (DateTime.UtcNow < deadline);

            if (missing.Count == 0)
            {
                session.Log("DefenderExclusion (temprana): exclusiones verificadas en el registro de Defender.");
            }
            else
            {
                session.Log("DefenderExclusion (temprana): no quedaron verificadas (revisar la protección contra "
                            + "alteraciones o el antivirus activo): " + string.Join(" | ", missing));
            }
        }

        /// <summary>Lee los NOMBRES de valor de una clave de exclusión (cada exclusión es un valor).</summary>
        private static List<string> ReadValues(string subKey)
        {
            var values = new List<string>();
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(subKey))
                {
                    if (key == null) return values;
                    foreach (var name in key.GetValueNames())
                    {
                        var value = name == null ? null : name.Trim().TrimEnd('\\');
                        if (!string.IsNullOrEmpty(value)) values.Add(value);
                    }
                }
            }
            catch (Exception)
            {
                // Lectura best-effort: si no se puede leer, la verificación lo reporta como faltante.
            }
            return values;
        }

        /// <summary>
        /// Desarma el CustomActionData: la primera línea son las rutas (separadas por '|')
        /// y la segunda los procesos.
        /// </summary>
        private static bool TryParseData(Session session, out List<string> paths, out List<string> processes)
        {
            paths = new List<string>();
            processes = new List<string>();

            try
            {
                var data = session.CustomActionData;
                var text = data == null ? string.Empty : (data.ToString() ?? string.Empty);
                if (string.IsNullOrWhiteSpace(text)) return false;

                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                    paths = lines[0].Split('|').Select(Normalize).Where(p => p != null).ToList();
                if (lines.Length > 1)
                    processes = lines[1].Split('|')
                        .Select(p => p == null ? null : p.Trim())
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();

                return paths.Count > 0 || processes.Count > 0;
            }
            catch (Exception ex)
            {
                session.Log("DefenderExclusion (temprana): no se pudo leer el CustomActionData: " + ex.Message);
                return false;
            }
        }

        /// <summary>Normaliza una ruta para compararla: sin espacios al borde y sin barra final.</summary>
        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim().TrimEnd('\\');
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
