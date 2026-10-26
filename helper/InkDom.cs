using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Threading;

namespace InkOverlay;

/// <summary>
/// Ink that scrolls with the note. This window is separate from Obsidian, so following a scroll means
/// measuring, messaging and redrawing every frame, and the ink trails behind the text. So while you aren't
/// writing, the plugin draws the ink inside the note itself (as SVG, the exact outlines Windows Ink draws),
/// where it scrolls as part of the page; this window shows its own ink only while the pen is near (and while
/// something needs it: a selection, a drag, the laser, a replay), which is when inking has to be live.
/// </summary>
public sealed partial class OverlayWindow
{
    /// <summary>The plugin draws the ink in the note when the pen's away (notes; set by the layout).</summary>
    bool domInk;
    bool nativeShown = true;
    DispatcherTimer publishTimer = null!;

    /// <summary>Each stroke's id in the note's copy, and whether the copy has its current shape.</summary>
    sealed class DomId { public int Id; public bool Sent; }
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Stroke, DomId> domIds = new();
    int nextDomId;
    bool domReset = true;

    DomId IdOf(Stroke s)
    {
        if (domIds.TryGetValue(s, out var id)) return id;
        id = new DomId { Id = ++nextDomId };
        domIds.Add(s, id);
        // Moved, reshaped or recoloured: send it again.
        void Stale(object? _, EventArgs __) => id.Sent = false;
        s.StylusPointsChanged += Stale;
        s.StylusPointsReplaced += (_, _) => id.Sent = false;
        s.DrawingAttributesChanged += (_, _) => id.Sent = false;
        s.DrawingAttributesReplaced += (_, _) => id.Sent = false;
        s.Invalidated += Stale;
        return id;
    }

    /// <summary>The plugin has been told the native layer is up (so it hid its copy).</summary>
    bool toldNative = true;
    DispatcherTimer lingerTimer = null!, tellTimer = null!, hideTimer = null!;

