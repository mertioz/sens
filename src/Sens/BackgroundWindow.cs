using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sens;

// A hidden top-level HWND receives both the global shortcut and taskbar broadcasts.
internal sealed class BackgroundWindow : IDisposable
{
    private const string ClassName = "Sens.BackgroundWindow";
    private readonly nint module = Win32.GetModuleHandle(null);
    private readonly WindowProc callback;
    public nint Handle { get; }

    public BackgroundWindow(Func<int, nint, nint, bool> onMessage)
    {
        callback = (window, message, wParam, lParam) =>
            onMessage(message, wParam, lParam) ? 0 : DefWindowProc(window, message, wParam, lParam);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(callback),
            Module = module,
            Name = ClassName
        };
        if (RegisterClassEx(ref windowClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        Handle = CreateWindowEx(0x80, ClassName, "Sens background", 0x80000000,
            0, 0, 0, 0, 0, 0, module, 0);
        if (Handle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            UnregisterClass(ClassName, module);
            throw new Win32Exception(error);
        }
    }

    public void Dispose()
    {
        DestroyWindow(Handle);
        UnregisterClass(ClassName, module);
        GC.KeepAlive(callback);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Module, Icon, Cursor, Background;
        public string? MenuName;
        public string Name;
        public nint SmallIcon;
    }

    private delegate nint WindowProc(nint window, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint module, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, nint module);
}
