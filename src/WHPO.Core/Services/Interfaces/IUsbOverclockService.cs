namespace WHPO.Core.Services.Interfaces;

/// <summary>
/// "Overclock USB": ajusta el intervalo de sondeo (bInterval) de dispositivos USB
/// (mouses, teclados y mandos) con el mecanismo del filtro:
/// - El filtro inferior (servicio kernel + binario en %systemroot%\system32\drivers)
/// se agrega al valor LowerFilters del dispositivo (SetDevNodeProperty).
/// - El intervalo deseado se guarda como valor DWORD "bInterval" en la clave de
/// hardware del dispositivo (HKLM\SYSTEM\CurrentControlSet\Enum\...\Device Parameters).
/// El driver lo lee al cargar la pila (IoOpenDeviceRegistryKey) y parchea en vivo
/// los IRP de configuración para forzar el nuevo intervalo en los endpoints de
/// interrupción.
/// - Un intervalo de N ms equivale a 1000/N Hz: bajar bInterval sube los Hz
/// (overclocking) y subirlo los baja (downclocking, que siempre funciona).
/// - En dispositivos high-speed, bInterval no es milisegundos sino un exponente
/// (intervalo = 2^(N-1) microframes → Hz = 8000/2^(N-1)): la conversión y el
/// rango válido se resuelven según la velocidad real del dispositivo.
/// - Al re-arrancar el dispositivo (CM_Reenumerate_DevNode) la pila se re-crea y
/// el filtro entra con el nuevo intervalo.
/// El overclocking (subir Hz) puede no funcionar en dispositivos Low Speed con
/// drivers USB no Microsoft (o drivers demasiado nuevos): el downclocking siempre
/// se aplica. Igual que con el filtro, no se toca ningún otro dispositivo del bus.
/// </summary>
public interface IUsbOverclockService
{
    /// <summary>Lista los dispositivos USB candidatos con su intervalo actual y configuración del filtro.</summary>
    Task<List<UsbPollingDevice>> GetDevicesAsync();

    /// <summary>
    /// Techo REAL de overclock para un dispositivo y su motivo: combina la variante del
    /// binario instalado, Memory Integrity (HVCI), la velocidad del bus (high-speed) y el
    /// driver xHCI de Microsoft, más el techo nativo del descriptor. La UI usa esto para
    /// no ofrecer nunca hercios que el mecanismo no puede aplicar.
    /// </summary>
    Task<UsbPollingCapability> GetPollingCapabilityAsync(string deviceInstanceId);

    /// <summary>
    /// Verifica el estado REAL de un dispositivo tras aplicar un cambio: hercios
    /// efectivos (leídos del descriptor por el hub) y código de problema del devnode.
    /// </summary>
    Task<UsbPollingVerify> VerifyDeviceAsync(string deviceInstanceId);

    /// <summary>
    /// Estado de la Integridad de memoria (HVCI) y de la variante del filtro instalada. Es lo
    /// que permite advertir en la UI que, con esa protección activa, el overclock real queda
    /// limitado a la variante NoPatch (que no puede subir el techo nativo del dispositivo).
    /// </summary>
    UsbOverclockGuardStatus GetGuardStatus();

    /// <summary>
    /// Aplica el intervalo elegido a un dispositivo: activa el filtro (si no está) y
    /// escribe el bInterval deseado. rateHz = 1000/intervalMs (31, 62, 125, 250, 500, 1000…);
    /// pasá 0 para quitar el override (vuelve al intervalo nativo del dispositivo).
    /// El valor escrito se ajusta al rango válido según la velocidad del dispositivo
    /// (Low/Full: bInterval 1–255 ms; High: exponente 1–16 → 8000/2^(N-1) Hz), por lo
    /// que los Hz efectivos pueden diferir levemente del pedido (quedan en el log).
    /// Devuelve true si quedó escrito (falta el restart para que el driver lo cargue).
    /// </summary>
    Task<bool> SetRateAsync(string deviceInstanceId, int rateHz);

    /// <summary>
    /// Restaura de fábrica: saca el override de bInterval y desinstala el filtro del
    /// dispositivo (LowerFilters vuelve a su valor previo al filtro). Deja el driver
    /// instalado en el sistema: el filtro deja de aplicarse al dispositivo.
    /// </summary>
    Task<bool> RestoreDeviceAsync(string deviceInstanceId);

