using System.Reflection;
using System.Text.Json;

namespace WHPO.Core.Optimizations;

/// <summary>
/// Carga (y cachea) el catálogo de optimizaciones embebido en el ensamblado.
/// El JSON es de ~1 MB con los 418 ajustes y sus textos en 5 idiomas, así que se
/// lee UNA vez al primer uso y se comparte como instancia única.
/// </summary>
public sealed class OptimizationCatalogProvider
{
    /// <summary>Nombre lógico del recurso embebido.</summary>
    public const string ResourceName = "WHPO.Core.Optimizations.optimization-catalog.json";

    private static readonly Lazy<OptimizationCatalog> LazyCatalog =
        new(() => LoadEmbedded(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>El catálogo compartido (se carga la primera vez que se pide).</summary>
    public static OptimizationCatalog Catalog => LazyCatalog.Value;

    /// <summary>True si el catálogo se pudo cargar.</summary>
    public static bool IsLoaded => LazyCatalog.Value is not null;

    private static OptimizationCatalog LoadEmbedded()
    {
        using var stream = typeof(OptimizationCatalogProvider).GetTypeInfo().Assembly
            .GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            // Fallback: el archivo suelto junto al ejecutable (builds de desarrollo).
            var loose = Path.Combine(AppContext.BaseDirectory, "optimization-catalog.json");
            if (File.Exists(loose))
            {
                using var file = File.OpenRead(loose);
                return Deserialize(file);
            }

            return new OptimizationCatalog();
        }

        return Deserialize(stream);
    }

    private static OptimizationCatalog Deserialize(Stream stream)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        return JsonSerializer.Deserialize<OptimizationCatalog>(stream, options) ?? new OptimizationCatalog();
    }
}
