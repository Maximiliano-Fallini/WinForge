using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WHPO.Core.Services.Interfaces;

namespace WHPO.Core.Services;

/// <summary>
/// Implementación del servicio de repetición del teclado. Escribe en
/// HKCU\Control Panel\Accessibility\Keyboard Response (los mismos valores que
/// Windows usa para Filter Keys) y aplica el cambio al instante con
/// SystemParametersInfo(SPI_SETFILTERKEYS) + SPIF_SENDCHANGE.
///
/// Nota: los valores de AutoRepeatDelay/AutoRepeatRate solo toman efecto cuando
/// Filter Keys está activado (bit FKF_FILTERKEYSON). La app lo activa solo (Flags=43,
/// igual que FilterKeysSetter: On + Available + Confirm + Show status), SIN el atajo
/// de Shift × 8 s (bit FKF_HOTKEYACTIVE apagado) para que no se encienda por accidente,
/// ni el "beep" de accesibilidad (bit FKF_HOTKEYSOUND apagado).
/// </summary>
public sealed class KeyboardService : IKeyboardService
{
    private const string KeyPath = @"Control Panel\Accessibility\Keyboard Response";

    // FKF_* (FilterKeys, winuser.h)
    private const uint FKF_FilterKeysOn = 0x00000001;
    private const uint FKF_Available = 0x00000002;
    private const uint FKF_ConfirmHotkey = 0x00000008;
    private const uint FKF_HotkeySound = 0x00000010;
    private const uint FKF_Indicator = 0x00000020;

    // Flags al aplicar (igual que FilterKeysSetter): FilterKeys ON + disponible +
    // confirmar al activar + mostrar estado/indicador. Sin FKF_HOTKEYACTIVE (0x4):
    // el atajo de Shift×8 s queda desactivado, y sin FKF_HOTKEYSOUND (el "beep").
    private const uint FlagsApply = FKF_FilterKeysOn | FKF_Available | FKF_ConfirmHotkey | FKF_Indicator; // 43
    private const uint FlagsDefault = 0; // Filter Keys apagado (comportamiento estándar)

    // Valores por defecto de Windows 11 (Keyboard Response)
    private const int DefaultIgnoreUnderMs = 0;
    private const int DefaultRepeatDelayMs = 250;
    private const int DefaultRepeatRateMs = 33;
    private const int DefaultBounceMs = 0;

    // ¡Importante! Son 0x0032 (GET) y 0x0033 (SET). Tenerlos invertidos hace que
    // el "SET" sea en realidad una lectura: devuelve True pero no aplica nada.
    private const uint SPI_SETFILTERKEYS = 0x0033;
    private const uint SPI_GETFILTERKEYS = 0x0032;
    private const uint SPIF_UPDATEINIFILE = 0x0001;
    private const uint SPIF_SENDCHANGE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref FILTERKEYS pvParam, uint fWinIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILTERKEYS
    {
        public uint cbSize;
        public uint dwFlags;
        public uint iWaitMSec;   // DelayBeforeAcceptance (Slow Keys)
        public uint iDelayMSec;  // AutoRepeatDelay
        public uint iRepeatMSec; // AutoRepeatRate
        public uint iBounceMSec; // BounceTime
    }

