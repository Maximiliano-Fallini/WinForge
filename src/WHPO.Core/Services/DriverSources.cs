namespace WHPO.Core.Services;

/// <summary>
/// De dónde salen los binarios de los drivers. Los servicios descargan de acá y la pestaña
/// Drivers de Configuración muestra estos mismos enlaces: el usuario ve exactamente el
/// repositorio oficial del que se baja, no una copia escrita a mano en el XAML que se puede
/// quedar vieja.
///
/// PawnIO va pinneado a una release concreta (y a su SHA-256, ver PawnIoService) porque el
/// permalink "latest" cambiaría de hash sin que la app lo sepa. hidusbf se toma del paquete
/// del repositorio y cada variante se verifica antes de instalar.
/// </summary>
public static class DriverSources
{
    /// <summary>Repositorio oficial de PawnIO (releases del instalador).</summary>
    public const string PawnIoRepository = "https://github.com/namazso/PawnIO.Setup";

    /// <summary>Instalador pinneado de PawnIO 2.2.0.</summary>
    public const string PawnIoDownload =
        "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";

    /// <summary>Repositorio oficial de hidusbf.</summary>
    public const string HidUsbfRepository = "https://github.com/LordOfMice/hidusbf";

    /// <summary>Paquete de hidusbf del que se extraen las variantes del filtro.</summary>
    public const string HidUsbfDownload =
        "https://raw.githubusercontent.com/LordOfMice/hidusbf/master/hidusbf.zip";
}
