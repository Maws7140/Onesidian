using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace InkOverlay;

/// <summary>
/// OneNote outlines back to Markdown, and a fingerprint of an outline's content to tell whether it was
/// edited in OneNote. Only edited/new outlines are ever converted; untouched blocks keep their original
/// Markdown exactly (see OneNoteSync).
/// </summary>
public static class OneNoteToMarkdown
{
    static readonly XNamespace one = OneNote.Ns;

    /// <summary>What the outline says, independent of how OneNote marks it up: per paragraph its depth,
    /// list/check state and plain text. Same content => same fingerprint.</summary>
    public static string Fingerprint(XElement outline)
    {
        var sb = new StringBuilder();
        void Walk(XElement oeChildren, int depth)
        {
            foreach (var oe in oeChildren.Elements(one + "OE"))
            {
                var tag = oe.Element(one + "Tag");
                var list = oe.Element(one + "List");
                sb.Append(depth).Append('|')
                  .Append(tag != null ? (tag.Attribute("completed")?.Value == "true" ? "[x]" : "[ ]") : "")
                  .Append(list?.Element(one + "Number") != null ? "1." : list != null ? "-" : "")
                  .Append('|').Append(Normalize(PlainText(oe))).Append('\n');
                var kids = oe.Element(one + "OEChildren");
                if (kids != null) Walk(kids, depth + 1);
            }
        }
        var top = outline.Element(one + "OEChildren");
        if (top != null) Walk(top, 0);
        using var sha = System.Security.Cryptography.SHA1.Create();
        return System.Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public static string PlainText(XElement oe)
    {
        var html = string.Concat(oe.Elements(one + "T").Select(t => t.Value));
        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        return WebUtility.HtmlDecode(text).Replace(' ', ' ');
    }

    static string Normalize(string s) => Regex.Replace(s, @"[ \t]+", " ").Trim();

    /// <summary>An outline as Markdown lines.</summary>
    public static string Convert(XElement outline)
    {
        var lines = new List<string>();
        var code = new List<string>();
        void FlushCode()
        {
            if (code.Count == 0) return;
            lines.Add("```");
            lines.AddRange(code);
            lines.Add("```");
            code.Clear();
        }
        void Walk(XElement oeChildren, int depth)
        {
            int number = 0;
            foreach (var oe in oeChildren.Elements(one + "OE"))
            {
                string oeStyle = oe.Attribute("style")?.Value ?? "";
                string html = string.Concat(oe.Elements(one + "T").Select(t => t.Value));
                var tag = oe.Element(one + "Tag");
                var list = oe.Element(one + "List");

                if (IsCode(oeStyle, html) && tag == null && list == null)
                {
                    code.Add(PlainText(oe).TrimEnd());
                }
                else
                {
                    FlushCode();
                    string indent = new string('\t', depth);
                    string text = Inline(html);
                    double size = FontSize(oeStyle, html);
                    if (tag != null)
                        lines.Add($"{indent}- [{(tag.Attribute("completed")?.Value == "true" ? "x" : " ")}] {text}");
                    else if (list?.Element(one + "Number") != null)
                        lines.Add($"{indent}{++number}. {text}");
                    else if (list != null)
                        lines.Add($"{indent}- {text}");
                    else if (size >= 19.5)
                        lines.Add("# " + StripBold(text));
                    else if (size >= 15.5)
                        lines.Add("## " + StripBold(text));
                    else if (size >= 13.5)
                        lines.Add("### " + StripBold(text));
                    else
                        lines.Add(indent + text);
                }
                var kids = oe.Element(one + "OEChildren");
                if (kids != null) Walk(kids, depth + 1);
            }
        }
        var top = outline.Element(one + "OEChildren");
        if (top != null) Walk(top, 0);
        FlushCode();
        return string.Join("\n", lines).TrimEnd();
    }

    static string StripBold(string md) => Regex.Replace(md, @"^\*\*(.*)\*\*$", "$1");

    static bool IsCode(string oeStyle, string html)
    {
        bool oeMono = Regex.IsMatch(oeStyle, "Consolas|Courier", RegexOptions.IgnoreCase);
        var text = Regex.Replace(html, "<[^>]+>", "");
        if (text.Trim().Length == 0) return oeMono;
        // Every run of text is in a monospace font.
        var runs = Regex.Matches(html, @"<span[^>]*style='([^']*)'[^>]*>([^<]*)</span>|([^<]+)");
        bool any = false;
        foreach (Match m in runs)
        {
            if (m.Groups[3].Success) { if (m.Groups[3].Value.Trim().Length > 0 && !oeMono) return false; continue; }
            if (m.Groups[2].Value.Trim().Length == 0) continue;
            any = true;
            if (!Regex.IsMatch(m.Groups[1].Value, "Consolas|Courier", RegexOptions.IgnoreCase) && !oeMono) return false;
        }
        return any || oeMono;
    }

    static double FontSize(string oeStyle, string html)
    {
        double size = 0;
        foreach (Match m in Regex.Matches(oeStyle + ";" + html, @"font-size:\s*([\d.]+)pt"))
            size = Math.Max(size, double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        return size;
    }

    [Flags] enum Fmt { None = 0, Bold = 1, Italic = 2, Code = 4, Strike = 8, Mark = 16 }

    /// <summary>OneNote's HTML subset (spans with styles, links, line breaks) to Markdown inline syntax.</summary>
    public static string Inline(string html)
    {
        // OneNote equations come back as $LaTeX$ ($$…$$ when it's a display equation on its own).
        var equations = new List<string>();
        bool alone = Regex.Replace(Regex.Replace(html, @"<!--\[if mathML\]>.*?<!\[endif\]-->", "", RegexOptions.Singleline), "<[^>]+>|&nbsp;|\\s", "").Length == 0;
        html = Regex.Replace(html, @"<!--\[if mathML\]>(.*?)<!\[endif\]-->", m =>
        {
            string tex;
            try { tex = MathML.Convert(m.Groups[1].Value).Latex; }
            catch { return ""; }
            bool display = alone && m.Groups[1].Value.Contains("display=\"block\"");
            equations.Add(display ? $"$${tex}$$" : $"${tex}$");
            return $"\u0002{equations.Count - 1}\u0002";
        }, RegexOptions.Singleline);
        if (equations.Count > 0)
        {
            var md = InlineText(html);
            return Regex.Replace(md, "\u0002(\\d+)\u0002", m => equations[int.Parse(m.Groups[1].Value)]);
        }
        return InlineText(html);
    }

    static string InlineText(string html)
    {
        var segs = new List<(string text, Fmt fmt, string? href)>();
        var fmts = new Stack<Fmt>();
        var hrefs = new Stack<string?>();
        fmts.Push(Fmt.None);
        hrefs.Push(null);
        static string Attr(string tag, string name)
        {
            var a = Regex.Match(tag, $@"\b{name}\s*=\s*(['""])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return a.Success ? a.Groups[2].Value : "";
        }
        foreach (Match m in Regex.Matches(html, @"<span\b[^>]*>|</span>|<a\b[^>]*>|</a>|<br\s*/?>|<[^>]+>|[^<]+", RegexOptions.IgnoreCase))
        {
            var v = m.Value;
            if (v.StartsWith("<span", StringComparison.OrdinalIgnoreCase)) fmts.Push(fmts.Peek() | Parse(Attr(v, "style")));
            else if (v.Equals("</span>", StringComparison.OrdinalIgnoreCase)) { if (fmts.Count > 1) fmts.Pop(); }
            else if (v.StartsWith("<a", StringComparison.OrdinalIgnoreCase)) hrefs.Push(WebUtility.HtmlDecode(Attr(v, "href")));
            else if (v.Equals("</a>", StringComparison.OrdinalIgnoreCase)) { if (hrefs.Count > 1) hrefs.Pop(); }
            else if (v.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) segs.Add(("\n", Fmt.None, null));
            else if (v.StartsWith("<")) { }
            else segs.Add((WebUtility.HtmlDecode(v).Replace(' ', ' '), fmts.Peek(), hrefs.Peek()));
        }
        // Merge neighbours with the same formatting, then wrap each run.
        var merged = new List<(string text, Fmt fmt, string? href)>();
        foreach (var s in segs)
        {
            if (merged.Count > 0 && merged[^1].fmt == s.fmt && merged[^1].href == s.href && s.text != "\n")
                merged[^1] = (merged[^1].text + s.text, s.fmt, s.href);
            else merged.Add(s);
        }
        var sb = new StringBuilder();
        foreach (var (text, fmt, href) in merged)
        {
            if (text == "\n") { sb.Append('\n'); continue; }
            // Keep spaces outside markers ("**bold** ", not "**bold **").
            var lead = text[..(text.Length - text.TrimStart().Length)];
            var core = text.Trim();
            var trail = text[(lead.Length + core.Length)..];
            if (core.Length == 0) { sb.Append(text); continue; }
            string w = core;
            if (fmt.HasFlag(Fmt.Code)) w = "`" + w + "`";
            else
            {
                if (fmt.HasFlag(Fmt.Mark)) w = "==" + w + "==";
                if (fmt.HasFlag(Fmt.Strike)) w = "~~" + w + "~~";
                if (fmt.HasFlag(Fmt.Italic)) w = "*" + w + "*";
                if (fmt.HasFlag(Fmt.Bold)) w = "**" + w + "**";
            }
            if (href != null) w = $"[{w}]({href})";
            sb.Append(lead).Append(w).Append(trail);
        }
        return sb.ToString();
    }

    static Fmt Parse(string style)
    {
        var f = Fmt.None;
        if (Regex.IsMatch(style, @"font-weight:\s*bold", RegexOptions.IgnoreCase)) f |= Fmt.Bold;
        if (Regex.IsMatch(style, @"font-style:\s*italic", RegexOptions.IgnoreCase)) f |= Fmt.Italic;
        if (Regex.IsMatch(style, @"font-family:[^;]*(Consolas|Courier)", RegexOptions.IgnoreCase)) f |= Fmt.Code;
        if (Regex.IsMatch(style, @"line-through", RegexOptions.IgnoreCase)) f |= Fmt.Strike;
        if (Regex.IsMatch(style, @"background(-color)?:\s*yellow|mso-highlight", RegexOptions.IgnoreCase)) f |= Fmt.Mark;
        return f;
    }
}
