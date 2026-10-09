using Microsoft.Win32;

namespace WHPO_UI;

/// <summary>
/// El VIDRIO DE LA PLATAFORMA: el <c>backdrop-filter</c> de Windows.
///
/// En WinUI 3, <c>AcrylicBrush</c> es SOLO in-app (ver "In-app acrylic" en Learn): desenfoca y tiñe el
/// contenido XAML que el elemento tiene DETRÁS, dentro de la misma ventana. El desenfoque lo resuelve
/// el compositor para cada elemento, así que el vidrio queda pegado a su panel por construcción: no
/// hay posiciones que seguir, no hay copia propia que recalcular y no puede desincronizarse al
/// scrollear — que es exactamente lo que le pasaba a la capa que la app recortaba por su cuenta.
///
/// LA TRAMPA, y por la que existe esta clase: cuando el usuario apaga "Efectos de transparencia"
/// (Configuración > Personalización > Colores) Windows reemplaza el acrílico por un color plano
/// (<c>FallbackColor</c>). En ese caso el vidrio lo tiene que aportar la capa propia de la app —la que
/// recorta parches de la foto desenfocada, ver <see cref="PanelBlurLayer"/>—, que no depende de
/// ningún ajuste del sistema. Acá se decide quién manda, con el ajuste leído del registro.
/// </summary>
internal static class PlatformGlass
{
    /// <summary>Dónde vive el ajuste de "Efectos de transparencia".</summary>
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string TransparencyValue = "EnableTransparency";

    private static bool _read;
    private static bool _available = true;

    /// <summary>
    /// ¿El acrílico in-app puede renderizar? Con los efectos de transparencia apagados NO: el sistema
    /// lo sustituye por un color plano, así que ahí el vidrio lo tiene que pintar la app.
    /// </summary>
    internal static bool Available
    {
        get
        {
            if (!_read)
            {
                _read = true;
                _available = Read();
            }

            return _available;
        }
    }

    /// <summary>
    /// Vuelve a leer el ajuste del sistema. El usuario puede apagar o encender los efectos de
    /// transparencia con la app abierta, y quién pinta el vidrio depende de eso (ver
    /// <see cref="PanelAppearance.PlatformGlassInCharge"/>).
    /// </summary>
    internal static void Recheck()
    {
        _read = false;
        _ = Available;
    }

    private static bool Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var value = key?.GetValue(TransparencyValue);
            return value is not int enabled || enabled != 0;
        }
        catch
        {
            // Sin registro legible se asume encendido: es el estado de fábrica de Windows y, además,
            // el único caso en el que el vidrio de la plataforma se ve.
            return true;
        }
    }
}
