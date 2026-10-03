using System.Collections.Generic;
using System.Linq;
using WinForge.Component.Benchmark.Scenes;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Un NIVEL de sombras: el nombre que se lee en el menú y las dos perillas del dato que fija de un
/// golpe (si la pasada se dibuja y con qué resolución de mapa).
///
/// Por qué un nivel y no dos perillas sueltas: en el dato las sombras son dos cosas —la pasada
/// encendida o no, y el tamaño del mapa—, pero nadie elige "2048" por su nombre. Cualquier juego
/// ofrece un solo desplegable de calidad con niveles, así que el nivel es la traducción entre lo que
/// el usuario entiende (Desactivadas, Bajas, Medias…) y lo que el backend necesita. Sin este catálogo,
/// cada lugar que arme un menú tendría que inventar de nuevo la correspondencia entre las dos perillas
/// y los cinco nombres.
/// </summary>
public sealed record SceneShadowQuality(
    string LabelKey,
    bool Shadows,
    int ShadowMapSize,
    string DescriptionKey)
{
    /// <summary>
    /// Los niveles, de más liviano a más pesado, con el mismo orden y los mismos nombres con los que se
    /// los llama en un menú de videojuego. "Desactivadas" conserva el tamaño de siempre —apagada no hay
    /// mapa que mirar—, así que encender de nuevo alguna calidad es elegir un nivel y no rearmar el
    /// estado anterior.
    /// </summary>
    public static IReadOnlyList<SceneShadowQuality> All { get; } = new SceneShadowQuality[]
    {
        new("Desactivadas", Shadows: false, ShadowMapSize: SceneDefinition.ShadowMapSize,
            "Sin sombras proyectadas. La escena no cambia: se saca el costo de la pasada de profundidad y del muestreo, que es justo lo que la corrida mide."),
        new("Bajas", Shadows: true, ShadowMapSize: 512,
            "El mapa más chico (512²): el costo por frame más bajo y el borde de la sombra más blando."),
        new("Medias", Shadows: true, ShadowMapSize: 1024,
            "Sombras en 1024²: equilibrio entre definición del borde y costo por frame."),
        new("Altas", Shadows: true, ShadowMapSize: 2048,
            "Sombras en 2048²: el borde nítido de siempre, sin castigar el frame."),
        new("Ultra", Shadows: true, ShadowMapSize: 4096,
            "Sombras en 4096²: el borde más fino posible. La textura de profundidad ocupa 64 MB de memoria de video y la pasada cuesta algo más por frame.")
    };

    /// <summary>Las sombras apagadas: el piso de la lista y el nivel que se muestra al desactivarlas.</summary>
    public static SceneShadowQuality Off => All[0];

    /// <summary>
    /// El nivel del look DE SIEMPRE: sombras encendidas con el tamaño histórico
    /// (<see cref="SceneDefinition.ShadowMapSize"/>). Es el punto de partida de una escena que nunca se
    /// configuró, el mismo de las corridas guardadas antes de que existiera esta configuración.
    /// </summary>
    public static SceneShadowQuality Default { get; } =
        All.First(level => level.Shadows && level.ShadowMapSize == SceneDefinition.ShadowMapSize);

    /// <summary>
    /// El nivel que describe una configuración guardada. El tamaño llega ya NORMALIZADO por el
    /// llamador (o por acá mismo, que es el mismo validador): una configuración escrita por otro build,
    /// o a mano, tiene que caer en un nivel que exista y no en un combo vacío.
    /// </summary>
    public static SceneShadowQuality From(bool shadows, int shadowMapSize)
    {
        if (!shadows) return Off;
        int size = SceneGraphicsOptions.NormalizeShadowMapSize(shadowMapSize);
        return All.FirstOrDefault(level => level.Shadows && level.ShadowMapSize == size) ?? Off;
    }
}
