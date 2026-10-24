using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace WHPO_UI;

/// <summary>
/// Host del separador para achicar el navbar con el mouse. Es un Grid que le pide al
/// sistema el cursor de doble flecha (SizeWestEast) en su constructor: el
/// <c>ProtectedCursor</c> de <c>UIElement</c> es protected —solo accesible desde una
/// subclase—, así que el cursor del resize vive acá y no en el code-behind de la
/// ventana. El resto de la lógica (drag, umbral de cierre, posición) está en
/// MainWindow.
/// </summary>
public sealed class NavResizeSplitter : Grid
{
    public NavResizeSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
