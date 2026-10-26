using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace InkOverlay;

/// <summary>
/// WPF's pen input can die after the Surface sleeps (Modern Standby): the pen then arrives as plain
/// mouse input, which we discard as "not the pen", so nothing draws. We restart (via the plugin) when
/// the screen comes back on after being off, and whenever pen input shows up as mouse input.
/// </summary>
public sealed partial class OverlayWindow
{
    const int WM_POWERBROADCAST = 0x0218;
    const int PBT_POWERSETTINGCHANGE = 0x8013;
    static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    // Mouse messages synthesised from pen/touch carry this signature in GetMessageExtraInfo.
    const long MI_WP_SIGNATURE = 0xFF515700;
    const long SIGNATURE_MASK = 0xFFFFFF00;
    const long TOUCH_FLAG = 0x80;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;
    }

    [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetMessageExtraInfo();

    bool displayWasOff;

    void InitSelfHeal()
    {
        var guid = GUID_CONSOLE_DISPLAY_STATE;
        RegisterPowerSettingNotification(hwnd, ref guid, 0);
        HwndSource.FromHwnd(hwnd)?.AddHook(PowerHook);

        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => Program.RequestReplacement("resumed from sleep"));
        };

        // handledEventsToo: the mouse-rejection handler marks these handled before we see them.
        ink.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.StylusDevice != null) return;
            long info = GetMessageExtraInfo().ToInt64();
            bool fromPenOrTouch = (info & SIGNATURE_MASK) == MI_WP_SIGNATURE;
            bool fromPen = fromPenOrTouch && (info & TOUCH_FLAG) == 0;
            if (fromPen) Program.RequestReplacement("pen input arrived as mouse (stylus stack lost)");
        }), true);
    }

    IntPtr PowerHook(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE)
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
            {
                // 0 = off, 1 = on, 2 = dimmed.
                if (setting.Data == 0) displayWasOff = true;
                else if (setting.Data == 1 && displayWasOff)
                {
                    displayWasOff = false;
                    Dispatcher.BeginInvoke(() => Program.RequestReplacement("display back on after sleep"));
                }
            }
        }
        return IntPtr.Zero;
    }
}
