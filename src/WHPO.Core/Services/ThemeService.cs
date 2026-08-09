using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Interfaz abstracta para aplicar el tema a la ventana raíz.
/// Permite desacoplar el ThemeService de WinUI.
/// </summary>
public interface IThemeApplier
{
    void ApplyTheme(AppTheme theme);
    AppTheme GetSystemTheme();
}

/// <summary>
/// Implementación del servicio de tema.
/// Gestiona el tema de la aplicación (claro/oscuro/sistema).
/// </summary>
public class ThemeService : IThemeService
{
    private readonly ILoggingService _logger;
    private readonly ISettingsService _settingsService;
    private readonly IThemeApplier _themeApplier;
    private AppTheme _currentTheme;

    public ThemeService(ILoggingService logger, ISettingsService settingsService, IThemeApplier themeApplier)
    {
        _logger = logger;
        _settingsService = settingsService;
        _themeApplier = themeApplier;

        // Cargar tema guardado o usar sistema por defecto
        var savedTheme = _settingsService.Get("AppTheme", AppTheme.SystemDefault);
        _currentTheme = savedTheme;
    }

    public AppTheme CurrentTheme => _currentTheme;

    public event EventHandler<AppTheme>? ThemeChanged;

    public void SetTheme(AppTheme theme)
    {
        if (_currentTheme == theme)
            return;

        _currentTheme = theme;
        _settingsService.Set("AppTheme", theme);
        _settingsService.Save();

        var effectiveTheme = theme == AppTheme.SystemDefault
            ? _themeApplier.GetSystemTheme()
            : theme;

        _themeApplier.ApplyTheme(effectiveTheme);
        ThemeChanged?.Invoke(this, theme);

        _logger.LogInfo($"Tema cambiado a: {theme}");
    }

    public void ToggleTheme()
    {
        var newTheme = _currentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        SetTheme(newTheme);
    }

    /// <summary>
    /// Inicializa el tema al arrancar la aplicación.
    /// </summary>
    public void Initialize()
    {
        var effectiveTheme = _currentTheme == AppTheme.SystemDefault
            ? _themeApplier.GetSystemTheme()
            : _currentTheme;

        _themeApplier.ApplyTheme(effectiveTheme);
        _logger.LogInfo($"Tema inicializado: {_currentTheme}");
    }
}