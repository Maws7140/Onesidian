using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Matrix3x2 = System.Numerics.Matrix3x2;
using WinInk = Windows.UI.Input.Inking;
using WinAnalysis = Windows.UI.Input.Inking.Analysis;

namespace InkOverlay;

/// <summary>A line of handwriting as Windows Ink read it.</summary>
public sealed record WrittenLine(string Text, Rect Bounds, double Angle, bool ListItem, IReadOnlyList<Stroke> Strokes);

/// <summary>Handwriting recognition with Windows Ink's own analyzer (offline, the system's handwriting languages).</summary>
public static class Handwriting
{
    /// <param name="allWriting">Treat every stroke as writing (the user picked them to convert); otherwise
    /// the analyzer tells writing from drawings and only writing comes back.</param>
    public static async Task<List<WrittenLine>> ReadAsync(IReadOnlyList<Stroke> strokes, bool allWriting)
    {
        var lines = new List<WrittenLine>();
        if (strokes.Count == 0) return lines;
        var builder = new WinInk.InkStrokeBuilder();
        var analyzer = new WinAnalysis.InkAnalyzer();
        var byId = new Dictionary<uint, Stroke>();
        foreach (var s in strokes)
        {
            var points = s.StylusPoints.Select(p => new WinInk.InkPoint(new Windows.Foundation.Point(p.X, p.Y), p.PressureFactor)).ToList();
            if (points.Count == 1) points.Add(new WinInk.InkPoint(new Windows.Foundation.Point(points[0].Position.X + 0.5, points[0].Position.Y), points[0].Pressure));
            var stroke = builder.CreateStrokeFromInkPoints(points, Matrix3x2.Identity);
            analyzer.AddDataForStroke(stroke);
            if (allWriting) analyzer.SetStrokeDataKind(stroke.Id, WinAnalysis.InkAnalysisStrokeKind.Writing);
            byId[stroke.Id] = s;
        }
        var result = await analyzer.AnalyzeAsync();
        if (result.Status != WinAnalysis.InkAnalysisStatus.Updated) return lines;

        void Walk(WinAnalysis.IInkAnalysisNode node, bool inList)
        {
            if (node is WinAnalysis.InkAnalysisLine line)
            {
                var text = line.RecognizedText?.Trim() ?? "";
                if (text.Length == 0) return;
                var r = line.BoundingRect;
                lines.Add(new WrittenLine(text, new Rect(r.X, r.Y, r.Width, r.Height), Slope(line), inList,
                    line.GetStrokeIds().Where(byId.ContainsKey).Select(id => byId[id]).ToList()));
                return;
            }
            if (node.Children == null) return;
            foreach (var child in node.Children) Walk(child, inList || node.Kind == WinAnalysis.InkAnalysisNodeKind.ListItem);
        }
        Walk(analyzer.AnalysisRoot, false);
        return ReadingOrder(lines);
    }

    /// <summary>Top to bottom; lines side by side (in columns, or a word off to the right) left to right.</summary>
    static List<WrittenLine> ReadingOrder(List<WrittenLine> lines)
    {
        static double Middle(WrittenLine l) => l.Bounds.Top + l.Bounds.Height / 2;
        var ordered = new List<WrittenLine>();
        var row = new List<WrittenLine>();
        foreach (var line in lines.OrderBy(Middle))
        {
            if (row.Count > 0 && Middle(line) - Middle(row[0]) > 0.5 * Math.Min(line.Bounds.Height, row[0].Bounds.Height))
            {
                ordered.AddRange(row.OrderBy(l => l.Bounds.Left));
                row.Clear();
            }
            row.Add(line);
        }
        ordered.AddRange(row.OrderBy(l => l.Bounds.Left));
        return ordered;
    }

