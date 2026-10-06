using System.Diagnostics;
using System.Text;

namespace WHPO_UI.Services;

/// <summary>
/// Runner de PowerShell de la UI: delega en el runner endurecido de Core
/// (<see cref="WHPO.Core.Services.PowerShellRunner"/>), que espera al proceso —no
/// al cierre de las tuberías— con timeout duro. Se mantiene esta clase para no
/// tocar los llamadores existentes.
/// </summary>
internal static class PowerShellRunner
{
    public const int DefaultTimeoutMs = WHPO.Core.Services.PowerShellRunner.DefaultTimeoutMs;

    public static Task<(string Output, int ExitCode)> RunAsync(string command)
        => WHPO.Core.Services.PowerShellRunner.RunAsync(command);

    public static Task<(string Output, int ExitCode)> RunAsync(string command, int timeoutMs)
        => WHPO.Core.Services.PowerShellRunner.RunAsync(command, timeoutMs);
}
