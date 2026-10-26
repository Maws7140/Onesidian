using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Matrix3x2 = System.Numerics.Matrix3x2;
using WinInk = Windows.UI.Input.Inking;
using WinAnalysis = Windows.UI.Input.Inking.Analysis;

namespace InkOverlay;

/// <summary>
/// A recognised shape as an outline in ink coordinates. `UsesPrevious`: it's made of the stroke just
/// drawn and the one before it (an arrow drawn as a shaft, then its head), so it replaces both.
/// </summary>
public sealed record Shape(string Kind, IReadOnlyList<Point> Outline, bool Smooth, bool UsesPrevious = false);

/// <summary>
/// Ink-to-shape using Windows Ink's own analyzer (Windows.UI.Input.Inking.Analysis), which recognises
/// circles, ellipses, triangles and quadrilaterals through hexagons. Straight lines and arrows are
/// handled here, since the analyzer has neither.
/// </summary>
public static class ShapeRecognizer
{
    /// <param name="previous">The stroke drawn just before, if recent: it may be the other half of an arrow.</param>
    public static async Task<Shape?> RecognizeAsync(IReadOnlyList<Point> points, IReadOnlyList<Point>? previous = null)
    {
        if (points.Count < 3) return null;
        var line = AsLine(points);
        // A straight stroke is a line, unless it's the shaft for a head drawn just before it.
        if (line != null) return (previous != null ? ArrowRecognizer.FromTwo(points, previous) : null) ?? line;
        var arrow = ArrowRecognizer.FromOne(points) ?? (previous != null ? ArrowRecognizer.FromTwo(points, previous) : null);
        if (arrow != null) return arrow;

        try
        {
            var builder = new WinInk.InkStrokeBuilder();
            var inkPoints = points.Select(p => new WinInk.InkPoint(new Windows.Foundation.Point(p.X, p.Y), 0.5f)).ToList();
            var stroke = builder.CreateStrokeFromInkPoints(inkPoints, Matrix3x2.Identity);

            var analyzer = new WinAnalysis.InkAnalyzer();
            analyzer.AddDataForStroke(stroke);
            analyzer.SetStrokeDataKind(stroke.Id, WinAnalysis.InkAnalysisStrokeKind.Drawing);
            var result = await analyzer.AnalyzeAsync();
            if (result.Status != WinAnalysis.InkAnalysisStatus.Updated) return null;

            var drawing = analyzer.AnalysisRoot
                .FindNodes(WinAnalysis.InkAnalysisNodeKind.InkDrawing)
                .OfType<WinAnalysis.InkAnalysisInkDrawing>()
                .FirstOrDefault();
            if (drawing == null || drawing.DrawingKind == WinAnalysis.InkAnalysisDrawingKind.Drawing) return null;

            var kind = drawing.DrawingKind.ToString();
            if (drawing.DrawingKind is WinAnalysis.InkAnalysisDrawingKind.Circle or WinAnalysis.InkAnalysisDrawingKind.Ellipse)
            {
                var rect = drawing.RotatedBoundingRect.Select(ToWpf).ToList();
                return new Shape(kind, Ellipse(rect, drawing.DrawingKind == WinAnalysis.InkAnalysisDrawingKind.Circle), true);
            }

            var corners = drawing.Points.Select(ToWpf).ToList();
            if (corners.Count < 3) return null;
            corners.Add(corners[0]);
            return new Shape(kind, corners, false);
        }
        catch (Exception e)
        {
            Protocol.Log("shape recognition failed: " + e.Message);
            return null;
        }
    }

    static Point ToWpf(Windows.Foundation.Point p) => new(p.X, p.Y);

    /// <summary>A stroke that's nearly as long as the distance between its ends is a line. Snaps to level/plumb.</summary>
    static Shape? AsLine(IReadOnlyList<Point> pts)
    {
        var a = pts[0];
        var b = pts[^1];
        double chord = (b - a).Length;
        if (chord < 16) return null;
        double length = 0;
        for (int i = 1; i < pts.Count; i++) length += (pts[i] - pts[i - 1]).Length;
        if (length / chord > 1.06) return null;

        double angle = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
        double snapTo = Math.Round(angle / 90) * 90;
        if (Math.Abs(angle - snapTo) < 4)
        {
            if (Math.Abs(snapTo) % 180 == 0) b = new Point(b.X, a.Y);
            else b = new Point(a.X, b.Y);
        }
        return new Shape("Line", new[] { a, b }, false);
    }

    /// <summary>Ellipse inscribed in a rotated bounding rectangle (corners in order).</summary>
    static List<Point> Ellipse(IReadOnlyList<Point> rect, bool circle)
    {
        var c = new Point(rect.Average(p => p.X), rect.Average(p => p.Y));
        Vector u = (rect[1] - rect[0]) / 2;
        Vector v = (rect[3] - rect[0]) / 2;
        if (circle)
        {
            double r = (u.Length + v.Length) / 2;
            u = u / u.Length * r;
            v = v / v.Length * r;
        }
        const int steps = 72;
        var outline = new List<Point>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = 2 * Math.PI * i / steps;
            outline.Add(c + u * Math.Cos(t) + v * Math.Sin(t));
        }
        return outline;
    }
}
