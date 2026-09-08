using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForge.Component.LatencyMeter;

/// <summary>
/// P/Invokes y structs nativos para una sesión ETW del kernel (PerfInfo).
///
/// TODOS los tamaños/offsets están MEDIDOS compilando sizeof/offsetof contra
/// evntrace.h / relogger.h / wmistr.h del SDK 10.0.26100.0 con MSVC x64:
///   EVENT_TRACE_LOGFILEW = 448  (LoggerName=8, ProcessTraceMode=28,
///                                EventRecordCallback=424, Context=440)
///   EVENT_RECORD         = 112  (UserDataLength=86, UserData=96, UserContext=104)
///   EVENT_TRACE_PROPERTIES = 120, WNODE_HEADER = 48, EVENT_HEADER = 80
///
/// Cómo funciona la medición (mismo enfoque que LatencyMon):
///  - Con EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT el kernel emite un
///    par de eventos por rutina: 66/68 (DPC inicio/fin) y 73/74 (ISR inicio/fin)
///    bajo el GUID PerfInfo (ce1dbfb4-137e-4da6-87b0-3f59aa102cbc).
///  - El evento de FIN trae InitialTime (cuándo ENTRÓ la rutina) y Routine
///    (dirección kernel de la función). Duración = TimeStamp - InitialTime,
///    válida con ClientContext=1 (QPC) + PROCESS_TRACE_MODE_RAW_TIMESTAMP.
///  - La dirección se resuelve a módulo con EnumDeviceDrivers (psapi).
/// </summary>
internal static unsafe class Native
{
    // ===== GUIDs =====
    // SystemTraceControlGuid: la sesión del kernel.
    public static readonly Guid SystemTraceControlGuid = new(0x9e814aad, 0x3204, 0x11d2, 0x9a, 0x82, 0x00, 0x60, 0x08, 0xa8, 0x69, 0x39);
    // PerfInfoGuid: clase de eventos DPC/ISR del kernel (MS Learn, "PerfInfo class").
    public static readonly Guid PerfInfoGuid = new(0xce1dbfb4, 0x137e, 0x4da6, 0x87, 0xb0, 0x3f, 0x59, 0xaa, 0x10, 0x2c, 0xbc);

    // ===== Tipos de evento del kernel (documentados en "PerfInfo class") =====
    public const byte EVENT_TRACE_TYPE_DPC_START = 66;   // Threaded DPC (inicio)
    public const byte EVENT_TRACE_TYPE_DPC_END = 68;     // DPC (fin; payload InitialTime+Routine)
    public const byte EVENT_TRACE_TYPE_ISR_START = 73;   // inicio de ISR
    public const byte EVENT_TRACE_TYPE_ISR_END = 74;     // fin de ISR (payload InitialTime+Routine)

    // ===== Flags del kernel (evntrace.h) =====
    public const uint EVENT_TRACE_FLAG_DPC = 0x00000020;
    public const uint EVENT_TRACE_FLAG_INTERRUPT = 0x00000040;

    // ===== Modos (evntrace.h) =====
    public const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    public const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
    public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    public const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    public const uint PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00000001;

    // ===== Control / errores =====
    public const uint EVENT_TRACE_FILE_MODE_SEQUENTIAL = 0x00000001;
    public const uint EVENT_TRACE_CONTROL_STOP = 1;
    public const int ERROR_ALREADY_EXISTS = 183;

    /// <summary>
    /// EVENT_RECORD (112 bytes x64, offsets medidos con el SDK):
    /// header 0..80 (Size/HeaderType/Flags/EventProperty/ThreadId/ProcessId/
    /// TimeStamp/ProviderId/EventDescriptor/ProcessorTime/ActivityId),
    /// BufferContext=80, ExtendedDataCount=84, UserDataLength=86,
    /// ExtendedData=88, UserData=96, UserContext=104.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_RECORD
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public ushort DescId;        // EVENT_DESCRIPTOR.Id
        public byte DescVersion;
        public byte DescChannel;
        public byte DescLevel;
        public byte DescOpcode;
        public ushort DescTask;
        public ulong DescKeyword;
        public ulong ProcessorTime;  // unión { KernelTime+UserTime ; ProcessorTime }
        public Guid ActivityId;
        public ushort ProcessorIndex; // ETW_BUFFER_CONTEXT (unión ProcessorNumber+Alignment)
        public ushort LoggerId;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    /// <summary>
    /// Bloque EVENT_TRACE_PROPERTIES + slots de nombres. Escrito por offset:
    /// WNODE_HEADER 0..48 (BufferSize=0, ClientContext=40, Flags=44, Guid=24),
    /// propiedades 48..120 (BufferSize=48 … LogFileNameOffset=112,
    /// LoggerNameOffset=116, sizeof total=120), nombres después.
    /// </summary>
    public sealed class TraceProperties : IDisposable
    {
        public const int PropsSize = 120;
        public const int LoggerNameOffset = 120;
        public const int LogFileNameOffset = 120 + 1024;

        public IntPtr Ptr { get; private set; }
        public int TotalBytes { get; private set; }

