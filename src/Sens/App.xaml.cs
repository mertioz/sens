using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Sens;

public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activate;
    private RegisteredWaitHandle? wait;
    private TrayIcon? tray;
    private ContextTrigger? context;
    private MainWindow? panel;
    private BackgroundWindow? source;
    private const int Shortcut = 1;
    private bool hotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool background = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        const string name = "Local\\Sens.Windows.8e0feaa1";
        // Create the signal before checking the mutex so a simultaneous manual launch can queue an open.
        activate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Open");
        instance = new Mutex(false, name, out var first);
        if (!first)
        {
            if (!background) activate.Set();
            Shutdown();
            return;
        }

        source = new BackgroundWindow(HandleMessage);
        hotkey = Win32.RegisterHotKey(source.Handle, Shortcut, 0x4003, 0x54);

        tray = new TrayIcon(source.Handle);
        wait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => Dispatcher.BeginInvoke(OpenPanel),
            null, Timeout.Infinite, executeOnlyOnce: false);
        context = new ContextTrigger(Dispatcher, (selection, point) => GetPanel().OpenSelection(selection, point));
        if (!background) OpenPanel();
    }

    private MainWindow GetPanel()
    {
        if (panel is not null) return panel;
        UseLightweightRendering();
        panel = new MainWindow();
        MainWindow = panel;
        panel.SetIntegrationStatus(hotkey, context?.Available == true);
        panel.Closed += (_, _) => Shutdown();
        return panel;
    }

    private void OpenPanel()
    {
        context?.Cancel();
        GetPanel().Open();
    }

    // Avoid initializing a GPU pipeline and its driver for these small, transparent panels.
    internal static void UseLightweightRendering() => RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

    private bool HandleMessage(int msg, nint wParam, nint lParam)
    {
        if ((msg == Win32.HotKey && wParam == Shortcut) || tray?.IsActivation(msg, lParam) == true)
        {
            Dispatcher.BeginInvoke(OpenPanel);
            return true;
        }
        if (msg == TrayIcon.TaskbarCreated) tray?.Add();
        return false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        context?.Dispose();
        tray?.Dispose();
        if (source is not null)
        {
            if (hotkey) Win32.UnregisterHotKey(source.Handle, Shortcut);
            source.Dispose();
        }
        wait?.Unregister(null);
        activate?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
