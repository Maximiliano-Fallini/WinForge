using System;
using System.IO;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Encabezado elegido para la etiqueta de un cartucho: el modo y, para «custom», el
/// texto libre. La firma forma parte de las claves de caché de las caras pintadas.
/// </summary>
public sealed record GameHeader(string Mode, string Text)
{
    public string Signature => $"{Mode}|{Text}";
    public bool IsDefault => Mode == GameLook.HeaderPlatform && string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Personalización por juego de la biblioteca 3D: carátula propia y encabezado de la
/// etiqueta. Se guarda en settings con el ejecutable como clave (el mismo `exe` que usan
/// las cards y las reglas de procesos), así sobrevive a reinicios y a reconstruir la
/// grilla. Los pintores corren en segundo plano: los getters son seguros desde cualquier
/// hilo porque <see cref="ISettingsService"/> protege su diccionario.
/// </summary>
public static class GameLook
{
    // Modos del encabezado. El texto resultante lo resuelve CartridgeModel.PaintHeader.
    public const string HeaderPlatform = "platform";           // logo + marca del launcher (de fábrica)
    public const string HeaderPlatformText = "platform-text";  // solo la marca del launcher
    public const string HeaderTitle = "title";                 // nombre del juego
    public const string HeaderCustom = "custom";               // texto libre
    public const string HeaderNone = "none";                   // etiqueta en blanco

    private const string ArtPrefix = "biblioteca.art.";
    private const string ArtSourcePrefix = "biblioteca.art.source.";
    private const string HeaderModePrefix = "biblioteca.header.mode.";
    private const string HeaderTextPrefix = "biblioteca.header.text.";

    public static GameHeader DefaultHeader => new(HeaderPlatform, "");

    private static ISettingsService? Settings =>
        WHPO_UI.App.Services?.GetService(typeof(ISettingsService)) as ISettingsService;

    /// <summary>Clave estable del juego: el nombre del ejecutable, en minúsculas y sin
    /// caracteres inválidos de archivo. Es la misma para la grilla, el detalle y el dock.</summary>
    public static string ExeKey(string? exe)
    {
        string name = (exe ?? "").Trim();
        try { name = Path.GetFileName(name); } catch { }
        if (string.IsNullOrWhiteSpace(name)) name = "unknown";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.ToLowerInvariant();
    }

    /// <summary>Carpeta de carátulas propias (PNG ya recortados + copia de la fuente).</summary>
    public static string ArtDir
    {
        get
        {
            string dir = Path.Combine(WHPO.Core.AppPaths.RootDir, "customart");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>PNG recortado que reemplaza a la carátula original de ese juego.</summary>
    public static string TargetArtPath(string? exe) => Path.Combine(ArtDir, $"art-{ExeKey(exe)}.png");

    /// <summary>Copia local de la imagen original elegida por el usuario, para volver a
    /// ajustar el recorte sin depender del archivo externo.</summary>
    public static string SourceArtPath(string? exe, string extension)
    {
        string ext = string.IsNullOrWhiteSpace(extension) ? ".png" : extension.ToLowerInvariant();
        if (!ext.StartsWith('.')) ext = "." + ext;
        return Path.Combine(ArtDir, $"src-{ExeKey(exe)}{ext}");
    }

    /// <summary>Ruta de la carátula personalizada, o null si el juego usa la de siempre.</summary>
    public static string? CustomArt(string? exe)
    {
        try
        {
            string? path = Settings?.Get<string>(ArtPrefix + ExeKey(exe));
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>Imagen original con la que se generó el recorte (puede ser la misma
    /// carátula propia si el usuario todavía no eligió otra).</summary>
    public static string? ArtSource(string? exe)
    {
        try
        {
            string? path = Settings?.Get<string>(ArtSourcePrefix + ExeKey(exe));
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>Guarda la carátula propia y la fuente usada para recortarla.</summary>
    public static void SetArt(string? exe, string outputPath, string? sourcePath)
    {
        var settings = Settings;
        if (settings == null) return;
        try
        {
            settings.Set(ArtPrefix + ExeKey(exe), outputPath);
            if (!string.IsNullOrWhiteSpace(sourcePath))
                settings.Set(ArtSourcePrefix + ExeKey(exe), sourcePath);
            settings.Save();
        }
        catch { }
    }

    /// <summary>Vuelve a la carátula original (banner/ícono) y borra los archivos propios.</summary>
    public static void ClearArt(string? exe)
    {
        var settings = Settings;
        try
        {
            // La fuente se resuelve ANTES de borrar la clave: después ya no se sabe dónde está.
            string? source = ArtSource(exe);
            if (settings != null)
            {
                settings.Remove(ArtPrefix + ExeKey(exe));
                settings.Remove(ArtSourcePrefix + ExeKey(exe));
                settings.Save();
            }

            foreach (string path in new[] { TargetArtPath(exe), source ?? "" })
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
        catch { }
    }

    public static GameHeader Header(string? exe)
    {
        try
        {
            var settings = Settings;
            if (settings == null) return DefaultHeader;
            string mode = settings.Get(HeaderModePrefix + ExeKey(exe), HeaderPlatform) ?? HeaderPlatform;
            string text = settings.Get(HeaderTextPrefix + ExeKey(exe), "") ?? "";
            return Normalize(mode, text);
        }
        catch { return DefaultHeader; }
    }

    public static void SetHeader(string? exe, string mode, string? text)
    {
        var settings = Settings;
        if (settings == null) return;
        GameHeader look = Normalize(mode, text ?? "");
        try
        {
            settings.Set(HeaderModePrefix + ExeKey(exe), look.Mode);
            settings.Set(HeaderTextPrefix + ExeKey(exe), look.Text);
            settings.Save();
        }
        catch { }
    }

    public static bool HasCustomizations(string? exe)
        => CustomArt(exe) != null || !Header(exe).IsDefault;

    private static GameHeader Normalize(string? mode, string? text)
    {
        string m = (mode ?? "").Trim().ToLowerInvariant();
        if (m is not (HeaderPlatform or HeaderPlatformText or HeaderTitle or HeaderCustom or HeaderNone))
            m = HeaderPlatform;
        string t = (text ?? "").Trim();
        if (t.Length > 40) t = t[..40];
        return new GameHeader(m, m == HeaderCustom ? t : "");
    }
}
