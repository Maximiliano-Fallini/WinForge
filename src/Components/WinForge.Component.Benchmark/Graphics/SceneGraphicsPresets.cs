using System;
using System.Collections.Generic;
using System.Linq;

namespace WinForge.Component.Benchmark.Graphics;

/// <summary>
/// Un preset GENERAL de gráficos: las perillas que fija de un golpe (sombras, resolución del mapa de
/// sombras, entorno y resolución de la ventana), válido para CUALQUIER escena del catálogo.
///
/// Vive en Graphics/ y no dentro de la página por el mismo motivo por el que
/// <see cref="SceneGraphicsOptions"/> vive en el pedido de la corrida: un preset no es de una escena
/// ni de una pantalla —es una forma de configurar el dibujo—, así que se puede guardar, comparar y
/// reusar sin pasar por la UI. La página lo único que hace es aplicarlo sobre sus combos.
/// </summary>
public sealed record SceneGraphicsPreset(
    string Id,
    string LabelKey,
    string DescriptionKey,
    bool Shadows,
    int ShadowMapSize,
    bool Environment,
    int Width,
    int Height)
{
    /// <summary>Las perillas de dibujo del preset, ya en la forma que reciben los cuatro backends.</summary>
    public SceneGraphicsOptions Options => new()
    {
        Shadows = Shadows,
        ShadowMapSize = ShadowMapSize,
        Environment = Environment
    };

    /// <summary>
    /// Tamaño de VENTANA pedido: 0 = automática (la que entra en el monitor) y -1 = nativa (el área
    /// útil de la pantalla). Son los mismos valores que acepta el host: en pantalla completa manda el
    /// monitor y esto se ignora.
    /// </summary>
    public (int Width, int Height) Resolution => (Width, Height);

    /// <summary>
    /// True si la configuración elegida es EXACTAMENTE la del preset (perillas + resolución pedida).
    /// Se compara contra los valores CRUDOS del combo (0 y -1 incluidos) y no contra la resolución ya
    /// resuelta: "nativa" en un monitor es 1440p y en otro 4K, y el preset tiene que seguir siendo el
    /// mismo en los dos.
    ///
    /// El tamaño del mapa de sombras se compara ya NORMALIZADO: el que normaliza es <see cref="Match"/>,
    /// que es el único camino por el que se llega hasta acá.
    /// </summary>
    public bool Matches(bool shadows, int shadowMapSize, bool environment, int width, int height)
        => Shadows == shadows
           && Options.ShadowMapSize == shadowMapSize
           && Environment == environment
           && Width == width
           && Height == height;
}

/// <summary>
/// El catálogo de presets generales, de más barato a más caro. "Alto" es el LOOK DE SIEMPRE —el que la
/// escena tenía antes de que existiera esta configuración—, así que volver a él devuelve los números a
/// los de las corridas viejas, que es lo que permite comparar contra un informe anterior.
///
/// La sincronización vertical NO es parte de un preset: no es una perilla de calidad, es cómo se
/// entrega el frame.
/// </summary>
public static class SceneGraphicsPresets
{
    /// <summary>Id del preset que NO fija nada: el usuario tocó una perilla suelta (ver la página).</summary>
    public const string CustomId = "personalizado";

    /// <summary>Etiqueta del preset personalizado (texto fuente: lo traduce el motor i18n).</summary>
    public const string CustomLabelKey = "Personalizado";

    /// <summary>
    /// Descripción del preset personalizado: la misma forma que la de los presets, para que la línea de
    /// abajo del desplegable diga algo también cuando no hay ningún nivel elegido.
    /// </summary>
    public const string CustomDescriptionKey =
        "Deja cada ajuste por separado: abajo se eligen las sombras, la iluminación global y la resolución una por una.";

    /// <summary>
    /// Presets disponibles, en orden de costo creciente. El rótulo es el NOMBRE del nivel —"Bajo",
    /// "Ultra"—, como en el menú de cualquier juego: lo que fija cada uno vive en la línea de
    /// descripción que la página muestra debajo del desplegable, porque un desplegable cerrado solo
    /// deja leer el nombre y los números que lo explican no entran ahí.
    /// </summary>
    public static IReadOnlyList<SceneGraphicsPreset> All { get; } = new SceneGraphicsPreset[]
    {
        new("bajo", "Bajo",
            "Lo más liviano: sombras desactivadas, sin iluminación global y con la ventana en 1280 × 720.",
            Shadows: false, ShadowMapSize: 2048, Environment: false, Width: 1280, Height: 720),
        new("medio", "Medio",
            "Sombras medias e iluminación global, con la ventana en 1600 × 900.",
            Shadows: true, ShadowMapSize: 1024, Environment: true, Width: 1600, Height: 900),
        new("alto", "Alto",
            "Sombras altas e iluminación global, con la ventana en automático. Es la calidad de siempre y la recomendada.",
            Shadows: true, ShadowMapSize: 2048, Environment: true, Width: 0, Height: 0),
        new("maxima", "Ultra",
            "Sombras ultra e iluminación global, con la ventana al tamaño del monitor. Lo más pesado de la lista.",
            Shadows: true, ShadowMapSize: 4096, Environment: true, Width: -1, Height: -1)
    };

    /// <summary>
    /// El preset con el que arranca una escena sin configuración guardada: el look de siempre. No se
    /// elige "el primero" ni "el más caro" a propósito — el default del componente tiene que seguir
    /// siendo el mismo look que ya describen los informes guardados.
    /// </summary>
    public static SceneGraphicsPreset Default { get; } = All.First(p => p.Id == "alto");

    /// <summary>Busca un preset por id (null si no existe: configuración guardada por otro build).</summary>
    public static SceneGraphicsPreset? Find(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Preset cuyos valores coinciden con la configuración dada (null = no es ninguno).</summary>
    public static SceneGraphicsPreset? Match(bool shadows, int shadowMapSize, bool environment, int width, int height)
        => All.FirstOrDefault(p => p.Matches(shadows, SceneGraphicsOptions.NormalizeShadowMapSize(shadowMapSize), environment, width, height));
}