    /// <summary>
    /// How far a line of writing slopes, in degrees: the trend through its words' centres; for a single
    /// word, its rotated bounding box's long side, if it's clearly longer than tall (a short word or a
    /// symbol has no direction to speak of: 0).
    /// </summary>
    static double Slope(WinAnalysis.InkAnalysisLine line)
    {
        var words = (line.Children ?? (IReadOnlyList<WinAnalysis.IInkAnalysisNode>)Array.Empty<WinAnalysis.IInkAnalysisNode>())
            .Where(n => n.Kind == WinAnalysis.InkAnalysisNodeKind.InkWord)
            .Select(n => new Point(n.BoundingRect.X + n.BoundingRect.Width / 2, n.BoundingRect.Y + n.BoundingRect.Height / 2))
            .ToList();
        if (words.Count >= 2)
        {
            double mx = words.Average(p => p.X), my = words.Average(p => p.Y);
            double sxx = words.Sum(p => (p.X - mx) * (p.X - mx)), sxy = words.Sum(p => (p.X - mx) * (p.Y - my));
            if (sxx > 1) return Math.Atan(sxy / sxx) * 180 / Math.PI;
        }
        var box = line.RotatedBoundingRect;
        if (box.Count < 4) return 0;
        double side1 = Math.Sqrt(Math.Pow(box[1].X - box[0].X, 2) + Math.Pow(box[1].Y - box[0].Y, 2));
        double side2 = Math.Sqrt(Math.Pow(box[2].X - box[1].X, 2) + Math.Pow(box[2].Y - box[1].Y, 2));
        if (Math.Max(side1, side2) < 2.5 * Math.Max(1, Math.Min(side1, side2))) return 0;
        return BoxSlope(box);
    }

    static double BoxSlope(IReadOnlyList<Windows.Foundation.Point> box)
    {
        double ax = box[1].X - box[0].X, ay = box[1].Y - box[0].Y;
        double bx = box[2].X - box[1].X, by = box[2].Y - box[1].Y;
        if (bx * bx + by * by > ax * ax + ay * ay) (ax, ay) = (bx, by);
        double angle = Math.Atan2(ay, ax) * 180 / Math.PI;
        while (angle > 90) angle -= 180;
        while (angle <= -90) angle += 180;
        return angle;
    }
}

/// <summary>
/// What you can do with handwriting and a lasso selection: Ink to Text (as a selection action and as the
/// Ink to Text pen, which converts as you pause), straighten, recolour, resize, rotate, copy/paste, and the
/// handwriting index behind "Search handwriting".
/// </summary>
public sealed partial class OverlayWindow
{
    static readonly TimeSpan TextPenPause = TimeSpan.FromMilliseconds(1000);
    static readonly TimeSpan IndexDelay = TimeSpan.FromSeconds(3);

    readonly List<Stroke> textPenStrokes = new();
    readonly Dictionary<int, StrokeCollection> conversions = new();
    DispatcherTimer textPenTimer = null!;
    DispatcherTimer indexTimer = null!;
    int nextConversion = 1;
    bool textPen;
    bool indexing;
    readonly Queue<string> indexQueue = new();

    void InitWriting()
    {
        ink.SelectionChanged += (_, _) => SendSelection();
        textPenTimer = new DispatcherTimer { Interval = TextPenPause };
        textPenTimer.Tick += (_, _) => { textPenTimer.Stop(); ConvertTextPen(); };
        ink.PreviewStylusDown += (_, _) => { if (textPen) textPenTimer.Stop(); };
        ink.StrokeCollected += (_, e) =>
        {
            if (!textPen) return;
            textPenStrokes.Add(e.Stroke);
            textPenTimer.Stop();
            textPenTimer.Start();
        };
        indexTimer = new DispatcherTimer { Interval = IndexDelay };
        indexTimer.Tick += (_, _) => { indexTimer.Stop(); IndexLoaded(); };
    }

    void SendSelection() => Protocol.Send(new { type = "selection", count = ink.GetSelectedStrokes().Count });

    static void Notice(string message) => Protocol.Send(new { type = "notice", message });

