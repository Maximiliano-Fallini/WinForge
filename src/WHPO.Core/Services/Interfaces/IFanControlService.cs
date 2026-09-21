using System.Collections.Generic;
using System.Threading.Tasks;

namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// Estado de un ventilador controlable (de un chip SuperIO de la placa madre).
/// </summary>
/// <param name="Id">Identificador estable del sensor de fan (para setear duty/auto).</param>
/// <param name="ChipName">Nombre del chip SuperIO (ej. "NCT6798D", "IT8613E").</param>
/// <param name="Name">Nombre del ventilador (ej. "Fan #1", "CPU Fan").</param>
/// <param name="Rpm">Revoluciones por minuto actuales (null si no hay señal).</param>
/// <param name="HasControl">True si el canal tiene control de PWM por software.</param>
/// <param name="IsManual">True si el control está en modo software (manual).</param>
/// <param name="DutyPercent">Duty actual 0-100 en modo manual (null si automático).</param>
/// <param name="IsGpu">True si el canal pertenece a una GPU (y no al SuperIO de la placa).</param>
/// <param name="Temperature">Temperatura de referencia del canal (°C): paquete CPU
/// para headers de placa, núcleo GPU para canales de video. Null si no hay lectura.</param>
/// <param name="IsCurveActive">True si el canal está siendo gobernado por una curva.</param>
/// <param name="RawName">Nombre genérico de LHM (ej. "Fan #2"), sin nombre personalizado.</param>
/// <param name="HasCustomName">True si el canal tiene un nombre personalizado guardado.</param>
public record FanControlInfo(
    string Id,
    string ChipName,
    string Name,
    double? Rpm,
    bool HasControl,
    bool IsManual,
    double? DutyPercent,
    bool IsGpu = false,
    double? Temperature = null,
    bool IsCurveActive = false,
    string RawName = "",
    bool HasCustomName = false);

/// <summary>
/// Punto de una curva ventilador: a <c>TemperatureC</c> °C el canal debe ir a
/// <c>DutyPercent</c> % de duty. La curva se interpola linealmente entre puntos.
/// </summary>
public record FanCurvePoint(double TemperatureC, double DutyPercent);

/// <summary>
/// Dinámica de aplicación de una curva: los filtros que hay entre la temperatura
/// medida y el duty que se escribe en el canal. Todo en 0 = sin filtros (se aplica
/// el duty pedido en cada actualización del motor, cada 2 segundos).
/// </summary>
/// <param name="HysteresisC">Banda de temperatura (°C) que un cambio tiene que
/// superar respecto de la temperatura en la que se aplicó el duty actual. 0 la
/// desactiva. En los extremos de la curva se ignora.</param>
/// <param name="ResponseSeconds">Segundos que un cambio tiene que sostenerse antes
/// de aplicarse. 0 lo desactiva. En los extremos de la curva se ignora.</param>
/// <param name="StepUpPercent">% máximo que puede subir el duty por actualización.
/// 0 = sin límite.</param>
/// <param name="StepDownPercent">% máximo que puede bajar el duty por actualización.
/// 0 = sin límite.</param>
public record FanCurveDynamics(
    double HysteresisC,
    double ResponseSeconds,
    double StepUpPercent,
    double StepDownPercent);

/// <summary>
/// Resultado de calibrar un canal: se barre el duty de 0 a 100% y de vuelta
/// midiendo qué reporta el chip en cada paso. Todo lo de acá salió de
/// <em>provocar</em> el ventilador y observar — no de leer una tabla del firmware.
/// También es la respuesta a "¿este canal tiene tacómetro?": si ningún sensor de
/// RPM se movió durante el barrido, no lo tiene (o no está conectado).
/// </summary>
public record FanCalibration
{
    /// <summary>True si algún sensor de RPM reaccionó al barrido.</summary>
    public bool TachResponds { get; init; }

    /// <summary>Sensor de RPM que reaccionó (null si ninguno).</summary>
    public string? TachSensorName { get; init; }

    /// <summary>Primer % (subiendo desde 0) en el que el ventilador dio vueltas.</summary>
    public double? StartPercent { get; init; }

