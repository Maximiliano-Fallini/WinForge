using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI;

/// <summary>
/// Motor de traducciones. Las claves son el texto fuente en español (es-AR) y la
/// tabla embebida guarda su traducción al inglés: esos son los dos únicos idiomas
/// que trae la app.
///
/// TODO lo demás (pt-BR, de-DE, fr-FR y los que se agreguen) son PAQUETES
/// descargables del repositorio: se mergean en <see cref="Translations.AddPack"/> al
/// arrancar y el idioma aparece recién ahí en <see cref="Languages"/>.
///
/// La búsqueda hace fallback en cadena: idioma elegido → en-US → español (fuente).
///
/// Cómo se traduce la UI:
/// - XAML y texto estático: I18n.ApplyToVisualTree recorre el árbol visual y
/// traduce Text/Content/PlaceholderText/etc. El texto original (español) se
/// recuerda por elemento, así al cambiar de idioma se vuelve a traducir sin
/// pisar textos que se actualizaron dinámicamente.
/// - Código: los strings visibles se envuelven en I18n.T("..."). Feedback.Set
/// traduce automáticamente los mensajes que recibe.
/// </summary>
public static class I18n
{
    public const string DefaultLanguage = "en-US";
    /// <summary>Idioma activo. El set es internal para la vista previa (simulador de
    /// onboarding), que cambia el idioma SIN persistir; el camino normal es SetLanguage.</summary>
    public static string Current { get; internal set; } = DefaultLanguage;

    /// <summary>
    /// Dispara LanguageChanged sin tocar el settings: para la vista previa del
    /// simulador de onboarding, que re-traduce sin persistir nada.
    /// </summary>
    internal static void DispatchLanguageChanged() => RaiseLanguageChanged();

    /// <summary>Se dispara al cambiar de idioma (los suscriptores re-aplican la UI).</summary>
    public static event Action? LanguageChanged;

    /// <summary>
    /// Aviso de un suscriptor de <see cref="LanguageChanged"/> que falló al re-aplicar la UI.
    /// Lo engancha el arranque de la app para volcarlo al log.
    /// </summary>
    public static Action<string>? SubscriberError;

    /// <summary>
    /// Idiomas que trae la app sin descargar nada: el español (es-AR, que además es
    /// la fuente: las claves SON sus textos) y el inglés (en-US, la única columna
    /// embebida, que hace de fallback).
    ///
    /// Cualquier otro idioma llega como paquete descargable y entra a
    /// <see cref="Languages"/> cuando su pack está instalado.
    /// </summary>
    public static readonly string[] BuiltinLanguages = ["es-AR", "en-US"];

    private static string[] _languages = BuiltinLanguages;

    /// <summary>
    /// Idiomas disponibles: los embebidos + los que tienen un paquete descargado.
    /// De acá salen el selector de idioma, las banderas y la resolución del idioma
    /// del sistema. Se actualiza con <see cref="SetExtraLanguages"/> al instalar o
    /// quitar un pack (no requiere reiniciar la app).
    /// </summary>
    public static string[] Languages => _languages;

    /// <summary>Re-registra los idiomas descargables instalados (packs de idioma).</summary>
    public static void SetExtraLanguages(IEnumerable<string>? codes)
    {
        var list = BuiltinLanguages.ToList();
        if (codes != null)
        {
            foreach (var c in codes)
            {
                if (!string.IsNullOrWhiteSpace(c) && !list.Contains(c, StringComparer.OrdinalIgnoreCase))
                    list.Add(c);
            }
        }
        _languages = list.ToArray();
    }

    public static bool IsSupported(string code)
        => Array.IndexOf(Languages, code) >= 0;