    private static RegistryKey? BaseKey()
    {
        try { return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64); }
        catch { return null; }
    }

    public KeyboardSettings GetSettings()
    {
        int ignore = DefaultIgnoreUnderMs, delay = DefaultRepeatDelayMs, rate = DefaultRepeatRateMs;
        int bounce = DefaultBounceMs, flags = 0;
        try
        {
            using var key = BaseKey()?.OpenSubKey(KeyPath);
            if (key != null)
            {
                ignore = ReadInt(key, "DelayBeforeAcceptance", DefaultIgnoreUnderMs);
                delay = ReadInt(key, "AutoRepeatDelay", DefaultRepeatDelayMs);
                rate = ReadInt(key, "AutoRepeatRate", DefaultRepeatRateMs);
                bounce = ReadInt(key, "BounceTime", DefaultBounceMs);
                flags = ReadInt(key, "Flags", 0);
            }
        }
        catch
        {
            // Sin valores en el registro: se devuelven los por defecto.
        }
        return new KeyboardSettings(ignore, delay, rate, bounce, flags);
    }

    public bool IsActiveLive()
    {
        try
        {
            var fk = new FILTERKEYS { cbSize = (uint)Marshal.SizeOf<FILTERKEYS>() };
            // IMPORTANTE: para que SPI_GETFILTERKEYS devuelva bien el estado, uiParam
            // debe ser sizeof(FILTERKEYS) (no 0). Con 0 la lectura falla en silencio
            // y el estado real en vivo no se refleja.
            if (!SystemParametersInfo(SPI_GETFILTERKEYS, (uint)Marshal.SizeOf<FILTERKEYS>(), ref fk, 0))
                return false;
            return (fk.dwFlags & FKF_FilterKeysOn) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadInt(RegistryKey key, string name, int fallback)
    {
        try
        {
            var v = key.GetValue(name);
            if (v == null) return fallback;
            // En el registro viven como REG_SZ ("150"); Convert acepta string y DWord.
            return Convert.ToInt32(v);
        }
        catch { return fallback; }
    }

    public bool Apply(int ignoreUnderMs, int repeatDelayMs, int repeatRateMs, bool saveToRegistry, out string error)
    {
        error = string.Empty;
        // Windows exige iWait/iDelay/iRepeat >= 1 ms en la llamada SPI cuando
        // BounceKeys está apagado (si algún valor queda en 0, RepeatKeys NO se
        // activa y el cambio no tiene efecto). Máximo 20000 ms según la doc.
        // Ojo: ese mínimo solo aplica a la llamada en vivo; el registro guarda
        // exactamente lo que el usuario puso (0 sigue siendo 0), así "Aplicados
        // actualmente" devuelve el valor real.
        int ignoreSpi = Math.Clamp(ignoreUnderMs, 1, 20000);
        int delaySpi = Math.Clamp(repeatDelayMs, 1, 20000);
        int rateSpi = Math.Clamp(repeatRateMs, 1, 20000);
        try
        {
            // "Guardar en el registro": si está activo, los valores persisten y se
            // vuelven a aplicar al iniciar sesión. Si no, solo cambian esta sesión.
            if (saveToRegistry)
                WriteValues(ignoreUnderMs, repeatDelayMs, repeatRateMs, 0 /* bounce siempre apagado */, FlagsApply);
            // Aplica en vivo al instante (SPI_SETFILTERKEYS), igual que FilterKeysSetter.
            ApplyLive(ignoreSpi, delaySpi, rateSpi, 0, FlagsApply);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool ResetToDefaults(out string error)
    {
        error = string.Empty;
        try
        {
            WriteValues(DefaultIgnoreUnderMs, DefaultRepeatDelayMs, DefaultRepeatRateMs, DefaultBounceMs, FlagsDefault);
            ApplyLive(DefaultIgnoreUnderMs, DefaultRepeatDelayMs, DefaultRepeatRateMs, DefaultBounceMs, FlagsDefault);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void WriteValues(int ignore, int delay, int rate, int bounce, uint flags)
    {
        using var baseKey = BaseKey() ?? throw new InvalidOperationException("No se pudo abrir HKCU.");
        using var key = baseKey.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("No se pudo abrir la clave Keyboard Response.");
        key.SetValue("DelayBeforeAcceptance", ignore.ToString(), RegistryValueKind.String);
        key.SetValue("AutoRepeatDelay", delay.ToString(), RegistryValueKind.String);
        key.SetValue("AutoRepeatRate", rate.ToString(), RegistryValueKind.String);
        key.SetValue("BounceTime", bounce.ToString(), RegistryValueKind.String);
        key.SetValue("Flags", flags.ToString(), RegistryValueKind.String);
    }

    /// <summary>
    /// Aplica el cambio en vivo para que no haga falta reiniciar sesión.
    /// Si la llamada falla, el registro ya quedó escrito y el cambio aplica en el
    /// próximo inicio de sesión — se informa para que la UI lo muestre.
    /// </summary>
    private static void ApplyLive(int ignore, int delay, int rate, int bounce, uint flags)
    {
        var fk = new FILTERKEYS
        {
            cbSize = (uint)Marshal.SizeOf<FILTERKEYS>(),
            dwFlags = flags,
            iWaitMSec = (uint)Math.Max(0, ignore),
            iDelayMSec = (uint)Math.Max(0, delay),
            iRepeatMSec = (uint)Math.Max(0, rate),
            iBounceMSec = (uint)Math.Max(0, bounce)
        };
        if (!SystemParametersInfo(SPI_SETFILTERKEYS, (uint)Marshal.SizeOf<FILTERKEYS>(), ref fk, SPIF_SENDCHANGE | SPIF_UPDATEINIFILE))
            throw new InvalidOperationException(
                $"Windows no aplicó el cambio en vivo (error {Marshal.GetLastWin32Error()}). Quedó guardado en el registro.");
    }
}