    /// <summary>% bajando en el que las vueltas volvieron a cero (null si nunca paró).</summary>
    public double? StopPercent { get; init; }

    /// <summary>RPM mínimo medido con el ventilador girando.</summary>
    public double? MinRpm { get; init; }

    /// <summary>RPM máximo medido.</summary>
    public double? MaxRpm { get; init; }

    /// <summary>True si el duty leído acompañó al pedido (registro vivo, no estático).</summary>
    public bool DutyReadbackLive { get; init; }

    /// <summary>Relación medida %→RPM (vacía si no hubo tacómetro que respondiera).</summary>
    public List<FanCurvePoint> PercentToRpm { get; init; } = new();

    /// <summary>Cuándo se midió.</summary>
    public DateTime CompletedUtc { get; init; }
}

/// <summary>
/// Estado general del subsistema de control de ventiladores.
/// </summary>
/// <param name="PawnIOInstalled">True si el driver PawnIO está instalado en el sistema.</param>
/// <param name="PawnIOVersion">Versión instalada de PawnIO (null si no está).</param>
/// <param name="HardwareAccessible">True si se pudo abrir el hardware y hay ventiladores visibles.</param>
/// <param name="ChipName">Chip SuperIO detectado (null si no hay).</param>
/// <param name="FanCount">Cantidad de ventiladores detectados.</param>
public record FanControlStatus(
    bool PawnIOInstalled,
    string? PawnIOVersion,
    bool HardwareAccessible,
    string? ChipName,
    int FanCount);

/// <summary>
/// Resultado de la instalación silenciosa de PawnIO.
/// </summary>
/// <param name="Success">True si quedó instalado (o ya estaba).</param>
/// <param name="Message">Mensaje de resultado (para feedback/log, en español).</param>
public record PawnIOInstallResult(bool Success, string Message);

/// <summary>
/// Control de ventiladores del sistema sobre LibreHardwareMonitor: los accesos
/// al chip SuperIO (ITE/Nuvoton) van por el driver de kernel PawnIO (el mismo
/// que usa LibreHardwareMonitor 0.9.6 en lugar de WinRing0).
/// El servicio mantiene su propia instancia de Computer (solo placa madre) y
/// serializa el acceso a registros con un lock interno.
/// </summary>
public interface IFanControlService
{
    /// <summary>Estado del driver PawnIO y del hardware.</summary>
    FanControlStatus GetStatus();

    /// <summary>
    /// Lista de ventiladores detectados con su estado de control actual.
    /// Actualiza lecturas (RPM) en cada llamada.
    /// </summary>
    List<FanControlInfo> GetFans();

    /// <summary>
    /// Fija el duty manual del ventilador (0-100). Pone el canal en modo
    /// software (manual) si no lo estaba. False si no se pudo escribir.
    /// </summary>
    bool SetManualDuty(string fanId, float percent);

    /// <summary>
    /// Devuelve el ventilador al control automático del BIOS (restaura el modo
    /// y el duty originales del canal). False si no se pudo.
    /// </summary>
    bool SetAuto(string fanId);

    /// <summary>Devuelve TODOS los canales tocados al control del BIOS.</summary>
    void RestoreAll();

    /// <summary>
    /// Reaplica las curvas que quedaron activas cuando se cerró la app (el apagado
    /// devuelve los canales al BIOS a propósito, así que el recuerdo vive en un flag
    /// aparte que sí sobrevive). Se llama una vez al arrancar y devuelve cuántas
    /// curvas se pudieron reaplicar. Suelta la curva explícitamente (Automático, o
    /// apagar su switch) hace que no se reaplique.
    /// </summary>
    int ReapplyCurvesOnStartup();

    /// <summary>
    /// True si hay curvas para reaplicar al arrancar (flag autoStart + puntos
    /// válidos en settings). Solo lee settings.json: NO abre el hardware
    /// (LHM/Computer), para que el arranque no toque el bus ISA cuando no hay
    /// nada que reaplicar. Puerta de entrada barata antes de ReapplyCurvesOnStartup.
    /// </summary>
    bool HasCurvesToReapply();

