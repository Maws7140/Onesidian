using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace InkOverlay;

/// <summary>
/// OneNote's page tools that the pen drives directly: Insert/remove space, the laser pointer, the shape
/// gallery, the ruler, and Ink Replay (every stroke now carries the time it was drawn).
/// </summary>
public sealed partial class OverlayWindow
{
    internal static readonly Guid StrokeTimeId = new("6a8f3c2e-1d4b-4e7a-9c55-0b1e2f3a4d61");

    string currentTool = "pen";
    bool dragging;
    Point dragStartInk, dragStartRoot;
    Path? toolPreview;
    readonly List<Polyline> laserLines = new();
    Polyline? laserLine;
    DispatcherTimer laserFade = null!;
    Ruler? ruler;
    internal Ruler? RulerForTest => ruler;
    enum RulerDrag { None, Move, Rotate }
    RulerDrag rulerDrag;
    double rulerGrabAngle;
    Vector rulerGrabOffset;

    void InitPageTools()
    {
        laserFade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        laserFade.Tick += (_, _) => { laserFade.Stop(); FadeLaser(); };
        ink.PreviewStylusDown += OnToolDown;
        ink.PreviewStylusMove += OnToolMove;
        ink.PreviewStylusUp += OnToolUp;
        ink.StrokeCollected += SnapToRuler;
    }

    bool IsShapeTool => currentTool.StartsWith("shape:");
    bool IsPageTool => currentTool is "space" or "laser" || IsShapeTool;

