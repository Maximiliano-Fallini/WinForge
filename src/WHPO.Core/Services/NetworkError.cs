using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace WHPO.Core.Services;

/// <summary>
/// Clasifica un fallo de red para que la app no diga «sin conexión» cuando el
/// servidor respondió con un error, ni muestre una excepción técnica cuando la
/// máquina está realmente cortada.
///
/// La regla sale del tipo de excepción y no del texto:
/// - DNS caído, conexión rechazada, TLS que no cerró, timeout del HttpClient y
///   cualquier I/O cuya causa sea un socket → NO hay conexión.
/// - HTTP 4xx/5xx → el servidor respondió (aunque sea un error): hay conexión.
///   GitHub devolver 403 por límite de la API no es «sin internet».
/// - Un TaskCanceledException por cancelación del usuario tampoco es red: solo
///   cuenta si lo provocó el timeout del propio HttpClient.
///
/// Los textos que devuelve son claves de traducción (pasan por I18n.T), igual
/// que el resto de los mensajes de los servicios.
/// </summary>
public static class NetworkError
{
    /// <summary>Frase fuente que pide revisar la conexión (ya traducida en la app).</summary>
    public const string ConnectionHint = "Revisá tu conexión a internet e intentá de nuevo.";

    /// <summary>True solo si el fallo fue por no poder llegar al servidor.</summary>
    public static bool IsConnectionFailure(Exception? ex)
    {
        // Se recorren las causas internas: CopyToAsync, Task.Run y HttpClient
        // envuelven el socket real en una o dos capas. El tope evita un ciclo
        // patológico de excepciones auto-referenciadas.
        for (int depth = 0; ex != null && depth < 8; depth++)
        {
            switch (ex)
            {
                case SocketException:
                    return true;

                // Un 403/404/500 también llega como HttpRequestException, pero con
                // StatusCode: el servidor contestó. Solo es corte de red sin código.
                case HttpRequestException { StatusCode: not null }:
                    return false;

                case HttpRequestException:
                    return true;

                // Timeout del HttpClient (.NET 5+): TaskCanceled con TimeoutException
                // adentro. La cancelación del usuario no trae esa causa.
                case TaskCanceledException { InnerException: TimeoutException }:
                    return true;

                case AggregateException aggregate:
                    ex = aggregate.InnerException;
                    continue;
            }

            ex = ex.InnerException;
        }

        return false;
    }
}
