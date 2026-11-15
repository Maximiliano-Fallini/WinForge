using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WHPO.Core.Services;

/// <summary>
/// Efficiency Mode de Windows (EcoQoS): marca un proceso como "no crítico" para que el
/// planificador lo trate como tarea de fondo. Efectos:
/// · En CPUs híbridas (Intel 12° gen+, con P/E cores): sus hilos van a los E-cores,
/// dejando los P-cores libres para el juego.
/// · Power throttling: Windows limita la frecuencia efectiva del proceso.
/// · Prioridad de scheduling de fondo.
/// Es el mismo mecanismo que usa Windows para las pestañas de fondo del navegador y lo
/// que muestra el Administrador de tareas con el ícono de hojita.
///
/// API: SetProcessInformation(ProcessPowerThrottling). Disponible desde Win10 20H1; el
/// beneficio completo se ve en Windows 11. Reversible por PID: StateMask = 0 vuelve al
/// estado normal. Los procesos protegidos por anti-cheat niegan la apertura → false.
/// </summary>
public static class EfficiencyMode
{
    /// <summary>
    /// True si ESTE Windows soporta EcoQoS: la versión lo trae (Windows 10 20H1, build
    /// 19041+, ver <see cref="WindowsCapabilities.SupportsEcoQos"/>) y la API responde al
    /// consultarla en el propio proceso (una build puede traer la clase y fallar igual).
    /// Se resuelve UNA vez: es una capacidad de la máquina, no un estado de la corrida.
    /// </summary>
    public static bool IsSupported => _supported ??= WindowsCapabilities.SupportsEcoQos
        && IsEnabled(Environment.ProcessId) != null;

    private static bool? _supported;

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
    private static extern bool GetProcessInformation(
        IntPtr hProcess, int informationClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Activa o desactiva Efficiency Mode para el PID indicado. Devuelve false si no se
    /// pudo abrir el proceso (protegido, cerrado o sin permisos): el llamador lo omite.
    ///
    /// COMPATIBILIDAD: la API (SetProcessInformation/ProcessPowerThrottling) existe desde
    /// Win10 20H1, pero en builds viejas la llamada puede fallar con ERROR_BAD_LENGTH o
    /// no estar implementada. Por eso NUNCA lanza: cualquier fallo devuelve false y el
    /// llamador sigue como si la opción estuviera apagada. Llamar en un Windows
    /// incompatible es seguro (no-op), no rompe nada.
    /// </summary>
    /// <summary>
    /// Prioridad que tenía cada proceso cuando SE LA BAJAMOS nosotros, con su hora de
    /// arranque para no confundir el PID con otro proceso si Windows lo reutiliza.
    /// </summary>
    private static readonly Dictionary<int, (DateTime Start, ProcessPriorityClass Priority)> _priorityBefore = new();

    /// <summary>
    /// Activa o desactiva Efficiency Mode para el PID indicado.
    /// <para>
    /// <paramref name="lowerPriority"/> agrega la segunda mitad del modo eficiencia del
    /// Administrador de tareas: bajar la prioridad a Idle. Microsoft documenta que la
    /// hojita aparece con las DOS cosas —el Administrador de tareas baja la prioridad base
    /// a baja y además pone EcoQoS—. Con solo EcoQoS el proceso queda clasificado como eco
    /// pero el Administrador de tareas NO muestra el modo eficiencia, aunque
    /// <see cref="IsEnabled"/> devuelva true. Los llamadores viejos (Modo juego,
    /// EcoQoS propio) siguen con el comportamiento de siempre: solo EcoQoS.
    /// </para>
    /// Devuelve false si no se pudo abrir el proceso o si falló alguno de los dos pasos.
    /// </summary>
    public static bool Set(int pid, bool enabled, bool lowerPriority = false)
    {
        bool ecoOk = SetEcoQos(pid, enabled);
        if (!lowerPriority) return ecoOk;

        bool prioOk = enabled ? LowerPriority(pid) : RestorePriority(pid);
        return ecoOk && prioOk;
    }

    /// <summary>La mitad EcoQoS: la marca de "tarea no crítica" del planificador.</summary>
    private static bool SetEcoQos(int pid, bool enabled)
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

    /// <summary>
    /// Baja la prioridad a Idle guardando la anterior. Si el proceso ya estaba en Idle
    /// (por ejemplo porque otra herramienta lo puso así) no hay nada que devolver después
    /// y se guarda igual: al quitar, vuelve a Idle, que es como estaba.
    /// </summary>
    private static bool LowerPriority(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var start = process.StartTime;
            if (!_priorityBefore.ContainsKey(pid))
                _priorityBefore[pid] = (start, process.PriorityClass);
            process.PriorityClass = ProcessPriorityClass.Idle;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Devuelve la prioridad que tenía el proceso antes de que se la bajáramos. Si no la
    /// conocemos (la app se reinició, el eco lo puso otro programa o el PID es otro proceso)
    /// se vuelve a Normal, que es lo que hace el Administrador de tareas al desactivarlo.
    /// </summary>
    private static bool RestorePriority(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            ProcessPriorityClass target = ProcessPriorityClass.Normal;
            if (_priorityBefore.TryGetValue(pid, out var saved))
            {
                bool sameProcess;
                try { sameProcess = process.StartTime == saved.Start; }
                catch { sameProcess = false; }
                if (sameProcess) target = saved.Priority;
                _priorityBefore.Remove(pid);
            }
            process.PriorityClass = target;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Lee si el PID tiene Efficiency Mode activo ahora mismo (null = no se pudo
    /// consultar: proceso cerrado, protegido o sin permisos). Lo usa el Modo juego
    /// para VERIFICAR el resultado antes de reportarlo: sin esto, la UI anunciaba
    /// "procesos optimizados" solo porque se había pedido el cambio.
    /// </summary>
    public static bool? IsEnabled(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION
            };
            if (!GetProcessInformation(h, ProcessPowerThrottlingClass, ref state,
                    Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
                return null;
            return (state.StateMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
