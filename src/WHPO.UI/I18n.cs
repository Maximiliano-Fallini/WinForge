using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Motor de traducciones embebidas. Los diccionarios están claveados por el texto
/// fuente en español (es-AR) y cada idioma tiene su traducción. La búsqueda hace
/// fallback en cadena: idioma elegido → en-US → español (fuente).
///
/// Cómo se traduce la UI:
///  - XAML y texto estático: I18n.ApplyToVisualTree() recorre el árbol visual y
///    traduce Text/Content/PlaceholderText/etc. El texto original (español) se
///    recuerda por elemento, así al cambiar de idioma se vuelve a traducir sin
///    pisar textos que se actualizaron dinámicamente.
///  - Código: los strings visibles se envuelven en I18n.T("..."). Feedback.Set
///    traduce automáticamente los mensajes que recibe.
/// </summary>
public static class I18n
{
    public const string DefaultLanguage = "en-US";
    public static string Current { get; private set; } = DefaultLanguage;

    /// <summary>Se dispara al cambiar de idioma (los suscriptores re-aplican la UI).</summary>
    public static event Action? LanguageChanged;

    public static readonly string[] Languages =
    [
        "es-AR", "en-US", "pt-BR", "de-DE", "fr-FR"
    ];

    public static bool IsSupported(string code)
        => Array.IndexOf(Languages, code) >= 0;

    /// <summary>
    /// Normaliza cualquier código de idioma a uno soportado: variantes regionales de
    /// la misma lengua caen al idioma base (es-ES/es-MX → es-AR, en-GB → en-US,
    /// pt-PT → pt-BR, de-AT → de-DE, fr-CA → fr-FR). Devuelve null si no se reconoce.
    /// </summary>
    public static string? ResolveSupported(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        foreach (var l in Languages)
            if (string.Equals(l, code, StringComparison.OrdinalIgnoreCase))
                return l;
        var baseLang = code.Split('-')[0].ToLowerInvariant();
        return baseLang switch
        {
            "es" => "es-AR",
            "en" => "en-US",
            "pt" => "pt-BR",
            "de" => "de-DE",
            "fr" => "fr-FR",
            _ => null
        };
    }

    /// <summary>
    /// Detecta el idioma del sistema (preferencia de idioma de Windows) y lo
    /// normaliza a uno soportado (variantes regionales caen al idioma base).
    /// Devuelve null si ningún idioma del sistema está disponible entre los
    /// soportados — el llamador decide el fallback (en-US).
    /// </summary>
    public static string? DetectSystemLanguage()
    {
        try
        {
            foreach (var lang in Windows.System.UserProfile.GlobalizationPreferences.Languages)
            {
                var resolved = ResolveSupported(lang);
                if (resolved != null) return resolved;
            }
        }
        catch { }
        return null;
    }

    public static void Initialize(ISettingsService settings)
    {
        // Primera vez (sin idioma guardado): detectar el idioma de la PC y aplicarlo
        // una sola vez. De ahí en más, manda el valor guardado.
        if (!settings.Contains("app.language"))
        {
            var detected = DetectSystemLanguage();
            if (detected != null)
            {
                Current = detected;
                settings.Set("app.language", detected);
                settings.Save();
                return;
            }
        }

        var saved = settings.Get("app.language", DefaultLanguage);
        Current = ResolveSupported(saved) ?? DefaultLanguage;
    }

    /// <summary>
    /// Traduce un string fuente (español) al idioma actual, con fallback en-US → español.
    /// Si no hay traducción, devuelve el texto original (nunca rompe la UI).
    /// </summary>
    public static string T(string es)
    {
        if (string.IsNullOrEmpty(es) || Current == "es-AR")
            return es;

        if (Translations.TryTranslate(Current, es, out var t))
            return t;

        // Fallback: en-US (si el idioma actual no es en-US).
        if (Current != "en-US" && Translations.TryTranslate("en-US", es, out var en))
            return en;

        return es;
    }

    /// <summary>
    /// Traduce una plantilla con marcadores ("{0}", "{1}", ...) y la formatea con los
    /// argumentos. La clave del diccionario es la plantilla en español; la traducción
    /// puede reordenar los marcadores libremente.
    /// </summary>
    public static string T(string template, params object?[] args)
    {
        var translated = T(template);
        if (args == null || args.Length == 0)
            return translated;
        try
        {
            return string.Format(translated, args);
        }
        catch (FormatException)
        {
            // Plantilla con llaves no balanceadas: devolver sin formatear.
            return translated;
        }
    }

    public static void SetLanguage(string code, ISettingsService settings)
    {
        var resolved = ResolveSupported(code);
        if (resolved == null || resolved == Current)
            return;

        Current = resolved;
        settings.Set("app.language", resolved);
        settings.Save();
        LanguageChanged?.Invoke();
    }

