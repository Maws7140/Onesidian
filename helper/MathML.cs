using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace InkOverlay;

/// <summary>
/// MathML (from Windows' math recognizer, or a OneNote equation) to the two forms the plugin needs:
/// LaTeX for the note (Obsidian renders it), and a plain linear expression a computer algebra system can
/// work with ("2*x+3=7", "integrate(x^2,x)", "matrix([1,2],[3,4])"). Several equations stacked (a table
/// of rows with "=") come back as a system, one equation per line.
/// </summary>
public static class MathML
{
    public sealed record Converted(string Latex, string Linear);

    public static Converted Convert(string mathml)
    {
        var root = XElement.Parse(mathml);
        var tex = Clean(Tex(root));
        var lin = Lin(root);
        return new Converted(tex, lin);
    }

    static string Name(XElement e) => e.Name.LocalName;
    static string Text(XElement e) => e.Value.Trim();
    static List<XElement> Kids(XElement e) => e.Elements().ToList();

    static readonly HashSet<string> Functions = new()
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh",
        "log", "ln", "exp", "sqrt", "abs", "det", "min", "max",
    };

    static readonly Dictionary<string, string> Greek = new()
    {
        ["α"] = "alpha", ["β"] = "beta", ["γ"] = "gamma", ["δ"] = "delta", ["ε"] = "epsilon", ["θ"] = "theta",
        ["λ"] = "lambda", ["μ"] = "mu", ["π"] = "pi", ["σ"] = "sigma", ["φ"] = "phi", ["ω"] = "omega",
        ["Δ"] = "Delta", ["Σ"] = "Sigma", ["Ω"] = "Omega",
    };

    // ---------- LaTeX ----------

    static readonly Dictionary<string, string> TexOps = new()
    {
        ["−"] = "-", ["×"] = "\\times", ["·"] = "\\cdot", ["⋅"] = "\\cdot", ["∗"] = "*", ["÷"] = "\\div",
        ["≤"] = "\\le", ["≥"] = "\\ge", ["≠"] = "\\ne", ["≈"] = "\\approx", ["±"] = "\\pm", ["∓"] = "\\mp",
        ["→"] = "\\to", ["∞"] = "\\infty", ["∫"] = "\\int", ["∑"] = "\\sum", ["∏"] = "\\prod", ["∂"] = "\\partial",
        ["{"] = "\\{", ["}"] = "\\}", ["′"] = "'", ["°"] = "^\\circ", ["∈"] = "\\in", ["⇒"] = "\\Rightarrow",
    };

    static string Tex(XElement e)
    {
        var k = Kids(e);
        switch (Name(e))
        {
            case "math":
            case "mrow":
            case "mstyle":
            case "mpadded":
                {
                    // A function name written letter by letter (s, i, n) is still \sin.
                    var parts = new List<string>();
                    for (int i = 0; i < k.Count; i++)
                    {
                        if (Name(k[i]) == "mi" && Text(k[i]).Length == 1 && FunctionAt(k, i, out var fn, out int letters) && letters > 1)
                        {
                            parts.Add("\\" + fn);
                            i += letters - 1;
                            continue;
                        }
                        parts.Add(Tex(k[i]));
                    }
                    return string.Join(" ", parts);
                }
            case "semantics":
                return k.Count > 0 ? Tex(k[0]) : "";
            case "mphantom":
            case "annotation":
            case "annotation-xml":
                return "";
            case "mn":
                return Text(e);
            case "mi":
                {
                    var t = Text(e);
                    if (Greek.TryGetValue(t, out var g)) return "\\" + g;
                    if (t == "∞") return "\\infty";
                    if (Functions.Contains(t) || t == "lim") return "\\" + (t == "abs" ? "operatorname{abs}" : t);
                    return t.Length > 1 ? $"\\mathrm{{{t}}}" : t;
                }
            case "mo":
                {
                    var t = Text(e);
                    if (t == "lim") return "\\lim";
                    return TexOps.TryGetValue(t, out var m) ? m : t;
                }
            case "mtext":
                return $"\\text{{{Text(e)}}}";
            case "mspace":
                return "\\,";
            case "msup":
                return $"{Group(k[0])}^{{{Tex(k[1])}}}";
            case "msub":
                return $"{Group(k[0])}_{{{Tex(k[1])}}}";
            case "msubsup":
                return $"{Group(k[0])}_{{{Tex(k[1])}}}^{{{Tex(k[2])}}}";
            case "munder":
                return $"{Tex(k[0])}_{{{Tex(k[1])}}}";
            case "mover":
                {
                    var accent = Text(k[1]);
                    var cmd = accent switch { "¯" or "‾" or "―" => "overline", "^" or "ˆ" => "hat", "→" or "⃗" => "vec", "˙" => "dot", "~" or "˜" => "tilde", _ => null };
                    return cmd != null ? $"\\{cmd}{{{Tex(k[0])}}}" : $"\\overset{{{Tex(k[1])}}}{{{Tex(k[0])}}}";
                }
            case "munderover":
                return $"{Tex(k[0])}_{{{Tex(k[1])}}}^{{{Tex(k[2])}}}";
            case "mfrac":
                return $"\\frac{{{Tex(k[0])}}}{{{Tex(k[1])}}}";
            case "msqrt":
                return $"\\sqrt{{{string.Join(" ", k.Select(Tex))}}}";
            case "mroot":
                return $"\\sqrt[{Tex(k[1])}]{{{Tex(k[0])}}}";
            case "mfenced":
                {
                    var open = (string?)e.Attribute("open") ?? "(";
                    var close = (string?)e.Attribute("close") ?? ")";
                    if (k.Count == 1 && Name(k[0]) == "mtable" && !IsSystem(k[0]))
                    {
                        var env = (open, close) switch { ("(", ")") => "pmatrix", ("[", "]") => "bmatrix", ("|", "|") => "vmatrix", _ => "matrix" };
                        return $"\\begin{{{env}}}{TableRows(k[0])}\\end{{{env}}}";
                    }
                    var sep = (string?)e.Attribute("separators") ?? ",";
                    return $"\\left{Fence(open)} {string.Join(sep.Trim() + " ", k.Select(Tex))} \\right{Fence(close)}";
                }
            case "mtable":
                return IsSystem(e)
                    ? $"\\begin{{cases}}{TableRows(e)}\\end{{cases}}"
                    : $"\\begin{{matrix}}{TableRows(e)}\\end{{matrix}}";
            default:
                return string.Join(" ", k.Select(Tex));
        }
    }

    static string Fence(string f) => f switch { "{" => "\\{", "}" => "\\}", "" => ".", "‖" => "\\|", _ => f };

    static string TableRows(XElement table) =>
        string.Join(" \\\\ ", table.Elements().Where(r => Name(r) is "mtr" or "mlabeledtr")
            .Select(r => string.Join(" & ", r.Elements().Where(c => Name(c) == "mtd").Select(Tex))));

    /// <summary>A sub/superscript base: wrapped in braces when it's more than one symbol.</summary>
    static string Group(XElement e)
    {
        var t = Tex(e);
        return Name(e) is "mi" or "mn" or "mo" || t.StartsWith("\\left") ? t : $"{{{t}}}";
    }

    /// <summary>Tidy spacing: keep a space only where LaTeX needs one (a command followed by a letter).</summary>
    static string Clean(string tex)
    {
        tex = Regex.Replace(tex, @"\s+", " ").Trim();
        tex = Regex.Replace(tex, @"(?<![_^])\{([A-Za-z0-9])\}(?=[\^_])", "$1");
        return Regex.Replace(tex, @" (?=.)", m =>
        {
            int i = m.Index;
            bool afterCommand = Regex.IsMatch(tex[..i], @"\\[A-Za-z]+$");
            return afterCommand && char.IsLetter(tex[i + 1]) ? " " : "";
        });
    }

    // ---------- linear (for the algebra system) ----------

    static string Lin(XElement e)
    {
        switch (Name(e))
        {
            case "math":
            case "mrow":
            case "mstyle":
            case "mpadded":
                {
                    // A table of equations at the top: a system, one per line.
                    var k = Kids(e);
                    if (k.Count == 1 && Name(k[0]) == "mtable" && IsSystem(k[0])) return Lin(k[0]);
                    return Row(Flatten(e));
                }
            case "mtable":
                if (IsSystem(e))
                    return string.Join("\n", e.Elements().Where(r => Name(r) is "mtr" or "mlabeledtr")
                        .Select(r => Row(r.Elements().SelectMany(Flatten).ToList())));
                return Matrix(e);
            default:
                return Row(new List<XElement> { e });
        }
    }

    /// <summary>Rows (mrow, mstyle...) only group; their children read as part of the surrounding sequence.</summary>
    static List<XElement> Flatten(XElement e)
    {
        if (Name(e) is "math" or "mrow" or "mstyle" or "mpadded" or "mtd")
            return e.Elements().SelectMany(Flatten).ToList();
        if (Name(e) == "semantics") return e.Elements().Take(1).SelectMany(Flatten).ToList();
        if (Name(e) is "mphantom" or "annotation" or "annotation-xml") return new List<XElement>();
        return new List<XElement> { e };
    }

    static string Matrix(XElement table) =>
        "matrix(" + string.Join(",", table.Elements().Where(r => Name(r) is "mtr" or "mlabeledtr")
            .Select(r => "[" + string.Join(",", r.Elements().Where(c => Name(c) == "mtd").Select(c => Row(Flatten(c)))) + "]")) + ")";

    static bool IsSystem(XElement table) =>
        table.Descendants().Any(d => Name(d) == "mo" && Text(d) is "=" or "<" or ">" or "≤" or "≥" or "≠");

    static readonly Dictionary<string, string> LinOps = new()
    {
        ["−"] = "-", ["×"] = "*", ["·"] = "*", ["⋅"] = "*", ["∗"] = "*", ["÷"] = "/", ["≤"] = "<=", ["≥"] = ">=",
        ["≠"] = "!=", ["["] = "(", ["]"] = ")", ["{"] = "(", ["}"] = ")",
    };

    /// <summary>
    /// A sequence of symbols as a linear expression: explicit * where multiplication is implied (2x, x(x+1)),
    /// function calls (sin x → sin(x)), and the calculus notations (∫ … dx, d/dx, lim, Σ).
    /// </summary>
    static string Row(List<XElement> items)
    {
        var sb = new StringBuilder();
        bool prevOperand = false;
        bool inAbs = false;

        void Operand(string s)
        {
            if (prevOperand) sb.Append('*');
            sb.Append(s);
            prevOperand = true;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var e = items[i];
            var name = Name(e);
            var t = Text(e);

            // Function names, whether written as one symbol ("sin") or letter by letter.
            if (name == "mi" && FunctionAt(items, i, out var fn, out int letters))
            {
                i += letters - 1;
                var arg = Argument(items, ref i);
                Operand($"{LinFunction(fn)}({arg})");
                continue;
            }
            if (name is "msup" && Name(Kids(e)[0]) == "mi" && Functions.Contains(Text(Kids(e)[0])))
            {
                var power = Lin(Kids(e)[1]);
                var arg = Argument(items, ref i);
                Operand($"{LinFunction(Text(Kids(e)[0]))}({arg})^({power})");
                continue;
            }
            // log with a base: log_b(x) = ln(x)/ln(b)
            if (name == "msub" && Text(Kids(e)[0]) == "log")
            {
                var b = Lin(Kids(e)[1]);
                var arg = Argument(items, ref i);
                Operand($"(log({arg})/log({b}))");
                continue;
            }
            // Integrals: ∫ f dx, ∫_a^b f dx
            if ((name == "mo" && t == "∫") || (name is "msubsup" or "munderover" && Text(Kids(e)[0]) == "∫"))
            {
                int d = FindDifferential(items, i + 1, out var v);
                var body = Row(items.Skip(i + 1).Take((d < 0 ? items.Count : d) - i - 1).ToList());
                if (d < 0) v = "x";
                Operand(name == "mo" ? $"integrate({body},{v})" : $"defint({body},{Lin(Kids(e)[1])},{Lin(Kids(e)[2])},{v})");
                i = d < 0 ? items.Count : d + (Text(items[d]) == "d" ? 1 : 0);
                continue;
            }
            // d/dx f  (the rest of the term is what's differentiated)
            if (name == "mfrac" && IsDdx(e, out var dv))
            {
                var rest = Term(items, i + 1, out int end);
                Operand($"diff({rest},{dv})");
                i = end - 1;
                continue;
            }
            // lim_{x→a} f
            if (name == "munder" && Text(Kids(e)[0]) == "lim")
            {
                var under = Flatten(Kids(e)[1]);
                int arrow = under.FindIndex(u => Text(u) is "→" or "->");
                var v = arrow > 0 ? Row(under.Take(arrow).ToList()) : "x";
                var to = arrow >= 0 ? Row(under.Skip(arrow + 1).ToList()) : "0";
                var rest = Term(items, i + 1, out int end);
                Operand($"limit({rest},{v},{to})");
                i = end - 1;
                continue;
            }
            // Σ_{k=1}^{n} f
            if (name is "munderover" or "msubsup" && Text(Kids(e)[0]) is "∑" or "∏")
            {
                var under = Flatten(Kids(e)[1]);
                int eq = under.FindIndex(u => Text(u) == "=");
                var v = eq > 0 ? Row(under.Take(eq).ToList()) : "k";
                var from = eq >= 0 ? Row(under.Skip(eq + 1).ToList()) : "1";
                var rest = Term(items, i + 1, out int end);
                Operand($"{(Text(Kids(e)[0]) == "∑" ? "sum" : "product")}({rest},{v},{from},{Lin(Kids(e)[2])})");
                i = end - 1;
                continue;
            }

            switch (name)
            {
                case "mn":
                    {
                        var digits = t.Replace(",", "");
                        // Digits written apart are still one number.
                        if (sb.Length > 0 && i > 0 && Name(items[i - 1]) == "mn") sb.Append(digits);
                        else Operand(digits);
                        break;
                    }
                case "mi":
                    if (t == "∞") { Operand("Infinity"); break; }
                    if (Greek.TryGetValue(t, out var g)) { Operand(g); break; }
                    // Several letters as one symbol (xy): a product of variables.
                    foreach (var c in t) Operand(c.ToString());
                    break;
                case "mo":
                    {
                        if (t is "(" or "[" or "{")
                        {
                            if (prevOperand) sb.Append('*');
                            sb.Append('(');
                            prevOperand = false;
                        }
                        else if (t is ")" or "]" or "}")
                        {
                            sb.Append(')');
                            prevOperand = true;
                        }
                        else if (t is "|")
                        {
                            if (!inAbs) { if (prevOperand) sb.Append('*'); sb.Append("abs("); prevOperand = false; inAbs = true; }
                            else { sb.Append(')'); prevOperand = true; inAbs = false; }
                        }
                        else if (t == "!") { sb.Append('!'); prevOperand = true; }
                        else if (t == "∞") Operand("Infinity");
                        else if (t is "π") Operand("pi");
                        else if (t is "°") { sb.Append("*pi/180"); prevOperand = true; }
                        else if (t is "⁡" or "⁢" or "⁣" or "") { /* invisible function application / times / separator */ }
                        else
                        {
                            sb.Append(LinOps.TryGetValue(t, out var op) ? op : t);
                            prevOperand = false;
                        }
                        break;
                    }
                case "msup":
                    {
                        var k = Kids(e);
                        Operand($"{Wrap(Lin(k[0]), k[0])}^({Lin(k[1])})");
                        break;
                    }
                case "msub":
                    {
                        var k = Kids(e);
                        // x_1 is a variable of its own.
                        var sub = Regex.Replace(Lin(k[1]), @"[^A-Za-z0-9]", "");
                        Operand(Name(k[0]) == "mi" ? Text(k[0]) + sub : Wrap(Lin(k[0]), k[0]));
                        break;
                    }
                case "msubsup":
                    {
                        var k = Kids(e);
                        var sub = Regex.Replace(Lin(k[1]), @"[^A-Za-z0-9]", "");
                        var b = Name(k[0]) == "mi" ? Text(k[0]) + sub : Wrap(Lin(k[0]), k[0]);
                        Operand($"{b}^({Lin(k[2])})");
                        break;
                    }
                case "mfrac":
                    {
                        var k = Kids(e);
                        Operand($"(({Lin(k[0])})/({Lin(k[1])}))");
                        break;
                    }
                case "msqrt":
                    Operand($"sqrt({Row(e.Elements().SelectMany(Flatten).ToList())})");
                    break;
                case "mroot":
                    {
                        var k = Kids(e);
                        Operand($"({Lin(k[0])})^(1/({Lin(k[1])}))");
                        break;
                    }
                case "mfenced":
                    {
                        var k = Kids(e);
                        var open = (string?)e.Attribute("open") ?? "(";
                        if (k.Count == 1 && Name(k[0]) == "mtable" && !IsSystem(k[0]))
                        {
                            var m = Matrix(k[0]);
                            Operand(open == "|" ? $"determinant({m})" : m);
                        }
                        else
                        {
                            var inner = string.Join(",", k.Select(Lin));
                            Operand(open == "|" ? $"abs({inner})" : $"({inner})");
                        }
                        break;
                    }
                case "mtable":
                    Operand(Matrix(e));
                    break;
                case "mover":
                case "munder":
                case "munderover":
                    Operand(Lin(Kids(e)[0]));
                    break;
                case "mtext":
                case "mspace":
                    break;
                default:
                    Operand(Lin(e));
                    break;
            }
        }
        if (inAbs) sb.Append(')');
        return sb.ToString();
    }

    static string LinFunction(string f) => f switch { "ln" => "log", "log" => "log10", "arcsin" => "asin", "arccos" => "acos", "arctan" => "atan", "det" => "determinant", _ => f };

    /// <summary>A function name starting at i: one symbol ("sin"), or spelled out over consecutive single letters.</summary>
    static bool FunctionAt(List<XElement> items, int i, out string fn, out int letters)
    {
        fn = Text(items[i]);
        letters = 1;
        if (Functions.Contains(fn)) return true;
        var spelled = new StringBuilder();
        for (int j = i; j < items.Count && j < i + 6 && Name(items[j]) == "mi" && Text(items[j]).Length == 1; j++)
        {
            spelled.Append(Text(items[j]));
            if (Functions.Contains(spelled.ToString()) && spelled.Length > 1)
            {
                fn = spelled.ToString();
                letters = j - i + 1;
                return true;
            }
        }
        return false;
    }

    /// <summary>A function's argument after position i: a bracketed group, or the next symbol (with its power).</summary>
    static string Argument(List<XElement> items, ref int i)
    {
        int j = i + 1;
        while (j < items.Count && Name(items[j]) == "mo" && Text(items[j]) is "⁡" or "") j++;
        if (j >= items.Count) { i = j - 1; return "x"; }
        if (Name(items[j]) == "mo" && Text(items[j]) is "(" or "[")
        {
            int depth = 0, k = j;
            for (; k < items.Count; k++)
            {
                if (Name(items[k]) == "mo" && Text(items[k]) is "(" or "[") depth++;
                if (Name(items[k]) == "mo" && Text(items[k]) is ")" or "]" && --depth == 0) break;
            }
            i = Math.Min(k, items.Count - 1);
            return Row(items.Skip(j + 1).Take(k - j - 1).ToList());
        }
        // sin 2x: a number and the symbols right after it go together.
        int end = j + 1;
        if (Name(items[j]) == "mn") while (end < items.Count && Name(items[end]) is "mi" && !Functions.Contains(Text(items[end]))) end++;
        i = end - 1;
        return Row(items.Skip(j).Take(end - j).ToList());
    }

    /// <summary>The rest of the current term from `start`: up to a top-level + − or relation.</summary>
    static string Term(List<XElement> items, int start, out int end)
    {
        int depth = 0;
        end = start;
        for (; end < items.Count; end++)
        {
            var t = Name(items[end]) == "mo" ? Text(items[end]) : "";
            if (t is "(" or "[") depth++;
            else if (t is ")" or "]") depth--;
            else if (depth == 0 && end > start && t is "+" or "-" or "−" or "=" or "<" or ">" or "≤" or "≥") break;
        }
        return Row(items.Skip(start).Take(end - start).ToList());
    }

    /// <summary>The "dx" closing an integral: index of the d (or of a single "dx" symbol), and the variable.</summary>
    static int FindDifferential(List<XElement> items, int from, out string variable)
    {
        variable = "x";
        for (int j = items.Count - 1; j >= from; j--)
        {
            var t = Text(items[j]);
            if (Name(items[j]) == "mi" && t.Length == 2 && t[0] == 'd') { variable = t[1..]; return j; }
            if (Name(items[j]) == "mi" && t == "d" && j + 1 < items.Count && Name(items[j + 1]) == "mi") { variable = Text(items[j + 1]); return j; }
        }
        return -1;
    }

    static bool IsDdx(XElement frac, out string variable)
    {
        variable = "x";
        var num = Flatten(Kids(frac)[0]);
        var den = Flatten(Kids(frac)[1]);
        if (num.Count != 1 || Text(num[0]) != "d") return false;
        if (den.Count == 2 && Text(den[0]) == "d") { variable = Text(den[1]); return true; }
        if (den.Count == 1 && Text(den[0]).Length == 2 && Text(den[0])[0] == 'd') { variable = Text(den[0])[1..]; return true; }
        return false;
    }

    static string Wrap(string s, XElement e) =>
        Name(e) is "mi" or "mn" || Regex.IsMatch(s, @"^[A-Za-z0-9.]+$") || (s.StartsWith('(') && s.EndsWith(')')) ? s : $"({s})";
}
