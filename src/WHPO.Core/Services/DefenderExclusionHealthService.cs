using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WHPO.Core.Services;

/// <summary>Qué se está verificando.</summary>
public enum DefenderExclusionTarget
{
    /// <summary>El ejecutable de la app: lo que Defender bloquearía o borraría.</summary>
    AppExecutable,

    /// <summary>Carpeta de datos de la app (%LocalAppData%\WHPO o \WHPO-Dev).</summary>
    DataFolder,
}

public enum DefenderExclusionState
{
    /// <summary>Cubierto: Defender no va a tocar eso.</summary>
    Present,

    /// <summary>El ejecutable está cubierto, pero falta otra exclusión (la de datos).</summary>
    Partial,

    /// <summary>El ejecutable NO está cubierto: Defender podría bloquear o borrar la app.</summary>
    Missing,

    /// <summary>No se pudo leer la configuración (clave protegida o sin permisos).</summary>
    Unreadable,

    /// <summary>Este equipo no tiene Windows Defender: la exclusión no aplica y no es un problema.</summary>
    NotApplicable,
}

/// <summary>
/// Una verificación y su resultado. <paramref name="Value"/> es lo que se buscó y
/// <paramref name="MatchedBy"/> la exclusión que lo cubre (puede ser el propio archivo,
/// su carpeta, una carpeta superior o el nombre del proceso): sin ese dato, un "Cubierto"
/// por una carpeta superior parecería magia.
/// </summary>
public sealed record DefenderExclusionItem(
    DefenderExclusionTarget Target,
    string Value,
    DefenderExclusionState State,
    string? MatchedBy = null);

public sealed record DefenderExclusionHealth(DefenderExclusionState Overall, IReadOnlyList<DefenderExclusionItem> Items);

/// <summary>
/// Salud de las exclusiones de Windows Defender para WinForge: las que el instalador
/// agrega (carpeta de instalación, carpeta de datos y nombre del proceso) para que
/// Defender no marque la app como sospechosa.
///
/// Se lee del REGISTRO y no con Get-MpPreference por dos razones concretas:
///   · es la misma fuente que verifica el instalador, y
///   · Get-MpPreference exige lanzar PowerShell (cientos de ms); esto se consulta cada
///     medio segundo y tiene que ser barato.
///
/// Dos detalles que costaron caro y por eso quedan escritos:
///   · Defender guarda cada exclusión como un VALOR de la clave (el NOMBRE del valor es
///     la ruta o el ejecutable), no como una subclave. Leer subclaves hacía que la app
///     dijera "falta la exclusión" con la exclusión puesta.
///   · Una exclusión de CARPETA cubre todo lo que está adentro, así que un archivo está
///     cubierto por su ruta, por su carpeta o por cualquier carpeta superior. Comparar
///     solo la igualdad exacta marcaba como "falta" una app ya protegida.
/// </summary>
public static class DefenderExclusionHealthService
{
    private const string DefenderKeyPath = @"SOFTWARE\Microsoft\Windows Defender";
    private const string PathsSubKey = @"Exclusions\Paths";
    private const string ProcessesSubKey = @"Exclusions\Processes";

