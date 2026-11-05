using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using static Sens.Win32;

namespace Sens;

internal sealed record TextSelection(string Text, Rect[] Rects)
{
    public bool IsNear(PixelPoint p) => Rects.Any(rect =>
        p.X >= rect.Left - 12 && p.X <= rect.Right + 12 &&
        p.Y >= rect.Top - 10 && p.Y <= rect.Bottom + 10);
}

internal sealed class SelectionReader(Dispatcher dispatcher) : IDisposable
{
    private sealed record Probe(PixelPoint Point, nint Source, Action<TextSelection?> Done);
    private readonly Channel<Probe> probes = Channel.CreateBounded<Probe>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
    });
    private Thread? worker;

    public void Read(PixelPoint point, nint source, Action<TextSelection?> done)
    {
        if (worker is null)
        {
            worker = new Thread(Run) { IsBackground = true, Name = "Sens UI Automation" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }
        probes.Writer.TryWrite(new Probe(point, source, done));
    }

    private void Run()
    {
        while (probes.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            if (!probes.Reader.TryRead(out var probe)) continue;
            TextSelection? selection = null;
            try { selection = GetSelection(probe.Point, probe.Source); }
            catch (Exception e) when (e is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
            {
                // The provider can disappear while the application opens its native menu.
            }
            dispatcher.BeginInvoke(() => probe.Done(selection), DispatcherPriority.Input);
        }
    }

    private static TextSelection? GetSelection(PixelPoint point, nint source)
    {
        var root = AutomationElement.FromHandle(source);
        var processId = root.Current.ProcessId;
        var focused = AutomationElement.FocusedElement;
        if (focused is not null && focused.Current.ProcessId == processId)
        {
            if (focused.Current.IsPassword) return null;
            var selected = ReadRanges(focused);
            if (selected is not null && selected.IsNear(point))
                return selected;
        }

        var atPoint = AutomationElement.FromPoint(new Point(point.X, point.Y));
        if (atPoint is not null && atPoint.Current.ProcessId == processId)
        {
            for (var item = atPoint; item is not null && item.Current.ProcessId == processId;
                 item = TreeWalker.RawViewWalker.GetParent(item))
            {
                if (item.Current.IsPassword) return null;
                var selected = ReadRanges(item);
                if (selected is not null && (selected.Rects.Length == 0 || selected.IsNear(point)))
                    return selected;
                if (item == root) break;
            }
        }

        return null;
    }

    private static TextSelection? ReadRanges(AutomationElement item)
    {
        if (!item.TryGetCurrentPattern(TextPattern.Pattern, out var value) ||
            value is not TextPattern pattern || pattern.SupportedTextSelection == SupportedTextSelection.None)
            return null;
        var ranges = pattern.GetSelection();
        if (ranges is null || ranges.Length == 0) return null;

        var text = new List<string>();
        var rects = new List<Rect>();
        int remaining = Translator.InputLimit + 1;
        foreach (var range in ranges)
        {
            if (remaining <= 0) break;
            var part = range.GetText(remaining);
            if (string.IsNullOrWhiteSpace(part)) continue;
            remaining -= part.Length + 1;
            text.Add(part);
            rects.AddRange(range.GetBoundingRectangles().Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0));
        }
        return text.Count > 0 ? new TextSelection(string.Join("\n", text), [.. rects]) : null;
    }

    public void Dispose() => probes.Writer.TryComplete();
}
