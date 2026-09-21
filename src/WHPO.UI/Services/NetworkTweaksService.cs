using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WHPO_UI.Services;

/// <summary>
/// Tweaks de red del SO (nivel Windows, no del driver): reserva de ancho de
/// banda multimedia, throttling de red, SystemResponsiveness para juegos,
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
        public required string Gaming;
        public required string Eco;
        public bool IfaceScoped;
    }

    public static readonly List<TweakDef> Definitions = new()
    {
        new TweakDef
        {
            Id = "nti", Title = "Limitación de paquetes multimedia (NPP)",
            Tooltip = "TtNetNti",
            Path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            Name = "NetworkThrottlingIndex", Gaming = "ffffffff", Eco = "a",
        },
        new TweakDef
        {
            Id = "sysresp", Title = "Prioridad a juegos (SystemResponsiveness)",
            Tooltip = "TtNetSysResp",
            Path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            Name = "SystemResponsiveness", Gaming = "0", Eco = "14",
        },
        new TweakDef
        {
            Id = "maxport", Title = "Puertos efímeros máximos",
            Tooltip = "TtNetMaxPort",
            Path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
            Name = "MaxUserPort", Gaming = "65534", Eco = "5000",
        },
        new TweakDef
        {
            Id = "timedwait", Title = "Reciclaje TIME_WAIT",
            Tooltip = "TtNetTimedWait",
            Path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
            Name = "TcpTimedWaitDelay", Gaming = "30", Eco = "240",
        },
        new TweakDef
        {
            Id = "largecache", Title = "Caché grande del sistema",
            Tooltip = "TtNetLargeCache",
            Path = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            Name = "LargeSystemCache", Gaming = "1", Eco = "0",
        },
        new TweakDef
        {
            Id = "lso", Title = "Descarga de segmentación (LSO)",
            Tooltip = "TtNetLso",
            Path = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces",
            Name = "DisableTaskOffload", Gaming = "1", Eco = "0", IfaceScoped = true,
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

    public static Task<List<TweakState>> GetStateAsync()
    {
        string? guid = ActiveInterfaceGuid();
        var list = Definitions.Select(d => new TweakState(d, ReadRaw(d, guid), ReadRaw(d, guid) != null)).ToList();
        return Task.FromResult(list);
    }

    public static async Task<List<string>> ApplyPresetAsync(string preset)
    {
        bool gaming = preset.Trim().Equals("gaming", StringComparison.OrdinalIgnoreCase);
        var values = Definitions.ToDictionary(d => d.Id, d => gaming ? d.Gaming : d.Eco);
        return await ApplyAsync(values);
    }

    public static Task<List<string>> ApplyAsync(Dictionary<string, string> values)
    {
        var failed = new List<string>();
        try
        {
            string? guid = ActiveInterfaceGuid();
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
                    if (!TryParseU32(want, out uint u)) { failed.Add(def.Id); continue; }
                    key.SetValue(def.Name, (int)u, RegistryValueKind.DWord);
                }
                catch { failed.Add(def.Id); }
            }
        }
        catch { failed.AddRange(values.Keys); }
        return Task.FromResult(failed);
    }

    private static bool TryParseU32(string s, out uint v)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out v);
        if (uint.TryParse(s, System.Globalization.NumberStyles.Integer, null, out v)) return true;
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v);
    }

    public static Task<(bool Ok, string Message)> RestoreDefaultsAsync()
    {
        try
        {
            string? guid = ActiveInterfaceGuid();
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
            return Task.FromResult((true, "ok"));
        }
        catch (Exception ex) { return Task.FromResult((false, ex.Message)); }
    }

    private static string? ActiveInterfaceGuid()
    {
        try
        {
            var script = "$a = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' } | Select-Object -First 1; if ($a) { $a.InterfaceGuid.ToString() }";
            var (output, exit) = PowerShellRunner.RunAsync(script).GetAwaiter().GetResult();
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