    /// <summary>
    /// Verifica la cobertura del ejecutable y de la carpeta de datos. Nunca lanza: un
    /// fallo de lectura se informa como "no se pudo leer", que NO es lo mismo que "falta"
    /// (el primero puede ser una clave protegida; el segundo es una acción pendiente).
    /// </summary>
    public static DefenderExclusionHealth Check(string exePath, string dataFolder, string processName)
    {
        try
        {
            using var defender = Registry.LocalMachine.OpenSubKey(DefenderKeyPath);
            if (defender is null)
                return Everything(DefenderExclusionState.NotApplicable, exePath, dataFolder);

            var paths = ReadExclusions(defender, PathsSubKey);
            var processes = ReadExclusions(defender, ProcessesSubKey);
            if (paths is null || processes is null)
                return Everything(DefenderExclusionState.Unreadable, exePath, dataFolder);

            // El ejecutable puede estar cubierto por su ruta (él mismo, su carpeta o una
            // carpeta superior) o por el nombre del proceso: cualquiera de las dos formas
            // evita que Defender lo bloquee, así que las dos cuentan.
            var exeBy = FindPathCoverage(paths, exePath);
            if (exeBy is null && Contains(processes, processName)) exeBy = processName;

            var dataBy = FindPathCoverage(paths, dataFolder);

            var items = new List<DefenderExclusionItem>
            {
                new(DefenderExclusionTarget.AppExecutable, exePath,
                    exeBy is null ? DefenderExclusionState.Missing : DefenderExclusionState.Present, exeBy),
                new(DefenderExclusionTarget.DataFolder, dataFolder,
                    dataBy is null ? DefenderExclusionState.Missing : DefenderExclusionState.Present, dataBy),
            };

            // El veredicto lo manda el ejecutable: si está cubierto, la app no va a quedar
            // bloqueada (lo que falte es una precaución, no un riesgo) — decir "podría
            // bloquear WinForge" en ese caso sería una alarma falsa.
            var overall = items[0].State == DefenderExclusionState.Missing
                ? DefenderExclusionState.Missing
                : items.Any(i => i.State == DefenderExclusionState.Missing)
                    ? DefenderExclusionState.Partial
                    : DefenderExclusionState.Present;

            return new DefenderExclusionHealth(overall, items);
        }
        catch (Exception)
        {
            return Everything(DefenderExclusionState.Unreadable, exePath, dataFolder);
        }
    }

    private static DefenderExclusionHealth Everything(DefenderExclusionState state, string exePath, string dataFolder)
        => new(state, new List<DefenderExclusionItem>
        {
            new(DefenderExclusionTarget.AppExecutable, exePath, state),
            new(DefenderExclusionTarget.DataFolder, dataFolder, state),
        });

    /// <summary>
    /// Nombres de las exclusiones configuradas. Defender las guarda como VALORES de la
    /// clave (el nombre del valor es la ruta o el ejecutable, con el dato en 0) — así las
    /// lee el instalador. Se leen TAMBIÉN las subclaves porque hay versiones/políticas que
    /// las escriben así, y sumar esa forma no puede inventar un "presente".
    ///
    /// Devuelve null si la clave no se pudo abrir por un error real (queda como "no se pudo
    /// leer"); si simplemente no existe, devuelve una lista vacía ("no hay ninguna").
    /// </summary>
    private static List<string>? ReadExclusions(RegistryKey defender, string relativePath)
    {
        using var key = defender.OpenSubKey(relativePath);
        if (key is null) return new List<string>();

        var names = key.GetValueNames()
            .Where(n => !string.IsNullOrWhiteSpace(n))   // el valor "(Predeterminado)" viene vacío
            .ToList();
        names.AddRange(key.GetSubKeyNames());
        return names;
    }

    /// <summary>
    /// La exclusión que cubre una ruta, o null. Se sube desde la ruta hasta la raíz: una
    /// exclusión de carpeta cubre todo lo que está adentro, así que sirve el archivo mismo,
    /// su carpeta o cualquier carpeta superior.
    /// </summary>
    private static string? FindPathCoverage(List<string> excluded, string path)
    {
        var current = Normalize(path);
        while (current.Length > 0)
        {
            var match = excluded.FirstOrDefault(e => Normalize(e).Equals(current, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) break;
            current = Normalize(parent);
        }
        return null;
    }

    private static bool Contains(List<string> excluded, string value)
        => excluded.Any(e => Normalize(e).Equals(Normalize(value), StringComparison.OrdinalIgnoreCase));

    /// <summary>Comparación como la hace Windows: sin distinguir mayúsculas, sin comillas y
    /// sin la barra final (la app aporta "C:\...\win-x64\" y Defender guarda sin la barra).</summary>
    private static string Normalize(string value)
        => value.Trim().Trim('"').TrimEnd('\\', '/');
}
