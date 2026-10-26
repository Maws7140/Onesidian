using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Ink;

namespace InkOverlay;

// Windows' Math Input Control (micaut.dll, the engine behind the Math Input Panel and OneNote's old Ink
// Equation editor): handwriting → MathML, offline. Interface layout from its type library.

[ComImport, Guid("EBA615AA-FAC6-4738-BA5F-FF09E9FE473E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface IMathInputControl
{
    void Show();
    void Hide();
    short IsVisible();
    void GetPosition(out int left, out int top, out int right, out int bottom);
    void SetPosition(int left, int top, int right, int bottom);
    void Clear();
    void SetCustomPaint(int element, short paint);
    void SetCaptionText([MarshalAs(UnmanagedType.BStr)] string caption);
    void LoadInk([MarshalAs(UnmanagedType.Interface)] IInkDisp ink);
    void SetOwnerWindow(IntPtr owner);
    void EnableExtendedButtons(short extended);
    int GetPreviewHeight();
    void SetPreviewHeight(int height);
    void EnableAutoGrow(short autoGrow);
    void AddFunctionName([MarshalAs(UnmanagedType.BStr)] string name);
    void RemoveFunctionName([MarshalAs(UnmanagedType.BStr)] string name);
}

/// <summary>Only passed through to LoadInk (methods are called late-bound).</summary>
[ComImport, Guid("9D398FA0-C4E2-4FCD-9973-975CAAF47EA6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
interface IInkDisp { }

[ComVisible(true), Guid("683336B5-A47D-4358-96F9-875A472AE70A"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMathInputControlEvents
{
    [DispId(0)] void Insert([MarshalAs(UnmanagedType.BStr)] string recoResult);
    [DispId(1)] void Close();
    [DispId(2)] void Paint(IntPtr hdc, int left, int top, int right, int bottom, int element, int state);
    [DispId(3)] void Clear();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class MathInputEvents : IMathInputControlEvents
{
    public event Action<string>? Inserted;
    public event Action? Closed;
    public void Insert(string recoResult) => Inserted?.Invoke(recoResult);
    public void Close() => Closed?.Invoke();
    public void Paint(IntPtr hdc, int left, int top, int right, int bottom, int element, int state) { }
    public void Clear() { }
}

/// <summary>
/// Ink to Math. `RecognizeAsync` reads handwriting without showing anything: the panel is loaded off screen
/// and its Insert button pressed through UI Automation. `EditAsync` is OneNote's "Fix it": the same panel on
/// screen with the ink in it, where you can correct symbols (select-and-correct, erase, rewrite) and Insert.
/// </summary>
public sealed class MathInput : IDisposable
{
    static readonly Guid Clsid = new("C561816C-14D8-4090-830C-98D994B21C7B");
    static readonly Guid EventsIid = new("683336B5-A47D-4358-96F9-875A472AE70A");

    readonly IMathInputControl control;
    readonly MathInputEvents events = new();
    readonly IConnectionPoint connection;
    readonly int cookie;
    TaskCompletionSource<string?>? pending;

    public MathInput(IntPtr owner)
    {
        control = (IMathInputControl)Activator.CreateInstance(Type.GetTypeFromCLSID(Clsid, true)!)!;
        var container = (IConnectionPointContainer)control;
        var iid = EventsIid;
        container.FindConnectionPoint(ref iid, out connection);
        connection.Advise(events, out cookie);
        events.Inserted += mathml => { control.Hide(); pending?.TrySetResult(mathml); };
        events.Closed += () => pending?.TrySetResult(null);
        if (owner != IntPtr.Zero) control.SetOwnerWindow(owner);
        control.EnableExtendedButtons(-1);
    }

    /// <summary>The MathML for some handwriting, or null if nothing was recognized.</summary>
    public async Task<string?> RecognizeAsync(StrokeCollection strokes)
    {
        // Off screen: same panel, nothing flashes. It takes ink once it's shown, and recognizes it as it
        // loads; give it a moment.
        var result = Begin();
        control.SetPosition(-32000, -32000, -32000 + 900, -32000 + 500);
        control.Show();
        Load(strokes);
        // Insert does nothing until the panel has read the ink, which takes a moment (longer on first use,
        // while the recognizer loads): keep pressing it until the result arrives, for up to 10 seconds.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!result.IsCompleted && clock.ElapsedMilliseconds < 10_000)
        {
            await Task.WhenAny(result, Task.Delay(clock.ElapsedMilliseconds < 1000 ? 150 : 400));
            if (!result.IsCompleted) PressInsert();
        }
        if (!result.IsCompleted) { control.Hide(); pending?.TrySetResult(null); }
        else Protocol.Log($"math: read in {clock.ElapsedMilliseconds} ms");
        return await result;
    }

    /// <summary>The panel on screen at `screen` (device px) with the ink loaded, until Insert or close.</summary>
    public Task<string?> EditAsync(StrokeCollection strokes, Int32Rect screen, string caption)
    {
        var result = Begin();
        control.SetCaptionText(caption);
        control.SetPosition(screen.X, screen.Y, screen.X + screen.Width, screen.Y + screen.Height);
        control.Show();
        Load(strokes);
        BringToFront();
        // Again once it has settled (it can create or re-show its inner window after Show returns).
        _ = Task.Delay(300).ContinueWith(_ => BringToFront(), TaskScheduler.FromCurrentSynchronizationContext());
        return result;
    }

    /// <summary>
    /// The panel is two windows: a frame and, inside it, the pen area and buttons, a window of its own with no
    /// owner, which sinks behind whatever is active. Tie the inner window to the frame (owned windows stay above
    /// their owner) and keep the frame on top (always on top when nothing owns it, as in its own process).
    /// </summary>
    static void BringToFront()
    {
        const int GWLP_HWNDPARENT = -8;
        const uint NoSizeMoveActivate = 0x0001 | 0x0002 | 0x0010;
        var windows = PanelWindows();
        var frame = windows.FirstOrDefault(w => Native.ClassName(w) == "MathInputControl_Window");
        foreach (var w in windows.Where(w => Native.ClassName(w) == "MathInput_Window"))
            if (frame != IntPtr.Zero) Native.SetWindowLongPtr(w, GWLP_HWNDPARENT, frame);
        var top = frame != IntPtr.Zero && Native.GetWindow(frame, 4 /* GW_OWNER */) == IntPtr.Zero ? Native.HWND_TOPMOST : IntPtr.Zero;
        foreach (var w in windows) Native.SetWindowPos(w, top, 0, 0, 0, 0, NoSizeMoveActivate);
    }

    /// <summary>What the overlay sends the Fix it process: the ink (ISF, base64), where, and the caption.</summary>
    public sealed class PanelRequest
    {
        public string Isf { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public string Caption { get; set; } = "";
    }

    /// <summary>
    /// `InkOverlay --math-panel`: Fix it in a process of its own. Hosted in the overlay, the panel showed (on
    /// top, where the pen landed) but took no input, the likely cause being what it shared there: a thread with
    /// the ink layer's WPF window and, through owner windows, an input queue with Obsidian. Here it has its own
    /// thread and queue, nothing owns it, and it stays on top until Insert or close. Reads one PanelRequest line; writes {"mathml": ...} (null when
    /// closed) and exits. Closing its stdin cancels.
    /// </summary>
    public static int RunPanel()
    {
        var line = Console.In.ReadLine() ?? "{}";
        var request = System.Text.Json.JsonSerializer.Deserialize<PanelRequest>(line[Math.Max(0, line.IndexOf('{'))..], // (past any byte-order mark)
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        using var math = new MathInput(IntPtr.Zero);
        var strokes = new StrokeCollection(new MemoryStream(Convert.FromBase64String(request.Isf)));
        math.EditAsync(strokes, new Int32Rect(request.X, request.Y, request.W, request.H), request.Caption).ContinueWith(t =>
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { mathml = t.IsCompletedSuccessfully ? t.Result : null }));
            Console.Out.Flush();
            dispatcher.InvokeShutdown();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        new System.Threading.Thread(() => { Console.In.ReadToEnd(); dispatcher.BeginInvoke(math.Cancel); }) { IsBackground = true }.Start();
        // Self-test: press Insert until it answers (it ignores Insert until it has read the ink).
        if (Environment.GetEnvironmentVariable("ONI_PANEL_TEST") == "insert")
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 40; i++) { await dispatcher.InvokeAsync(() => PressInsert()); await Task.Delay(300); }
            });
        System.Windows.Threading.Dispatcher.Run();
        return 0;
    }

    public void Cancel()
    {
        control.Hide();
        pending?.TrySetResult(null);
    }

    Task<string?> Begin()
    {
        pending?.TrySetResult(null);
        pending = new TaskCompletionSource<string?>();
        return pending.Task;
    }

    void Load(StrokeCollection strokes)
    {
        control.Clear();
        control.LoadInk((IInkDisp)ToInkObject(strokes));
    }

    /// <summary>
    /// The strokes as a COM Ink object (what the panel takes). It has refused selections ("the parameter is
    /// incorrect") that couldn't be reproduced later: then it gets plain copies (points, colour, size), and if
    /// even that fails, only the strokes it takes one by one.
    /// </summary>
    internal static object ToInkObject(StrokeCollection strokes)
    {
        try { return LoadInkObject(strokes); }
        catch (System.Reflection.TargetInvocationException e)
        {
            Protocol.Log($"math: ink refused ({e.InnerException?.Message}); retrying with plain strokes");
        }
        var plain = new StrokeCollection(strokes.Where(s => s.StylusPoints.Count > 0).Select(s => new Stroke(s.StylusPoints.Clone(),
            new DrawingAttributes { Color = s.DrawingAttributes.Color, Width = s.DrawingAttributes.Width, Height = s.DrawingAttributes.Height })));
        try { return LoadInkObject(plain); }
        catch (System.Reflection.TargetInvocationException) { }
        var taken = new StrokeCollection(plain.Where(s =>
        {
            try { LoadInkObject(new StrokeCollection { s }); return true; }
            catch (System.Reflection.TargetInvocationException) { return false; }
        }));
        Protocol.Log($"math: {plain.Count - taken.Count} of {plain.Count} strokes refused");
        return LoadInkObject(taken);
    }

    static object LoadInkObject(StrokeCollection strokes)
    {
        var isf = new MemoryStream();
        var copy = strokes.Clone();
        OverlayWindow.ToTrueColors(copy);
        copy.Save(isf, false);
        var ink = Activator.CreateInstance(Type.GetTypeFromProgID("msinkaut.InkObject", true)!)!;
        ink.GetType().InvokeMember("Load", System.Reflection.BindingFlags.InvokeMethod, null, ink, new object[] { isf.ToArray() });
        return ink;
    }

    /// <summary>Press the panel's Insert button (UI Automation). False until it's there and enabled.</summary>
    static bool PressInsert()
    {
        // Each of the panel's windows (its inner window can be owned by its frame, so not a top-level element).
        foreach (var window in PanelWindows().Select(AutomationElement.FromHandle))
        {
            var insert = window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Insert")));
            if (insert == null || !insert.Current.IsEnabled) continue;
            if (insert.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) { ((InvokePattern)p).Invoke(); return true; }
        }
        return false;
    }

    /// <summary>The panel's top-level windows (this process's visible windows that aren't WPF's).</summary>
    public static System.Collections.Generic.List<IntPtr> PanelWindows()
    {
        var found = new System.Collections.Generic.List<IntPtr>();
        uint me = (uint)Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == me && Native.IsWindowVisible(h) && !Native.ClassName(h).StartsWith("HwndWrapper")) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }
    /// <summary>Everything the panel's windows expose to UI Automation (for diagnosing).</summary>
    public static string DescribeWindows()
    {
        var sb = new System.Text.StringBuilder();
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, Environment.ProcessId));
        foreach (AutomationElement w in windows)
        {
            sb.AppendLine($"window '{w.Current.Name}' class {w.Current.ClassName}");
            foreach (AutomationElement e in w.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                sb.AppendLine($"   {e.Current.ControlType.ProgrammaticName} '{e.Current.Name}' enabled={e.Current.IsEnabled}");
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try { connection.Unadvise(cookie); } catch (COMException) { }
        try { control.Hide(); } catch (COMException) { }
        Marshal.ReleaseComObject(control);
    }
}