    void OnToolDown(object sender, StylusDownEventArgs e)
    {
        if (e.Handled || e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Touch) return;
        if (replaying) { StopReplay(); e.Handled = true; return; }
        var rootPoint = e.GetPosition(root);
        // The ruler is picked up wherever it is, whatever the tool: by its ends to turn it, else to move it.
        if (ruler != null && ruler.Hit(rootPoint) is { } part)
        {
            rulerDrag = part == "end" ? RulerDrag.Rotate : RulerDrag.Move;
            rulerGrabAngle = ruler.AngleTo(rootPoint) - ruler.Angle;
            rulerGrabOffset = ruler.Center - rootPoint;
            ink.CaptureStylus();
            e.Handled = true;
            return;
        }
        if (!IsPageTool) return;
        // A shape just drawn stays selected so it can be resized or moved with its handles (like OneNote);
        // the pen on those handles or inside the shape adjusts it, anywhere else starts the next shape.
        if (IsShapeTool && ink.EditingMode == InkCanvasEditingMode.Select)
        {
            if (ink.GetSelectedStrokes().Count > 0 && ink.HitTestSelection(e.GetPosition(ink)) != InkCanvasSelectionHitResult.None) return;
            ink.Select(new StrokeCollection());
            ink.EditingMode = InkCanvasEditingMode.None;
        }
        dragging = true;
        UpdateNativeVisibility();
        dragStartInk = e.GetPosition(ink);
        dragStartRoot = rootPoint;
        ink.CaptureStylus();
        e.Handled = true;
        if (currentTool == "laser")
        {
            laserFade.Stop();
            laserLine = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x2D)),
                StrokeThickness = 4,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Red, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 },
            };
            laserLine.Points.Add(rootPoint);
            root.Children.Add(laserLine);
            laserLines.Add(laserLine);
        }
    }

    void OnToolMove(object sender, StylusEventArgs e)
    {
        if (rulerDrag != RulerDrag.None && ruler != null)
        {
            var p = e.GetPosition(root);
            if (rulerDrag == RulerDrag.Move) ruler.MoveTo(p + rulerGrabOffset);
            else ruler.RotateTo(ruler.AngleTo(p) - rulerGrabAngle);
            e.Handled = true;
            return;
        }
        if (!dragging || e.InAir) return;
        e.Handled = true;
        if (currentTool == "laser") { foreach (var sp in e.GetStylusPoints(root)) laserLine?.Points.Add(new Point(sp.X, sp.Y)); return; }
        var now = e.GetPosition(ink);
        if (currentTool == "space") ShowSpacePreview(now);
        else if (IsShapeTool) ShowShapePreview(currentTool[6..], dragStartInk, now);
    }

    void OnToolUp(object sender, StylusEventArgs e)
    {
        if (rulerDrag != RulerDrag.None)
        {
            rulerDrag = RulerDrag.None;
            ink.ReleaseStylusCapture();
            e.Handled = true;
            return;
        }
        if (!dragging) return;
        dragging = false;
        UpdateNativeVisibility();
        ink.ReleaseStylusCapture();
        e.Handled = true;
        var end = e.GetPosition(ink);
        RemoveToolPreview();
        if (currentTool == "laser") { laserLine = null; laserFade.Start(); return; }
        if (currentTool == "space") { InsertSpace(dragStartInk.Y, end.Y); return; }
        if (IsShapeTool) AddShape(currentTool[6..], dragStartInk, end);
    }

    internal void RemoveToolPreview()
    {
        if (toolPreview == null) return;
        // InkCanvas keeps its children in its own collection (it isn't a Panel).
        ink.Children.Remove(toolPreview);
        toolPreview = null;
    }

    // ---------- insert / remove space ----------

    /// <summary>Line pitch of the note (the usual distance between lines).</summary>
    double Pitch()
    {
        if (tops == null || tops.Length < 2) return 24;
        var steps = tops.Zip(tops.Skip(1), (a, b) => b - a).Where(d => d > 0).OrderBy(d => d).ToList();
        return steps.Count > 0 ? steps[steps.Count / 2] : 24;
    }

    void ShowSpacePreview(Point now)
    {
        RemoveToolPreview();
        double top = Math.Min(dragStartInk.Y, now.Y), height = Math.Abs(now.Y - dragStartInk.Y);
        var area = VisibleInkArea();
        var geometry = new GeometryGroup();
        geometry.Children.Add(new RectangleGeometry(new Rect(area.Left, top, area.Width, Math.Max(1, height))));
        toolPreview = new Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(Color.FromArgb(40, 0x1F, 0x5F, 0xD6)),
            Stroke = new SolidColorBrush(Color.FromArgb(200, 0x1F, 0x5F, 0xD6)),
            StrokeThickness = 1.5 / Zoom,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
        };
        InkCanvas.SetLeft(toolPreview, 0);
        InkCanvas.SetTop(toolPreview, 0);
        ink.Children.Add(toolPreview);
    }

    /// <summary>
    /// Dragged down: blank lines go into the note there, and everything below moves down with them.
    /// Dragged up: blank lines between the two points come out. The plugin edits the note and says what it
    /// did ("space-applied"), then the ink below is moved along (<see cref="ShiftLines"/>).
    /// </summary>
    internal void InsertSpace(double fromY, double toY)
    {
        if (tops == null || tops.Length == 0 || topsMode == "page") return;
        double pitch = Pitch();
        int count = (int)Math.Round((toY - fromY) / pitch);
        if (count == 0) return;
        int LineBefore(double y)
        {
            if (y > tops[^1] + pitch) return tops.Length + (int)Math.Round((y - tops[^1]) / pitch) - 1;
            int i = LineAt(y);
            return y - tops[i] > pitch / 2 ? i + 1 : i;
        }
        int line = count > 0 ? LineBefore(fromY) : LineBefore(toY);
        Protocol.Send(new { type = "insert-space", inkPath, line, count });
    }

    // ---------- laser pointer ----------

    void FadeLaser()
    {
        foreach (var line in laserLines.ToList())
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(500));
            fade.Completed += (_, _) => { root.Children.Remove(line); laserLines.Remove(line); UpdateNativeVisibility(); };
            line.BeginAnimation(OpacityProperty, fade);
        }
    }

    // ---------- shape gallery ----------

    internal void ShowShapePreview(string kind, Point a, Point b)
    {
        RemoveToolPreview();
        var geometry = new PathGeometry();
        foreach (var poly in ShapeGallery.Build(kind, a, b))
        {
            if (poly.Count < 2) continue;
            var figure = new PathFigure { StartPoint = poly[0], IsFilled = false };
            figure.Segments.Add(new PolyLineSegment(poly.Skip(1), true));
            geometry.Figures.Add(figure);
        }
        var da = ink.DefaultDrawingAttributes;
        toolPreview = new Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(da.Color),
            StrokeThickness = Math.Max(1 / Zoom, da.Width),
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = 0.6,
            IsHitTestVisible = false,
        };
        InkCanvas.SetLeft(toolPreview, 0);
        InkCanvas.SetTop(toolPreview, 0);
        ink.Children.Add(toolPreview);
    }

    internal void AddShape(string kind, Point a, Point b)
    {
        if ((b - a).Length < 6 / Zoom) return;
        var strokes = new StrokeCollection();
        foreach (var poly in ShapeGallery.Build(kind, a, b))
        {
            if (poly.Count < 2) continue;
            var da = ink.DefaultDrawingAttributes.Clone();
            da.FitToCurve = ShapeGallery.Smooth(kind);
            da.IgnorePressure = true;
            strokes.Add(new Stroke(new StylusPointCollection(poly.Select(p => new StylusPoint(p.X, p.Y, 0.5f))), da));
        }
        if (strokes.Count == 0) return;
        ink.Strokes.Add(strokes);
        // All parts of the shape move with one line of text.
        if (tops != null) { var bounds = strokes.GetBounds(); int line = LineAt(bounds.Top + bounds.Height / 2); foreach (var s in strokes) SetAnchor(s, line, tops[line]); }
        // Selected, with handles, to adjust its size and place.
        ink.Select(strokes);
        Protocol.Log($"shape: {kind}");
    }

    // ---------- ruler ----------

    internal void ShowRuler(bool visible)
    {
        if (!visible) { if (ruler != null) root.Children.Remove(ruler.Visual); ruler = null; return; }
        if (ruler != null) return;
        ruler = new Ruler(dark);
        ruler.MoveTo(new Point(Math.Max(200, ActualWidth / 2), Math.Max(120, ActualHeight / 2)));
        root.Children.Add(ruler.Visual);
    }

    /// <summary>A stroke started along the ruler's edge becomes a straight line along it, like drawing against a real ruler.</summary>
    internal void SnapToRuler(object? sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (ruler == null || pendingShape != null || ink.EditingMode is not (InkCanvasEditingMode.Ink or InkCanvasEditingMode.InkAndGesture)) return;
        var pts = e.Stroke.StylusPoints;
        if (pts.Count < 2) return;
        var first = ink.TranslatePoint(new Point(pts[0].X, pts[0].Y), root);
        double inkScale = (ink.TranslatePoint(new Point(1, 0), root) - ink.TranslatePoint(new Point(0, 0), root)).Length;
        var edge = ruler.NearestEdge(first, 24, e.Stroke.DrawingAttributes.Width * inkScale / 2);
        if (edge == null) return;
        var (a, dir) = edge.Value;
        double t0 = (first - a) * dir, tMin = t0, tMax = t0;
        foreach (var sp in pts)
        {
            double t = (ink.TranslatePoint(new Point(sp.X, sp.Y), root) - a) * dir;
            tMin = Math.Min(tMin, t);
            tMax = Math.Max(tMax, t);
        }
        // From where the pen went down to the far end it reached.
        double tEnd = Math.Abs(tMax - t0) > Math.Abs(tMin - t0) ? tMax : tMin;
        var p0 = root.TranslatePoint(a + dir * t0, ink);
        var p1 = root.TranslatePoint(a + dir * tEnd, ink);
        if ((p1 - p0).Length < 2) return;
        var straight = e.Stroke.Clone();
        straight.StylusPoints = new StylusPointCollection(new[] { new StylusPoint(p0.X, p0.Y, 0.5f), new StylusPoint(p1.X, p1.Y, 0.5f) });
        straight.DrawingAttributes.FitToCurve = false;
        ink.Strokes.Replace(e.Stroke, new StrokeCollection { straight });
    }

    // ---------- ink replay ----------

    bool replaying;
    InkPresenter? replayLayer;
    List<(Stroke stroke, double start, double duration)> replayPlan = new();
    int replayDone;
    Stroke? replayPartial;
    DateTime replayStartedAt;

    static long TimeOf(Stroke s) => s.ContainsPropertyData(StrokeTimeId) ? Convert.ToInt64(s.GetPropertyData(StrokeTimeId)) : 0;

    /// <summary>
    /// Redraw the page's ink in the order it was written, at writing speed (pauses shortened, the whole
    /// replay kept under half a minute). Ink from before strokes had times plays first, in page order.
    /// </summary>
    internal void StartReplay()
    {
        StopReplay();
        var strokes = ink.Strokes.Select((s, i) => (s, i)).OrderBy(x => TimeOf(x.s)).ThenBy(x => x.i).Select(x => x.s).ToList();
        if (strokes.Count == 0) { Notice("No ink to replay"); return; }
        double t = 0;
        long prev = 0;
        replayPlan = new();
        foreach (var s in strokes)
        {
            double length = 0;
            for (int i = 1; i < s.StylusPoints.Count; i++) length += Math.Sqrt(Math.Pow(s.StylusPoints[i].X - s.StylusPoints[i - 1].X, 2) + Math.Pow(s.StylusPoints[i].Y - s.StylusPoints[i - 1].Y, 2));
            double duration = Math.Clamp(length * Zoom / 0.9, 60, 1500); // ~0.9 px/ms, like handwriting
            long time = TimeOf(s);
            double gap = prev == 0 || time == 0 ? 80 : Math.Clamp((time - prev) / 10_000.0 - duration, 40, 600);
            replayPlan.Add((s.Clone(), t + gap, duration));
            t += gap + duration;
            prev = time;
        }
        if (t > 30_000) replayPlan = replayPlan.Select(p => (p.stroke, p.start * 30_000 / t, p.duration * 30_000 / t)).ToList();
        replayLayer = new InkPresenter { RenderTransform = ink.RenderTransform, IsHitTestVisible = false };
        root.Children.Add(replayLayer);
        ink.Visibility = Visibility.Hidden;
        replaying = true;
        // The note's copy of the ink steps aside too, or the replay would play over the finished page.
        nativeShown = true;
        toldNative = true;
        Protocol.Send(new { type = "ink-native", path = inkPath, visible = true });
        replayDone = 0;
        replayPartial = null;
        replayStartedAt = DateTime.Now;
        CompositionTarget.Rendering += ReplayFrame;
        Protocol.Send(new { type = "replay", playing = true });
    }

    void ReplayFrame(object? sender, EventArgs e)
    {
        if (replayLayer == null) return;
        double now = (DateTime.Now - replayStartedAt).TotalMilliseconds;
        while (replayDone < replayPlan.Count && now >= replayPlan[replayDone].start + replayPlan[replayDone].duration)
        {
            if (replayPartial != null) { replayLayer.Strokes.Remove(replayPartial); replayPartial = null; }
            replayLayer.Strokes.Add(replayPlan[replayDone].stroke);
            replayDone++;
        }
        if (replayDone >= replayPlan.Count) { StopReplay(); return; }
        var (stroke, start, duration) = replayPlan[replayDone];
        if (now < start) return;
        int n = Math.Max(1, (int)Math.Ceiling(stroke.StylusPoints.Count * (now - start) / duration));
        var part = stroke.Clone();
        part.StylusPoints = new StylusPointCollection(stroke.StylusPoints.Take(Math.Min(n, stroke.StylusPoints.Count)));
        if (replayPartial != null) replayLayer.Strokes.Remove(replayPartial);
        replayLayer.Strokes.Add(part);
        replayPartial = part;
    }

    void StopReplay()
    {
        if (!replaying) return;
        replaying = false;
        CompositionTarget.Rendering -= ReplayFrame;
        if (replayLayer != null) root.Children.Remove(replayLayer);
        replayLayer = null;
        ink.Visibility = Visibility.Visible;
        nativeShown = true;
        toldNative = true;
        Protocol.Send(new { type = "replay", playing = false });
        UpdateNativeVisibility();
    }
}

