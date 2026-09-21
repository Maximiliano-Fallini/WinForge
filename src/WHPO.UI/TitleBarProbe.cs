// SONDA TEMPORAL: verifica si el SDK trae el control TitleBar (y la API para
// ocultar los botones del sistema). Se borra apenas se confirma.
using Microsoft.UI.Windowing;

namespace WHPO_UI;

internal static class TitleBarProbe
{
    public static void Probe()
    {
        var t = typeof(Microsoft.UI.Xaml.Controls.TitleBar);
        var p = typeof(OverlappedPresenter);
        System.Console.WriteLine(t.FullName);
        System.Console.WriteLine(p.FullName);
    }
}
