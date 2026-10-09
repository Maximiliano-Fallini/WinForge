using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WHPO_UI.Components;

namespace WHPO_UI.Views.Pages;

/// <summary>
/// Host genérico para componentes descargados (DLLs del Workshop): el
/// NavigationService solo puede navegar a tipos Page registrados, así que cada
/// componente dinámico se registra con este host, que en OnNavigatedTo recibe el
/// id del componente como parámetro de navegación y pinta su UI creada por el
/// propio componente (CreatePage).
/// </summary>
public sealed partial class ComponentHostPage : Page
{
    public ComponentHostPage()
    {
        InitializeComponent();
        // Sin caché: el contenido puede cambiar entre visitas (desinstalado/reinstalado).
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
    }

    // Id del componente a pintar. Lo setea MainWindow justo antes de navegar (variable
    // estática: Frame.Navigate(type) no pasa parámetros seguros en este caso — con un
    // parámetro explícito WinUI 3 fail-fast nativamente dentro de Frame.Navigate
    // ("unexpected parameters"), imposible de catchear en managed.
    internal static string? PendingComponentId;

    /// <summary>
    /// Id del componente que está EN PANTALLA en este momento ("" en una visita sin
    /// componente válido). Lo consulta MainWindow al desinstalar: la pestaña activa
    /// puede ser un host en el back stack o bajo el mouse, y el servicio de navegación
    /// solo conoce la página de la navegación MÁS RECIENTE (navegar a un core desde un
    /// ítem de menú no la actualiza). El host también se usa como marcador de página:
    /// el Frame no permite leer el contenido de sus entradas del back stack.
    /// </summary>
    internal static string CurrentComponentId { get; private set; } = "";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var id = e.Parameter as string ?? PendingComponentId;
        var registry = App.Services.GetRequiredService<ComponentRegistry>();
        var component = id == null ? null : registry.Find(id);

        // Registrar el id en pantalla ANTES de decidir: el redireccionamiento que
        // dispara el Workshop al desinstalar la pestaña activa consulta esta
        // propiedad (al re-navegar al Workshop pasa de nuevo por acá y se
        // actualiza sola). Cualquier camino sale con un id definido.
        CurrentComponentId = component != null ? component.Id : "";

        if (component == null)
        {
            Content = new TextBlock
            {
                Text = I18n.T("Este componente ya no está instalado."),
                Margin = new Thickness(24),
                FontSize = 14,
                Foreground = ThemeBrushes.Get("SecondaryTextBrush")
            };
            return;
        }

        try
        {
            Content = component.CreatePage(App.Services) as UIElement
                ?? Fallback(I18n.T("El componente no devolvió una interfaz válida."));
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<WHPO.Core.Services.Interfaces.ILoggingService>()
                .LogError($"ComponentHostPage ({component.Id}): error creando la UI: {ex.Message}", ex);
            Content = Fallback(I18n.T("No se pudo cargar la interfaz del componente: {0}", ex.Message));
        }
    }

    private static UIElement Fallback(string message) => new TextBlock
    {
        Text = message,
        Margin = new Thickness(24),
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeBrushes.Get("SecondaryTextBrush")
    };
}
