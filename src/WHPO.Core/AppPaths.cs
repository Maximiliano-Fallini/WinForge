using System;
using System.IO;

namespace WHPO.Core;

/// <summary>
/// Rutas y nombres de recursos de la app, con separación entre la build de
/// DESARROLLO (carpeta bin\Debug|Release del repo) y la INSTALADA (MSI).
///
/// Sin esto, las dos copias pisan la misma data: settings.json, macros.json,
/// cachés del Workshop/biblioteca, app.log, y hasta se expulsan entre sí por
/// el mutex de instancia única (abrir la de desarrollo cierra la instalada).
/// Con la separación pueden convivir: la de desarrollo usa "WHPO-Dev".
///
/// Detección: flag de entorno WHPO_DEV (fuerza el modo) o heurística de ruta:
/// el exe corre desde un subdirectorio de bin\Debug o bin\Release del repo.
/// El publish del instalador (carpeta plana "publish") se clasifica como
/// instalada a propósito: prueba el flujo de la versión que se va a distribuir.
/// </summary>
public static class AppPaths
{
    private static readonly bool _isDev = DetectDevBuild();

    /// <summary>True si esta copia es una build de desarrollo (fuera del MSI).</summary>
    public static bool IsDevBuild => _isDev;

    /// <summary>
    /// Carpeta raíz de datos de la app en %LocalAppData%: "WHPO" para la
    /// instalada, "WHPO-Dev" para la de desarrollo.
    /// </summary>
    public static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        _isDev ? "WHPO-Dev" : "WHPO");

    /// <summary>Nombre del mutex de instancia única (una por copia, no compartido).</summary>
    public static string SingleInstanceMutexName => _isDev
        ? @"Local\WHPO.UI.SingleInstance.Dev"
        : @"Local\WHPO.UI.SingleInstance";

    /// <summary>Nombre de la tarea programada de "Iniciar con Windows".</summary>
    public static string StartupTaskName => _isDev ? "WHPO-Dev" : "WHPO";

    /// <summary>Nombre del valor en HKCU\...\Run (fallback de la tarea programada).</summary>
    public static string StartupRunValueName => _isDev ? "WHPO-Dev" : "WHPO";

    private static bool DetectDevBuild()
    {
        try
        {
            // 1) Flag explícito: WHPO_DEV=1 fuerza desarrollo, WHPO_DEV=0 fuerza instalada.
            var flag = Environment.GetEnvironmentVariable("WHPO_DEV");
            if (!string.IsNullOrEmpty(flag))
                return !flag.Equals("0", StringComparison.OrdinalIgnoreCase);

            // 2) Heurística: subir desde la carpeta del exe buscando bin\Debug|Release.
            var dir = AppContext.BaseDirectory;
            var cur = new DirectoryInfo(dir);
            for (int i = 0; i < 8 && cur != null; i++)
            {
                var parent = cur.Parent;
                if (parent != null
                    && parent.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    && (cur.Name.Equals("Debug", StringComparison.OrdinalIgnoreCase)
                        || cur.Name.Equals("Release", StringComparison.OrdinalIgnoreCase)))
                    return true;
                // Carpeta "publish": salida del instalador → clasificar como instalada.
                if (cur.Name.Equals("publish", StringComparison.OrdinalIgnoreCase))
                    return false;
                cur = parent;
            }
        }
        catch
        {
            // Ante cualquier duda, tratar como instalada (comportamiento histórico).
        }
        return false;
    }
}
