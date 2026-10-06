using System.Diagnostics;
using System.Text;

namespace WHPO.Core.Services;

/// <summary>
/// Ejecuta comandos de PowerShell ocultos y devuelve salida + exit code.
///
/// ENDURECIDO CONTRA COLGUES: la versión original esperaba el cierre de las
/// tuberías (ReadToEnd/WaitForExitAsync); si un proceso hijo de PowerShell (p. ej.
/// conhost, o un comando que deja un nieto vivo) heredaba stdout/stderr, la lectura
/// no terminaba NUNCA aunque PowerShell ya hubiera salido — la pestaña TCP quedaba
/// en "Consultando estado TCP…" para siempre. Ahora:
///   · la espera es sobre el HANDLE del proceso (WaitForExit(ms)), no sobre las
///     tuberías;
///   · hay timeout duro (se mata el árbol si se pasa);
///   · tras salir el proceso se drena lo recibido (lectura por eventos, que ya
///     está en el buffer) y se cancelan las lecturas pendientes.
/// Todos los awaits usan ConfigureAwait(false): los llamadores pueden esperar el
/// resultado de forma sincrónica sin quedar bloqueados esperando al hilo de UI.
/// </summary>
public static class PowerShellRunner
{
    /// <summary>Timeout por defecto de un comando (ms).</summary>
    public const int DefaultTimeoutMs = 25000;

    public static Task<(string Output, int ExitCode)> RunAsync(string command)
        => RunAsync(command, DefaultTimeoutMs);

    /// <summary>
    /// Ejecuta el comando con timeout explícito (ms). Devuelve la salida capturada
    /// hasta ese momento y el exit code (-1 = no terminó, se mató o no arrancó).
    /// </summary>
    public static async Task<(string Output, int ExitCode)> RunAsync(string command, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var output = new StringBuilder();
        var errors = new StringBuilder();
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return ("", -1);

            proc.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // Task.Run: el overload con timeout es sincrónico y no queremos bloquear
            // al llamador (que en la UI es el hilo del dispatcher).
            bool exited = await Task.Run(() => proc.WaitForExit(timeoutMs)).ConfigureAwait(false);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                await DrainAsync(output, errors, maxMs: 600).ConfigureAwait(false);
                CancelReads(proc);
                var partial = Compose(output, errors);
                return (partial + (partial.Length > 0 ? Environment.NewLine : "")
                        + $"Tiempo de espera agotado ({timeoutMs} ms): el comando no terminó.", -1);
            }

            // El proceso salió: drenar lo que ya está en el buffer. Si un hijo dejó
            // las tuberías abiertas, esto termina igual (no esperamos EOF).
            await DrainAsync(output, errors, maxMs: 1500).ConfigureAwait(false);
            CancelReads(proc);
            return (Compose(output, errors), proc.ExitCode);
        }
        catch (Exception ex)
        {
            return (ex.Message, -1);
        }
    }

    /// <summary>
    /// Espera a que los handlers de salida dejen de recibir datos (estable durante
    /// 3 chequeos de 100 ms) con un tope duro: alcanza para volcar lo que PowerShell
    /// ya escribió sin depender del cierre de la tubería.
    /// </summary>
    private static async Task DrainAsync(StringBuilder output, StringBuilder errors, int maxMs)
    {
        int stable = 0;
        int elapsed = 0;
        while (elapsed < maxMs && stable < 3)
        {
            int before = output.Length + errors.Length;
            await Task.Delay(100).ConfigureAwait(false);
            elapsed += 100;
            if (output.Length + errors.Length == before) stable++;
            else stable = 0;
        }
    }

    /// <summary>Corta las lecturas asíncronas pendientes (libera los handlers).</summary>
    private static void CancelReads(Process proc)
    {
        try { proc.CancelOutputRead(); } catch { }
        try { proc.CancelErrorRead(); } catch { }
    }

    /// <summary>Une stdout + stderr (stderr al final, como antes).</summary>
    private static string Compose(StringBuilder output, StringBuilder errors)
    {
        var text = output.ToString().Trim();
        if (errors.Length > 0)
            text = string.IsNullOrEmpty(text) ? errors.ToString().Trim() : text + Environment.NewLine + errors.ToString().Trim();
        return text;
    }
}
