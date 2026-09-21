using System.Runtime.InteropServices;

namespace WHPO.Core.Services.Fan.Interop;

/// <summary>
/// Interop con ADL, la librería del driver de AMD (atiadlxx.dll) que expone el
/// control de los ventiladores de una GPU por Overdrive5. Es la misma API que usa
/// LibreHardwareMonitor para el camino AMD: acá se reproduce para poder escribir el
/// duty incluso en el caso en que LHM no llegue a activar su canal de control
/// (por ejemplo cuando la lectura de % que LHM usa como condición falla).
///
/// Firmas, structs y constantes están copiados de LibreHardwareMonitor 0.9.6
/// (MPL-2.0) para respetar el layout exacto que espera el driver.
/// </summary>
internal static class AmdAdl
{
    private const string DllName = "atiadlxx.dll";

    public const int AdlMaxPath = 256;
    public const int AdlOk = 0;
    public const int AdlTrue = 1;
    public const int AtiVendorId = 0x1002;

    public const int SpeedTypePercent = 1;
    public const int SpeedTypeRpm = 2;
    public const int FlagUserDefinedSpeed = 1;

    public const int SupportsPercentRead = 1;
    public const int SupportsPercentWrite = 2;
    public const int SupportsRpmRead = 4;
    public const int SupportsRpmWrite = 8;

    /// <summary>
    /// True si atiadlxx.dll está y exporta las funciones de fan de Overdrive5 que
    /// hacen falta. Se resuelve por nombre de export (no solo por archivo) para no
    /// depender de una versión concreta del driver.
    /// </summary>
    public static bool IsAvailable { get; } = Detect();
    public static bool HasFanOverdrive5 { get; } = DetectFanOverdrive5();

    private static bool Detect()
    {
        try
        {
            if (!NativeLibrary.TryLoad(DllName, out var module)) return false;
            return NativeLibrary.TryGetExport(module, "ADL2_Main_Control_Create", out _);
        }
        catch { return false; }
    }

    private static bool DetectFanOverdrive5()
    {
        try
        {
            if (!NativeLibrary.TryLoad(DllName, out var module)) return false;
            return NativeLibrary.TryGetExport(module, "ADL2_Overdrive5_FanSpeed_Set", out _) &&
                   NativeLibrary.TryGetExport(module, "ADL2_Overdrive5_FanSpeedInfo_Get", out _);
        }
        catch { return false; }
    }

    public static IntPtr MainMemoryAlloc(int size) => Marshal.AllocHGlobal(size);

    public delegate IntPtr AdlMainMemoryAllocDelegate(int size);

    public enum AdlStatus
    {
        Ok = 0,
        OkWarning = 1,
        OkModeChange = 2,
        OkRestart = 3,
        OkWait = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdlAdapterInfo
    {
        public int Size;
        public int AdapterIndex;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string Udid;

        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string AdapterName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string DisplayName;

        public int Present;
        public int Exist;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string DriverPath;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string DriverPathExt;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = AdlMaxPath)]
        public string PnpString;

        public int OsDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdlFanSpeedValue
    {
        public int Size;
        public int SpeedType;
        public int FanSpeed;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdlFanSpeedInfo
    {
        public int Size;
        public int Flags;
        public int MinPercent;
        public int MaxPercent;
        public int MinRpm;
        public int MaxRpm;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern AdlStatus ADL2_Main_Control_Create(AdlMainMemoryAllocDelegate callback, int connectedAdapters, ref IntPtr context);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern AdlStatus ADL2_Main_Control_Destroy(IntPtr context);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern AdlStatus ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, ref int numAdapters);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern AdlStatus ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr adapterInfo, int size);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern AdlStatus ADL2_Adapter_Active_Get(IntPtr context, int adapterIndex, out int status);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern AdlStatus ADL2_Overdrive_Caps(IntPtr context, int adapterIndex, ref int supported, ref int enabled, ref int version);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern AdlStatus ADL2_Overdrive5_FanSpeedInfo_Get(IntPtr context, int adapterIndex, int thermalControllerIndex, ref AdlFanSpeedInfo fanSpeedInfo);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern AdlStatus ADL2_Overdrive5_FanSpeed_Get(IntPtr context, int adapterIndex, int thermalControllerIndex, ref AdlFanSpeedValue fanSpeedValue);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern AdlStatus ADL2_Overdrive5_FanSpeed_Set(IntPtr context, int adapterIndex, int thermalControllerIndex, ref AdlFanSpeedValue fanSpeedValue);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern AdlStatus ADL2_Overdrive5_FanSpeedToDefault_Set(IntPtr context, int adapterIndex, int thermalControllerIndex);

    /// <summary>
    /// Lee la lista de adaptadores. ADL devuelve la info en un buffer propio que hay
    /// que reservar: se hace en un solo bloque y se copia a structs de C#.
    /// </summary>
    public static AdlStatus GetAdapterInfo(IntPtr context, AdlAdapterInfo[] info)
    {
        int elementSize = Marshal.SizeOf<AdlAdapterInfo>();
        int size = info.Length * elementSize;
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            var status = ADL2_Adapter_AdapterInfo_Get(context, ptr, size);
            if (status != AdlStatus.Ok) return status;

            for (int i = 0; i < info.Length; i++)
            {
                info[i] = Marshal.PtrToStructure<AdlAdapterInfo>(IntPtr.Add(ptr, i * elementSize));
            }
            return status;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
