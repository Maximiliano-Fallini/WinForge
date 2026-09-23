using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    /// Aviso de un nodo que no se pudo traducir en una pasada. Un elemento que
    /// falla se AÍSLA (el recorrido sigue) y queda anotado acá: el arranque lo manda
    /// al log y a la bitácora de traducciones.
    /// </summary>
    public static Action<string>? WalkError;

    /// <summary>
    /// Con esto activado, cada texto que el recorrido visita queda anotado en
    /// <see cref="TraceLines"/>: elemento, texto actual y qué se aplicó. Es la única
    /// forma de ver QUÉ hay en pantalla sin poder mirarla (la app pide elevación y no
    /// se puede abrir desde acá), y es lo que contesta "este texto quedó en español
    /// porque no es clave" contra "quedó en español porque nadie lo visitó".
    /// </summary>
    public static bool TraceEnabled { get; set; }

    /// <summary>Textos visitados en la última pasada trazada (ver <see cref="TraceEnabled"/>).</summary>
    public static List<string> TraceLines { get; } = new();

    /// <summary>Tope de líneas del trazo: una página grande tiene miles de textos.</summary>
    private const int TraceMaxLines = 4000;

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
    /// Resultado de una pasada de traducción: cuántos nodos se recorrieron, cuántos
    /// textos se tradujeron/registraron y cuántos nodos fallaron. Lo usa el arranque
    /// para decidir si vale volver a pasar (una página sigue armando contenido) y
    /// para dejar la evidencia en la bitácora cuando algo no se pudo traducir.
    /// </summary>
    public struct TranslatePass
    {
        public int Visited;
        public int Translated;
        public int Registered;
        public int Failed;

        /// <summary>True si esta pasada encontró algo para traducir o registrar.</summary>
        public bool Changed => Translated > 0 || Registered > 0;

        public override string ToString()
            => $"recorridos={Visited} traducidos={Translated} registrados={Registered} fallidos={Failed}";
    }

    /// <summary>
    /// Recorre el árbol traduciendo los textos estáticos. Es seguro llamarlo
    /// varias veces (al cambiar de idioma): los textos modificados dinámicamente
    /// (por ejemplo mensajes de estado) no se pisan.
    ///
    /// Recorre DOS árboles porque ninguno alcanza solo:
    /// - el VISUAL tiene lo que ya se realizó (incluidos los hijos que el template
    ///   creó: items de listas virtualizadas, contenido de popups abiertos);
    /// - el LÓGICO tiene lo que todavía no se realizó, que es justo el caso de las
    ///   partes de la página que se ven después: el contenido de un Button/Expander
    ///   colapsado (sin medir, su template no se aplicó) y las pestañas cerradas.
    /// Con el visual solo, esos textos quedaban en español hasta que el usuario
    /// cambiaba de idioma en la misma vista.
    /// </summary>
    public static TranslatePass ApplyToVisualTree(DependencyObject root)
    {
        var pass = new TranslatePass();
        if (root == null) return pass;

        try
        {
            Walk(root, new HashSet<DependencyObject>(), ref pass);
        }
        catch (Exception ex)
        {
            // Walk aísla nodo por nodo; esto es la red de seguridad para que un
            // fallo del recorrido en sí no deje la UI a medio traducir en silencio.
            pass.Failed++;
            WalkError?.Invoke($"Traducción: el recorrido falló en {Describe(root)}: {ex.Message}");
        }
        return pass;
    }

    private static void Walk(DependencyObject node, HashSet<DependencyObject> visited, ref TranslatePass pass)
    {
        // El conjunto de visitados es obligatorio desde que el recorrido mira los dos
        // árboles: el mismo objeto aparece como hijo visual y como contenido lógico, y
        // sin deduplicar el recorrido se multiplicaría por cada nivel de profundidad.
        if (node == null || !visited.Add(node)) return;
        pass.Visited++;

        // Antes, la pasada entera vivía dentro de UN try/catch que se tragaba el error:
        // un solo elemento problemático (por ejemplo uno que está saliendo del árbol en
        // ese instante) cortaba el recorrido y TODO lo que venía después quedaba en
        // español. Ahora cada nodo se aísla: el que falla se anota y el resto sigue.
        try { TranslateNode(node, visited, ref pass); }
        catch (Exception ex)
        {
            pass.Failed++;
            WalkError?.Invoke($"Traducción: no se pudo traducir {Describe(node)}: {ex.Message}");
        }

        foreach (var logical in LogicalChildren(node))
            Walk(logical, visited, ref pass);

        int count;
        try { count = VisualTreeHelper.GetChildrenCount(node); }
        catch (Exception ex)
        {
            pass.Failed++;
            WalkError?.Invoke($"Traducción: no se pudieron leer los hijos de {Describe(node)}: {ex.Message}");
            return;
        }

        for (int i = 0; i < count; i++)
        {
            DependencyObject? child;
            try { child = VisualTreeHelper.GetChild(node, i); }
            catch (Exception ex)
            {
                pass.Failed++;
                WalkError?.Invoke($"Traducción: no se pudo leer el hijo {i} de {Describe(node)}: {ex.Message}");
                continue;
            }
            if (child != null) Walk(child, visited, ref pass);
        }
    }

    /// <summary>
    /// Hijos LÓGICOS del nodo: el contenido declarado en XAML que puede no estar
    /// todavía en el árbol visual (contenedores sin medir, pestañas cerradas, items
    /// de un SelectorBar). Devuelve objetos, no elementos ya pintados.
    /// </summary>
    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject node)
    {
        switch (node)
        {
            // Expander: el Header es lo que se ve con el panel plegado y no es hijo
            // visual hasta que se expande (el "Ajustes avanzados del procesador" que
            // quedaba en español, porque el Expander arranca colapsado y lo muestra un
            // sondeo asincrónico). Se recorren Header y Content.
            case Expander expander:
                if (expander.Header is DependencyObject expanderHeader) yield return expanderHeader;
                if (expander.Content is DependencyObject expanderContent) yield return expanderContent;
                break;
            case ToggleSwitch toggle when toggle.Header is DependencyObject toggleHeader:
                yield return toggleHeader;
                break;
            case ComboBox comboBoxHeader when comboBoxHeader.Header is DependencyObject comboHeader:
                yield return comboHeader;
                break;
            case ContentControl contentControl when contentControl.Content is DependencyObject content:
                yield return content;
                break;
            case Border border when border.Child is DependencyObject borderChild:
                yield return borderChild;
                break;
            case Viewbox viewbox when viewbox.Child is DependencyObject viewboxChild:
                yield return viewboxChild;
                break;
            case ScrollViewer scroll when scroll.Content is DependencyObject scrollContent:
                yield return scrollContent;
                break;
            case ContentPresenter presenter when presenter.Content is DependencyObject presenterContent:
                yield return presenterContent;
                break;
            case Popup popup when popup.Child is DependencyObject popupChild:
                yield return popupChild;
                break;
            // SelectorBar no expone su contenido como hijo visual hasta realizarlo: sus
            // items (los textos de las pestañas internas) se alcanzan por Items.
            case SelectorBar bar:
                foreach (var item in bar.Items)
                    if (item is DependencyObject barItem) yield return barItem;
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                    if (child is DependencyObject panelChild) yield return panelChild;
                break;
            case ItemsControl itemsControl:
                foreach (var item in itemsControl.Items)
                    if (item is DependencyObject itemChild) yield return itemChild;
                break;
        }
    }

    /// <summary>Nombre legible de un nodo para la bitácora: tipo y x:Name si lo tiene.</summary>
    private static string Describe(DependencyObject node)
    {
        var name = node is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) ? $" '{fe.Name}'" : string.Empty;
        return $"{node.GetType().Name}{name}";
    }

    // ===== Flyouts: viven fuera del árbol hasta que se abren =====

    /// <summary>
    /// Traduce el flyout declarado por un elemento (menú contextual, menú de botón o
    /// flyout adjunto). Sus items no son hijos visuales hasta que el flyout se abre,
    /// así que el recorrido no los veía y quedaban en español.
    /// </summary>
    private static void TranslateFlyout(FlyoutBase? flyout, HashSet<DependencyObject> visited, ref TranslatePass pass)
    {
        switch (flyout)
        {
            case MenuFlyout menu:
                TranslateMenuItems(menu.Items, visited, ref pass);
                break;
            case Flyout flyoutContent when flyoutContent.Content is DependencyObject content:
                Walk(content, visited, ref pass);
                break;
        }
    }

    private static void TranslateMenuItems(IEnumerable<MenuFlyoutItemBase> items, HashSet<DependencyObject> visited, ref TranslatePass pass)
    {
        foreach (var item in items)
        {
            Walk(item, visited, ref pass);
            if (item is MenuFlyoutSubItem subItem) TranslateMenuItems(subItem.Items, visited, ref pass);
        }
    }

    /// <summary>Flyouts que declara un elemento: contextual, adjunto y el de un botón.</summary>
    private static IEnumerable<FlyoutBase> DeclaredFlyouts(FrameworkElement owner)
    {
        if (owner.ContextFlyout != null) yield return owner.ContextFlyout;
        if (FlyoutBase.GetAttachedFlyout(owner) is { } attached) yield return attached;
        if (owner is Button button && button.Flyout != null) yield return button.Flyout;
    }

    /// <summary>Elementos que ya tienen enganchado el re-traducido de apertura.</summary>
    private static readonly ConditionalWeakTable<DependencyObject, object> OpenHooks = new();

    /// <summary>
    /// Engancha el re-traducido JUSTO antes de que el contenido se muestre: el popup de
    /// un ComboBox (DropDownOpened) y el cuerpo de un menú/flyout (Opening). Es contenido
    /// que vive en su propio árbol —el recorrido de la página no lo alcanza— y que puede
    /// crearse recién al abrirse: sin esto se veía en español hasta el próximo repaso.
    /// Se engancha UNA vez por elemento (no se limpia hasta que el elemento muere).
    /// </summary>
    private static void HookBeforeOpen(DependencyObject node)
    {
        if (node is ComboBox combo && !OpenHooks.TryGetValue(combo, out _))
        {
            OpenHooks.Add(combo, new object());
            combo.DropDownOpened += (_, _) => TranslateNow(combo);
        }

        if (node is FrameworkElement owner)
        {
            foreach (var flyout in DeclaredFlyouts(owner))
            {
                if (OpenHooks.TryGetValue(flyout, out _)) continue;
                OpenHooks.Add(flyout, new object());
                flyout.Opening += (_, _) => TranslateNow(flyout);
            }
        }
    }

    /// <summary>Traduce en el acto un elemento (o el contenido de un flyout).</summary>
    private static void TranslateNow(DependencyObject node)
    {
        var pass = new TranslatePass();
        try
        {
            if (node is FlyoutBase flyout)
                TranslateFlyout(flyout, new HashSet<DependencyObject>(), ref pass);
            else
                Walk(node, new HashSet<DependencyObject>(), ref pass);
        }
        catch (Exception ex)
        {
            WalkError?.Invoke($"Traducción: al abrir {Describe(node)}: {ex.Message}");
        }
    }

    private static void TranslateNode(DependencyObject node, HashSet<DependencyObject> visited, ref TranslatePass pass)
    {
        // ToolTipService.ToolTip es una propiedad ADJUNTA y el texto del tooltip no es hijo
        // visual del elemento, así que el recorrido no lo veía: un tooltip escrito en XAML
        // quedaba en español en todos los idiomas. Los que se arman en código ya pasan por
        // I18n.T; este es el camino de los de XAML.
        if (node is FrameworkElement owner)
        {
            if (ToolTipService.GetToolTip(owner) is string tip)
                ApplyText(node, ToolTipService.ToolTipProperty, tip, ref pass);

            // Flyouts declarados en XAML (menú contextual del navbar, menús de botón):
            // sus items existen como objetos pero no están en el árbol visual hasta que
            // el usuario los abre, así que se traducen por referencia al que los declara.
            foreach (var flyout in DeclaredFlyouts(owner))
                TranslateFlyout(flyout, visited, ref pass);
        }

        // Y se engancha el re-traducido previo a la apertura: el contenido que aparece
        // al abrir (el popup de un ComboBox, el cuerpo de un menú) se dibuja FUERA del
        // árbol de la página, así que su texto se traducía recién en el próximo repaso:
        // el desplegable se veía en español un instante y después se corregía solo.
        HookBeforeOpen(node);

        switch (node)
        {
            case TextBlock tb:
                ApplyText(tb, TextBlock.TextProperty, tb.Text, ref pass);
                break;
            case TextBox box:
                ApplyText(box, TextBox.PlaceholderTextProperty, box.PlaceholderText, ref pass);
                ApplyHeader(box, TextBox.HeaderProperty, box.Header, ref pass);
                break;
            case PasswordBox pwd:
                ApplyText(pwd, PasswordBox.PlaceholderTextProperty, pwd.PlaceholderText, ref pass);
                ApplyHeader(pwd, PasswordBox.HeaderProperty, pwd.Header, ref pass);
                break;
            case ComboBox combo:
                ApplyText(combo, ComboBox.PlaceholderTextProperty, combo.PlaceholderText, ref pass);
                ApplyHeader(combo, ComboBox.HeaderProperty, combo.Header, ref pass);
                break;
            // Los items de un desplegable declarados en XAML son objetos ComboBoxItem
            // con el texto en Content: sin este caso el desplegable quedaba en español
            // (el popup vive fuera del árbol, así que nadie más lo traducía a tiempo).
            case ComboBoxItem comboItem:
                ApplyObject(comboItem, ComboBoxItem.ContentProperty, comboItem.Content, ref pass);
                break;
            case Button btn:
                ApplyObject(btn, Button.ContentProperty, btn.Content, ref pass);
                break;
            case HyperlinkButton hb:
                ApplyObject(hb, HyperlinkButton.ContentProperty, hb.Content, ref pass);
                break;
            case CheckBox cb:
                ApplyObject(cb, CheckBox.ContentProperty, cb.Content, ref pass);
                break;
            case RadioButton rb:
                ApplyObject(rb, RadioButton.ContentProperty, rb.Content, ref pass);
                break;
            case NavigationViewItem nvi:
                ApplyObject(nvi, NavigationViewItem.ContentProperty, nvi.Content, ref pass);
                break;
            case SelectorBarItem sbi:
                ApplyText(sbi, SelectorBarItem.TextProperty, sbi.Text, ref pass);
                break;
            case MenuFlyoutItem mfi:
                // Cubre también ToggleMenuFlyoutItem y MenuFlyoutSubItem.
                ApplyText(mfi, MenuFlyoutItem.TextProperty, mfi.Text, ref pass);
                break;
            case Expander exp:
                ApplyObject(exp, Expander.HeaderProperty, exp.Header, ref pass);
                break;
            case ToggleSwitch ts:
                ApplyObject(ts, ToggleSwitch.HeaderProperty, ts.Header, ref pass);
                ApplyObject(ts, ToggleSwitch.OnContentProperty, ts.OnContent, ref pass);
                ApplyObject(ts, ToggleSwitch.OffContentProperty, ts.OffContent, ref pass);
                break;
            case InfoBar bar:
                ApplyText(bar, InfoBar.TitleProperty, bar.Title, ref pass);
                ApplyText(bar, InfoBar.MessageProperty, bar.Message, ref pass);
                break;
        }
    }

    private static void ApplyHeader(DependencyObject el, DependencyProperty dp, object? header, ref TranslatePass pass)
    {
        if (header is string s) ApplyText(el, dp, s, ref pass);
    }

    private static void ApplyObject(DependencyObject el, DependencyProperty dp, object? value, ref TranslatePass pass)
    {
        if (value is string s) ApplyText(el, dp, s, ref pass);
    }

    private static void ApplyText(DependencyObject el, DependencyProperty dp, string? current, ref TranslatePass pass)
    {
        if (string.IsNullOrEmpty(current)) return;

        if (TraceEnabled && TraceLines.Count < TraceMaxLines)
            TraceLines.Add($"{Describe(el)} = \"{current}\"");

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
                    pass.Translated++;
                    if (TraceEnabled && TraceLines.Count < TraceMaxLines)
                        TraceLines.Add($"    → cambio a \"{t}\"");
                }
                else if (TraceEnabled && TraceLines.Count < TraceMaxLines)
                {
                    // El texto es una clave conocida y la traducción NO cambió nada: es
                    // el caso exacto del usuario ("quedó en español").
                    TraceLines.Add(string.Equals(t, original.Es, StringComparison.Ordinal)
                        ? $"    → SIN TRADUCCIÓN para el idioma activo ({Current}): clave = \"{original.Es}\""
                        : $"    → ya estaba traducido");
                }
            }
            else if (TraceEnabled && TraceLines.Count < TraceMaxLines)
            {
                TraceLines.Add($"    → texto dinámico (no es clave; se respeta)");
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
            pass.Registered++;
            var t = T(source);
            if (!string.Equals(t, current, StringComparison.Ordinal))
            {
                el.SetValue(dp, t);
                entry.LastApplied = t;
                pass.Translated++;
                if (TraceEnabled && TraceLines.Count < TraceMaxLines)
                    TraceLines.Add($"    → cambio a \"{t}\"");
            }
            else if (TraceEnabled && TraceLines.Count < TraceMaxLines)
            {
                TraceLines.Add(string.Equals(t, source, StringComparison.Ordinal)
                    ? $"    → SIN TRADUCCIÓN para el idioma activo ({Current}): clave = \"{source}\""
                    : $"    → ya estaba traducido");
            }
        }
        else if (TraceEnabled && TraceLines.Count < TraceMaxLines)
        {
            TraceLines.Add($"    → texto sin clave (dinámico o texto sin traducir)");
        }
    }
}
