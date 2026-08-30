namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio para gestionar el tema de la aplicación (claro/oscuro).
/// </summary>
public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    event EventHandler<AppTheme>? ThemeChanged;
    void SetTheme(AppTheme theme);
    void ToggleTheme();
}

/// <summary>
/// Temas disponibles para la aplicación.
///
/// Light/Dark/SystemDefault son los clásicos. PinkLight ("Rosa / Blanco") y
/// BlueBlack ("Negro / Azul") son temas con paleta propia: heredan la estructura
/// de un diccionario base (Light o Dark) y pisan sus pinceles de identidad
/// (acento, fondos, cards, navbar) — ver ThemePalettes en la UI.
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
    BlueBlack = 4
}