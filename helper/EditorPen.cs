using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;

namespace InkOverlay;

/// <summary>
/// OneNote's Ink Editor pen, on the note's typed Markdown: strike through words or scratch them out to
/// delete them, circle words to select them, draw a caret (^) to insert there (then write what to insert).
/// Anything that isn't one of those gestures is handwriting, which becomes text like the Ink to Text pen.
/// The helper recognizes the gesture; the plugin finds the words under it and edits the note.
/// </summary>
public sealed partial class OverlayWindow
{
    bool editorPen;

    static readonly ApplicationGesture[] EditorGestures =
    {
        ApplicationGesture.ScratchOut, ApplicationGesture.Circle, ApplicationGesture.ChevronUp,
        ApplicationGesture.Right, ApplicationGesture.Left,
    };

    /// <summary>Which gestures the canvas listens for: the editor pen's set, or just scratch-out.</summary>
    void UseGestures(bool editor)
    {
        if (!gesturesAvailable) return;
        try { ink.SetEnabledGestures(editor ? EditorGestures : new[] { ApplicationGesture.ScratchOut }); }
        catch (Exception e) { Protocol.Log("gestures: " + e.Message); }
    }

    /// <summary>A gesture with the editor pen. Returns false to keep the stroke as handwriting.</summary>
    bool EditorGesture(InkCanvasGestureEventArgs e)
    {
        var top = e.GetGestureRecognitionResults().FirstOrDefault();
        if (top == null || top.RecognitionConfidence == RecognitionConfidence.Poor || !EditorGestures.Contains(top.ApplicationGesture)) return false;
        var strokes = e.Strokes;
        var b = strokes.GetBounds();
        // Lines must be fairly flat to be a strike-through (not a dash or a slanted stroke of a letter).
        if (top.ApplicationGesture is ApplicationGesture.Right or ApplicationGesture.Left && (b.Width < 24 / Zoom || b.Height > b.Width * 0.35)) return false;
        if (top.ApplicationGesture == ApplicationGesture.ScratchOut)
        {
            // Over ink, it erases ink (as with every pen); over text, it deletes the text.
            var hits = ink.Strokes.HitTest(b, 60);
            if (hits.Count > 0) { ink.Strokes.Remove(hits); return true; }
        }
        var pts = strokes.SelectMany(s => s.StylusPoints).ToList();
        var tip = pts.OrderBy(p => p.Y).First();
        string kind = top.ApplicationGesture switch
        {
            ApplicationGesture.ScratchOut => "scratch",
            ApplicationGesture.Circle => "circle",
            ApplicationGesture.ChevronUp => "caret",
            _ => "strike",
        };
        Protocol.Send(new
        {
            type = "edit-gesture",
            kind,
            inkPath,
            x = b.X, y = b.Y, w = b.Width, h = b.Height,
            tipX = tip.X, tipY = tip.Y,
        });
        Protocol.Log($"ink editor: {kind}");
        return true;
    }
}
