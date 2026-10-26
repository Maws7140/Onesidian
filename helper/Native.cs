using System;
using System.Runtime.InteropServices;
using System.Text;

namespace InkOverlay;

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_HWNDPARENT = -8;
    public const long WS_EX_TRANSPARENT = 0x20;
    public const long WS_EX_TOOLWINDOW = 0x80;
    public const long WS_EX_LAYERED = 0x80000;
    public const long WS_EX_NOACTIVATE = 0x8000000;
    public const uint SWP_NOZORDER = 0x4;
    public const uint SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static void SetExStyle(IntPtr hwnd, long add, long remove)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long next = (style | add) & ~remove;
        if (next != style) SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(next));
    }

    /// <summary>Find Obsidian's top-level window: owned by the plugin's parent (Electron main) process, with a matching title.</summary>
    public static IntPtr FindObsidianWindow(int ppid, string? title)
    {
        IntPtr byPid = IntPtr.Zero, byTitle = IntPtr.Zero;
        var buf = new StringBuilder(512);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            buf.Clear();
            GetWindowText(h, buf, buf.Capacity);
            bool titleMatch = title != null && buf.ToString() == title;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == ppid && titleMatch) { byPid = h; return false; }
            if (titleMatch && byTitle == IntPtr.Zero) byTitle = h;
            return true;
        }, IntPtr.Zero);
        return byPid != IntPtr.Zero ? byPid : byTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    /// <summary>Opt out of EcoQoS (efficiency mode) execution-speed throttling.</summary>
    public static void DisableThrottling()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1 /* EXECUTION_SPEED */, StateMask = 0 /* off */ };
        SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>A window's class, title, place, enabled state, styles and owner (for diagnosing).</summary>
    public static string Describe(IntPtr hwnd)
    {
        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        GetWindowRect(hwnd, out var r);
        long style = GetWindowLongPtr(hwnd, -16).ToInt64(), ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        return $"{hwnd:X} class={ClassName(hwnd)} title='{title}' rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) enabled={IsWindowEnabled(hwnd)} style={style:X} ex={ex:X} owner={GetWindow(hwnd, 4):X} foreground={GetForegroundWindow() == hwnd}";
    }
}
