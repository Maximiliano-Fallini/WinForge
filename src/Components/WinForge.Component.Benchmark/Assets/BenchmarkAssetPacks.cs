using System.Net.Http;
using System.Security.Cryptography;
using WHPO.Core;

namespace WinForge.Component.Benchmark.Assets;

/// <summary>
/// Resultado de asegurar un set de assets en disco.
///
/// <see cref="Root"/> solo vale si <see cref="Success"/>. <see cref="Detail"/> es el motivo técnico del
/// fallo, en el idioma fuente (español): la página lo mete DENTRO de una frase traducida en vez de
/// mostrar un texto suelto sin traducir.
/// </summary>
internal sealed record AssetPackResult(bool Success, string Root, string Detail)
{
    internal static AssetPackResult Ready(string root) => new(true, root, "");

    internal static AssetPackResult Failed(string detail) => new(false, "", detail);
}

/// <summary>
/// Deja el set de assets de una escena en disco ANTES de prepararla, bajándolo si hace falta.
///
/// Por qué existe: la DLL del componente no lleva modelos ni texturas (README cinematográfico §7), y
/// en una instalación limpia la escena por defecto no tenía de dónde sacarlos — el usuario veía
/// "no encontré el modelo" y tenía que copiar una carpeta a mano. Ahora el pack viaja aparte,
/// VERSIONADO y con SHA-256, igual que los packs de idioma y los componentes del Workshop: se baja a
/// <c>%LOCALAPPDATA%\WHPO\BenchmarkAssets\&lt;set&gt;</c>, queda cacheado y la próxima corrida no toca la red.
///
/// Orden de resolución (el mismo que <c>SceneAssets</c>, que es quien después lo encuentra):
/// <list type="number">
/// <item><c>WHPO_BENCHMARK_ASSETS</c> (o <c>%LOCALAPPDATA%\...\&lt;set&gt;</c>) con contenido: listo, no
/// se baja nada. Es el camino de desarrollo y el de quien ya tenía el set copiado a mano.</item>
/// <item>El pack publicado del set, si lo hay: se baja, se verifica el SHA-256 y se descomprime en el
/// lugar definitivo.</item>
/// <item>Nada: se devuelve el motivo CON el camino de recuperación (el script de descarga), que es lo
/// que evita la hora perdida buscando dónde iba la carpeta.</item>
/// </list>
///
/// La descarga es ATÓMICA de cara a la escena: se descomprime en una carpeta temporal y recién cuando
/// está completa se mueve al lugar definitivo. Una descarga cortada a la mitad no deja un set a medio
/// armar que la escena lea como si estuviera bueno.
/// </summary>
internal static class BenchmarkAssetPacks
{
    /// <summary>Variable con la carpeta de desarrollo (la misma que lee <c>SceneAssets</c>).</summary>
    private const string OverrideVariable = "WHPO_BENCHMARK_ASSETS";

    private static readonly HttpClient Http = CreateClient();
    private static readonly object Sync = new();

    private static HttpClient CreateClient()
    {
        // Los packs se miden en decenas de MB: el timeout de 30 s de los catálogos no alcanza para una
        // conexión lenta. Sigue habiendo timeout (una descarga colgada no puede dejar la app esperando).
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinForge-Benchmark/1.0");
        return client;
    }

    /// <summary>Carpeta de la app donde queda el set (la misma ruta que resuelve <c>SceneAssets</c>).</summary>
    internal static string LocalRoot(string set) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WHPO", "BenchmarkAssets", set);

    /// <summary>
    /// True si el set ya está en disco (carpeta con algo adentro). No exige que lo haya bajado este
    /// componente: una carpeta copiada a mano vale igual, que es justo lo que hacía todo el mundo
    /// antes de que existiera la descarga.
    /// </summary>
    internal static bool IsReady(string set) => FindRoot(set) != null;

