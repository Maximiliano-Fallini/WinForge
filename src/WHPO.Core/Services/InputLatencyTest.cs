using System;

namespace WHPO.Core.Services;

/// <summary>
/// Veredicto de una prueba de latencia. No juzga "velocidad" (eso lo dice el Hz
/// medido): juzga REGULARIDAD — si los informes llegan parejos o con huecos.
/// </summary>
public enum InputLatencyVerdict
{
    /// <summary>Menos informes que el mínimo: la muestra no alcanza para juzgar.</summary>
    InsufficientData,

    /// <summary>
    /// El dispositivo no emite un flujo continuo: los intervalos los marca el USO, no el
    /// polling. Es el caso de un teclado (solo manda informe cuando cambia de estado) o de
    /// un mando que solo recibe pulsaciones sueltas de botones. En ese caso el Hz medido es
    /// el ritmo de tus eventos, NO la tasa de sondeo: informarlo como polling sería mentir.
    /// Se detecta automáticamente por la dispersión de la serie, sin asumir el tipo.
    /// </summary>
    NoContinuousStream,

    /// <summary>Huecos o picos marcados: el dispositivo o el bus no sostienen la tasa.</summary>
    Unstable,

    /// <summary>Algún hueco o pico aislado, dentro de lo razonable.</summary>
    Acceptable,

    /// <summary>Informes parejos y sin huecos: el dispositivo sostiene la tasa.</summary>
    Excellent
}

/// <summary>
/// Resultado de una prueba de latencia para UN dispositivo. Igual que el medidor en
/// vivo, "latencia" acá es el intervalo entre informes HID consecutivos (= 1/polling
/// real, medido con QPC): NO incluye la latencia del juego, del cable ni del
/// compositor de Windows.
///
/// <paramref name="Handle"/> (VID/PID) es la clave de comparación entre corridas: el
/// handle crudo de Raw Input <paramref name="DeviceId"/> cambia cada vez que el
/// dispositivo se reinicia, así que dos corridas del mismo periférico solo se pueden
/// correlacionar por VID/PID.
/// </summary>
public record InputLatencyTestResult(
    InputDeviceKind Kind,
    string Name,
    string Handle,            // "VID_xxxx&PID_xxxx": sobrevive al reinicio del dispositivo
    long DeviceId,            // handle crudo de Raw Input de ESTA corrida
    int Samples,              // intervalos medidos
    int Reports,              // informes que representan (ranuras del período)
    int Gaps,                 // ranuras sin informe dentro de la cadencia
    double Hz,                // tasa real medida (1000 / media)
    double AvgMs,
    double MinMs,
    double MaxMs,
    double P1Ms,              // 1% de los informes llegó más rápido que esto
    double MedianMs,
    double P99Ms,             // 1% de los informes llegó más lento que esto (jitter)
    double GapPercent,        // huecos como % de los informes
    InputLatencyVerdict Verdict,
    DateTimeOffset Timestamp);

/// <summary>
/// Análisis de una serie de intervalos entre informes. Es una función pura sobre los
/// intervalos (sin Raw Input, sin UI) para poder verificarla con series sintéticas:
/// el veredicto no puede depender de tener el hardware delante.
///
/// Los umbrales son juicios explícitos, no constantes físicas: están acá arriba para
/// poder discutirlos y ajustarlos sin tocar el resto del test.
/// </summary>
public static class InputLatencyAnalysis
{
    /// <summary>Intervalos mínimos para que un veredicto signifique algo: con menos,
    /// cualquier conclusión es ruido de la muestra.</summary>
    public const int MinSamples = 200;

    /// <summary>Huecos (en % de los informes) que ya marcan inestabilidad.</summary>
    public const double UnstableGapPercent = 0.5;

    /// <summary>Pico máximo tolerable, en múltiplos del período mediano.</summary>
    public const double UnstableMaxRatio = 4.0;

    /// <summary>Huecos (en %) por debajo de los cuales la serie es impecable.</summary>
    public const double ExcellentGapPercent = 0.05;

    /// <summary>Pico máximo de una serie impecable, en múltiplos del período mediano.</summary>
    public const double ExcellentMaxRatio = 1.8;

    /// <summary>
    /// Dispersión (desviación estándar / media) por encima de la cual la serie NO es un
    /// flujo continuo. Un mouse o un mando con el stick girando reportan a la tasa de sondeo
    /// con jitter chico: la dispersión ronda 0,02–0,15. Un teclado (o un mando quieto al que
    /// solo le apretás un botón) tiene intervalos que dependen de la mano: la dispersión
    /// pasa de 0,4. El corte en 0,35 los separa sin asumir qué tipo de dispositivo es.
    /// </summary>
    public const double ContinuityCvThreshold = 0.35;

