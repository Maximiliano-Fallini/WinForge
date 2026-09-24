using System;
using System.Collections.Generic;

namespace WHPO.Core.Components;

/// <summary>Categoría con la que se agrupa un componente en el Workshop.</summary>
public enum ComponentCategory
{
    Juego,
    Rendimiento,
    Latencia,
    Monitoreo,
    Sistema
}

/// <summary>
/// Contrato mínimo que implementan TODOS los componentes de WinForge (los built-in
/// del navbar y los descargados desde GitHub por el Workshop).
///
/// Está en WHPO.Core a propósito: un componente descargable es una DLL chica que
/// referencia Core (por este contrato) y construye su UI EN CÓDIGO (sin XAML
/// compilado — evita los dolores de XAML en assemblies cargados por reflexión con
/// AssemblyLoadContext). Las dependencias pesadas (WinAppSDK, servicios) ya viven
/// en la app: el ALC del componente resuelve lo propio de su carpeta y delega el
/// resto al contexto por defecto.
/// </summary>
public interface IWinForgeComponent
{
    /// <summary>Identificador único, también tag del navbar (ej. "memoria").</summary>
    string Id { get; }

    /// <summary>Nombre visible, texto fuente en español (el motor i18n lo traduce).</summary>
    string Name { get; }

    /// <summary>Descripción corta para la card del Workshop (español, traducible).</summary>
    string Description { get; }

    /// <summary>Glifo de Segoe Fluent Icons para navbar y card (ej. "\uE765").</summary>
    string IconGlyph { get; }

    ComponentCategory Category { get; }

    /// <summary>Versión del componente (semver "X.Y.Z").</summary>
    string Version { get; }

    /// <summary>Versión mínima de la app requerida ("" = cualquier versión).</summary>
    string MinAppVersion { get; }

    /// <summary>
    /// True para los componentes de fábrica (siempre presentes, no ocultables ni
    /// desinstalables desde el Workshop: Sistema, Red, Núcleos, Biblioteca, Workshop).
    /// </summary>
    bool IsCore { get; }

    /// <summary>
    /// Crea la UI del componente. Debe devolver un Microsoft.UI.Xaml.FrameworkElement
    /// (puede ser una Page construida en código). Los built-in devuelven su Page.
    /// </summary>
    object CreatePage(IServiceProvider services);
}

/// <summary>Entrada del catálogo de componentes (components.json en el repo).</summary>
public sealed class ComponentCatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Category { get; set; } = "Sistema";
    public string Version { get; set; } = "1.0.0";
    public string MinAppVersion { get; set; } = "";
    /// <summary>URL del asset (zip con la DLL) en las Releases de GitHub.</summary>
    public string Url { get; set; } = "";
    /// <summary>SHA-256 del zip, en hex minúsculas. Si no coincide, se descarta.</summary>
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>
    /// Componente "en desarrollo": solo instalable desde builds de la app que
    /// están por delante de la última release publicada (modo desarrollo).
    /// En builds estables la card se muestra con el badge pero sin botón de instalar.
    /// </summary>
    public bool InDevelopment { get; set; }

    /// <summary>
    /// Traducciones de los textos de esta entrada, por código de idioma ("en-US",
    /// "pt-BR", "de-DE"…). El texto fuente está SIEMPRE en español (Name y
    /// Description): acá va solo lo traducido, y lo que falte cae al español.
    ///
    /// Existe para que publicar un componente no obligue a tocar la app: antes el
    /// nombre y la descripción solo se traducían si eran claves de la tabla de
    /// Translations.cs, así que un componente nuevo quedaba en español para todos los
    /// idiomas (incluido el inglés, que es el nativo de la app). Ver reglas_de_desarollo_.
    /// </summary>
    public Dictionary<string, ComponentLocalizedText> I18n { get; set; } = new();

    /// <summary>
    /// Pares (idioma, texto fuente en español, traducción) de esta entrada, para que
    /// el motor de traducciones los registre igual que cualquier otro texto.
    /// </summary>
    public IEnumerable<(string Language, string Source, string Translated)> Translations()
    {
        foreach (var kv in I18n)
        {
            var text = kv.Value;
            if (text == null) continue;
            if (!string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(text.Name))
                yield return (kv.Key, Name, text.Name);
            if (!string.IsNullOrWhiteSpace(Description) && !string.IsNullOrWhiteSpace(text.Description))
                yield return (kv.Key, Description, text.Description);

            // Textos de la UI del propio componente (bloque "strings" del mismo idioma):
            // sus pantallas se construyen en código y no pueden ser claves de la tabla de la
            // app, pero sí traducirse igual que el nombre y la descripción.
            if (text.Strings == null) continue;
            foreach (var pair in text.Strings)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                yield return (kv.Key, pair.Key, pair.Value);
            }
        }
    }
}

/// <summary>
/// Textos de una entrada del catálogo en un idioma (bloque "i18n" de components.json,
/// con la forma <c>"de-DE": { "name": "…", "description": "…", "strings": { "…": "…" } }</c>).
/// </summary>
public sealed class ComponentLocalizedText
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>
    /// Textos de la UI del componente: texto fuente en español → traducción. Es lo que permite
    /// que una pantalla construida en código (selectores, informes, avisos) se traduzca sin
    /// agregar claves a la tabla de la app ni publicar packs de idioma nuevos.
    /// </summary>
    public Dictionary<string, string> Strings { get; set; } = new();
}

/// <summary>Catálogo completo (raíz de components.json).</summary>
public sealed class ComponentCatalog
{
    public int CatalogVersion { get; set; } = 1;
    public List<ComponentCatalogEntry> Components { get; set; } = new();
}

/// <summary>Registro de un componente descargado e instalado localmente.</summary>
public sealed class InstalledComponentRecord
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Carpeta de instalación (%LOCALAPPDATA%\WHPO\Modules\&lt;id&gt;\&lt;versión&gt;).</summary>
    public string Path { get; set; } = "";
    public string InstalledAt { get; set; } = "";
}
