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

    /// <summary>
    /// Sombras proyectadas encendidas durante la corrida: es parte de la CONFIGURACIÓN, así que dos
    /// informes con distinto valor no describen la misma escena y no se comparan (junto con la API, la
    /// escena, el tamaño y la sincronización, que ya se guardaban).
    /// </summary>
    public bool Shadows { get; set; } = true;

    /// <summary>Resolución del mapa de sombras de la corrida (el lado del cuadrado). Junto con las
    /// sombras apagadas/encendidas, es el otro extremo de la misma perilla de calidad.</summary>
    public int ShadowMapSize { get; set; }

    /// <summary>
    /// Entorno (IBL del HDRI) encendido en esta corrida. Apagado, la escena usa el cielo procedural del
    /// estilo: cambia la luz ambiente y los reflejos, no solo el fondo.
    /// </summary>
    public bool Environment { get; set; } = true;

    /// <summary>
    /// Nivel de calidad gráfica con el que se corrió, como texto fuente en español ("Bajo", "Alto",
    /// "Ultra",
    /// "Personalizado"…): el NOMBRE del nivel, sin el detalle de lo que fija, que es el mismo rótulo que
    /// se lee en el desplegable. Es la etiqueta que RESUME la configuración: el detalle de cada perilla está
    /// en <see cref="Shadows"/>, <see cref="ShadowMapSize"/> y <see cref="Environment"/>, y sigue siendo
    /// lo que decide si dos corridas se comparan (un preset con perillas distintas no iguala nada).
    ///
    /// Vacío en los informes guardados antes de que existiera el dato: la página no muestra el renglón
    /// en ese caso, en vez de afirmar que se usó "Personalizado".
    /// </summary>
    public string GraphicsPreset { get; set; } = "";
    public int DurationSeconds { get; set; }

    /// <summary>Segundos de calentamiento PEDIDOS (los que salen de la escena; ver <see cref="WarmupElapsedSeconds"/>).</summary>
    public double WarmupSeconds { get; set; }

    /// <summary>
    /// Calentamiento REAL que quedó fuera de la medición y frames que se descartaron con él
    /// (compilado de shaders, subida de geometría y constantes, primer uso de las caches del
    /// driver). Puede superar a <see cref="WarmupSeconds"/> si el arranque fue lento: por debajo
    /// del piso de frames no se empieza a medir (ver SceneHost).
    /// </summary>
    public double WarmupElapsedSeconds { get; set; }
    public int WarmupFrames { get; set; }
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

    /// <summary>
    /// Borra UN informe por su ruta. Devuelve true si el archivo ya no está (se borró ahora o no
    /// existía): el botón de la página no puede quedar reportando un error por un archivo que el
    /// usuario ya había movido a mano.
    /// </summary>
    public static bool Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Borra TODOS los informes guardados y devuelve cuántos se borraron.</summary>
    public static int DeleteAll()
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(Folder)) return 0;
            foreach (var file in Directory.EnumerateFiles(Folder, "benchmark-*.json").ToList())
            {
                if (Delete(file)) deleted++;
            }
        }
        catch { }
        return deleted;
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
