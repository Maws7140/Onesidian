using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Xml.Linq;

namespace InkOverlay;

/// <summary>One block of the note (from Obsidian's own section cache), with where it sits on the page.</summary>
public sealed class ExportBlock
{
    public string Kind { get; set; } = "paragraph";   // heading | paragraph | list | code | blockquote | table | thematicBreak | callout | ...
    public string Text { get; set; } = "";             // the block's markdown
    public double Y { get; set; }                       // CSS px from the text column top, like the ink
    public int Start { get; set; }                      // its source lines (0-based, inclusive)
    public int End { get; set; }
    public Dictionary<string, string>? Math { get; set; }  // LaTeX in the block -> its MathML (becomes a OneNote equation)
}

public sealed class ExportRequest
{
    public string Title { get; set; } = "";
    public string? NoteKey { get; set; }                // the note's vault path, echoed back with the page ID
    public string? InkPath { get; set; }
    public string? PageId { get; set; }                 // the page this note went to last time, if any
    public string Section { get; set; } = "Obsidian";
    public List<ExportBlock> Blocks { get; set; } = new();
    public double ContentLeft { get; set; }             // CSS px: where the text column's text starts
    public double ContentWidth { get; set; } = 700;
    public double OriginY { get; set; }                 // CSS px: top of the note body (below the properties)
    public bool WhiteInkToBlack { get; set; } = true;   // near-white ink (for dark themes) -> black, OneNote's "automatic" ink
    public SyncState? State { get; set; }               // the page as left by the last send (to spot OneNote edits)
    public bool Force { get; set; }                     // send even if the page has unpulled OneNote edits
    public bool Navigate { get; set; } = true;          // open the page in OneNote afterwards
}

/// <summary>"Pull from OneNote": bring the page's edits back into the note.</summary>
public sealed class PullRequest
{
    public string? NoteKey { get; set; }
    public string? InkPath { get; set; }
    public SyncState State { get; set; } = new();
}

/// <summary>The page has OneNote edits that a send would overwrite.</summary>
public sealed class PageChangedException(PageDiff diff) : Exception("The OneNote page has changes that haven't been pulled")
{
    public PageDiff Diff { get; } = diff;
}

/// <summary>
/// Sends a note to OneNote as a page: each Markdown block becomes an outline at the same height it has
/// in Obsidian (so handwriting stays next to the text it was written by), and the ink becomes real
/// OneNote ink (ISF, OneNote's own ink format). Re-sending updates the same page.
/// </summary>
public static class OneNoteExport
{
    const double PtPerPx = 0.75;          // CSS px (1/96") to points (1/72")
    const double PageLeft = 36, PageTop = 86; // where OneNote's page body starts, under the title
    static readonly XNamespace one = OneNote.Ns;