    // ===================== Aplicar a un árbol visual =====================

    /// <summary>Texto original (español) y última traducción aplicada por elemento.</summary>
    private sealed class OriginalText
    {
        public required string Es;
        public required string LastApplied;
    }

    private static readonly ConditionalWeakTable<object, OriginalText> Originals = new();

    /// <summary>
    /// Recorre el árbol visual traduciendo los textos estáticos. Es seguro llamarlo
    /// varias veces (al cambiar de idioma): los textos modificados dinámicamente
    /// (por ejemplo mensajes de estado) no se pisan.
    /// </summary>
    public static void ApplyToVisualTree(DependencyObject root)
    {
        if (root == null) return;
        try { Walk(root); }
        catch { /* Un error de recorrido no debe romper la navegación. */ }
    }

    private static void Walk(DependencyObject node)
    {
        TranslateNode(node);
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child != null) Walk(child);
        }
    }

    private static void TranslateNode(DependencyObject node)
    {
        switch (node)
        {
            case TextBlock tb:
                ApplyText(tb, TextBlock.TextProperty, tb.Text);
                break;
            case TextBox box:
                ApplyText(box, TextBox.PlaceholderTextProperty, box.PlaceholderText);
                ApplyHeader(box, TextBox.HeaderProperty, box.Header);
                break;
            case PasswordBox pwd:
                ApplyText(pwd, PasswordBox.PlaceholderTextProperty, pwd.PlaceholderText);
                ApplyHeader(pwd, PasswordBox.HeaderProperty, pwd.Header);
                break;
            case ComboBox combo:
                ApplyText(combo, ComboBox.PlaceholderTextProperty, combo.PlaceholderText);
                break;
            case Button btn:
                ApplyObject(btn, Button.ContentProperty, btn.Content);
                break;
            case HyperlinkButton hb:
                ApplyObject(hb, HyperlinkButton.ContentProperty, hb.Content);
                break;
            case CheckBox cb:
                ApplyObject(cb, CheckBox.ContentProperty, cb.Content);
                break;
            case RadioButton rb:
                ApplyObject(rb, RadioButton.ContentProperty, rb.Content);
                break;
            case NavigationViewItem nvi:
                ApplyObject(nvi, NavigationViewItem.ContentProperty, nvi.Content);
                break;
            case ComboBoxItem cbi:
                ApplyObject(cbi, ComboBoxItem.ContentProperty, cbi.Content);
                break;
            case MenuFlyoutItem mfi:
                // Cubre también ToggleMenuFlyoutItem (deriva de MenuFlyoutItem).
                ApplyText(mfi, MenuFlyoutItem.TextProperty, mfi.Text);
                break;
            case SelectorBarItem sbi:
                ApplyText(sbi, SelectorBarItem.TextProperty, sbi.Text);
                break;
            case Expander exp:
                ApplyObject(exp, Expander.HeaderProperty, exp.Header);
                break;
            case ToggleSwitch ts:
                ApplyObject(ts, ToggleSwitch.HeaderProperty, ts.Header);
                break;
        }
    }

    private static void ApplyHeader(DependencyObject el, DependencyProperty dp, object? header)
    {
        if (header is string s) ApplyText(el, dp, s);
    }

    private static void ApplyObject(DependencyObject el, DependencyProperty dp, object? value)
    {
        if (value is string s) ApplyText(el, dp, s);
    }

    private static void ApplyText(DependencyObject el, DependencyProperty dp, string? current)
    {
        if (string.IsNullOrEmpty(current)) return;

        if (Originals.TryGetValue(el, out var original))
        {
            // Se re-traduce si el texto actual es la fuente (español) o una traducción
            // conocida de esa fuente en cualquier idioma (la haya puesto el recorrido o
            // el código). Si el texto se modificó dinámicamente con algo que no
            // corresponde a la fuente (mensaje de estado, contador, valor), se respeta.
            if (string.Equals(current, original.Es, StringComparison.Ordinal)
                || Translations.SourceOf(current) == original.Es)
            {
                var t = T(original.Es);
                if (!string.Equals(t, current, StringComparison.Ordinal))
                {
                    el.SetValue(dp, t);
                    original.LastApplied = t;
                }
            }
            return;
        }

        // Texto no registrado: si es una clave (español) o una traducción conocida de
        // alguna clave (elemento creado en código mientras la app estaba en otro
        // idioma), registrarlo y traducirlo. Cualquier otro texto dinámico queda igual.
        if (Translations.TryGetSource(current, out var source))
        {
            var entry = new OriginalText { Es = source, LastApplied = current };
            Originals.Add(el, entry);
            var t = T(source);
            if (!string.Equals(t, current, StringComparison.Ordinal))
            {
                el.SetValue(dp, t);
                entry.LastApplied = t;
            }
        }
    }
}