    void HandleWriting(Command cmd)
    {
        switch (cmd.Type)
        {
            case "ink-to-text": InkToText(cmd.Purpose ?? "insert"); break;
            case "consume": Consume(cmd.Id); break;
            case "straighten": Straighten(); break;
            case "restyle": Restyle(); break;
            case "pen-scale": ScaleSelection(cmd.Factor); break;
            case "rotate": RotateSelection(cmd.Angle); break;
            case "copy": CopySelection(); break;
            case "paste": Paste(); break;
            case "index": if (cmd.Files != null) QueueIndex(cmd.Files); break;
            case "ink-to-math": InkToMath(); break;
            case "math-fix": FixMath(cmd.Id); break;
        }
    }

    // ---------- Ink to Text ----------

    async void InkToText(string purpose)
    {
        var strokes = ink.GetSelectedStrokes().ToList();
        if (strokes.Count == 0) return;
        var lines = await Handwriting.ReadAsync(strokes, true);
        if (lines.Count == 0) { Notice("No handwriting recognized in the selection"); return; }
        SendText(strokes, lines, purpose);
    }

    /// <summary>The Ink to Text pen: what was written since the last pause becomes text.</summary>
    async void ConvertTextPen()
    {
        var strokes = textPenStrokes.Where(ink.Strokes.Contains).ToList();
        textPenStrokes.Clear();
        if (strokes.Count == 0) return;
        var lines = await Handwriting.ReadAsync(strokes, true);
        if (lines.Count > 0) SendText(strokes, lines, "insert");
    }

    /// <summary>
    /// Hand recognized text to the plugin, which puts it in the note at the lines it was written on. The ink
    /// goes once the plugin confirms the text is in ("consume"), so nothing is lost if it can't.
    /// </summary>
    void SendText(List<Stroke> strokes, List<WrittenLine> lines, string purpose)
    {
        int id = nextConversion++;
        conversions[id] = new StrokeCollection(strokes);
        foreach (var old in conversions.Keys.Where(k => k < id - 20).ToList()) conversions.Remove(old);
        Protocol.Send(new
        {
            type = "ink-text",
            id,
            inkPath,
            purpose,
            lines = lines.Select(l => new { text = l.Text, line = SourceLine(l.Bounds), list = l.ListItem }),
        });
    }

    void Consume(int id)
    {
        if (!conversions.Remove(id, out var strokes)) return;
        var present = new StrokeCollection(strokes.Where(ink.Strokes.Contains));
        if (present.Count > 0) ink.Strokes.Remove(present);
    }

    /// <summary>The note line a line of writing sits on; past the end of the note, counted on at the usual line pitch.</summary>
    int SourceLine(Rect b)
    {
        if (tops == null || tops.Length == 0) return -1;
        double y = b.Top + b.Height / 2;
        double pitch = 24;
        if (tops.Length > 1)
        {
            var steps = tops.Zip(tops.Skip(1), (a, c) => c - a).Where(d => d > 0).OrderBy(d => d).ToList();
            if (steps.Count > 0) pitch = steps[steps.Count / 2];
        }
        if (y > tops[^1] + pitch) return tops.Length - 1 + (int)Math.Round((y - tops[^1]) / pitch);
        return LineAt(y);
    }

    // ---------- selection actions ----------

    async void Straighten()
    {
        var selected = ink.GetSelectedStrokes();
        if (selected.Count == 0) return;
        var lines = await Handwriting.ReadAsync(selected.ToList(), true);
        int straightened = 0;
        foreach (var line in lines)
        {
            if (Math.Abs(line.Angle) < 1 || Math.Abs(line.Angle) > 60) continue;
            var m = Matrix.Identity;
            m.RotateAt(-line.Angle, line.Bounds.Left + line.Bounds.Width / 2, line.Bounds.Top + line.Bounds.Height / 2);
            foreach (var s in line.Strokes) s.Transform(m, false);
            straightened++;
        }
        if (straightened == 0) { Notice(lines.Count == 0 ? "No handwriting recognized in the selection" : "Already straight"); return; }
        ink.Select(selected); // redraw the selection box around where the ink is now
        Changed();
    }

