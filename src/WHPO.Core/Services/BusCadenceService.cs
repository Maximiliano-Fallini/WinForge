using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>Resultado de una medición de cadencia por bus para UN teclado.</summary>
/// <param name="Reports">Transferencias de interrupción vistas para ese VID/PID.</param>
/// <param name="WindowSeconds">Ventana pedida (solo informativo).</param>
/// <param name="FloorMs">Piso de entrega: mediana de los 10 intervalos más cortos (ms). 0 = sin datos.</param>
/// <param name="MinMs">Intervalo más corto observado (ms). 0 = sin datos.</param>
/// <param name="Error">No nulo si la captura MISMA falló (ETW rechazado, etc.). Distingue
/// "el teclado no mandó nada" de "no se pudo medir", que la UI dice distinto.</param>
/// <param name="HostMedianMs">Camino del host kernel→app: mediana de (llegada a la app − sello
/// del kernel) por informe emparejado. 0 = no se pudo cruzar (sin sellos de la app o muy
/// pocos emparejados).</param>
/// <param name="HostP99Ms">P99 del camino del host.</param>
/// <param name="MatchedReports">Informes emparejados con confianza que sostienen el cruce.</param>
public sealed record BusCadenceResult(int Reports, int WindowSeconds, double FloorMs, double MinMs, string? Error = null,
    double HostMedianMs = 0, double HostP99Ms = 0, int MatchedReports = 0)
{
    public bool HasData => Reports > 0 && FloorMs > 0;
    public bool HasHostPath => HostMedianMs > 0 && MatchedReports >= 8;
}

/// <summary>
/// Cadencia de TECLADO medida en el BUS, no en la app. La magia del enfoque: el teclado solo
/// emite cuando cambia de estado, así que NO hay que forzarlo — con las transferencias que
/// genere el uso normal alcanza, porque cada una llega sellada por el KERNEL (ETW) y el piso
/// de esos sellos es el dato del endpoint que la app nunca puede ver por sí sola.
///
/// Pipeline validado a mano en esta máquina (ver tools/etw-spike/):
///  1) logman arranca una sesión ETW con Microsoft-Windows-USB-USBXHCI (traza de bus).
///  2) El usuario teclea normalmente durante la ventana.
///  3) tracerpt vuelca la traza a XML; EventID 41 = transferencia completada, con SlotId,
///     EndpointContextIndex y BytesTotal. EventID 4 (enumeración) trae SlotId + VID/PID.
///  4) Se casa el slot con el VID/PID elegido y se mide la cadencia de su endpoint IN.
///
/// Límite honesto: el controlador cuantiza los sellos (1 ms en esta máquina); por eso el
/// "piso" es la mediana de los 10 intervalos más cortos y no el mínimo absoluto, y períodos
/// por debajo de la cuantización solo se pueden probar como cota, no resolver.
/// </summary>
public sealed class BusCadenceService
{
    /// <summary>Duración por defecto de la captura, en segundos.</summary>
    public const int DefaultWindowSeconds = 12;

    private const string Provider = "Microsoft-Windows-USB-USBXHCI";
    private const long KeywordBus = 0x3006F;       // Default+Error+IRP+Performance+HeadersBusTrace+Device+Hub
    private const int EventTransferCompleted = 41; // SlotId + EndpointContextIndex + BytesTotal
    private const int EventDeviceEnumerated = 4;   // SlotId + idVendor + idProduct
    private const int KeyboardReportBytes = 8;     // informe boot de teclado (modificadores + 6 teclas)

    private readonly ILoggingService _logging;

    public BusCadenceService(ILoggingService logging) => _logging = logging;

    /// <summary>Reloj de la app en µs desde el arranque (QPC): el MISMO que usa el monitor de
    /// Raw Input para sellar las llegadas. Stopwatch.GetTimestamp comparte origen con QPC, así
    /// que los dos números son comparables sin conversión.</summary>
    private static long QpcUs() => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

    /// <summary>Reloj de pared preciso (100 ns), misma base que la hora que trae la traza ETW.
    /// Ticks de FILETIME (1601) → ticks de DateTime (año 1) para poder restarlo de lo que
    /// devuelve DateTime.TryParse sobre el SystemTime de tracerpt.</summary>
    private static long WallUs()
    {
        GetSystemTimePreciseAsFileTime(out long fileTime);
        return (fileTime + 504_911_232_000_000_000L) / 10;
    }

    [DllImport("kernel32.dll")]
    private static extern void GetSystemTimePreciseAsFileTime(out long lpSystemTimeAsFileTime);

