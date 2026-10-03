using System;
using WHPO.Core.Services.Interfaces;
using WinForge.Component.Benchmark.Graphics;

namespace WinForge.Component.Benchmark;

/// <summary>
/// Configuración gráfica GUARDADA de una escena: el preset elegido y las perillas tal como quedaron,
/// para que cada escenario conserve la suya y no haya que volver a armarla en cada visita.
///
/// Vive al lado de la PÁGINA y no en Graphics/ —donde viven los presets y las perillas— porque la
/// persistencia necesita los servicios de la app (<see cref="AppBridge"/>) y Graphics/ es la carpeta
/// que el probador headless compila sin WinUI: con el store adentro, el probador dejaba de compilar.
///
/// Se guarda el preset APARTE de las perillas aunque el preset las fije: el usuario puede haber
/// arrancado de "Medio" y tocado una sola perilla, y en ese caso el id queda en "personalizado"
/// mientras los valores describen lo que realmente se corre. La corrida se arma SIEMPRE con las
/// perillas; el id es la etiqueta que resume la configuración (y la que va al informe).
///
/// Es un dato puro y serializable: nada que se pueda reconstruir entra al archivo de ajustes.
/// </summary>
internal sealed record SceneGraphicsConfig(
    string PresetId,
    bool Shadows,
    int ShadowMapSize,
    bool Environment,
    int Width,
    int Height)
{
    /// <summary>
    /// Id del preset que se debe mostrar. Es el guardado cuando todavía existe; si no existe (una
    /// configuración escrita por un build con otros presets), uno que describa EXACTAMENTE los valores
    /// guardados, y si ninguno lo hace, "Personalizado".
    ///
    /// Elegir "Personalizado" a mano se respeta aunque los valores coincidan con un preset: es una
    /// decisión del usuario, no un id roto.
    /// </summary>
    public string ResolvedPresetId()
    {
        if (SceneGraphicsPresets.Find(PresetId) != null) return PresetId;
        if (string.Equals(PresetId, SceneGraphicsPresets.CustomId, StringComparison.OrdinalIgnoreCase))
            return SceneGraphicsPresets.CustomId;
        return SceneGraphicsPresets.Match(Shadows, ShadowMapSize, Environment, Width, Height)?.Id
               ?? SceneGraphicsPresets.CustomId;
    }
}

/// <summary>
/// Guarda y recupera la configuración gráfica de CADA escena en los ajustes de la app
/// (<c>settings.json</c>, clave <c>benchmark.graphics.&lt;escena&gt;</c>).
///
/// Por qué en los ajustes de la app y no en el informe: el informe es el RESULTADO de una corrida y se
/// guarda al terminar; esto es la INTENCIÓN del usuario y tiene que sobrevivir a cerrar la app sin
/// haber corrido nada. Por qué por escena: el diorama de neón y el corredor no se configuran igual
/// —uno tiene sombras dinámicas y el otro mide vértices—, y compartir una sola configuración obligaría
/// a rehacerla al cambiar de escena.
///
/// Sin app anfitriona (el harness headless) no hay ajustes: se devuelve null y no se guarda nada, que
/// es el mismo comportamiento de un primer arranque.
/// </summary>
internal static class SceneGraphicsStore
{
    private const string KeyPrefix = "benchmark.graphics.";

    /// <summary>Clave de los ajustes donde vive la configuración de una escena.</summary>
    public static string KeyFor(string sceneId) => KeyPrefix + sceneId;

    private static ISettingsService? Settings => AppBridge.GetService<ISettingsService>();

    /// <summary>Configuración guardada de la escena (null si nunca se configuró o no hay app).</summary>
    public static SceneGraphicsConfig? Load(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId)) return null;
        try { return Settings?.Get<SceneGraphicsConfig>(KeyFor(sceneId)); }
        catch { return null; }
    }

    /// <summary>
    /// Guarda la configuración de la escena. Se persiste en el acto (Save) y no al cerrar: la app se
    /// puede cerrar con la X o desde la bandeja sin pasar por ningún cierre ordenado, y una
    /// configuración que solo vive en memoria se perdería.
    /// </summary>
    public static void Save(string sceneId, SceneGraphicsConfig config)
    {
        if (string.IsNullOrWhiteSpace(sceneId)) return;
        try
        {
            var settings = Settings;
            if (settings == null) return;
            settings.Set(KeyFor(sceneId), config);
            settings.Save();
        }
        catch { /* la configuración es una comodidad: si no se puede guardar, la corrida sigue igual */ }
    }
}
