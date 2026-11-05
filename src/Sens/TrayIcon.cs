using System.Runtime.InteropServices;

namespace Sens;

internal sealed class TrayIcon : IDisposable
{
    private const int Callback = 0x8001;
    private const int Select = 0x0400, KeySelect = 0x0401, LeftButtonUp = 0x0202;
    private const uint AddIcon = 0, DeleteIcon = 2, SetVersion = 4;
    private const uint MessageIconAndTip = 0x07, ShowTip = 0x80;
    internal static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private NotificationIconData data;
    private bool version4;

    public TrayIcon(nint window)
    {
        if (Environment.ProcessPath is not { } path || ExtractIconEx(path, 0, 0, out var icon, 1) == 0)
            return;

        data = new NotificationIconData
        {
            Size = (uint)Marshal.SizeOf<NotificationIconData>(),
            Window = window,
            Id = 1,
            Flags = MessageIconAndTip | ShowTip,
            CallbackMessage = Callback,
            Icon = icon,
            Tip = "Sens · Traduire"
        };
        Add();
    }

    internal void Add()
    {
        if (data.Icon == 0 || !ShellNotifyIcon(AddIcon, ref data)) return;
        data.Version = 4;
        version4 = ShellNotifyIcon(SetVersion, ref data);
    }

    internal bool IsActivation(int message, nint payload)
    {
        if (message != Callback) return false;
        var notification = (int)(payload & 0xffff);
        return version4 ? notification is Select or KeySelect : notification == LeftButtonUp;
    }

    public void Dispose()
    {
        if (data.Icon == 0) return;
        ShellNotifyIcon(DeleteIcon, ref data);
        DestroyIcon(data.Icon);
        data.Icon = 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotificationIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotificationIconData data);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string path, int index, nint large, out nint small, uint count);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}