    /// <summary>Re-arranca el dispositivo: la pila se re-crea y el filtro toma el nuevo intervalo.</summary>
    Task<bool> RestartDeviceAsync(string deviceInstanceId);

    /// <summary>True si el servicio del filtro está instalado en el sistema (SCM).</summary>
    bool IsFilterServiceInstalled();

    /// <summary>True si el servicio del filtro existe Y puede cargar (start pending → ok).</summary>
    bool IsFilterServiceRunnable();

    /// <summary>
    /// True si el componente está listo para usarse: servicio del filtro instalado y
    /// su binario presente (el estado que la UI usa para mostrar la grilla o la
    /// pantalla de "descargar componentes").
    /// </summary>
    bool IsComponentReady();

    /// <summary>Ruta de la carpeta donde la app guarda el componente descargado (%LocalAppData%\WHPO).</summary>
    string ComponentCachePath { get; }

    /// <summary>
    /// Desinstala el componente Overclock USB y borra el caché: quita el filtro y el
    /// override de los dispositivos afectados, elimina el servicio kernel y el
    /// binario del filtro del sistema, y borra la copia descargada. Al abrir la sección de
    /// nuevo aparece la pantalla de instalación (el componente se re-descarga solo).
    /// </summary>
    void ClearComponentCache();

    /// <summary>
    /// Resuelve el binario del componente listo para instalar: primero la copia
    /// verificada en caché, luego un binario opcional junto al exe (solo si su
    /// SHA-256 coincide con el fijado) y como camino normal descarga el paquete
    /// oficial (repositorio oficial, HTTPS) validando su hash. Devuelve la ruta
    /// lista para instalar o null si falló. No instala el servicio: eso lo hace
    /// InstallFilterServiceAsync.
    /// </summary>
    Task<string?> DownloadComponentAsync();

    /// <summary>
    /// Instala el servicio del filtro: copia el binario del filtro (desde la carpeta elegida,
    /// ej. DRIVER\AMD64_AS) a %systemroot%\system32\drivers y crea el servicio
    /// kernel a demanda.
    /// </summary>
    Task<bool> InstallFilterServiceAsync(string driverSysPath);

    /// <summary>
    /// Busca el binario del componente entre las fuentes confiables (el caché/drop
    /// avanzado de la app y un binario opcional junto al exe), validando el SHA-256 de
    /// cada candidato contra el hash del paquete oficial. Sin escaneo de carpetas del
    /// usuario. Devuelve la primera coincidencia verificada, o null.
    /// </summary>
    Task<string?> FindDriverSysAsync();

    /// <summary>Intervención de depuración: refresca el estado del servicio del filtro en la próxima consulta.</summary>
    void InvalidateFilterServiceCache();

    // Nota de diseño: la UI nunca muestra el nombre interno del componente, solo
    // "componentes del sistema"; el caché del paquete vive en %LocalAppData%\WHPO\.
}

/// <summary>Tipo funcional del dispositivo (lo que la UI usa para el emoji de la grilla).</summary>
public enum UsbDeviceKind
{
    /// <summary>Mouse / puntero (mouhid).</summary>
    Mouse,
    /// <summary>Teclado (kbdhid).</summary>
    Keyboard,
    /// <summary>Endpoint de audio del producto (ej. auriculares con HID de volumen).</summary>
    Audio,
    /// <summary>Mando/volante u otro HID genérico.</summary>
    Controller,
    /// <summary>No clasificado.</summary>
    Unknown,

    /// <summary>
    /// Adaptador (radio) Bluetooth USB. NO es un periférico de entrada: es el transporte del
    /// que cuelgan los inalámbricos, pero la placa en sí es un dispositivo USB con su endpoint
    /// de interrupción, así que el filtro puede colgarse de él (el filtro de referencia lo lista
    /// con su modo /all). Lo que se ajusta acá es el sondeo de la RADIO, no el del periférico
    /// emparejado: afecta a todo lo que se conecte por ella.
    /// </summary>
    Bluetooth
}

/// <summary>Función real de un producto (mouse, teclado, audio…): la grilla muestra
/// el mejor hijo, y el tooltip de la fila lista todas las funciones del combo
/// (un dongle mouse+teclado se ve como mouse con subtítulo del producto).</summary>
public record UsbChildFunction(string Name, UsbDeviceKind Kind);

