using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WHPO.Core;

namespace WinForge.Component.Benchmark.Metrics;

/// <summary>
/// Informe de una corrida: qué se midió, con qué API, con qué configuración y sobre
/// qué equipo. NO hay puntaje compuesto a propósito — el resultado son las métricas
/// crudas (FPS, percentiles, ms de GPU y de CPU, hitches) más la huella del sistema,
/// para que cada número se pueda explicar y comparar.
///
/// La huella (<see cref="System"/>) es lo que hace válida una comparación: dos corridas
/// solo son comparables si describen el mismo equipo, la misma resolución y el mismo
/// estado del sistema. El informe la guarda aunque el usuario no la mire.
/// </summary>
public sealed class BenchmarkReport
{
    /// <summary>Versión del formato del archivo (para poder leer informes viejos).</summary>
    public int FormatVersion { get; set; } = 1;

    public string ComponentVersion { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    // ---- Qué se corrió y con qué ----
    public string Backend { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string AdapterDetail { get; set; } = "";
    public string SceneId { get; set; } = "";
    public string SceneName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Presentation { get; set; } = "";
    public bool VSync { get; set; }
    public int DurationSeconds { get; set; }
    public double WarmupSeconds { get; set; }
    public bool Completed { get; set; }
    public string? AbortReason { get; set; }

    // ---- Qué se midió ----
    public FrameStats Stats { get; set; } = new();

    /// <summary>Estado de la máquina durante la corrida (uso, temperaturas, frecuencias, potencias).</summary>
    public SensorSummary Sensors { get; set; } = new();

    /// <summary>Huella del equipo y del contexto (CPU, GPU, RAM, SO, energía…).</summary>
    public Dictionary<string, string> System { get; set; } = new();

    /// <summary>
    /// Cosas que afectan la corrida y el usuario tiene que saber (sensores ausentes, corrida
    /// incompleta, vsync). Se guardan como PLANTILLA + datos, no como frase armada: así el mismo
    /// informe se puede volver a mostrar en cualquier idioma y el archivo no queda atado al
    /// idioma en el que se corrió.
    /// </summary>
    public List<BenchmarkNote> Warnings { get; set; } = new();

    [JsonIgnore]
    public string FileName { get; set; } = "";

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Ojo: "System" acá tiene que ser el global — la clase tiene una propiedad System
        // (la huella del equipo) y en un inicializador de campo esa gana la resolución.
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Guarda el informe en la carpeta de benchmark de la app y devuelve la ruta.</summary>
    public string Save()
    {
        string folder = BenchmarkStore.Folder;
        Directory.CreateDirectory(folder);
        string name = $"benchmark-{Timestamp:yyyyMMdd-HHmmss}-{Sanitize(SceneId)}-{Sanitize(Backend)}.json";
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, ToJson());
        FileName = name;
        return path;
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray();
        return new string(chars).Trim('-').ToLowerInvariant();
    }
}

/// <summary>
/// Un aviso del informe: plantilla en español (clave de traducción) y los datos que la
/// completan. La página lo traduce al mostrarlo, con el idioma activo en ese momento.
/// </summary>
public sealed record BenchmarkNote(string Template, string[] Args)
{
    public static BenchmarkNote Of(string template, params string[] args) => new(template, args);
}

/// <summary>
/// Carpeta e historial de informes. Vive en la raíz de datos de la app
/// (<c>%LOCALAPPDATA%\WHPO\benchmark</c>, o la variante de desarrollo) para que el
/// historial siga al usuario y no a la instalación.
/// </summary>
public static class BenchmarkStore
{
    public const int KeepLast = 50;

    public static string Folder => Path.Combine(AppPaths.RootDir, "benchmark");

    /// <summary>Informes guardados, del más nuevo al más viejo.</summary>
    public static IReadOnlyList<string> ListRecent(int max = 20)
    {
        try
        {
            if (!Directory.Exists(Folder)) return Array.Empty<string>();
            return Directory.EnumerateFiles(Folder, "benchmark-*.json")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Take(max)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static BenchmarkReport? Load(string path)
    {
        try { return JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(path), BenchmarkReportJson.Options); }
        catch { return null; }
    }

    /// <summary>Borra los informes más viejos, dejando los últimos <see cref="KeepLast"/>.</summary>
    public static void Trim()
    {
        try
        {
            var all = Directory.EnumerateFiles(Folder, "benchmark-*.json")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Skip(KeepLast)
                .ToList();
            foreach (var file in all)
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}

/// <summary>Opciones de lectura compartidas (el escritor usa las suyas con indentación).</summary>
internal static class BenchmarkReportJson
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
}