    /// <summary>Selected ink takes the current pen's colour, like OneNote's colour picker on a selection.</summary>
    void Restyle()
    {
        var pen = ink.DefaultDrawingAttributes;
        bool flipped = pen.ContainsPropertyData(FlippedForDark);
        EachSelected(da =>
        {
            da.Color = pen.Color;
            if (flipped) da.AddPropertyData(FlippedForDark, 1);
            else if (da.ContainsPropertyData(FlippedForDark)) da.RemovePropertyData(FlippedForDark);
        });
    }

    void ScaleSelection(double factor)
    {
        if (factor <= 0) return;
        EachSelected(da =>
        {
            da.Width = Math.Clamp(da.Width * factor, 0.2, 400);
            da.Height = Math.Clamp(da.Height * factor, 0.2, 400);
        });
    }

    void EachSelected(Action<DrawingAttributes> change)
    {
        var selected = ink.GetSelectedStrokes();
        if (selected.Count == 0) return;
        foreach (var s in selected)
        {
            var da = s.DrawingAttributes.Clone();
            change(da);
            s.DrawingAttributes = da;
        }
        Changed();
    }

    void RotateSelection(double angle)
    {
        var selected = ink.GetSelectedStrokes();
        if (selected.Count == 0) return;
        var b = selected.GetBounds();
        var m = Matrix.Identity;
        m.RotateAt(angle, b.Left + b.Width / 2, b.Top + b.Height / 2);
        selected.Transform(m, false);
        ink.Select(selected);
        AnchorSelection();
        Changed();
    }

