using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación de "Overclock USB" con un filtro de polling a nivel kernel:
/// 1) Servicio kernel del filtro instalado a demanda (INF: StartType=3).
/// 2) El filtro inferior del dispositivo se agrega con CM_Set_DevNode_Registry_PropertyW
/// sobre la propiedad CM_DRP_LOWERFILTERS.
/// 3) El intervalo deseado se escribe como valor DWORD "bInterval" en Device Parameters
/// del dispositivo (HKLM\SYSTEM\CurrentControlSet\Enum\...\Device Parameters): el
/// driver lo lee vía IoOpenDeviceRegistryKey y fuerza ese bInterval en los endpoints
/// de interrupción al cargar la pila (overclock y downclock, solo ese dispositivo).
/// 4) El "Restart" re-arranca el devnode con CM_Reenumerate_DevNode (sincrónico): la
/// pila se re-crea y el filtro entra con el nuevo intervalo.
/// El bInterval REAL del endpoint se lee del hub padre con IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX.
/// Las estructuras USB del paquete user-mode van empaquetadas a 1 byte (pshpack1.h) y cada USB_PIPE_INFO mide 11.
/// El binario del filtro NO se redistribuye dentro de la app: el paquete oficial no
/// declara ninguna licencia de redistribución. Se descarga desde el repositorio oficial
/// y se valida por SHA-256 antes de instalar; un binario con el mismo hash colocado
/// junto al exe (Assets\Drivers) se acepta sin red, lo que permite sumar el bundling
/// en el futuro si el autor da permiso explícito.
/// </summary>
public class UsbOverclockService : IUsbOverclockService
{
    private readonly ILoggingService _logging;

 // ===== cfgmgr32 =====
    private const uint CM_DRP_DEVICEDESC = 0x00000001;
    private const uint CM_DRP_SERVICE = 0x00000005;
    private const uint CM_DRP_CLASS = 0x00000009;
    private const uint CM_DRP_FRIENDLYNAME = 0x0000000D;
    private const uint CM_DRP_UPPERFILTERS = 0x00000012;
    private const uint CM_DRP_LOWERFILTERS = 0x00000013;
    private const uint CR_SUCCESS = 0x00000000;
    private const uint CR_BUFFER_SMALL = 0x0000001A;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001;
    private const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x00000000;
    private const uint CM_REENUMERATE_NORMAL = 0x00000000;
    private const uint CM_REENUMERATE_SYNCHRONOUS = 0x00000001;

 // ===== usbioctl (user-mode, pack(1)) =====
    private const uint FILE_DEVICE_USB = 0x00000022;
    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;

    private const uint USB_GET_NODE_INFORMATION = 258;
    private const uint USB_GET_NODE_CONNECTION_INFORMATION_EX = 274;
    private const uint USB_GET_NODE_CONNECTION_DRIVERKEY_NAME = 264;

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    private static readonly uint IOCTL_USB_GET_NODE_INFORMATION =
        CtlCode(FILE_DEVICE_USB, USB_GET_NODE_INFORMATION, METHOD_BUFFERED, FILE_ANY_ACCESS);
    private static readonly uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX =
        CtlCode(FILE_DEVICE_USB, USB_GET_NODE_CONNECTION_INFORMATION_EX, METHOD_BUFFERED, FILE_ANY_ACCESS);
    private static readonly uint IOCTL_USB_GET_NODE_CONNECTION_DRIVERKEY_NAME =
        CtlCode(FILE_DEVICE_USB, USB_GET_NODE_CONNECTION_DRIVERKEY_NAME, METHOD_BUFFERED, FILE_ANY_ACCESS);

 // USB_DEVICE_DESCRIPTOR = 18; USB_ENDPOINT_DESCRIPTOR = 7; USB_PIPE_INFO = 7+4 = 11.
    private const int UsbDeviceDescriptorSize = 18;
    private const int UsbEndpointDescriptorSize = 7;
    private const int UsbPipeInfoSize = UsbEndpointDescriptorSize + 4;
    private const int MaxPipes = 32;

 // USB_NODE_CONNECTION_INFORMATION_EX (pack(1)):
 // 0: ULONG ConnectionIndex
 // 4: USB_DEVICE_DESCRIPTOR (18)
 // 22: UCHAR CurrentConfigurationValue
 // 23: UCHAR Speed
 // 24: BOOLEAN DeviceIsHub
 // 25: USHORT DeviceAddress
 // 27: ULONG NumberOfOpenPipes
 // 31: USB_PIPE_INFO PipeList[]
    private const int ConnInfoHeaderSize = 31;
    private const int ConnInfoBufferSize = ConnInfoHeaderSize + MaxPipes * UsbPipeInfoSize;

 // USB_NODE_INFORMATION (pack(1)): USB_HUB_NODE NodeType(4) + USB_HUB_INFORMATION;
 // USB_HUB_DESCRIPTOR: bDescriptorLength@4, bDescriptorType@5, bNumberOfPorts@6 …
 // → NumberOfPorts queda en offset 6 del buffer. El driver devuelve 76 bytes;
 // el buffer debe ser >= 76 o el IOCTL falla (antes 4+64=68 → siempre 0 puertos).
    private const int HubNodeInfoBufferSize = 128;
    private const int HubNumberOfPortsOffset = 6;

 // USB_NODE_CONNECTION_DRIVERKEY_NAME (pack(1)): ULONG ConnectionIndex; ULONG ActualLength; WCHAR NodeName[1].
    private const int DriverKeyNameBufferSize = 4 + 4 + 512;

 // GUID_DEVINTERFACE_USB_HUB {f18a0e88-c30c-11d0-8815-00a0c906bed8}
    private static readonly Guid GuidDevinterfaceUsbHub = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

 // ===== Distribución del binario del filtro =====
 // El binario del filtro NO viene con la app: el paquete oficial no
 // declara licencia, así que redistribuir el binario requiere permiso del autor.
 // El flujo es: copia verificada en caché (%LocalAppData%\WHPO) → descarga
 // del paquete oficial con el SHA-256 fijado acá. Un binario junto al exe
 // (Assets\Drivers) se acepta solo si su hash coincide: es la reserva para sumar
 // el bundling sin cambios de código si el autor autoriza la redistribución.
 //
 // IMPORTANTE: el techo de hercios NO lo decide el valor de registro, lo decide
 // el BINARIO instalado. El paquete oficial trae un build por rango (AMD64_AS):
 //   1khz       → fuerza hasta 1000 Hz   (31 = 31 Hz, 62 = 62 Hz)
 //   2khz-4khz  → 31 = 2000 Hz, 62 = 4000 Hz
 //   4khz-8khz  → 31 = 4000 Hz, 62 = 8000 Hz
 //   NoPatch    → sin parcheo de la pila USB: respeta Memory Integrity (HVCI),
 //                pero NO puede subir el techo nativo del bus (solo fuerzar
 //                intervalos que el dispositivo ya declara).
 // (README.2kHz-8kHz.ENG.TXT del paquete; requisitos 2k+: USB 3.x + driver de
 // Microsoft + Windows 8+). El desplegable de la UI nunca ofrece más de lo que
 // la variante instalada puede dar (ver UsbPollingCapability).
    private const string KnownDriverSha256 = "81F649B34978FE9F74CE5C7C04BA24D5238FAEC6C70018F14DA9423A46E6E04D";

