using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WHPO.Core.Services;

/// <summary>
/// Interop de WASAPI: lo mínimo para enumerar endpoints, medir la latencia del motor y
/// hacer una ida y vuelta de sonido (loopback). Está aparte del servicio que mide para que
/// las declaraciones COM (que son frágiles: el ORDEN de los métodos es la vtable) queden
/// en un solo lugar y no mezcladas con la lógica.
///
/// Todo va con [PreserveSig] y devuelve HRESULT en vez de lanzar: así una placa que no
/// soporta algo se informa como "no se pudo", no como una excepción en medio de la medición.
/// </summary>
internal static class WasapiAudio
{
    // ---------------------------------------------------------------------
    // Constantes
    // ---------------------------------------------------------------------
    public const int EDataFlowRender = 0;
    public const int EDataFlowCapture = 1;
    public const int ERoleConsole = 0;

    public const int DeviceStateActive = 0x00000001;
    public const int ClsCtxAll = 0x17;
    public const int StgmRead = 0;

    public const uint ShareModeShared = 0;
    public const uint ShareModeExclusive = 1;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsNoPersist = 0x00080000;
    public const uint BufferFlagsSilent = 0x2;
    public const uint BufferFlagsDataDiscontinuity = 0x1;

    public const int COINIT_MULTITHREADED = 0x0;
    public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    public const int S_OK = 0;
    public const int S_FALSE = 1;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    private static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid IID_IAudioClock = new("CD63314F-3FBA-4A1F-812E-EB96EC4DFB8B");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName =
        new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsCtx, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    // ---- Eventos de búfer (AUDCLNT_STREAMFLAGS_EVENTCALLBACK) ----
    // El motor de audio avisa con este evento cada vez que libera espacio en el búfer: es
    // lo que permite medir su CADENCIA real (cada cuánto pide datos) en vez de deducirla.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset,
        bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 258;

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    /// <summary>
    /// Inicializa COM en el hilo actual y dice si hay que desinicializar. Devuelve false si
    /// el hilo ya estaba en otro apartment (RPC_E_CHANGED_MODE): en ese caso se puede seguir
    /// usando COM, pero no corresponde llamar a CoUninitialize.
    /// </summary>
    public static bool TryInitCom()
    {
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        return hr == S_OK || hr == S_FALSE;
    }

