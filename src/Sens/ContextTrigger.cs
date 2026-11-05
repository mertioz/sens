using System.Runtime.InteropServices;
using System.Windows.Threading;
using static Sens.Win32;

namespace Sens;

internal sealed class ContextTrigger : IDisposable
{
    private sealed class Click(nint window, PixelPoint point)
    {
        public nint Window { get; } = window;
        public PixelPoint Point { get; } = point;
        public long Started { get; } = Environment.TickCount64;
        public TextSelection? Selection;
        public bool Released;
    }

    private readonly Dispatcher dispatcher;
    private readonly SelectionReader reader;
    private ContextChip? chip;
    private readonly DispatcherTimer timer;
    private readonly Action<TextSelection, PixelPoint> translate;
    private readonly HookProc callback;
    private readonly nint hook;
    private Click? pending;
    private (nint Window, long Time, TextSelection Text)? preview;
    private PixelPoint? leftStart;
    private (PixelPoint Point, long Time)? previousLeft;
    private int revision;
    private bool capturedLeft, disposed;

    public bool Available => hook != 0;

    public ContextTrigger(Dispatcher dispatcher, Action<TextSelection, PixelPoint> translate)
    {
        this.dispatcher = dispatcher;
        this.translate = translate;
        reader = new SelectionReader(dispatcher);
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(95), DispatcherPriority.Background, Tick, dispatcher);
        timer.Stop();
        callback = OnMouse;
        hook = InstallMouseHook(callback);
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code < 0 || disposed) return CallNextHookEx(hook, code, message, data);
        var kind = (int)message;
        if (kind is not (LeftDown or LeftUp or RightDown or RightUp or MiddleDown or MouseWheel or XButtonDown or HorizontalWheel))
            return CallNextHookEx(hook, code, message, data);
        if (pending is null && kind is (MiddleDown or MouseWheel or XButtonDown or HorizontalWheel))
            return CallNextHookEx(hook, code, message, data);

        if (kind == LeftUp && capturedLeft)
        {
            capturedLeft = false;
            return 1;
        }

        var point = Marshal.PtrToStructure<MouseEvent>(data).Point;
        if (kind == LeftDown && chip is { IsVisible: true } && GetWindowRect(chip.Handle, out var rect)
            && rect.Contains(point) && TopAt(point) == chip.Handle && pending is { Selection: { } text } click)
        {
            capturedLeft = true;
            dispatcher.BeginInvoke(() =>
            {
                Cancel();
                translate(text, click.Point);
            }, DispatcherPriority.Input);
            return 1;
        }

        // The hook does no cross-process accessibility calls. The native click is delivered immediately.
        var window = kind is RightDown or LeftUp ? GetForegroundWindow() : 0;
        if (kind == RightDown && TopAt(point) != window) window = 0;
        dispatcher.BeginInvoke(() => Handle(kind, point, window), DispatcherPriority.Input);
        return CallNextHookEx(hook, code, message, data);
    }

    private void Handle(int kind, PixelPoint point, nint window)
    {
        if (disposed) return;
        switch (kind)
        {
            case LeftDown:
                Cancel();
                preview = null;
                revision++;
                leftStart = point;
                break;
            case LeftUp:
                bool dragged = leftStart is { } start && !Near(start, point, 5);
                bool repeated = previousLeft is { } last && Environment.TickCount64 - last.Time < 520
                    && Near(last.Point, point, 7);
                leftStart = null;
                previousLeft = (point, Environment.TickCount64);
                if (window != 0 && !OwnedByUs(window) && (dragged || repeated))
                    WarmUp(point, window, revision);
                break;
            case RightDown:
                Cancel();
                if (window == 0 || OwnedByUs(window)) break;
                var click = pending = new Click(window, point);
                if (preview is { } candidate && candidate.Window == window
                    && Environment.TickCount64 - candidate.Time < 1050 && candidate.Text.IsNear(point))
                    click.Selection = candidate.Text;
                reader.Read(point, window, result =>
                {
                    if (pending != click || result is null) return;
                    click.Selection = result;
                    TryShow(click);
                });
                break;
            case RightUp:
                if (pending is { } ready && Near(ready.Point, point, 18))
                {
                    ready.Released = true;
                    TryShow(ready);
                    timer.Start();
                }
                else Cancel();
                break;
            default:
                Cancel();
                break;
        }
    }

    private async void WarmUp(PixelPoint point, nint window, int expected)
    {
        await Task.Delay(60);
        if (disposed || revision != expected || pending is not null || GetForegroundWindow() != window) return;
        reader.Read(point, window, text =>
        {
            if (disposed || revision != expected || text is null) return;
            preview = (window, Environment.TickCount64, text);
            if (pending is { } click && click.Window == window && click.Selection is null && text.IsNear(click.Point))
            {
                click.Selection = text;
                TryShow(click);
            }
        });
    }

    private void TryShow(Click click)
    {
        if (!click.Released || click.Selection is null || chip?.IsVisible == true ||
            Environment.TickCount64 - click.Started > 2200 || !IsForegroundOf(click.Window)) return;
        if (chip is null)
        {
            App.UseLightweightRendering();
            chip = new ContextChip();
            new System.Windows.Interop.WindowInteropHelper(chip).EnsureHandle();
        }
        PlaceChip(chip, click.Point);
        chip.ShowAnimated();
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (pending is not { } click || !IsForegroundOf(click.Window) || IsEscapeDown()
            || Environment.TickCount64 - click.Started > (chip?.IsVisible == true ? 8000 : 2300))
            Cancel();
    }

    public void Cancel()
    {
        pending = null;
        if (chip?.IsVisible == true) chip.Hide();
        timer.Stop();
    }

    public void Dispose()
    {
        disposed = true;
        if (hook != 0) UnhookWindowsHookEx(hook);
        Cancel();
        chip?.Close();
        reader.Dispose();
    }

    private static bool Near(PixelPoint a, PixelPoint b, int d) =>
        Math.Abs(a.X - b.X) <= d && Math.Abs(a.Y - b.Y) <= d;
}
