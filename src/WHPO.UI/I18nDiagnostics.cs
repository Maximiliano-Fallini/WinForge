using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WHPO.Core;

namespace WHPO_UI;

/// <summary>
/// Bitácora del motor de traducciones: deja asentado en %LocalAppData%\WHPO\i18n.log
/// qué copia de la app se ejecutó, en qué idioma, con qué paquetes instalados y
/// cuántas claves conoce; y anota cualquier nodo que no se haya podido traducir.
///
/// Por qué un archivo aparte y no app.log: app.log depende del ajuste "Logs de
/// desarrollo", que viene APAGADO en la build instalada. Un reporte de "ciertos
/// textos quedan en español" llegaba sin un solo dato de la copia que lo mostraba.
/// Esta bitácora se escribe siempre (una línea por arranque, una por navegación y
/// una por fallo), con tope de tamaño para no crecer sin control.
/// </summary>
internal static class I18nDiagnostics
{
    /// <summary>Tope del archivo: al superarlo se reescribe desde cero (es la bitácora de las últimas corridas).</summary>
    private const long MaxBytes = 64 * 1024;

    /// <summary>Máximo de fallos de traducción anotados por corrida (el resto se cuenta).</summary>
    private const int MaxFailures = 20;

    /// <summary>Tope del volcado de páginas: al superarlo se descarta el anterior.</summary>
    private const long MaxDumpBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static int _failuresWritten;

    private static string FilePath => Path.Combine(AppPaths.RootDir, "i18n.log");

    /// <summary>
    /// Contexto de arranque: qué exe corre, con qué idioma quedó, cuántas claves
    /// conoce y qué packs tiene instalados. Con esto se responde sin adivinar "¿qué
    /// copia abriste y en qué idioma?".
    /// </summary>
    public static void RecordStartup(
        string exePath,
        string currentLanguage,
        int keyCount,
        string sourceHash,
        IEnumerable<string> languages,
        Func<string, string?> packVersion)
    {
        try
        {
            var available = (languages ?? Enumerable.Empty<string>()).ToArray();
            var packs = available
                .Select(code => (code, version: packVersion?.Invoke(code)))
                .Where(p => p.version != null)
                .Select(p => $"{p.code} v{p.version}");

            Append($"===== arranque ===== exe={exePath} | idioma={currentLanguage} | claves={keyCount} "
                 + $"| sourceHash={sourceHash} | packs=[{string.Join(", ", packs)}] | idiomas=[{string.Join(", ", available)}]");
        }
        catch { /* La bitácora no puede romper el arranque. */ }
    }

    /// <summary>Resumen de la traducción de una página al terminar sus pasadas.</summary>
    public static void RecordPass(string page, I18n.TranslatePass pass)
        => Append($"página={page} | {pass}");

    /// <summary>
    /// Volcado de los textos que el recorrido vio en la ventana: por cada elemento, el
    /// texto que tenía y qué se le aplicó. Es lo que permite responder "este texto quedó
    /// en español" sin poder mirar la pantalla, y distingue los tres casos posibles:
    /// cambió, la clave no tiene traducción para el idioma activo, o el texto no es una
    /// clave (nadie lo va a traducir nunca).
    /// </summary>
    public static void RecordPageDump(string phase, IEnumerable<string> lines)
    {
        try
        {
            var path = Path.Combine(AppPaths.RootDir, "i18n-pagina.log");
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.RootDir);
                if (File.Exists(path) && new FileInfo(path).Length > MaxDumpBytes)
                    File.Delete(path);

                var builder = new StringBuilder();
                builder.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {phase} =====");
                foreach (var line in lines) builder.AppendLine(line);
                File.AppendAllText(path, builder.ToString());
            }
        }
        catch { /* El volcado no puede romper la UI. */ }
    }

    /// <summary>Un nodo que no se pudo traducir (el recorrido lo aísla y sigue).</summary>
    public static void RecordFailure(string message)
    {
        try
        {
            if (_failuresWritten >= MaxFailures)
            {
                if (_failuresWritten == MaxFailures)
                {
                    _failuresWritten++;
                    Append($"... se omiten más fallos (tope de {MaxFailures} por corrida).");
                }
                return;
            }
            _failuresWritten++;
            Append(message);
        }
        catch { }
    }

    private static void Append(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.RootDir);
                var path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { /* Nunca romper la UI por escribir la bitácora. */ }
    }
}
