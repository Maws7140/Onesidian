using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace InkOverlay;

/// <summary>
/// Arrows for draw-and-hold ink-to-shape (Windows Ink's analyzer has no arrow kind). Two ways to draw one,
/// like OneNote: in one stroke (shaft, then the head without lifting: out to one barb, back to the tip,
/// out to the other, or round a closed head), or as a shaft followed by a separate "V" at one end. A head
/// at both ends makes a double-headed arrow. The result is a straight shaft with a clean symmetric head.
/// </summary>
public static class ArrowRecognizer
{
    const double HeadAngle = 28;      // degrees between the shaft and each barb of the clean head
    const double MinShaft = 30;       // px

    /// <summary>A one-stroke arrow (one or two heads).</summary>
    public static Shape? FromOne(IReadOnlyList<Point> pts)
    {
        var v = Corners(pts);
        if (v.Count < 3) return null;

        // The shaft is the longest straight run, and most of the ink.
        int k = 0;
        for (int i = 1; i < v.Count - 1; i++) if ((v[i + 1] - v[i]).Length > (v[k + 1] - v[k]).Length) k = i;
        double shaft = (v[k + 1] - v[k]).Length;
        double total = 0;
        for (int i = 1; i < v.Count; i++) total += (v[i] - v[i - 1]).Length;
        if (shaft < MinShaft || shaft < 0.4 * total) return null;

        var atEnd = Head(v[k + 1], v[k], v.Skip(k + 2).ToList(), shaft);
        var atStart = Head(v[k], v[k + 1], v.Take(k).Reverse().ToList(), shaft);
        if (atEnd == null || atStart == null) return null;            // something that isn't an arrowhead
        if (atEnd == 0 && atStart == 0) return null;                  // no head at all
        return Build(v[k], v[k + 1], atStart.Value, atEnd.Value, false);
    }

    /// <summary>
    /// The stroke just drawn plus the one before it: a straight shaft and a "V" at one of its ends,
    /// in either order.
    /// </summary>
    public static Shape? FromTwo(IReadOnlyList<Point> current, IReadOnlyList<Point> previous)
    {
        return Try(previous, current) ?? Try(current, previous);

        static Shape? Try(IReadOnlyList<Point> shaftStroke, IReadOnlyList<Point> headStroke)
        {
            if (shaftStroke.Count < 2 || headStroke.Count < 3) return null;
            var a = shaftStroke[0];
            var b = shaftStroke[^1];
            double shaft = (b - a).Length;
            if (shaft < MinShaft || PathLength(shaftStroke) / shaft > 1.1) return null;

            var head = Corners(headStroke);
            // The V's point sits on one end of the shaft: that end is the tip.
            double reach = Math.Max(12, 0.2 * shaft);
            double toA = head.Min(p => (p - a).Length), toB = head.Min(p => (p - b).Length);
            if (Math.Min(toA, toB) > reach) return null;
            var (tip, tail) = toB <= toA ? (b, a) : (a, b);
            var len = Head(tip, tail, head, shaft, Math.Max(6, Math.Min(toA, toB) * 1.5));
            if (len is not > 0) return null;
            return Build(tail, tip, 0, len.Value, true);
        }
    }

    /// <summary>
    /// The points of an arrowhead at `tip` (the shaft running back to `tail`): barbs either side, pointing
    /// back along the shaft. Returns the mean barb length; 0 if there's no head here (nothing, or just a
    /// retrace to the tip); null if what's here isn't an arrowhead.
    /// </summary>
    static double? Head(Point tip, Point tail, IReadOnlyList<Point> corners, double shaft, double? tipReach = null)
    {
        var dir = tip - tail;
        dir.Normalize();
        double near = tipReach ?? Math.Max(6, 0.08 * shaft);
        var barbs = new List<double>();
        int left = 0, right = 0;
        foreach (var p in corners)
        {
            var w = p - tip;
            if (w.Length < near) continue; // back at the tip
            double along = -(w * dir);     // > 0: behind the tip, towards the tail
            double across = Vector.CrossProduct(dir, w);
            double angle = Math.Atan2(Math.Abs(across), along) * 180 / Math.PI;
            if (angle < 10 || angle > 75 || w.Length > 0.6 * shaft) return null;
            barbs.Add(w.Length);
            if (across > 0) left++; else right++;
        }
        if (barbs.Count == 0) return 0;
        if (barbs.Count > 3 || left == 0 || right == 0) return null;
        return barbs.Average();
    }

