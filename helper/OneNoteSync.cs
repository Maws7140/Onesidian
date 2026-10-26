using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Ink;
using System.Windows.Media;
using System.Xml.Linq;

namespace InkOverlay;

/// <summary>One sent block: which OneNote outline it became, what that outline said, and its Markdown.</summary>
public sealed class SyncBlock
{
    public string ObjectId { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Text { get; set; } = "";   // the block's Markdown when sent
    public int Start { get; set; }
    public int End { get; set; }
    public double Y { get; set; }            // outline top on the page (pt)
}

/// <summary>What the page looked like right after the last send (kept by the plugin per note).</summary>
public sealed class SyncState
{
    public string PageId { get; set; } = "";
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public string InkSig { get; set; } = "";
    // Fingerprint of the page's XML when this state was taken. OneNote doesn't reliably bump a page's
    // modified time, but any real edit changes its XML (text, object IDs, sizes, per-object timestamps).
    public string LastModified { get; set; } = "";
    public List<SyncBlock> Blocks { get; set; } = new();
}

public sealed class TextChange
{
    public string Kind { get; set; } = "";      // changed | deleted | new
    public int Block { get; set; } = -1;        // index into SyncState.Blocks (changed/deleted)
    public int AfterBlock { get; set; } = -1;   // new: goes after this block (-1 = top of the note body)
    public string OldText { get; set; } = "";
    public string Markdown { get; set; } = "";
    public string ObjectId { get; set; } = "";  // the OneNote outline (changed/new)
    public string Hash { get; set; } = "";
    public double Y { get; set; }
}

/// <summary>The page compared with the state from the last send.</summary>
public sealed class PageDiff
{
    public List<TextChange> Changes { get; } = new();
    public bool InkChanged { get; set; }
    public StrokeCollection Ink { get; set; } = new();   // page ink in Obsidian's ink coordinates
    public int InkWords { get; set; }                     // handwriting inside text boxes (placed approximately)
    public string InkSig { get; set; } = "";
    public string LastModified { get; set; } = "";
    public bool Any => Changes.Count > 0 || InkChanged;

    /// <summary>The state once these changes are in the note: so the next check doesn't report them again.</summary>
    public SyncState After(SyncState before)
    {
        var blocks = new List<SyncBlock>();
        for (int i = 0; i < before.Blocks.Count; i++)
        {
            var c = Changes.FirstOrDefault(x => x.Block == i && x.Kind != "new");
            if (c?.Kind == "deleted") continue;
            var b = before.Blocks[i];
            blocks.Add(c == null ? b : new SyncBlock { ObjectId = b.ObjectId, Hash = c.Hash, Text = c.Markdown, Start = b.Start, End = b.End, Y = c.Y });
        }
        foreach (var c in Changes.Where(x => x.Kind == "new"))
            blocks.Add(new SyncBlock { ObjectId = c.ObjectId, Hash = c.Hash, Text = c.Markdown, Start = -1, End = -1, Y = c.Y });
        return new SyncState
        {
            PageId = before.PageId, OriginX = before.OriginX, OriginY = before.OriginY,
            InkSig = InkSig, LastModified = LastModified, Blocks = blocks.OrderBy(b => b.Y).ToList(),
        };
    }
}

public static class OneNoteSync
{
    const double PtPerPx = 0.75, PageLeft = 36, PageTop = 86;
    static readonly XNamespace one = OneNote.Ns;

    internal static XDocument Page(IOneNoteApplication app, string pageId)
    {
        app.GetPageContent(pageId, out var xml, 0, OneNote.Xs2013);
        return XDocument.Parse(xml);
    }

