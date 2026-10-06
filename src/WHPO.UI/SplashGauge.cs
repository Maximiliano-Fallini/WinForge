using System;

namespace WHPO_UI;

/// <summary>
/// Matemática del velocímetro del splash, sin UI: un dial de 240° con el 0 %
/// abajo a la izquierda y el 100 % abajo a la derecha, pasando por arriba.
///
/// Los ángulos se miden en GRADOS, con 0 en las 12 en punto y sentido horario
/// (positivo hacia la derecha): es la misma convención que RotateTransform, así
/// que la aguja y el arco comparten una sola cuenta y no pueden desincronizarse.
///
/// Vive aparte de la ventana para poder verificarla sin abrir la app (que pide
/// UAC y no se puede lanzar desde las pruebas).
/// </summary>
internal static class SplashGauge
{
    /// <summary>Ángulo de la aguja en 0 % (abajo a la izquierda).</summary>
    public const double StartAngleDegrees = -120;

    /// <summary>Recorrido total del dial: 240° (quedan 120° abiertos abajo).</summary>
    public const double SweepDegrees = 240;

    /// <summary>
    /// Ángulo de la aguja para un porcentaje de carga. Fuera de rango se recorta
    /// (0..100): un porcentaje negativo o mayor a 100 no debe sacar la aguja del dial.
    /// </summary>
    public static double AngleForPercent(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent))
            percent = 0;
        percent = Math.Clamp(percent, 0, 100);
        return StartAngleDegrees + SweepDegrees * (percent / 100.0);
    }

    /// <summary>
    /// Punto del dial para un ángulo de la convención de arriba. Devuelve una tupla
    /// (y no Windows.Foundation.Point) para que la cuenta se pueda verificar sin
    /// proyección de WinRT de por medio.
    /// </summary>
    public static (double X, double Y) PointOnDial(double centerX, double centerY, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        return (centerX + radius * Math.Sin(radians), centerY - radius * Math.Cos(radians));
    }
}
