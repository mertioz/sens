using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sens;

internal static class Win32
{
    internal const int LeftDown = 0x201, LeftUp = 0x202, RightDown = 0x204, RightUp = 0x205;
    internal const int MiddleDown = 0x207, MouseWheel = 0x20A, XButtonDown = 0x20B, HorizontalWheel = 0x20E;
    internal const int HotKey = 0x312;
    private const uint NoActivate = 0x0010, NoSize = 0x0001, NoZOrder = 0x0004;

    internal static void PlaceChip(Window window, PixelPoint near) => Place(window, near, []);

    internal static void PlaceTranslation(Window window, PixelPoint near, IReadOnlyList<Rect> selection) =>
        Place(window, near, selection, chip: false);

    private static void Place(Window window, PixelPoint near, IReadOnlyList<Rect> selection, bool chip = true)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var bounds)) return;
        var monitor = MonitorFromPoint(near, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        uint currentDpi = Math.Max(96, GetDpiForWindow(handle));
        uint targetDpi = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : currentDpi;
        var width = (int)Math.Ceiling((bounds.Right - bounds.Left) * (double)targetDpi / currentDpi);
        var height = (int)Math.Ceiling((bounds.Bottom - bounds.Top) * (double)targetDpi / currentDpi);
        var gap = (int)Math.Ceiling(7 * targetDpi / 96.0);
        var area = info.Work;
        int x, y;
        if (chip)
        {
            x = near.X - width - gap;
            y = near.Y - height - gap;
            if (x < area.Left + gap) x = near.X + gap;
            if (y < area.Top + gap) y = near.Y + gap;
        }
        else
        {
            var work = new Rect(area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top);
            Rect selected = Rect.Empty;
            foreach (var rect in selection)
            {
                if (rect.IntersectsWith(work))
                    selected.Union(Rect.Intersect(work, rect));
            }
            if (selected.IsEmpty) selected = new Rect(near.X, near.Y, 0, 0);

            var right = (int)Math.Ceiling(selected.Right) + gap;
            var left = (int)Math.Floor(selected.Left) - width - gap;
            var below = (int)Math.Ceiling(selected.Bottom) + gap;
            var above = (int)Math.Floor(selected.Top) - height - gap;
            if (right + width <= area.Right - gap)
                (x, y) = (right, near.Y);
            else if (left >= area.Left + gap)
                (x, y) = (left, near.Y);
            else if (below + height <= area.Bottom - gap || above < area.Top + gap)
                (x, y) = (near.X, below);
            else
                (x, y) = (near.X, above);
        }

        x = Math.Clamp(x, area.Left + gap, Math.Max(area.Left + gap, area.Right - width - gap));
        y = Math.Clamp(y, area.Top + gap, Math.Max(area.Top + gap, area.Bottom - height - gap));
        if (x != bounds.Left || y != bounds.Top)
            SetWindowPos(handle, 0, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
    }
    internal static void NoFocus(nint window) =>
        SetWindowLongPtr(window, -20, GetWindowLongPtr(window, -20) | 0x08000080);

    internal static nint TopAt(PixelPoint p) => GetAncestor(WindowFromPoint(p), 2);
    internal static bool IsForegroundOf(nint window)
    {
        var active = GetForegroundWindow();
        if (active == window) return true;
        if (active == 0 || GetAncestor(active, 3) == 0) return false;
        if (GetAncestor(active, 3) == GetAncestor(window, 3)) return true;

        // Chromium can open the context menu as a separate foreground popup.
        GetWindowThreadProcessId(active, out var activeProcess);
        GetWindowThreadProcessId(window, out var sourceProcess);
        return activeProcess != 0 && activeProcess == sourceProcess &&
            (GetWindowLongPtr(active, -16).ToInt64() & 0x80C00000L) == 0x80000000L &&
            GetWindowTextLength(active) == 0;
    }

    internal static bool IsEscapeDown() => (GetAsyncKeyState(0x1B) & 0x8000) != 0;
    internal static bool OwnedByUs(nint window)
    {
        GetWindowThreadProcessId(window, out var process);
        return process == (uint)Environment.ProcessId;
    }

    internal static nint InstallMouseHook(HookProc callback) =>
        SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);

    internal delegate nint HookProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PixelPoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseEvent
    {
        public PixelPoint Point;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelRect
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Contains(PixelPoint point) =>
            point.X >= Left && point.Y >= Top && point.X < Right && point.Y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public PixelRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    internal static extern nint GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? module);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out PixelRect rect);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PixelPoint p);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(PixelPoint p, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
