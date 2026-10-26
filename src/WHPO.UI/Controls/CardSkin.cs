using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Controls;

/// <summary>
/// Paleta de apariencia de la biblioteca 3D: un solo color de «hardware» que tiñe el
/// cartucho, la consola del pie y sus detalles, como los «editions» de Carthage.
///
/// El valor por defecto es <c>theme</c>: el plástico sale del color de identidad del
/// tema activo, apagado y oscurecido para que se lea como carcasa y no como un color
/// de interfaz. Los temas con paleta propia (Marea, Brasa, Rosa/Blanco…) tienen un
/// color de carcasa propio y curado; los clásicos Oscuro y Claro usan su plástico neutro, porque el azul de la
/// app es el acento de la UI, no el color de su carcasa. Los presets son colores
/// fijos, por si se quiere una bandeja que no siga al tema. <see cref="Refresh"/> toma
/// una foto (inmutable) del ajuste, del acento y del tema: se llama solo desde la UI
/// (al entrar a Apariencia, al entrar a la biblioteca y al elegir un color), porque
/// los pintores corren en segundo plano y leen <see cref="Current"/> sin tocar
/// recursos de XAML.
/// </summary>
public static class CardSkin
{
    /// <summary>Ajuste persistido con el id del preset (ver <see cref="Options"/>).</summary>
    public const string SettingKey = "biblioteca.cards3D.skin";

    /// <summary>Preset por defecto: la carcasa acompaña a la apariencia de la app.</summary>
    public const string ThemeId = "theme";

    /// <summary>
    /// Prefijo del ajuste cuando el color se elige a mano con el selector: el valor
    /// guardado queda como <c>custom:RRGGBB</c>. Así el color exacto convive con los
    /// presets sin agregar claves nuevas al archivo de settings.
    /// </summary>
    public const string CustomPrefix = "custom:";

    /// <summary>Grafito de Carthage: el color que tenía la biblioteca antes de este ajuste.</summary>
    private static readonly Color Graphite = Color.FromArgb(0x30, 0x30, 0x36);

    /// <summary>Negro del preset «Negro»: es el plástico asignado a la apariencia Oscura.</summary>
    private static readonly Color BlackPlastic = Color.FromArgb(0x1A, 0x1B, 0x1F);

    /// <summary>Opciones del selector, en el orden en que se muestran.</summary>
    public static IReadOnlyList<Option> Options { get; } = new[]
    {
        new Option(ThemeId, "Del tema", null),
        new Option("graphite", "Grafito", Color.FromArgb(0x30, 0x30, 0x36)),
        new Option("black", "Negro", BlackPlastic),
        new Option("white", "Blanco", Color.FromArgb(0xDD, 0xE0, 0xE6)),
        new Option("red", "Rojo", Color.FromArgb(0xA9, 0x35, 0x30)),
        new Option("orange", "Naranja", Color.FromArgb(0xC0, 0x6A, 0x24)),
        new Option("neon", "Neón", Color.FromArgb(0x86, 0xCC, 0x4B)),
        new Option("teal", "Verde azulado", Color.FromArgb(0x1F, 0x7A, 0x70)),
        new Option("sky", "Celeste", Color.FromArgb(0x3E, 0x8C, 0xC4))
    };

    private static Palette _current = Build(ThemeId, null, AppTheme.Dark);

    /// <summary>Paleta vigente (inmutable y segura de leer desde cualquier hilo).</summary>
    public static Palette Current => Volatile.Read(ref _current);

    /// <summary>Id del preset vigente («theme» o uno de <see cref="Options"/>).</summary>
    public static string CurrentId => Current.Id;

    /// <summary>
    /// Vuelve al preset «Del tema» y lo persiste. Se llama cuando el usuario cambia
    /// de apariencia: cada tema estrena su propio plástico y no se arrastra el color
    /// fijo que se hubiera elegido con el tema anterior.
    /// </summary>
    public static void ResetToTheme() => Persist(ThemeId);

    /// <summary>
    /// Fija un color exacto elegido con el selector y lo persiste. La UI llama después
    /// a <see cref="Refresh"/> y repinta las vistas previas, igual que con un preset.
    /// </summary>
    public static void SetCustom(Color color) => Persist(FormatCustom(color));

    /// <summary>
    /// Publica una paleta SOLO en memoria, sin tocar el ajuste guardado: la usa el
    /// selector de color exacto para mostrar en vivo cómo quedan los cartuchos y la
    /// consola mientras se mueve el color. Se deshace con <see cref="Refresh"/>, que
    /// vuelve a leer el valor persistido.
    /// </summary>
    public static void Preview(Color color) =>
        Volatile.Write(ref _current, new Palette(FormatCustom(color), color));

    private static void Persist(string id)
    {
        try
        {
            if (WHPO_UI.App.Services?.GetService(typeof(ISettingsService)) is ISettingsService settings)
            {
                settings.Set(SettingKey, id);
                settings.Save();
            }
        }
        catch { }
    }

