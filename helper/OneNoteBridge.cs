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

    /// <summary>
    /// `background`: a sync the user didn't ask for just now. If OneNote can't be reached, it's tried again
    /// later without a word (`retry`), rather than shown as an error.
    /// </summary>
    static void RunOneNote(Action work, string? noteKey, bool background = false, Action? unreachable = null, bool retryNow = true)
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
            try
            {
                try { work(); }
                catch (Exception e) when (retryNow && Unreachable(e))
                {
                    // Often OneNote is just starting (or still closing): one more try in a moment. (Not for a
                    // send, which may have got partway: the plugin sends it again later, from the start.)
                    Thread.Sleep(3000);
                    work();
                }
            }
            catch (Exception e) when (Unreachable(e))
            {
                if (unreachable != null) unreachable();
                else Protocol.Log($"OneNote isn't reachable ({e.GetType().Name} {e.HResult:X8}){(background ? "; will try again later" : "")}");
                if (noteKey != null)
                    Protocol.Send(new { type = "onenote-result", ok = false, retry = true, quiet = background, noteKey,
                        error = "OneNote isn't responding right now (it may be starting, updating or closed). It'll be tried again." });
            }
            catch (Exception e)
            {
                Protocol.Log("OneNote failed: " + e);
                Protocol.Send(new { type = "onenote-result", ok = false, error = e.Message, noteKey });
            }
        });
    }

    /// <summary>
    /// OneNote couldn't be started or talked to (it's starting, closing, updating, or the machine is waking):
    /// not a failure of what was asked, just not now. Seen: CO_E_SERVER_EXEC_FAILURE when starting it, and
    /// "RPC server is unavailable" (as an InvalidCastException) when it quit mid-call.
    /// </summary>
    static bool Unreachable(Exception e) =>
        e is InvalidCastException
        || (e is System.Runtime.InteropServices.COMException && (uint)e.HResult is 0x80080005 or 0x800706BA or 0x800706BE or 0x80010108 or 0x80010001 or 0x8001010A or 0x80010012);

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
        }, req.NoteKey, background: !req.Navigate, retryNow: false);
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
        if (req == null || req.Pages.Count == 0 || DateTime.UtcNow < pollPausedUntil) return;
        RunOneNote(() =>
        {
            var changed = OneNoteSync.ChangedPages(OneNote.Connect(), req.Pages.Select(p => (p.NoteKey, p.PageId, p.LastModified)));
            if (pollFailures > 0) Protocol.Log("OneNote reachable again; background checks back on");
            pollFailures = 0;
            Protocol.Send(new { type = "onenote-poll-result", changed });
        }, null, background: true, unreachable: () =>
        {
            // Not there (closed, updating, the machine just woke): check less often, up to every 10 minutes.
            pollFailures++;
            var pause = TimeSpan.FromMinutes(Math.Min(10, Math.Pow(2, pollFailures - 1)));
            pollPausedUntil = DateTime.UtcNow + pause;
            Protocol.Log($"OneNote isn't reachable; next check for OneNote edits in {pause.TotalMinutes:0} min");
        });
    }

    static int pollFailures;
    static DateTime pollPausedUntil;

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
    internal void ReplaceInk(string? path, StrokeCollection pulled)
    {
        if (path == null) return;
        // A page that was half-rewritten when OneNote failed can hold the same strokes twice.
        pulled = WithoutDuplicates(pulled);
        int local = path == inkPath ? ink.Strokes.Count : StrokesIn(path);
        if (pulled.Count == 0 && local > 0)
        {
            // A page with no ink at all is far more often a page OneNote didn't finish writing than one where
            // every stroke was erased: never wipe a note's ink from it.
            Protocol.Log($"OneNote page has no ink: kept the {local} strokes of {Path.GetFileName(path)}");
            Protocol.Send(new { type = "notice", message = "The OneNote page has no ink, so the note's ink was kept. Erase it here if that's what you meant." });
            return;
        }
        BackUpInk(path);
        if (path != inkPath)
        {
            // Not the open note: just write its file (anchored to its lines when it's next opened).
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp)) pulled.Save(fs, true);
            File.Move(tmp, path, overwrite: true);
            return;
        }
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

    static int StrokesIn(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            using var fs = OverlayWindow.OpenInk(path);
            return new StrokeCollection(fs).Count;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>
    /// The ink as it was before a pull replaced it, in the helper's folder (backups\, the last 20): a pull is
    /// the one thing that rewrites a note's ink wholesale, so it can always be undone by hand.
    /// </summary>
    void BackUpInk(string path)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "backups");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(path)}");
            if (path == inkPath)
            {
                var copy = ink.Strokes.Clone();
                ToTrueColors(copy);
                using var fs = File.Create(target);
                copy.Save(fs, true);
            }
            else if (File.Exists(path)) File.Copy(path, target, true);
            // Newest first by name (it starts with the time; a copy keeps the original's file times).
            foreach (var old in new DirectoryInfo(dir).GetFiles("*.ink").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(20)) old.Delete();
        }
        catch (Exception e)
        {
            Protocol.Log($"could not back up {path} before a pull: {e.Message}");
        }
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
