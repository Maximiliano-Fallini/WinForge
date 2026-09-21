using System.Runtime.CompilerServices;

// El harness de verificación del motor de curvas (tools/CurveEngineProbe) ejerce la
// planificación real del servicio (PlanCurveDuty / FanCurveState) en vez de copiarla:
// así el test sigue probando el código que corre en la app.
[assembly: InternalsVisibleTo("CurveEngineProbe")]

// Probe del control de ventiladores (tools/FanServiceProbe): arma el servicio real y
// consulta los backends por fabricante (IGCL/ADL), que son internos a propósito
// (nadie fuera del servicio debe manejar handles del driver).
[assembly: InternalsVisibleTo("FanServiceProbe")]
