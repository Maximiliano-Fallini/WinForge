using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Un servicio tal como estaba cuando el Modo juego lo detuvo. Incluye tanto los
/// servicios HARDCODEADOS del boost (Windows Update, SysMain, WSearch…) como los
/// que el usuario haya dejado en su configuración: el journal registra lo que
/// REALMENTE se aplicó, sin importar de qué lista salió.
/// </summary>
public sealed class BoostJournalService
{
    public string Name { get; set; } = "";

    /// <summary>Modo de arranque leído en el snapshot (informativo para el log).</summary>
    public string StartType { get; set; } = "";

    /// <summary>True si estaba CORRIENDO: solo esos se re-arrancan al restaurar.</summary>
    public bool WasRunning { get; set; }
}

/// <summary>
/// Una tarea programada de Windows que el boost deshabilitó durante la partida.
/// Se guarda el path COMPLETO (\Carpeta\Tarea) porque hay tareas con el mismo
/// nombre en carpetas distintas. Igual que los servicios: cubre la whitelist del
/// código y las tareas que el usuario agregó a su configuración.
/// </summary>
public sealed class BoostJournalTask
{
    /// <summary>Path completo de la tarea (ej. \Microsoft\Windows\Defrag\ScheduledDefrag).</summary>
    public string Path { get; set; } = "";

    /// <summary>True si estaba HABILITADA: solo esas se re-habilitan al restaurar.</summary>
    public bool WasEnabled { get; set; }
}

/// <summary>
/// Un proceso de segundo plano al que el boost le cambió la prioridad / le activó
/// el modo eficiencia. Igual que los servicios: cubre la lista por defecto y la
/// configurada por el usuario.
/// </summary>
public sealed class BoostJournalProcess
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Prioridad ORIGINAL del proceso (se devuelve tal cual al restaurar).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessPriorityClass OriginalPriority { get; set; } = ProcessPriorityClass.Normal;

    /// <summary>
    /// True si el proceso YA tenía el modo eficiencia (EcoQoS) activo antes del
    /// boost: al restaurar se devuelve a ese estado en vez de apagarlo siempre.
    /// </summary>
    public bool EcoQosWasEnabled { get; set; }
}

/// <summary>
/// Journal del Modo juego: el snapshot COMPLETO de lo que el boost cambió en la
/// partida activa (servicios, procesos, notificaciones y Modo Juego de Windows),
/// persistido en disco.
///
/// Existe por un caso que el restore en memoria no cubre: si WinForge crashea o
/// la matan desde el Administrador de tareas con el boost activo, los servicios
/// quedaban detenidos y las prioridades bajas hasta reiniciar Windows (la
/// restauración vivía solo en el cierre de ventana). Con el journal, el próximo
/// arranque de la app detecta el cierre inesperado y devuelve el estado previo.
/// </summary>
public sealed class BoostJournalSnapshot
{
    /// <summary>Versión del formato: un journal de otra versión se descarta, no se interpreta.</summary>
    public int SchemaVersion { get; set; } = GameBoostJournal.SchemaVersion;

    /// <summary>Identificador de la sesión de boost (diagnóstico/log).</summary>
    public string SessionToken { get; set; } = "";

    /// <summary>PID de la app dueña del journal (detección de instancia viva).</summary>
    public int AppPid { get; set; }

    /// <summary>Hora de arranque del proceso dueño: distingue un PID reutilizado.</summary>
    public long AppStartTicks { get; set; }

    public string StartedUtc { get; set; } = "";

    /// <summary>Juegos que estaban corriendo cuando se aplicó (contexto/diagnóstico).</summary>
    public List<string> RunningGameExes { get; set; } = new();

    /// <summary>Servicios que estaban CORRIENDO y que el boost detuvo.</summary>
    public List<BoostJournalService> Services { get; set; } = new();

    /// <summary>Procesos a los que el boost bajó prioridad / activó EcoQoS.</summary>
    public List<BoostJournalProcess> Processes { get; set; } = new();

    /// <summary>
    /// Tareas programadas que estaban HABILITADAS y que el boost deshabilitó
    /// (whitelist + lista del usuario). Se re-habilitan al restaurar.
    /// </summary>
    public List<BoostJournalTask> Tasks { get; set; } = new();

    /// <summary>
    /// Mantenimiento automático de Windows: valor PREVIO del valor
    /// MaintenanceDisabled (null = la clave no existía, que es el default
    /// "activado"). Solo se restaura si el boost lo escribió.
    /// </summary>
    public int? MaintenanceDisabledSnapshot { get; set; }

