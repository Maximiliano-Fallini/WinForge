namespace WHPO_UI;

/// <summary>
/// Vista previa del asistente de primera configuración para las herramientas de
/// desarrollo de Configuración (botón "Simular onboarding"). Hereda la UI del
/// OnboardingWindow real — se ve EXACTAMENTE igual — pero no aplica ningún cambio:
/// no persiste el tema, no marca el onboarding como completado y no toca el idioma
/// de la app.
/// </summary>
public sealed class OnboardingSimulatorWindow : OnboardingWindow
{
    // Estado para restaurar al cerrar la vista previa.
    private readonly string? _savedLanguage;

    public OnboardingSimulatorWindow() : base()
    {
        _savedLanguage = I18n.Current;
    }

    protected override string WindowTitleText => I18n.T("Simulador de onboarding (desarrollo)");

    // La vista previa nunca detecta ni persiste idioma al abrirse: usa el que la app ya tiene activo.
    protected override void ApplyOnboardingLanguage()
    {
    }

    /// <summary>
    /// Cambiar de idioma en la vista previa solo re-traduce el asistente: no persiste
    /// la elección (el SetLanguage real escribe en el settings de la app, y acá es una
    /// previsualización: nada de lo tocado acá sale de la ventana).
    /// </summary>
    protected override void ActivateLanguage(string code)
    {
        if (string.Equals(I18n.Current, code, StringComparison.OrdinalIgnoreCase)) return;
        I18n.Current = code;
        I18n.DispatchLanguageChanged();
    }

    // Cerrar sin escribir configuración: restaura el idioma que tenía la app.
    protected override void Finish()
    {
        if (!string.IsNullOrEmpty(_savedLanguage)
            && !string.Equals(I18n.Current, _savedLanguage, StringComparison.OrdinalIgnoreCase))
        {
            I18n.Current = _savedLanguage;
            I18n.DispatchLanguageChanged();
        }
        Close();
    }
}
