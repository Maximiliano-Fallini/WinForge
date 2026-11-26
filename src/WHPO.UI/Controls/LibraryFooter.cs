namespace WHPO_UI.Controls;

/// <summary>
/// Estilo del pie de la biblioteca 3D. Son dos lecturas del MISMO dock (misma ranura,
/// mismo arrastre, mismo estado): la consola anclada al borde inferior o una barra
/// flotante despegada del fondo, como el pie de Carthage.
///
/// Es una preferencia de apariencia (<c>biblioteca.cards3D.footer</c>) y se elige en
/// Configuración → Apariencia, solo con las cards 3D activas. La biblioteca la relee
/// al abrirse; no cambia la mecánica ni la geometría interna del dock.
/// </summary>
public static class LibraryFooter
{
    /// <summary>Ajuste persistido con el id del estilo.</summary>
    public const string SettingKey = "biblioteca.cards3D.footer";

    /// <summary>Consola de siempre, apoyada en el borde inferior de la ventana.</summary>
    public const string ConsoleId = "console";

    /// <summary>Barra de vidrio flotante, separada del fondo.</summary>
    public const string FloatingId = "floating";

    public const string DefaultId = ConsoleId;

    public static bool IsFloating(string? id) =>
        string.Equals(id, FloatingId, System.StringComparison.OrdinalIgnoreCase);
}
