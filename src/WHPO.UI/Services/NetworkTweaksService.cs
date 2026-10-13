using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WHPO_UI.Services;

/// <summary>
/// Tweaks de red del SO (nivel Windows, no del driver): reserva de ancho de
/// banda multimedia, throttling de red, SystemResponsiveness (reserva de CPU multimedia),
/// puertos efimeros, TIME_WAIT, LSO por interfaz y LargeSystemCache.
/// Todo reversible: GetState lee el valor real, Apply escribe solo lo pedido y
/// RestoreDefaults borra lo escrito (vuelve al default de fabrica).
/// App elevada: escritura directa al registro, sin UAC extra.
/// </summary>
public static class NetworkTweaksService
{
    /// <summary>Un tweak: donde vive, que tipo es y que valor pone cada preset.</summary>
    public sealed class TweakDef
    {
        public required string Id;
        public required string Title;
        public required string Tooltip;
        public required string Path;
        public required string Name;
        /// <summary>Valor del preset "Rendimiento (juegos)".</summary>
        public required string Gaming;

        /// <summary>
        /// Valor del preset NO-gaming: NO es un "modo ecológico", es el PREDETERMINADO de
        /// Windows (varios ajustes se restauran borrando el valor, que acá es la cadena vacía).
        /// El rótulo visible es "Windows (predeterminado)".
        /// </summary>
        public required string Windows;

        /// <summary>
        /// Valor PREDETERMINADO de Windows para este ajuste, con el mismo formato que
        /// <see cref="Gaming"/> y <see cref="Windows"/>. Sirve para que la UI reconozca el
        /// default POR VALOR y no solo por ausencia en el registro (una fila con el valor
        /// ya escrito e igual al default debe leerse como "Default de Windows").
        /// La cadena vacía significa "el default es que el valor NO exista".
        /// </summary>
        public required string WindowsDefault;

        /// <summary>Etiqueta propia de la opción "Rendimiento" del combo (null = etiqueta genérica).</summary>
        public string? GamingLabel;

        /// <summary>Etiqueta propia de la opción "Windows (predeterminado)" del combo (null = genérica).</summary>
        public string? WindowsLabel;
        public bool IfaceScoped;
    }

    public static readonly List<TweakDef> Definitions = new()
    {
        new TweakDef
        {
            Id = "nti", Title = "Limitación de paquetes multimedia (NPP)",
            Tooltip = "TtNetNti",
            Path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            Name = "NetworkThrottlingIndex", Gaming = "ffffffff", Windows = "a", WindowsDefault = "a",
        },
        new TweakDef
        {
            // UNA sola perilla, no un "modo": Windows reserva un % de CPU a las tareas de
            // fondo durante escenarios multimedia (MMCSS). 20 es el predeterminado y 10 el
            // mínimo que Windows acepta (valores <10 se devuelven a 20). De ahí que el título
            // y las opciones hablen de porcentaje, no de "prioridad a juegos" ni "ecológico".
            Id = "sysresp", Title = "Reserva de CPU multimedia (SystemResponsiveness)",
            Tooltip = "TtNetSysResp",
            Path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            Name = "SystemResponsiveness", Gaming = "10", Windows = "20", WindowsDefault = "20",
            GamingLabel = "10 % (mínimo)", WindowsLabel = "20 % (predeterminado)",
        },
        new TweakDef
        {
            Id = "maxport", Title = "Puertos efímeros máximos",
            Tooltip = "TtNetMaxPort",
            Path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
            Name = "MaxUserPort",
            // Desde Windows 10 el RANGO dinámico por defecto ya llega a 65535
            // (49152-65535, documentado por Microsoft). MaxUserPort no se escribe
            // en el registro: al no estar, Windows usa ese rango. Por eso los dos
            // presets lo dejan en blanco (borrar el valor) en vez de inventar un
            // 5000 que no es el default de ningún Windows moderno — escribirlo
            // ACHICABA el rango en vez de restaurarlo.
            Gaming = "", Windows = "", WindowsDefault = "",
        },
        new TweakDef
        {
            Id = "timedwait", Title = "Reciclaje TIME_WAIT",
            Tooltip = "TtNetTimedWait",
            Path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
            Name = "TcpTimedWaitDelay",
            // Default real: 120 s (0x78), y Microsoft documenta que el valor NO existe
            // por defecto en el registro. Rango válido 2-300 en Windows 8.1+.
            Gaming = "30", Windows = "", WindowsDefault = "",
        },
        new TweakDef
        {
            Id = "largecache", Title = "Caché grande del sistema",
            Tooltip = "TtNetLargeCache",
            Path = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            Name = "LargeSystemCache", Gaming = "1", Windows = "0", WindowsDefault = "0",
        },
        new TweakDef
        {
            // El valor que se escribe es DisableTaskOffload, y 1 APAGA toda la descarga al
            // hardware de esa interfaz (segmentación LSO, checksum y RSS offload): no es un
            // ajuste de "LSO on/off". Por eso el título nombra el valor y las dos opciones
            // dicen explícitamente qué pasa con la descarga — antes se llamaba "LSO" y la
            // opción de "Rendimiento" la apagaba, que se leía justo al revés.
            Id = "lso", Title = "Descarga al hardware (DisableTaskOffload)",
            Tooltip = "TtNetLso",
            Path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces",
            Name = "DisableTaskOffload", Gaming = "1", Windows = "0", WindowsDefault = "0", IfaceScoped = true,
            GamingLabel = "Desactivar la descarga (la CPU segmenta)",
            WindowsLabel = "Descarga activada (predeterminado)",
        },
    };
    public sealed record TweakState(TweakDef Def, string? RawValue, bool IsSet);

