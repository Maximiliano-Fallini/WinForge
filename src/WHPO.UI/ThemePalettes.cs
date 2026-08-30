using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Paletas de los temas con identidad propia (Rosa/Blanco y Negro/Azul).
///
/// Cada tema hereda la estructura de un diccionario base (Light o Dark) y pisa
/// sus pinceles de identidad en DOS lugares, cada clave en el diccionario donde
/// vive originalmente (así gana la lookup igual que el valor que reemplaza):
///  - Pinceles semánticos (AppBackgroundBrush, AccentBrush, cards...) →
///    ThemeDictionaries de App.xaml.
///  - SystemAccentColor* (como Color), SystemAccentColorBrush,
///    AccentFillColor* y NavigationView* → diccionario mergeado
///    AccentOverrides.xaml. Escribir los SystemAccentColor* como COLOR (no como
///    pincel) es lo que hace reactivos a ToggleSwitch, CheckBox, ProgressBar y
///    botones de acento, porque los pinceles internos de WinUI derivan de esos
///    colores en runtime.
///
/// Initialize() captura los valores originales al arrancar; RestoreBase() los
/// devuelve exactos al salir del tema, así Claro/Oscuro/Sistema quedan intactos.
/// </summary>
public static class ThemePalettes
{
    // Inicialización perezosa: el diccionario referencia a Pink/BlueBlack, que
    // están declarados más abajo; crearla en un field initializer los guardaría null.
    private static Dictionary<AppTheme, (AppTheme Base, ThemePalette Palette)>? _palettes;

    private static Dictionary<AppTheme, (AppTheme Base, ThemePalette Palette)> Palettes
        => _palettes ??= new Dictionary<AppTheme, (AppTheme Base, ThemePalette Palette)>
        {
            [AppTheme.PinkLight] = (AppTheme.Light, Pink),
            [AppTheme.BlueBlack] = (AppTheme.Dark, BlueBlack)
        };

    public static bool HasOwnPalette(AppTheme theme) => Palettes.ContainsKey(theme);

    /// <summary>Tema base que estructura al tema (Light o Dark). El resto es identidad propia.</summary>
    public static AppTheme BaseThemeFor(AppTheme theme)
        => Palettes.TryGetValue(theme, out var p) ? p.Base : theme;

    // =====================================================================
    // Aplicación / restauración
    // =====================================================================

    private static readonly Dictionary<string, Dictionary<string, object>> _originalsRoot = new();
    private static readonly Dictionary<string, Dictionary<string, object>> _originalsOverrides = new();
    private static ResourceDictionary? _overridesHost;
    private static bool _initialized;