    /// <summary>
    /// Mide la cadencia del teclado con ese DeviceId ("VID_XXXX&amp;PID_YYYY") durante la ventana.
    /// El usuario teclea con normalidad: no hace falta aplastar teclas ni trucos.
    /// <paramref name="appStampsProvider"/> (opcional) devuelve los sellos QPC (µs) de llegada a
    /// la app por informe (InputLatencyMonitorService.EndSamplingStamps); se invoca al terminar
    /// la ventana y sus sellos se cruzan con los del kernel para medir el camino del host
    /// (kernel → Raw Input), la parte del retardo que sí es visible por software.
    /// La ventana dura LO QUE DICE, sin cortes por cantidad de informes: medir es usar la PC con
    /// normalidad y el usuario sigue su ritmo, así que el largo de la captura no puede depender de
    /// cuánto tecleó (y una duración fija es lo que hace comparables dos corridas del mismo equipo).
    /// Sin ninguna pulsación no hay nada que cronometrar —un teclado que informa por cambios solo
    /// habla cuando lo tocás—: ahí el resultado informa que faltaron datos, en vez de inventar un
    /// número.
    /// </summary>
    public async Task<BusCadenceResult> MeasureAsync(string deviceId, int seconds, CancellationToken ct,
        Func<long[]>? appStampsProvider = null)
    {
        seconds = Math.Clamp(seconds, 5, 60);
        string etl = Path.Combine(Path.GetTempPath(), $"whpo-buscadence-{Guid.NewGuid():N}.etl");
        try
        {
            // Alineación de relojes para el cruce kernel → app: el monitor sella con QPC
            // (µs desde el arranque) y la traza ETW trae hora de pared (año 1). Sin este
            // desfase los dos lados no se pueden comparar nunca (por eso el cruce daba "--").
            // Dos muestras, al abrir y al cerrar la ventana: el promedio cancela el ruido.
            long clockOffsetUs = WallUs() - QpcUs();

            // Sesión previa colgada de una corrida anterior: matarla en silencio.
            await Task.Run(() => RunHidden("logman", "stop whpo-buscadence -ets"), CancellationToken.None).ConfigureAwait(false);
            if (!RunHidden("logman", $"start whpo-buscadence -ets -p {Provider} 0x{KeywordBus:X} 5 -o \"{etl}\" -max 200 -ct perf"))
            {
                _logging.LogWarning("Cadencia por bus: logman no pudo arrancar la sesión ETW.");
                return new BusCadenceResult(0, seconds, 0, 0, "etw-start");
            }

            try
            {
                // Ventana FIJA (con el clamp de arriba): no se corta antes ni por cantidad de
                // informes. Cancelar es la única salida temprana, y en ese caso se analiza lo que
                // haya en la traza.
                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < deadline)
                    await Task.Delay(250, ct).ConfigureAwait(false);

                _logging.LogInfo($"Cadencia por bus: ventana completa de {seconds} s.");
            }
            catch (OperationCanceledException) { /* cancelar = medir lo que haya */ }

            RunHidden("logman", "stop whpo-buscadence -ets");
            if (!File.Exists(etl))
            {
                _logging.LogWarning("Cadencia por bus: la traza no quedó escrita.");
                return new BusCadenceResult(0, seconds, 0, 0, "no-trace");
            }

            // Sellos de la app AL TERMINAR la ventana (la captura la abrió el llamador con
            // BeginSampling antes de entrar acá). Si la captura de la app quedó vacía NO se
            // aborta: el piso de cadencia sale de la traza del kernel y no depende de que
            // Raw Input haya entregado los informes en esta máquina. Solo se pierde el cruce
            // kernel → app (el camino del host), que es un dato extra.
            long[]? appStamps = appStampsProvider?.Invoke();
            if (appStamps is { Length: 0 })
            {
                _logging.LogInfo("Cadencia por bus: la app no registró informes en la ventana; se analiza solo la traza del kernel.");
                appStamps = null;
            }

            // Segunda muestra del desfase al cerrar la ventana: promedio con la de apertura.
            clockOffsetUs = (clockOffsetUs + (WallUs() - QpcUs())) / 2;

            return await Task.Run(() => Analyze(etl, deviceId, appStamps, clockOffsetUs), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(etl)) File.Delete(etl); } catch { /* temp */ }
        }
    }

    /// <summary>Vuelca la traza a XML y extrae el piso de entrega del teclado pedido; con
    /// sellos de la app, además mide el camino del host por emparejamiento por orden.</summary>
    private BusCadenceResult Analyze(string etl, string deviceId, long[]? appStampsUs, long clockOffsetUs)
    {
        string xml = Path.ChangeExtension(etl, ".xml");
        try
        {
            if (!RunHidden("tracerpt", $"\"{etl}\" -o \"{xml}\" -of XML -y") || !File.Exists(xml))
            {
                _logging.LogWarning("Cadencia por bus: tracerpt no pudo volcar la traza.");
                return new BusCadenceResult(0, 0, 0, 0, "dump");
            }

            var (reports, windowSeconds) = Parse(xml, deviceId, out bool hadTransfers);
            if (reports.Count == 0)
            {
                // Distinguir "la traza no vio NINGUNA transferencia" de "vio transferencias
                // pero no pudo atribuirlas al teclado" (sin evento de enumeración en la
                // ventana el mapeo SlotId→VID/PID no existe: el teclado ya estaba enchufado
                // cuando arrancó la captura). La UI dice cosas distintas porque la salida es
                // distinta: en un caso hay que teclear, en el otro reconectar el teclado.
                if (hadTransfers)
                {
                    _logging.LogWarning($"Cadencia por bus: la traza no permitió identificar el teclado {deviceId} (sin evento de enumeración en la ventana).");
                    return new BusCadenceResult(0, 0, 0, 0, "no-slot");
                }
                _logging.LogInfo($"Cadencia por bus: sin transferencias de teclado para {deviceId}.");
                return new BusCadenceResult(0, 0, 0, 0);
            }

            reports.Sort();
            var gaps = new List<double>(reports.Count);
            for (int i = 1; i < reports.Count; i++)
            {
                double d = reports[i] - reports[i - 1];
                if (d > 0) gaps.Add(d);
            }
            if (gaps.Count == 0)
                return new BusCadenceResult(reports.Count, windowSeconds, 0, 0); // un solo informe: sin intervalos

            gaps.Sort();
            var shortest = gaps.Take(10).ToList();        // el piso: los 10 más cortos
            double floor = shortest[shortest.Count / 2];  // mediana de ese grupo (anti-cuantización)

            // Cruce con los sellos de la app: el camino del host (kernel → Raw Input).
            var (hostMed, hostP99, matched) = appStampsUs is { Length: >= 8 }
                ? CrossHostPath(appStampsUs, reports, clockOffsetUs)
                : (0.0, 0.0, 0);

            // Los timestamps son µs (dt.Ticks/10); el contrato del record es ms.
            return new BusCadenceResult(reports.Count, windowSeconds, floor / 1000.0, gaps[0] / 1000.0,
                Error: null, HostMedianMs: hostMed / 1000.0, HostP99Ms: hostP99 / 1000.0, MatchedReports: matched);
        }
        finally
        {
            try { if (File.Exists(xml)) File.Delete(xml); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// Lee el XML de tracerpt: resuelve el SlotId del teclado con ese VID/PID (por los eventos
    /// de enumeración) y devuelve los timestamps (µs) de las transferencias completadas de su
    /// endpoint IN (informes de 8 bytes). El segundo valor es la ventana efectiva de la traza.
    /// Formato verificado contra el volcado real de esta máquina: los campos viajan como
    /// &lt;Data Name="fid_SlotId"&gt;       2&lt;/Data&gt; — cierre con '&gt;' plano (NO la entidad
    /// &amp;gt;) y valores numéricos con relleno de espacios a la izquierda.
    /// </summary>
    private static (List<double> ReportsUs, int WindowSeconds) Parse(string xmlPath, string deviceId, out bool hadTransfers)
    {
        hadTransfers = false;
        var slotOfVidPid = new Dictionary<int, string>();
        var transfers = new Dictionary<string, List<(double tUs, int bytes)>>(StringComparer.Ordinal);
        double firstUs = double.MaxValue, lastUs = double.MinValue;
        string wanted = deviceId.ToUpperInvariant().Replace(" ", "");

        foreach (var block in File.ReadAllText(xmlPath).Split("<Event ").Skip(1))
        {
            if (!block.Contains("USBXHCI")) continue;

            var idMatch = Regex.Match(block, @"<EventID>(\d+)</EventID>");
            if (!idMatch.Success) continue;
            int id = int.Parse(idMatch.Groups[1].Value);

            if (id == EventDeviceEnumerated)
            {
                string? vid = Data(block, "fid_idVendor");
                string? pid = Data(block, "fid_idProduct");
                string? slot = Data(block, "fid_SlotId");
                if (vid != null && pid != null && slot != null)
                {
                    string key = $"VID_{ParseHexOrDec(vid):X4}&PID_{ParseHexOrDec(pid):X4}";
                    slotOfVidPid[int.Parse(slot)] = key;
                }
            }
            else if (id == EventTransferCompleted)
            {
                string? slot = Data(block, "fid_SlotId");
                string? dci = Data(block, "fid_EndpointContextIndex");
                string? bytes = Data(block, "fid_BytesTotal");
                string time = Regex.Match(block, @"TimeCreated SystemTime=""([^""]+)""").Groups[1].Value;
                if (slot == null || dci == null || time.Length == 0) continue;
                if (!DateTime.TryParse(time, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                    continue;

                string key = $"{slot}/{dci}";
                if (!transfers.TryGetValue(key, out var list))
                    transfers[key] = list = new List<(double, int)>();
                double tUs = dt.Ticks / 10.0; // 100 ns -> µs
                hadTransfers = true; // hay transferencias en la traza (aunque no se puedan atribuir)
                list.Add((tUs, bytes != null ? (int)ParseHexOrDec(bytes) : -1));
                if (tUs < firstUs) firstUs = tUs;
                if (tUs > lastUs) lastUs = tUs;
            }
        }

        var empty = new List<double>();
        int targetSlot = slotOfVidPid.FirstOrDefault(kv => wanted.Contains(kv.Value)).Key;
        if (targetSlot == 0)
            return (empty, 0);

        // Endpoint IN de interrupción del teclado: DCI impar (par = OUT). Preferir DCI 3 con
        // informes de 8 bytes; si no aparece, el IN con más tráfico de 8 bytes de ese slot, y
        // como último recurso el IN con más tráfico de cualquier tamaño (teclados con informes
        // de más de 8 bytes: multimedia, NKRO).
        var candidates = transfers
            .Where(kv => kv.Key.StartsWith(targetSlot + "/", StringComparison.Ordinal))
            .Select(kv => (Dci: int.Parse(kv.Key.Split('/')[1]), List: kv.Value))
            .Where(t => t.Dci % 2 == 1)
            .OrderBy(t => t.Dci == 3 ? 0 : 1)
            .ThenByDescending(t => t.List.Count(r => r.bytes == KeyboardReportBytes));

        foreach (var c in candidates)
        {
            var times = c.List.Where(r => r.bytes == KeyboardReportBytes).Select(r => r.tUs).ToList();
            if (times.Count >= 2)
            {
                int window = (int)Math.Round((lastUs - firstUs) / 1_000_000.0);
                return (times, window);
            }
        }
        // Fallback: el IN con más tráfico de ese slot, sea cual sea el tamaño del informe.
        foreach (var c in candidates)
        {
            if (c.List.Count >= 2)
            {
                int window = (int)Math.Round((lastUs - firstUs) / 1_000_000.0);
                return (c.List.Select(r => r.tUs).ToList(), window);
            }
        }
        return (empty, 0);
    }

    /// <summary>
    /// Cruce kernel → app: para cada sello de la app, el kernel-sello más cercano ANTERIOR
    /// (ese informe es el que generó la llegada); la diferencia es el tramo de host para ese
    /// informe. Con informes emparejados >= 8 y media/p99 estables, el número es defendible:
    /// incluye un sesgo fijo (el retraso del volcado: sellos de tracerpt, resolución ~1 ms) que
    /// se CANCELA en comparaciones A/B — el uso honesto es "antes/después de un tweak".
    /// </summary>
    private static (double MedianUs, double P99Us, int Matched) CrossHostPath(long[] appStampsUs, List<double> kernelStampsUs, long clockOffsetUs)
    {
        var deltas = new List<double>();
        foreach (var rawAppUs in appStampsUs)
        {
            // Sello de la app (QPC, desde el arranque) → hora de pared, la base de la traza.
            double a = rawAppUs + clockOffsetUs;
            // Búsqueda binaria del kernel-sello mayor estricto más cercano por debajo de a.
            int lo = 0, hi = kernelStampsUs.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (kernelStampsUs[mid] < a) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (best < 0) continue; // no hay kernel-sello anterior (p. ej. el volcado arranca tarde)
            double delta = a - kernelStampsUs[best];
            if (delta >= 0 && delta <= 50_000) deltas.Add(delta); // 50 ms de techo de coherencia
        }
        if (deltas.Count < 8) return (0, 0, deltas.Count);
        deltas.Sort();
        double median = deltas[deltas.Count / 2];
        double p99 = deltas[Math.Min(deltas.Count - 1, (int)Math.Ceiling(deltas.Count * 0.99) - 1)];
        return (median, p99, deltas.Count); 
    }

    /// <summary>Valor del campo &lt;Data Name="..."&gt; de un bloque de evento de tracerpt,
    /// sin el relleno de espacios que trae el volcado.</summary>
    private static string? Data(string block, string name)
    {
        var m = Regex.Match(block, "<Data Name=\"" + name + "\">([^<]*)</Data>");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>Parsea un número que puede venir en decimal (" 23") o hex ("0x54C").</summary>
    private static long ParseHexOrDec(string value)
    {
        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(value[2..], 16)
            : long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Corre un exe de sistema oculto; true si existió y terminó en tiempo.</summary>
    private static bool RunHidden(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return false;
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