    private static RegistryKey OpenBase()
        => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private static string? ReadRaw(TweakDef def, string? ifaceGuid)
    {
        try
        {
            using var baseKey = OpenBase();
            if (def.IfaceScoped && string.IsNullOrEmpty(ifaceGuid)) return null;
            string path = def.IfaceScoped ? def.Path + "\\" + ifaceGuid : def.Path;
            using var key = baseKey.OpenSubKey(path);
            var v = key?.GetValue(def.Name);
            if (v == null) return null;
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    public static async Task<List<TweakState>> GetStateAsync()
    {
        string? guid = await ActiveInterfaceGuidAsync();
        // Una sola lectura del registro por ajuste: antes ReadRaw se llamaba dos veces (12
        // lecturas por refresco) solo para saber si el valor existía.
        var list = Definitions.Select(d =>
        {
            var raw = ReadRaw(d, guid);
            return new TweakState(d, raw, raw != null);
        }).ToList();
        return list;
    }

    public static async Task<List<string>> ApplyPresetAsync(string preset)
    {
        bool gaming = preset.Trim().Equals("gaming", StringComparison.OrdinalIgnoreCase);
        var values = Definitions.ToDictionary(d => d.Id, d => gaming ? d.Gaming : d.Windows);
        return await ApplyAsync(values);
    }

    public static async Task<List<string>> ApplyAsync(Dictionary<string, string> values)
    {
        var failed = new List<string>();
        try
        {
            string? guid = await ActiveInterfaceGuidAsync();
            using var baseKey = OpenBase();
            foreach (var def in Definitions)
            {
                if (!values.TryGetValue(def.Id, out var want)) continue;
                try
                {
                    if (def.IfaceScoped && string.IsNullOrEmpty(guid)) { failed.Add(def.Id); continue; }
                    string path = def.IfaceScoped ? def.Path + "\\" + guid : def.Path;
                    using var key = baseKey.CreateSubKey(path);
                    if (key == null) { failed.Add(def.Id); continue; }

                    // Un valor vacío significa "volver al default de Windows", que para estos
                    // ajustes es NO tener el valor escrito: se borra y Windows recalcula. Escribir
                    // un número inventado para "restaurar" es lo que achicaba el rango de puertos.
                    if (string.IsNullOrWhiteSpace(want))
                    {
                        key.DeleteValue(def.Name, throwOnMissingValue: false);
                        continue;
                    }

                    if (!TryParseU32(want, out uint u)) { failed.Add(def.Id); continue; }
                    key.SetValue(def.Name, (int)u, RegistryValueKind.DWord);
                }
                catch { failed.Add(def.Id); }
            }
        }
        catch { failed.AddRange(values.Keys); }
        return failed;
    }

    private static bool TryParseU32(string s, out uint v)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out v);
        if (uint.TryParse(s, System.Globalization.NumberStyles.Integer, null, out v)) return true;
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v);
    }

    public static async Task<(bool Ok, string Message)> RestoreDefaultsAsync()
    {
        try
        {
            string? guid = await ActiveInterfaceGuidAsync();
            using var baseKey = OpenBase();
            foreach (var def in Definitions)
            {
                try
                {
                    if (def.IfaceScoped && string.IsNullOrEmpty(guid)) continue;
                    string path = def.IfaceScoped ? def.Path + "\\" + guid : def.Path;
                    using var key = baseKey.OpenSubKey(path, writable: true);
                    key?.DeleteValue(def.Name, throwOnMissingValue: false);
                }
                catch { }
            }
            return (true, "ok");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// GUID de la interfaz física activa. ASYNC a propósito: antes era síncrona
    /// (GetAwaiter().GetResult()) y bloqueaba el hilo de UI mientras esperaba un
    /// proceso de PowerShell — la página quedaba congelada.
    /// </summary>
    private static async Task<string?> ActiveInterfaceGuidAsync()
    {
        try
        {
            var script = "$a = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1; if ($a) { $a.InterfaceGuid.ToString() }";
            var (output, exit) = await PowerShellRunner.RunAsync(script);
            if (exit != 0) return null;
            var guid = output.Trim().Trim('\'', '"');
            return Guid.TryParse(guid, out _) ? guid : null;
        }
        catch { return null; }
    }

    public static string DisplayValue(TweakDef def, string? raw)
    {
        if (raw == null) return "";
        if (TryParseU32(raw, out uint u))
        {
            if (def.Id == "nti" && u == 0xFFFFFFFF) return "0xFFFFFFFF";
            return "0x" + u.ToString("X") + " (" + u + ")";
        }
        return raw;
    }
}