/// <summary>
/// The ruler: a translucent straightedge in centimetres and millimetres, at any angle (shown on it).
/// The pen moves it (grab the middle) or turns it (grab an end); it settles on 0°, 15°, 30°… when close.
/// </summary>
internal sealed class Ruler
{
    public const double Length = 820, Thick = 66;
    const double PxPerMm = 96 / 25.4;
    public readonly Canvas Visual = new() { Width = Length, Height = Thick, IsHitTestVisible = false };
    readonly TextBlock label;
    readonly RotateTransform rotate = new();
    readonly TranslateTransform move = new();
    public Point Center { get; private set; }
    public double Angle { get; private set; }

    public Ruler(bool dark)
    {
        var face = dark ? Color.FromArgb(215, 0x3A, 0x3A, 0x3E) : Color.FromArgb(215, 0xF2, 0xF2, 0xF2);
        var marks = dark ? Color.FromRgb(0xE6, 0xE6, 0xE6) : Color.FromRgb(0x30, 0x30, 0x30);
        Visual.Children.Add(new Rectangle { Width = Length, Height = Thick, Fill = new SolidColorBrush(face), RadiusX = 4, RadiusY = 4,
            Stroke = new SolidColorBrush(Color.FromArgb(120, marks.R, marks.G, marks.B)), StrokeThickness = 1 });
        var tickBrush = new SolidColorBrush(marks);
        for (int mm = 0; mm * PxPerMm <= Length - 10; mm++)
        {
            double x = 10 + mm * PxPerMm;
            if (x > Length - 10) break;
            double h = mm % 10 == 0 ? 16 : mm % 5 == 0 ? 11 : 6;
            Visual.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = h, Stroke = tickBrush, StrokeThickness = 1 });
            Visual.Children.Add(new Line { X1 = x, X2 = x, Y1 = Thick, Y2 = Thick - h, Stroke = tickBrush, StrokeThickness = 1 });
            if (mm % 10 == 0 && mm > 0)
            {
                var n = new TextBlock { Text = (mm / 10).ToString(), FontSize = 10, Foreground = tickBrush };
                Canvas.SetLeft(n, x - 3);
                Canvas.SetTop(n, 17);
                Visual.Children.Add(n);
            }
        }
        label = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = tickBrush };
        Canvas.SetLeft(label, Length / 2 - 14);
        Canvas.SetTop(label, Thick / 2 - 2);
        Visual.Children.Add(label);
        Visual.RenderTransform = new TransformGroup
        {
            Children = { new TranslateTransform(-Length / 2, -Thick / 2), rotate, move },
        };
        RotateTo(0);
    }

    public void MoveTo(Point center)
    {
        Center = center;
        move.X = center.X;
        move.Y = center.Y;
    }

    public void RotateTo(double degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        double snap = Math.Round(degrees / 15) * 15;
        if (Math.Abs(degrees - snap) < 2.5) degrees = snap % 360;
        Angle = degrees;
        rotate.Angle = degrees;
        double shown = degrees > 180 ? 360 - degrees : degrees;
        label.Text = $"{Math.Round(shown):0}°";
    }

    public double AngleTo(Point p) => Math.Atan2(p.Y - Center.Y, p.X - Center.X) * 180 / Math.PI;

    /// <summary>A point in the ruler's own coordinates (0..Length along it, 0..Thick across).</summary>
    Point Local(Point p)
    {
        var d = p - Center;
        double r = -Angle * Math.PI / 180;
        return new Point(d.X * Math.Cos(r) - d.Y * Math.Sin(r) + Length / 2, d.X * Math.Sin(r) + d.Y * Math.Cos(r) + Thick / 2);
    }

    Point World(double x, double y)
    {
        double r = Angle * Math.PI / 180, lx = x - Length / 2, ly = y - Thick / 2;
        return new Point(Center.X + lx * Math.Cos(r) - ly * Math.Sin(r), Center.Y + lx * Math.Sin(r) + ly * Math.Cos(r));
    }

    /// <summary>"end" (turn it), "body" (move it), or null if the point isn't on the ruler.</summary>
    public string? Hit(Point p)
    {
        var l = Local(p);
        if (l.X < 0 || l.X > Length || l.Y < 0 || l.Y > Thick) return null;
        return l.X < Length * 0.14 || l.X > Length * 0.86 ? "end" : "body";
    }

    /// <summary>
    /// The edge a point is next to (within `reach`, outside the ruler): a point on the line the pen's
    /// centre should follow (half a pen width off the edge) and the edge's direction.
    /// </summary>
    public (Point origin, Vector direction)? NearestEdge(Point p, double reach, double halfPen)
    {
        var l = Local(p);
        if (l.X < -reach || l.X > Length + reach) return null;
        double r = Angle * Math.PI / 180;
        var dir = new Vector(Math.Cos(r), Math.Sin(r));
        if (l.Y < 0 && -l.Y <= reach) return (World(0, -halfPen), dir);
        if (l.Y > Thick && l.Y - Thick <= reach) return (World(0, Thick + halfPen), dir);
        return null;
    }
}