    /// <summary>Id canónico de un color exacto: <c>custom:RRGGBB</c>, en mayúsculas.</summary>
    public static string FormatCustom(Color color) => $"{CustomPrefix}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// ¿El ajuste es un color exacto? Devuelve el color parseado. Se valida el formato
    /// porque el settings es un JSON que también se puede editar a mano.
    /// </summary>
    public static bool TryParseCustom(string? id, out Color color)
    {
        color = default;
        if (id == null || !id.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string hex = id[CustomPrefix.Length..].TrimStart('#');
        if (hex.Length != 6) return false;
        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return false;
        color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>
    /// Relee el ajuste y el acento del tema y publica la paleta. Llamar desde la UI
    /// antes de pintar la biblioteca o la consola: el acento vive en recursos de XAML.
    /// </summary>
    public static void Refresh()
    {
        string id = ThemeId;
        try
        {
            if (WHPO_UI.App.Services?.GetService(typeof(ISettingsService)) is ISettingsService settings)
                id = settings.Get(SettingKey, ThemeId) ?? ThemeId;
        }
        catch { }

        Color? accent = null;
        try
        {
            if (ThemeBrushes.Get("AccentBrush") is SolidColorBrush brush)
                accent = ToDrawing(brush.Color);
        }
        catch { }

        Volatile.Write(ref _current, Build(id, accent, ResolveTheme()));
    }

    /// <summary>
    /// Tema efectivo: el elegido, o el claro/oscuro real cuando la apariencia es
    /// «Usar sistema». Se lee desde la UI en <see cref="Refresh"/>.
    /// </summary>
    private static AppTheme ResolveTheme()
    {
        try
        {
            var services = WHPO_UI.App.Services;
            if (services?.GetService(typeof(IThemeService)) is IThemeService themeService
                && themeService.CurrentTheme != AppTheme.SystemDefault)
            {
                return themeService.CurrentTheme;
            }

            if (services?.GetService(typeof(IThemeApplier)) is IThemeApplier applier)
                return applier.GetSystemTheme();
        }
        catch { }
        return AppTheme.Dark;
    }

    /// <summary>
    /// Color de carcasa del preset: el color de identidad del tema para «Del tema»,
    /// el color fijo para los demás. Un id desconocido cae en el grafito.
    /// </summary>
    private static Palette Build(string id, Color? accent, AppTheme theme)
    {
        if (id == ThemeId)
            return new Palette(ThemeId, ThemePlastic(theme, accent));

        // Color elegido a mano: manda sobre los presets y sobre el tema.
        if (TryParseCustom(id, out var custom))
            return new Palette(FormatCustom(custom), custom);

        foreach (var option in Options)
        {
            if (string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase) && option.Color is { } color)
                return new Palette(option.Id, color);
        }

        return new Palette("graphite", Graphite);
    }

    /// <summary>
    /// Plástico asignado a cada tema. Los temas con paleta propia tienen un color de
    /// carcasa curado a mano (no derivado del acento): es su «Del tema». Oscuro y Claro
    /// no tienen acento de identidad —usan el azul de la app—, así que conservan un
    /// plástico neutro: negro y claro respectivamente. Así «Del tema» en Oscuro no pinta
    /// los cartuchos de azul ni de grafito.
    /// </summary>
    private static Color ThemePlastic(AppTheme theme, Color? accent)
    {
        if (theme == AppTheme.Dark) return BlackPlastic;
        if (theme == AppTheme.Light) return Color.FromArgb(0xDD, 0xE0, 0xE6);

        // Carcasa por tema, elegida a mano. Un tema nuevo sin entrada acá sigue cayendo
        // en el acento atenuado de antes, así que esto no rompe a los que vengan después.
        Color? curated = theme switch
        {
            AppTheme.Tide => Rgb(0x40, 0x7A, 0x8A),        // Marea
            AppTheme.Aurora => Rgb(0x21, 0x42, 0x46),      // Aurora
            AppTheme.Brasa => Rgb(0x4D, 0x11, 0x08),       // Brasa
            AppTheme.Metal => Rgb(0x38, 0x40, 0x45),       // Metal
            AppTheme.Amatista => Rgb(0x3B, 0x29, 0x5C),    // Amatista
            AppTheme.CristalAzul => Rgb(0x1E, 0x32, 0x82), // Cristal Azul
            AppTheme.Twilight => Rgb(0x32, 0x10, 0x4D),    // Crepúsculo
            AppTheme.PinkLight => Rgb(0xFB, 0xCA, 0xFF),   // Rosa / Blanco
            AppTheme.BlueBlack => Rgb(0x00, 0x14, 0x2E),   // Negro / Azul
            _ => null
        };
        if (curated is { } plastic) return plastic;

        if (WHPO_UI.ThemeCatalog.Find(theme) != null && accent is { } a)
            return MutedAccent(a);
        return Graphite;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    /// <summary>
    /// Acento del tema convertido en plástico: mismo tono, saturación recortada y
    /// luminosidad baja y fija, para que la bandeja sea homogénea y el color se lea
    /// como carcasa, no como un botón.
    /// </summary>
    private static Color MutedAccent(Color accent)
    {
        RgbToHsl(accent, out float h, out float s, out _);
        // Un acento prácticamente gris no tiene tono que seguir: se usa el grafito, no un
        // gris teñido de rojo por el h=0 que devuelve la conversión.
        if (s < 0.05f) return Graphite;
        float saturation = Math.Clamp(s * 0.62f, 0.10f, 0.38f);
        return FromHsl(h, saturation, 0.20f);
    }

    private static void RgbToHsl(Color c, out float h, out float s, out float l)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float d = max - min;
        h = 0;
        if (d > 0.0001f)
        {
            if (max == r) h = ((g - b) / d) % 6f;
            else if (max == g) h = (b - r) / d + 2f;
            else h = (r - g) / d + 4f;
            h *= 60f;
            if (h < 0) h += 360f;
        }
        l = (max + min) * 0.5f;
        s = d <= 0.0001f ? 0 : d / (1f - Math.Abs(2f * l - 1f));
    }

    private static Color FromHsl(float h, float s, float l)
    {
        float c = (1f - Math.Abs(2f * l - 1f)) * s;
        float x = c * (1f - Math.Abs((h / 60f) % 2f - 1f));
        float m = l - c * 0.5f;
        (float r, float g, float b) = h switch
        {
            < 60 => (c, x, 0f),
            < 120 => (x, c, 0f),
            < 180 => (0f, c, x),
            < 240 => (0f, x, c),
            < 300 => (x, 0f, c),
            _ => (c, 0f, x)
        };
        return Color.FromArgb(
            (int)Math.Clamp((r + m) * 255f, 0, 255),
            (int)Math.Clamp((g + m) * 255f, 0, 255),
            (int)Math.Clamp((b + m) * 255f, 0, 255));
    }

    private static Color ToDrawing(Windows.UI.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Windows.UI.Color → System.Drawing.Color (color exacto del selector).</summary>
    public static Color FromUi(Windows.UI.Color c) => ToDrawing(c);

    public static Windows.UI.Color ToUi(Color c) => Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Una opción del selector: id persistido, etiqueta en español (la traduce I18n) y color de muestra.</summary>
    public sealed record Option(string Id, string Label, Color? Color);

    /// <summary>
    /// Foto inmutable de la paleta. Los tonos de la consola se derivan del mismo
    /// plástico del cartucho, así el pie y la bandeja siempre cuentan la misma historia.
    /// </summary>
    public sealed class Palette
    {
        public Palette(string id, Color plastic)
        {
            Id = id;
            Plastic = plastic;
            Luminance = ToLuminance(plastic);
            IsLight = Luminance > 150;
            Key = $"{id}:{plastic.R:X2}{plastic.G:X2}{plastic.B:X2}";
        }

        public string Id { get; }

        /// <summary>Clave corta para las cachés de imágenes: cambia con el color.</summary>
        public string Key { get; }

        /// <summary>Color del plástico del cartucho y de la consola.</summary>
        public Color Plastic { get; }

        /// <summary>El plástico es claro: el texto del dock y los relieves van en oscuro.</summary>
        public bool IsLight { get; }

        /// <summary>
        /// Luminancia del plástico (0..255). La usan los pintores del chasis para decidir
        /// cuánto puede aclararse una pieza: un negro tolera un 5 % de aclarado sin dejar
        /// de ser negro, un blanco necesita el triple para mostrar el mismo relieve.
        /// </summary>
        public float Luminance { get; }

        /// <summary>Muestra del selector (el propio plástico).</summary>
        public Windows.UI.Color Swatch => ToUi(Plastic);

        /// <summary>Plástico oscurecido en <paramref name="amount"/> (0..1).</summary>
        public Color Dark(float amount) => Color.FromArgb(
            (int)Math.Clamp(Plastic.R * (1f - amount), 0, 255),
            (int)Math.Clamp(Plastic.G * (1f - amount), 0, 255),
            (int)Math.Clamp(Plastic.B * (1f - amount), 0, 255));

        /// <summary>Plástico aclarado hacia blanco en <paramref name="amount"/> (0..1).</summary>
        public Color Light(float amount) => Color.FromArgb(
            (int)Math.Clamp(Plastic.R + (255 - Plastic.R) * amount, 0, 255),
            (int)Math.Clamp(Plastic.G + (255 - Plastic.G) * amount, 0, 255),
            (int)Math.Clamp(Plastic.B + (255 - Plastic.B) * amount, 0, 255));

        private static float ToLuminance(Color c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
    }
}
