using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace InkOverlay;

/// <summary>
/// Message from the Obsidian plugin. One JSON object per line on stdin.
/// All geometry is in CSS pixels relative to Obsidian's viewport (= its client area).
/// </summary>
public sealed class Command
{
    public string Type { get; set; } = "";
    public bool Visible { get; set; }
    public string? Title { get; set; }
    public int Ppid { get; set; }
    // Overlay rect (the note's content area).
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    // Page origin (the text column's top-left) relative to the overlay rect. Moves with scrolling.
    public double Ox { get; set; }
    public double Oy { get; set; }
    public double Dpr { get; set; } = 1;
    // CSS px per ink unit: 1 for notes; for PDFs (ink in PDF points) and images (ink in image pixels) the zoom.
    public double Zoom { get; set; } = 1;
    public string? InkPath { get; set; }
    public bool Dark { get; set; }
    // Obsidian UI over the note (menus, popovers, other plugins' floating toolbars), CSS px in the viewport:
    // the ink layer leaves these see-through, so they show and take the pen instead of the ink.
    public Hole[]? Holes { get; set; }
    // The plugin draws the ink inside the note while the pen is away (see InkDom.cs).
    public bool DomInk { get; set; }

    // "tool" command: the active tool and pen behaviour (sizes in CSS px).
    public string? Tool { get; set; }        // pen | highlighter | eraser-stroke | eraser-point | lasso
    public string? Color { get; set; }       // #RRGGBB
    public double Size { get; set; } = 2;
    public double EraserSize { get; set; } = 12;
    public string? EraserEnd { get; set; }   // stroke | point
    public bool Pressure { get; set; } = true;
    public bool Smooth { get; set; } = true;
    public bool ScratchOut { get; set; } = true;
    public bool Shapes { get; set; } = true;     // draw-and-hold ink-to-shape

    // "lines": where each source line sits (CSS px from the text column top), in the given mode.
    public string? Mode { get; set; }            // source | preview
    public double[]? Tops { get; set; }
    // Lines whose positions are measured (on/near screen) rather than estimated, 0-based inclusive.
    public int? MeasuredFrom { get; set; }
    public int? MeasuredTo { get; set; }
    // Per line: a fingerprint of its text (0 = blank/frontmatter), so ink can find its text again.
    public int[]? Hashes { get; set; }
    // "debug": diagnostics from the plugin, written to the log.
    public string? Message { get; set; }
    // "onenote": send a note to OneNote and open it.
    public ExportRequest? Export { get; set; }
    // "onenote-pull": bring OneNote edits back.
    public PullRequest? Pull { get; set; }
    // "onenote-poll": background sync's check for OneNote edits; "onenote-open": just show a page.
    public PollRequest? Poll { get; set; }
    public string? PageId { get; set; }

    // Handwriting and selection actions (see Writing.cs).
    public int Id { get; set; }                  // "consume": the conversion whose ink to remove
    public string? Purpose { get; set; }         // "ink-to-text": insert | copy
    public double Angle { get; set; }            // "rotate": degrees clockwise
    public double Factor { get; set; } = 1;      // "pen-scale": thickness multiplier
    public IndexFile[]? Files { get; set; }      // "index": ink files to read for handwriting search
    public string? Effect { get; set; }          // "tool": effect pen (rainbow, galaxy...), with Tool "pen"
    public int Line { get; set; }                // "space-applied": where lines went in (or came out)...
    public int Count { get; set; }               // ...and how many (negative: removed)
    public string? From { get; set; }            // "rename-ink": the ink file's old path...
    public string? To { get; set; }              // ...and new one (null: its file was deleted)
}

public sealed class Hole
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}

public sealed class IndexFile
{
    public string? Path { get; set; }
}

public static class Protocol
{
    static readonly object Gate = new();
    static readonly Stream Out = Console.OpenStandardOutput();
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Self-tests: see every message sent to the plugin.</summary>
    internal static Action<string>? Tap;

    public static void Send(object message)
    {
        var json = JsonSerializer.Serialize(message, Json);
        Tap?.Invoke(json);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        lock (Gate)
        {
            try { Out.Write(bytes); Out.Flush(); } catch (IOException) { }
        }
    }