    /// <summary>
    /// Normaliza cualquier código de idioma a uno disponible: variantes regionales de
    /// la misma lengua caen al idioma base (es-ES/es-MX → es-AR, en-GB → en-US,
    /// pt-PT → pt-BR, de-AT → de-DE, fr-CA → fr-FR). Si el usuario instaló el pack de
    /// un idioma nuevo, su variante regional también resuelve (it-CH → it-IT).
    /// Devuelve null si no se reconoce ningún idioma disponible.
    /// </summary>
    public static string? ResolveSupported(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        foreach (var l in Languages)
            if (string.Equals(l, code, StringComparison.OrdinalIgnoreCase))
                return l;
        // Segunda pasada por idioma base: cubre las variantes regionales y los packs.
        var baseLang = code.Split('-')[0];
        foreach (var l in Languages)
            if (string.Equals(l.Split('-')[0], baseLang, StringComparison.OrdinalIgnoreCase))
                return l;
        return null;
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
        RaiseLanguageChanged();
    }

    /// <summary>
    /// Vuelve a aplicar las traducciones a la UI SIN cambiar de idioma. Lo usa el
    /// Workshop después de registrar los textos que trae el catálogo de componentes:
    /// recién ahí esos textos pasan a ser claves conocidas, y el navbar (que se armó
    /// al arrancar, cuando el catálogo todavía no existía) tiene que volver a
    /// traducirse para que la pestaña del componente salga en el idioma activo.
    /// </summary>
    public static void Reapply() => RaiseLanguageChanged();

    /// <summary>
    /// Dispara LanguageChanged avisando a CADA suscriptor por separado.
    ///
    /// Un cambio de idioma es una pasada de re-traducción sobre toda la UI viva: la
    /// ventana, la página actual y todas las páginas ya visitadas. Si uno de esos
    /// suscriptores fallaba, la excepción subía hasta el manejador global y la app se
    /// cerraba por re-traducir una vista (pasó: armar de nuevo una lista reusando un
    /// elemento que ya tenía padre tira "Element is already the child of another
    /// element"). Acá se aísla: el que falla queda anotado con su nombre y el resto
    /// sigue re-traduciendo, así el cambio de idioma nunca es una bomba.
    /// </summary>
    private static void RaiseLanguageChanged()
    {
        var handlers = LanguageChanged;
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                var target = handler.Target?.GetType().Name ?? "(estático)";
                SubscriberError?.Invoke($"Idioma: '{target}' falló al re-aplicar la UI: {ex}");
            }
        }
    }

    // ===================== Aplicar a un árbol visual =====================

    /// <summary>Texto original (español) y última traducción aplicada, por elemento Y propiedad.</summary>
    private sealed class OriginalText
    {
        public required string Es;
        public required string LastApplied;
    }

    /// <summary>
    /// Originales por (elemento, propiedad): un mismo elemento puede tener varios textos
    /// traducibles — el Content de un botón y su tooltip, el Text de un TextBlock y su Header
    /// adjunto. Con una sola ranura por elemento, el SEGUNDO texto nunca se traducía: al
    /// compararlo contra el original del primero no coincidía y se lo tomaba por texto dinámico.
    /// </summary>
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, OriginalText>> Originals = new();

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
        // ToolTipService.ToolTip es una propiedad ADJUNTA y el texto del tooltip no es hijo
        // visual del elemento, así que el recorrido no lo veía: un tooltip escrito en XAML
        // quedaba en español en todos los idiomas. Los que se arman en código ya pasan por
        // I18n.T; este es el camino de los de XAML.
        if (node is FrameworkElement owner && ToolTipService.GetToolTip(owner) is string tip)
            ApplyText(node, ToolTipService.ToolTipProperty, tip);

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

        var byProperty = Originals.GetOrCreateValue(el);

        if (byProperty.TryGetValue(dp, out var original))
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
            byProperty[dp] = entry;
            var t = T(source);
            if (!string.Equals(t, current, StringComparison.Ordinal))
            {
                el.SetValue(dp, t);
                entry.LastApplied = t;
            }
        }
    }
}