    /// <summary>Debe llamarse UNA vez al arrancar, antes de aplicar el tema guardado.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _overridesHost = FindOverridesHost();
        foreach (var themeKey in new[] { "Light", "Dark" })
        {
            _originalsRoot[themeKey] = Capture(GetThemeDictionary(themeKey), AllSemanticKeys());
            _originalsOverrides[themeKey] = Capture(GetOverridesThemeDictionary(themeKey), AllOverrideKeys());
        }
    }

    /// <summary>Pisa los pinceles de identidad del tema. No-op si el tema no tiene paleta propia.</summary>
    public static void ApplyPalette(AppTheme theme)
    {
        try
        {
            if (!Palettes.TryGetValue(theme, out var p)) return;
            var root = GetThemeDictionary(p.Base == AppTheme.Light ? "Light" : "Dark");
            if (root == null) return;
            var overrides = GetOverridesThemeDictionary(p.Base == AppTheme.Light ? "Light" : "Dark");

            foreach (var entry in p.Palette.Brushes)
                root[entry.Key] = entry.Brush;
            if (overrides == null) return;
            foreach (var (key, hex) in p.Palette.OverrideBrushes)
                overrides[key] = MakeBrush(hex);
            foreach (var (key, hex) in p.Palette.AccentColors)
                overrides[key] = MakeColor(hex);
        }
        catch
        {
            // Nunca debe tirar la app: peor caso, el tema queda sin paleta propia.
        }
    }

    /// <summary>Restaura los valores originales de un diccionario base. Idempotente.</summary>
    public static void RestoreBase(AppTheme baseTheme)
    {
        try
        {
            var key = baseTheme == AppTheme.Light ? "Light" : "Dark";
            Restore(GetThemeDictionary(key), _originalsRoot[key]);
            Restore(GetOverridesThemeDictionary(key), _originalsOverrides[key]);
        }
        catch { }
    }

    private static void Restore(ResourceDictionary? dict, Dictionary<string, object> snapshot)
    {
        if (dict == null) return;
        foreach (var (key, value) in snapshot)
            dict[key] = value;
    }

    private static Dictionary<string, object> Capture(ResourceDictionary? dict, IEnumerable<string> keys)
    {
        var snapshot = new Dictionary<string, object>();
        if (dict == null) return snapshot;
        foreach (var key in keys)
            if (dict.TryGetValue(key, out var value))
                snapshot[key] = value;
        return snapshot;
    }

    private static IEnumerable<string> AllSemanticKeys()
    {
        foreach (var palette in new[] { Pink, BlueBlack })
            foreach (var entry in palette.Brushes)
                yield return entry.Key;
    }

    private static IEnumerable<string> AllOverrideKeys()
    {
        foreach (var palette in new[] { Pink, BlueBlack })
        {
            foreach (var (_, key) in palette.OverrideBrushes) yield return key;
            foreach (var (key, _) in palette.AccentColors) yield return key;
        }
    }

    // =====================================================================
    // Resolución de diccionarios
    // =====================================================================

    private static ResourceDictionary? GetThemeDictionary(string themeKey)
    {
        try
        {
            return Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dict)
                ? dict as ResourceDictionary
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// ThemeDictionary de AccentOverrides para una clave de tema. Si no se
    /// encontró el host mergeado, cae al diccionario raíz (mejor que nada).
    /// </summary>
    private static ResourceDictionary? GetOverridesThemeDictionary(string themeKey)
    {
        var host = _overridesHost;
        if (host == null) return GetThemeDictionary(themeKey);
        return host.ThemeDictionaries.TryGetValue(themeKey, out var dict) ? dict as ResourceDictionary : null;
    }

    /// <summary>
    /// Devuelve el diccionario mergeado que define los SystemAccentColor*.
    /// Se queda con el ÚLTIMO que los define (AccentOverrides va después de
    /// XamlControlsResources en App.xaml, igual que el orden de lookup).
    /// </summary>
    private static ResourceDictionary? FindOverridesHost()
    {
        try
        {
            ResourceDictionary? last = null;
            foreach (var merged in Application.Current.Resources.MergedDictionaries)
            {
                foreach (var value in merged.ThemeDictionaries.Values)
                {
                    if (value is ResourceDictionary rd && rd.ContainsKey("SystemAccentColor"))
                    {
                        last = merged;
                        break;
                    }
                }
            }
            return last;
        }
        catch { return null; }
    }

    // =====================================================================
    // Construcción de valores
    // =====================================================================

    public sealed class Entry
    {
        public string Key { get; }
        public Brush Brush { get; }

        public Entry(string key, string hex)
        {
            Key = key;
            Brush = MakeBrush(hex);
        }
    }

    private static SolidColorBrush MakeBrush(string hex)
    {
        var (a, r, g, b) = ParseHex(hex);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
    }

    private static Windows.UI.Color MakeColor(string hex)
    {
        var (a, r, g, b) = ParseHex(hex);
        return Windows.UI.Color.FromArgb(a, r, g, b);
    }

    private static (byte a, byte r, byte g, byte b) ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        byte a = 255;
        if (h.Length == 8) { a = Convert.ToByte(h[..2], 16); h = h[2..]; }
        return (a, Convert.ToByte(h[..2], 16), Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16));
    }

    public sealed class ThemePalette
    {
        /// <summary>Pinceles semánticos → ThemeDictionaries de App.xaml.</summary>
        public required Entry[] Brushes { get; init; }

        /// <summary>Pinceles → diccionario mergeado AccentOverrides.xaml.</summary>
        public required (string Key, string Hex)[] OverrideBrushes { get; init; }

        /// <summary>Colores SystemAccentColor* (tipo Color) → AccentOverrides.xaml.</summary>
        public required (string Key, string Hex)[] AccentColors { get; init; }
    }

    // =====================================================================
    // ROSA / BLANCO — base clara
    // =====================================================================

    private static readonly ThemePalette Pink = new()
    {
        // Fondo rosa bien marcado y cards BLANCAS: el contraste viene del color,
        // no de un borde apenas visible. Navbar blanco = mismo bloque que las cards.
        Brushes =
        [
            new Entry("AppBackgroundBrush", "#FFF9E6F0"),
            new Entry("CardBackgroundBrush", "#FFFFFFFF"),
            new Entry("CardBorderBrush", "#FFF2C9DD"),
            new Entry("SecondaryTextBrush", "#FF8A5E74"),

            new Entry("AccentBrush", "#FFD6338A"),
            new Entry("AccentForegroundBrush", "#FFFFFFFF"),

            new Entry("ChartBackgroundBrush", "#FFFDF2F8"),
            new Entry("ChartBackgroundHotBrush", "#FFFBE4EF"),
            new Entry("ChartGridBrush", "#FFF3D5E4"),
            new Entry("ChartCrosshairBrush", "#FFDBA3C3"),
            new Entry("ChartAxisTextBrush", "#FF9A6E85"),
            new Entry("ChartHoverBadgeBgBrush", "#FFFBE4EF"),
            new Entry("ChartHoverBadgeBorderBrush", "#FFE8C0D4"),
            new Entry("ChartHoverTextBrush", "#FF4A2436"),

            new Entry("MetricUsageBrush", "#FFC2185B"),
            new Entry("MetricTempBrush", "#FF3A9A4A"),
            new Entry("MetricPowerBrush", "#FFC99600"),

            new Entry("SensorGridLineBrush", "#FFF3D5E4"),
            new Entry("SensorGroupFillBrush", "#FFFFFFFF"),
            new Entry("SensorCategoryFillBrush", "#FFFBEEF5"),

            new Entry("DisabledCardBackgroundBrush", "#FFFBE9E6"),
            new Entry("DisabledCardTextBrush", "#FF8A4A42"),

            new Entry("CardHoverBrush", "#FFFDF1F7"),
            new Entry("CardSelectedBrush", "#FFF9DCEA"),
            new Entry("AccentTintBrush", "#22D6338A"),
            new Entry("MutedBrush", "#FFB0879C"),

            new Entry("OnboardingScrimBrush", "#73FFFFFF"),
            new Entry("OnboardingSurfaceBrush", "#E6FFFFFF"),

            new Entry("ChipBackgroundBrush", "#FFFBEEF5"),

            new Entry("CoreCardBackgroundBrush", "#FFFCF3F8"),
            new Entry("CoreTrackBackgroundBrush", "#FFF6DEEA")
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FFD6338A"),
            ("SystemAccentColorForegroundBrush", "#FFFFFFFF"),
            ("AccentFillColorDefaultBrush", "#FFD6338A"),
            ("AccentFillColorSecondaryBrush", "#E6D6338A"),
            ("AccentFillColorTertiaryBrush", "#CCD6338A"),
            ("NavigationViewDefaultPaneBackground", "#FFFFFFFF"),
            ("NavigationViewContentBackground", "#FFF9E6F0")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FFD6338A"),
            ("SystemAccentColorLight1", "#FFDF4F9C"),
            ("SystemAccentColorLight2", "#FFD6338A"),
            ("SystemAccentColorLight3", "#FFC22A7B"),
            ("SystemAccentColorDark1", "#FFB02570"),
            ("SystemAccentColorDark2", "#FF9A2061"),
            ("SystemAccentColorDark3", "#FF841B53")
        ]
    };

    // =====================================================================
    // NEGRO / AZUL — base oscura (azul-negro profundo, acento celeste)
    // =====================================================================

    private static readonly ThemePalette BlueBlack = new()
    {
        // Todo el tema vive en la misma familia azul: fondo #060A12 y cards/navbar
        // #0E1524 forman UN solo bloque (navbar == cards, como pide el diseño),
        // con el celeste #4FC3F7 como único color de acento.
        Brushes =
        [
            new Entry("AppBackgroundBrush", "#FF060A12"),
            new Entry("CardBackgroundBrush", "#FF0E1524"),
            new Entry("CardBorderBrush", "#FF223047"),
            new Entry("SecondaryTextBrush", "#FF8FA3BC"),

            new Entry("AccentBrush", "#FF4FC3F7"),
            new Entry("AccentForegroundBrush", "#FF04121C"),

            new Entry("ChartBackgroundBrush", "#FF05080E"),
            new Entry("ChartBackgroundHotBrush", "#FF0B1626"),
            new Entry("ChartGridBrush", "#FF16202F"),
            new Entry("ChartCrosshairBrush", "#FF33415A"),
            new Entry("ChartAxisTextBrush", "#FF66788F"),
            new Entry("ChartHoverBadgeBgBrush", "#FF16202F"),
            new Entry("ChartHoverBadgeBorderBrush", "#FF223047"),
            new Entry("ChartHoverTextBrush", "#FFE8F2FC"),

            new Entry("MetricUsageBrush", "#FF4CC2C9"),
            new Entry("MetricTempBrush", "#FF4CC257"),
            new Entry("MetricPowerBrush", "#FFFFC93C"),

            new Entry("SensorGridLineBrush", "#FF223047"),
            new Entry("SensorGroupFillBrush", "#FF0E1524"),
            new Entry("SensorCategoryFillBrush", "#FF0A0F1A"),

            new Entry("DisabledCardBackgroundBrush", "#FF2B1C1C"),
            new Entry("DisabledCardTextBrush", "#FFFFC1BC"),

            new Entry("CardHoverBrush", "#FF141C2E"),
            new Entry("CardSelectedBrush", "#FF13233A"),
            new Entry("AccentTintBrush", "#224FC3F7"),
            new Entry("MutedBrush", "#FF7A8CA3"),

            new Entry("OnboardingScrimBrush", "#55000000"),
            new Entry("OnboardingSurfaceBrush", "#D90E1524"),

            new Entry("ChipBackgroundBrush", "#FF16202F"),

            new Entry("CoreCardBackgroundBrush", "#FF0E1524"),
            new Entry("CoreTrackBackgroundBrush", "#FF070B12")
        ],
        OverrideBrushes =
        [
            ("SystemAccentColorBrush", "#FF4FC3F7"),
            ("SystemAccentColorForegroundBrush", "#FF04121C"),
            ("AccentFillColorDefaultBrush", "#FF4FC3F7"),
            ("AccentFillColorSecondaryBrush", "#E64FC3F7"),
            ("AccentFillColorTertiaryBrush", "#CC4FC3F7"),
            ("NavigationViewDefaultPaneBackground", "#FF0E1524"),
            ("NavigationViewContentBackground", "#FF060A12")
        ],
        AccentColors =
        [
            ("SystemAccentColor", "#FF4FC3F7"),
            ("SystemAccentColorLight1", "#FF6BD0FA"),
            ("SystemAccentColorLight2", "#FF4FC3F7"),
            ("SystemAccentColorLight3", "#FF3AB4EE"),
            ("SystemAccentColorDark1", "#FF2FA9E0"),
            ("SystemAccentColorDark2", "#FF1E93C8"),
            ("SystemAccentColorDark3", "#FF127CB0")
        ]
    };
}