    /// <summary>
    /// Copy the selection as ink (ISF, which OneNote and other Windows Ink apps paste as ink) and as a
    /// picture for everything else. Always in true colours, never the dark-mode display ones.
    /// </summary>
    void CopySelection()
    {
        var selected = ink.GetSelectedStrokes();
        if (selected.Count == 0) return;
        var copy = selected.Clone();
        ToTrueColors(copy);
        var data = new DataObject();
        var isf = new MemoryStream();
        copy.Save(isf, true);
        isf.Position = 0;
        data.SetData(StrokeCollection.InkSerializedFormat, isf);
        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(copy, null)));
        encoder.Save(png);
        png.Position = 0;
        data.SetData("PNG", png);
        data.SetImage(Render(copy, Brushes.White)); // apps that don't take PNG: a white background, not black
        try
        {
            Clipboard.SetDataObject(data, true);
            Notice(selected.Count == 1 ? "Copied 1 stroke" : $"Copied {selected.Count} strokes");
        }
        catch (Exception e)
        {
            Protocol.Log("copy failed: " + e.Message);
            Notice("Couldn't copy: the clipboard is busy");
        }
    }

    static BitmapSource Render(StrokeCollection strokes, Brush? background)
    {
        const double pad = 8, scale = 2;
        var b = strokes.GetBounds();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (background != null) dc.DrawRectangle(background, null, new Rect(0, 0, b.Width + 2 * pad, b.Height + 2 * pad));
            dc.PushTransform(new TranslateTransform(pad - b.Left, pad - b.Top));
            strokes.Draw(dc);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling((b.Width + 2 * pad) * scale), (int)Math.Ceiling((b.Height + 2 * pad) * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Paste ink from the clipboard (ours, or OneNote's), selected so it can be dragged into place.</summary>
    void Paste()
    {
        StrokeCollection strokes;
        try
        {
            if (!Clipboard.ContainsData(StrokeCollection.InkSerializedFormat)) { Notice("No ink on the clipboard"); return; }
            if (Clipboard.GetData(StrokeCollection.InkSerializedFormat) is not Stream stream) return;
            strokes = new StrokeCollection(stream);
        }
        catch (Exception e)
        {
            Protocol.Log("paste failed: " + e.Message);
            Notice("Couldn't read the ink on the clipboard");
            return;
        }
        if (strokes.Count == 0) return;
        ToTrueColors(strokes);
        // A pasted stroke is anchored where it lands, not to the line it was copied from.
        foreach (var s in strokes)
            foreach (var id in new[] { AnchorLineId, AnchorTopId, AboveHashId, AboveOffsetId, BelowHashId, BelowOffsetId })
                if (s.ContainsPropertyData(id)) s.RemovePropertyData(id);
        ToDisplay(strokes);
        Pens.Stylize(strokes);

        // Where it came from if that's on screen (nudged so it's visibly a copy), else the top of the view.
        var view = VisibleInkArea();
        var b = strokes.GetBounds();
        var to = view.Contains(b.TopLeft) ? b.TopLeft + new Vector(16, 16) / Zoom : view.TopLeft + new Vector(40, 40) / Zoom;
        strokes.Transform(new Matrix(1, 0, 0, 1, to.X - b.Left, to.Y - b.Top), false);
        ink.Strokes.Add(strokes);
        ink.Select(strokes);
    }

    /// <summary>The part of the page on screen, in ink units.</summary>
    Rect VisibleInkArea()
    {
        var l = layout;
        if (l == null) return new Rect(0, 0, 400, 400);
        return new Rect(-l.Ox / Zoom, -l.Oy / Zoom, Math.Max(1, l.W) / Zoom, Math.Max(1, l.H) / Zoom);
    }

    // ---------- handwriting index ----------
    // Each note's (and PDF's, image's) handwriting as text, for "Search handwriting". The open note is
    // re-read a few seconds after each save; other ink files when the plugin finds their index stale.

    void ScheduleIndex()
    {
        indexTimer.Stop();
        indexTimer.Start();
    }

    void IndexLoaded()
    {
        // Read from the saved file (it was just saved), like any other ink file.
        if (inkPath != null) QueueIndex(new[] { new IndexFile { Path = inkPath } });
    }

    void QueueIndex(IndexFile[] files)
    {
        foreach (var f in files) if (f.Path != null && !indexQueue.Contains(f.Path)) indexQueue.Enqueue(f.Path);
        if (!indexing) RunIndexQueue();
    }

    /// <summary>
    /// Reading handwriting loads Windows' handwriting models (60+ MB that are never given back), so it's done
    /// in a short-lived copy of this program (--index-files), a batch at a time, whose results are passed on.
    /// </summary>
    async void RunIndexQueue()
    {
        indexing = true;
        try
        {
            while (indexQueue.Count > 0)
            {
                var batch = new List<string>();
                while (indexQueue.Count > 0 && batch.Count < 40) batch.Add(indexQueue.Dequeue());
                await System.Threading.Tasks.Task.Run(() => RunIndexer(batch));
            }
        }
        finally
        {
            indexing = false;
        }
    }

    static void RunIndexer(List<string> paths)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            psi.ArgumentList.Add("--index-files");
            foreach (var p in paths) psi.ArgumentList.Add(p);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            try { proc.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
                if (line.StartsWith("{\"type\":\"ink-index\"")) Protocol.SendRaw(line);
            if (!proc.WaitForExit(60_000)) proc.Kill();
        }
        catch (Exception e)
        {
            Protocol.Log("handwriting index failed: " + e.Message);
        }
    }

    /// <summary>`InkOverlay --index-files a.ink b.ink…`: the handwriting in each file, as ink-index messages.</summary>
    public static int IndexFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                StrokeCollection strokes = new();
                if (File.Exists(path)) { using var fs = File.OpenRead(path); strokes = new StrokeCollection(fs); }
                SendIndex(path, Handwriting.ReadAsync(strokes.ToList(), false).GetAwaiter().GetResult());
            }
            catch (Exception e)
            {
                Protocol.Log($"could not index {path}: {e.Message}");
            }
        }
        return 0;
    }
    /// <summary>Each line of writing with the note line (or PDF page) its ink is anchored to.</summary>
    static void SendIndex(string path, List<WrittenLine> lines)
    {
        Protocol.Send(new
        {
            type = "ink-index",
            path,
            lines = lines.Select(l => new
            {
                text = l.Text,
                line = l.Strokes.Select(s => TryGetAnchor(s, out var n, out _) ? n : -1).GroupBy(n => n)
                    .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(-1),
            }),
        });
    }
}
