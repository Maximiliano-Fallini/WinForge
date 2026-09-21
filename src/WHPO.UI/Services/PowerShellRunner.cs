using System.Diagnostics;
using System.Text;

namespace WHPO_UI.Services;

/// <summary>
/// Ejecuta comandos de PowerShell ocultos y devuelve salida + exit code. La app
/// corre elevada (app.manifest: requireAdministrator), así que los cmdlets de
/// sistema (Defender, mitigaciones…) se ejecutan sin pedir UAC. Sin Verb="runas":
/// UseShellExecute=false + runas lanza InvalidOperationException.
/// </summary>
internal static class PowerShellRunner
{
    public static async Task<(string Output, int ExitCode)> RunAsync(string command)
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
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return ("", -1);
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            string err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (!string.IsNullOrWhiteSpace(err)) output.AppendLine(err);
            return (output.ToString().Trim(), proc.ExitCode);
        }
        catch (Exception ex)
        {
            return (ex.Message, -1);
        }
    }
}
