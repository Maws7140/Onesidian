using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Ink;

namespace InkOverlay;

/// <summary>
/// Ink to Math: lasso a handwritten equation and Windows' math recognizer reads it (offline, nothing shown);
/// the plugin's Math pane takes it from there (solve, graph, steps...). "Fix it" opens the recognizer's own
/// correction panel next to the ink, like OneNote's, and sends the corrected equation back.
/// </summary>
public sealed partial class OverlayWindow
{
    MathInput? mathInput;
    readonly Dictionary<int, (int line, int lineEnd)> mathLines = new();

    MathInput MathRecognizer()
    {
        if (mathInput == null)
        {
            mathInput = new MathInput(obsidian);
            Protocol.Log("math recognizer ready");
        }
        return mathInput;
    }

    async void InkToMath()
    {
        var selected = ink.GetSelectedStrokes();
        if (selected.Count == 0) return;
        var strokes = new StrokeCollection(selected);
        string? mathml;
        try
        {
            mathml = await MathRecognizer().RecognizeAsync(strokes);
        }
        catch (Exception e)
        {
            Protocol.Log("math recognition failed: " + e);
            Notice("Ink to Math isn't available: Windows' math recognizer didn't start");
            return;
        }
        if (string.IsNullOrWhiteSpace(mathml)) { Notice("No math recognized in the selection"); return; }
        int id = nextConversion++;
        conversions[id] = strokes;
        var b = strokes.GetBounds();
        // The lines it covers: answers go under the last one.
        mathLines[id] = (SourceLine(new Rect(b.Left, b.Top, b.Width, Math.Min(b.Height, 1))), SourceLine(new Rect(b.Left, b.Bottom - 1, b.Width, 1)));
        SendMath(id, mathml, false);
    }

    async void FixMath(int id)
    {
        if (!conversions.TryGetValue(id, out var strokes) || !strokes.Any(ink.Strokes.Contains))
        {
            Notice("That handwriting isn't on the page any more");
            return;
        }
        try
        {
            var mathml = await EditInPanel(strokes, PanelNear(strokes.GetBounds()), "Fix the equation: circle or rewrite what's wrong, then Insert");
            if (!string.IsNullOrWhiteSpace(mathml)) SendMath(id, mathml, true);
        }
        catch (Exception e)
        {
            Protocol.Log("math fix failed: " + e);
            Notice("Couldn't open the math correction panel");
        }
    }

    System.Diagnostics.Process? mathPanel;

    /// <summary>The correction panel, in its own process (see MathInput.RunPanel); null if it was closed.</summary>
    async System.Threading.Tasks.Task<string?> EditInPanel(StrokeCollection strokes, Int32Rect at, string caption)
    {
        // One at a time: a second Fix it replaces the first.
        try { mathPanel?.StandardInput.Close(); } catch (Exception) { }
        var isf = new System.IO.MemoryStream();
        strokes.Save(isf, true);
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("--math-panel");
        using var proc = System.Diagnostics.Process.Start(psi)!;
        mathPanel = proc;
        try
        {
            await proc.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new MathInput.PanelRequest
            {
                Isf = Convert.ToBase64String(isf.ToArray()), X = at.X, Y = at.Y, W = at.Width, H = at.Height, Caption = caption,
            }));
            await proc.StandardInput.FlushAsync();
            Protocol.Log("math: correction panel open");
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                // Its log messages pass through; the answer ends it.
                if (!doc.RootElement.TryGetProperty("mathml", out var m)) { Protocol.SendRaw(line); continue; }
                Protocol.Log("math: correction panel " + (m.ValueKind == System.Text.Json.JsonValueKind.String ? "inserted" : "closed"));
                return m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() : null;
            }
            Protocol.Log("math: correction panel ended without an answer");
            return null;
        }
        finally
        {
            if (mathPanel == proc) mathPanel = null;
            if (!proc.WaitForExit(2000)) try { proc.Kill(); } catch (Exception) { }
        }
    }

    void SendMath(int id, string mathml, bool corrected)
    {
        MathML.Converted converted;
        try
        {
            converted = MathML.Convert(mathml);
        }
        catch (Exception e)
        {
            Protocol.Log($"math conversion failed: {e.Message}\n{mathml}");
            Notice("Couldn't read the recognized math");
            return;
        }
        Protocol.Log($"ink to math: {converted.Linear}");
        Protocol.Send(new
        {
            type = "math",
            id,
            inkPath,
            line = mathLines.TryGetValue(id, out var lines) ? lines.line : -1,
            lineEnd = mathLines.TryGetValue(id, out var range) ? range.lineEnd : -1,
            mathml,
            latex = converted.Latex,
            linear = converted.Linear,
            corrected,
        });
    }

    /// <summary>Where the correction panel goes: just under the ink (above it if there's no room), in screen px.</summary>
    Int32Rect PanelNear(Rect inkBounds)
    {
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)(720 * dpi), h = (int)(420 * dpi);
        Point topLeft, bottomLeft;
        try
        {
            topLeft = ink.PointToScreen(inkBounds.TopLeft);
            bottomLeft = ink.PointToScreen(inkBounds.BottomLeft);
        }
        catch (InvalidOperationException)
        {
            return new Int32Rect(placed.x + 40, placed.y + 40, w, h);
        }
        var screen = SystemParameters.WorkArea;
        double screenBottom = screen.Bottom * dpi;
        int x = (int)Math.Clamp(topLeft.X, screen.Left * dpi, Math.Max(screen.Left * dpi, screen.Right * dpi - w));
        int y = bottomLeft.Y + 12 + h <= screenBottom ? (int)(bottomLeft.Y + 12) : (int)Math.Max(screen.Top * dpi, topLeft.Y - 12 - h);
        return new Int32Rect(x, y, w, h);
    }

}
