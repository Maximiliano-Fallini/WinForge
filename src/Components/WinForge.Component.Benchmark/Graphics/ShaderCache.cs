using System.Security.Cryptography;
using System.Text;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Caché en disco del bytecode compilado de los shaders de escena (DXBC de D3D11/D3D12).
///
/// El pixel shader de geometría es grande y el optimizador de fxc tarda ~11 s con él (y antes de
/// acotar los bucles desenrollados directamente NO terminaba: 150 s sin acabar). Compilarlo en cada
/// arranque deja la ventana en negro el tiempo suficiente para que parezca que la app se colgó —que
/// es exactamente el síntoma con el que se reportó—. Los shaders sólo cambian cuando alguien edita
/// el HLSL, así que el resultado se guarda: la clave es el hash del código + punto de entrada +
/// perfil, de modo que editar el shader invalida la entrada solo y nunca se sirve código viejo.
///
/// Es un acelerador, no un requisito: cualquier fallo de lectura o escritura se ignora en silencio
/// y se compila en vivo.
/// </summary>
internal static class ShaderCache
{
    // Subir si cambian las banderas de compilación (el bytecode deja de ser el mismo contrato).
    private const string Version = "fxc-1";

    private static readonly string? Folder = ResolveFolder();

    internal static byte[]? TryRead(string source, string entryPoint, string profile)
    {
        string? path = PathFor(source, entryPoint, profile);
        if (path == null || !File.Exists(path)) return null;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void Write(string source, string entryPoint, string profile, ReadOnlySpan<byte> bytecode)
    {
        string? path = PathFor(source, entryPoint, profile);
        if (path == null || bytecode.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Folder!);
            // Escritura atómica: un archivo a medio escribir no debe quedar como entrada válida.
            string temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytecode.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? PathFor(string source, string entryPoint, string profile)
    {
        if (Folder == null) return null;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}|{profile}|{entryPoint}|{source}"));
        return Path.Combine(Folder, $"{entryPoint}-{profile}-{Convert.ToHexString(hash.AsSpan(0, 16))}.dxbc");
    }

    private static string? ResolveFolder()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "WinForge", "shadercache");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
