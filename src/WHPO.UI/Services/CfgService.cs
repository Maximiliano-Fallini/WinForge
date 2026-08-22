using System.Text.RegularExpressions;

namespace WHPO_UI.Services;

/// <summary>
/// Control Flow Guard (CFG) por ejecutable, vía IFEO (Image File Execution Options):
/// Set-ProcessMitigation escribe en el registro y NO requiere que el exe exista.
/// El estado se lee con Get-ProcessMitigation, que decodifica el MitigationOptions:
/// el bloque "CFG:" muestra "Enable : OFF" cuando está desactivado (NOTSET = usa el
/// valor del sistema, que para la mayoría de los procesos es ON).
/// </summary>
public static class CfgService
{
    /// <summary>Activa (disabled=false) o desactiva (disabled=true) el CFG para el ejecutable.</summary>
    public static Task<(bool Ok, string Message)> SetAsync(string exeName, bool disabled)
        => RunAsync($"Set-ProcessMitigation -Name '{Escape(exeName)}' -{(disabled ? "Disable" : "Enable")} CFG");

    /// <summary>True si el CFG está DESACTIVADO para ese ejecutable.</summary>
    public static async Task<bool> IsDisabledAsync(string exeName)
    {
        var (_, output) = await RunAsync(
            $"(Get-ProcessMitigation -Name '{Escape(exeName)}' -ErrorAction SilentlyContinue | Out-String)");
        return Regex.IsMatch(output, @"CFG:\s*Enable\s*:\s*OFF");
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static async Task<(bool Ok, string Message)> RunAsync(string command)
    {
        var (output, exit) = await PowerShellRunner.RunAsync(command);
        return (exit == 0, output);
    }
}
