using System;
using System.Diagnostics;

namespace WHPO.Core.Services;

/// <summary>
/// Efficiency Mode de Windows sobre el PROPIO proceso de WinForge.
///
/// Por qué existe aparte de <see cref="EfficiencyMode"/>: esa clase es internal de
/// WHPO.Core y opera sobre PIDs ajenos (Modo juego). Esta fachada pública expone lo
/// justo para que la UI aplique Efficiency Mode a sí misma al minimizar a bandeja:
/// <list type="bullet">
/// <item>Activa al ocultar, desactiva al mostrar (reversible: el estado vuelve solo).</item>
/// <item>Aplica las DOS mitades de Efficiency Mode como lo define Windows: EcoQoS y
/// prioridad base Idle. Task Manager muestra la hojita por la combinación: con solo
/// EcoQoS no la dibuja (verificado en Windows 11 25H2: stateMask=1 sin hojita; al
/// bajar la prioridad, aparece).</item>
/// <item>Nunca rompe en Windows viejos: <see cref="EfficiencyMode.Set"/> no lanza y
/// devuelve false si la API no existe; acá eso se registra como "no soportado" y el
/// comportamiento sigue como si la opción estuviera apagada (sin tocar la prioridad).</item>
/// </list>
/// </summary>
public static class SelfEfficiencyMode
{
    /// <summary>PID del proceso actual (cacheado: no cambia en la vida del proceso).</summary>
    private static readonly int SelfPid = Environment.ProcessId;

    /// <summary>
    /// True si ESTE Windows aceptó EcoQoS alguna vez en esta corrida (null = todavía no
    /// se intentó: ni confirma ni niega). Sirve para no insistir en cada ocultar/mostrar
    /// cuando la build no lo soporta, y para mostrar el estado real en la UI.
    /// </summary>
    public static bool? Supported { get; private set; }

    /// <summary>
    /// True si EcoQoS está aplicado AHORA al propio proceso.
    /// </summary>
    public static bool Applied { get; private set; }

    /// <summary>
    /// Prioridad base que tenía el proceso ANTES de bajarla (null = nunca se bajó o ya
    /// se restauró). Se captura una sola vez: dos ocultares seguidos no deben guardar
    /// "Idle" como si fuera el valor original.
    /// </summary>
    private static ProcessPriorityClass? _originalPriority;

    /// <summary>
    /// La guardia que decide si hay que tocar EcoQoS, extraída de MainWindow para
    /// poder probarla sin abrir la app:
    /// <list type="bullet">
    /// <item>Al OCULTAR (<paramref name="hiding"/> true) manda el switch del usuario:
    /// si está apagado, no se toca nada.</item>
    /// <item>Al MOSTRAR (<paramref name="hiding"/> false) manda lo que está aplicado,
    /// NO el switch: si el usuario apagó el switch mientras estaba en bandeja, al
    /// volver hay que quitar el EcoQoS que ya estaba puesto — si no, la UI quedaría
    /// throttled.</item>
    /// </list>
    /// </summary>
    public static bool ShouldSet(bool hiding, bool switchOn, bool applied)
        => hiding ? switchOn : applied;

    /// <summary>
    /// Aplica o quita Efficiency Mode al propio proceso: EcoQoS + prioridad Idle al
    /// activar, y la reversión de ambas al desactivar. Seguro en cualquier Windows: si
    /// la API no existe o falla, devuelve false y no cambia nada (el llamador sigue igual).
    /// </summary>
    public static bool Set(bool enabled)
    {
        bool ok;
        try
        {
            ok = EfficiencyMode.Set(SelfPid, enabled);
        }
        catch
        {
            ok = false;
        }

        if (ok)
        {
            Supported = true;
            Applied = enabled;
            if (enabled) LowerPriorityOnce();
        }
        else if (enabled)
        {
            // Solo el intento de ACTIVAR informa soporte: desactivar algo que nunca se
            // aplicó devuelve false igual (nada que quitar) y no dice nada del Windows.
            Supported = false;
        }

        // Al MOSTRAR la prioridad se devuelve SIEMPRE, aunque el desactivado de EcoQoS
        // haya fallado: la UI nunca debe quedar arrastrándose en Idle.
        if (!enabled) RestorePriority();

        return ok;
    }

    /// <summary>
    /// Baja la prioridad base del proceso a Idle la PRIMERA vez, guardando la original.
    /// Task Manager define "Efficiency Mode" como prioridad baja + EcoQoS: con solo
    /// EcoQoS el proceso está en modo eco, pero la hojita no aparece y parece que la
    /// función no hiciera nada. Idle es el "Bajo" del Administrador de tareas.
    /// </summary>
    private static void LowerPriorityOnce()
    {
        try
        {
            var self = Process.GetCurrentProcess();
            _originalPriority ??= self.PriorityClass;
            self.PriorityClass = ProcessPriorityClass.Idle;
        }
        catch
        {
            // Sin permiso o API rara: se sigue solo con EcoQoS, como antes.
        }
    }

    /// <summary>Devuelve la prioridad base original (una sola vez por aplicación).</summary>
    private static void RestorePriority()
    {
        if (_originalPriority is not ProcessPriorityClass original) return;
        try
        {
            Process.GetCurrentProcess().PriorityClass = original;
        }
        catch
        {
        }
        _originalPriority = null;
    }
}