    public static SyncState Export(ExportRequest req)
    {
        var app = OneNote.Connect();
        // Don't silently overwrite edits made in OneNote since the last send.
        if (!req.Force && req.State != null && OneNoteSync.PageExists(app, req.State.PageId))
        {
            var diff = OneNoteSync.Diff(app, req.State);
            if (diff.Any) throw new PageChangedException(diff);
        }
        string sectionId = EnsureSection(app, req.Section);
        string pageId = FindPage(app, sectionId, req.PageId ?? req.State?.PageId, req.Title) ?? NewPage(app, sectionId);

        ClearPage(app, pageId);
        var strokes = LoadInk(req.InkPath);
        if (req.WhiteInkToBlack) WhiteToBlack(strokes);
        double originX = Math.Min(req.ContentLeft, strokes.Count > 0 ? strokes.GetBounds().Left : req.ContentLeft);
        double X(double px) => PageLeft + (px - originX) * PtPerPx;
        double Y(double px) => PageTop + Math.Max(0, px - req.OriginY) * PtPerPx;

        var page = new XElement(one + "Page", new XAttribute("ID", pageId));
        var tagDef = new XElement(one + "TagDef", new XAttribute("index", 0), new XAttribute("type", 0), new XAttribute("symbol", 3),
            new XAttribute("fontColor", "automatic"), new XAttribute("highlightColor", "none"), new XAttribute("name", "To Do"));
        bool usesTodo = false;
        var objects = new List<XElement>();
        var written = new List<(ExportBlock, double)>();

        foreach (var block in req.Blocks)
        {
            var oes = BlockToOEs(block, ref usesTodo);
            if (oes.Count == 0) continue;
            written.Add((block, Y(block.Y)));
            objects.Add(new XElement(one + "Outline",
                new XElement(one + "Position", new XAttribute("x", F(X(req.ContentLeft))), new XAttribute("y", F(Y(block.Y))), new XAttribute("z", 0)),
                new XElement(one + "Size", new XAttribute("width", F(req.ContentWidth * PtPerPx)), new XAttribute("height", 14), new XAttribute("isSetByUser", "true")),
                new XElement(one + "OEChildren", oes)));
        }

        if (strokes.Count > 0)
        {
            var b = strokes.GetBounds();
            // ISF is stored in HIMETRIC; OneNote places the drawing at Position, with strokes relative to it.
            var local = strokes.Clone();
            local.Transform(new Matrix(1, 0, 0, 1, -b.Left, -b.Top), false);
            using var ms = new MemoryStream();
            local.Save(ms, false);
            objects.Add(new XElement(one + "InkDrawing",
                new XElement(one + "Position", new XAttribute("x", F(X(b.Left))), new XAttribute("y", F(Y(b.Top))), new XAttribute("z", 1)),
                new XElement(one + "Size", new XAttribute("width", F(b.Width * PtPerPx)), new XAttribute("height", F(b.Height * PtPerPx))),
                new XElement(one + "Data", Convert.ToBase64String(ms.ToArray()))));
        }

        if (usesTodo) page.Add(tagDef);
        page.Add(new XElement(one + "Title", new XElement(one + "OE", new XElement(one + "T", new XCData(WebUtility.HtmlEncode(req.Title))))));
        page.Add(objects);
        var xml = new XDocument(page).ToString(SaveOptions.DisableFormatting);
        app.UpdatePageContent(xml, DateTime.MinValue, OneNote.Xs2013, false);
        // Remember what the page looks like now, so later OneNote edits can be told apart and pulled.
        return OneNoteSync.Capture(app, pageId, written, originX, req.OriginY);
    }

    public static void Open(string pageId) => OneNote.Connect().NavigateTo(pageId, "", false);

    static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    static StrokeCollection LoadInk(string? path)
    {
        if (path == null || !File.Exists(path)) return new StrokeCollection();
        using var fs = File.OpenRead(path);
        return new StrokeCollection(fs); // the saved file has the true colours (no dark-mode flip)
    }

    static string EnsureSection(IOneNoteApplication app, string name)
    {
        var doc = OneNote.Hierarchy(app, "", OneNote.HsSections);
        var existing = doc.Descendants(one + "Section").FirstOrDefault(s => s.Attribute("name")?.Value == name
            && s.Attribute("isInRecycleBin")?.Value != "true");
        if (existing != null) return existing.Attribute("ID")!.Value;
        var notebook = doc.Root!.Elements(one + "Notebook").FirstOrDefault(n => n.Attribute("isCurrentlyViewed")?.Value == "true")
            ?? doc.Root!.Elements(one + "Notebook").First();
        app.OpenHierarchy(name + ".one", notebook.Attribute("ID")!.Value, out var sectionId, 3 /* cftSection */);
        Protocol.Log($"OneNote: created section \"{name}\" in {notebook.Attribute("name")?.Value}");
        return sectionId;
    }

    /// <summary>The page this note went to before: by its ID, else a page in the section with the note's title.</summary>
    static string? FindPage(IOneNoteApplication app, string sectionId, string? pageId, string title)
    {
        var pages = OneNote.Hierarchy(app, sectionId, OneNote.HsPages).Descendants(one + "Page")
            .Where(p => p.Attribute("isInRecycleBin")?.Value != "true").ToList();
        return pages.FirstOrDefault(p => !string.IsNullOrEmpty(pageId) && p.Attribute("ID")?.Value == pageId)?.Attribute("ID")?.Value
            ?? pages.FirstOrDefault(p => p.Attribute("name")?.Value == title)?.Attribute("ID")?.Value;
    }

