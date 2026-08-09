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
/// </summary>
public enum AppTheme
{
    Light,
    Dark,
    SystemDefault
}