    void InitInkDom()
    {
        publishTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        publishTimer.Tick += (_, _) => { publishTimer.Stop(); PublishInk(); };
        // After the pen leaves, the native layer stays a moment (the pen often comes straight back).
        lingerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        lingerTimer.Tick += (_, _) => { lingerTimer.Stop(); if (!WantNative()) HandOffToNote(); };
        // Each layer hands over only once the other is on screen: never a frame with no ink.
        tellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        tellTimer.Tick += (_, _) =>
        {
            tellTimer.Stop();
            if (!nativeShown || toldNative || !WantNative()) return;
            toldNative = true;
            Protocol.Send(new { type = "ink-native", path = inkPath, visible = true });
        };
        hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer.Stop();
            if (toldNative || WantNative() || replaying) return;
            nativeShown = false;
            ink.Visibility = Visibility.Hidden;
        };
        ink.SelectionChanged += (_, _) => UpdateNativeVisibility();
    }

    void SetDomInk(bool value)
    {
        if (value == domInk) return;
        domInk = value;
        domReset = true;
        if (domInk) PublishInk();
        UpdateNativeVisibility();
    }

    /// <summary>Ink changed or moved: send the note's copy of it soon.</summary>
    void SchedulePublish()
    {
        if (!domInk) return;
        publishTimer.Stop();
        publishTimer.Start();
    }

    /// <summary>The native layer is needed: pen near, a selection, a drag, the laser (or no note copy).</summary>
    bool WantNative() => !domInk || armed || dragging || laserLines.Count > 0 || ink.GetSelectedStrokes().Count > 0;

    /// <summary>
    /// Which layer shows the ink. Taking over: the native layer draws first, and only then does the note's copy
    /// hide; handing back: the note's copy (brought up to date) shows first, then the native layer hides. The
    /// two draw the same outlines in the same place, so the moment both are up is invisible, where a moment with
    /// neither was a flicker. `scrolled`: the page moved, so hand back now (the native layer can't keep up with
    /// scrolling; that's what the note's copy is for).
    /// </summary>
    void UpdateNativeVisibility(bool scrolled = false)
    {
        if (replaying) return; // replay manages the layers itself
        if (WantNative())
        {
            lingerTimer.Stop();
            hideTimer.Stop();
            if (!nativeShown)
            {
                nativeShown = true;
                ink.Visibility = Visibility.Visible;
            }
            if (!toldNative && !tellTimer.IsEnabled) tellTimer.Start();
            return;
        }
        if (!nativeShown || (!toldNative && hideTimer.IsEnabled)) return;
        if (scrolled) { HandOffToNote(); return; }
        if (!lingerTimer.IsEnabled) lingerTimer.Start();
    }

    void HandOffToNote()
    {
        lingerTimer.Stop();
        tellTimer.Stop();
        publishTimer.Stop();
        PublishInk();
        toldNative = false;
        Protocol.Send(new { type = "ink-native", path = inkPath, visible = false });
        hideTimer.Stop();
        hideTimer.Start();
    }
    /// <summary>
    /// The note's copy brought up to date: the strokes in drawing order (by id), with the outline (as Windows
    /// Ink draws it), colour and opacity of only those that are new or changed since the last time.
    /// </summary>
    void PublishInk()
    {
        if (!domInk) return;
        bool reset = domReset;
        domReset = false;
        var order = new System.Collections.Generic.List<int>(ink.Strokes.Count);
        var add = new System.Collections.Generic.List<object>();
        foreach (var s in ink.Strokes)
        {
            var id = IdOf(s);
            order.Add(id.Id);
            if (id.Sent && !reset) continue;
            id.Sent = true;
            var da = s.DrawingAttributes;
            string d;
            try { d = SvgPath(s.GetGeometry(da)); }
            catch { continue; }
            add.Add(new
            {
                id = id.Id,
                d,
                c = $"#{da.Color.R:X2}{da.Color.G:X2}{da.Color.B:X2}",
                // Highlighters draw see-through, under the pen ink.
                o = Math.Round(da.Color.A / 255.0 * (da.IsHighlighter ? 0.5 : 1), 3),
                hl = da.IsHighlighter,
            });
        }
        Protocol.Send(new { type = "ink-svg", path = inkPath, reset, order, add });
    }

    static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
    static string P(Point p) => N(p.X) + " " + N(p.Y);

    /// <summary>A WPF geometry in SVG path syntax (lines, cubic and quadratic Béziers, arcs).</summary>
    internal static string SvgPath(Geometry geometry)
    {
        var path = PathGeometry.CreateFromGeometry(geometry);
        var sb = new StringBuilder();
        foreach (var figure in path.Figures)
        {
            sb.Append('M').Append(P(figure.StartPoint));
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment l: sb.Append('L').Append(P(l.Point)); break;
                    case PolyLineSegment pl: sb.Append('L').Append(string.Join(" ", pl.Points.Select(P))); break;
                    case BezierSegment b: sb.Append('C').Append(P(b.Point1)).Append(' ').Append(P(b.Point2)).Append(' ').Append(P(b.Point3)); break;
                    case PolyBezierSegment pb: sb.Append('C').Append(string.Join(" ", pb.Points.Select(P))); break;
                    case QuadraticBezierSegment q: sb.Append('Q').Append(P(q.Point1)).Append(' ').Append(P(q.Point2)); break;
                    case PolyQuadraticBezierSegment pq: sb.Append('Q').Append(string.Join(" ", pq.Points.Select(P))); break;
                    case ArcSegment a:
                        sb.Append('A').Append(N(a.Size.Width)).Append(' ').Append(N(a.Size.Height)).Append(' ').Append(N(a.RotationAngle))
                          .Append(a.IsLargeArc ? " 1 " : " 0 ").Append(a.SweepDirection == SweepDirection.Clockwise ? "1 " : "0 ").Append(P(a.Point));
                        break;
                }
            }
            if (figure.IsClosed) sb.Append('Z');
        }
        return sb.ToString();
    }
}
