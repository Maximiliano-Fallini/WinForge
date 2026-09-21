namespace WHPO_UI.Services;

/// <summary>
/// Excepciones de Windows Defender (Add-MpPreference / Remove-MpPreference).
/// La app corre elevada (app.manifest: requireAdministrator), así que los
/// cmdlets de Defender se ejecutan sin pedir UAC. Las rutas se comparan sin
/// distinguir mayúsculas y normalizando la barra final.
/// </summary>
public static class DefenderService
{
    public static Task<(bool Ok, string Message)> AddPathExclusionAsync(string path)
        => RunMpAsync($"Add-MpPreference -ExclusionPath '{Escape(path)}'");

    public static Task<(bool Ok, string Message)> RemovePathExclusionAsync(string path)
        => RunMpAsync($"Remove-MpPreference -ExclusionPath '{Escape(path)}'");

    public static Task<(bool Ok, string Message)> AddProcessExclusionAsync(string exeName)
        => RunMpAsync($"Add-MpPreference -ExclusionProcess '{Escape(exeName)}'");

    public static Task<(bool Ok, string Message)> RemoveProcessExclusionAsync(string exeName)
        => RunMpAsync($"Remove-MpPreference -ExclusionProcess '{Escape(exeName)}'");

    /// <summary>True si la ruta ya figura en ExclusionPath (insensible a mayúsculas).</summary>
    public static async Task<bool> IsPathExcludedAsync(string path)
    {
        var (_, output) = await RunMpAsync("(Get-MpPreference).ExclusionPath");
        return ContainsNormalized(output, path);
    }

    /// <summary>True si el proceso ya figura en ExclusionProcess (insensible a mayúsculas).</summary>
    public static async Task<bool> IsProcessExcludedAsync(string exeName)
    {
        var (_, output) = await RunMpAsync("(Get-MpPreference).ExclusionProcess");
        return ContainsNormalized(output, exeName);
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static bool ContainsNormalized(string haystack, string needle)
    {
        string n = needle.Trim().TrimEnd('\\');
        if (n.Length == 0) return false;
        return haystack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim().TrimEnd('\\'), n, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(bool Ok, string Message)> RunMpAsync(string command)
    {
        var (output, exit) = await PowerShellRunner.RunAsync(command);
        return (exit == 0, output);
    }
}
