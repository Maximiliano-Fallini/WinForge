using System;
using System.Runtime.InteropServices;

namespace WHPO.Core.Services;

/// <summary>
/// Efficiency Mode de Windows (EcoQoS): marca un proceso como "no crítico" para que el
/// planificador lo trate como tarea de fondo. Efectos:
///   · En CPUs híbridas (Intel 12° gen+, con P/E cores): sus hilos van a los E-cores,
///     dejando los P-cores libres para el juego.
///   · Power throttling: Windows limita la frecuencia efectiva del proceso.
///   · Prioridad de scheduling de fondo.
/// Es el mismo mecanismo que usa Windows para las pestañas de fondo del navegador y lo
/// que muestra el Administrador de tareas con el ícono de hojita.
///
/// API: SetProcessInformation(ProcessPowerThrottling). Disponible desde Win10 20H1; el
/// beneficio completo se ve en Windows 11. Reversible por PID: StateMask = 0 vuelve al
/// estado normal. Los procesos protegidos por anti-cheat niegan la apertura → false.
/// </summary>
internal static class EfficiencyMode
{
    private const int ProcessPowerThrottlingClass = 4; // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling

    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        // OBLIGATORIO: el campo Version debe ser 1 (PROCESS_POWER_THROTTLING_CURRENT_VERSION)
        // y el struct completo medir 12 bytes; sin él, SetProcessInformation devuelve
        // ERROR_BAD_LENGTH (0x18) y EcoQoS nunca se aplica.
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess, int informationClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Activa o desactiva Efficiency Mode para el PID indicado. Devuelve false si no se
    /// pudo abrir el proceso (protegido, cerrado o sin permisos): el llamador lo omite.
    /// </summary>
    public static bool Set(int pid, bool enabled)
    {
        IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                // ControlMask declara qué campo controlamos; StateMask ON = eco activo,
                // 0 = explícitamente desactivado (vuelve al estado normal).
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enabled ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0u
            };
            return SetProcessInformation(h, ProcessPowerThrottlingClass, ref state,
                Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