    /// <summary>Crea el enumerador de dispositivos de audio (null si no se pudo).</summary>
    public static IMMDeviceEnumerator? CreateEnumerator()
    {
        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_IMMDeviceEnumerator;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out var instance);
        if (hr < 0 || instance is not IMMDeviceEnumerator enumerator) return null;
        return enumerator;
    }

    /// <summary>Id del endpoint predeterminado para un sentido, o null.</summary>
    public static string? GetDefaultEndpointId(IMMDeviceEnumerator enumerator, int dataFlow)
    {
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(dataFlow, ERoleConsole, out var device) < 0 || device == null) return null;
            return device.GetId(out var id) >= 0 ? id : null;
        }
        catch { return null; }
    }

    public static string? ReadFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(StgmRead, out var store) < 0 || store == null) return null;
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var value) < 0) return null;
            try
            {
                // Solo se espera VT_LPWSTR (31). Cualquier otro tipo se ignora en vez de
                // interpretar el puntero como texto (eso sería leer memoria ajena).
                if (value.vt != 31 || value.data == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(value.data);
            }
            finally { PropVariantClear(ref value); }
        }
        catch { return null; }
    }

    /// <summary>Activa el cliente de audio de un endpoint (null si no se pudo).</summary>
    public static IAudioClient? ActivateClient(IMMDevice device)
    {
        try
        {
            var iid = IID_IAudioClient;
            int hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var raw);
            return hr < 0 ? null : raw as IAudioClient;
        }
        catch { return null; }
    }

    /// <summary>El mismo cliente visto como IAudioClient3 (null si el sistema no lo expone:
    /// IAudioClient3 existe desde Windows 10, pero un driver puede no soportarlo).</summary>
    public static IAudioClient3? AsClient3(IAudioClient client) => client as IAudioClient3;

    public static IAudioRenderClient? GetRenderClient(IAudioClient client)
    {
        try
        {
            var iid = IID_IAudioRenderClient;
            return client.GetService(ref iid, out var ptr) < 0 || ptr == IntPtr.Zero
                ? null
                : Marshal.GetObjectForIUnknown(ptr) as IAudioRenderClient;
        }
        catch { return null; }
    }

    public static IAudioCaptureClient? GetCaptureClient(IAudioClient client)
    {
        try
        {
            var iid = IID_IAudioCaptureClient;
            return client.GetService(ref iid, out var ptr) < 0 || ptr == IntPtr.Zero
                ? null
                : Marshal.GetObjectForIUnknown(ptr) as IAudioCaptureClient;
        }
        catch { return null; }
    }

    public static IAudioClock? GetClock(IAudioClient client)
    {
        try
        {
            var iid = IID_IAudioClock;
            return client.GetService(ref iid, out var ptr) < 0 || ptr == IntPtr.Zero
                ? null
                : Marshal.GetObjectForIUnknown(ptr) as IAudioClock;
        }
        catch { return null; }
    }

    /// <summary>Lee el WAVEFORMATEX que devuelve WASAPI (memoria del sistema: la libera quien llama).</summary>
    public static bool TryReadFormat(IntPtr format, out WAVEFORMATEX result)
    {
        result = default;
        if (format == IntPtr.Zero) return false;
        try
        {
            result = Marshal.PtrToStructure<WAVEFORMATEX>(format);
            return result.nSamplesPerSec > 0 && result.nChannels > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// True si el formato es de coma flotante de 32 bits. El mix de WASAPI casi siempre lo
    /// es, pero puede venir como PCM de 16: hay que mirar el SUBFORMATO cuando el tag es
    /// WAVE_FORMAT_EXTENSIBLE (0xFFFE), porque ahí el tag solo dice "extensible".
    /// </summary>
    public static bool IsFloat32(IntPtr format)
    {
        try
        {
            var wf = Marshal.PtrToStructure<WAVEFORMATEX>(format);
            if (wf.wFormatTag == 3) return true;
            if (wf.wFormatTag != 0xFFFE) return false;
            if (wf.cbSize < 22) return false;
            // WAVEFORMATEXTENSIBLE: 18 bytes de WAVEFORMATEX + wValidBits(2) + dwChannelMask(4) = 24
            var sub = Marshal.PtrToStructure<Guid>(format + 24);
            return sub == SubtypeIeeeFloat;
        }
        catch { return false; }
    }

    /// <summary>
    /// Genera el impulso que se reproduce para medir la ida y vuelta: un tono corto con
    /// envolvente de Hann. Es corto a propósito (una ráfaga larga emborrona el comienzo) y
    /// con envolvente para que no haga "clic" de banda ancha, que ensuciaría la detección.
    /// </summary>
    public static float[] BuildImpulse(int sampleRate, double frequencyHz = 2000, double milliseconds = 2, double amplitude = 0.6)
    {
        int samples = Math.Max(8, (int)Math.Round(sampleRate * milliseconds / 1000.0));
        var result = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            double envelope = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (samples - 1)));
            result[i] = (float)(amplitude * envelope * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
        }
        return result;
    }

    /// <summary>Copia el impulso a un búfer nativo, con el formato del flujo.</summary>
    public static void WriteSamples(IntPtr destination, float[] samples, bool float32)
    {
        if (destination == IntPtr.Zero || samples.Length == 0) return;

        if (float32)
        {
            Marshal.Copy(samples, 0, destination, samples.Length);
            return;
        }

        var pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            pcm[i] = (short)Math.Clamp(samples[i] * 32767, short.MinValue, short.MaxValue);
        Marshal.Copy(pcm, 0, destination, pcm.Length);
    }

    // ---------------------------------------------------------------------
    // Declaraciones COM. El ORDEN de los métodos es la vtable: NO reordenar.
    // ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr data;
        public IntPtr data2;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long bufferDuration, long periodicity,
            IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
    }

    /// <summary>
    /// IAudioClient3: los mismos métodos de IAudioClient (heredados, por eso se repiten acá)
    /// más los del período en modo compartido. Es lo que permite saber si el motor está
    /// corriendo en un período chico (baja latencia) o en el grande.
    /// </summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42")]
    public interface IAudioClient3
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long bufferDuration, long periodicity,
            IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr service);
        [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriodInFrames,
            out uint fundamentalPeriodInFrames, out uint minPeriodInFrames, out uint maxPeriodInFrames);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodInFrames);
        [PreserveSig] int InitializeSharedAudioStream(uint streamFlags, uint periodInFrames, IntPtr format, IntPtr sessionGuid);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint framesRequested, out IntPtr buffer);
        [PreserveSig] int ReleaseBuffer(uint framesWritten, uint flags);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint framesRead);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("CD63314F-3FBA-4A1F-812E-EB96EC4DFB8B")]
    public interface IAudioClock
    {
        [PreserveSig] int GetFrequency(out ulong frequency);
        [PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
        [PreserveSig] int GetCharacteristics(out uint characteristics);
    }

    /// <summary>Un paquete capturado, con el QPC de su primera muestra (100 ns).</summary>
    public readonly record struct CapturedPacket(ulong QpcPosition, int FirstSampleIndex, int Frames);
}
