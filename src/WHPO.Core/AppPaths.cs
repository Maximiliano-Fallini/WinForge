using System;
using System.IO;

namespace WHPO.Core;

/// <summary>
/// Rutas y nombres de recursos de la app.
///
/// UNA sola carpeta de datos (%LocalAppData%\WHPO) para todas las copias: la
/// partición dev/instalada se eliminó a propósito. "Desarrollo" NO es una
/// carpeta ni una ruta: es un concepto de VERSIÓN — una build está en desarrollo
/// cuando va por delante de la última release publicada
/// (<see cref="Services.AppUpdateStatus.DevelopmentBuild"/>, que es lo que usa
/// el gating del Workshop para los componentes marcados "en desarrollo").
/// </summary>
public static class AppPaths
{
    /// <summary>Carpeta raíz de datos de la app en %LocalAppData%.</summary>
    public static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WHPO");

    /// <summary>Nombre del mutex de instancia única: una sola app en la máquina.</summary>
    public const string SingleInstanceMutexName = @"Local\WHPO.UI.SingleInstance";

    /// <summary>Nombre de la tarea programada de "Iniciar con Windows".</summary>
    public const string StartupTaskName = "WHPO";

    /// <summary>Nombre del valor en HKCU\...\Run (fallback de la tarea programada).</summary>
    public const string StartupRunValueName = "WHPO";
}