    // Persistent log, since the plugin's console isn't visible: helper.log next to the exe (the plugin's
    // helper folder). Not under AppData, which packaged apps see redirected to their own private copy.
    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "helper.log");

    public static void StartLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                File.Move(LogPath, LogPath + ".1", overwrite: true);
        }
        catch (IOException) { }
    }

    /// <summary>A message already in JSON (passed on from a helper process).</summary>
    public static void SendRaw(string json)
    {
        Tap?.Invoke(json);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        lock (Gate)
        {
            try { Out.Write(bytes); Out.Flush(); } catch (IOException) { }
        }
    }

    public static void Log(string text)
    {
        Send(new { type = "log", message = text });
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {text}\n"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static Command? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<Command>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException e)
        {
            Log("bad command: " + e.Message);
            return null;
        }
    }
}

public static partial class Program
{
    /// <summary>
    /// Developer self-tests and probes (`--selftest-tools`...): helper/Tests, kept out of the repository. Without
    /// that folder this is compiled away and the helper is just the helper.
    /// </summary>
    static partial void DevCommand(string[] args, ref int? exit);

    /// <summary>Exit code asking the plugin to start a fresh helper (e.g. pen input lost after sleep).</summary>
    public const int RestartExitCode = 75;

    static OverlayWindow? overlay;

    // Startup timing, written to the log once: the helper is relaunched on wake, so startup speed matters.
    static readonly System.Diagnostics.Stopwatch startClock = System.Diagnostics.Stopwatch.StartNew();
    static readonly List<string> marks = new();
    internal static void Mark(string phase) => marks.Add($"{phase} {startClock.ElapsedMilliseconds}ms");

    static bool replacementRequested;

    /// <summary>
    /// Get a fresh pen/input stack (after sleep, or when the pen stopped reaching us) without the ink ever
    /// disappearing: ask the plugin to start a replacement, and keep showing the ink until it's up and the
    /// plugin closes our stdin (then we save and exit as usual).
    /// </summary>
    public static void RequestReplacement(string reason)
    {
        if (replacementRequested) return;
        replacementRequested = true;
        Protocol.Log("asking to be replaced: " + reason);
        overlay?.FlushSave();
        Protocol.Send(new { type = "replace-me", reason });
    }

    /// <summary>Save, then exit so the plugin relaunches us (for crashes: this process can't be trusted).</summary>
    public static void Restart(string reason)
    {
        Protocol.Log("restarting: " + reason);
        Protocol.Send(new { type = "restarting", reason });
        overlay?.FlushSave();
        Application.Current.Shutdown(RestartExitCode);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Copies of this program the helper starts itself: the Fix it math panel, the handwriting reader.
        if (args.Length > 0 && args[0] == "--math-panel") return MathInput.RunPanel();
        if (args.Length > 1 && args[0] == "--index-files") return OverlayWindow.IndexFiles(args.Skip(1));
        int? devExit = null;
        DevCommand(args, ref devExit);
        if (devExit is int code) return code;

        Mark("process");
        // Run at full speed in the background: Windows' efficiency mode throttles windowless background
        // processes, which kept the ink from coming back for half a minute after the screen woke.
        Native.DisableThrottling();
        // Software rendering: a transparent overlay is copied back from the GPU every frame anyway, and the
        // GPU renderer costs ~100 MB on its own. "--gpu" (a plugin setting) turns it back on.
        if (!args.Contains("--gpu")) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Protocol.StartLog();
        Protocol.Log("helper started");
        Mark("log");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Mark("WPF");
        app.DispatcherUnhandledException += (_, e) =>
        {
            Protocol.Log("crash: " + e.Exception);
            e.Handled = true;
            Restart("unhandled exception");
        };
        overlay = new OverlayWindow();
        var window = overlay;

        // The plugin owns our lifetime: when Obsidian closes our stdin, we save and exit.
        var reader = new Thread(() =>
        {
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            string? line;
            while ((line = input.ReadLine()) != null)
            {
                var cmd = Protocol.Parse(line);
                if (cmd != null) app.Dispatcher.BeginInvoke(() => window.Handle(cmd));
            }
            app.Dispatcher.BeginInvoke(() =>
            {
                Protocol.Log("stdin closed; exiting");
                window.FlushSave();
                app.Shutdown();
            });
        })
        { IsBackground = true, Name = "stdin" };
        reader.Start();

        Mark("ready");
        Protocol.Log("startup: " + string.Join(", ", marks));
        Protocol.Send(new { type = "ready" });
        return app.Run();
    }
}