    /// <summary>Near-white ink (written white for a dark theme) becomes black: OneNote shows black ink white in dark mode.</summary>
    static void WhiteToBlack(StrokeCollection strokes)
    {
        foreach (var s in strokes)
        {
            var c = s.DrawingAttributes.Color;
            bool neutral = Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 12;
            if (!neutral || Math.Min(c.R, Math.Min(c.G, c.B)) < 200) continue;
            var da = s.DrawingAttributes.Clone();
            da.Color = Color.FromArgb(c.A, 0, 0, 0);
            s.DrawingAttributes = da;
        }
    }

    static string NewPage(IOneNoteApplication app, string sectionId)
    {
        app.CreateNewPage(sectionId, out var pageId, 1 /* npsBlankPageWithTitle */);
        return pageId;
    }

    /// <summary>Remove what a previous send put on the page (outlines, ink), so re-sending replaces it.</summary>
    static void ClearPage(IOneNoteApplication app, string pageId)
    {
        app.GetPageContent(pageId, out var xml, 0, OneNote.Xs2013);
        var doc = XDocument.Parse(xml);
        foreach (var o in doc.Root!.Elements().Where(e => e.Name != one + "Title" && e.Attribute("objectID") != null).ToList())
            app.DeletePageContent(pageId, o.Attribute("objectID")!.Value, DateTime.MinValue, false);
    }

    // ---------- Markdown block -> OneNote paragraphs ----------

    static List<XElement> BlockToOEs(ExportBlock block, ref bool usesTodo)
    {
        var lines = block.Text.Replace("\r", "").Split('\n').ToList();
        var oes = new List<XElement>();
        var math = block.Math;
        string Inline(string md) => OneNoteExport.Inline(md, math);
        switch (block.Kind)
        {
            case "math":
                // A $$ block: one display equation.
                oes.Add(OE(Inline(string.Join("\n", lines))));
                return oes;
            case "yaml":
                return oes;
            case "code":
                if (lines.Count > 0 && lines[0].TrimStart().StartsWith("```")) lines.RemoveAt(0);
                if (lines.Count > 0 && lines[^1].TrimStart().StartsWith("```")) lines.RemoveAt(lines.Count - 1);
                foreach (var l in lines)
                    oes.Add(OE(Span(WebUtility.HtmlEncode(l.Length == 0 ? " " : l).Replace(" ", "&nbsp;"), "font-family:Consolas;font-size:10pt")));
                return oes;
            case "heading":
            {
                var m = Regex.Match(lines[0], @"^(#{1,6})\s+(.*)$");
                int level = m.Success ? m.Groups[1].Length : 1;
                string size = level switch { 1 => "20pt", 2 => "16pt", 3 => "14pt", _ => "12pt" };
                oes.Add(OE(Span(Inline(m.Success ? m.Groups[2].Value : lines[0]), $"font-size:{size};font-weight:bold")));
                return oes;
            }
            case "thematicBreak":
                oes.Add(OE("────────────────────────────"));
                return oes;
            case "list":
                return ListToOEs(lines, ref usesTodo, math);
            case "blockquote":
            case "callout":
                foreach (var l in lines)
                {
                    var t = Regex.Replace(l, @"^\s*>\s?", "");
                    t = Regex.Replace(t, @"^\[!(\w+)\][+-]?\s*", m => m.Groups[1].Value.ToUpperInvariant() + ": ");
                    oes.Add(OE(Span(Inline(t), "font-style:italic")));
                }
                return oes;
            default:
                foreach (var l in lines) oes.Add(OE(Inline(l)));
                return oes;
        }
    }