    /// <summary>The page's content fingerprint ("" if it's gone): a hash of its XML, without ink data.</summary>
    internal static string LastModified(IOneNoteApplication app, string pageId)
    {
        try
        {
            if (!PageExists(app, pageId)) return "";
            app.GetPageContent(pageId, out var xml, 0, OneNote.Xs2013);
            using var sha = System.Security.Cryptography.SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(xml)))[..20];
        }
        catch (System.Runtime.InteropServices.COMException) { return ""; }
    }

    internal static bool PageExists(IOneNoteApplication app, string pageId)
    {
        if (string.IsNullOrEmpty(pageId)) return false;
        try
        {
            var doc = OneNote.Hierarchy(app, pageId, 0 /* hsSelf */);
            return doc.Root?.Attribute("isInRecycleBin")?.Value != "true";
        }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    static StrokeCollection? InkData(IOneNoteApplication app, string pageId, XElement e)
    {
        var cb = e.Element(one + "CallbackID")?.Attribute("callbackID")?.Value;
        string b64;
        if (cb != null) app.GetBinaryPageContent(pageId, cb, out b64);
        else b64 = e.Element(one + "Data")?.Value ?? "";
        return b64.Length == 0 ? null : new StrokeCollection(new MemoryStream(Convert.FromBase64String(b64)));
    }

    /// <summary>
    /// All ink on the page as one collection: free ink (InkDrawing, exactly placed) and handwriting inside
    /// text boxes (InkWord: OneNote doesn't say where each word sits, so it goes on its paragraph's line in
    /// that box, words left to right). In page points for fingerprinting, or Obsidian px with `toObsidian`.
    /// </summary>
    static (StrokeCollection ink, string sig, int inkWords) ReadInk(IOneNoteApplication app, string pageId, XDocument page, SyncState? toObsidian)
    {
        var all = new StrokeCollection();
        var parts = new List<string>();
        int points = 0, inkWords = 0;
        void Place(StrokeCollection strokes, double x, double y)
        {
            parts.Add($"{strokes.Count}@{Math.Round(x)},{Math.Round(y)}");
            points += strokes.Sum(s => s.StylusPoints.Count);
            // Inverse of the export: page points -> CSS px in the text column's space.
            if (toObsidian != null)
                strokes.Transform(new Matrix(1, 0, 0, 1, toObsidian.OriginX + (x - PageLeft) / PtPerPx, toObsidian.OriginY + (y - PageTop) / PtPerPx), false);
            all.Add(strokes);
        }

        foreach (var d in page.Root!.Elements(one + "InkDrawing"))
        {
            var strokes = InkData(app, pageId, d);
            if (strokes == null) continue;
            var pos = d.Element(one + "Position");
            Place(strokes, Num(pos?.Attribute("x")), Num(pos?.Attribute("y")));
        }

        foreach (var o in page.Root!.Elements(one + "Outline"))
        {
            var oes = o.Descendants(one + "OE").ToList();
            if (!oes.Any(oe => oe.Descendants(one + "InkWord").Any())) continue;
            var pos = o.Element(one + "Position");
            double ox = Num(pos?.Attribute("x")), oy = Num(pos?.Attribute("y"));
            double lineH = Math.Max(14, Num(o.Element(one + "Size")?.Attribute("height")) / Math.Max(1, oes.Count));
            for (int k = 0; k < oes.Count; k++)
            {
                // Only this paragraph's own words, not those of nested paragraphs.
                var words = oes[k].Elements().Where(e => e.Name != one + "OEChildren").SelectMany(e => e.DescendantsAndSelf(one + "InkWord"));
                double x = ox;
                foreach (var w in words)
                {
                    var strokes = InkData(app, pageId, w);
                    if (strokes == null || strokes.Count == 0) continue;
                    var b = strokes.GetBounds();
                    strokes.Transform(new Matrix(1, 0, 0, 1, -b.Left, -b.Top), false);
                    Place(strokes, x, oy + k * lineH);
                    x += b.Width * PtPerPx + 4;
                    inkWords++;
                }
            }
        }
        return (all, $"{all.Count}|{points}|{string.Join(";", parts)}", inkWords);
    }

    static double Num(XAttribute? a) =>
        a == null ? 0 : double.Parse(a.Value, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Right after a send: which outline each block became, and the page's ink fingerprint.</summary>
    internal static SyncState Capture(IOneNoteApplication app, string pageId, List<(ExportBlock block, double y)> written, double originX, double originY)
    {
        var page = Page(app, pageId);
        var outlines = page.Root!.Elements(one + "Outline").ToList();
        var used = new HashSet<XElement>();
        var state = new SyncState { PageId = pageId, OriginX = originX, OriginY = originY };
        foreach (var (block, y) in written)
        {
            var o = outlines.Where(x => !used.Contains(x))
                .OrderBy(x => Math.Abs(Num(x.Element(one + "Position")?.Attribute("y")) - y)).FirstOrDefault();
            if (o == null) continue;
            used.Add(o);
            state.Blocks.Add(new SyncBlock
            {
                ObjectId = o.Attribute("objectID")?.Value ?? "",
                Hash = OneNoteToMarkdown.Fingerprint(o),
                Text = block.Text,
                Start = block.Start,
                End = block.End,
                Y = Num(o.Element(one + "Position")?.Attribute("y")),
            });
        }
        state.InkSig = ReadInk(app, pageId, page, null).sig;
        state.LastModified = LastModified(app, pageId);
        return state;
    }

    /// <summary>What changed on the page since the last send: edited/new/deleted text blocks, and the ink.</summary>
    internal static PageDiff Diff(IOneNoteApplication app, SyncState state)
    {
        var page = Page(app, state.PageId);
        var diff = new PageDiff();
        var outlines = page.Root!.Elements(one + "Outline").Where(o => o.Attribute("objectID") != null).ToList();
        var byId = outlines.ToDictionary(o => o.Attribute("objectID")!.Value);

        for (int i = 0; i < state.Blocks.Count; i++)
        {
            var sb = state.Blocks[i];
            if (!byId.TryGetValue(sb.ObjectId, out var o))
                diff.Changes.Add(new TextChange { Kind = "deleted", Block = i, OldText = sb.Text });
            else
            {
                var hash = OneNoteToMarkdown.Fingerprint(o);
                if (hash != sb.Hash)
                    diff.Changes.Add(new TextChange
                    {
                        Kind = "changed", Block = i, OldText = sb.Text, Markdown = OneNoteToMarkdown.Convert(o),
                        ObjectId = sb.ObjectId, Hash = hash, Y = Num(o.Element(one + "Position")?.Attribute("y")),
                    });
            }
        }
        var known = state.Blocks.Select(b => b.ObjectId).ToHashSet();
        foreach (var o in outlines.Where(o => !known.Contains(o.Attribute("objectID")!.Value))
                     .OrderBy(o => Num(o.Element(one + "Position")?.Attribute("y"))))
        {
            var md = OneNoteToMarkdown.Convert(o);
            if (md.Trim().Length == 0) continue;
            double y = Num(o.Element(one + "Position")?.Attribute("y"));
            int after = -1;
            for (int i = 0; i < state.Blocks.Count; i++) if (state.Blocks[i].Y <= y) after = i;
            diff.Changes.Add(new TextChange
            {
                Kind = "new", AfterBlock = after, Markdown = md,
                ObjectId = o.Attribute("objectID")!.Value, Hash = OneNoteToMarkdown.Fingerprint(o), Y = y,
            });
        }

        var (ink, sig, inkWords) = ReadInk(app, state.PageId, page, state);
        diff.Ink = ink;
        diff.InkSig = sig;
        diff.InkChanged = sig != state.InkSig;
        diff.InkWords = inkWords;
        diff.LastModified = LastModified(app, state.PageId);
        return diff;
    }

    /// <summary>Which of these pages were modified in OneNote since their state was taken (cheap: timestamps only).</summary>
    internal static List<string> ChangedPages(IOneNoteApplication app, IEnumerable<(string noteKey, string pageId, string lastModified)> pages)
    {
        var changed = new List<string>();
        foreach (var (noteKey, pageId, lastModified) in pages)
        {
            var now = LastModified(app, pageId);
            if (now.Length > 0 && now != lastModified) changed.Add(noteKey);
        }
        return changed;
    }
}
