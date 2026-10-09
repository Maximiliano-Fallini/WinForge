namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio para gestionar el tema de la aplicación (claro/oscuro).
/// </summary>
public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    event EventHandler<AppTheme>? ThemeChanged;
    void SetTheme(AppTheme theme);
}

/// <summary>
/// Temas disponibles para la aplicación.
///
/// Light/Dark/SystemDefault son los clásicos. El resto son temas con paleta propia:
/// heredan la estructura de un diccionario base (Light o Dark) y pisan sus pinceles de
/// identidad (acento, fondos, cards, navbar) y, los cuatro últimos, también la imagen de
/// fondo. Cada uno tiene su configuración completa en un archivo de la carpeta
/// <c>Themes\</c> de la UI (ver ThemeCatalog).
///
/// IMPORTANTE: no reordenar ni renumerar. SettingsService persiste el enum por
/// NÚMERO en settings.json ("AppTheme"), así que los valores ya guardados deben
/// seguir resolviendo igual: solo se agregan valores nuevos AL FINAL.
/// </summary>
public enum AppTheme
{
    Light = 0,
    Dark = 1,
    SystemDefault = 2,

    /// <summary>"Rosa / Blanco": base clara con acento rosa.</summary>
    PinkLight = 3,

    /// <summary>"Negro / Azul": base oscura (negro puro) con acento celeste.</summary>
    BlueBlack = 4,

    /// <summary>"Nebulosa": ELIMINADO. Se conserva el valor para no renumerar el enum
    /// (settings.json guarda por número); quien lo tenga guardado migra a Marea al
    /// arrancar (ver ThemeService).</summary>
    [Obsolete("Tema eliminado: Nebulosa. Migrado a Marea.")]
    Nebula = 5,

    /// <summary>"Marea": base oscura con foto de agua detrás de toda la ventana y superficies
    /// translúcidas.</summary>
    Tide = 6,

    /// <summary>"Aurora": base oscura con foto de aurora boreal detrás de toda la ventana,
    /// acento verde menta con contrapunto violeta.</summary>
    Aurora = 7,

    /// <summary>"Brasa": base oscura con foto de brasas detrás de toda la ventana, el único
    /// tema cálido (acento naranja de fuego).</summary>
    Brasa = 8,

    /// <summary>"Crepúsculo": base oscura con un gradiente de atardecer propio (violeta → coral)
    /// y SIN foto de fondo: el primer tema del grupo "Degradado" del selector.</summary>
    Twilight = 9
}
