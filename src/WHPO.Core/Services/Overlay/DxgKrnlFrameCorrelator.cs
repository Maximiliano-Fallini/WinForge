using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services.Overlay;

/// <summary>
/// Correlador interno de presentaciones DxgKrnl. No depende de una aplicación
/// externa: recibe eventos ETW directamente y correlaciona la ruta flip mediante
/// secuencias de cola y señales MMIO/VSync. La API pública de FPS todavía no mezcla
/// esta ruta con DXGI hasta disponer de una métrica Displayed FPS separada.
///
/// Los payloads de DxgKrnl varían entre builds de Windows. Por eso todos los
/// campos se leen de forma opcional y un evento incompleto se ignora, nunca se
/// convierte en un FPS heurístico.
/// </summary>
internal sealed class DxgKrnlFrameCorrelator
{
    internal static readonly Guid ProviderGuid = new("802ec45a-1e99-4b83-9920-87c98277ba9d");

    // IDs públicos del manifest de DxgKrnl usados por PresentMon.
    private const int FlipInfo = 0x00A8;
    private const int QueuePacketStart = 0x00B2;
    private const int QueuePacketStop = 0x00B4;
    private const int MmioFlipInfo = 0x0074;
    private const int VSyncDpcInfo = 0x0011;
    private const int PresentInfo = 0x00B8;

    private const uint MmioFlipPacket = 3;
    private readonly Dictionary<(int Pid, ulong Context), PendingFlip> _pending = new();
    private readonly Dictionary<(int Pid, uint Sequence), PendingFlip> _bySequence = new();

    private sealed class PendingFlip
    {
        public int Pid;
        public ulong Context;
        public uint Sequence;
        public long StartTicks;
        public bool IsPresentPacket;
        public bool Completed;
    }

    public bool TryProcess(TraceEvent e, out FrametimeSample sample)
    {
        sample = default;
        if (e.ProviderGuid != ProviderGuid || e.ProcessID <= 0) return false;

        int eventId = (int)e.ID;
        return eventId switch
        {
            FlipInfo => HandleFlip(e),
            QueuePacketStart => HandleQueueStart(e),
            QueuePacketStop => HandleQueueStop(e),
            MmioFlipInfo or VSyncDpcInfo => HandleDisplayCompletion(e, out sample),
            PresentInfo => HandlePresentInfo(e),
            _ => false
        };
    }

    private bool HandleFlip(TraceEvent e)
    {
        ulong context = ReadUInt64(e, "hContext", "Context");
        if (context == 0) context = (ulong)e.ThreadID;
        var key = (e.ProcessID, context);
        _pending[key] = new PendingFlip
        {
            Pid = e.ProcessID,
            Context = context,
            StartTicks = e.TimeStamp.Ticks
        };
        return false;
    }

    private bool HandleQueueStart(TraceEvent e)
    {
        uint sequence = ReadUInt32(e, "SubmitSequence", "QueueSubmitSequence");
        if (sequence == 0) return false;
        ulong context = ReadUInt64(e, "hContext", "Context");
        bool isPresent = ReadBool(e, "bPresent", "Present");
        uint packetType = ReadUInt32(e, "PacketType", "Type");
        if (!isPresent && packetType != MmioFlipPacket) return false;

        PendingFlip? pending = FindPending(e.ProcessID, context);
        pending ??= new PendingFlip
        {
            Pid = e.ProcessID,
            Context = context,
            StartTicks = e.TimeStamp.Ticks
        };
        pending.Sequence = sequence;
        pending.IsPresentPacket = isPresent || packetType == MmioFlipPacket;
        _bySequence[(e.ProcessID, sequence)] = pending;
        return false;
    }

    private bool HandleQueueStop(TraceEvent e)
    {
        uint sequence = ReadUInt32(e, "SubmitSequence", "QueueSubmitSequence");
        if (sequence == 0 || !_bySequence.TryGetValue((e.ProcessID, sequence), out var pending)) return false;
        pending.Completed = true;
        return false;
    }

    private bool HandleDisplayCompletion(TraceEvent e, out FrametimeSample sample)
    {
        sample = default;
        uint sequence = ReadUInt32(e, "FlipSubmitSequence", "SubmitSequence");
        PendingFlip? pending = null;
        if (sequence != 0) _bySequence.TryGetValue((e.ProcessID, sequence), out pending);
        pending ??= FindNewestPending(e.ProcessID);
        if (pending == null || !pending.IsPresentPacket || pending.StartTicks <= 0) return false;

        pending.Completed = true;
        // La correlación se completa aquí; esta duración es Present→display y no es
        // un delta entre frames. No se publica todavía: mezclar ambas métricas
        // produciría valores incorrectos. La futura métrica Displayed FPS deberá
        // comparar completions consecutivas por proceso.
        Remove(pending);
        return false;
    }

    private bool HandlePresentInfo(TraceEvent e)
    {
        // Present_Info solo confirma actividad; no es una señal de pantalla.
        // Se mantiene como pista de limpieza para no dejar correlaciones eternas.
        ulong context = ReadUInt64(e, "hContext", "Context");
        if (context != 0) _pending.Remove((e.ProcessID, context));
        return false;
    }

    private PendingFlip? FindPending(int pid, ulong context)
    {
        if (context != 0 && _pending.TryGetValue((pid, context), out var exact)) return exact;
        return FindNewestPending(pid);
    }

    private PendingFlip? FindNewestPending(int pid)
    {
        PendingFlip? newest = null;
        foreach (var item in _pending.Values)
        {
            if (item.Pid != pid || item.Completed) continue;
            if (newest == null || item.StartTicks > newest.StartTicks) newest = item;
        }
        return newest;
    }

    private void Remove(PendingFlip pending)
    {
        _pending.Remove((pending.Pid, pending.Context));
        if (pending.Sequence != 0) _bySequence.Remove((pending.Pid, pending.Sequence));
    }

    private static ulong ReadUInt64(TraceEvent e, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                object? value = e.PayloadByName(name);
                if (value is ulong u) return u;
                if (value is long l && l >= 0) return (ulong)l;
                if (value is uint ui) return ui;
                if (value is int i && i >= 0) return (ulong)i;
            }
            catch { }
        }
        return 0;
    }

    private static uint ReadUInt32(TraceEvent e, params string[] names)
        => (uint)Math.Min(uint.MaxValue, ReadUInt64(e, names));

    private static bool ReadBool(TraceEvent e, params string[] names)
        => ReadUInt64(e, names) != 0;
}
