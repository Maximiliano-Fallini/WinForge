using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WHPO.Core.Services;

/// <summary>
/// Endpoint de audio activo, solo para MOSTRARLO en el desplegable del test de latencia.
/// No hay medición de audio: el test de ese tipo se retiró, así que este dato no habilita
/// ninguna corrida.
/// </summary>
public sealed record AudioEndpointInfo(string Id, string Name, bool IsInput, bool IsDefault);

/// <summary>
/// Enumeración de solo lectura de los endpoints de audio activos (WASAPI), con el mismo
/// criterio que usaba el retirado test de audio: salidas primero, después entradas, y el
/// predeterminado de cada sentido marcado.
///
/// Vive aparte y expone únicamente lo que el desplegable necesita (id, nombre, sentido y
/// si es el predeterminado). No abre streams ni mide nada: la lógica de medición de audio
/// se eliminó a propósito.
/// </summary>
public static class AudioEndpointCatalog
{
    public static IReadOnlyList<AudioEndpointInfo> Enumerate()
    {
        var result = new List<AudioEndpointInfo>();
        bool com = TryInitCom();
        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator == null) return result;

            string? defaultRender = GetDefaultEndpointId(enumerator, EDataFlowRender);
            string? defaultCapture = GetDefaultEndpointId(enumerator, EDataFlowCapture);

            AddEndpoints(result, enumerator, EDataFlowRender, isInput: false, defaultRender);
            AddEndpoints(result, enumerator, EDataFlowCapture, isInput: true, defaultCapture);
        }
        finally
        {
            if (com) CoUninitialize();
        }
        return result;
    }

    private static void AddEndpoints(List<AudioEndpointInfo> result, IMMDeviceEnumerator enumerator,
        int dataFlow, bool isInput, string? defaultId)
    {
        if (enumerator.EnumAudioEndpoints(dataFlow, DeviceStateActive, out var collection) < 0 || collection == null)
            return;
        if (collection.GetCount(out int count) < 0) return;

        for (int i = 0; i < count; i++)
        {
            try
            {
                if (collection.Item(i, out var device) < 0 || device == null) continue;
                string? id = device.GetId(out var rawId) >= 0 ? rawId : null;
                if (string.IsNullOrWhiteSpace(id)) continue;

                string name = ReadFriendlyName(device) ?? (isInput ? "Entrada" : "Salida");
                result.Add(new AudioEndpointInfo(
                    id, name, isInput,
                    string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // Un endpoint que no se puede leer no invalida la lista completa.
            }
        }
    }

    private static bool TryInitCom()
    {
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        return hr == S_OK || hr == S_FALSE;
    }

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        var clsid = CLSID_MMDeviceEnumerator;
        var iid = IID_IMMDeviceEnumerator;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out var instance);
        return hr < 0 || instance is not IMMDeviceEnumerator enumerator ? null : enumerator;
    }

    private static string? GetDefaultEndpointId(IMMDeviceEnumerator enumerator, int dataFlow)
    {
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(dataFlow, ERoleConsole, out var device) < 0 || device == null)
                return null;
            return device.GetId(out var id) >= 0 ? id : null;
        }
        catch { return null; }
    }

    private static string? ReadFriendlyName(IMMDevice device)
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

    // ---------------------------------------------------------------------
    // Interop COM. El ORDEN de los métodos de cada interfaz es la vtable:
    // NO reordenar.
    // ---------------------------------------------------------------------

    private const int EDataFlowRender = 0;
    private const int EDataFlowCapture = 1;
    private const int ERoleConsole = 0;
    private const int DeviceStateActive = 0x00000001;
    private const int ClsCtxAll = 0x17;
    private const int StgmRead = 0;
    private const int COINIT_MULTITHREADED = 0x0;
    private const int S_OK = 0;
    private const int S_FALSE = 1;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName =
        new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsCtx, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr data;
        public IntPtr data2;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }
}