 /// <summary>SHA-256 de cada variante oficial (carpeta AMD64_AS del paquete), medido sobre el zip de master.</summary>
    private static readonly Dictionary<string, string> VariantSha256 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1khz"] = "81F649B34978FE9F74CE5C7C04BA24D5238FAEC6C70018F14DA9423A46E6E04D",
        ["2khz-4khz"] = "E2C9FC626BB92D2219FBEF3458014C198A3C90C563F948C9A433826E64D77E90",
        ["4khz-8khz"] = "DB73A8C259E16A0D02F138650497C1BDEC81ADD66D928F3CF3FF39FAD4EB421B",
        ["nopatch"] = "2F82CDEB36BDAA42EA1933A9B11F3B8E1BDB28E6D3E3DA7E65B4631B3375412D",
    };

 /// <summary>Techo de overclock que da cada variante del driver (Hz).</summary>
    private const int MaxHz1khzVariant = 1000;
    private const int MaxHz2kVariant = 4000;
    private const int MaxHz8kVariant = 8000;

 /// <summary>
 /// Codificación de tasa por build (contrato del autor): los builds de rango alto
 /// reutilizan los códigos 31/62 con significados propios. El resto de códigos que
 /// el filtro acepta siguen la fórmula base (31 Hz en 1khz; en los de rango alto
 /// los códigos intermedios 125..1000 NO están soportados por esos builds).
 /// rateCode → Hz.
 /// </summary>
    private static Dictionary<int, int> RateMapFor(string variant)
    {
        var map = new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase)
        {
            ["1khz"] = new Dictionary<int, int> { [31] = 31, [62] = 62, [125] = 125, [250] = 250, [500] = 500, [1000] = 1000 },
            ["2khz-4khz"] = new Dictionary<int, int> { [31] = 2000, [62] = 4000 },
            ["4khz-8khz"] = new Dictionary<int, int> { [31] = 4000, [62] = 8000 },
            // NoPatch no puede forzar más de lo que el dispositivo declara: no se usa
            // para overclock, solo como variante segura con HVCI (códigos base).
            ["nopatch"] = new Dictionary<int, int> { [31] = 31, [62] = 62, [125] = 125, [250] = 250, [500] = 500, [1000] = 1000 },
        };
 // Variante no identificada: se interpreta con la codificación base (1khz), que es la que la
 // app escribe por defecto. El aviso de seguridad NO muestra variante en ese caso.
        return map.TryGetValue(variant, out var m) ? m : map["1khz"];
    }

 /// <summary>Binario opcional junto al exe (solo se acepta si su SHA-256 coincide).</summary>
    private static string BundledDriverSysPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "Drivers", "hidusbf.sys");

 // Velocidad del dispositivo según USB_NODE_CONNECTION_INFORMATION_EX.Speed
 // (USB_DEVICE_SPEED: 0=Full, 1=Low, 2=High, 3=Super).
    private const byte UsbSpeedHigh = 2;
        // USB_DEVICE_SPEED: 1 = Low Speed (1,5 Mbps). Los topes prácticos de cada clase,
        // según la tabla del autor: LS 125 Hz, FS 1000 Hz, HS 8000 Hz.
    private const byte UsbSpeedLow = 1;

    public UsbOverclockService(ILoggingService logging, ISystemInfoService systemInfo)
    {
        _logging = logging;
        _systemInfo = systemInfo;
    }

 /// <summary>Estado de VBS/HVCI (Memory Integrity): decide la variante del driver.</summary>
    private readonly ISystemInfoService _systemInfo;

 // ===================== Enumeración =====================

    public async Task<List<UsbPollingDevice>> GetDevicesAsync()
    {
        return await Task.Run(() =>
        {
            var devices = new List<UsbPollingDevice>();
            var seenProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in QueryDeviceIds("USB"))
            {
                try
                {
                    uint dn = GetDevNode(id);
                    if (dn == 0) continue;

 // Una fila por producto físico (como el filtro): las interfaces
 // MI_xx de un compuesto USB se agrupan bajo su nodo usbccgp.
                    uint product = ProductNodeOf(dn);
                    string? productId = DevNodeIdOf(product);
                    if (productId == null) continue;
                    if (!seenProducts.Add(productId)) continue;

                    var dev = BuildProduct(product, productId);
                    if (dev != null) devices.Add(dev);
                }
                catch (Exception ex) { _logging.LogDebug($"OverclockUSB: BuildProduct falló para {id}: {ex}\n{ex.StackTrace}"); /* un dispositivo con problemas no corta el listado */ }
            }

 // Orden estable por nombre (como el listado).
            devices.Sort((a, b) => string.Compare(a.ControllerName, b.ControllerName, StringComparison.OrdinalIgnoreCase));
            return devices;
        });
    }

 /// <summary>
 /// Sube por la cadena de devnodes hasta el producto físico: mientras el padre sea
 /// el compuesto USB (usbccgp), la fila representa a ese compuesto completo y no a
 /// una interfaz MI_xx suelta (igual que el filtro, que lista un dispositivo por fila).
 /// </summary>
    private static uint ProductNodeOf(uint dn)
    {
        uint current = dn;
        for (int i = 0; i < 8; i++)
        {
            uint parent = 0;
            if (CM_Get_Parent(ref parent, current, 0) != CR_SUCCESS || parent == 0 || parent == current)
                break;
            string? svc = GetStringProperty(parent, CM_DRP_SERVICE);
            if (svc != null && svc.Equals("usbccgp", StringComparison.OrdinalIgnoreCase))
            {
                current = parent;
                continue;
            }
            break;
        }
        return current;
    }

 /// <summary>Construye el registro del producto físico si califica: interfaces HID/mouse/teclado/mando USB, o un adaptador Bluetooth (radio USB, que no tiene HID adentro).</summary>
    private UsbPollingDevice? BuildProduct(uint productDn, string productId)
    {
 // Descendientes de TODAS las clases (HID, audio…), sin bajar por compuestos
 // ni hubs: así el HyperX muestra su endpoint de audio como hijo, igual que el filtro.
        var nodes = WalkSubtree(productDn, maxDepth: 4);

        bool isMouse = nodes.Any(n => IsMouseSvc(n.Service));
        bool isKeyboard = nodes.Any(n => IsKeyboardSvc(n.Service));
        bool isHid = nodes.Any(n => IsHidInterfaceSvc(n.Service));

 // Interfaces HID donde se aplica filtro + override de bInterval (para un
 // compuesto: cada MI_xx con driver HidUsb; para un HID simple: el propio nodo).
        string productSvc = GetStringProperty(productDn, CM_DRP_SERVICE) ?? "";

 // Un CONCENTRADOR/HUB no es un dispositivo ajustable: no tiene endpoint propio que el
 // filtro pueda tocar, y su árbol sí incluye a los dispositivos colgados de él, así que el
 // hub se "adjudicaba" la función de su hijo y aparecía como fila fantasma — un concentrador
 // raíz listado como el mouse que cuelga de él, o (con la rama de la radio) como si fuera un
 // adaptador Bluetooth. Se corta en el nodo del hub.
        if (IsHubSvc(productSvc)) return null;

        bool isBtRadio = IsBluetoothRadio(productDn, productSvc);

 // Un adaptador Bluetooth es un dispositivo USB más (no tiene interfaz HID adentro), así
 // que sin esta rama la calificación lo dejaba afuera y la grilla no lo mostraba: era el
 // hueco frente al filtro de referencia, que sí lo lista y le puede colgar su filtro.
        if (!isMouse && !isKeyboard && !isHid && !isBtRadio) return null;

        var interfaces = new List<string>();
        if (IsHidInterfaceSvc(productSvc))
            interfaces.Add(productId);
        else
            interfaces.AddRange(nodes
                .Where(n => IsHidInterfaceSvc(n.Service) && !string.IsNullOrEmpty(n.Id))
                .Select(n => n.Id));

 // La radio Bluetooth no expone ninguna interfaz HID: la función ES el propio
 // dispositivo (como un HID simple). Sin esto quedaría sin endpoint del que leer el
 // intervalo y sin destino donde el filtro se cuelga — el filtro, en cambio, lo hace
 // justamente en el devnode de la radio.
        if (interfaces.Count == 0 && isBtRadio)
            interfaces.Add(productId);

        string name = BestProductName(productDn, nodes);
        var children = AllChildren(nodes);

 // Fabricante de mandos (ej. 8BitDo): aunque Windows lo enumere como teclado
 // HID (así lo declara su firmware), es un mando. El VID del fabricante manda:
 // se descartan las funciones mouse/teclado que declare (son del mando, no un
 // teclado real) y queda el mando como función principal.
        if (IsGamepadVendor(productId))
        {
            // El mando va PRIMERO en la lista: es la función que lo identifica y la que elige
            // el emoji de la fila. Un DualShock/DualSense también expone audio (jack + micrófono)
            // y, sin esto, ese hijo podía quedar primero y el mando se veía con el ícono de
            // auriculares (el VID del fabricante es lo único que delata que es un control).
            children = children
                .Where(c => c.Kind != UsbDeviceKind.Mouse && c.Kind != UsbDeviceKind.Keyboard && c.Kind != UsbDeviceKind.Controller)
                .ToList();
            children.Insert(0, new UsbChildFunction(name, UsbDeviceKind.Controller));
        }

        var (child, childKind) = children.Count > 0
            ? (children[0].Name, children[0].Kind)
            : ("", UsbDeviceKind.Unknown);

 // Kind del producto para el emoji de la grilla. Si el producto tiene un hijo
 // funcional claro (mouse/teclado/audio), ese tipo gana aunque el compuesto
 // exponga además interfaces de otra clase (un mouse con botones extra no es
 // un teclado). Si el nodo ya ES el funcional (ChildName vacío), se usa la
 // clase del propio nodo.
        UsbDeviceKind kind = childKind;
        if (string.IsNullOrEmpty(child))
            kind = isMouse ? UsbDeviceKind.Mouse
                : isKeyboard ? UsbDeviceKind.Keyboard
                : isBtRadio ? UsbDeviceKind.Bluetooth
                : isHid ? UsbDeviceKind.Controller
                : UsbDeviceKind.Unknown;

 // "Controlador": la controladora de host USB (xHCI) donde está enchufado el
 // dispositivo (igual que la columna Controller de el filtro), no el driver.
        (string hostName, string hostId) = FindHostController(productDn);

 // bInterval real del endpoint de interrupción IN (por el hub padre). 0 = desconocido.
 // La velocidad importa: en high-speed bInterval es un exponente, no milisegundos.
        int interval = 0;
        byte speed = 0;
        foreach (var iface in interfaces)
        {
            uint idn = GetDevNode(iface);
            if (idn == 0) continue;
            (interval, speed) = GetInterruptInEndpointInfo(iface, idn);
            if (interval > 0) break;
        }
        bool isHighSpeed = speed == UsbSpeedHigh;
        int currentHz = interval > 0 ? HzFromBInterval(interval, isHighSpeed) : 0;

        int? activeRaw = null;
        bool filterOn = false;
        foreach (var iface in interfaces)
        {
            activeRaw ??= ReadActiveInterval(iface);
            filterOn |= IsFilterAttached(iface);
        }
 // El override se interpreta según la variante instalada: los builds de rango alto
 // reutilizan los códigos 31/62 (31=2000/4000, 62=4000/8000 según el build).
        int? active = activeRaw is int raw && raw > 0 ? HzFromActiveInterval(raw, isHighSpeed) : null;

        return new UsbPollingDevice(
            InstanceId: productId,
            ControllerName: name,
            ChildName: child,
            HostControllerName: hostName,
            HostControllerInstance: hostId,
            CurrentHz: currentHz,
            NativeHz: currentHz,
            ActiveHz: active,
            FilterOn: filterOn,
            IsMouse: isMouse,
            IsKeyboard: isKeyboard,
            IsHid: isHid,
            Kind: kind,
            Children: children,
            IsHighSpeed: isHighSpeed,
            DescriptorBInterval: interval,
            InterfaceIds: interfaces);
    }

 // ===================== Configuración por dispositivo =====================

    public async Task<bool> SetRateAsync(string deviceInstanceId, int rateHz)
    {
        return await Task.Run(async () =>
        {
            try
            {
                uint dn = GetDevNode(deviceInstanceId);
                if (dn == 0)
                {
                    _logging.LogWarning($"OverclockUSB: dispositivo no encontrado: {deviceInstanceId}");
                    return false;
                }

 // La fila es el producto físico: se aplica a todas sus interfaces HID.
                var targets = InterfaceIdsOf(dn, deviceInstanceId);
                if (targets.Count == 0) targets.Add(deviceInstanceId);

 // Quitar el override: borrar "bInterval" (el driver no fuerza nada).
                if (rateHz <= 0)
                {
                    foreach (var t in targets)
                    {
                        using var dp = OpenDeviceParametersKey(t, writable: true);
                        if (dp != null && dp.GetValue(ValueBInterval) != null)
                            dp.DeleteValue(ValueBInterval, throwOnMissingValue: false);
                    }
                    _logging.LogInfo($"OverclockUSB: {deviceInstanceId} sin override de bInterval. Falta restart.");
                    return true;
                }

 // Capacidad honesta ANTES de tocar nada: el overclock >1000 Hz en dispositivos
 // no high-speed depende de poder parchear la pila USB (HVCI lo prohíbe) y del
 // driver xHCI de Microsoft (requisito del autor para los builds 2k/8k).
                var (_, spd) = GetInterruptInEndpointInfo(targets[0], GetDevNode(targets[0]));
 // spd == 0 significa "desconocida o Full speed" (USB_DEVICE_SPEED Full = 0):
 // en ambos casos el valor se interpreta como ms, igual que el filtro.
                bool isHighSpeed = spd == UsbSpeedHigh;

                // Integridad de memoria: cuenta tanto si está corriendo como si está configurada
                // (pendiente de reiniciar). Instalar un build que parchea la pila USB cuando la
                // protección se va a activar en el próximo arranque dejaría al filtro sin poder
                // cargar en ese arranque, con el dispositivo colgado de un filtro bloqueado.
                var security = _systemInfo?.GetSecurityFeatures();
                bool hvci = security?.HvciRunning == true || security?.HvciConfigured == true;

 // Techo por BUS, no por driver: en Low/Full Speed el sondeo es como máximo una vez por
 // milisegundo. Por encima de 1000 Hz solo se llega con el "hard overclocking" del autor
 // (parche al driver xHCI), que en sus propias palabras depende de que el DISPOSITIVO sea
 // capaz de responder más rápido — algo que no se puede saber desde acá. Es el mismo techo
 // que expone hidusbf con su build de 1kHz, así que no se ofrece: sería escribir un número
 // que el dispositivo casi nunca entrega.
                if (!isHighSpeed && rateHz > MaxHz1khzVariant)
                {
                    _logging.LogWarning($"OverclockUSB: {rateHz} Hz rechazado para {deviceInstanceId}: el " +
                        "dispositivo va por Low/Full Speed y su bus sondea una vez por milisegundo (máx. 1000 Hz).");
                    return false;
                }

 // Asegurar el servicio del filtro instalado antes de colgarlo del dispositivo.
                if (!IsFilterServiceInstalled())
                {
 // Auto-instalación: primero un .sys local, si no hay, descarga el paquete.
                    var sys = FindDriverSysAsync().GetAwaiter().GetResult()
                              ?? DownloadComponentAsync().GetAwaiter().GetResult();
                    if (sys == null || !InstallFilterServiceAsync(sys).GetAwaiter().GetResult())
                    {
                        _logging.LogWarning("OverclockUSB: el servicio del filtro no está instalado y no se pudo instalar.");
                        return false;
                    }
                }

 // Variante del binario que el pedido necesita. Los builds "patching" no cargan
 // con Memory Integrity (HVCI): en ese caso SIEMPRE se instala NoPatch, que sí
 // carga y mantiene todo el rango nativo (byte del descriptor; en high-speed
 // incluye 8000 Hz sin parchear controladora). Los códigos 31/62 de los builds
 // de rango alto solo hacen falta para dispositivos NO high-speed.
                string wantedVariant;
                if (hvci) wantedVariant = "nopatch";
                else if (!isHighSpeed && rateHz > MaxHz1khzVariant)
                    wantedVariant = rateHz > MaxHz2kVariant ? "4khz-8khz" : "2khz-4khz";
                else wantedVariant = "";

                if (wantedVariant.Length > 0)
                {
                    var cacheDir = ComponentCachePath;
                    Directory.CreateDirectory(cacheDir);
                    string? variantSys = await EnsureVariantAsync(cacheDir, wantedVariant);
                    if (variantSys == null)
                    {
                        _logging.LogWarning($"OverclockUSB: no se pudo conseguir la variante '{wantedVariant}' verificada del paquete oficial.");
                        return false;
                    }
                    if (!SwapVariantBinary(cacheDir, wantedVariant))
                    {
                        _logging.LogWarning($"OverclockUSB: no se pudo instalar la variante '{wantedVariant}' (¿binario en uso por otro dispositivo con filtro activo?).");
                        return false;
                    }
                }

 // Codificación del valor (contrato del autor, README.2kHz-8kHz: los builds de
 // rango alto reutilizan los códigos 31 y 62 con significados propios):
 // - Dispositivo high-speed: byte NATIVO del descriptor (exponente 2^(N-1) µframes);
 //   8000 Hz es byte 1 y funciona con cualquier variante, incluso NoPatch.
 // - Dispositivo no high-speed ≤1000 Hz: valor en ms (1000/N), con cualquier variante.
 // - Dispositivo no high-speed >1000 Hz: SOLO con la variante del rango:
 //   2khz-4khz → 31=2000, 62=4000; 4khz-8khz → 31=4000, 62=8000.
                int intervalToWrite;
                double effectiveHz;
                if (isHighSpeed)
                {
                    intervalToWrite = 1;
                    double bestDiff = double.MaxValue;
                    for (int n = 1; n <= 16; n++)
                    {
                        double hz = 8000.0 / (1 << (n - 1));
                        double diff = Math.Abs(hz - rateHz);
                        if (diff < bestDiff) { bestDiff = diff; intervalToWrite = n; }
                    }
                    effectiveHz = 8000.0 / (1 << (intervalToWrite - 1));
                }
                else if (rateHz > MaxHz1khzVariant && wantedVariant.Length > 0 && RateMapFor(wantedVariant).TryGetValue(rateHz, out int ocCode))
                {
                    intervalToWrite = ocCode;
                    effectiveHz = rateHz;
                }
                else
                {
                    intervalToWrite = Math.Clamp((int)Math.Round(1000.0 / rateHz), 1, 255);
                    effectiveHz = 1000.0 / intervalToWrite;
                }

                foreach (var t in targets)
                {
 // 1) Filtro inferior del dispositivo (mechanism 1 de el filtro).
                    uint tdn = GetDevNode(t);
                    if (tdn != 0) AttachFilter(tdn, t, attach: true);

 // 2) Override de bInterval en Device Parameters (mechanism 2).
                    using var dp = OpenDeviceParametersKey(t, writable: true);
                    if (dp == null)
                    {
                        _logging.LogWarning($"OverclockUSB: Device Parameters inaccesible para {t}");
                        return false;
                    }
                    dp.SetValue(ValueBInterval, intervalToWrite, RegistryValueKind.DWord);
                }

                _logging.LogInfo($"OverclockUSB: {deviceInstanceId} → filtro activo en {targets.Count} interfaz(es), " +
                    $"bInterval={intervalToWrite} ({(isHighSpeed ? $"exp. high-speed, 2^{intervalToWrite - 1} µf" : wantedVariant.Length > 0 && rateHz > MaxHz1khzVariant ? $"código '{wantedVariant}'" : "ms")}) " +
                    $"→ {effectiveHz:0.#} Hz efectivos (pedido {rateHz} Hz){(hvci ? " [NoPatch: HVCI activa]" : "")}. Falta restart del dispositivo.");
                return true;
            }
            catch (Exception ex)
            {
                _logging.LogError($"OverclockUSB: error configurando {deviceInstanceId}", ex);
                return false;
            }
        });
    }

    public async Task<bool> RestoreDeviceAsync(string deviceInstanceId)
    {
        return await Task.Run(() =>
        {
            try
            {
                uint dn = GetDevNode(deviceInstanceId);
                if (dn == 0) return false;

 // La fila es el producto físico: restaurar todas sus interfaces HID.
                var targets = InterfaceIdsOf(dn, deviceInstanceId);
                if (targets.Count == 0) targets.Add(deviceInstanceId);

                foreach (var t in targets)
                {
 // 1) Borrar el override de bInterval.
                    using (var dp = OpenDeviceParametersKey(t, writable: true))
                    {
                        if (dp != null && dp.GetValue(ValueBInterval) != null)
                            dp.DeleteValue(ValueBInterval, throwOnMissingValue: false);
                    }

 // 2) Sacar el filtro del dispositivo (LowerFilters vuelve a su valor previo).
                    uint tdn = GetDevNode(t);
                    if (tdn != 0) AttachFilter(tdn, t, attach: false);
                }

                _logging.LogInfo($"OverclockUSB: {deviceInstanceId} restaurado a fábrica ({targets.Count} interfaz(es), sin filtro ni override). Falta restart.");
                return true;
            }
            catch (Exception ex)
            {
                _logging.LogError($"OverclockUSB: error restaurando {deviceInstanceId}", ex);
                return false;
            }
        });
    }

    /// <summary>
    /// Estado de la Integridad de memoria (HVCI) y de la variante del filtro instalada, en una
    /// sola lectura. Es lo que la UI necesita para advertir por qué el techo de Hz puede ser
    /// menor al esperado: las variantes que parchean la pila USB NO cargan con HVCI (lo dice el
    /// README oficial del driver, "Warning 2"), así que el servicio instala NoPatch, que respeta
    /// la protección pero no puede subir el techo nativo del dispositivo.
    ///
    /// Nunca lanza: si algo no se puede leer, devuelve "sin aviso" (todo false/0) y lo deja en
    /// el log — un aviso de seguridad no puede romper la página.
    /// </summary>
    public UsbOverclockGuardStatus GetGuardStatus()
    {
        try
        {
            var security = _systemInfo?.GetSecurityFeatures();
            bool installed = IsFilterServiceInstalled();
            string variant = installed ? InstalledVariant() : "";
            return new UsbOverclockGuardStatus(
                security?.HvciRunning == true,
                security?.HvciConfigured == true,
                variant,
                CeilingOfVariant(variant),
                installed);
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: no se pudo leer el estado de seguridad (HVCI): {ex.Message}");
            return new UsbOverclockGuardStatus(false, false, "", 0, false);
        }
    }

    /// <summary>Techo de overclock que da cada variante del binario (0 = no hay driver instalado).
    /// NoPatch no parchea la pila, así que nunca pasa del techo nativo del dispositivo: sus
    /// 1000 Hz son el máximo que puede FORZAR, no un techo del rango nativo.</summary>
    private static int CeilingOfVariant(string variant) => variant switch
    {
        "2khz-4khz" => MaxHz2kVariant,
        "4khz-8khz" => MaxHz8kVariant,
        "1khz" => MaxHz1khzVariant,
        "nopatch" => MaxHz1khzVariant,
        _ => 0
    };

    public async Task<UsbPollingVerify> VerifyDeviceAsync(string deviceInstanceId)
    {
        return await Task.Run(() =>
        {
            int effectiveHz = 0;
            int? problem = null;
            try
            {
                uint dn = GetDevNode(deviceInstanceId);
                if (dn != 0)
                {
                {
                    uint status = 0, prob = 0;
                    if (CM_Get_DevNode_Status(ref status, ref prob, dn, 0) == CR_SUCCESS)
                        problem = (int)prob;
                }

                    var (interval, speed) = GetInterruptInEndpointInfo(deviceInstanceId, dn);
                    bool hs = speed == UsbSpeedHigh;
                    if (interval > 0)
                    {
                        // High-speed: el descriptor muestra el exponente nativo
                        // (2^(N-1) µframes) y el driver lo traduce al vuelo para el
                        // stack, así que el Hz del descriptor es la tasa real.
                        // No high-speed: los códigos 31/62 pueden significar 2k/4k/8k
                        // según la variante instalada (HzFromActiveInterval lo resuelve).
                        effectiveHz = hs ? HzFromBInterval(interval, true)
                                         : HzFromActiveInterval(interval, hs);
                    }
                }
            }
            catch (Exception ex)
            {
                _logging.LogDebug($"OverclockUSB: verificación de {deviceInstanceId} falló: {ex.Message}");
            }
            return new UsbPollingVerify(effectiveHz, problem);
        });
    }

    public async Task<bool> RestartDeviceAsync(string deviceInstanceId)
    {
        return await Task.Run(() =>
        {
            try
            {
                uint dn = GetDevNode(deviceInstanceId);
                if (dn == 0)
                {
                    _logging.LogWarning($"OverclockUSB: restart fallido, devnode inexistente: {deviceInstanceId}");
                    return false;
                }

                // El filtro y el override viven en las INTERFACES HID (MI_xx): hay que
                // reiniciar CADA interfaz. Re-enumerar solo el devnode del PRODUCTO (lo que
                // se hacía antes) no reconstruye la pila de las interfaces, así que PnP no
                // vuelve a leer LowerFilters y el filtro queda sin cargar (Stopped): el
                // polling rate nunca cambiaba, aunque el registro quedara bien escrito.
                var targets = InterfaceIdsOf(dn, deviceInstanceId);
                if (targets.Count == 0) targets.Add(deviceInstanceId);

                bool allOk = true;
                foreach (var target in targets)
                    allOk &= RestartDevNode(target);

                if (!allOk)
                {
                    // Respaldo: re-enumerar el devnode del producto (comportamiento previo),
                    // sincrónico: no vuelve hasta que la pila se re-creó.
                    uint cr = CM_Reenumerate_DevNode(dn, CM_REENUMERATE_SYNCHRONOUS | CM_REENUMERATE_NORMAL);
                    _logging.LogInfo($"OverclockUSB: restart de respaldo del producto {deviceInstanceId}: CR=0x{cr:X8}");
                    allOk = cr == CR_SUCCESS;
                }
                return allOk;
            }
            catch (Exception ex)
            {
                _logging.LogError($"OverclockUSB: error re-arrancando {deviceInstanceId}", ex);
                return false;
            }
        });
    }    /// <summary>
    /// Reinicia un devnode con DIF_PROPERTYCHANGE/DICS_PROPCHANGE (el mismo "reiniciar
    /// dispositivo" que pnputil /restart-device). Devuelve false si PnP no lo aceptó.
    /// </summary>
    private bool RestartDevNode(string instanceId)
    {
        IntPtr set = IntPtr.Zero;
        try
        {
            set = SetupDiCreateDeviceInfoListExW(IntPtr.Zero, IntPtr.Zero, null, IntPtr.Zero);
            if (set == InvalidHandleValue)
            {
                _logging.LogWarning($"OverclockUSB: no se pudo crear la lista de dispositivos (err={Marshal.GetLastWin32Error()}).");
                return false;
            }

            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data))
            {
                _logging.LogWarning($"OverclockUSB: no se pudo abrir el devnode {instanceId} (err={Marshal.GetLastWin32Error()}).");
                return false;
            }

            var props = new SP_PROPCHANGE_PARAMS
            {
                ClassInstallHeader = new SP_CLASSINSTALL_HEADER
                {
                    cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                    InstallFunction = DIF_PROPERTYCHANGE
                },
                StateChange = DICS_PROPCHANGE,
                Scope = DICS_FLAG_GLOBAL,
                HwProfile = 0
            };

            if (!SetupDiSetClassInstallParamsW(set, ref data, ref props, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>())
                || !SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref data))
            {
                _logging.LogWarning($"OverclockUSB: restart de {instanceId} falló (err={Marshal.GetLastWin32Error()}).");
                return false;
            }

            _logging.LogInfo($"OverclockUSB: interfaz {instanceId} reiniciada (DIF_PROPERTYCHANGE).");
            return true;
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: error reiniciando {instanceId}: {ex.Message}");
            return false;
        }
        finally
        {
            if (set != IntPtr.Zero && set != InvalidHandleValue)
                SetupDiDestroyDeviceInfoList(set);
        }
    }

    // ===================== Servicio del filtro =====================

    public bool IsFilterServiceInstalled()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var svc = key.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\hidusbf");
            return svc != null;
        }
        catch { return false; }
    }

    public bool IsFilterServiceRunnable()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var svc = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\hidusbf");
            if (svc == null) return false;

            var imagePath = (svc.GetValue("ImagePath") as string)?.Trim().Trim('"');
            if (string.IsNullOrEmpty(imagePath)) return false;

 // Resolver el binario: \SystemRoot\... → C:\Windows\...; system32\... relativo; o absoluto.
            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string candidate = imagePath;
            if (candidate.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                candidate = Path.Combine(windowsDir, candidate.Substring(@"\SystemRoot\".Length));
            else if (!Path.IsPathRooted(candidate))
                candidate = Path.Combine(windowsDir, candidate);
            candidate = candidate.Replace(@"\??\", "");
            return File.Exists(candidate);
        }
        catch { return false; }
    }

 /// <summary>True si el servicio del filtro existe Y su binario está presente (la UI muestra la grilla).</summary>
    public bool IsComponentReady()
    {
        return IsFilterServiceInstalled() && IsFilterServiceRunnable();
    }

 /// <summary>Ruta de la carpeta donde se guarda el componente descargado (para re-descargar al limpiar el caché).</summary>
    public string ComponentCachePath => Path.Combine(AppPaths.RootDir, "hidusbf");

 /// <summary>
 /// Desinstala el componente de Overclock USB por completo y borra el caché: quita
 /// el filtro y el override de bInterval de cada dispositivo afectado, elimina el
 /// servicio kernel y el el binario del filtro de System32\drivers, y borra la copia
 /// en caché (%LocalAppData%\WHPO). La próxima vez que se abra la sección
 /// la puerta de entrada vuelve a aparecer y el componente se re-descarga del
 /// repositorio oficial (verificado por SHA-256; o se reutiliza si el usuario
 /// coloca el binario junto al exe).
 /// </summary>
    public void ClearComponentCache()
    {
 // 1) Quitar filtro + override de bInterval de los dispositivos que lo tengan.
        try
        {
            foreach (var id in QueryDeviceIds("USB"))
            {
                try
                {
                    if (!IsFilterAttached(id)) continue;
                    uint dn = GetDevNode(id);
                    if (dn != 0) AttachFilter(dn, id, attach: false);
                    using var dp = OpenDeviceParametersKey(id, writable: true);
                    if (dp != null && dp.GetValue(ValueBInterval) != null)
                        dp.DeleteValue(ValueBInterval, throwOnMissingValue: false);
                }
                catch { /* un dispositivo con problemas no corta el resto */ }
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: error quitando filtros al limpiar el componente: {ex.Message}");
        }

 // 2) Borrar el servicio kernel del filtro.
        try
        {
            IntPtr scm = ScManagerHandle;
            if (scm != IntPtr.Zero)
            {
                IntPtr svc = OpenServiceW(scm, FilterServiceName, ServiceDelete);
                if (svc != IntPtr.Zero)
                {
 // API nativa: DeleteService (no tiene variante W/A; antes se
 // declaraba "DeleteServiceW" y fallaba con EntryPointNotFoundException,
 // dejando la clave del servicio huérfana tras la desinstalación).
                    bool ok = DeleteService(svc);
                    int err = Marshal.GetLastWin32Error();
                    CloseServiceHandle(svc);
                    _logging.LogInfo($"OverclockUSB: servicio del filtro eliminado (ok={ok}, err={err}).");

 // Verificación: DeleteService marca para eliminación; la clave puede
 // quedar viva hasta el reinicio (si el servicio está en uso). Sin el
 // binario, IsComponentReady igualmente reporta "no listo", pero
 // dejamos el estado asentado en el log para diagnóstico.
                    if (IsFilterServiceInstalled())
                        _logging.LogWarning("OverclockUSB: la clave del servicio del filtro sigue presente (marcado para eliminación; desaparece al reiniciar Windows).");
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 1060) // ERROR_SERVICE_DOES_NOT_EXIST
                        _logging.LogInfo("OverclockUSB: servicio del filtro no estaba instalado.");
                    else
                        _logging.LogWarning($"OverclockUSB: no se pudo abrir el servicio del filtro para eliminarlo (err={err}; ¿falta elevación?).");
                }
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: no se pudo borrar el servicio del filtro: {ex.Message}");
        }

 // 3) Borrar el binario del filtro de System32\drivers (puede fallar si el binario está
 // cargado; igual el servicio ya no existe → la puerta de entrada reaparece).
        try
        {
            var sysPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "hidusbf.sys");
            if (File.Exists(sysPath))
            {
                File.Delete(sysPath);
                _logging.LogInfo($"OverclockUSB: {sysPath} eliminado.");
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: no se pudo borrar el binario del filtro (¿cargado?): {ex.Message}");
        }

 // 4) Caché de la app (la copia descargada del paquete).
        try
        {
            if (Directory.Exists(ComponentCachePath))
            {
                Directory.Delete(ComponentCachePath, recursive: true);
                _logging.LogInfo($"OverclockUSB: caché del componente borrado ({ComponentCachePath}).");
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: no se pudo borrar el caché del componente: {ex.Message}");
        }
    }

 /// <summary>
 /// Resuelve el binario del componente (el binario del filtro) listo para instalar:
 /// 1) Copia previa verificada en el caché de la app (%LocalAppData%\WHPO):
 /// ya instalada antes, o extraída por el usuario del drop avanzado.
 /// 2) Binario opcional junto al exe (Assets\Drivers\el binario del filtro), solo si su
 /// SHA-256 coincide con el fijado (reserva para distribuir con permiso).
 /// 3) Descarga el paquete oficial (repositorio oficial, HTTPS) y valida el
 /// SHA-256 del el binario del filtro extraído antes de aceptarlo.
 /// Todo lo que se acepta pasa por IsKnownDriverBinary. No instala el servicio:
 /// eso lo hace InstallFilterServiceAsync; la UI muestra esto como "descargar
 /// componentes".
 /// </summary>
    public async Task<string?> DownloadComponentAsync()
    {
        return await Task.Run(async () =>
        {
            try
            {
                var cacheDir = ComponentCachePath;
                Directory.CreateDirectory(cacheDir);

                var bundled = BundledDriverSysPath;
                var cachedSys = Path.Combine(cacheDir, "hidusbf.sys");

 // 1) Copia previa en caché (instalaciones anteriores), verificada.
                if (IsKnownDriverBinary(cachedSys))
                {
                    _logging.LogInfo($"OverclockUSB: componente ya en caché ({cachedSys}).");
                    return cachedSys;
                }

 // 2) Binario opcional junto al exe: se copia al caché (queda disponible
 // también después de "desinstalar componente", sin volver a descargar).
                if (IsKnownDriverBinary(bundled))
                {
                    File.Copy(bundled, cachedSys, overwrite: true);
                    _logging.LogInfo($"OverclockUSB: componente resuelto desde {bundled}.");
                    return cachedSys;
                }

 // 3) Fallback (app sin el binario embebido): descarga el paquete oficial.
                const string packageUrl =
                    "https://raw.githubusercontent.com/LordOfMice/hidusbf/master/hidusbf.zip";
                var zipPath = Path.Combine(cacheDir, "hidusbf-pkg.zip");
                var extractDir = Path.Combine(cacheDir, "pkg");
                try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch (Exception ex) { _logging.LogDebug($"OverclockUSB: no se pudo limpiar la carpeta de extracción previa: {ex.Message}"); }
                Directory.CreateDirectory(extractDir);

                _logging.LogInfo($"OverclockUSB: descargando componente desde {packageUrl}");
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("WinForge-ComponentSetup/1.0");
                    using var resp = await http.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    using var remote = await resp.Content.ReadAsStreamAsync();
                    using var file = File.Create(zipPath);
                    await remote.CopyToAsync(file);
                }

                ZipFile.ExtractToDirectory(zipPath, extractDir);

 // Cosechar TODAS las variantes del paquete (1khz, 2khz-4khz, 4khz-8khz, NoPatch):
 // el techo de overclock lo decide el binario instalado, así que la app necesita
 // poder cambiar de variante sin re-descargar. Cada una se valida contra su hash.
                int harvested = HarvestDriverVariants(extractDir, cacheDir);
                if (harvested == 0)
                {
                    _logging.LogWarning("OverclockUSB: el paquete descargado no contiene ninguna variante de hidusbf.sys válida.");
                    return null;
                }

                File.Copy(PathOfVariant(cacheDir, "1khz"), cachedSys, overwrite: true);
                try { File.Delete(zipPath); } catch (Exception ex) { _logging.LogDebug($"OverclockUSB: no se pudo borrar el zip descargado: {ex.Message}"); }
                try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch (Exception ex) { _logging.LogDebug($"OverclockUSB: no se pudo borrar la carpeta de extracción temporal: {ex.Message}"); }

                _logging.LogInfo($"OverclockUSB: componente descargado y verificado ({cachedSys}); variantes cosechadas: {harvested}.");
                return cachedSys;
            }
            catch (Exception ex)
            {
                _logging.LogError("OverclockUSB: error resolviendo el componente", ex);
                return null;
            }
        });
    }

 /// <summary>Busca el driver del paquete extraído, prefiriendo la variante AMD64_AS y luego AMD64 (nunca NTX86/98ME).</summary>
    private static string? LocateDriverSys(string root)
    {
        try
        {
            var all = Directory.EnumerateFiles(root, "hidusbf.sys", SearchOption.AllDirectories).ToList();
            if (all.Count == 0) return null;
            foreach (var variant in new[] { "AMD64_AS", "AMD64" })
            {
                var hit = all.FirstOrDefault(p => p.Contains(variant, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return null;
        }
        catch { return null; }
    }

 /// <summary>Sanity check del binario: PE (MZ) y tamaño plausible para un driver.</summary>
    private static bool LooksLikeDriverSys(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 10_000) return false;
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[2];
            return fs.Read(head) == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z';
        }
        catch { return false; }
    }

    public void InvalidateFilterServiceCache() { /* el estado se lee siempre del registro: sin caché */ }

 /// <summary>
 /// Hz reales que representa un bInterval, según la velocidad del dispositivo:
 /// - Low/Full speed: bInterval en frames de 1 ms → Hz = 1000/N.
 /// - High speed: bInterval es un exponente N y el intervalo real es 2^(N-1)
 /// microframes de 125 µs → Hz = 8000/2^(N-1) (USB 2.0 §9.6.6). Sin esto, un
 /// dispositivo high-speed con N=1 se mostraba como 1000 Hz, y un pedido de
 /// 1000 Hz se escribía como "1 ms" (que en high-speed es N=8 → 62.5 Hz).
 /// </summary>
 /// <summary>
 /// Interpreta un valor "bInterval" GUARDADO (override) según la variante del binario
 /// instalado. Los builds 2khz-4khz y 4khz-8khz reutilizan los códigos 31/62 con
 /// significados propios (README.2kHz-8kHz); el resto de valores sigue la regla base.
 /// </summary>
    private int HzFromActiveInterval(int raw, bool isHighSpeed)
    {
        if (!isHighSpeed && raw is 31 or 62)
        {
            try
            {
                var map = RateMapFor(InstalledVariant());
                if (map.TryGetValue(raw, out int hz) && hz > 1000) return hz;
            }
            catch { }
        }
        return HzFromBInterval(raw, isHighSpeed);
    }

    public async Task<UsbPollingCapability> GetPollingCapabilityAsync(string deviceInstanceId)
    {
        return await Task.Run(() =>
        {
 // Techo por defecto: el máximo del mecanismo (8000). Se recorta SOLO con
 // evidencia, y cada recorte deja su motivo (la UI lo muestra).
            int maxHz = MaxHz8kVariant;
            string? reason = null;
            try
            {
                uint dn = GetDevNode(deviceInstanceId);
                if (dn == 0) return new UsbPollingCapability(MaxHz1khzVariant, null);

                var (_, spd) = GetInterruptInEndpointInfo(deviceInstanceId, dn);
                bool isHighSpeed = spd == UsbSpeedHigh;

 // High-speed: el byte nativo del descriptor llega a 8000 Hz (exponente 1) y
 // funciona con CUALQUIER variante, incluso NoPatch bajo HVCI: no hay techo.
                if (isHighSpeed) return new UsbPollingCapability(maxHz, null);

 // Low/Full Speed: el techo lo pone el BUS (una trama de 1 ms), no el driver. El propio
 // autor del componente lo dice en su tabla: LS = Low Speed (máx. 125 Hz de diseño),
 // FS = Full Speed (máx. 1000 Hz), HS = High Speed (máx. 8000 Hz). Los 2000-8000 en un
 // dispositivo Low/Full Speed son su "hard overclocking": parchear el driver xHCI y
 // rezar para que el dispositivo responda más rápido (él mismo avisa que si el rate no
 // supera los 1000, el aparato "no es capaz de hard overclocking"). hidusbf lo expone
 // aparte, instalando a mano el build 2k/4k/8k, y no lo garantiza. Acá no se ofrece:
 // sería mostrar números que el dispositivo casi nunca entrega.
 // El motivo es la CLAVE de traducción, y en esta app la clave es el texto fuente en
 // español (en es-AR se muestra tal cual). Por eso son frases y no identificadores
 // técnicos como "usb.hvci": con un identificador, el usuario en español veía la clave.
                maxHz = MaxHz1khzVariant;
                if (spd == UsbSpeedLow)
                {
                    // El autor advierte que el ajuste en Low Speed puede no tomar efecto, incluso
                    // con la pila de Microsoft actual; con una que no es la suya, directamente
                    // puede rechazarse. Se dice, no se oculta: el techo sigue siendo el mismo.
                    reason = IsMicrosoftUsb3Controller(deviceInstanceId)
                        ? "Este dispositivo va por USB Low Speed: el bus lo sondea muy despacio y en Windows actual el ajuste puede no tomar efecto. El techo de esta conexión son los 1000 Hz."
                        : "Este dispositivo va por USB Low Speed y este equipo no usa el controlador USB de Microsoft: el ajuste puede ser rechazado. El techo de esta conexión son los 1000 Hz.";
                }
                else
                {
                    reason = "Este dispositivo va por USB Full Speed: el bus lo sondea como máximo una vez por milisegundo, así que 1000 Hz es el techo de esta conexión. Los que van por High Speed sí llegan a 8000 Hz.";
                }
            }
            catch (Exception ex)
            {
                _logging.LogDebug($"OverclockUSB: no se pudo calcular la capacidad de overclock de {deviceInstanceId}: {ex.Message}");
                maxHz = MaxHz1khzVariant;
            }
            return new UsbPollingCapability(maxHz, reason);
        });
    }

    private static int HzFromBInterval(int interval, bool isHighSpeed)
    {
        if (interval <= 0) return 0;
        return isHighSpeed
            ? Math.Max(1, (int)Math.Round(8000.0 / (1 << Math.Min(interval - 1, 15))))
            : Math.Max(1, (int)Math.Round(1000.0 / interval));
    }

 /// <summary>SHA-256 del archivo en mayúsculas, o null si no se pudo calcular.</summary>
    private static string? Sha256Of(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
        catch { return null; }
    }

 /// <summary>
 /// True si el binario es plausible (PE + tamaño) Y su SHA-256 coincide con el
 /// fijado del paquete oficial. Cualquier copia con hash distinto no pasa por los
 /// flujos automáticos: nunca se instala un .sys de origen desconocido.
 /// </summary>
    private bool IsKnownDriverBinary(string path)
    {
        if (!LooksLikeDriverSys(path)) return false;
        var hash = Sha256Of(path);
        if (hash == null) return false;
        if (hash.Equals(KnownDriverSha256, StringComparison.OrdinalIgnoreCase)) return true;
        _logging.LogWarning($"OverclockUSB: hidusbf.sys con SHA-256 inesperado ({hash}); ignorado por seguridad.");
        return false;
    }

 // ===================== Variantes del driver (techo real de overclock) =====================

 /// <summary>Ruta esperada de la variante dada dentro del caché del componente.</summary>
    private static string PathOfVariant(string cacheDir, string variant) => Path.Combine(cacheDir, "variants", variant, "hidusbf.sys");

 /// <summary>Variante del binario instalado en system32\drivers, identificada por SHA-256; "" si el hash no coincide con ninguna verificada (así el aviso de la UI no afirma una variante desconocida).</summary>
    private string InstalledVariant()
    {
        try
        {
            var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "hidusbf.sys");
            var hash = Sha256Of(dest);
            foreach (var (variant, known) in VariantSha256)
                if (hash != null && hash.Equals(known, StringComparison.OrdinalIgnoreCase)) return variant;
        }
        catch { }
 // Hash desconocido = el .sys no es ninguna de las variantes verificadas (lo instaló otro
 // medio, o el autor publicó un build nuevo). Devolver "" es deliberado: el aviso de
 // seguridad de la UI no puede afirmar una variante que no pudo identificar.
        return "";
    }

 /// <summary>
 /// Copia al caché todas las variantes del paquete extraído que verifiquen contra el
 /// hash oficial (solo las x64 de AMD64_AS, que es la arquitectura que instala la
 /// app). Devuelve cuántas quedaron disponibles.
 /// </summary>
    private int HarvestDriverVariants(string extractRoot, string cacheDir)
    {
        int count = 0;
        try
        {
            var all = Directory.EnumerateFiles(extractRoot, "hidusbf.sys", SearchOption.AllDirectories).ToList();
            foreach (var (variant, known) in VariantSha256)
            {
                var hit = all.FirstOrDefault(p =>
                    p.Contains("AMD64_AS", StringComparison.OrdinalIgnoreCase) &&
                    p.Contains(variant, StringComparison.OrdinalIgnoreCase));
                if (hit == null) continue;
                var hash = Sha256Of(hit);
                if (hash == null || !hash.Equals(known, StringComparison.OrdinalIgnoreCase))
                {
                    _logging.LogWarning($"OverclockUSB: variante '{variant}' del paquete con SHA-256 inesperado; se descarta.");
                    continue;
                }
                var dest = PathOfVariant(cacheDir, variant);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(hit, dest, overwrite: true);
                count++;
            }
        }
        catch (Exception ex)
        {
            _logging.LogWarning($"OverclockUSB: error cosechando variantes del paquete: {ex.Message}");
        }
        return count;
    }

 /// <summary>
 /// Garantiza en caché la variante pedida (y verifica su hash). Si no está, intenta
 /// FindDriverSysAsync (caché raíz / drop junto al exe) y como última salida re-descarga
 /// el paquete oficial. Devuelve la ruta verificada o null.
 /// </summary>
    private async Task<string?> EnsureVariantAsync(string cacheDir, string variant)
    {
        var dest = PathOfVariant(cacheDir, variant);
        if (IsKnownVariantBinary(dest, variant)) return dest;

 // El paquete del caché raíz (instalaciones viejas) puede servir si su hash coincide.
        if (variant == "1khz")
        {
            var legacy = Path.Combine(cacheDir, "hidusbf.sys");
            if (IsKnownVariantBinary(legacy, variant))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(legacy, dest, overwrite: true);
                return dest;
            }
        }

 // Re-descarga completa (el flujo normal ya cosecha todas las variantes).
        var resolved = await DownloadComponentAsync();
        return IsKnownVariantBinary(dest, variant) ? dest
             : (resolved != null && variant == "1khz" ? resolved : null);
    }

 /// <summary>IsKnownDriverBinary pero contra el hash de la variante dada.</summary>
    private bool IsKnownVariantBinary(string path, string variant)
    {
        if (!VariantSha256.TryGetValue(variant, out var known)) return false;
        if (!LooksLikeDriverSys(path)) return false;
        var hash = Sha256Of(path);
        if (hash == null) return false;
        if (hash.Equals(known, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

 /// <summary>
 /// Reemplaza el binario del servicio por la variante pedida. Si ya es esa variante
 /// no toca nada. Falla si el servicio está corriendo con el binario abierto (compartido
 /// violado) — el llamante reintenta tras desenganchar el filtro de los dispositivos.
 /// </summary>
    private bool SwapVariantBinary(string cacheDir, string variant)
    {
        try
        {
            if (!VariantSha256.TryGetValue(variant, out var known)) return false;
            var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "hidusbf.sys");
            var current = Sha256Of(dest);
            if (current != null && current.Equals(known, StringComparison.OrdinalIgnoreCase)) return true;

            var src = PathOfVariant(cacheDir, variant);
            if (!IsKnownVariantBinary(src, variant))
            {
                _logging.LogWarning($"OverclockUSB: no hay copia verificada de la variante '{variant}' para instalar.");
                return false;
            }
            File.Copy(src, dest, overwrite: true);
            _logging.LogInfo($"OverclockUSB: binario del filtro reemplazado por la variante '{variant}'.");
            return true;
        }
        catch (IOException ex)
        {
            _logging.LogWarning($"OverclockUSB: el binario del filtro está en uso (no se puede cambiar la variante): {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _logging.LogError($"OverclockUSB: error cambiando a la variante '{variant}'", ex);
            return false;
        }
    }

    public async Task<bool> InstallFilterServiceAsync(string driverSysPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(driverSysPath) || !File.Exists(driverSysPath))
                {
                    _logging.LogWarning($"OverclockUSB: hidusbf.sys no existe en {driverSysPath}");
                    return false;
                }

 // Registro del hash: los flujos automáticos ya vienen verificados desde
 // FindDriverSysAsync/DownloadComponentAsync; la vía manual es una decisión
 // explícita del usuario (escape hatch), así que no se bloquea, se audita.
                var pickedHash = Sha256Of(driverSysPath);
                if (pickedHash != null)
                    _logging.LogInfo(pickedHash.Equals(KnownDriverSha256, StringComparison.OrdinalIgnoreCase)
                        ? "OverclockUSB: hidusbf.sys verificado (SHA-256 = binario oficial del paquete)."
                        : $"OverclockUSB: hidusbf.sys manual con SHA-256 distinto del oficial ({pickedHash}); instalando por decisión del usuario.");

 // Copiar a system32\drivers (DestinationDirs del INF: 12 = DRIVERS).
                var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var dest = Path.Combine(system32, "drivers", "hidusbf.sys");
                File.Copy(driverSysPath, dest, overwrite: true);

                if (!IsFilterServiceInstalled())
                {
 // Servicio kernel a demanda (StartType=3 y ServiceBinary=%12%\el binario del filtro del INF).
 // El ImagePath estilo INF es relativo a %SystemRoot%.
                    IntPtr hService = CreateServiceW(ScManagerHandle, FilterServiceName,
                        "USB Mouse Rate Adjuster Lower Filter by SweetLow",
                        ServiceAccessAll, ServiceKernelDriver, ServiceDemandStart, ServiceErrorNormal,
                        @"System32\DRIVERS\hidusbf.sys", null, IntPtr.Zero, null, null, null);
                    if (hService == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        _logging.LogError($"OverclockUSB: CreateService(hidusbf) falló (error {err}).");
                        return false;
                    }
 // Descripción (best-effort, como el resto del paquete).
                    try
                    {
                        IntPtr sc = OpenServiceW(ScManagerHandle, FilterServiceName, ServiceChangeConfig);
                        if (sc != IntPtr.Zero)
                        {
                            var desc = new SERVICE_DESCRIPTIONW { lpDescription = "USB Mouse Rate Adjuster Lower Filter by SweetLow" };
                            ChangeServiceConfig2W(sc, SERVICE_CONFIG_DESCRIPTION, ref desc);
                            CloseServiceHandle(sc);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logging.LogDebug($"OverclockUSB: no se pudo configurar la descripción del servicio de filtro: {ex.Message}");
                    }
                    finally
                    {
                        CloseServiceHandle(hService);
                    }
                }
                else
                {
 // Ya existe: dejar el ImagePath como esté (inf-style) y solo asegurar el binario.
                    _logging.LogInfo("OverclockUSB: servicio del filtro ya existía; binario actualizado.");
                }

                _logging.LogInfo($"OverclockUSB: servicio del filtro instalado desde {driverSysPath} → {dest}");
                return true;
            }
            catch (Exception ex)
            {
                _logging.LogError("OverclockUSB: error instalando el servicio del filtro", ex);
                return false;
            }
        });
    }

    public async Task<string?> FindDriverSysAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
 // Orden: caché de la app (incluye el drop manual avanzado DRIVER\AMD64_AS /
 // DRIVER\AMD64) → binario opcional junto al exe. Sin escaneo de carpetas
 // del usuario (Downloads): todo lo aceptado debe verificar contra el
 // SHA-256 fijado del paquete oficial.
                var candidates = new List<string>
                {
                    Path.Combine(ComponentCachePath, "hidusbf.sys"),
                    Path.Combine(ComponentCachePath, "DRIVER", "AMD64_AS", "hidusbf.sys"),
                    Path.Combine(ComponentCachePath, "DRIVER", "AMD64", "hidusbf.sys"),
                    BundledDriverSysPath
                };
                return candidates.FirstOrDefault(IsKnownDriverBinary);
            }
            catch { return null; }
        });
    }

 // ===================== Registro por dispositivo =====================

 /// <summary>Lee el bInterval crudo guardado en Device Parameters (valor "bInterval" DWORD).</summary>
    private static int? ReadActiveInterval(string deviceInstanceId)
    {
        try
        {
            using var dp = OpenDeviceParametersKey(deviceInstanceId, writable: false);
            if (dp?.GetValue(ValueBInterval) is int interval && interval > 0)
                return interval;
        }
        catch { }
        return null;
    }

    private static bool IsFilterAttached(string deviceInstanceId)
    {
        try
        {
 // Registry64 explícito (consistente con el resto del Core).
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + deviceInstanceId);
            if (key?.GetValue("LowerFilters") is string[] arr)
                return arr.Any(v => v.Equals(FilterServiceName, StringComparison.OrdinalIgnoreCase));
            if (key?.GetValue("LowerFilters") is string s)
                return s.Split(SeparatorCharArray, StringSplitOptions.RemoveEmptyEntries)
                        .Any(v => v.Equals(FilterServiceName, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return false;
    }

    private static readonly char[] SeparatorCharArray = { ',', ';', ' ' };

 /// <summary>Agrega o quita el filtro en la propiedad LOWERFILTERS del devnode (igual que SETUP.EXE).</summary>
    private static void AttachFilter(uint dn, string instanceId, bool attach)
    {
        var current = new List<string>();
        uint size = 0;
        uint cr = CM_Get_DevNode_Registry_PropertyW(dn, CM_DRP_LOWERFILTERS, IntPtr.Zero, IntPtr.Zero, ref size, 0);
        if (cr == CR_BUFFER_SMALL && size >= 2)
        {
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (CM_Get_DevNode_Registry_PropertyW(dn, CM_DRP_LOWERFILTERS, IntPtr.Zero, buffer, ref size, 0) == CR_SUCCESS)
                    current = ReadMultiSz(buffer, size);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        var desired = current.Where(v => !v.Equals(FilterServiceName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (attach)
            desired.Add(FilterServiceName);

        if (desired.SequenceEqual(current))
            return;

        IntPtr native = WriteMultiSz(desired);
        try
        {
            uint byteLen = (uint)(2 * (desired.Sum(v => v.Length + 1) + 1));
            uint crSet = CM_Set_DevNode_Registry_PropertyW(dn, CM_DRP_LOWERFILTERS, native, byteLen, 0);
            if (crSet != CR_SUCCESS)
                throw new InvalidOperationException($"CM_Set_DevNode_Registry_PropertyW(LOWERFILTERS) = 0x{crSet:X8}");
        }
        finally { Marshal.FreeHGlobal(native); }
    }

    private static List<string> ReadMultiSz(IntPtr buffer, uint size)
    {
        var list = new List<string>();
        if (size < 2) return list;
        int count = (int)size / 2;
        for (int i = 0; i < count;)
        {
            int start = i;
            while (i < count && Marshal.ReadInt16(buffer, i * 2) != 0) i++;
            if (i == start) break; // doble nul = fin de la lista
            list.Add(Marshal.PtrToStringUni(buffer + start * 2, i - start));
            i++; // saltar el nul del string
        }
        return list;
    }

    private static IntPtr WriteMultiSz(List<string> values)
    {
        int charCount = values.Sum(v => v.Length + 1) + 1;
        IntPtr ptr = Marshal.AllocHGlobal(charCount * 2);
        int offset = 0;
        foreach (var v in values)
        {
            var bytes = Encoding.Unicode.GetBytes(v + "\0");
            Marshal.Copy(bytes, 0, ptr + offset, bytes.Length);
            offset += bytes.Length;
        }
        Marshal.WriteInt16(ptr + offset, 0);
        return ptr;
    }

    private static RegistryKey? OpenDeviceParametersKey(string deviceInstanceId, bool writable)
    {
        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return lm.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceInstanceId}\Device Parameters", writable);
    }

 // ===================== bInterval real (IOCTL al hub padre) =====================

 /// <summary>
 /// Lee el bInterval del endpoint de interrupción IN del dispositivo. Camina la cadena
 /// de devnodes del dispositivo hacia arriba (CM_Get_Parent) hasta encontrar el hub
 /// (el devnode que expone GUID_DEVINTERFACE_USB_HUB); el nodo que cuelga directo del
 /// hub es la "conexión" que el hub conoce por puerto. Se abre el hub y se consulta
 /// puerto por puerto IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX hasta encontrar,
 /// por driver key name, esa conexión: su PipeList trae los descriptores de endpoint
 /// de la configuración activa (bInterval real del descriptor, no el override) y la
 /// velocidad del dispositivo (necesaria para interpretar bInterval en high-speed).
 /// Esto cubre HID simples (el devnode cuelga del hub) y compuestos (la función
 /// MI_* cuelga del compuesto USB\VID&amp;PID, que es el que cuelga del hub).
 /// </summary>
    private (int Interval, byte Speed) GetInterruptInEndpointInfo(string instanceId, uint devNode)
    {
        try
        {
 // Cadena de devnodes desde el dispositivo hasta el hub (deviceId conocido).
            var chain = new List<(uint Dn, string Id)>();
            uint current = devNode;
            for (int depth = 0; depth < 8 && current != 0; depth++)
            {
                string? id = DevNodeIdOf(current);
                if (id == null) break;
                chain.Add((current, id));

 // ¿Este devnode es el hub (expone la interfaz de hub)?
                if (GetDeviceInterfacePaths(GuidDevinterfaceUsbHub, id).Length > 0)
                    break;

                uint parent = 0;
                if (CM_Get_Parent(ref parent, current, 0) != CR_SUCCESS || parent == current)
                    break;
                current = parent;
            }

 // El último de la cadena es el hub; el anterior es la conexión del hub.
            if (chain.Count < 2) return (0, 0);
            var hubNode = chain[^1];
            var childOfHub = chain[^2];

            string? hubPath = GetDeviceInterfacePaths(GuidDevinterfaceUsbHub, hubNode.Id).FirstOrDefault();
            if (hubPath == null) return (0, 0);

            using var hub = CreateFileW(hubPath, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (hub.IsInvalid) return (0, 0);

            int portCount = GetHubPortCount(hub);
            if (portCount <= 0) return (0, 0);

 // IDs a comparar con el driver key name del puerto: el nodo hijo directo del
 // hub y (por si acaso) cada ancestro intermedio del dispositivo.
            var matchIds = new List<string> { childOfHub.Id };
            for (int i = chain.Count - 3; i >= 0; i--)
                matchIds.Add(chain[i].Id);

 // VID/PID esperados del dispositivo (de los instance ids USB\VID_xxxx&PID_yyyy…).
            var expectedVidPids = new HashSet<(int Vid, int Pid)>();
            foreach (var id in matchIds)
            {
                var m = Regex.Match(id, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
                if (m.Success)
                    expectedVidPids.Add((Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16)));
            }

            for (int port = 1; port <= portCount; port++)
            {
                string? driverKey = GetConnectionDriverKeyName(hub, port);
                if (driverKey != null && matchIds.Any(id => id.Equals(driverKey, StringComparison.OrdinalIgnoreCase)))
                    return ReadConnectionBIntervalAndSpeed(hub, port);

 // En hubs raíz xHCI el driver key puede venir como "{clase}\00xx" y no
 // coincidir con el instance id; se compara entonces VID/PID del descriptor.
                if (expectedVidPids.Count > 0 && TryReadConnectionVidPid(hub, port, out int vid, out int pid)
                    && expectedVidPids.Contains((vid, pid)))
                    return ReadConnectionBIntervalAndSpeed(hub, port);
            }
            return (0, 0);
        }
        catch (Exception ex)
        {
            _logging.LogDebug($"OverclockUSB: no se pudo leer bInterval de {instanceId}: {ex.Message}");
            return (0, 0);
        }
    }

    private static string? DevNodeIdOf(uint devNode)
    {
 // Ojo: cfgmgr32.dll NO exporta CM_Get_Device_ID_SizeW (solo CM_Get_Device_ID_Size
 // y CM_Get_Device_IDA/CM_Get_Device_IDW); la variante sin sufijo devuelve la
 // longitud en caracteres, que sirve para el buffer W de CM_Get_Device_IDW.
 // Se agranda el buffer y se rellena con ceros: si el driver no escribe el NUL
 // final, PtrToStringUni igual corta en el primer cero (evita ids con basura).
        uint size = 0;
        if (CM_Get_Device_ID_Size(out size, devNode, 0) != CR_SUCCESS || size < 2)
            return null;
        if (size > 1024) size = 1024;
        var buffer = Marshal.AllocHGlobal((int)(size + 64) * 2);
        try
        {
            for (int i = 0; i < (size + 64) * 2; i++)
                Marshal.WriteByte(buffer, i, 0);
            if (CM_Get_Device_IDW(devNode, buffer, size + 64, 0) != CR_SUCCESS)
                return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string[] GetDeviceInterfacePaths(Guid interfaceClass, string? deviceId)
    {
        try
        {
            uint size = 0;
            uint cr = CM_Get_Device_Interface_List_SizeW(out size, ref interfaceClass, deviceId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (cr != CR_SUCCESS || size < 2) return Array.Empty<string>();
            var buffer = Marshal.AllocHGlobal((int)size * 2);
            try
            {
                if (CM_Get_Device_Interface_ListW(ref interfaceClass, deviceId, buffer, size, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
                    return Array.Empty<string>();
                return ReadMultiSz(buffer, size * 2).ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return Array.Empty<string>(); }
    }

 /// <summary>Cantidad de puertos del hub (USB_NODE_INFORMATION → HubDescriptor.bNumberOfPorts).</summary>
    private int GetHubPortCount(SafeFileHandle hub)
    {
        var buffer = Marshal.AllocHGlobal(HubNodeInfoBufferSize);
        try
        {
            int bytes = 0;
            if (!DeviceIoControl(hub, IOCTL_USB_GET_NODE_INFORMATION, IntPtr.Zero, 0,
                    buffer, HubNodeInfoBufferSize, ref bytes, IntPtr.Zero))
                return 0;
            return Marshal.ReadByte(buffer, HubNumberOfPortsOffset);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

 /// <summary>Driver key name del dispositivo conectado al puerto (igual al instance id del padre compuesto).</summary>
    private string? GetConnectionDriverKeyName(SafeFileHandle hub, int port)
    {
        var buffer = Marshal.AllocHGlobal(DriverKeyNameBufferSize);
        try
        {
            Marshal.WriteInt32(buffer, 0, port);
            int bytes = 0;
            if (!DeviceIoControl(hub, IOCTL_USB_GET_NODE_CONNECTION_DRIVERKEY_NAME,
                    buffer, 8, buffer, DriverKeyNameBufferSize, ref bytes, IntPtr.Zero))
                return null;
 // NodeName empieza en offset 8 (ULONG ConnectionIndex + ULONG ActualLength).
            return Marshal.PtrToStringUni(buffer + 8);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

 /// <summary>VID/PID del dispositivo conectado al puerto (USB_DEVICE_DESCRIPTOR del connection info EX).</summary>
    private bool TryReadConnectionVidPid(SafeFileHandle hub, int port, out int vid, out int pid)
    {
        vid = 0; pid = 0;
        var buffer = Marshal.AllocHGlobal(ConnInfoBufferSize);
        try
        {
            Marshal.WriteInt32(buffer, 0, port);
            int bytes = 0;
            if (!DeviceIoControl(hub, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX,
                    buffer, ConnInfoBufferSize, buffer, ConnInfoBufferSize, ref bytes, IntPtr.Zero))
                return false;
            if (bytes < 16) return false;
 // ConnectionIndex(4) + USB_DEVICE_DESCRIPTOR: idVendor@+8, idProduct@+10.
            vid = Marshal.ReadInt16(buffer, 4 + 8) & 0xFFFF;
            pid = Marshal.ReadInt16(buffer, 4 + 10) & 0xFFFF;
            return vid != 0 || pid != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

 /// <summary>
 /// bInterval del primer endpoint de interrupción IN y velocidad del dispositivo
 /// (USB_NODE_CONNECTION_INFORMATION_EX: Speed en el offset 23 del buffer pack(1)).
 /// Devuelve (0, 0) si no hay datos; (0, speed) si el dispositivo no expone pipes.
 /// </summary>
    private (int Interval, byte Speed) ReadConnectionBIntervalAndSpeed(SafeFileHandle hub, int port)
    {
        var buffer = Marshal.AllocHGlobal(ConnInfoBufferSize);
        try
        {
            Marshal.WriteInt32(buffer, 0, port);
            int bytes = 0;
            if (!DeviceIoControl(hub, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX,
                    buffer, ConnInfoBufferSize, buffer, ConnInfoBufferSize, ref bytes, IntPtr.Zero))
                return (0, 0);
            if (bytes < 16) return (0, 0);

 // Layout pack(1): 22 = CurrentConfigurationValue, 23 = Speed.
            byte speed = bytes >= 24 ? Marshal.ReadByte(buffer, 23) : (byte)0;

            int pipeCount = Math.Min(Marshal.ReadInt32(buffer, 27), MaxPipes);
            for (int i = 0; i < pipeCount; i++)
            {
                int p = ConnInfoHeaderSize + i * UsbPipeInfoSize;
 // USB_PIPE_INFO (pack(1)): ScheduleOffset ULONG + USB_ENDPOINT_DESCRIPTOR(7):
 // bEndpointAddress@+6, bmAttributes@+7, bInterval@+10.
                byte endpointAddress = Marshal.ReadByte(buffer, p + 6);
                byte attributes = Marshal.ReadByte(buffer, p + 7);
                byte bInterval = Marshal.ReadByte(buffer, p + 10);
 // Interrupt IN: bmAttributes 0x03 y dirección IN (bit 7 de bEndpointAddress).
                if ((attributes & 0x03) == 0x03 && (endpointAddress & 0x80) != 0 && bInterval > 0)
                    return (bInterval, speed);
            }
            return (0, speed);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private sealed record UsbTreeNode(uint Dn, string Id, string Service, string DeviceDesc, string FriendlyName, string? HidUsage);

    /// <summary>
    /// ¿El nodo lo enumeró la pila de Bluetooth? Los enumeradores de Windows para eso empiezan
    /// todos con "BTH": BTHENUM (clásico), BTHLE (baja energía), BTHHFENUM, BTHPAN… Ver
    /// WalkSubtree: un dispositivo con esas características no es una función del bus USB aunque
    /// cuelgue del árbol de un adaptador USB.
    /// </summary>
    private static bool IsBluetoothEnumerated(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return false;
        int sep = instanceId.IndexOf('\\');
        string enumerator = sep > 0 ? instanceId[..sep] : instanceId;
        return enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ¿El producto es un ADAPTADOR (radio) Bluetooth? Es la placa USB que transporta el
    /// Bluetooth: un dispositivo USB real, con su endpoint de interrupción, pero SIN ninguna
    /// interfaz HID adentro — por eso la calificación por mouse/teclado/HID no lo veía.
    /// Se reconoce por dos vías, y cada una cubre un caso distinto: la CLASE del dispositivo
    /// ("Bluetooth", la declara el INF del adaptador: así entran las radios con transporte
    /// propio, como las Intel con ibtusb) y el driver de transporte BTHUSB de Microsoft (que es
    /// el que usa la mayoría). No se mira lo emparejado —los periféricos Bluetooth cuelgan de
    /// otro enumerador y no son dispositivos USB— ni el nombre, que cambia por idioma y modelo.
    /// Lo que se ajusta acá es el sondeo de la RADIO (el transporte), no el del periférico.
    /// Las dos señales se miran SOLO en el nodo del producto: la radio es de función única, así
    /// que mirar sus descendientes hacía que un concentrador raíz se diera por radio cuando el
    /// adaptador cuelga de él (el árbol de un hub incluye lo que tiene enchufado).
    /// </summary>
    private static bool IsBluetoothRadio(uint productDn, string productSvc)
    {
        if (productSvc.Equals("BTHUSB", StringComparison.OrdinalIgnoreCase)) return true;
        string? cls = GetStringProperty(productDn, CM_DRP_CLASS);
        return cls != null && cls.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>¿El nodo es un concentrador (hub)?: el driver de hub de Windows, propio o de
    /// terceros. No se puede ajustar el sondeo de un hub ni tiene sentido listarlo.</summary>
    private static bool IsHubSvc(string svc)
        => svc.Equals("usbhub", StringComparison.OrdinalIgnoreCase)
           || svc.Equals("USBHUB3", StringComparison.OrdinalIgnoreCase);

 /// <summary>
 /// Recorre BFS los descendientes del devnode (todas las clases: HID, audio…,
 /// hasta maxDepth niveles) sin bajar por compuestos (usbccgp) ni hubs (usbhub)
 /// ajenos — así un hub no "hereda" los hijos de los dispositivos que cuelgan de él.
 /// </summary>
    private static List<UsbTreeNode> WalkSubtree(uint rootDn, int maxDepth)
    {
        var result = new List<UsbTreeNode>();
        var queue = new Queue<(uint Dn, int Depth)>();
        queue.Enqueue((rootDn, 0));
        var visited = new HashSet<uint>();
        while (queue.Count > 0)
        {
            var (dn, depth) = queue.Dequeue();
            if (!visited.Add(dn)) continue;
            if (depth >= maxDepth) continue;

            uint child = 0;
            if (CM_Get_Child(ref child, dn, 0) != CR_SUCCESS || child == 0)
                continue;

            uint current = child;
            while (current != 0)
            {
                string? svc = GetStringProperty(current, CM_DRP_SERVICE);
                string? id = DevNodeIdOf(current);

 // Un nodo enumerado por BLUETOOTH no es una función del bus USB. El adaptador
 // Bluetooth SÍ es un dispositivo USB, pero lo que se le conecta (un mando, un
 // mouse, un teclado) cuelga de la pila Bluetooth: no tiene endpoint USB, no tiene
 // bInterval que el filtro pueda tocar y su enlace no es USB. Incluirlo en el
 // recorrido hacía que el ADAPTADOR apareciera como si fuera el periférico —el
 // desplegable lo mostraba con el emoji del mando emparejado y con el transporte
 // USB del adaptador—. Se corta acá: ni se agrega ni se sigue bajando.
                if (!IsBluetoothEnumerated(id))
                {
                    result.Add(new UsbTreeNode(
                        current,
                        id ?? "",
                        svc ?? "",
                        GetStringProperty(current, CM_DRP_DEVICEDESC) ?? "",
                        GetStringProperty(current, CM_DRP_FRIENDLYNAME) ?? "",
                        id == null ? null : ReadHidUsage(id)));

 // No seguir bajando por compuestos ni hubs (sus hijos son de otros nodos).
                    if (svc == null
                        || (!svc.Equals("usbccgp", StringComparison.OrdinalIgnoreCase)
                            && !svc.Equals("usbhub", StringComparison.OrdinalIgnoreCase)))
                        queue.Enqueue((current, depth + 1));
                }

                uint sibling = 0;
                if (CM_Get_Sibling(ref sibling, current, 0) != CR_SUCCESS || sibling == 0)
                    break;
                current = sibling;
            }
        }
        return result;
    }

 /// <summary>
 /// Usage HID (usage page + usage) del descriptor del dispositivo, leído de los
 /// hardware IDs del registro ("HID_DEVICE_UP:0001_U:0002" / "HID\VID_...&UP:0001_U:0002").
 /// Es el estándar universal del descriptor HID: "UP:0001 U:0002" es un mouse en
 /// CUALQUIER idioma del sistema (el nombre localizado no hace falta parsearlo).
 /// Devuelve "XXXX:YYYY" o null si no se puede determinar.
 /// </summary>
    private static string? ReadHidUsage(string instanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            if (key == null) return null;
            foreach (string valueName in new[] { "HardwareID", "CompatibleIDs" })
            {
                if (key.GetValue(valueName) is not string[] values) continue;
                foreach (string v in values)
                {
                    var m = Regex.Match(v, @"UP:([0-9A-Fa-f]{4})_U:([0-9A-Fa-f]{4})");
                    if (m.Success)
                        return $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}";
                }
            }
        }
        catch { /* sin acceso al registro del devnode: queda sin usage */ }
        return null;
    }

 /// <summary>
 /// Tipo funcional según el usage HID estándar (Generic Desktop / Game Controls /…):
 /// mouse (UP:0001 U:0002), teclado (U:0006), mando/volante (U:0004 joystick, U:0005
 /// gamepad, U:0008 multi-axis, o usage page 0x05). Consumer (UP:000C = volumen/media),
 /// System Control (U:0080+) y Vendor (FF00+) NO son mandos: se devuelve null.
 /// </summary>
    private static UsbDeviceKind? KindFromHidUsage(string usage)
    {
        var parts = usage.Split(':');
        if (parts.Length != 2) return null;
        ushort up = Convert.ToUInt16(parts[0], 16);
        ushort u = Convert.ToUInt16(parts[1], 16);
        if (up == 0x0001) // Generic Desktop
        {
            return u switch
            {
                0x0002 => UsbDeviceKind.Mouse,
                0x0006 or 0x0007 => UsbDeviceKind.Keyboard, // 0x07 = keypad
                0x0004 or 0x0005 or 0x0008 => UsbDeviceKind.Controller, // joystick/gamepad/multi-axis
                _ => null // System Control (0x80+), generic desktop, etc.
            };
        }
        if (up == 0x0005) return UsbDeviceKind.Controller; // Game Controls
        return null; // Consumer (0x000C), Vendor (FF00+), otros
    }

 /// <summary>
 /// Fabricantes cuyo VID pertenece a mandos (usb.org asigna VID por empresa; la
 /// misma técnica que usan Steam/reWASD para reconocer mandos): 8BitDo (2DC8),
 /// Sony (054C), Nintendo (057E), HORI (0F0D), NVIDIA Shield (0955), PowerA (20D6),
 /// PDP (1BAD), Mad Catz (0738), DragonRise genéricos (0079). Un 8BitDo se ve como
 /// "Dispositivo de teclado HID" en Windows (así lo declara su firmware), pero es un
 /// mando: el VID lo delata. NO están Microsoft (045E), Razer ni SINO WEALTH (258A,
 /// teclados OEM Redragon/Dareu): hacen teclados reales.
 /// </summary>
    private static readonly HashSet<string> GamepadVendorIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "2DC8",         // 8BitDo
        "054C",         // Sony
        "057E",         // Nintendo
        "0F0D",         // HORI
        "0955",         // NVIDIA Shield
        "20D6",         // PowerA
        "1BAD",         // PDP
        "0738",         // Mad Catz
        "0079"          // DragonRise (mandos genéricos)
    };

    private static bool IsGamepadVendor(string productId)
    {
        var m = Regex.Match(productId, @"VID_([0-9A-Fa-f]{4})");
        return m.Success && GamepadVendorIds.Contains(m.Groups[1].Value);
    }

 /// <summary>Driver de una interfaz HID (HidUsb / hidclass / xusb…), donde se cuelga el filtro.</summary>
    private static bool IsHidInterfaceSvc(string svc)
        => svc.StartsWith("hid", StringComparison.OrdinalIgnoreCase)
           || svc.StartsWith("xusb", StringComparison.OrdinalIgnoreCase);

    private static bool IsMouseSvc(string svc)
        => svc.StartsWith("mou", StringComparison.OrdinalIgnoreCase);

    private static bool IsKeyboardSvc(string svc)
        => svc.StartsWith("kbd", StringComparison.OrdinalIgnoreCase);

/// <summary>
 /// Funciones reales del producto, ordenadas por prioridad (mouse > teclado > audio
 /// > otro): cada una con su nombre visible (como el filtro muestra su columna Child)
 /// y su tipo. El desplegable de la grilla elige qué función mostrar de cada fila;
 /// el "mejor hijo" (BestChild) es simplemente la primera de esta lista.
 /// La clasificación usa el USAGE HID del descriptor (independiente del idioma del
 /// SO): UP:0001_U:0002 = mouse, U:0006 = teclado, U:0004/0005/0008 o UP:0005 =
 /// mando. Consumer (UP:000C, el control de volumen de los auriculares) y Vendor
 /// (UP:FF00+) NO son mandos — se omiten. Sin usage, cae al driver (mouhid/kbdhid).
 /// </summary>
    private static List<UsbChildFunction> AllChildren(List<UsbTreeNode> nodes)
    {
        var result = new List<UsbChildFunction>();
        foreach (var n in nodes)
        {
            UsbDeviceKind? kind = null;
            if (n.Id.StartsWith(@"SWD\MMDEVAPI\", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(n.FriendlyName)) kind = UsbDeviceKind.Audio;
            else if (n.HidUsage is { } usage) kind = KindFromHidUsage(usage);
            else if (IsMouseSvc(n.Service)) kind = UsbDeviceKind.Mouse;
            else if (IsKeyboardSvc(n.Service)) kind = UsbDeviceKind.Keyboard;
            else if (n.Service.StartsWith("xusb", StringComparison.OrdinalIgnoreCase)) kind = UsbDeviceKind.Controller; // mandos X-input
            else continue;

 // Consumer/Vendor/System Control (usage no clasificable) o nodo genérico:
 // no aportan una función a la fila (el control de volumen de los auriculares
 // no es un mando).
            if (kind == null) continue;

            string name = kind == UsbDeviceKind.Audio
                ? $"{n.DeviceDesc} {n.FriendlyName}".Trim()
                : !string.IsNullOrEmpty(n.FriendlyName) ? n.FriendlyName : n.DeviceDesc;
            if (string.IsNullOrWhiteSpace(name)) continue;
            result.Add(new UsbChildFunction(name, kind.Value));
        }

 // Prioridad igual que el BestChild clásico: mouse > teclado > audio > otro.
 // El orden del árbol NO es confiable (el nombre del producto puede aparecer
 // antes que el mouse), así que se ordena explícitamente y se dedupe por tipo
 // quedándose con la primera (la de mayor prioridad).
        static int RankOf(UsbDeviceKind k) => k switch
        {
            UsbDeviceKind.Mouse => 0,
            UsbDeviceKind.Keyboard => 1,
            UsbDeviceKind.Audio => 2,
            _ => 3
        };
        var seen = new HashSet<UsbDeviceKind>();
        return result.OrderBy(f => RankOf(f.Kind)).Where(f => seen.Add(f.Kind)).ToList();
    }

 /// <summary>"Nombre hijo" como el filtro: la función de mayor prioridad del producto.</summary>
    private static (string Name, UsbDeviceKind Kind) BestChild(List<UsbTreeNode> nodes)
    {
        var all = AllChildren(nodes);
        if (all.Count == 0) return ("", UsbDeviceKind.Unknown);
        return (all[0].Name, all[0].Kind);
    }

 /// <summary>
 /// Nombre visible del producto: FriendlyName del propio nodo si existe; si no, el
 /// DeviceDesc del compuesto; y si es el genérico "Dispositivo compuesto USB", el
 /// nombre de la primera interfaz HID ("Dispositivo de entrada USB").
 /// </summary>
    private static string BestProductName(uint productDn, List<UsbTreeNode> nodes)
    {
        string? friendly = GetStringProperty(productDn, CM_DRP_FRIENDLYNAME);
        if (!string.IsNullOrWhiteSpace(friendly)) return friendly;

        string? desc = GetStringProperty(productDn, CM_DRP_DEVICEDESC);
        if (!string.IsNullOrWhiteSpace(desc)
            && !desc.Equals("USB Composite Device", StringComparison.OrdinalIgnoreCase)
            && !desc.Equals("Dispositivo compuesto USB", StringComparison.OrdinalIgnoreCase))
            return desc;

        var iface = nodes.FirstOrDefault(n => IsHidInterfaceSvc(n.Service));
        if (iface != null && !string.IsNullOrEmpty(iface.DeviceDesc))
            return iface.DeviceDesc;
        return desc ?? "";
    }

 /// <summary>Interfaces HID de un producto (donde se aplica el filtro + override de bInterval).</summary>
    private static List<string> InterfaceIdsOf(uint productDn, string productId)
    {
        var ids = new List<string>();
        string? svc = GetStringProperty(productDn, CM_DRP_SERVICE);
        if (IsHidInterfaceSvc(svc ?? ""))
        {
            ids.Add(productId);
            return ids;
        }

        foreach (var n in WalkSubtree(productDn, maxDepth: 4))
            if (IsHidInterfaceSvc(n.Service) && !string.IsNullOrEmpty(n.Id))
                ids.Add(n.Id);
        return ids;
    }

 /// <summary>
 /// True si la controladora de host donde cuelga el dispositivo usa el driver xHCI de
 /// Microsoft (requisito del autor para los builds 2k/4k/8k: "the microsoft usb 3.x
 /// driver"). Se resuelve con la MISMA caminata que FindHostController: el servicio
 /// del nodo PCI de la controladora debe ser usbxhci (el genérico de Microsoft).
 /// Si no se encuentra la controladora se devuelve false (honesto: no afirmar
 /// compatibilidad que no se pudo verificar).
 /// </summary>
    private static bool IsMicrosoftUsb3Controller(string deviceInstanceId)
    {
        try
        {
            uint dn = GetDevNode(deviceInstanceId);
            if (dn == 0) return false;
            uint current = dn;
            for (int depth = 0; depth < 16; depth++)
            {
                uint parent = 0;
                if (CM_Get_Parent(ref parent, current, 0) != CR_SUCCESS || parent == current || parent == 0)
                    break;
                current = parent;

                string? id = DevNodeIdOf(current);
                string? svc = GetStringProperty(current, CM_DRP_SERVICE);
                if (id != null && id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(svc))
                {
                    return svc.Contains("xhci", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch { }
        return false;
    }

 /// <summary>
 /// Sube por la cadena de devnodes del dispositivo hasta encontrar la controladora
 /// de host USB (xHCI) — el nodo PCI con driver usbxhci/amd_xhci/etc. que cuelga
 /// del hub raíz — y devuelve su nombre visible y su instance id, como muestra
 /// el filtro en su columna Controller. Si no la encuentra, ("", "").
 /// </summary>
    private static (string Name, string Id) FindHostController(uint devNode)
    {
        uint current = devNode;
        for (int depth = 0; depth < 16; depth++)
        {
            uint parent = 0;
            if (CM_Get_Parent(ref parent, current, 0) != CR_SUCCESS || parent == current || parent == 0)
                break;
            current = parent;

            string? id = DevNodeIdOf(current);
            string? svc = GetStringProperty(current, CM_DRP_SERVICE);
            bool isControllerNode = id != null
                && id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                && (!string.IsNullOrEmpty(svc)
                    && (svc.Contains("xhci", StringComparison.OrdinalIgnoreCase)
                        || svc.Contains("ehci", StringComparison.OrdinalIgnoreCase)
                        || svc.StartsWith("usb", StringComparison.OrdinalIgnoreCase)));

            if (isControllerNode)
            {
                string name = GetStringProperty(current, CM_DRP_FRIENDLYNAME)
                              ?? GetStringProperty(current, CM_DRP_DEVICEDESC)
                              ?? id!;
                return (name, id!);
            }
        }
        return ("", "");
    }

 // ===================== cfgmgr32 helpers =====================

    private static uint GetDevNode(string instanceId)
    {
        uint dn = 0;
        return CM_Locate_DevNodeW(ref dn, instanceId, 0) == CR_SUCCESS ? dn : 0;
    }

    private static string? GetStringProperty(uint dn, uint property)
    {
        uint size = 0;
        uint cr = CM_Get_DevNode_Registry_PropertyW(dn, property, IntPtr.Zero, IntPtr.Zero, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size < 2) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (CM_Get_DevNode_Registry_PropertyW(dn, property, IntPtr.Zero, buffer, ref size, 0) != CR_SUCCESS)
                return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static List<string> QueryDeviceIds(string enumerator)
    {
        uint size = 0;
        if (CM_Get_Device_ID_List_SizeW(out size, enumerator, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS || size < 2)
            return new List<string>();
        var buffer = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            if (CM_Get_Device_ID_ListW(enumerator, buffer, size, CM_GETIDLIST_FILTER_ENUMERATOR) != CR_SUCCESS)
                return new List<string>();
            return ReadMultiSz(buffer, size * 2).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

 // ===================== P/Invoke =====================

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint CM_Locate_DevNodeW(ref uint pdnDevInst, string pDeviceID, uint ulFlags);

 // Sin sufijo A/W (no existe SizeW en cfgmgr32): devuelve la longitud en caracteres.
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint dnDevInst, IntPtr buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszEnumerator, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string pszEnumerator, IntPtr buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint pulLen, ref Guid interfaceClass, string? pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? pDeviceID, IntPtr buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(ref uint pdnParent, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Child(ref uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Sibling(ref uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, IntPtr pulRegDataType, IntPtr buffer, ref uint pulLength, uint ulFlags);

    // OJO: a diferencia de la variante Get, la Set NO recibe "pulRegDataType": la firma
    // real es (dnDevInst, ulProperty, Buffer, ulLength, ulFlags). Declarar el parámetro
    // extra (copiado de la Get) corría todos los argumentos un lugar y el driver recibía
    // Buffer = NULL → CR_INVALID_POINTER (0x3): colgar el filtro fallaba SIEMPRE.
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Set_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty, IntPtr buffer, uint ulLength, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(ref uint pulStatus, ref uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    // ===== setupapi: reiniciar un devnode (DIF_PROPERTYCHANGE / DICS_PROPCHANGE) =====
    // Es el mismo "reiniciar dispositivo" que pnputil /restart-device: detiene y vuelve a
    // arrancar la pila del devnode, que es lo único que obliga a PnP a releer LowerFilters
    // y cargar el filtro recién agregado. CM_Reenumerate_DevNode NO alcanza para eso.
    private const uint DIF_PROPERTYCHANGE = 0x00000012;
    private const uint DICS_PROPCHANGE = 0x00000003;
    private const uint DICS_FLAG_GLOBAL = 0x00000001;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoListExW(IntPtr classGuid, IntPtr hwndParent, string? machineName, IntPtr reserved);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr deviceInfoSet, string deviceInstanceId,
        IntPtr hwndParent, uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParamsW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_PROPCHANGE_PARAMS classInstallParams, uint classInstallParamsSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, ref int lpBytesReturned, IntPtr lpOverlapped);

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

 // ===== Service Control Manager =====
    private static IntPtr _scManager;
    private static IntPtr ScManagerHandle
    {
        get
        {
            if (_scManager == IntPtr.Zero)
                _scManager = OpenSCManagerW(null, null, ScManagerAllAccess);
            return _scManager;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateServiceW(IntPtr hSCManager, string lpServiceName, string lpDisplayName, uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName, string? lpLoadOrderGroup, IntPtr lpdwTagId, string? lpDependencies, string? lpServiceStartName, string? lpPassword);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_DESCRIPTIONW { public string? lpDescription; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(IntPtr hService, uint dwInfoLevel, ref SERVICE_DESCRIPTIONW lpInfo);

    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceDelete = 0x00010000;
    private const uint ServiceAccessAll = 0xF01FF;
    private const uint ServiceKernelDriver = 0x00000001;
    private const uint ServiceDemandStart = 3;
    private const uint ServiceErrorNormal = 1;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint SERVICE_CONFIG_DESCRIPTION = 1;

    private const string FilterServiceName = "hidusbf";
    private const string ValueBInterval = "bInterval";
}
