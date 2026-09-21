using System.Runtime.InteropServices;
using WHPO.Core.Services.Interfaces;

namespace WHPO_UI.Services;

/// <summary>
/// Reemplazo integrado de WLAN Optimizer, con el MISMO mecanismo que la herramienta
/// original: la API nativa wlanapi.dll. Bloquea el escaneo de fondo de la red Wi-Fi
/// mientras estás conectado (opcode background_scan_enabled = 0) y el modo streaming
/// (opcode media_streaming_mode). Como Windows/driver re-habilitan el escaneo solos,
/// hay un timer keep-alive que re-aplica cada 5 s mientras la app está abierta.
/// </summary>
internal sealed class WlanOptimizerService : IDisposable
{
    private const int OpBackgroundScan = 3;   // wlan_intf_opcode_background_scan_enabled
    private const int OpMediaStreaming = 4;   // wlan_intf_opcode_media_streaming_mode

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanSetInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, int OpCode, uint dwDataSize, IntPtr pData, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanScan(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pDot11Ssid, IntPtr pIeData, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    private readonly ILoggingService _logging;
    private IntPtr _hClient;
    private System.Threading.Timer? _timer;
    private Guid _guid;
    private bool _blockScan;
    private bool _streaming;
    private int _failCount;
    private int _applies;

    /// <summary>
    /// Algunos drivers (ej: Realtek RTL8811AU USB) devuelven ERROR_INVALID_PARAMETER
    /// para el opcode de media streaming — el adaptador no lo soporta. La UI lo usa
    /// para avisar en vez de dejar el toggle mudo.
    /// </summary>
    public bool MediaStreamingSupported { get; private set; } = true;

    /// <summary>Estado deseado del bloqueo de escaneo (persiste entre navegaciones:
    /// el servicio es singleton, la página se recrea).</summary>
    public bool BlockScanActive { get; private set; }

    /// <summary>Estado deseado del modo streaming (persiste entre navegaciones).</summary>
    public bool StreamingActive { get; private set; }

    public WlanOptimizerService(ILoggingService logging)
    {
        _logging = logging;
        Open();
    }

    public void Dispose()
    {
        StopKeepAlive();
        Close();
    }

    private bool Open()
    {
        if (_hClient != IntPtr.Zero) return true;
        try
        {
            return WlanOpenHandle(2, IntPtr.Zero, out _, out _hClient) == 0 && _hClient != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            _logging.LogError($"WlanOptimizer: no se pudo abrir wlanapi: {ex.Message}", ex);
            return false;
        }
    }

    private void Close()
    {
        if (_hClient != IntPtr.Zero)
        {
            try { WlanCloseHandle(_hClient, IntPtr.Zero); }
            catch { /* el handle ya no es válido */ }
            _hClient = IntPtr.Zero;
        }
    }

    /// <summary>Adaptador Wi-Fi detectado (descripción + GUID + estado). Vacío si no hay.</summary>
    public sealed record WlanAdapterInfo(Guid Guid, string Description, int State);

    /// <summary>Enumera los adaptadores Wi-Fi (vacío si la máquina no tiene).</summary>
    public List<WlanAdapterInfo> GetAdapters()
    {
        var result = new List<WlanAdapterInfo>();
        if (!Open()) return result;
        try
        {
            if (WlanEnumInterfaces(_hClient, IntPtr.Zero, out var listPtr) != 0 || listPtr == IntPtr.Zero)
                return result;

            const int guidSize = 16, descChars = 256, stateSize = 4;
            int stride = guidSize + descChars * 2 + stateSize;
            try
            {
                int count = Marshal.ReadInt32(listPtr);
                IntPtr first = listPtr + 8; // dwNumberOfItems(4) + dwIndex(4)
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = first + i * stride;
                    var guid = Marshal.PtrToStructure<Guid>(entry);
                    string desc = Marshal.PtrToStringUni(entry + guidSize, descChars)?.TrimEnd('\0') ?? "";
                    int state = Marshal.ReadInt32(entry + guidSize + descChars * 2);
                    result.Add(new WlanAdapterInfo(guid, desc, state));
                }
            }
            finally
            {
                WlanFreeMemory(listPtr);
            }
        }
        catch (Exception ex)
        {
            _logging.LogError($"WlanOptimizer: error enumerando adaptadores: {ex.Message}", ex);
        }
        return result;
    }