    static List<XElement> ListToOEs(List<string> lines, ref bool usesTodo, Dictionary<string, string>? math)
    {
        string Inline(string md) => OneNoteExport.Inline(md, math);
        var root = new List<XElement>();
        var stack = new List<(int indent, List<XElement> items)> { (-1, root) };
        foreach (var raw in lines)
        {
            var m = Regex.Match(raw, @"^(\s*)([-*+]|\d+[.)])\s+(\[( |x|X)\]\s+)?(.*)$");
            if (!m.Success)
            {
                // Continuation line: add to the last item's text.
                var lastList = stack[^1].items;
                if (lastList.Count > 0) lastList[^1].Element(one + "T")!.ReplaceNodes(new XCData(
                    lastList[^1].Element(one + "T")!.Value + "<br>" + Inline(raw.Trim())));
                continue;
            }
            int indent = m.Groups[1].Value.Replace("\t", "    ").Length;
            while (stack.Count > 1 && indent <= stack[^1].indent) stack.RemoveAt(stack.Count - 1);
            bool numbered = char.IsDigit(m.Groups[2].Value[0]);
            var oe = new XElement(one + "OE");
            if (m.Groups[3].Success)
            {
                usesTodo = true;
                oe.Add(new XElement(one + "Tag", new XAttribute("index", 0), new XAttribute("completed", m.Groups[4].Value != " " ? "true" : "false")));
            }
            else
            {
                oe.Add(new XElement(one + "List", numbered
                    ? new XElement(one + "Number", new XAttribute("numberSequence", 0), new XAttribute("numberFormat", "##."))
                    : new XElement(one + "Bullet", new XAttribute("bullet", 2))));
            }
            oe.Add(new XElement(one + "T", new XCData(Inline(m.Groups[5].Value))));
            stack[^1].items.Add(oe);
            // Children of this item go in its OEChildren.
            var children = new List<XElement>();
            stack.Add((indent, children));
            oe.AddAnnotation(children);
        }
        Nest(root);
        return root;

        static void Nest(List<XElement> items)
        {
            foreach (var oe in items)
            {
                var kids = oe.Annotation<List<XElement>>();
                if (kids == null || kids.Count == 0) continue;
                Nest(kids);
                oe.Add(new XElement(one + "OEChildren", kids));
            }
        }
    }

    static XElement OE(string html) => new(one + "OE", new XElement(one + "T", new XCData(html)));

    static string Span(string html, string style) => $"<span style='{style}'>{html}</span>";

    /// <summary>$$display$$ and $inline$ math (the same patterns the plugin found MathML for).</summary>
    internal static readonly Regex MathPattern = new(@"\$\$([\s\S]+?)\$\$|(?<![\\$])\$(?!\s)([^$\n]+?)(?<!\s)\$(?!\d)");

    /// <summary>A OneNote equation: MathML in the conditional comment OneNote itself uses.</summary>
    internal static string Equation(string mathml) => $"<!--[if mathML]>{mathml}<![endif]-->";

