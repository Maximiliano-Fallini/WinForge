using System;
using System.Collections.Generic;
using System.Threading;

namespace WHPO_UI.Controls;

/// <summary>
/// Modelo de hardware de la consola virtual de la biblioteca: el relieve de la tapa y
/// el grosor del cuerpo que comparten la malla (<see cref="CartridgeModel.BuildMesh"/>),
/// el pintado de caras y la vista previa 3D.
///
/// Es una preferencia de apariencia (ajuste <c>biblioteca.console.model</c>) y se elige
/// en Configuración → Apariencia, solo con las cards 3D activas. Los valores de fábrica
/// son dos piezas distintas: la consola clásica (un perfil de losa) y el gabinete
/// arcade. Cada diseño entra acá con sus medidas y el resto del código no cambia.
///
/// Igual que <see cref="CardSkin"/>, el pintado corre en segundo plano: se lee
/// <see cref="Current"/> (una foto inmutable) y no se tocan recursos de XAML.
/// </summary>
public static class ConsoleModel
{
    /// <summary>Ajuste persistido con el id del modelo.</summary>
    public const string SettingKey = "biblioteca.console.model";

    public const string DefaultId = "classic";

    /// <summary>
    /// Un modelo de consola. <see cref="LidRadius"/> es el radio de la tapa y
    /// <see cref="LidRaise"/> su relieve, en las mismas unidades que la malla
    /// (ancho de cara = 1). <see cref="Thickness"/> es la mitad del grosor.
    /// </summary>
    public sealed record Option(
        string Id, string Label, string Hint,
        float LidRadius, float LidRaise, float Thickness,
        DeviceKind Kind = DeviceKind.Console);

    /// <summary>Opciones del selector, en el orden en que se muestran.</summary>
    public static IReadOnlyList<Option> Options { get; } = new[]
    {
        new Option("classic", "Clásica", "Perfil bajo y tapa grande, como la PS1.", 162f, 0.013f, 0.055f),
        // Consola retro: otro mueble, no un perfil de la misma losa.
        new Option("retro", "Retro", "Gabinete arcade con joystick, botones y monedas.",
            162f, 0.013f, 0.055f, DeviceKind.Arcade)
    };

    private static Option _current = Options[0];

    /// <summary>Modelo vigente (inmutable y seguro de leer desde cualquier hilo).</summary>
    public static Option Current => Volatile.Read(ref _current);

    public static string CurrentId => Current.Id;

    /// <summary>Forma del equipo con la que se representan los emuladores de consola.</summary>
    public static DeviceKind CurrentKind => Current.Kind;

    /// <summary>Fija el modelo vigente en memoria (no persiste). Ids desconocidos → clásica.</summary>
    public static void Set(string? id)
    {
        foreach (var option in Options)
        {
            if (!string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            Volatile.Write(ref _current, option);
            return;
        }
        Volatile.Write(ref _current, Options[0]);
    }

    // Persistencia: la hace la UI (Configuración guarda el ajuste; la biblioteca lo
    // relee al abrirse). Este registro no toca settings ni XAML a propósito, así lo
    // puede compilar el arnés de pintado sin arrastrar la app entera.
}