    /// <summary>
    /// Corte, en múltiplos del período DECLARADO por el dispositivo, a partir del cual lo medido
    /// ya no puede ser su flujo de sondeo. Un teclado informa cuando cambia de estado —y su
    /// firmware puede repetir mientras mantenés una tecla—, y un mouse solo cuando se mueve: el
    /// intervalo mediano queda muy por encima del período. Cuatro ranuras dejan pasar el jitter y
    /// el redondeo del reloj, y cortan el ritmo de uso.
    /// </summary>
    public const double UsageDrivenSlots = 4.0;

    /// <summary>
    /// Ajusta el veredicto con lo que el dispositivo DECLARA en su descriptor (bInterval → Hz).
    /// Sin este ajuste, un teclado que repite regular a ~30 Hz daba "Excelente": un verde que se
    /// lee como "este teclado responde a 1 ms" cuando lo medido es la mano del usuario. Con el
    /// período declarado, una serie mucho más lenta que él pasa a "sin flujo continuo" (gris),
    /// que es lo que de verdad se midió. Sin tasa declarada (no es USB, o el hub no la dio) no hay
    /// referencia que aplicar y el veredicto queda como estaba.
    /// </summary>
    public static InputLatencyVerdict RefineVerdict(InputLatencyVerdict verdict, double medianMs, int declaredHz)
    {
        if (declaredHz <= 0 || medianMs <= 0) return verdict;
        if (verdict is InputLatencyVerdict.InsufficientData or InputLatencyVerdict.NoContinuousStream)
            return verdict;
        return medianMs >= 1000.0 / declaredHz * UsageDrivenSlots
            ? InputLatencyVerdict.NoContinuousStream
            : verdict;
    }

    /// <summary>Percentil sobre un arreglo YA ordenado (interpolación lineal entre los
    /// dos vecinos). Única implementación: la usan el medidor en vivo y el test.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        double idx = p * (sorted.Length - 1);
        int lo = (int)idx;
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    /// <summary>
    /// Analiza los intervalos (ms) de un dispositivo. Devuelve null solo si no hay
    /// ningún intervalo; con pocos informes devuelve un resultado con el veredicto
    /// <see cref="InputLatencyVerdict.InsufficientData"/> (la UI necesita poder decir
    /// "faltan datos" en vez de mostrar una fila vacía).
    /// </summary>
    public static InputLatencyTestResult? Analyze(
        InputDeviceKind kind,
        string name,
        string handle,
        long deviceId,
        double[] intervalsMs,
        DateTimeOffset timestamp)
    {
        if (intervalsMs == null || intervalsMs.Length == 0) return null;

        var sorted = (double[])intervalsMs.Clone();
        Array.Sort(sorted);

        double sum = 0;
        foreach (var v in sorted) sum += v;
        double avg = sum / sorted.Length;
        double median = Percentile(sorted, 0.5);
        double hz = avg > 0 ? 1000.0 / avg : 0;

        // Huecos: cada intervalo se mide en "ranuras" del período mediano, con una
        // tolerancia de ±50% (el jitter normal no cuenta como hueco). Un intervalo que
        // abarca 3 ranuras son 2 ranuras sin informe.
        //
        // OJO, límite honesto: un informe PERDIDO y un informe que llegó TARDE (el hilo
        // se atrasó y después llegaron varios juntos) se ven igual desde acá. Por eso la
        // métrica se llama "huecos" y mide regularidad, no pérdida: no se puede afirmar
        // que el dispositivo perdió un informe, sí que la cadencia se rompió.
        int gaps = 0;
        int reports = 1;
        if (median > 0)
        {
            foreach (var d in intervalsMs)
            {
                int slots = (int)Math.Floor(d / median + 0.5);
                if (slots < 1) slots = 1;
                reports += slots;
                gaps += slots - 1;
            }
        }
        double gapPercent = reports > 0 ? 100.0 * gaps / reports : 0;

        // Continuidad: ¿los informes llegan a la tasa del dispositivo o al ritmo del uso?
        // (desviación estándar sobre la media — ver ContinuityCvThreshold).
        double variance = 0;
        foreach (var v in sorted) variance += (v - avg) * (v - avg);
        double cv = avg > 0 ? Math.Sqrt(variance / sorted.Length) / avg : 0;

        InputLatencyVerdict verdict;
        if (sorted.Length < MinSamples)
            verdict = InputLatencyVerdict.InsufficientData;
        else if (cv > ContinuityCvThreshold)
            verdict = InputLatencyVerdict.NoContinuousStream;
        else if (gapPercent > UnstableGapPercent || (median > 0 && sorted[^1] > median * UnstableMaxRatio))
            verdict = InputLatencyVerdict.Unstable;
        else if (gapPercent <= ExcellentGapPercent && (median <= 0 || sorted[^1] <= median * ExcellentMaxRatio))
            verdict = InputLatencyVerdict.Excellent;
        else
            verdict = InputLatencyVerdict.Acceptable;

        return new InputLatencyTestResult(
            kind, name, handle, deviceId,
            sorted.Length, reports, gaps,
            hz, avg, sorted[0], sorted[^1],
            Percentile(sorted, 0.01), median, Percentile(sorted, 0.99),
            gapPercent, verdict, timestamp);
    }
}