        public TraceProperties(string loggerName, uint enableFlags, uint logFileMode, uint clientContext,
            Guid? wnodeGuid = null, string? logFileName = null)
        {
            // Regla ETW: SystemTraceControlGuid es SOLO para la sesión kernel clásica
            // ("NT Kernel Logger"). Una sesión con nombre propio (system logger u
            // ordinaria) lleva un GUID único aleatorio en el WNODE — reutilizar el
            // GUID del kernel hace que StartTrace devuelva ERROR_INVALID_PARAMETER.
            Guid guid = wnodeGuid ?? Guid.NewGuid();

            // Sesión solo real-time: sin archivo de log → LogFileNameOffset = 0
            // (documentado). En modo archivo SÍ hace falta offset + nombre válido.
            int logFileNameOffset = logFileName != null ? LogFileNameOffset : 0;

            TotalBytes = LogFileNameOffset + 1024;
            Ptr = Marshal.AllocHGlobal(TotalBytes);
            Unsafe.InitBlockUnaligned((void*)Ptr, 0, (uint)TotalBytes);

            // --- WNODE_HEADER ---
            Marshal.WriteInt32(Ptr, 0, TotalBytes);                  // BufferSize
            Marshal.WriteInt32(Ptr, 40, unchecked((int)clientContext));
            Marshal.WriteInt32(Ptr, 44, unchecked((int)WNODE_FLAG_TRACED_GUID));
            byte[] guidBytes = guid.ToByteArray();
            Marshal.Copy(guidBytes, 0, Ptr + 24, 16);

            // --- EVENT_TRACE_PROPERTIES (offset 48) ---
            int p = 48;
            Marshal.WriteInt32(Ptr, p + 0, 64);                     // BufferSize (KB)
            Marshal.WriteInt32(Ptr, p + 4, Math.Min(Environment.ProcessorCount * 4, 64));   // MinimumBuffers
            Marshal.WriteInt32(Ptr, p + 8, Math.Min(Environment.ProcessorCount * 8, 128));  // MaximumBuffers
            Marshal.WriteInt32(Ptr, p + 12, 0);                     // MaximumFileSize
            Marshal.WriteInt32(Ptr, p + 16, unchecked((int)logFileMode));
            Marshal.WriteInt32(Ptr, p + 20, 1);                     // FlushTimer (s)
            Marshal.WriteInt32(Ptr, p + 24, unchecked((int)enableFlags));
            Marshal.WriteInt32(Ptr, p + 28, 0);                     // AgeLimit
            Marshal.WriteInt32(Ptr, p + 64, logFileNameOffset);     // LogFileNameOffset
            Marshal.WriteInt32(Ptr, p + 68, LoggerNameOffset);      // LoggerNameOffset

            WriteStringAt(LoggerNameOffset, loggerName);
            if (logFileName != null) WriteStringAt(LogFileNameOffset, logFileName);
        }

        private void WriteStringAt(int offset, string s)
        {
            foreach (char c in s) { Marshal.WriteInt16(Ptr, offset, c); offset += 2; }
            Marshal.WriteInt16(Ptr, offset, 0);
        }

        public void Dispose() { if (Ptr != IntPtr.Zero) { Marshal.FreeHGlobal(Ptr); Ptr = IntPtr.Zero; } }
    }

    /// <summary>
    /// EVENT_TRACE_LOGFILEW (448 bytes x64, offsets medidos): LoggerName=8,
    /// ProcessTraceMode=28, EventRecordCallback=424, Context=440.
    /// ETW copia Context a EVENT_RECORD.UserContext de cada evento.
    /// </summary>
    public sealed unsafe class TraceLogfile : IDisposable
    {
        public const int StructSize = 448;
        public const int OffLoggerName = 8;
        public const int OffProcessTraceMode = 28;
        public const int OffEventRecordCallback = 424;
        public const int OffContext = 440;

        public IntPtr Ptr { get; }
        private readonly IntPtr _pName;

        public TraceLogfile(string loggerName, delegate* unmanaged[Stdcall]<EVENT_RECORD*, void> callback)
        {
            Ptr = Marshal.AllocHGlobal(StructSize);
            Unsafe.InitBlockUnaligned((void*)Ptr, 0, StructSize);
            _pName = Marshal.StringToHGlobalUni(loggerName);
            Marshal.WriteIntPtr(Ptr, 0, IntPtr.Zero);                      // LogFileName (null en real-time)
            Marshal.WriteIntPtr(Ptr, OffLoggerName, _pName);               // LoggerName
            Marshal.WriteIntPtr(Ptr, OffEventRecordCallback, (IntPtr)callback);
            Marshal.WriteInt32(Ptr, OffProcessTraceMode, unchecked((int)(PROCESS_TRACE_MODE_EVENT_RECORD |
                                                                        PROCESS_TRACE_MODE_REAL_TIME |
                                                                        PROCESS_TRACE_MODE_RAW_TIMESTAMP)));
        }

        /// <summary>Contexto que ETW entrega en EVENT_RECORD.UserContext de cada evento.</summary>
        public void SetContext(IntPtr context) => Marshal.WriteIntPtr(Ptr, OffContext, context);

        public void Dispose()
        {
            if (_pName != IntPtr.Zero) Marshal.FreeHGlobal(_pName);
            Marshal.FreeHGlobal(Ptr);
        }
    }

    // ===== API de advapi32 =====

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint ProcessTrace(ulong[] handles, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint CloseTrace(ulong handle);

    // ===== API de psapi =====

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EnumDeviceDrivers([Out] ulong[]? imageBases, uint arrayBytes, out uint bytesNeeded);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetDeviceDriverFileNameW(IntPtr imageBase, [Out] char[]? name, uint size);

    // ===== API de kernel32 =====

    /// <summary>FILETIME (100ns) preciso del momento actual — para anclar QPC↔FILETIME.</summary>
    [DllImport("kernel32.dll")]
    public static extern void GetSystemTimePreciseAsFileTime(out long fileTime);
}
