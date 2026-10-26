using System.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Ink;

namespace InkOverlay;

public sealed partial class OverlayWindow
{
    // All OneNote work runs on one background thread, one job at a time: background sync can ask for
    // sends, pulls and change checks close together, and OneNote is happiest with one caller.
    static readonly BlockingCollection<Action> oneNoteJobs = new();
    static Thread? oneNoteThread;

    static void RunOneNote(Action work, string? noteKey)
    {
        if (oneNoteThread == null)
        {
            oneNoteThread = new Thread(() =>
            {
                foreach (var job in oneNoteJobs.GetConsumingEnumerable()) job();
            })
            { IsBackground = true, Name = "onenote" };
            oneNoteThread.SetApartmentState(ApartmentState.STA);
            oneNoteThread.Start();
        }
        oneNoteJobs.Add(() =>
        {
            try { work(); }
            catch (Exception e)
            {
                Protocol.Log("OneNote failed: " + e);
                Protocol.Send(new { type = "onenote-result", ok = false, error = e.Message, noteKey });
            }
        });
    }

    /// <summary>
    /// Send a note to OneNote: save the ink (so the file has every stroke, in true colours), build the
    /// page, and open it unless it's a background sync.
    /// </summary>
    void SendToOneNote(ExportRequest? req)
    {
        if (req == null) return;
        // The file must have every stroke for the export. This save isn't an edit to send (it's what's being
        // sent): announcing it as one made every send schedule the next, forever.
        if (req.InkPath == inkPath) { edited = false; dirty = true; FlushSave(); }
        RunOneNote(() =>
        {
            try
            {
                var state = OneNoteExport.Export(req);
                if (req.Navigate) OneNoteExport.Open(state.PageId);
                Protocol.Log($"OneNote: sent \"{req.Title}\" ({req.Blocks.Count} blocks){(req.Navigate ? "" : " in the background")}");
                Protocol.Send(new { type = "onenote-result", ok = true, pageId = state.PageId, state, noteKey = req.NoteKey, navigated = req.Navigate });
            }
            catch (PageChangedException e)
            {
                Protocol.Send(new
                {
                    type = "onenote-result", ok = false, pageChanged = true, noteKey = req.NoteKey, navigate = req.Navigate,
                    textChanges = e.Diff.Changes.Count, inkChanged = e.Diff.InkChanged,
                });
            }
        }, req.NoteKey);
    }

    /// <summary>Just show the page: background sync keeps it current, so "Open in OneNote" is instant.</summary>
    void OpenInOneNote(string? pageId)
    {
        if (string.IsNullOrEmpty(pageId)) return;
        RunOneNote(() => OneNoteExport.Open(pageId), null);
    }

    /// <summary>
    /// Bring OneNote edits back. Ink changes are applied here straight away (one undo step if the note is
    /// open); text changes go to the plugin, with the state as it will be once they're in the note.
    /// </summary>
    void PullFromOneNote(PullRequest? req)
    {
        if (req == null) return;
        if (req.InkPath == inkPath) { dirty = true; FlushSave(); }
        RunOneNote(() =>
        {
            var diff = OneNoteSync.Diff(OneNote.Connect(), req.State);
            if (diff.InkChanged) Dispatcher.Invoke(() => ReplaceInk(req.InkPath, diff.Ink));
            Protocol.Log($"OneNote: pulled \"{req.NoteKey}\": {diff.Changes.Count} text changes, ink {(diff.InkChanged ? $"changed ({diff.Ink.Count} strokes)" : "unchanged")}");
            Protocol.Send(new
            {
                type = "onenote-pulled", noteKey = req.NoteKey,
                inkChanged = diff.InkChanged, strokes = diff.Ink.Count, inkWords = diff.InkWords,
                changes = diff.Changes, state = diff.After(req.State),
            });
        }, req.NoteKey);
    }

    /// <summary>Background sync: which synced notes' pages were edited in OneNote since we last looked.</summary>
    void PollOneNote(PollRequest? req)
    {
        if (req == null || req.Pages.Count == 0) return;
        RunOneNote(() =>
        {
            var changed = OneNoteSync.ChangedPages(OneNote.Connect(), req.Pages.Select(p => (p.NoteKey, p.PageId, p.LastModified)));
            Protocol.Send(new { type = "onenote-poll-result", changed });
        }, null);
    }

    /// <summary>The strokes with exact copies (same points, colour and width) dropped.</summary>
    internal static StrokeCollection WithoutDuplicates(StrokeCollection strokes)
    {
        var seen = new HashSet<string>();
        var kept = new StrokeCollection();
        foreach (var s in strokes)
        {
            var key = $"{s.DrawingAttributes.Color}|{s.DrawingAttributes.Width:0.##}|" + string.Join(";", s.StylusPoints.Select(p => $"{p.X:0.0},{p.Y:0.0}"));
            if (seen.Add(key)) kept.Add(s);
        }
        if (kept.Count < strokes.Count) Protocol.Log($"pulled ink: dropped {strokes.Count - kept.Count} duplicate strokes");
        return kept;
    }

    /// <summary>Replace a note's ink with the ink pulled from OneNote.</summary>
    void ReplaceInk(string? path, StrokeCollection pulled)
    {
        if (path == null) return;
        if (path != inkPath)
        {
            // Not the open note: just write its file (anchored to its lines when it's next opened).
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp)) pulled.Save(fs, true);
            File.Move(tmp, path, overwrite: true);
            return;
        }
        // A page that was half-rewritten when OneNote failed can hold the same strokes twice.
        pulled = WithoutDuplicates(pulled);
        ToDisplay(pulled);
        Pens.Stylize(pulled);
        importing = true;
        try
        {
            ink.Select(new StrokeCollection());
            ink.Strokes.Clear();
            ink.Strokes.Add(pulled);
        }
        finally { importing = false; }
        // OneNote doesn't keep our line anchors: re-anchor the pulled ink, strokes that touch together.
        AnchorLegacy(pulled.ToList());
        edited = false; // this came from OneNote; nothing to send back
        FlushSave();
        SendExtent();
    }
}

public sealed class PollPage
{
    public string NoteKey { get; set; } = "";
    public string PageId { get; set; } = "";
    public string LastModified { get; set; } = "";
}

public sealed class PollRequest
{
    public System.Collections.Generic.List<PollPage> Pages { get; set; } = new();
}