/// <summary>Capacidad REAL de overclock para un dispositivo, con el motivo cuando el techo es
/// menor. La UI usa MaxOverclockHz para capar el desplegable (nunca ofrecer hercios que el
/// dispositivo no puede dar) y Reason (null si no hay techo) para explicar por qué.</summary>
public record UsbPollingCapability(
    int MaxOverclockHz,   // techo real del BUS: 1000 en Low/Full Speed, 8000 en High Speed
    string? Reason        // motivo del techo (clave de traducción), null si es el máximo
);

/// <summary>
/// Estado del filtro frente a la seguridad del sistema (Integridad de memoria / HVCI) y la
/// variante del binario instalada. La UI lo usa para advertir POR QUÉ el techo real de Hz puede
/// ser menor al esperado y qué hacer al respecto.
///
/// Contrato del driver (README oficial de hidusbf, "Warning 2"): con la Integridad de memoria
/// activa, Windows NO carga las variantes que parchean la pila USB; solo carga NoPatch, que
/// respeta la protección pero no puede subir el techo nativo del dispositivo. Los binarios
/// están firmados por atestación de Microsoft, así que el arranque seguro y la firma de pruebas
/// no hacen falta (eso quedó atrás en 2024: antes sí se pedía desactivar el arranque seguro).
/// </summary>
public record UsbOverclockGuardStatus(
    bool HvciRunning,         // Integridad de memoria corriendo AHORA
    bool HvciConfigured,      // configurada (se aplica al reiniciar)
    string InstalledVariant,  // "" si no hay driver; si no: 1khz | 2khz-4khz | 4khz-8khz | nopatch
    int CeilingHz,            // techo de esa variante (0 si no hay driver)
    bool ServiceInstalled     // el servicio del filtro está instalado en el sistema
);

/// <summary>Resultado de la verificación post-cambio: hercios efectivos leídos del
/// descriptor (0 = desconocido) y código de problema del devnode (CM_Get_DevNode_Status;
/// null = desconocido, 0 = sin problema).</summary>
public record UsbPollingVerify(
    int EffectiveHz,
    int? DeviceProblemCode
);

/// <summary>Dispositivo USB con polling ajustable, tal como se muestra en la grilla.</summary>
public record UsbPollingDevice(
    string InstanceId,        // device instance id (HKLM\SYSTEM\Enum\...)
    string ControllerName,    // nombre visible del dispositivo USB (FriendlyName o DeviceDesc)
    string ChildName,         // nombre(s) del hijo HID funcional ("Mouse compatible con HID", "Dispositivo de teclado HID"…; "" si el nodo ya es el funcional)
    string HostControllerName,   // controladora de host USB (xHCI) donde cuelga el dispositivo ("AMD USB 3.10 eXtensible Host Controller - 1.20 (Microsoft)"…)
    string HostControllerInstance, // device instance de esa controladora (PCI\VEN_…)
    int CurrentHz,            // 1000/bInterval real del descriptor del endpoint de interrupción
    int NativeHz,             // Hz del descriptor SIN override (para mostrar la original)
    int? ActiveHz,            // Hz configurados en el registro con el filtro activo (null = sin override); interpretado según la variante del binario (los códigos 31/62 significan 2k/4k/8k en los builds de rango alto)
    bool FilterOn,            // "hidusbf" está en LowerFilters del dispositivo
    bool IsMouse,             // viene de la clase Mouse
    bool IsKeyboard,          // viene de la clase Keyboard
    bool IsHid,               // dispositivo HID genérico (mandos, volantes…)
    UsbDeviceKind Kind,       // tipo funcional del mejor hijo (prioridad mouse>teclado>audio)
    IReadOnlyList<UsbChildFunction> Children,  // TODAS las funciones reales del producto (el tooltip de la fila las lista)
    bool IsHighSpeed,         // velocidad alta: bInterval es exponente (2^(N-1) µframes), no ms
    int DescriptorBInterval,  // bInterval crudo del descriptor del endpoint (0 = desconocido)
    IReadOnlyList<string> InterfaceIds  // interfaces HID del producto (MI_xx): donde se aplica filtro/override
);
