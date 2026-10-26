using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InkOverlay;

/// <summary>
/// Pencil and effect pens (rainbow, galaxy, lava, ocean, gold, silver, rose gold, bronze), like OneNote's.
/// The pen is a property on the stroke's drawing attributes, so it's saved in the ISF and survives undo,
/// copy/paste and reloads; strokes carrying it are drawn by <see cref="StyledStroke"/>. OneNote doesn't
/// know the property (and how it stores its own pencil isn't documented), so there they show as plain ink
/// in the stroke's colour, which for effect pens is a representative one.
/// </summary>
public static class Pens
{
    public static readonly Guid PenStyleId = new("6a8f3c2e-1d4b-4e7a-9c55-0b1e2f3a4d60");

    public static readonly string[] Effects = { "rainbow", "galaxy", "lava", "ocean", "gold", "silver", "rose-gold", "bronze" };

    /// <summary>The plain colour an effect pen saves as (what OneNote and other apps show).</summary>
    public static Color BaseColor(string effect) => effect switch
    {
        "rainbow" => Color.FromRgb(0x8E, 0x2F, 0xC8),
        "galaxy" => Color.FromRgb(0x3B, 0x2C, 0x89),
        "lava" => Color.FromRgb(0xE2, 0x4A, 0x14),
        "ocean" => Color.FromRgb(0x0E, 0x6B, 0xA8),
        "gold" => Color.FromRgb(0xC9, 0xA2, 0x27),
        "silver" => Color.FromRgb(0xA8, 0xA8, 0xA8),
        "rose-gold" => Color.FromRgb(0xC9, 0x8A, 0x8E),
        "bronze" => Color.FromRgb(0xA0, 0x6A, 0x32),
        _ => Colors.Black,
    };

    public static string? StyleOf(DrawingAttributes da) =>
        da.ContainsPropertyData(PenStyleId) ? da.GetPropertyData(PenStyleId) as string : null;

    /// <summary>Strokes with a pen style become <see cref="StyledStroke"/>s (in place, same order).</summary>
    public static void Stylize(StrokeCollection strokes)
    {
        for (int i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            if (s is StyledStroke || StyleOf(s.DrawingAttributes) == null) continue;
            strokes[i] = StyledStroke.From(s);
        }
    }

    static readonly Dictionary<string, Brush> brushCache = new();

    /// <summary>How a styled stroke is painted.</summary>
    public static Brush Fill(string style, Color color, Rect bounds)
    {
        if (style == "pencil") return Graphite(color);
        var stops = style switch
        {
            "rainbow" => new[] { "#E81123", "#F7630C", "#FFB900", "#16C60C", "#0078D7", "#8E2FC8" },
            "galaxy" => new[] { "#1B1464", "#4B2C9A", "#8E44AD", "#3B2C89", "#1B1464" },
            "lava" => new[] { "#8B0000", "#E24A14", "#FF9F1C", "#E24A14", "#8B0000" },
            "ocean" => new[] { "#0B2E59", "#0E6BA8", "#2EC4B6", "#0E6BA8", "#0B2E59" },
            "gold" => new[] { "#8A6D1F", "#F6E27A", "#B8860B", "#F6E27A", "#8A6D1F" },
            "silver" => new[] { "#7D7D7D", "#F2F2F2", "#A0A0A0", "#EDEDED", "#7D7D7D" },
            "rose-gold" => new[] { "#9E5A63", "#F4C2C2", "#C08081", "#F4C2C2", "#9E5A63" },
            "bronze" => new[] { "#6E4A1E", "#D9A066", "#8C5A2B", "#D9A066", "#6E4A1E" },
            _ => new[] { "#000000", "#000000" },
        };
        var g = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Reflect };
        for (int i = 0; i < stops.Length; i++) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(stops[i]), i / (double)(stops.Length - 1)));
        if (style == "rainbow")
        {
            // Across the whole stroke, left to right.
            g.StartPoint = bounds.TopLeft;
            g.EndPoint = new Point(bounds.Right + 1, bounds.Top + bounds.Height * 0.3);
            g.SpreadMethod = GradientSpreadMethod.Pad;
        }
        else
        {
            // Metallic sheen: bands every few dozen px along the diagonal.
            double span = style is "galaxy" or "lava" or "ocean" ? 90 : 36;
            g.StartPoint = bounds.TopLeft;
            g.EndPoint = bounds.TopLeft + new Vector(span, span * 0.6);
        }
        g.Freeze();
        return g;
    }

    /// <summary>Galaxy's stars: small white specks over the gradient.</summary>
    public static Brush Stars()
    {
        if (brushCache.TryGetValue("stars", out var cached)) return cached;
        var rng = new Random(11);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 40, 40))));
        for (int i = 0; i < 9; i++)
        {
            double r = rng.NextDouble() < 0.25 ? 0.9 : 0.5;
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb((byte)rng.Next(150, 255), 255, 255, 255)), null,
                new EllipseGeometry(new Point(rng.NextDouble() * 40, rng.NextDouble() * 40), r, r)));
        }
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 40, 40), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        brushCache["stars"] = brush;
        return brush;
    }

    /// <summary>Pencil: the colour broken up by paper grain.</summary>
    static Brush Graphite(Color color)
    {
        var key = "pencil" + color;
        if (brushCache.TryGetValue(key, out var cached)) return cached;
        const int size = 24;
        var rng = new Random(5);
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            // Mostly covered, with gaps where the "paper" shows through.
            double grain = rng.NextDouble();
            byte a = (byte)(grain < 0.18 ? 40 : grain < 0.45 ? 150 : 225);
            pixels[i * 4 + 0] = (byte)(color.B * a / 255);
            pixels[i * 4 + 1] = (byte)(color.G * a / 255);
            pixels[i * 4 + 2] = (byte)(color.R * a / 255);
            pixels[i * 4 + 3] = a;
        }
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        var brush = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, size / 2.0, size / 2.0), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        brushCache[key] = brush;
        return brush;
    }
}

