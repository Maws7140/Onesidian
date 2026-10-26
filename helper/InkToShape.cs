using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace InkOverlay;

/// <summary>
/// OneNote's "draw and hold" ink-to-shape: draw a shape, keep the pen still on the screen, and a clean
/// shape previews; lift the pen and it replaces the stroke. Move again instead and nothing happens.
/// </summary>
public sealed partial class OverlayWindow
{
    static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(600);
    const double HoldSlop = 4; // px the pen may wobble and still count as held
    // A stroke drawn within this long after the previous one can pair with it (an arrow's shaft and head).
    static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(3);

    readonly List<Point> livePoints = new();
    DispatcherTimer holdTimer = null!;
    Point holdAnchor;
    bool tracking;
    bool shapesEnabled = true;
    Task<Shape?>? pendingShape;
    Stroke? pendingPrevious;
    Path? shapePreview;

    void InitInkToShape()
    {
        holdTimer = new DispatcherTimer { Interval = HoldTime };
        holdTimer.Tick += (_, _) => OnHold();

        ink.PreviewStylusDown += (_, e) =>
        {
            if (e.Handled || !ShapesActive()) return;
            ClearHold();
            tracking = true;
            AddPoints(e.GetStylusPoints(ink));
        };
        ink.PreviewStylusMove += (_, e) =>
        {
            if (tracking && !e.InAir) AddPoints(e.GetStylusPoints(ink));
        };
        ink.StrokeCollected += OnStrokeCollected;
    }

    bool ShapesActive() =>
        shapesEnabled && ink.EditingMode is InkCanvasEditingMode.Ink or InkCanvasEditingMode.InkAndGesture;

    void AddPoints(StylusPointCollection points)
    {
        foreach (var sp in points)
        {
            var p = new Point(sp.X, sp.Y);
            livePoints.Add(p);
            if (livePoints.Count == 1 || (p - holdAnchor).Length > HoldSlop)
            {
                // Moving: (re)start the hold clock, and drop any shape from an earlier pause.
                holdAnchor = p;
                holdTimer.Stop();
                holdTimer.Start();
                if (pendingShape != null) CancelShape();
            }
        }
    }

    async void OnHold()
    {
        holdTimer.Stop();
        if (!tracking || livePoints.Count < 6) return;
        // The stroke before this one, if it was just drawn: it and this one may make an arrow together.
        pendingPrevious = lastStroke != null && ink.Strokes.Contains(lastStroke) && (DateTime.Now - lastStrokeAt) < PairWindow
            ? lastStroke : null;
        var previous = pendingPrevious?.StylusPoints.Select(p => new Point(p.X, p.Y)).ToList();
        var task = ShapeRecognizer.RecognizeAsync(livePoints.ToList(), previous);
        pendingShape = task;
        var shape = await task;
        if (pendingShape == task && shape != null && tracking) ShowPreview(shape);
    }

    async void OnStrokeCollected(object? sender, InkCanvasStrokeCollectedEventArgs e)
    {
        tracking = false;
        holdTimer.Stop();
        var task = pendingShape;
        if (task == null)
        {
            ClearHold();
            return;
        }
        var shape = await task;
        if (task != pendingShape) return;
        ClearHold();
        var previous = pendingPrevious;
        pendingPrevious = null;
        if (shape == null || !ink.Strokes.Contains(e.Stroke)) return;
        if (shape.UsesPrevious && (previous == null || !ink.Strokes.Contains(previous))) return;

        var da = e.Stroke.DrawingAttributes.Clone();
        da.FitToCurve = shape.Smooth;
        da.IgnorePressure = true;
        var points = new StylusPointCollection(shape.Outline.Select(p => new StylusPoint(p.X, p.Y, 0.5f)));
        var clean = new Stroke(points, da);
        // Keep the hand-drawn stroke's line anchor so the shape stays with the same text (for a two-stroke
        // arrow, the first stroke's: the new one was anchored with it anyway, being drawn right after).
        if (TryGetAnchor(shape.UsesPrevious ? previous! : e.Stroke, out var line, out var top)) SetAnchor(clean, line, top);
        // Replaced together, so a single undo brings back the hand-drawn strokes (history snapshots are debounced).
        if (shape.UsesPrevious) ink.Strokes.Remove(previous!);
        ink.Strokes.Replace(e.Stroke, new StrokeCollection { clean });
        Protocol.Log($"ink to shape: {shape.Kind}{(shape.UsesPrevious ? " (two strokes)" : "")}");
    }

    void ShowPreview(Shape shape)
    {
        RemovePreview();
        var da = ink.DefaultDrawingAttributes;
        var figure = new PathFigure { StartPoint = shape.Outline[0], IsFilled = false };
        figure.Segments.Add(new PolyLineSegment(shape.Outline.Skip(1), true));
        shapePreview = new Path
        {
            Data = new PathGeometry(new[] { figure }),
            Stroke = new SolidColorBrush(da.Color),
            StrokeThickness = Math.Max(1, da.Width),
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = da.IsHighlighter ? 0.35 : 0.6,
            IsHitTestVisible = false,
        };
        InkCanvas.SetLeft(shapePreview, 0);
        InkCanvas.SetTop(shapePreview, 0);
        ink.Children.Add(shapePreview);
    }

    void RemovePreview()
    {
        if (shapePreview == null) return;
        ink.Children.Remove(shapePreview);
        shapePreview = null;
    }

    void CancelShape()
    {
        pendingShape = null;
        RemovePreview();
    }

    void ClearHold()
    {
        holdTimer.Stop();
        livePoints.Clear();
        CancelShape();
    }
}