    /// <summary>
    /// Reanuda el control tras volver de una suspensión: reabre el hardware y
    /// re-aplica las curvas activas sin esperar al próximo tick.
    /// </summary>
    void ResumeFromSleep();

    /// <summary>
    /// Guarda (o borra con null/vacío) un nombre personalizado para el canal,
    /// persistente entre sesiones. La UI lo muestra en vez de "Fan #N".
    /// </summary>
    void SetCustomName(string fanId, string? name);

    /// <summary>Nombre personalizado del canal, o vacío si no tiene.</summary>
    string GetCustomName(string fanId);

    /// <summary>
    /// Curva guardada del canal (pares temperatura→duty ordenados por
    /// temperatura). Vacía si nunca se configuró.
    /// </summary>
    List<FanCurvePoint> GetFanCurve(string fanId);

    /// <summary>
    /// Guarda la curva del canal y la activa/desactiva. Al activar, el canal
    /// pasa a modo software y un motor interno aplica el duty interpolado cada
    /// 2 segundos según la temperatura de referencia (CPU para la placa, GPU
    /// para canales de video). Al desactivar, el canal vuelve al BIOS.
    /// </summary>
    void SetFanCurve(string fanId, List<FanCurvePoint> points, bool enabled);

    /// <summary>True si la curva del canal está activa (gobernando el duty).</summary>
    bool IsFanCurveEnabled(string fanId);

    /// <summary>
    /// Dinámica de aplicación de la curva del canal (histéresis, tiempo de
    /// respuesta y límites de subida/bajada). Si nunca se configuró, devuelve los
    /// valores por defecto.
    /// </summary>
    /// <summary>Perfil de puntos elegido en el desplegable ("4", "5", "6", "q4",
    /// "q5", "q6"), recordado por canal. Vacío si nunca se eligió.</summary>
    string GetCurveProfile(string fanId);

    /// <summary>Recuerda el perfil de puntos elegido para el canal y lo persiste.</summary>
    void SetCurveProfile(string fanId, string profileKey);

    FanCurveDynamics GetCurveDynamics(string fanId);

    /// <summary>
    /// Guarda la dinámica de aplicación de la curva del canal. Si la curva está
    /// activa, los nuevos valores rigen desde la próxima actualización del motor.
    /// </summary>
    void SetCurveDynamics(string fanId, FanCurveDynamics dynamics);

    /// <summary>True si el fail-safe tuvo que soltar este canal al BIOS por perder
    /// la lectura del sensor de temperatura (la curva quedó desactivada).</summary>
    bool WasSensorLost(string fanId);

    /// <summary>Limpia el aviso de sensor perdido del canal.</summary>
    void ClearSensorLost(string fanId);

    /// <summary>
    /// Calibra el canal: barre el duty de 0 a 100% y de vuelta, midiendo en cada
    /// paso qué RPM reporta el chip. Devuelve el punto de arranque, el de parada,
    /// el RPM mínimo y máximo, la relación medida %→RPM y si algún tacómetro
    /// reaccionó. Al terminar, el canal vuelve exactamente a como estaba (curva
    /// propia o control del BIOS). Tarda alrededor de un minuto.
    /// </summary>
    /// <param name="progress">Recibe el % que se está aplicando en cada paso.</param>
    Task<FanCalibration> CalibrateAsync(string fanId, IProgress<int>? progress, CancellationToken cancellationToken);

    /// <summary>Calibración guardada del canal, o null si nunca se calibró.</summary>
    FanCalibration? GetCalibration(string fanId);

    /// <summary>Borra la calibración guardada del canal.</summary>
    void ResetCalibration(string fanId);

    /// <summary>
    /// Cierra y vuelve a abrir el hardware. Se usa después de instalar PawnIO
    /// para levantar los ventiladores sin reiniciar la app.
    /// </summary>
    void Reinitialize();

    /// <summary>
    /// Descarga el instalador oficial de PawnIO y lo ejecuta silencioso
    /// (/S). Requiere que la app corra como administrador. Devuelve true si
    /// al terminar el driver quedó instalado.
    /// </summary>
    Task<PawnIOInstallResult> InstallPawnIOSilentAsync();
}
