using System;
using System.Collections.Generic;

namespace WinForge.Component.Benchmark.Metrics;

/// <summary>
/// Una lectura de sensores durante la corrida: uso, temperatura, frecuencia y potencia de GPU y
/// CPU, VRAM y RAM. Es el estado de la máquina MIENTRAS se mide, que es lo que permite explicar
/// el resultado (si el equipo estaba al 100 % de uso, a 88 °C o limitado por potencia).
/// </summary>
public readonly record struct SensorSample(
    double GpuUsagePercent,
    double GpuTemperatureCelsius,
    double GpuClockMHz,
    double GpuWatts,
    double VramUsedMb,
    double VramTotalMb,
    double CpuUsagePercent,
    double CpuTemperatureCelsius,
    double CpuClockMHz,
    double CpuWatts,
    double RamUsagePercent,
    double RamUsedMb,
    double RamTotalMb);

/// <summary>
/// Resumen de los sensores durante la corrida: promedio y pico de cada magnitud. Se calcula con
/// funciones puras (ver <see cref="Compute"/>) y los sensores que no reportan (0) quedan afuera
/// del promedio — nunca se promedian como si fueran ceros reales.
/// </summary>
public sealed record SensorSummary
{
    public int Samples { get; init; }

    public double GpuUsageAveragePercent { get; init; }
    public double GpuUsageMaxPercent { get; init; }
    public double GpuTemperatureAverageCelsius { get; init; }
    public double GpuTemperatureMaxCelsius { get; init; }
    public double GpuClockAverageMHz { get; init; }
    public double GpuWattsAverage { get; init; }
    public double GpuWattsMax { get; init; }
    public double VramUsedAverageMb { get; init; }
    public double VramUsedMaxMb { get; init; }
    public double VramTotalMb { get; init; }

    public double CpuUsageAveragePercent { get; init; }
    public double CpuUsageMaxPercent { get; init; }
    public double CpuTemperatureAverageCelsius { get; init; }
    public double CpuTemperatureMaxCelsius { get; init; }
    public double CpuClockAverageMHz { get; init; }
    public double CpuWattsAverage { get; init; }
    public double CpuWattsMax { get; init; }

    public double RamUsageAveragePercent { get; init; }
    public double RamUsedAverageMb { get; init; }
    public double RamUsedMaxMb { get; init; }
    public double RamTotalMb { get; init; }

    public bool HasData => Samples > 0;

    public static SensorSummary Compute(IReadOnlyList<SensorSample> samples)
    {
        if (samples == null || samples.Count == 0) return new SensorSummary();

        var gpuUsage = new List<double>();
        var gpuTemp = new List<double>();
        var gpuClock = new List<double>();
        var gpuWatts = new List<double>();
        var vram = new List<double>();
        var cpuUsage = new List<double>();
        var cpuTemp = new List<double>();
        var cpuClock = new List<double>();
        var cpuWatts = new List<double>();
        var ramPercent = new List<double>();
        var ramUsed = new List<double>();
        double vramTotal = 0, ramTotal = 0;

        foreach (var sample in samples)
        {
            AddIfValid(gpuUsage, sample.GpuUsagePercent);
            AddIfValid(gpuTemp, sample.GpuTemperatureCelsius);
            AddIfValid(gpuClock, sample.GpuClockMHz);
            AddIfValid(gpuWatts, sample.GpuWatts);
            AddIfValid(vram, sample.VramUsedMb);
            AddIfValid(cpuUsage, sample.CpuUsagePercent);
            AddIfValid(cpuTemp, sample.CpuTemperatureCelsius);
            AddIfValid(cpuClock, sample.CpuClockMHz);
            AddIfValid(cpuWatts, sample.CpuWatts);
            AddIfValid(ramPercent, sample.RamUsagePercent);
            AddIfValid(ramUsed, sample.RamUsedMb);
            if (sample.VramTotalMb > vramTotal) vramTotal = sample.VramTotalMb;
            if (sample.RamTotalMb > ramTotal) ramTotal = sample.RamTotalMb;
        }

        return new SensorSummary
        {
            Samples = samples.Count,
            GpuUsageAveragePercent = Average(gpuUsage),
            GpuUsageMaxPercent = Max(gpuUsage),
            GpuTemperatureAverageCelsius = Average(gpuTemp),
            GpuTemperatureMaxCelsius = Max(gpuTemp),
            GpuClockAverageMHz = Average(gpuClock),
            GpuWattsAverage = Average(gpuWatts),
            GpuWattsMax = Max(gpuWatts),
            VramUsedAverageMb = Average(vram),
            VramUsedMaxMb = Max(vram),
            VramTotalMb = vramTotal,
            CpuUsageAveragePercent = Average(cpuUsage),
            CpuUsageMaxPercent = Max(cpuUsage),
            CpuTemperatureAverageCelsius = Average(cpuTemp),
            CpuTemperatureMaxCelsius = Max(cpuTemp),
            CpuClockAverageMHz = Average(cpuClock),
            CpuWattsAverage = Average(cpuWatts),
            CpuWattsMax = Max(cpuWatts),
            RamUsageAveragePercent = Average(ramPercent),
            RamUsedAverageMb = Average(ramUsed),
            RamUsedMaxMb = Max(ramUsed),
            RamTotalMb = ramTotal
        };
    }

    private static void AddIfValid(List<double> values, double value)
    {
        // 0, negativo o no finito = el sensor no reporta en esta máquina: no entra al promedio.
        if (double.IsFinite(value) && value > 0) values.Add(value);
    }

    private static double Average(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        double sum = 0;
        foreach (double value in values) sum += value;
        return sum / values.Count;
    }

    private static double Max(IReadOnlyList<double> values)
    {
        double max = 0;
        foreach (double value in values)
        {
            if (value > max) max = value;
        }
        return max;
    }
}