    /// <summary>Bloquea (enabled=false) o habilita (true) el escaneo de fondo.</summary>
    public bool SetBackgroundScan(Guid guid, bool enabled)
    {
        if (!Open()) return false;
        try { return SetBoolOpcode(_hClient, guid, OpBackgroundScan, enabled); }
        catch { return false; }
    }

    public bool SetMediaStreaming(Guid guid, bool enabled)
    {
        if (!Open()) return false;
        try
        {
            bool ok = SetBoolOpcode(_hClient, guid, OpMediaStreaming, enabled);
            if (!ok) MediaStreamingSupported = false;
            return ok;
        }
        catch { return false; }
    }

    /// <summary>Fuerza un escaneo manual (el bloqueo deja la lista de redes vieja).</summary>
    public bool ScanNow(Guid guid)
    {
        if (!Open()) return false;
        try { return WlanScan(_hClient, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0; }
        catch { return false; }
    }

    /// <summary>
    /// Arranca el keep-alive: aplica ya y re-aplica cada 5 s. blockScan=true bloquea
    /// el escaneo de fondo; streaming=true además activa el modo streaming.
    /// </summary>
    public void StartKeepAlive(Guid guid, bool blockScan, bool streaming)
    {
        StopKeepAlive();
        _guid = guid;
        _blockScan = blockScan;
        _streaming = streaming;
        BlockScanActive = blockScan;
        StreamingActive = streaming;
        ApplyOnce();
        _timer = new System.Threading.Timer(_ => ApplyOnce(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void StopKeepAlive()
    {
        _timer?.Dispose();
        _timer = null;
        BlockScanActive = false;
        StreamingActive = false;
    }

    /// <summary>
    /// Restaura el adaptador (escaneo de fondo y streaming a sus valores normales).
    /// Se usa al desactivar los toggles o al cerrar la app.
    /// </summary>
    public void RestoreDefaults(Guid guid)
    {
        StopKeepAlive();
        SetBackgroundScan(guid, true);
        SetMediaStreaming(guid, false);
    }

    private void ApplyOnce()
    {
        try
        {
            // El bloqueo del escaneo de fondo es el que importa para el keep-alive:
            // falla si el adaptador no está en un estado válido (ej: re-habilitarlo
            // estando desconectado da ERROR_INVALID_STATE). El modo streaming NO
            // cuenta como fallo: hay drivers (RTL8811AU) que lo rechazan siempre.
            bool ok = _blockScan ? SetBackgroundScan(_guid, false) : SetBackgroundScan(_guid, true);
            if (_streaming && !SetMediaStreaming(_guid, true))
                MediaStreamingSupported = false;
            _failCount = ok ? 0 : _failCount + 1;
            _applies++;
            // Log de éxito cada ~60 s (12 ticks × 5 s): permite verificar desde el log
            // que el keep-alive sigue activo aunque la pestaña Red esté cerrada.
            if (!ok && _failCount % 20 == 1)
                _logging.LogWarning($"WlanOptimizer: {_failCount} re-aplicaciones fallidas (¿adaptador desconectado?)");
            else if (ok && _applies % 12 == 1)
                _logging.LogInfo($"WlanOptimizer: keep-alive activo (blockScan={_blockScan}, streaming={_streaming})");
        }
        catch (Exception ex)
        {
            _failCount++;
            if (_failCount % 20 == 1)
                _logging.LogError($"WlanOptimizer: error en keep-alive: {ex.Message}", ex);
        }
    }

    private static bool SetBoolOpcode(IntPtr hClient, Guid guid, int opcode, bool value)
    {
        IntPtr data = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(data, value ? 1 : 0);
            return WlanSetInterface(hClient, ref guid, opcode, 4, data, IntPtr.Zero) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

}
