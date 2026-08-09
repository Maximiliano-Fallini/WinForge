using System.Collections.Generic;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Interfaz abstracta para un Frame de navegación.
/// Permite desacoplar el NavigationService de WinUI.
/// </summary>
public interface INavigationFrame
{
    void Navigate(Type pageType, object? parameter);
    void Navigate(Type pageType);
    bool CanGoBack { get; }
    void GoBack();
}

/// <summary>
/// Implementación del servicio de navegación.
/// Maneja el registro de páginas y la navegación entre ellas.
/// </summary>
public class NavigationService : INavigationService
{
    private readonly Dictionary<string, Type> _pageRegistry = new();
    private readonly ILoggingService _logger;
    private INavigationFrame? _frame;
    private string _currentPage = string.Empty;

    public NavigationService(ILoggingService logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Establece el Frame que se usará para la navegación.
    /// </summary>
    public void SetFrame(INavigationFrame frame)
    {
        _frame = frame;
        _logger.LogInfo("Frame de navegación establecido");
    }

    /// <summary>
    /// Registra una página con su clave.
    /// </summary>
    public void RegisterPage(string key, Type pageType)
    {
        _pageRegistry[key] = pageType;
        _logger.LogDebug($"Página registrada: {key} -> {pageType.Name}");
    }

    public string CurrentPage => _currentPage;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public void NavigateTo(string pageKey)
    {
        NavigateTo(pageKey, null);
    }

    public void NavigateTo(string pageKey, object? parameter)
    {
        if (_frame == null)
        {
            _logger.LogError("No se ha establecido un Frame para la navegación");
            return;
        }

        if (!_pageRegistry.TryGetValue(pageKey, out var pageType))
        {
            _logger.LogError($"Página no registrada: {pageKey}");
            return;
        }

        try
        {
            _frame.Navigate(pageType, parameter);
            _currentPage = pageKey;
            _logger.LogInfo($"Navegando a: {pageKey}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error navegando a {pageKey}: {ex.Message}", ex);
        }
    }

    public void GoBack()
    {
        if (_frame != null && _frame.CanGoBack)
        {
            _frame.GoBack();
            _logger.LogInfo("Navegación hacia atrás");
        }
    }
}