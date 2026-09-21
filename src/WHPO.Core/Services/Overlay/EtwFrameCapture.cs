using System;
using Microsoft.Diagnostics.Tracing;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services.Overlay;

/// <summary>
/// Captura interna de presentaciones mediante ETW. Esta clase no expone ni requiere
/// una aplicación auxiliar: consume directamente la sesión ETW de Windows.
///
/// El correlador de DxgKrnl se mantiene separado de la fachada FpsMonitor para que
/// pueda incorporar los eventos manifestados de distintas versiones de Windows sin
/// contaminar el contrato público del overlay. Mientras no haya payload verificable,
/// solo se acepta la ruta DXGI, evitando falsos FPS.
/// </summary>
internal sealed class EtwFrameCapture
{
    internal static readonly Guid DxgiProviderGuid = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");

    public bool IsFrameEvent(TraceEvent traceEvent)
        => traceEvent.ProviderGuid == DxgiProviderGuid && IsDxgiPresentStart(traceEvent);

    public static bool IsDxgiPresentStart(TraceEvent traceEvent)
    {
        var name = traceEvent.EventName;
        if (!string.IsNullOrEmpty(name))
        {
            if (name.EndsWith("DXGI_Present_Start", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith("Present_Start", StringComparison.OrdinalIgnoreCase)) return true;
        }

        // Fallback para sistemas donde el manifest DXGI no fue decodificado.
        return (int)traceEvent.ID == 42 && traceEvent.Opcode == TraceEventOpcode.Start;
    }
}
