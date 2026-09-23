using System;
using System.Diagnostics;
using System.Security.Principal;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Services;

/// <summary>
/// Abre una URL externa (GitHub, buscadores, paneles) con el navegador del usuario.
///
/// Por qué no alcanza con Process.Start: WinForge corre SIEMPRE elevada (el manifest pide
/// administrador) y desde un proceso elevado ShellExecute de una URL puede fallar sin
/// devolver error — la asociación del navegador vive en el contexto del usuario sin elevar
/// (instalaciones por usuario, handlers empaquetados) —, así que el clic no abre nada y
/// tampoco queda una excepción que loguear. explorer.exe corre sin elevar y resuelve la
/// asociación como el usuario, por eso es el primer camino cuando la app está elevada y el
/// respaldo cuando no lo está (ahí ShellExecute es el camino directo).
///
/// Devuelve false solo si ningún camino pudo lanzar la apertura: el llamador decide qué
/// mostrar en ese caso (el onboarding, por ejemplo, deja el enlace para copiar a mano).
/// </summary>
internal static class UrlOpener
{
    /// <summary>
    /// Abre <paramref name="url"/> en el navegador predeterminado. <paramref name="logging"/>
    /// es opcional: cada camino fallido deja su motivo en el log para diagnosticar sin depurar.
    /// </summary>
    public static bool Open(string url, ILoggingService? logging = null)
    {
        // Solo http/https: explorer.exe no debe recibir cualquier cadena como argumento.
        if (!IsHttpUrl(url)) return false;

        return IsElevated()
            ? TryExplorer(url, logging) || TryShellExecute(url, logging)
            : TryShellExecute(url, logging) || TryExplorer(url, logging);
    }

    private static bool IsHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// La URL se le pasa al shell ya en ejecución (explorer.exe es de instancia única: la
    /// invocación nueva le entrega el argumento y termina), que la abre como el usuario sin
    /// elevar. Va entre comillas porque puede traer '?' y '&amp;' en la query.
    /// </summary>
    private static bool TryExplorer(string url, ILoggingService? logging)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\""));
            return process != null;
        }
        catch (Exception ex)
        {
            logging?.LogInfo($"UrlOpener: explorer.exe no pudo abrir {url}: {ex.Message}");
            return false;
        }
    }

    private static bool TryShellExecute(string url, ILoggingService? logging)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return process != null;
        }
        catch (Exception ex)
        {
            logging?.LogInfo($"UrlOpener: no hay asociación para abrir {url}: {ex.Message}");
            return false;
        }
    }
}
