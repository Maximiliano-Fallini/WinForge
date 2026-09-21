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
    public OnboardingSimulatorWindow() : base()
    {
    }

    protected override string WindowTitleText => I18n.T("Simulador de onboarding (desarrollo)");

    // La vista previa nunca detecta ni persiste idioma: usa el que la app ya tiene activo.
    protected override void ApplyOnboardingLanguage()
    {
    }

    // Cerrar sin escribir configuración (el real persiste tema + onboarding.complete).
    protected override void Finish()
    {
        Close();
    }
}