/// <summary>A stroke drawn with a pencil or effect pen (see <see cref="Pens"/>).</summary>
public sealed class StyledStroke : Stroke
{
    public StyledStroke(System.Windows.Input.StylusPointCollection points, DrawingAttributes da) : base(points, da) { }

    /// <summary>Device pixels per ink unit the ink is shown at now (zoom and screen scale; set by the overlay).</summary>
    public static double PixelsPerUnit = 2;

    // The pencil as Windows drew it, and for which shape (relative to the first point), size, colour and scale:
    // moving the stroke reuses it, anything else draws it again.
    WindowsInk.Rendered? pencil;
    int pencilKey;
    Point pencilOrigin;
    string? pencilPng;

    /// <summary>Draw it again (the scale it's shown at changed).</summary>
    public void Redraw() => OnInvalidated(EventArgs.Empty);

    /// <summary>
    /// This pencil stroke drawn by Windows (see WindowsInk), and how far the stroke has moved since: the
    /// image goes at Bounds + offset. Null when Windows can't draw it.
    /// </summary>
    public WindowsInk.Rendered? PencilImage(DrawingAttributes da, out Vector offset)
    {
        offset = default;
        var pts = StylusPoints;
        if (pts.Count == 0) return null;
        var origin = new Point(pts[0].X, pts[0].Y);
        var hash = new HashCode();
        foreach (var p in pts)
        {
            hash.Add(Math.Round(p.X - origin.X, 2));
            hash.Add(Math.Round(p.Y - origin.Y, 2));
            hash.Add(p.PressureFactor);
        }
        hash.Add(da.Width); hash.Add(da.Height); hash.Add(da.Color); hash.Add(da.IgnorePressure);
        hash.Add(Math.Round(PixelsPerUnit, 2));
        int key = hash.ToHashCode();
        if (pencil == null || key != pencilKey)
        {
            pencil = WindowsInk.Pencil(pts, da.Width, da.Height, da.Color, da.IgnorePressure, PixelsPerUnit);
            pencilKey = key;
            pencilOrigin = origin;
            pencilPng = null;
        }
        if (pencil == null) return null;
        offset = origin - pencilOrigin;
        return pencil;
    }

    /// <summary>The pencil image as PNG (base64), for the note's in-page copy; made once per drawing.</summary>
    public string? PencilPng(DrawingAttributes da, out Rect bounds)
    {
        bounds = Rect.Empty;
        var image = PencilImage(da, out var offset);
        if (image == null) return null;
        pencilPng ??= WindowsInk.Png(image.Image);
        bounds = image.Bounds;
        bounds.Offset(offset);
        return pencilPng;
    }

    public static StyledStroke From(Stroke s)
    {
        var styled = new StyledStroke(s.StylusPoints.Clone(), s.DrawingAttributes.Clone());
        foreach (var id in s.GetPropertyDataIds()) styled.AddPropertyData(id, s.GetPropertyData(id));
        return styled;
    }

    public override Stroke Clone()
    {
        var copy = From(this);
        return copy;
    }

    protected override void DrawCore(DrawingContext dc, DrawingAttributes da)
    {
        var style = Pens.StyleOf(da);
        if (style == null) { base.DrawCore(dc, da); return; }
        // OneNote's pencil is Windows' pencil: drawn by Windows itself (the paper-grain texture below is the
        // fallback when Windows can't).
        if (style == "pencil" && PencilImage(da, out var offset) is { } drawn)
        {
            var at = drawn.Bounds;
            at.Offset(offset);
            dc.DrawImage(drawn.Image, at);
            return;
        }
        var geometry = GetGeometry(da);
        var bounds = geometry.Bounds;
        dc.DrawGeometry(Pens.Fill(style, da.Color, bounds), null, geometry);
        if (style == "galaxy") dc.DrawGeometry(Pens.Stars(), null, geometry);
    }
}