    /// <summary>An arrow from a to b exactly as given (the shape gallery's), one stroke.</summary>
    public static IReadOnlyList<Point> Make(Point a, Point b, bool bothEnds)
    {
        double size = Math.Clamp((b - a).Length * 0.12, 6, 18);
        var outline = new List<Point>();
        if (bothEnds) { var (l, r) = Barbs(a, b, size); outline.AddRange(new[] { l, a, r }); }
        outline.Add(a);
        outline.Add(b);
        { var (l, r) = Barbs(b, a, size); outline.AddRange(new[] { l, b, r }); }
        return outline;
    }

    /// <summary>A straight shaft from `a` to `b` (levelled/plumbed within 4°, like lines) with clean heads.</summary>
    static Shape Build(Point a, Point b, double headA, double headB, bool usesPrevious)
    {
        double angle = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
        double snapTo = Math.Round(angle / 90) * 90;
        if (Math.Abs(angle - snapTo) < 4)
        {
            if (Math.Abs(snapTo) % 180 == 0) b = new Point(b.X, a.Y);
            else b = new Point(a.X, b.Y);
        }
        double shaft = (b - a).Length;
        double Size(double drawn) => Math.Clamp(drawn, Math.Min(10, shaft * 0.3), shaft * 0.45);

        // One continuous stroke: [head at a] a → b [head at b], each head drawn out and back from its tip.
        var outline = new List<Point>();
        if (headA > 0)
        {
            var (l, r) = Barbs(a, b, Size(headA));
            outline.AddRange(new[] { l, a, r });
        }
        outline.Add(a);
        outline.Add(b);
        if (headB > 0)
        {
            var (l, r) = Barbs(b, a, Size(headB));
            outline.AddRange(new[] { l, b, r });
        }
        string kind = headA > 0 && headB > 0 ? "DoubleArrow" : "Arrow";
        return new Shape(kind, outline, false, usesPrevious);
    }

    static (Point, Point) Barbs(Point tip, Point tail, double length)
    {
        var back = tail - tip;
        back.Normalize();
        double t = HeadAngle * Math.PI / 180;
        Vector Rotate(Vector u, double r) => new(u.X * Math.Cos(r) - u.Y * Math.Sin(r), u.X * Math.Sin(r) + u.Y * Math.Cos(r));
        return (tip + Rotate(back, t) * length, tip + Rotate(back, -t) * length);
    }

    /// <summary>The stroke's corners: simplified to straight runs, with near-straight bends merged.</summary>
    internal static List<Point> Corners(IReadOnlyList<Point> pts)
    {
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        double diag = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        var v = Simplify(pts, Math.Clamp(diag * 0.03, 3, 8));
        for (int i = 1; i < v.Count - 1;)
        {
            double turn = Vector.AngleBetween(v[i] - v[i - 1], v[i + 1] - v[i]);
            if (Math.Abs(turn) < 15) v.RemoveAt(i); else i++;
        }
        return v;
    }

    /// <summary>Ramer–Douglas–Peucker.</summary>
    static List<Point> Simplify(IReadOnlyList<Point> pts, double epsilon)
    {
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            double worst = 0;
            int index = -1;
            for (int i = first + 1; i < last; i++)
            {
                double d = Distance(pts[i], pts[first], pts[last]);
                if (d > worst) { worst = d; index = i; }
            }
            if (index < 0 || worst <= epsilon) continue;
            keep[index] = true;
            stack.Push((first, index));
            stack.Push((index, last));
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }

    static double Distance(Point p, Point a, Point b)
    {
        var ab = b - a;
        if (ab.LengthSquared < 1e-9) return (p - a).Length;
        double t = Math.Clamp((p - a) * ab / ab.LengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    static double PathLength(IReadOnlyList<Point> pts)
    {
        double length = 0;
        for (int i = 1; i < pts.Count; i++) length += (pts[i] - pts[i - 1]).Length;
        return length;
    }
}