    /// <summary>Dónde está el set, o <c>null</c> si todavía no está en disco.</summary>
    internal static string? FindRoot(string set)
    {
        foreach (var candidate in Candidates(set))
        {
            try
            {
                if (!Directory.Exists(candidate)) continue;
                if (Directory.EnumerateFileSystemEntries(candidate).Any()) return candidate;
            }
            catch (Exception)
            {
                // Un candidato ilegible no es un motivo para no probar el siguiente.
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string set)
    {
        string? overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return Path.Combine(overridePath, set);
            yield return overridePath;
        }
        yield return LocalRoot(set);
    }

    /// <summary>
    /// Asegura el set: no hace nada si ya está, lo baja si falta y hay pack publicado, y si no
    /// devuelve el motivo con las instrucciones para conseguirlo a mano.
    /// </summary>
    internal static async Task<AssetPackResult> EnsureAsync(
        string set, IProgress<double>? progress, CancellationToken ct = default)
    {
        string? existing = FindRoot(set);
        if (existing != null) return AssetPackResult.Ready(existing);

        var pack = AssetPackCatalog.Find(set);
        if (pack == null)
        {
            return AssetPackResult.Failed(
                $"el set '{set}' todavía no está publicado como pack." + ManualPath(set));
        }

        string zipPath = Path.Combine(Path.GetTempPath(), $"whpo-{set}-{Guid.NewGuid():N}.zip");
        string staging = LocalRoot(set) + ".incoming";

        try
        {
            await DownloadAsync(pack, zipPath, progress, ct).ConfigureAwait(false);

            // Verificación ANTES de descomprimir: un pack corrupto o una respuesta de error guardada
            // como si fuera el archivo no puede llegar a la carpeta de assets.
            string actual = await Task.Run(() => HashOf(zipPath), ct).ConfigureAwait(false);
            if (!string.Equals(actual, pack.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return AssetPackResult.Failed(
                    $"el pack '{set}' {pack.Version} llegó con SHA-256 {actual[..12]}… " +
                    $"y se esperaba {pack.Sha256[..12]}…: se descartó." + ManualPath(set));
            }

            await Task.Run(() => ExtractTo(zipPath, staging), ct).ConfigureAwait(false);

            // Recién acá se toca el lugar definitivo: si algo falló antes, la carpeta que la escena
            // podría haber estado usando queda como estaba.
            string target = LocalRoot(set);
            lock (Sync)
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(staging, target);
            }

            progress?.Report(1.0);
            return AssetPackResult.Ready(target);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return AssetPackResult.Failed(
                $"no se pudo bajar el pack '{set}': {exception.Message}." + ManualPath(set));
        }
        finally
        {
            TryDelete(zipPath);
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// El camino de recuperación manual, SIEMPRE pegado al motivo del fallo: sin esto, un pack que no
    /// se publicó o una descarga que no llegó dejan al usuario con "no se pudo bajar" y sin saber que
    /// existe un script que le arma la carpeta en un minuto.
    /// </summary>
    private static string ManualPath(string set) =>
        $" Bajalo a mano con './tools/benchmark-assets/fetch-assets.ps1 -Set {set}' y copiá el set a " +
        $"{LocalRoot(set)}.";

    private static async Task DownloadAsync(
        AssetPack pack, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        string url = ProjectEndpoints.BenchmarkAssetPack(pack.Set, pack.Version);

        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? pack.SizeBytes;
        double expected = total > 0 ? total : pack.SizeBytes;
        if (expected > 0 && pack.SizeBytes > 0 && total != pack.SizeBytes)
        {
            // El tamaño es parte del contrato publicado: si no coincide, el pack cambió o la respuesta
            // no es la que el bloque describe. El hash lo atraparía igual, pero avisar acá es más claro.
            throw new InvalidDataException(
                $"el pack pesa {total} bytes y el catálogo declara {pack.SizeBytes}.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long received = 0;
        progress?.Report(0.0);
        while (true)
        {
            int read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if (expected > 0) progress?.Report(Math.Clamp(received / expected, 0.0, 1.0));
        }
    }

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void ExtractTo(string zipPath, string destination)
    {
        TryDeleteDirectory(destination);
        Directory.CreateDirectory(destination);
        // ExtractToDirectory valida las rutas de las entradas: una entrada con "..\" no puede escribir
        // fuera del destino.
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destination, overwriteFiles: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* el temporal lo limpia el sistema */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception) { /* ídem */ }
    }
}
