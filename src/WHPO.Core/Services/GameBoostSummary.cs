using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Qué corresponde decirle al usuario cuando termina de aplicarse el Modo juego.
///
/// El servicio entrega conteos YA VERIFICADOS, y un cero puede significar dos cosas
/// distintas que en pantalla no se leen igual:
///   · se intentó y ninguna aceptó (los "resistidos" lo dicen aparte), o
///   · no había nada que tocar en este equipo (una PC sin SysMain/DiagTrack, por ejemplo).
/// El segundo caso merece otra frase: "0 servicios, 0 procesos" se lee como un fallo.
///
/// Vive en Core y no en la página para poder verificarlo sin levantar la UI.
/// </summary>
public enum GameBoostSummaryKind
{
    /// <summary>Algo se optimizó (o algo resistió): corresponde el resumen con los conteos.</summary>
    Counts,

    /// <summary>No había ningún servicio ni proceso para tocar, y tampoco reglas de juego: nada que hacer.</summary>
    NothingToOptimize,

    /// <summary>Se aplicaron reglas de juego, pero no había servicios ni procesos para optimizar.</summary>
    RulesOnly,
}

public static class GameBoostSummary
{
    /// <summary>
    /// Clasifica el resultado del boost. No entra acá si el switch estaba apagado: en ese
    /// caso el servicio no aplica nada y no dispara resumen.
    /// </summary>
    public static GameBoostSummaryKind Classify(GameBoostApplyResult result)
    {
        // Un proceso que resistió EXISTÍA: hubo algo que optimizar, aunque su conteo sea
        // cero. Lo mismo con un servicio que siguió corriendo. Por eso los resistidos
        // cuentan como "hubo trabajo" y no como "no había nada".
        bool nothingTouched = result.ServicesOptimized == 0
                              && result.ProcessesOptimized == 0
                              && result.ProcessesKilled == 0
                              && result.ProcessesResisted == 0
                              && (result.ServicesResisted is null || result.ServicesResisted.Count == 0);

        if (!nothingTouched) return GameBoostSummaryKind.Counts;
        return result.RulesApplied > 0 ? GameBoostSummaryKind.RulesOnly : GameBoostSummaryKind.NothingToOptimize;
    }
}
