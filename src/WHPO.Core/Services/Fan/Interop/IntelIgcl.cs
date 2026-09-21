using System.Runtime.InteropServices;

namespace WHPO.Core.Services.Fan.Interop;

/// <summary>
/// Interop con IGCL (Intel Graphics Control Library), la API de control de las GPU
/// Intel que vive en ControlLib.dll (viene con el driver de Intel). Es el mismo
/// camino que usa el plugin Intel de FanControl: LHM la usa solo para LEER
/// (ctlFanGetState) y no declara ninguna función de escritura, así que acá se
/// agregan las que faltan para poder fijar el duty:
///
///   ctlFanSetFixedSpeedMode  → fija el ventilador a un % (modo manual)
///   ctlFanSetDefaultMode     → lo devuelve al control del driver
///
/// Los structs y firmas de lectura están copiados de LibreHardwareMonitor 0.9.6
/// (MPL-2.0, el mismo que ya usa el proyecto) para no inventar layouts: la única
/// pieza que LHM no ejercita es ctlFanGetProperties, así que se lee con cuidado y
/// todo valor raro se trata como "no controlable" (nunca como controlable).
/// IGCL limita estas APIs a procesos de 64 bits (limitación de Level Zero).
/// </summary>
internal static class IntelIgcl
{
    private const string DllName = "ControlLib.dll";

    public const int MaxDevices = 64;
    public const int MaxDeviceNameLen = 100;
    public const int MaxReservedSize = 112;
    public const uint ImplVersion = (1u << 16) | 1u; // 1.1, igual que LHM

    /// <summary>Vendor PCI de Intel (para no tocar el equipo de otro fabricante).</summary>
    public const uint IntelVendorId = 0x8086;

    public const int CtlResultSuccess = 0x00000000;

    /// <summary>
    /// ctlInit devuelve esto cuando OTRO llamador del mismo proceso ya abrió IGCL
    /// (es el caso normal acá: LHM inicializa IGCL para leer los ventiladores de la
    /// GPU Intel antes de que FanControlService pida el canal de escritura). IGCL lo
    /// lista como éxito, así que se acepta: el handle que devuelve sirve igual.
    /// </summary>
    public const int CtlResultStillOpenByAnotherCaller = 0x00000001;

    /// <summary>
    /// supportedModes es un bitfield de ctl_fan_speed_mode_t (ver ctl_fan_properties_t
    /// en igcl_api.h): el modo Fixed es el bit 1, NO el valor 1. Confundirlos hacía
    /// leer el bit de Default y habilitar el control en ventiladores que no aceptan
    /// modo fijo.
    /// </summary>
    public const uint FanModeFixedBit = 1u << (int)CtlFanSpeedMode.Fixed;

    /// <summary>True si ControlLib.dll está y exporta las funciones que necesitamos.</summary>
    public static bool IsAvailable { get; }

    static IntelIgcl()
    {
        try
        {
            if (!NativeLibrary.TryLoad(DllName, out var module))
            {
                IsAvailable = false;
                return;
            }

            IsAvailable =
                NativeLibrary.TryGetExport(module, "ctlInit", out _) &&
                NativeLibrary.TryGetExport(module, "ctlEnumerateDevices", out _) &&
                NativeLibrary.TryGetExport(module, "ctlEnumFans", out _) &&
                NativeLibrary.TryGetExport(module, "ctlFanSetFixedSpeedMode", out _) &&
                NativeLibrary.TryGetExport(module, "ctlFanSetDefaultMode", out _);
        }
        catch
        {
            IsAvailable = false;
        }
    }

    // =====================================================================
    // Enums
    // =====================================================================

    public enum CtlDeviceType
    {
        Graphics = 1,
        System = 2,
        Max = 3
    }

    public enum CtlFanSpeedMode
    {
        Default = 0,
        Fixed = 1,
        Table = 2,
        Max = 3
    }

    public enum CtlFanSpeedUnits
    {
        Rpm = 0,
        Percent = 1,
        Max = 2
    }

