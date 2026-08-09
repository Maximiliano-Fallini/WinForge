namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Servicio de navegación para moverse entre páginas de la aplicación.
/// </summary>
public interface INavigationService
{
    void NavigateTo(string pageKey);
    void NavigateTo(string pageKey, object? parameter);
    bool CanGoBack { get; }
    void GoBack();
    string CurrentPage { get; }
}