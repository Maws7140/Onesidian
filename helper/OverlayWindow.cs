using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace InkOverlay;

/// <summary>
/// A borderless, transparent window owned by Obsidian and kept exactly over the note's content area.
/// It is click-through until the pen comes near ("armed"), so touch and mouse keep working in Obsidian.
/// Strokes live in page coordinates (CSS px from the text column's top-left) and are saved as ISF,
/// the same serialized ink format OneNote uses.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    static readonly Brush ArmedBackground = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
    const int MaxHistory = 100;

    readonly Canvas root = new() { ClipToBounds = true, Background = Brushes.Transparent };
    // Tall enough for any note; the page itself grows to fit the ink (see SendExtent).
    // (1,000,000 px ran WPF's renderer out of memory on a window resize; 200,000 is ~80 screens of page.)
    // Wide enough for big images (ink in image pixels); taller for long PDFs when their pages arrive (ApplyLines).
    readonly InkCanvas ink = new() { Background = Brushes.Transparent, Width = 16_000, Height = 200_000 };
    readonly ScaleTransform scale = new(1, 1);
    readonly TranslateTransform translate = new();
    readonly DispatcherTimer tracker;
    readonly DispatcherTimer saveDebounce;
    readonly DispatcherTimer disarmTimer;
    readonly DispatcherTimer snapshotDebounce;
    readonly IntPtr hwnd;
    readonly bool gesturesAvailable;

    // Undo history: whole-page snapshots, kept as compressed ink (ISF, true colours): about 60 KB a step for
    // a full page, where live copies of every stroke cost over a megabyte a step. Simple for lasso moves and
    // point erases, and small enough to keep 100 steps.
    readonly List<byte[]> history = new();
    int historyIndex = -1;
    bool restoring;

    IntPtr obsidian;
    Command? layout;
    string? inkPath;
    bool armed;
    bool dirty;
    // Changed by an edit (not just moved to follow its text): only that is worth sending to OneNote.
    bool edited;
    bool scratchOut = true;
    bool dark;
    bool announcedShown;
    Command? lastTool;
    (int x, int y, int w, int h) placed;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 1;
        Height = 1;

        ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Colors.Black,
            Width = 2.5,
            Height = 2.5,
            FitToCurve = true,
            IgnorePressure = false,
        };
        // Pen eraser end erases whole strokes, like OneNote's default eraser.
        ink.EditingModeInverted = InkCanvasEditingMode.EraseByStroke;
        ink.RenderTransform = new TransformGroup { Children = { scale, translate } };
        root.Children.Add(ink);
        Content = root;

        Program.Mark("window");
        try { gesturesAvailable = new GestureRecognizer().IsRecognizerAvailable; } catch { gesturesAvailable = false; }
        Program.Mark("gesture recognizer");
        if (gesturesAvailable) ink.SetEnabledGestures(new[] { ApplicationGesture.ScratchOut });
        ink.Gesture += OnGesture;

        // Only the pen inks. Touch (palm) and real mouse input are swallowed while armed.
        ink.PreviewStylusDown += (_, e) =>
        {
            if (e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Touch) e.Handled = true;
        };
        ink.PreviewMouseDown += (_, e) =>
        {
            if (e.StylusDevice == null) e.Handled = true;
        };
        ink.SelectionMoved += (_, _) => { AnchorSelection(); Changed(); };
        ink.SelectionResized += (_, _) => { AnchorSelection(); Changed(); };
        InitInkToShape();
        InitWriting();
        InitPageTools();
        InitInkDom();
        // A scribble the recognizer let through as ink (see ScratchOutBy): erase what it covers, after the
        // other handlers of the new stroke have run.
        ink.StrokeCollected += (_, e) =>
        {
            if (ink.EditingMode != InkCanvasEditingMode.InkAndGesture || editorPen) return;
            var stroke = e.Stroke;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (ink.Strokes.Contains(stroke)) ScratchOutBy(stroke); });
        };

        PreviewStylusInAirMove += (_, _) => KeepArmed();
        PreviewStylusMove += (_, _) => KeepArmed();
        PreviewStylusDown += (_, _) => KeepArmed();
        StylusOutOfRange += (_, _) => DisarmSoon();
        StylusLeave += (_, _) => DisarmSoon();

        Program.Mark("input handlers");
        hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetExStyle(hwnd,
            Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, 0);
        Program.Mark("native window");
        InitSelfHeal();
        Program.Mark("sleep/wake hooks");

        tracker = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        tracker.Tick += (_, _) => Place();
        tracker.Start();

        saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        saveDebounce.Tick += (_, _) => { saveDebounce.Stop(); FlushSave(); };

        snapshotDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        snapshotDebounce.Tick += (_, _) => { snapshotDebounce.Stop(); Snapshot(); };

        disarmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        disarmTimer.Tick += (_, _) => Disarm();

        HookStrokes();
        ResetHistory();
        Protocol.Send(new { type = "capabilities", scratchOut = gesturesAvailable });
    }

    public void Handle(Command cmd)
    {
        switch (cmd.Type)
        {
            case "layout":
                bool zoomChanged = Math.Abs((layout?.Zoom ?? 1) - cmd.Zoom) > 1e-6;
                // The page moved under the ink (scrolling): the note's copy should be showing.
                bool scrolled = layout != null && (Math.Abs(layout.Ox - cmd.Ox) > 0.5 || Math.Abs(layout.Oy - cmd.Oy) > 0.5);
                layout = cmd;
                if (scrolled && domInk && nativeShown && !WantNative()) UpdateNativeVisibility(scrolled: true);
                // Pen and eraser sizes are on-screen sizes: re-derive them in ink units when zoom changes.
                if (zoomChanged && lastTool != null) ApplyTool(lastTool);
                if (cmd.Visible) SetDark(cmd.Dark);
                if (cmd.Visible) SetDomInk(cmd.DomInk);
                if (cmd.Visible && cmd.InkPath != inkPath) LoadInk(cmd.InkPath);
                if (obsidian == IntPtr.Zero || !Native.IsWindow(obsidian))
                {
                    obsidian = Native.FindObsidianWindow(cmd.Ppid, cmd.Title);
                    if (obsidian != IntPtr.Zero)
                    {
                        // Owned by Obsidian: stays directly above it, never above other apps.
                        Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, obsidian);
                        Protocol.Log("attached to Obsidian window");
                    }
                }
                Place();
                break;
            case "pen":
                Arm();
                break;
            case "tool":
                ApplyTool(cmd);
                break;
            case "undo":
                Restore(historyIndex - 1);
                break;
            case "redo":
                Restore(historyIndex + 1);
                break;
            case "deleteSelection":
                var selected = ink.GetSelectedStrokes();
                if (selected.Count > 0) ink.Strokes.Remove(selected);
                break;
            case "lines":
                OnLines(cmd);
                break;
            case "debug":
                Protocol.Log("layout from plugin:\n" + cmd.Message);
                break;
            case "onenote":
                SendToOneNote(cmd.Export);
                break;
            case "onenote-pull":
                PullFromOneNote(cmd.Pull);
                break;
            case "onenote-poll":
                PollOneNote(cmd.Poll);
                break;
            case "onenote-open":
                OpenInOneNote(cmd.PageId);
                break;
            case "replace":
                // "Restart the ink helper" command: same seamless handover as after sleep.
                Program.RequestReplacement("asked from Obsidian");
                break;
            case "reload":
                // Took over from an older helper: pick up anything it saved on its way out.
                if (inkPath != null) LoadInk(inkPath);
                break;
            case "space-applied":
                if (cmd.InkPath == inkPath) ShiftLines(cmd.Line, cmd.Count);
                break;
            case "ruler":
                ShowRuler(cmd.Visible);
                break;
            case "replay":
                StartReplay();
                break;
            case "replay-stop":
                StopReplay();
                break;
            case "rename-ink":
                RenameInk(cmd.From, cmd.To);
                break;
            default:
                HandleWriting(cmd);
                break;
        }
    }

    /// <summary>
    /// A note (or PDF, picture) was renamed or moved: its ink goes with it. `to` null: it was deleted, so its
    /// ink is let go (the plugin trashes the file). Done here, where the ink is saved, so a save can't land
    /// at the old path afterwards (renaming a folder used to bring the old folder back, with the ink in it).
    /// </summary>
    void RenameInk(string? from, string? to)
    {
        if (string.IsNullOrEmpty(from)) return;
        bool loaded = string.Equals(inkPath, from, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (to == null)
            {
                if (!loaded) return;
                // Deleted: nothing more to save for it.
                dirty = false;
                saveDebounce.Stop();
                inkPath = null;
                ink.Strokes = new StrokeCollection();
                HookStrokes();
                ResetHistory();
                Protocol.Log($"ink let go: {Path.GetFileName(from)} (its file was deleted)");
                return;
            }
            if (File.Exists(from) && File.Exists(to))
            {
                Protocol.Log($"ink not moved: {to} already exists (kept {from})");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(from)) File.Move(from, to);
            if (loaded)
            {
                // Saved where it lives now; the plugin's next layout names the new path and finds it loaded.
                inkPath = to;
                if (dirty) FlushSave();
            }
            Protocol.Log($"ink moved: {Path.GetFileName(from)} → {to}");
        }
        catch (Exception e)
        {
            Protocol.Log($"could not move ink {from} → {to}: {e.Message}");
        }
    }

    // ---------- dark mode ----------
    // Like OneNote: on a dark page, dark ink (black, dark greys) is shown light so it stays readable; and on
    // a light page (a PDF, the light theme) white ink is shown dark, or it would be invisible (a white pen
    // picked for dark notes wrote nothing you could see on a PDF). Only neutrals are flipped; coloured ink
    // stays as it is. Flipped strokes carry a marker so saving restores exactly those, and files always
    // hold the true colours.

    internal static readonly Guid FlippedForDark = new("6a8f3c2e-1d4b-4e7a-9c55-0b1e2f3a4d5c");

    static bool IsDarkInk(Color c) =>
        Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 12
        && Math.Max(c.R, Math.Max(c.G, c.B)) < 96;

    static bool IsLightInk(Color c) =>
        Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 12
        && Math.Min(c.R, Math.Min(c.G, c.B)) > 200;

    static Color Invert(Color c) => Color.FromArgb(c.A, (byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B));

    /// <summary>True colours â†’ what's shown on a dark page.</summary>
    internal static void ToDarkDisplay(StrokeCollection strokes) => Flip(strokes, IsDarkInk);

    /// <summary>True colours → what's shown on a light page.</summary>
    internal static void ToLightDisplay(StrokeCollection strokes) => Flip(strokes, IsLightInk);

    /// <summary>True colours → what's shown on this page (dark or light).</summary>
    void ToDisplay(StrokeCollection strokes)
    {
        if (dark) ToDarkDisplay(strokes);
        else ToLightDisplay(strokes);
    }

    static void Flip(StrokeCollection strokes, Func<Color, bool> unreadable)
    {
        foreach (var s in strokes)
        {
            var c = s.DrawingAttributes.Color;
            if (s.DrawingAttributes.ContainsPropertyData(FlippedForDark) || !unreadable(c)) continue;
            // Own copy: strokes can share one DrawingAttributes instance (ISF load, point-erase splits).
            var da = s.DrawingAttributes.Clone();
            da.Color = Invert(c);
            da.AddPropertyData(FlippedForDark, 1);
            s.DrawingAttributes = da;
        }
    }

    /// <summary>Undo <see cref="ToDarkDisplay"/>: back to true colours, marker removed.</summary>
    internal static void ToTrueColors(StrokeCollection strokes)
    {
        foreach (var s in strokes)
        {
            if (!s.DrawingAttributes.ContainsPropertyData(FlippedForDark)) continue;
            var da = s.DrawingAttributes.Clone();
            da.Color = Invert(da.Color);
            da.RemovePropertyData(FlippedForDark);
            s.DrawingAttributes = da;
        }
    }

    void SetDark(bool value)
    {
        if (value == dark) return;
        dark = value;
        Protocol.Log($"theme: {(dark ? "dark" : "light")}");
        ToTrueColors(ink.Strokes);
        ToDisplay(ink.Strokes);
        SchedulePublish();
        // (Snapshots are stored in true colours; they're mapped when restored.)
        if (lastTool != null) ApplyTool(lastTool);
    }

    void ApplyTool(Command cmd)
    {
        lastTool = cmd;
        // The Ink to Text pen writes like a pen, but its ink becomes text, so no shapes or scratch-out.
        // The Ink Editor pen is the same, plus editing gestures on the typed text.
        bool wasTextPen = textPen;
        currentTool = cmd.Tool ?? "pen";
        editorPen = cmd.Tool == "editor-pen";
        textPen = cmd.Tool is "text-pen" or "editor-pen";
        if (wasTextPen && !textPen) ConvertTextPen();
        shapesEnabled = cmd.Shapes && !textPen;
        // Effect pens have their own colours; what's saved is a representative plain one.
        string? effect = cmd.Tool == "pen" && !string.IsNullOrEmpty(cmd.Effect) ? cmd.Effect : null;
        var color = effect != null ? Pens.BaseColor(effect) : ParseColor(cmd.Color) ?? Colors.Black;
        bool flipped = effect == null && (dark ? IsDarkInk(color) : IsLightInk(color));
        if (flipped) color = Invert(color);
        // Sizes are what you see on screen; in a zoomed PDF/image the ink units are bigger or smaller.
        var size = Math.Clamp(cmd.Size, 0.5, 200) / Zoom;
        scratchOut = cmd.ScratchOut;

        if (cmd.Tool == "highlighter")
        {
            // Chisel tip, drawn under pen ink, like OneNote's highlighter.
            ink.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = color,
                IsHighlighter = true,
                StylusTip = StylusTip.Rectangle,
                Width = Math.Max(1, size / 3),
                Height = size,
                FitToCurve = cmd.Smooth,
                IgnorePressure = true,
            };
        }
        else
        {
            ink.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = color,
                StylusTip = StylusTip.Ellipse,
                Width = size,
                Height = size,
                FitToCurve = cmd.Smooth,
                IgnorePressure = !cmd.Pressure,
            };
        }
        // New strokes copy these attributes, marker included, so they save as the true colour.
        if (flipped) ink.DefaultDrawingAttributes.AddPropertyData(FlippedForDark, 1);
        // Pencil and effect pens: the style travels with each stroke (see Pens.cs).
        if (cmd.Tool == "pencil") ink.DefaultDrawingAttributes.AddPropertyData(Pens.PenStyleId, "pencil");
        else if (effect != null) ink.DefaultDrawingAttributes.AddPropertyData(Pens.PenStyleId, effect);

        var eraser = Math.Clamp(cmd.EraserSize, 2, 200) / Zoom;
        ink.EraserShape = new EllipseStylusShape(eraser, eraser);
        ink.EditingModeInverted = cmd.EraserEnd == "point" ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.EraseByStroke;

        bool gestures = gesturesAvailable && ((scratchOut && cmd.Tool is "pen" or "pencil") || editorPen);
        UseGestures(editorPen);
        RemoveToolPreview();
        ink.EditingMode = cmd.Tool switch
        {
            "eraser-stroke" => InkCanvasEditingMode.EraseByStroke,
            "eraser-point" => InkCanvasEditingMode.EraseByPoint,
            "lasso" => InkCanvasEditingMode.Select,
            // The pen drives these itself (PageTools.cs).
            "space" or "laser" => InkCanvasEditingMode.None,
            _ when IsShapeTool => InkCanvasEditingMode.None,
            _ => gestures ? InkCanvasEditingMode.InkAndGesture : InkCanvasEditingMode.Ink,
        };
        if (cmd.Tool != "lasso" && ink.GetSelectedStrokes().Count > 0) ink.Select(new StrokeCollection());
    }

    /// <summary>Scratch-out: scribbling over ink erases what it covers.</summary>
    void OnGesture(object sender, InkCanvasGestureEventArgs e)
    {
        if (editorPen)
        {
            if (!EditorGesture(e)) e.Cancel = true;
            return;
        }
        // Scratch-out anywhere in the answer counts (it was often second, "intermediate", behind "no gesture").
        bool scratch = e.GetGestureRecognitionResults().Any(r => r.ApplicationGesture == ApplicationGesture.ScratchOut && r.RecognitionConfidence != RecognitionConfidence.Poor);
        var hits = scratch ? Covered(e.Strokes.GetBounds(), e.Strokes) : new StrokeCollection();
        if (hits.Count == 0)
        {
            e.Cancel = true; // keep the stroke as ink
            return;
        }
        ink.Strokes.Remove(hits);
    }

    /// <summary>The ink a scribble over `bounds` covers (each stroke mostly inside), not counting the scribble.</summary>
    StrokeCollection Covered(Rect bounds, StrokeCollection scribble) =>
        new(ink.Strokes.HitTest(bounds, 60).Where(s => !scribble.Contains(s)));

    /// <summary>
    /// Scratch-out the recognizer missed. Windows' recognizer wants five or more even passes; real scribbles
    /// are looser and it called them "no gesture", so they stayed as ink. Here: a stroke going back and forth
    /// at least four times along its long side, each pass across most of it, over existing ink. Letters with
    /// loops (their passes are short), a Z (three passes) or a zigzag over empty page don't count.
    /// </summary>
    internal bool ScratchOutBy(Stroke stroke)
    {
        if (!IsScribble(stroke, Zoom)) return false;
        var hits = Covered(stroke.GetBounds(), new StrokeCollection { stroke });
        if (hits.Count == 0) return false;
        hits.Add(stroke);
        ink.Strokes.Remove(new StrokeCollection(hits.Where(ink.Strokes.Contains)));
        return true;
    }

    internal static bool IsScribble(Stroke stroke, double zoom)
    {
        var pts = stroke.StylusPoints;
        var b = stroke.GetBounds();
        double extent = Math.Max(b.Width, b.Height);
        if (pts.Count < 8 || extent * zoom < 12) return false;
        bool across = b.Width >= b.Height;
        double At(int i) => across ? pts[i].X : pts[i].Y;
        // Passes between turns; a turn only counts once the pen has come back a quarter of the way (no jitter).
        double start = At(0), extreme = At(0);
        int dir = 0, longPasses = 0;
        double length = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            length += Math.Sqrt(Math.Pow(pts[i].X - pts[i - 1].X, 2) + Math.Pow(pts[i].Y - pts[i - 1].Y, 2));
            double v = At(i);
            if (dir == 0)
            {
                if (Math.Abs(v - start) >= 0.25 * extent) { dir = Math.Sign(v - start); extreme = v; }
                continue;
            }
            if ((v - extreme) * dir > 0) { extreme = v; continue; }
            if (Math.Abs(v - extreme) >= 0.25 * extent)
            {
                if (Math.Abs(extreme - start) >= 0.5 * extent) longPasses++;
                start = extreme;
                extreme = v;
                dir = -dir;
            }
        }
        if (dir != 0 && Math.Abs(extreme - start) >= 0.5 * extent) longPasses++;
        return longPasses >= 4 && length >= 3 * extent;
    }

    static Color? ParseColor(string? hex)
    {
        if (hex == null) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return null; }
    }

    void Place()
    {
        var l = layout;
        bool show = l != null && l.Visible && obsidian != IntPtr.Zero
            && Native.IsWindow(obsidian) && !Native.IsIconic(obsidian);
        if (!show)
        {
            if (IsVisible) Hide();
            return;
        }

        var origin = new Native.POINT();
        Native.ClientToScreen(obsidian, ref origin);
        var rect = (
            x: origin.X + (int)Math.Round(l!.X * l.Dpr),
            y: origin.Y + (int)Math.Round(l.Y * l.Dpr),
            w: Math.Max(1, (int)Math.Round(l.W * l.Dpr)),
            h: Math.Max(1, (int)Math.Round(l.H * l.Dpr)));

        if (!IsVisible) Show();
        if (!announcedShown && inkPath != null)
        {
            // On screen with the note's ink: if we're replacing an older helper, it can go now.
            announcedShown = true;
            Protocol.Send(new { type = "shown" });
        }
        if (rect != placed)
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, rect.x, rect.y, rect.w, rect.h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            placed = rect;
        }

        // Ink units -> CSS px (Zoom: 1 for notes, CSS px per PDF point or per image pixel for PDFs and
        // images), then CSS px -> WPF device-independent px for the monitor we're on.
        double k = l.Dpr / VisualTreeHelper.GetDpi(this).DpiScaleX;
        scale.ScaleX = scale.ScaleY = k * Zoom;
        translate.X = l.Ox * k;
        translate.Y = l.Oy * k;
        double dip = VisualTreeHelper.GetDpi(this).DpiScaleX;
        SizeInk();
        CutHoles(l, k, rect.w / dip, rect.h / dip);
    }

    /// <summary>How tall the page of ink should be, in ink units (see ApplyLines).</summary>
    double wantedHeight = 200_000;
    /// <summary>On-screen size (device px) the ink canvas mustn't exceed: see SizeInk.</summary>
    const double MaxCanvasPixels = 480_000;

    /// <summary>
    /// Size the ink canvas: as big as the page needs, but never past ~500,000 device pixels on screen. Past
    /// that WPF draws nothing at all for it, so the window is see-through everywhere and the pen goes to
    /// Obsidian instead (on a PDF at 227% the pen just panned the page). A note at 100% on a 200% screen is
    /// 400,000; a PDF zoomed in, or Obsidian zoomed past 125%, went over.
    /// </summary>
    void SizeInk()
    {
        double perUnit = scale.ScaleX * VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (!(perUnit > 0)) return;
        double height = Math.Min(wantedHeight, MaxCanvasPixels / perUnit);
        double width = Math.Min(16_000, MaxCanvasPixels / perUnit);
        if (Math.Abs(ink.Height - height) > 0.5) ink.Height = height;
        if (Math.Abs(ink.Width - width) > 0.5) ink.Width = width;
    }

    string lastHoles = "";

    /// <summary>
    /// Leave Obsidian's menus, popovers and other plugins' floating toolbars see-through: nothing of the ink
    /// layer is drawn there, and fully transparent parts of a layered window let the pen and mouse through
    /// to what's underneath. (The alternative, hiding the whole layer, made the ink vanish for every menu.)
    /// </summary>
    void CutHoles(Command l, double k, double width, double height)
    {
        var holes = l.Holes ?? Array.Empty<Hole>();
        var key = $"{width:0}x{height:0}|" + string.Join(";", holes.Select(h => $"{h.X:0},{h.Y:0},{h.W:0},{h.H:0}"));
        if (key == lastHoles) return;
        lastHoles = key;
        if (holes.Length == 0) { root.Clip = null; return; }
        Geometry visible = new RectangleGeometry(new Rect(0, 0, Math.Max(1, width), Math.Max(1, height)));
        foreach (var h in holes)
        {
            var r = new Rect((h.X - l.X) * k, (h.Y - l.Y) * k, Math.Max(0, h.W * k), Math.Max(0, h.H * k));
            visible = Geometry.Combine(visible, new RectangleGeometry(r), GeometryCombineMode.Exclude, null);
        }
        visible.Freeze();
        root.Clip = visible;
    }

    /// <summary>How many CSS px one ink unit is right now (PDF/image zoom; 1 in notes).</summary>
    double Zoom => layout is { Zoom: > 0 } l ? l.Zoom : 1;

    void Arm()
    {
        if (!armed)
        {
            armed = true;
            root.Background = ArmedBackground;
            Native.SetExStyle(hwnd, 0, Native.WS_EX_TRANSPARENT);
            Protocol.Log($"armed (tablets: {Tablet.TabletDevices.Count})");
            UpdateNativeVisibility();
        }
        KeepArmed();
    }

    void KeepArmed()
    {
        if (!armed) return;
        disarmTimer.Stop();
        disarmTimer.Interval = TimeSpan.FromSeconds(2);
        disarmTimer.Start();
    }

    void DisarmSoon()
    {
        if (!armed) return;
        disarmTimer.Stop();
        disarmTimer.Interval = TimeSpan.FromMilliseconds(250);
        disarmTimer.Start();
    }

    void Disarm()
    {
        disarmTimer.Stop();
        if (!armed) return;
        // Never drop the pen mid-stroke.
        if (Stylus.CurrentStylusDevice is { InAir: false }) { disarmTimer.Start(); return; }
        armed = false;
        root.Background = Brushes.Transparent;
        Native.SetExStyle(hwnd, Native.WS_EX_TRANSPARENT, 0);
        Protocol.Log("disarmed");
        UpdateNativeVisibility();
    }

    void LoadInk(string? path)
    {
        FlushSave();
        inkPath = path;
        var strokes = new StrokeCollection();
        if (path != null && File.Exists(path))
        {
            try
            {
                using var fs = File.OpenRead(path);
                strokes = new StrokeCollection(fs);
            }
            catch (Exception e)
            {
                Protocol.Log($"could not read {path}: {e.Message}");
            }
        }
        ToDisplay(strokes);
        Pens.Stylize(strokes);
        StopReplay();
        ink.Strokes = strokes;
        HookStrokes();
        dirty = false;
        ResetAnchors();
        ResetHistory();
        publishTimer.Stop();
        domReset = true;
        PublishInk();
    }

    void HookStrokes()
    {
        ink.Strokes.StrokesChanged += (_, e) =>
        {
            if (!restoring && !importing) foreach (var s in e.Added) OnStrokeAdded(s);
            // Pencil/effect strokes made by the canvas itself (drawn, split by the eraser) get their look once
            // the change is done (after ink-to-shape, which runs at a higher priority).
            if (e.Added.Any(s => s is not StyledStroke && Pens.StyleOf(s.DrawingAttributes) != null))
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Pens.Stylize(ink.Strokes));
            Changed();
        };
        SendExtent();
        SendSelection();
    }

    /// <summary>Tell the plugin how far down the ink reaches, so the note can grow to fit it.</summary>
    void SendExtent()
    {
        double bottom = ink.Strokes.Count == 0 ? 0 : Math.Ceiling(ink.Strokes.GetBounds().Bottom);
        Protocol.Send(new { type = "extent", path = inkPath, bottom });
        // Whatever moved the ink also changes the note's copy of it.
        SchedulePublish();
    }

    void Changed()
    {
        SendExtent();
        if (restoring) return;
        dirty = true;
        edited = true;
        saveDebounce.Stop();
        saveDebounce.Start();
        snapshotDebounce.Stop();
        snapshotDebounce.Start();
    }

    void ResetHistory()
    {
        history.Clear();
        history.Add(Pack(ink.Strokes));
        historyIndex = 0;
        snapshotDebounce.Stop();
        SendHistory();
    }

    void Snapshot()
    {
        if (historyIndex < history.Count - 1) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        history.Add(Pack(ink.Strokes));
        if (history.Count > MaxHistory) history.RemoveAt(0);
        historyIndex = history.Count - 1;
        SendHistory();
    }

    void Restore(int index)
    {
        // Commit a pending snapshot first so undo never skips the latest change.
        if (snapshotDebounce.IsEnabled) { snapshotDebounce.Stop(); Snapshot(); }
        if (index < 0 || index >= history.Count) return;
        historyIndex = index;
        restoring = true;
        StopReplay();
        ink.Select(new StrokeCollection());
        ink.Strokes = Unpack(history[index]);
        HookStrokes();
        Reanchor(); // the snapshot may be from a different layout
        restoring = false;
        dirty = true;
        FlushSave();
        SendHistory();
    }

    static byte[] Pack(StrokeCollection strokes)
    {
        var copy = strokes.Clone();
        ToTrueColors(copy);
        using var ms = new MemoryStream();
        copy.Save(ms, true);
        return ms.ToArray();
    }

    StrokeCollection Unpack(byte[] isf)
    {
        using var ms = new MemoryStream(isf);
        var strokes = new StrokeCollection(ms);
        ToDisplay(strokes);
        Pens.Stylize(strokes);
        return strokes;
    }

    void SendHistory() =>
        Protocol.Send(new { type = "history", canUndo = historyIndex > 0, canRedo = historyIndex < history.Count - 1 });

    public void FlushSave()
    {
        if (!dirty || inkPath == null) return;
        dirty = false;
        ScheduleIndex();
        try
        {
            if (ink.Strokes.Count == 0)
            {
                if (File.Exists(inkPath)) File.Delete(inkPath);
                return;
            }
            var tmp = inkPath + ".tmp";
            // Save true colours, never the dark-mode display colours.
            var toSave = ink.Strokes.Clone();
            ToTrueColors(toSave);
            using (var fs = File.Create(tmp)) toSave.Save(fs, true);
            File.Move(tmp, inkPath, overwrite: true);
            // Announced only for edits: the plugin syncs announced saves to OneNote.
            if (edited) Protocol.Send(new { type = "saved", path = inkPath, strokes = ink.Strokes.Count, edited });
            edited = false;
        }
        catch (Exception e)
        {
            Protocol.Log($"could not save {inkPath}: {e.Message}");
        }
    }
}
