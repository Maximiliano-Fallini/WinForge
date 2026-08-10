using System;
using System.Collections.Generic;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Tipo de carga del test de estabilidad. La lista de tipos disponibles se arma
/// según las instrucciones detectadas del procesador (SSE, AVX, AVX2, AVX-512...).
/// </summary>
public enum StabilityTestType
{
    /// <summary>Mezcla de operaciones FP, enteras y SIMD (carga variada).</summary>
    Balanced,
    /// <summary>Punto flotante pesado (sqrt/sin/cos/exp).</summary>
    Fpu,
    /// <summary>Aritmética de enteros (mul/xor/shift).</summary>
    Integer,
    /// <summary>SIMD de 128 bits (SSE/SSE2).</summary>
    Sse,
    /// <summary>SIMD de 256 bits (AVX/AVX2).</summary>
    Avx,
    /// <summary>SIMD de 512 bits (AVX-512).</summary>
    Avx512
}

/// <summary>
/// Muestra periódica del test (1 por segundo): uso, temperatura, potencia y
/// frecuencia de la CPU, más el tiempo transcurrido y el restante.
/// </summary>
public sealed record StabilitySample(
    double UsagePercent,
    double TempCelsius,
    double PowerWatts,
    double FrequencyMHz,
    TimeSpan Elapsed,
    TimeSpan Remaining);

/// <summary>
/// Resultado del test al terminar (por duración cumplida o detención manual).
/// </summary>
public sealed record StabilityTestResult(
    bool Completed,
    TimeSpan Duration,
    double MaxUsagePercent,
    double MaxTempCelsius,
    double MaxPowerWatts);

/// <summary>
/// Servicio del test de estabilidad: pone todos los núcleos al 100% con la carga
/// elegida mientras monitorea uso/temperatura/potencia/frecuencia en tiempo real.
/// </summary>
public interface IStabilityService
{
    /// <summary>Indica si hay un test corriendo actualmente.</summary>
    bool IsRunning { get; }

    /// <summary>Tipo de carga del test activo (o el último iniciado).</summary>
    StabilityTestType ActiveType { get; }

    /// <summary>Duración del test activo (o del último iniciado).</summary>
    TimeSpan Duration { get; }

    /// <summary>Momento en que arrancó el test activo (para calcular el restante).</summary>
    DateTime StartTime { get; }

    /// <summary>Última muestra generada por el test (null si nunca corrió).</summary>
    StabilitySample? LastSample { get; }

    /// <summary>
    /// Tipos de test disponibles según las instrucciones detectadas del procesador.
    /// El proceso es x86 (32 bits), así que AVX/AVX2/AVX-512 solo se ofrecen si el
    /// runtime las soporta; si no, la carga SIMD cae a SSE.
    /// </summary>
    IReadOnlyList<(StabilityTestType Type, string Label)> GetAvailableTestTypes();

    /// <summary>Inicia el test con el tipo de carga y duración indicados.</summary>
    void Start(StabilityTestType type, TimeSpan duration);

    /// <summary>Detiene el test manualmente.</summary>
    void Stop();

    /// <summary>Se dispara una vez por segundo con la muestra actual.</summary>
    event Action<StabilitySample>? SampleUpdated;

    /// <summary>Se dispara cuando el test termina (por duración o manual).</summary>
    event Action<StabilityTestResult>? TestCompleted;
}
