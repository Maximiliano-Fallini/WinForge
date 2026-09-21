using LibreHardwareMonitor.Hardware;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services.Fan;

/// <summary>
/// Punto único donde FanControlService pide un handle de ESCRITURA para un
/// ventilador de GPU, según el fabricante:
///
///  - NVIDIA: no hace falta nada — LHM escribe por NVAPI (NvAPI_GPU_SetCoolerLevels)
///    y arma el canal de control siempre que el driver exponga los cooler settings.
///  - AMD: LHM escribe por ADL Overdrive5 cuando logra activar su canal; si no lo
///    activa, este backend escribe por ADL directamente.
///  - Intel (Arc): LHM sólo puede LEER (IGCL no tiene canal de control en LHM); este
///    backend agrega la escritura por IGCL (ctlFanSetFixedSpeedMode).
///
/// Es el equivalente, dentro del proyecto, de lo que FanControl resuelve con
/// NvAPIWrapper / ADLXWrapper / IntelCtlLibrary: en vez de wrappers de terceros se
/// habla directo con las mismas librerías del driver (nvapi/atiadlxx/ControlLib),
/// que es lo que el proyecto ya hace con PawnIO y con LHM.
/// La UI no cambia: los canales que aparecen como "Solo lectura" pasan a ser
/// controlables y el resto del servicio (curvas, manual, calibración, auto) sigue igual.
/// </summary>
internal static class GpuFanVendor
{
    /// <summary>
    /// Handle de escritura para el ventilador <paramref name="fanIndex"/> de la GPU,
    /// o null si el fabricante no ofrece un backend propio para ese canal.
    /// </summary>
    public static IFanWriteHandle? TryGetWriteHandle(IHardware gpu, int fanIndex, ILoggingService log)
    {
        try
        {
            return gpu.HardwareType switch
            {
                HardwareType.GpuIntel => IntelFanAccess.TryGetWriteHandle(gpu, fanIndex, log),
                HardwareType.GpuAmd => AmdFanAccess.TryGetWriteHandle(gpu, fanIndex, log),
                _ => null
            };
        }
        catch
        {
            // Un backend de terceros nunca debe tumbar el listado de ventiladores.
            return null;
        }
    }

    /// <summary>
    /// Estado del backend propio del fabricante (para el diagnóstico del log): dice
    /// si la GPU queda en solo lectura por el driver, por la tarjeta o porque el
    /// backend ni está disponible.
    /// </summary>
    public static string DescribeBackend(IHardware gpu, ILoggingService log)
    {
        try
        {
            return gpu.HardwareType switch
            {
                HardwareType.GpuIntel => $"IGCL: {IntelFanAccess.DescribeBackend(gpu, log)}",
                HardwareType.GpuAmd => $"ADL: {AmdFanAccess.DescribeBackend(gpu, log)}",
                HardwareType.GpuNvidia => "NVAPI vía LibreHardwareMonitor",
                _ => "sin backend"
            };
        }
        catch (Exception ex)
        {
            return $"backend no disponible ({ex.Message})";
        }
    }

    /// <summary>
    /// Descarta las sesiones abiertas (IGCL/ADL). Se usa tras salir de una suspensión
    /// o al reabrir el hardware: los handles del driver dejan de ser válidos.
    /// </summary>
    public static void ResetSessions()
    {
        try { IntelFanAccess.ResetSession(); } catch { }
        try { AmdFanAccess.ResetSession(); } catch { }
    }

    public static void Dispose()
    {
        try { IntelFanAccess.Dispose(); } catch { }
        try { AmdFanAccess.Dispose(); } catch { }
    }
}