    /// <summary>¿Se llegó a deshabilitar el Mantenimiento automático? (usa MaintenanceDisabledSnapshot).</summary>
    public bool MaintenanceDisabledTouched { get; set; }

    /// <summary>
    /// Procesos que el usuario pidió CERRAR (no se restauran: el cierre es
    /// intencional). Se registran para poder informarlo al recuperar.
    /// </summary>
    public List<string> KilledProcessNames { get; set; } = new();

    /// <summary>
    /// Pasos que se llegaron a aplicar ("services", "processes", "toasts",
    /// "gamemode", "tasks", "maintenance"). Permite saber hasta dónde llegó el boost si
    /// el proceso murió a mitad de camino.
    /// </summary>
    public List<string> AppliedSteps { get; set; } = new();

    /// <summary>Interruptor maestro de notificaciones previo (null = la clave no existía).</summary>
    public int? ToastsSnapshot { get; set; }

    /// <summary>Modo Juego de Windows previo (null = habilitado por defecto).</summary>
    public int? GameModeSnapshot { get; set; }

    /// <summary>¿Se llegó a pausar las notificaciones? (usa ToastsSnapshot).</summary>
    public bool ToastsPaused { get; set; }

    /// <summary>¿Se llegó a tocar el Modo Juego de Windows?</summary>
    public bool GameModeTouched { get; set; }
}

/// <summary>
/// Persistencia del journal del Modo juego en %LocalAppData% (WHPO / WHPO-Dev
/// según <see cref="WHPO.Core.AppPaths"/>, igual que el resto de las cachés: la
/// build de desarrollo y la instalada no se pisan).
///
/// Se guarda con escritura atómica (tmp + move), el mismo patrón que la caché de
/// la biblioteca: un corte a mitad de escritura nunca deja un JSON a medias.
/// </summary>
public static class GameBoostJournal
{
    public const int SchemaVersion = 1;

    private static readonly string JournalFile = Path.Combine(AppPaths.RootDir, "gameboost-journal.json");

    /// <summary>Ruta del journal (para log/diagnóstico).</summary>
    public static string FilePath => JournalFile;

    /// <summary>
    /// Hora de arranque del proceso ACTUAL en ticks UTC. Junto con el PID alcanza
    /// para saber si el dueño de un journal sigue vivo (un PID reciclado por otro
    /// proceso tiene otra hora de arranque).
    /// </summary>
    public static long CurrentProcessStartTicks()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Persiste el journal (best-effort: un fallo de disco nunca rompe el boost).</summary>
    public static void Save(BoostJournalSnapshot snapshot, ILoggingService? logging = null)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RootDir);
            var json = JsonSerializer.Serialize(snapshot);
            var tmp = JournalFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, JournalFile, overwrite: true);
        }
        catch (Exception ex)
        {
            logging?.LogWarning($"GameBoost: no se pudo guardar el journal ({ex.Message}); no habrá recuperación si la app se cierra de forma inesperada en esta partida.");
        }
    }

    /// <summary>
    /// Lee el journal de disco. Null si no existe, si está corrupto (se borra) o si
    /// es de otro esquema (se descarta sin interpretarlo).
    /// </summary>
    public static BoostJournalSnapshot? Load(ILoggingService? logging = null)
    {
        try
        {
            if (!File.Exists(JournalFile)) return null;
            var json = File.ReadAllText(JournalFile);
            var snapshot = JsonSerializer.Deserialize<BoostJournalSnapshot>(json);
            if (snapshot == null || snapshot.SchemaVersion != SchemaVersion)
            {
                logging?.LogWarning("GameBoost: el journal es de otra versión — se descarta.");
                Delete(logging);
                return null;
            }
            return snapshot;
        }
        catch (Exception ex)
        {
            logging?.LogWarning($"GameBoost: journal ilegible ({ex.Message}) — se descarta.");
            Delete(logging);
            return null;
        }
    }

    /// <summary>Borra el journal (al completar la restauración o al descartarlo).</summary>
    public static void Delete(ILoggingService? logging = null)
    {
        try
        {
            if (File.Exists(JournalFile)) File.Delete(JournalFile);
            var tmp = JournalFile + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch (Exception ex)
        {
            logging?.LogDebug($"GameBoost: no se pudo borrar el journal: {ex.Message}");
        }
    }
}