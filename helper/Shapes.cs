using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace InkOverlay;

/// <summary>
/// OneNote's Insert Shapes gallery: drag from one corner to the other (or from start to end for lines and
/// arrows). Each shape is a few strokes, so it's ordinary ink: it erases, moves and goes to OneNote like
/// anything drawn by hand.
/// </summary>
public static class ShapeGallery
{
    public static readonly string[] Kinds =
    {
        "line", "arrow", "double-arrow", "rectangle", "oval", "triangle", "right-triangle", "diamond",
        "parallelogram", "trapezoid", "pentagon", "hexagon", "cube", "cylinder", "cone", "axes", "axes-quadrant", "axes-3d",
    };

    /// <summary>Whether the shape is drawn with smooth curves (ovals, cylinders...).</summary>
    public static bool Smooth(string kind) => kind is "oval" or "cylinder" or "cone";

    /// <summary>The shape's strokes (each a polyline) for a drag from `a` to `b`.</summary>
    public static List<List<Point>> Build(string kind, Point a, Point b)
    {
        var r = new Rect(a, b);
        double L = r.Left, T = r.Top, R = r.Right, B = r.Bottom, W = r.Width, H = r.Height, cx = L + W / 2, cy = T + H / 2;
        List<Point> P(params (double x, double y)[] pts) => pts.Select(p => new Point(p.x, p.y)).ToList();
        List<Point> Closed(params (double x, double y)[] pts) { var l = P(pts); l.Add(l[0]); return l; }
        List<Point> Arc(double ex, double ey, double rx, double ry, double from, double to, int n = 48) =>
            Enumerable.Range(0, n + 1).Select(i => { double t = from + (to - from) * i / n; return new Point(ex + rx * Math.Cos(t), ey + ry * Math.Sin(t)); }).ToList();
        List<Point> Polygon(int sides) =>
            Enumerable.Range(0, sides + 1).Select(i => { double t = -Math.PI / 2 + 2 * Math.PI * i / sides; return new Point(cx + W / 2 * Math.Cos(t), cy + H / 2 * Math.Sin(t)); }).ToList();

        switch (kind)
        {
            case "line": return new() { new() { a, b } };
            case "arrow":
            case "double-arrow": return new() { ArrowRecognizer.Make(a, b, kind == "double-arrow").ToList() };
            case "rectangle": return new() { Closed((L, T), (R, T), (R, B), (L, B)) };
            case "oval": return new() { Arc(cx, cy, W / 2, H / 2, 0, 2 * Math.PI, 72) };
            case "triangle": return new() { Closed((cx, T), (R, B), (L, B)) };
            case "right-triangle": return new() { Closed((L, T), (L, B), (R, B)) };
            case "diamond": return new() { Closed((cx, T), (R, cy), (cx, B), (L, cy)) };
            case "parallelogram": { double s = W * 0.25; return new() { Closed((L + s, T), (R, T), (R - s, B), (L, B)) }; }
            case "trapezoid": { double s = W * 0.2; return new() { Closed((L + s, T), (R - s, T), (R, B), (L, B)) }; }
            case "pentagon": return new() { Polygon(5) };
            case "hexagon": return new() { Polygon(6) };
            case "cube":
            {
                double d = Math.Min(W, H) * 0.3;
                return new()
                {
                    Closed((L, T + d), (R - d, T + d), (R - d, B), (L, B)),         // front
                    P((L, T + d), (L + d, T), (R, T), (R - d, T + d)),              // top
                    P((R, T), (R, B - d), (R - d, B)),                              // side
                };
            }
            case "cylinder":
            {
                double ry = Math.Min(H * 0.15, W * 0.25);
                return new()
                {
                    Arc(cx, T + ry, W / 2, ry, 0, 2 * Math.PI, 72),                 // top ellipse
                    P((L, T + ry), (L, B - ry)),
                    P((R, T + ry), (R, B - ry)),
                    Arc(cx, B - ry, W / 2, ry, 0, Math.PI, 36),                     // front half of the base
                };
            }
            case "cone":
            {
                double ry = Math.Min(H * 0.15, W * 0.25);
                return new()
                {
                    P((L, B - ry), (cx, T), (R, B - ry)),
                    Arc(cx, B - ry, W / 2, ry, 0, 2 * Math.PI, 72),
                };
            }
            case "axes":
            {
                // Both axes through the middle, arrowheads at the positive ends, ticks every tenth.
                var strokes = new List<List<Point>>
                {
                    ArrowRecognizer.Make(new Point(L, cy), new Point(R, cy), false).ToList(),
                    ArrowRecognizer.Make(new Point(cx, B), new Point(cx, T), false).ToList(),
                };
                for (int i = 1; i < 10; i++)
                {
                    if (i == 5) continue;
                    double x = L + W * i / 10, y = T + H * i / 10;
                    strokes.Add(P((x, cy - 4), (x, cy + 4)));
                    strokes.Add(P((cx - 4, y), (cx + 4, y)));
                }
                return strokes;
            }
            case "axes-quadrant":
            {
                var strokes = new List<List<Point>>
                {
                    ArrowRecognizer.Make(new Point(L, B), new Point(R, B), false).ToList(),
                    ArrowRecognizer.Make(new Point(L, B), new Point(L, T), false).ToList(),
                };
                for (int i = 1; i < 10; i++)
                {
                    double x = L + W * i / 10, y = B - H * i / 10;
                    strokes.Add(P((x, B - 4), (x, B + 4)));
                    strokes.Add(P((L - 4, y), (L + 4, y)));
                }
                return strokes;
            }
            case "axes-3d":
            {
                var o = new Point(L + W * 0.35, T + H * 0.6);
                return new()
                {
                    ArrowRecognizer.Make(o, new Point(R, o.Y), false).ToList(),                 // x
                    ArrowRecognizer.Make(o, new Point(o.X, T), false).ToList(),                 // z (up)
                    ArrowRecognizer.Make(o, new Point(L, B), false).ToList(),                   // y (towards you)
                };
            }
        }
        return new();
    }
}
