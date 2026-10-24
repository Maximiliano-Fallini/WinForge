namespace WHPO_UI.Views.Pages;

/// <summary>
/// Información de un tweak del sistema (los que viven en TweakService).
/// Lo comparten la página de Debloat y las utilidades del hub de optimizaciones.
/// </summary>
public record TweakInfo(
    string Name,
    string Description,
    string Compatibility,
    bool IsReversible,
    string Category);