    /// <summary>Markdown inline formatting to OneNote's HTML subset; math becomes OneNote equations.</summary>
    static string Inline(string md, Dictionary<string, string>? math = null)
    {
        var codes = new List<string>();
        string s = Regex.Replace(md, "`([^`]+)`", m => { codes.Add(m.Groups[1].Value); return $"\u0001{codes.Count - 1}\u0001"; });
        var equations = new List<string>();
        s = MathPattern.Replace(s, m =>
        {
            var tex = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (math == null || !math.TryGetValue(tex, out var mathml)) return m.Value;
            equations.Add(Equation(mathml));
            return $"\u0002{equations.Count - 1}\u0002";
        });
        s = WebUtility.HtmlEncode(s);
        s = Regex.Replace(s, @"!\[\[([^\]]+)\]\]", "[$1]");
        s = Regex.Replace(s, @"\[\[([^\]|]+)\|([^\]]+)\]\]", "$2");
        s = Regex.Replace(s, @"\[\[([^\]]+)\]\]", "$1");
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)\)", "<a href=\"$2\">$1</a>");
        s = Regex.Replace(s, @"\*\*(.+?)\*\*|__(.+?)__", m => Span(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, "font-weight:bold"));
        s = Regex.Replace(s, @"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?!\w)|(?<![\w_])_(?!\s)(.+?)(?<!\s)_(?!\w)",
            m => Span(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, "font-style:italic"));
        s = Regex.Replace(s, @"~~(.+?)~~", m => Span(m.Groups[1].Value, "text-decoration:line-through"));
        s = Regex.Replace(s, @"==(.+?)==", m => Span(m.Groups[1].Value, "background:yellow"));
        s = Regex.Replace(s, "\u0001(\\d+)\u0001", m => Span(WebUtility.HtmlEncode(codes[int.Parse(m.Groups[1].Value)]), "font-family:Consolas"));
        s = Regex.Replace(s, "\u0002(\\d+)\u0002", m => equations[int.Parse(m.Groups[1].Value)]);
        return s.Length == 0 ? " " : s;
    }

    /// <summary>`InkOverlay --onenote-ink pageId`: the ink OneNote actually stored on a page (strokes, colours).</summary>
    public static int InkReport(string pageId)
    {
        var app = OneNote.Connect();
        app.GetPageContent(pageId, out var pageXml, 0, OneNote.Xs2013);
        foreach (var d in XDocument.Parse(pageXml).Descendants(one + "InkDrawing"))
        {
            var cb = d.Element(one + "CallbackID")?.Attribute("callbackID")?.Value;
            string b64;
            if (cb != null) app.GetBinaryPageContent(pageId, cb, out b64);
            else b64 = d.Element(one + "Data")?.Value ?? "";
            var sc = new StrokeCollection(new MemoryStream(Convert.FromBase64String(b64)));
            var pos = d.Element(one + "Position");
            var size = d.Element(one + "Size");
            var b = sc.GetBounds();
            var p0 = sc.Count > 0 ? sc[0].StylusPoints[0] : default;
            Console.WriteLine($"ink drawing: {sc.Count} strokes, colours {string.Join(" ", sc.Select(s => s.DrawingAttributes.Color.ToString()).Distinct())}");
            Console.WriteLine($"  page position {pos?.Attribute("x")?.Value},{pos?.Attribute("y")?.Value} size {size?.Attribute("width")?.Value}x{size?.Attribute("height")?.Value} (pt)");
            Console.WriteLine($"  ISF bounds (DIP) {b.Left:0.##},{b.Top:0.##} {b.Width:0.##}x{b.Height:0.##}; first point {p0.X:0.##},{p0.Y:0.##}");
            Console.WriteLine($"  ISF properties on first stroke: {(sc.Count > 0 ? string.Join(",", sc[0].GetPropertyDataIds()) : "")}");
            foreach (var w in XDocument.Parse(pageXml).Descendants().Where(e => e.Name.LocalName is "InkWord" or "InkParagraph").Take(1))
                Console.WriteLine($"  (page also has {w.Name.LocalName} elements)");
        }
        return 0;
    }

    /// <summary>`InkOverlay --onenote-export request.json`: send a note described in a JSON file, print the page ID.</summary>
    public static int ExportFromFile(string jsonPath)
    {
        try
        {
            var req = System.Text.Json.JsonSerializer.Deserialize<ExportRequest>(File.ReadAllText(jsonPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var pageId = Export(req).PageId;
            Console.WriteLine("page: " + pageId);
            OneNote.Connect().GetPageContent(pageId, out var xml, 0, OneNote.Xs2013);
            var doc = XDocument.Parse(xml);
            foreach (var e in doc.Root!.Elements().Where(e => e.Name.LocalName is "Outline" or "InkDrawing"))
            {
                var p = e.Element(one + "Position");
                var sz = e.Element(one + "Size");
                Console.WriteLine($"{e.Name.LocalName}: pos {p?.Attribute("x")?.Value},{p?.Attribute("y")?.Value} size {sz?.Attribute("width")?.Value}x{sz?.Attribute("height")?.Value}"
                    + (e.Name.LocalName == "InkDrawing" ? $" data {(e.Element(one + "Data")?.Value.Length ?? 0)} chars, callback {e.Element(one + "CallbackID")?.Attribute("callbackID")?.Value != null}" : ""));
            }
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("FAILED: " + e);
            return 1;
        }
    }
}
