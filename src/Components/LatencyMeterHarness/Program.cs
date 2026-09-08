using WinForge.Component.LatencyMeter;
using System.Runtime.InteropServices;

// Harness de consola para el motor ETW DPC/ISR: arranca la sesión, consume 8 s
// y vuelca las métricas. Sirve para validar StartTrace/OpenTrace/ProcessTrace
// (y el formato de los eventos) sin depender de la UI de la app.

Console.WriteLine($"Admin: {Environment.IsPrivilegedProcess}");

// ===== Prueba 0: estructura base (sesión de archivo simple, no kernel) =====
// Aísla el problema: si esto falla, el bloque de propiedades está mal armado;
// si pasa, el problema es de los modos/flags del kernel.
{
    const string name0 = "WinForge-Harness-Base";
    string tmpLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf-harness-base.etl");
    var props0 = new Native.TraceProperties(name0, 0, Native.EVENT_TRACE_FILE_MODE_SEQUENTIAL, 1, logFileName: tmpLog);
    uint st0 = Native.StartTraceW(out _, name0, props0.Ptr);
    Console.WriteLine($"[base file session] StartTrace=0x{st0:X8}");
    if (st0 == 0)
    {
        var stop0 = new Native.TraceProperties(name0, 0, 0, 0);
        Native.ControlTraceW(0, name0, stop0.Ptr, Native.EVENT_TRACE_CONTROL_STOP);
        Console.WriteLine("  -> base OK y detenida");
    }
    props0.Dispose();
}

// ===== Prueba 1: igual que la base pero con SYSTEM_LOGGER_MODE =====
{
    const string name1 = "WinForge-Harness-SysLogger";
    var props1 = new Native.TraceProperties(name1, Native.EVENT_TRACE_FLAG_DPC | Native.EVENT_TRACE_FLAG_INTERRUPT,
        Native.EVENT_TRACE_REAL_TIME_MODE | Native.EVENT_TRACE_SYSTEM_LOGGER_MODE, 1);
    uint st1 = Native.StartTraceW(out _, name1, props1.Ptr);
    Console.WriteLine($"[system logger rt] StartTrace=0x{st1:X8}");
    if (st1 == 0)
    {
        var stop1 = new Native.TraceProperties(name1, 0, 0, 0);
        Native.ControlTraceW(0, name1, stop1.Ptr, Native.EVENT_TRACE_CONTROL_STOP);
        Console.WriteLine("  -> system logger OK y detenida");
    }
    props1.Dispose();
}

var trace = new DpcIsrTrace();

Console.WriteLine("Iniciando sesión ETW...");
if (!trace.Start())
{
    Console.WriteLine($"FALLO Start: {trace.LastError}");
    return 1;
}
Console.WriteLine("Sesión iniciada. Muestreando 8 segundos...");
Thread.Sleep(8000);
trace.Stop();
Console.WriteLine("Sesión detenida.");
if (trace.LastError is not null)
    Console.WriteLine($"Aviso consumer: {trace.LastError}");

var snap = trace.GetSnapshot();
var opcodes = trace.GetOpcodeCounts();
var (paired, unpaired) = trace.GetPairStats();
Console.WriteLine($"Pares: {paired} emparejados / {unpaired} sin par");
Console.WriteLine("=== Payloads crudos (primeros 3 por opcode) ===");
foreach (var s in trace.GetPayloadSamples()) Console.WriteLine($"  {s}");
var diag = trace.GetRawDiagnostics();
Console.WriteLine($"Diag: +{diag.PositiveCount} / -{diag.NegativeCount} | ancla QPC={diag.QpcAtStart} FT={diag.FiletimeAtStart}");
Console.WriteLine($"Diag: primeras 5 muestras (initialTime QPC, record FT, durTicks): ");
foreach (var s in diag.FirstSamples)
    Console.WriteLine($"  init={s.InitialTime}  rec={s.RecordTs}  dur={s.DurationTicks}");
Console.Write("Opcodes PerfInfo vistos: ");
for (int i = 0; i < opcodes.Length; i++) if (opcodes[i] > 0) Console.Write($"{i}={opcodes[i]} ");
Console.WriteLine();
Console.WriteLine($"DPC total: {snap.DpcCount} ({snap.DpcPerSec}/s) | ISR total: {snap.IsrCount}");
Console.WriteLine($"Pico DPC: {snap.MaxDpcUs:F1} µs | Promedio: {snap.AvgDpcUs:F1} µs | P99: {snap.P99DpcUs:F1} µs");
Console.WriteLine($"Peor módulo: {snap.WorstModule}");
Console.WriteLine("Top drivers:");
foreach (var d in snap.TopDrivers.Take(8))
    Console.WriteLine($"  {d.Module,-32} DPC={d.DpcCount,-9} max={d.MaxDpcUs,8:F1} µs total={d.TotalDpcUs,10:F1} µs");
return snap.DpcCount > 0 ? 0 : 2;