    public enum CtlInitFlag : uint
    {
        UseLevelZero = 1 << 0
    }

    // =====================================================================
    // Structs (layouts de LHM 0.9.6)
    // =====================================================================

    [StructLayout(LayoutKind.Sequential)]
    public struct CtlApplicationId
    {
        public uint Data1;
        public ushort Data2;
        public ushort Data3;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CtlInitArgs
    {
        public uint Size;
        public byte Version;
        public uint AppVersion;
        public uint Flags;
        public uint SupportedVersion;
        public CtlApplicationId ApplicationUid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CtlApiHandle
    {
        private IntPtr _pNext;

        /// <summary>True si el driver no dejó un handle (no se puede usar la sesión).</summary>
        public readonly bool IsNull => _pNext == IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CtlDeviceAdapterHandle
    {
        private IntPtr _pNext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CtlFanHandle
    {
        private IntPtr _pNext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CtlFirmwareVersion
    {
        public ulong MajorVersion;
        public ulong MinorVersion;
        public ulong BuildNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CtlAdapterBdf
    {
        public byte Bus;
        public byte Device;
        public byte Function;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CtlDeviceAdapterProperties
    {
        public uint Size;
        public byte Version;
        public IntPtr DeviceId;
        public uint DeviceIdSize;
        public CtlDeviceType DeviceType;
        public uint SupportedSubfunctionFlags;
        public ulong DriverVersion;
        public CtlFirmwareVersion FirmwareVersion;
        public uint PciVendorId;
        public uint PciDeviceId;
        public uint RevId;
        public uint NumEusPerSubSlice;
        public uint NumSubSlicesPerSlice;
        public uint NumSlices;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxDeviceNameLen)]
        public string Name;

        public uint GraphicsAdapterProperties;
        public uint Frequency;
        public ushort PciSubsysId;
        public ushort PciSubsysVendorId;
        public CtlAdapterBdf AdapterBdf;
        public uint NumXeCores;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxReservedSize)]
        public byte[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CtlFanSpeed
    {
        public uint Size;
        public byte Version;
        public int Speed;
        public CtlFanSpeedUnits Units;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CtlFanProperties
    {
        public uint Size;
        public byte Version;

        // OJO: en igcl_api.h esto es un `bool` de C++ (1 byte). Un `bool` de C# dentro
        // de un struct se marshalea como BOOL de 4 bytes, lo que corría todos los campos
        // siguientes 4 bytes y hacía que IGCL rechazara Size (INVALID_SIZE): el canal
        // Intel quedaba de solo lectura sin motivo visible. Con `byte` el layout es el
        // mismo que el nativo (24 bytes: Size 0, Version 4, canControl 5, pad 6-7,
        // supportedModes 8, supportedUnits 12, maxRPM 16, maxPoints 20).
        public byte CanControl;

        public uint SupportedModes;
        public uint SupportedUnits;
        public int MaxRpm;
        public int MaxPoints;
    }

    // =====================================================================
    // Funciones
    // =====================================================================

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ctlInit(ref CtlInitArgs initDesc, ref CtlApiHandle apiHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ctlEnumerateDevices(CtlApiHandle apiHandle, ref uint count, [Out] CtlDeviceAdapterHandle[]? devices);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ctlGetDeviceProperties(CtlDeviceAdapterHandle device, ref CtlDeviceAdapterProperties properties);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ctlEnumFans(CtlDeviceAdapterHandle device, ref uint count, [Out] CtlFanHandle[]? fans);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ctlFanGetState(CtlFanHandle fan, CtlFanSpeedUnits units, ref int speed);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ctlFanGetProperties(CtlFanHandle fan, ref CtlFanProperties properties);

    /// <summary>Fija el ventilador en un % (equivale al modo manual).</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ctlFanSetFixedSpeedMode(CtlFanHandle fan, ref CtlFanSpeed speed);

    /// <summary>Devuelve el ventilador al control automático del driver.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ctlFanSetDefaultMode(CtlFanHandle fan);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ctlClose(CtlApiHandle apiHandle);
}
