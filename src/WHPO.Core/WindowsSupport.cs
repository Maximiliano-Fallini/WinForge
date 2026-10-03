namespace WHPO.Core;

/// <summary>
/// Versión mínima de Windows que necesita un tweak para tener efecto real.
///
/// Existe porque varios tweaks del catálogo son de Windows 11 (Widgets, "Finalizar
/// tarea" en la barra, el menú contextual clásico, Inicio y Galería del Explorador,
/// Windows AI, el diseño de Inicio de 25H2) y en Windows 10 no hacen nada: aplicarlos
/// igual mostraba un "listo" que no cambiaba nada. Peor: "Microsoft Edge - Eliminar"
/// escribe un archivo dummy en una carpeta que en Windows 10 es el Edge legado real.
///
/// La app usa esto para NO ofrecer (y no aplicar) esos tweaks fuera de su versión.
/// </summary>
public enum WindowsSupport
{
    /// <summary>Funciona en cualquier Windows soportado (10 1809+ y 11).</summary>
    Any = 0,

    /// <summary>Requiere Windows 11 (build 22000+).</summary>
    Windows11,

    /// <summary>Requiere Windows 11 24H2 (build 26100+).</summary>
    Windows11_24H2,

    /// <summary>Requiere Windows 11 25H2 (build 26200+).</summary>
    Windows11_25H2,
}

/// <summary>
/// Capacidades del Windows que está corriendo la app. Centraliza el "¿este tweak
/// aplica acá?" para que ningún tweak de Windows 11 se aplique en Windows 10.
/// </summary>
public static class WindowsCapabilities
{
    /// <summary>True en Windows 11 (build 22000+).</summary>
    public static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>True si esta versión de Windows cumple el requisito de un tweak.</summary>
    public static bool Supports(WindowsSupport support) => support switch
    {
        WindowsSupport.Any => true,
        WindowsSupport.Windows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
        WindowsSupport.Windows11_24H2 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100),
        WindowsSupport.Windows11_25H2 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26200),
        _ => true,
    };

    /// <summary>
    /// Etiqueta de compatibilidad para un tweak con requisito (clave de traducción):
    /// es la que ve el usuario en la card cuando el tweak no aplica en su Windows.
    /// </summary>
    public static string Label(WindowsSupport support) => support switch
    {
        WindowsSupport.Windows11_24H2 => "Solo Windows 11 24H2",
        WindowsSupport.Windows11_25H2 => "Solo Windows 11 25H2",
        _ => "Solo Windows 11",
    };
}
