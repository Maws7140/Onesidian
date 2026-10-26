import { App, FileSystemAdapter, FileView, MarkdownRenderChild, MarkdownView, Notice, Plugin, PluginSettingTab, Setting, TFile, debounce, loadMathJax } from "obsidian";
import { spawn, ChildProcessWithoutNullStreams } from "child_process";
import * as path from "path";
import { DEFAULT_SETTINGS, DEFAULT_SLOTS, ERASER_SIZES, InkSettings, PAGE_LINES, isDarkTheme, mmToPx } from "./settings";
import { Toolbar, ToolbarHost } from "./toolbar";
import { clearBlankSpace, describeLayout, lineHashes, lineTops } from "./lines";
import { OverwriteModal, PullModal, SyncState, TextChange, applyTextChanges, noteBlocks, textColumn, withoutEchoes } from "./onenote";
import { InkKind, imageLayout, inkFileFor, inkKindOf, pdfLayout } from "./targets";
import { HandwritingIndex, HandwritingSearchModal, RecognizedLine, documentFor, insertRecognized, replaceLines } from "./writing";
import { MATH_VIEW, MathHost, MathSource, MathView, readTyped } from "./math/pane";
import { MathSettings } from "./math/engine";
import { GraphView, parseGraph } from "./math/graph";
import { EditorView, ViewUpdate } from "@codemirror/view";
import { EditGesture, applyEditGesture } from "./editgesture";
import { DomInk, DomUpdate } from "./domink";
import { lineShifts } from "./lineshift";

/**
 * Drives a native Windows Ink overlay (helper/InkOverlay.exe) that sits over the active note.
 * Every animation frame we measure the note's content area and its text column, and send changes
 * to the helper. The helper keeps the ink glued to the text column as you scroll.
 */
export default class OneNoteInkPlugin extends Plugin implements ToolbarHost, MathHost {
  settings: InkSettings = structuredClone(DEFAULT_SETTINGS);
  scratchOutAvailable = true;
  private helper: ChildProcessWithoutNullStreams | null = null;
  private toolbar!: Toolbar;
  private frame = 0;
  private lastSent = "";
  private lastPen = 0;
  private enabled = true;
  private unloading = false;
  /** Recent helper restart times, to stop a crash loop. */
  private restarts: number[] = [];
  // Line positions last sent (see syncLines).
  private lastLines = "";
  private linesAt = 0;
  private linesDirty = true;
  private linesMode = "";
  private linesPath = "";
  /** Lowest ink point per note (CSS px from the top of the text column), reported by the helper. */
  private inkBottom = new Map<string, number>();
  private persist = debounce(() => this.saveData(this.settings), 500, true);
  /** Strokes selected with the lasso (the toolbar shows selection actions while there are some). */
  private selected = 0;
  private handwriting!: HandwritingIndex;
  /** The ink drawn in the note itself, so it scrolls with the text (see domink.ts). */
  private domInk = new DomInk();

  async onload() {
    await this.loadSettings();
    this.toolbar = new Toolbar(this);
    this.handwriting = new HandwritingIndex(this.app, `${this.manifest.dir}/handwriting-index.json`);
    this.startHelper();
    this.sendTool();

    this.addSettingTab(new InkSettingTab(this.app, this));
    // Switching light/dark changes how neutral pen colours are shown.
    this.registerEvent(this.app.workspace.on("css-change", () => this.toolbar.render()));
    this.addRibbonIcon("pen-tool", "Toggle ink layer", () => this.toggleLayer());
    this.addCommand({ id: "toggle-ink-layer", name: "Toggle ink layer", callback: () => this.toggleLayer() });
    this.addCommand({ id: "toggle-ink-toolbar", name: "Show/hide ink toolbar", callback: () => {
      this.settings.showToolbar = !this.settings.showToolbar;
      this.save();
    } });
    this.addCommand({ id: "undo-ink", name: "Undo ink", callback: () => this.command("undo") });
    this.addCommand({
      id: "restart-ink-helper",
      name: "Restart the ink helper (if the pen stops drawing)",
      callback: () => {
        if (this.helper) this.send({ type: "replace" });
        else this.startHelper();
      },
    });
    this.addCommand({ id: "redo-ink", name: "Redo ink", callback: () => this.command("redo") });
    this.addCommand({
      id: "open-in-onenote",
      name: "Open in OneNote",
      checkCallback: (checking) => {
        if (!this.activeNote()) return false;
        if (!checking) this.openInOneNote();
        return true;
      },
    });
    this.addCommand({
      id: "pull-from-onenote",
      name: "Pull changes from OneNote",
      checkCallback: (checking) => {
        const view = this.activeNote();
        if (!view?.file || !this.syncState(view.file.path)) return false;
        if (!checking) this.pullFromOneNote();
        return true;
      },
    });

    this.addCommand({
      id: "ink-to-text",
      name: "Convert selected ink to text",
      checkCallback: (checking) => this.selectionCommand(checking, () => this.inkToText()),
    });
    this.addCommand({
      id: "copy-ink-as-text",
      name: "Copy selected ink as text",
      checkCallback: (checking) => this.selectionCommand(checking, () => this.inkToText("copy")),
    });
    this.addCommand({
      id: "straighten-ink",
      name: "Straighten selected handwriting",
      checkCallback: (checking) => this.selectionCommand(checking, () => this.command("straighten")),
    });
    this.addCommand({
      id: "copy-ink",
      name: "Copy selected ink",
      checkCallback: (checking) => this.selectionCommand(checking, () => this.command("copy")),
    });
    this.addCommand({
      id: "paste-ink",
      name: "Paste ink",
      checkCallback: (checking) => {
        if (!this.inkKind()) return false;
        if (!checking) this.pasteInk();
        return true;
      },
    });
    this.addCommand({
      id: "ink-to-text-pen",
      name: "Ink to Text pen",
      checkCallback: (checking) => {
        if (this.inkKind() !== "markdown") return false;
        if (!checking) {
          this.settings.tool = "text-pen";
          this.save();
        }
        return true;
      },
    });
    // Math Assistant.
    this.registerView(MATH_VIEW, (leaf) => new MathView(leaf, this));
    this.registerMarkdownCodeBlockProcessor("math-graph", (source, el, ctx) => {
      const graph = new GraphView(el, parseGraph(source));
      const child = new MarkdownRenderChild(el);
      child.onunload = () => graph.destroy();
      ctx.addChild(child);
    });
    this.addCommand({
      id: "ink-to-math",
      name: "Ink to Math (selected handwriting)",
      checkCallback: (checking) => this.selectionCommand(checking, () => this.command("ink-to-math")),
    });
    this.addCommand({
      id: "math-assistant",
      name: "Math Assistant (selected math, or type an equation)",
      callback: () => this.openMathForSelection(),
    });
    this.addCommand({
      id: "search-handwriting",
      name: "Search handwriting",
      callback: () => new HandwritingSearchModal(this.app, this.handwriting).open(),
    });

    // Handwriting search: read ink files that changed since they were last read (the open note's ink is
    // re-read by the helper after each save), and follow ink files that are renamed or deleted.
    await this.handwriting.load();
    this.app.workspace.onLayoutReady(() => window.setTimeout(() => this.indexStaleInk(), 5000));
    this.registerEvent(this.app.vault.on("delete", (f) => {
      this.handwriting.remove(f.path);
      if (f instanceof TFile && f.extension !== "ink") this.inkFileGone(f);
    }));
    this.registerEvent(this.app.vault.on("rename", (f, old) => {
      this.handwriting.rename(old, f.path);
      if (f instanceof TFile && f.extension !== "ink") this.inkFileMoved(f, old);
    }));
    this.registerEvent(this.app.vault.on("modify", (f) => {
      if (f instanceof TFile && f.extension === "ink" && f.path !== this.loadedInkPath()) this.queueIndex([f]);
    }));

    // A pen hovering over the note arms the overlay so the stroke goes to Windows Ink.
    this.registerDomEvent(document, "pointermove", (e: PointerEvent) => this.onPointer(e), { capture: true });
    this.registerDomEvent(document, "pointerover", (e: PointerEvent) => this.onPointer(e), { capture: true });
    // A pen-down that Obsidian sees over the ink area went past the ink layer: say where (for the log).
    this.registerDomEvent(document, "pointerdown", (e: PointerEvent) => {
      const r = this.inkArea;
      if (e.pointerType !== "pen" || !r || e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) return;
      if (performance.now() - this.lastMissLog < 20_000) return;
      this.lastMissLog = performance.now();
      const el = e.target instanceof Element ? e.target : null;
      // Menus and other plugins' toolbars are see-through on purpose (see uiHoles): the pen belongs there.
      if (el?.closest(`${OVER_NOTE}, ${FLOATING}, .oni-toolbar`)) return;
      const what = el ? `${el.tagName.toLowerCase()}.${Array.from(el.classList).join(".")}` : "?";
      this.send({ type: "debug", message: `pen down reached Obsidian, not the ink layer: (${Math.round(e.clientX)},${Math.round(e.clientY)}) on ${what} in a ${this.inkKind()} view` });
    }, { capture: true });

    // Lines going in or out of a note: the ink under them moves with them (see onEditorUpdate).
    this.registerEditorExtension(EditorView.updateListener.of((u) => this.onEditorUpdate(u)));
    // Text changed: re-send line positions and fingerprints right away, so ink follows its text.
    this.registerEvent(this.app.workspace.on("editor-change", (_editor, info) => {
      this.linesDirty = true;
      if (info.file) this.lastEdit.set(info.file.path, Date.now());
      if (info.file) this.markDirty(info.file.path);
    }));
    // Background OneNote sync: send pending changes when a note opens; check OneNote for edits.
    this.registerEvent(this.app.workspace.on("file-open", () => this.onNoteOpened()));
    this.app.workspace.onLayoutReady(() => this.startOneNotePolling());
    // MathJax turns $math$ into MathML for OneNote equations (and the Math pane renders with it).
    this.app.workspace.onLayoutReady(() => void loadMathJax());

    const tick = () => {
      this.frame = requestAnimationFrame(tick);
      this.sync();
    };
    this.frame = requestAnimationFrame(tick);
  }

  onunload() {
    this.unloading = true;
    cancelAnimationFrame(this.frame);
    for (const leaf of this.app.workspace.getLeavesOfType("markdown")) {
      if (leaf.view instanceof MarkdownView) clearBlankSpace(leaf.view);
    }
    this.toolbar.destroy();
    this.domInk.destroy();
    for (const proc of [this.helper, this.standby]) {
      if (!proc) continue;
      // Closing stdin makes the helper save and exit; kill it if it doesn't.
      proc.stdin.end();
      setTimeout(() => { if (proc.exitCode === null) proc.kill(); }, 2000);
    }
    this.helper = null;
    this.standby = null;
  }

  async loadSettings() {
    const saved = (await this.loadData()) as Partial<InkSettings> | null;
    this.settings = { ...structuredClone(DEFAULT_SETTINGS), ...saved };
    if (!this.settings.slots?.length) this.settings.slots = DEFAULT_SLOTS.map((s) => ({ ...s }));
    // Rule lines were on/off before there were spacings and grids.
    if (this.settings.ruled && this.settings.pageLines === "none") { this.settings.pageLines = "text"; this.settings.ruled = false; }
    this.settings.active = Math.min(this.settings.active, this.settings.slots.length - 1);
  }

  // ---------- ToolbarHost ----------

  save(rerender = true) {
    this.persist();
    this.sendTool();
    if (rerender) this.toolbar?.render();
  }

  command(type: string, extra: object = {}) {
    this.send({ ...extra, type });
  }

  selectionCount() {
    return this.selected;
  }

  /** Selected ink to text: typed into the note where it was written (or copied, for PDFs and images). */
  inkToText(purpose: "insert" | "copy" = "insert") {
    this.send({ type: "ink-to-text", purpose: this.inkKind() === "markdown" ? purpose : "copy" });
  }

  pasteInk() {
    // Pasted ink arrives selected, ready to drag into place.
    if (this.settings.tool !== "lasso") {
      this.settings.tool = "lasso";
      this.save();
    }
    this.send({ type: "paste" });
  }

  private selectionCommand(checking: boolean, run: () => void) {
    if (!this.selected || !this.inkKind()) return false;
    if (!checking) run();
    return true;
  }

  toggleLayer() {
    this.enabled = !this.enabled;
    this.toolbar.render();
    new Notice(this.enabled ? "Ink layer on" : "Ink layer off");
  }

  layerOn() {
    return this.enabled;
  }

  // ---------- helper process ----------

  private sendTool() {
    const s = this.settings;
    const slot = s.slots[s.active] ?? s.slots[0];
    // The Ink to Text and Ink Editor pens and Insert space are for notes (their buttons only show there): on
    // a PDF or picture the current pen writes instead, or the pen did nothing there. The choice is kept for notes.
    const notesOnly = s.tool === "text-pen" || s.tool === "editor-pen" || s.tool === "space";
    const tool = s.tool === "slot" || (notesOnly && this.inkKind() !== null && this.inkKind() !== "markdown") ? "slot" : s.tool;
    this.send({
      type: "tool",
      tool: tool === "slot" ? slot.kind : tool,
      effect: tool === "slot" && slot.kind === "pen" ? slot.effect ?? null : null,
      color: slot.color,
      size: mmToPx(slot.size),
      eraserSize: mmToPx(s.eraserSize),
      eraserEnd: s.eraserEnd,
      pressure: s.pressure,
      smooth: s.smooth,
      scratchOut: s.scratchOut,
      shapes: s.shapes,
    });
    this.send({ type: "ruler", visible: s.ruler });
  }

  private vaultPath(): string | null {
    const adapter = this.app.vault.adapter;
    return adapter instanceof FileSystemAdapter ? adapter.getBasePath() : null;
  }

  /**
   * Start the ink helper. `asStandby` starts a replacement next to the running one (after sleep, or when
   * the pen stopped reaching it): it gets the same layout and loads the note, and takes over once it's on
   * screen, so the ink never disappears while it starts (see promoteStandby).
   */
  private startHelper(asStandby = false) {
    const base = this.vaultPath();
    if (!base || !this.manifest.dir) return;
    const exe = path.join(base, this.manifest.dir, "helper", "InkOverlay.exe");
    let proc: ChildProcessWithoutNullStreams;
    try {
      proc = spawn(exe, this.settings.gpuRendering ? ["--gpu"] : [], { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
    } catch (e) {
      new Notice("OneNote Ink: could not start helper");
      console.error("[onenote-ink]", e);
      return;
    }
    if (asStandby) this.standby = proc;
    else this.helper = proc;
    let buffered = "";
    proc.stdout.setEncoding("utf8");
    proc.stdout.on("data", (chunk: string) => {
      buffered += chunk;
      let nl: number;
      while ((nl = buffered.indexOf("\n")) >= 0) {
        const line = buffered.slice(0, nl).trim();
        buffered = buffered.slice(nl + 1);
        if (line) this.onHelperMessage(line, proc);
      }
    });
    proc.stderr.setEncoding("utf8");
    proc.stderr.on("data", (d: string) => console.error("[onenote-ink]", d));
    proc.on("error", (e) => {
      if (proc === this.standby) { this.standby = null; return; }
      if (proc !== this.helper) return;
      new Notice("OneNote Ink helper failed: " + e.message);
      this.helper = null;
    });
    proc.on("exit", (code) => {
      if (proc === this.standby) { this.standby = null; return; }
      if (proc !== this.helper) return;
      this.helper = null;
      if (this.unloading) return;
      // The helper exits on purpose (code 75) when its pen input dies, e.g. after the Surface sleeps,
      // and may crash; either way bring up a fresh one. Give up if it's stuck in a crash loop.
      const now = Date.now();
      this.restarts = this.restarts.filter((t) => now - t < 60_000);
      if (this.restarts.length >= 5) {
        new Notice("OneNote Ink: the ink helper keeps stopping. Details: .obsidian/plugins/onenote-ink/helper/helper.log");
        return;
      }
      this.restarts.push(now);
      console.log(`[onenote-ink] helper exited (${code}); restarting`);
      window.setTimeout(() => {
        if (this.unloading || this.helper) return;
        this.startHelper();
        this.lastSent = ""; // resend layout and line positions so the new helper loads the note
        this.lastLines = "";
        this.linesDirty = true;
        this.sendTool();
      }, code === 75 ? 100 : 1000);
    });
  }

  /** A replacement helper, starting up next to the running one. */
  private standby: ChildProcessWithoutNullStreams | null = null;

  /** The helper asked to be replaced: start the new one alongside and hand it the current state. */
  private startStandby() {
    if (this.standby || this.unloading) return;
    this.startHelper(true);
    if (!this.standby) return;
    // Layout, line positions and pen settings go to both (see send); resend them all now.
    this.lastSent = "";
    this.lastLines = "";
    this.linesDirty = true;
    this.sendTool();
    // No note on screen means it can't show itself: take over after a while anyway.
    const standby = this.standby;
    window.setTimeout(() => { if (this.standby === standby) this.promoteStandby(); }, 15_000);
  }

  /** The replacement is on screen with the note's ink: retire the old helper and let it save on its way out. */
  private promoteStandby() {
    const next = this.standby;
    if (!next) return;
    const old = this.helper;
    this.standby = null;
    this.helper = next;
    if (old) {
      // It saves anything pending when its stdin closes; then the new one re-reads the file.
      old.once("exit", () => this.send({ type: "reload" }));
      old.stdin.end();
      window.setTimeout(() => { if (old.exitCode === null) old.kill(); }, 3000);
    } else {
      this.send({ type: "reload" });
    }
    console.log("[onenote-ink] ink helper replaced without a gap");
  }

  private onHelperMessage(line: string, from?: ChildProcessWithoutNullStreams) {
    if (from && from === this.standby) {
      // A replacement only reports in; everything else waits until it takes over.
      if (line.includes('"type":"shown"')) this.promoteStandby();
      return;
    }
    if (from && from !== this.helper) return; // an old helper on its way out
    if (line.includes('"type":"replace-me"')) {
      this.startStandby();
      return;
    }
    let msg: {
      type?: string; canUndo?: boolean; canRedo?: boolean; scratchOut?: boolean; message?: string;
      path?: string; bottom?: number; ok?: boolean; pageId?: string; noteKey?: string; error?: string;
      state?: SyncState; navigated?: boolean; navigate?: boolean; pageChanged?: boolean; textChanges?: number; inkChanged?: boolean;
      changes?: TextChange[]; strokes?: number; inkWords?: number; changed?: string[];
      count?: number; id?: number; inkPath?: string; purpose?: string; lines?: RecognizedLine[];
      line?: number; lineEnd?: number; latex?: string; linear?: string;
      edited?: boolean; retry?: boolean; quiet?: boolean; visible?: boolean; kind?: string; x?: number; y?: number; w?: number; h?: number; tipX?: number; tipY?: number; playing?: boolean;
    };
    try {
      msg = JSON.parse(line);
    } catch {
      console.log("[onenote-ink]", line);
      return;
    }
    switch (msg.type) {
      case "history":
        this.toolbar.setHistory(!!msg.canUndo, !!msg.canRedo);
        break;
      case "capabilities":
        this.scratchOutAvailable = !!msg.scratchOut;
        break;
      case "extent":
        if (msg.path) this.inkBottom.set(msg.path, msg.bottom ?? 0);
        break;
      case "onenote-result":
        this.onSent(msg);
        break;
      case "onenote-pulled":
        if (msg.noteKey) this.onPulled(msg.noteKey, msg.changes ?? [], !!msg.inkChanged, msg.strokes ?? 0, msg.inkWords ?? 0, msg.state);
        break;
      case "onenote-poll-result":
        for (const noteKey of msg.changed ?? []) this.pullFromOneNote(noteKey, "auto");
        break;
      case "saved":
        // Only saves of new or changed ink are edits to sync; a save before a send, or of ink just moved to
        // follow its text, isn't (treating those as edits made every send trigger the next one).
        if (msg.path && msg.edited) this.onInkSaved(msg.path);
        break;
      case "selection":
        if ((msg.count ?? 0) !== this.selected) {
          // The toolbar only changes when there starts or stops being a selection.
          const had = this.selected > 0;
          this.selected = msg.count ?? 0;
          if (had !== this.selected > 0) this.toolbar.render();
        }
        break;
      case "ink-text":
        void this.onInkText(msg.id ?? 0, msg.inkPath ?? "", msg.purpose ?? "insert", msg.lines ?? []);
        break;
      case "ink-index":
        if (msg.path) this.onInkIndex(msg.path, msg.lines ?? []);
        break;
      case "notice":
        if (msg.message) new Notice(msg.message);
        break;
      case "ink-svg":
        this.domInk.update(msg as unknown as DomUpdate);
        break;
      case "ink-native":
        this.domInk.setNative(msg.path ?? "", !!msg.visible);
        break;
      case "insert-space":
        void this.onInsertSpace(msg.inkPath ?? "", msg.line ?? 0, msg.count ?? 0);
        break;
      case "edit-gesture":
        this.onEditGesture(msg as EditGesture);
        break;
      case "replay":
        this.toolbar.setReplay(!!msg.playing);
        break;
      case "math":
        if (msg.linear) void this.onInkMath(msg.id ?? 0, msg.inkPath ?? "", msg.line ?? -1, msg.lineEnd ?? msg.line ?? -1, msg.linear, msg.latex);
        break;
      default:
        console.log("[onenote-ink]", line);
    }
  }

  private send(msg: object) {
    const line = JSON.stringify(msg) + "\n";
    if (this.helper?.stdin.writable) this.helper.stdin.write(line);
    // A starting replacement needs what it takes to show the note, but never the pen or OneNote jobs:
    // until it takes over, the running helper alone handles input and saving.
    const type = (msg as { type?: string }).type;
    if (this.standby?.stdin.writable && (type === "layout" || type === "lines" || type === "tool")) this.standby.stdin.write(line);
  }

  // ---------- OneNote sync ----------
  //
  // Background sync ("Sync with OneNote: Automatic"): a synced note's page is re-sent a few seconds after
  // you type or draw in it, and OneNote is checked every few seconds for edits, which come back on their
  // own (for closed notes too). So "Open in OneNote" just shows the page, instantly.

  private pushTimers = new Map<string, number>();
  /** Notes with changes OneNote doesn't have yet (sent once the note is open: its layout places the text). */
  private pendingPush = new Set<string>();
  /** Notes with a send or pull in flight. */
  private busy = new Set<string>();
  private pullOrigin = new Map<string, "manual" | "auto">();
  /** Run once a pull of this note is done (e.g. the send that was waiting for it). */
  private afterPull = new Map<string, () => void>();
  /** Our own writes (pulled ink and text) aren't edits to send back. */
  private quietUntil = new Map<string, number>();
  /** Pulls the user cancelled: don't ask again until the page changes again. */
  private dismissed = new Map<string, string>();
  private pollTimer = 0;
  private lastSyncError = 0;

  private autoSync() {
    return this.settings.oneNoteSync === "auto";
  }

  private syncsNote(path: string) {
    return this.autoSync() && (!!this.settings.oneNotePages[path] || this.settings.oneNoteScope === "opened");
  }

  /** (Re)start the background check for OneNote edits. */
  startOneNotePolling() {
    window.clearInterval(this.pollTimer);
    this.pollTimer = 0;
    if (!this.autoSync()) return;
    const seconds = Math.max(5, this.settings.oneNotePollSeconds || 30);
    this.pollTimer = window.setInterval(() => this.pollOneNote(), seconds * 1000);
    this.registerInterval(this.pollTimer);
  }

  private pollOneNote() {
    if (!this.helper) return;
    const pages = Object.keys(this.settings.oneNotePages)
      .map((noteKey) => ({ noteKey, state: this.syncState(noteKey) }))
      .filter((p) => p.state && p.state.lastModified && !this.busy.has(p.noteKey) && this.app.vault.getFileByPath(p.noteKey))
      .map((p) => ({ noteKey: p.noteKey, pageId: p.state!.pageId, lastModified: this.dismissed.get(p.noteKey) ?? p.state!.lastModified }));
    if (pages.length) this.send({ type: "onenote-poll", poll: { pages } });
  }

  /** Something changed in a note (text or ink): send it to OneNote a few seconds after you stop. */
  private markDirty(path: string) {
    if (!this.syncsNote(path) || (this.quietUntil.get(path) ?? 0) > Date.now()) return;
    this.pendingPush.add(path);
    window.clearTimeout(this.pushTimers.get(path));
    this.pushTimers.set(path, window.setTimeout(() => {
      this.pushTimers.delete(path);
      this.flushPush(path);
    }, 4000));
  }

  /** Send a note with pending changes now, if it's the open note; otherwise when it's next opened. */
  /** Recent background sends per note: a note sending over and over is a bug, and must not hammer OneNote. */
  private recentSends = new Map<string, number[]>();
  private pausedUntil = new Map<string, number>();

  private flushPush(path: string) {
    if (!this.pendingPush.has(path) || this.busy.has(path)) return;
    if (this.activeNote()?.file?.path !== path) return;
    const now = Date.now();
    if ((this.pausedUntil.get(path) ?? 0) > now) return;
    const sends = (this.recentSends.get(path) ?? []).filter((t) => now - t < 60_000);
    if (sends.length >= 6) {
      // Circuit breaker: six background sends in a minute isn't typing, it's a loop. Stop for a while.
      this.pausedUntil.set(path, now + 5 * 60_000);
      this.recentSends.set(path, []);
      console.warn(`[onenote-ink] background sync of ${path} paused: it kept re-sending`);
      this.send({ type: "debug", message: `background OneNote sync of ${path} paused for 5 minutes: ${sends.length} sends in a minute` });
      return;
    }
    sends.push(now);
    this.recentSends.set(path, sends);
    this.pendingPush.delete(path);
    this.openInOneNote({ navigate: false });
  }

  // ---------- ink files follow their notes ----------

  /** A note (PDF, picture) renamed or moved: its ink goes along, and so does its OneNote page. */
  private inkFileMoved(file: TFile, oldPath: string) {
    const base = this.vaultPath();
    if (!base) return;
    const kind = file.extension === "md" ? "markdown" : "pdf";
    const from = inkFileFor(oldPath, kind), to = inkFileFor(file.path, kind);
    if (this.helper) {
      // The helper moves it (it may have that ink open, unsaved): sent now, before the next layout names
      // the new path. A folder renamed moves the ink with it; the helper then just follows.
      this.send({ type: "rename-ink", from: path.join(base, from), to: path.join(base, to) });
    } else {
      void (async () => {
        const adapter = this.app.vault.adapter;
        if (await adapter.exists(from) && !(await adapter.exists(to))) await adapter.rename(from, to);
      })();
    }
    const page = this.settings.oneNotePages[oldPath];
    if (page && !this.settings.oneNotePages[file.path]) {
      this.settings.oneNotePages[file.path] = page;
      delete this.settings.oneNotePages[oldPath];
      this.persist();
    }
  }

  /** A note (PDF, picture) deleted: its ink goes to the trash with it (not left behind for search to find). */
  private inkFileGone(file: TFile) {
    const base = this.vaultPath();
    if (!base) return;
    const ink = inkFileFor(file.path, file.extension === "md" ? "markdown" : "pdf");
    const inkFile = this.app.vault.getFileByPath(ink);
    if (!inkFile) return;
    // The helper lets go of it first, so nothing is saved there again.
    this.send({ type: "rename-ink", from: path.join(base, ink), to: null });
    window.setTimeout(() => {
      const still = this.app.vault.getFileByPath(ink);
      if (still) void this.app.fileManager.trashFile(still);
    }, 500);
  }

  // ---------- Insert space, Ink Editor pen ----------

  /** The note the helper means (by its ink file), if it's the open one. */
  private noteFor(inkPath: string): MarkdownView | null {
    const view = this.activeNote();
    const base = this.vaultPath();
    return view?.file && base && this.inkPathFor(base, view.file.path) === inkPath ? view : null;
  }

  /**
   * Insert space: blank lines in (or out of) the note where the pen dragged; the helper then moves the ink
   * below along with the text (it's told exactly what changed).
   */
  private async onInsertSpace(inkPath: string, line: number, count: number) {
    const view = this.noteFor(inkPath);
    if (!view?.file || !count) return;
    if (count > 0) {
      await this.spliceLines(view.file.path, line, 0, Array<string>(count).fill(""));
      return;
    }
    // Only blank lines come out: text is never deleted by removing space.
    const lines = view.getViewData().split("\n");
    let blank = 0;
    while (blank < -count && line + blank < lines.length && lines[line + blank].trim() === "") blank++;
    if (blank) await this.spliceLines(view.file.path, line, blank, []);
    else new Notice("No empty lines there to remove");
  }

  /** While the plugin edits a note itself, which reports exactly where lines moved (see spliceLines). */
  private ownEdit = false;
  /**
   * Notes the plugin changed outside the editor (Reading view), already reported, by their new text: the
   * editor catches up with the same change later (when you switch back to editing), which isn't new.
   */
  private reported = new Map<string, string>();

  /**
   * Lines went into or out of a note in its editor (typing Enter or Backspace, paste, undo, the Ink Editor
   * pen...): tell the helper exactly where, so ink below moves with its text and ink above stays. Without
   * this the helper worked out where text had gone from the text around each bit of ink, and where the only
   * text near some ink was below an insertion, it moved that ink down too (a practice quiz inserted under
   * a handwritten problem pushed the problem's own ink down into the quiz).
   */
  private onEditorUpdate(u: ViewUpdate) {
    if (!u.docChanged || this.ownEdit) return;
    // Only the editor of the note the ink is on: a note open twice gets each edit in both editors (the
    // other one is brought up to date), which would move its ink twice.
    const active = this.activeNote();
    const file = active?.file;
    const base = this.vaultPath();
    if (!file || !base || (active.editor as unknown as { cm?: EditorView }).cm !== u.view) return;
    const known = this.reported.get(file.path);
    if (known !== undefined && u.transactions.some((t) => t.isUserEvent("set")) && u.state.doc.toString() === known.replace(/\r\n/g, "\n")) {
      this.reported.delete(file.path);
      return;
    }
    const shifts = lineShifts(u.startState.doc, u.changes);
    if (!shifts?.length) return;
    const inkPath = this.inkPathFor(base, file.path);
    for (const s of shifts) this.send({ type: "space-applied", inkPath, line: s.line, count: s.count });
  }

  /**
   * Remove `remove` lines at line `at` and put `insert` there, and tell the helper exactly that (rather than
   * what the editor's diff would suggest: among empty lines, any of them could be "the new one").
   */
  private async spliceLines(notePath: string, at: number, remove: number, insert: string[]): Promise<boolean> {
    const file = this.app.vault.getFileByPath(notePath);
    const base = this.vaultPath();
    if (!file || !base) return false;
    const view = this.app.workspace.getLeavesOfType("markdown").map((l) => l.view).find((v): v is MarkdownView => v instanceof MarkdownView && v.file?.path === notePath);
    if (view && view.getMode() === "source") {
      const editor = view.editor;
      this.ownEdit = true;
      try {
        // Past the end: pad with empty lines first.
        while (editor.lineCount() < at) {
          const last = editor.lineCount() - 1;
          editor.replaceRange("\n", { line: last, ch: editor.getLine(last).length });
        }
        const n = editor.lineCount();
        const removed = Math.min(remove, n - at);
        if (removed > 0) {
          if (at + removed < n) editor.replaceRange("", { line: at, ch: 0 }, { line: at + removed, ch: 0 });
          else if (at > 0) editor.replaceRange("", { line: at - 1, ch: editor.getLine(at - 1).length }, { line: n - 1, ch: editor.getLine(n - 1).length });
          else editor.setValue("");
        }
        if (insert.length) {
          const left = editor.lineCount();
          if (at < left) editor.replaceRange(insert.join("\n") + "\n", { line: at, ch: 0 });
          else editor.replaceRange("\n" + insert.join("\n"), { line: left - 1, ch: editor.getLine(left - 1).length });
        }
        remove = removed;
      } finally {
        this.ownEdit = false;
      }
    } else {
      await this.app.vault.process(file, (data) => {
        const eol = data.includes("\r\n") ? "\r\n" : "\n";
        const lines = data.split(eol);
        while (lines.length < at) lines.push("");
        remove = Math.min(remove, lines.length - at);
        lines.splice(at, remove, ...insert);
        const text = lines.join(eol);
        this.reported.set(notePath, text);
        return text;
      });
    }
    const inkPath = this.inkPathFor(base, notePath);
    if (remove) this.send({ type: "space-applied", inkPath, line: at, count: -remove });
    if (insert.length) this.send({ type: "space-applied", inkPath, line: at, count: insert.length });
    return true;
  }

  /** Where the Ink Editor pen's caret put the insertion point (the next handwriting goes there). */
  private caret: { notePath: string; pos: number; at: number } | null = null;

  /** An Ink Editor pen gesture on the typed text: delete, select, or set where to insert. */
  private onEditGesture(g: EditGesture) {
    const view = this.noteFor(g.inkPath ?? "");
    if (!view?.file) return;
    if (view.getMode() !== "source") {
      new Notice("The Ink Editor pen edits text in Live Preview or Source mode");
      return;
    }
    const cm = (view.editor as unknown as { cm?: EditorView }).cm;
    const sizer = view.contentEl.querySelector(".cm-sizer");
    if (!cm || !(sizer instanceof HTMLElement)) return;
    // Ink is in px from the text column's top-left (the sizer), like everything the helper sends.
    const caret = applyEditGesture(cm, sizer.getBoundingClientRect(), g);
    if (caret !== null) {
      this.caret = { notePath: view.file.path, pos: caret, at: Date.now() };
      new Notice("Write what to insert");
    }
  }
  // ---------- Math Assistant ----------

  mathSettings(): MathSettings {
    return { angle: this.settings.mathAngle, numbers: this.settings.mathNumbers };
  }

  setMathSettings(s: MathSettings) {
    this.settings.mathAngle = s.angle;
    this.settings.mathNumbers = s.numbers;
    this.persist();
  }

  fixInk(id: number) {
    this.send({ type: "math-fix", id });
  }

  /** The Math pane in the right sidebar (opened if needed). */
  private async mathView(): Promise<MathView | null> {
    let leaf = this.app.workspace.getLeavesOfType(MATH_VIEW)[0];
    if (!leaf) {
      const right = this.app.workspace.getRightLeaf(false);
      if (!right) return null;
      await right.setViewState({ type: MATH_VIEW, active: true });
      leaf = right;
    }
    await this.app.workspace.revealLeaf(leaf);
    return leaf.view instanceof MathView ? leaf.view : null;
  }

  /** Handwriting read as math (or corrected with Fix it): show it in the Math pane. */
  private async onInkMath(id: number, inkPath: string, line: number, lineEnd: number, linear: string, latex?: string) {
    const base = this.vaultPath();
    const rel = base ? path.relative(base, inkPath).split(path.sep).join("/") : "";
    const doc = rel ? documentFor(rel) : "";
    const source: MathSource = { from: "ink", id, notePath: /\.md$/i.test(doc) ? doc : undefined, line, lineEnd: Math.max(line, lineEnd) };
    (await this.mathView())?.setProblem(linear, latex, source);
  }

  /** "Math Assistant": the math selected in the note (or an empty pane to type into). */
  private async openMathForSelection() {
    const view = this.activeNote();
    const editor = view?.editor;
    const selected = editor?.getSelection() ?? "";
    const pane = await this.mathView();
    if (!pane) return;
    const typed = selected ? readTyped(selected) : null;
    if (!typed || !view?.file) {
      pane.setProblem("", undefined, view?.file ? { from: "typed", notePath: view.file.path, line: editor?.getCursor().line ?? -1, lineEnd: editor?.getCursor().line ?? -1 } : null);
      return;
    }
    const from = editor!.getCursor("from").line, to = editor!.getCursor("to").line;
    pane.setProblem(typed, undefined, { from: "typed", notePath: view.file.path, line: from, lineEnd: to });
  }

  /** Change a note's lines: through its editor when it's open (one undo step), else the file. */
  private async editNote(notePath: string, change: (lines: string[]) => string[]): Promise<boolean> {
    const file = this.app.vault.getFileByPath(notePath);
    if (!file) return false;
    const view = this.app.workspace.getLeavesOfType("markdown").map((l) => l.view).find((v): v is MarkdownView => v instanceof MarkdownView && v.file?.path === notePath);
    if (view && view.getMode() === "source") {
      const before = view.editor.getValue().split("\n");
      replaceLines(view.editor, before, change(before.slice()));
    } else {
      await this.app.vault.process(file, (data) => {
        const eol = data.includes("\r\n") ? "\r\n" : "\n";
        return change(data.split(eol)).join(eol);
      });
    }
    return true;
  }

  /** Handwritten math → typed: on its own line as a block, or inline beside text; then the ink goes. */
  async convertInk(source: MathSource, latex: string): Promise<boolean> {
    if (!source.notePath || source.id === undefined) return false;
    const ok = await this.editNote(source.notePath, (lines) => {
      const at = source.line < 0 ? lines.length : source.line;
      while (lines.length <= at) lines.push("");
      lines[at] = lines[at].trim() === "" ? `$$${latex}$$` : `${lines[at].replace(/\s+$/, "")} $${latex}$`;
      return lines;
    });
    if (ok) this.send({ type: "consume", id: source.id });
    return ok;
  }

  /** Answers, steps, graphs and quizzes go on new lines under the problem. */
  async insertBelow(source: MathSource, markdown: string): Promise<boolean> {
    if (!source.notePath) return false;
    const file = this.app.vault.getFileByPath(source.notePath);
    if (!file) return false;
    const open = this.app.workspace.getLeavesOfType("markdown").map((l) => l.view).find((v): v is MarkdownView => v instanceof MarkdownView && v.file?.path === source.notePath);
    const lines = (open ? open.getViewData() : await this.app.vault.cachedRead(file)).split(/\r?\n/);
    const after = Math.max(source.line, source.lineEnd);
    const at = after < 0 ? lines.length : after + 1;
    // Blocks (lists, callouts, code) need a blank line before them.
    const gap = at > 0 && at <= lines.length && lines[at - 1].trim() !== "" ? [""] : [];
    // Through spliceLines, so the ink below moves down with the text (and the problem's ink stays).
    return this.spliceLines(source.notePath, at, 0, [...gap, ...markdown.split("\n")]);
  }

  // ---------- handwriting ----------

  /** Recognized handwriting from the helper: type it into the note, then let the helper remove the ink. */
  private async onInkText(id: number, inkPath: string, purpose: string, lines: RecognizedLine[]) {
    const text = lines.map((l) => l.text).join("\n");
    if (!text.trim()) return;
    const target = this.activeInkView();
    const base = this.vaultPath();
    if (purpose === "copy" || target?.kind !== "markdown") {
      await navigator.clipboard.writeText(text);
      new Notice("Handwriting copied as text");
      return;
    }
    const view = target.view as MarkdownView;
    if (!base || this.inkPathFor(base, view.file!.path) !== inkPath) {
      new Notice("Ink to Text: the note changed before the text could go in; the ink is still there");
      return;
    }
    const caret = this.caret;
    if (caret && caret.notePath === view.file?.path && Date.now() - caret.at < 30_000 && view.getMode() === "source") {
      // Written after an Ink Editor caret: goes in right there, with spaces around it as needed.
      this.caret = null;
      const cm = (view.editor as unknown as { cm?: EditorView }).cm;
      if (cm) {
        const doc = cm.state.doc;
        const pos = Math.min(caret.pos, doc.length);
        const before = pos > 0 && !/\s/.test(doc.sliceString(pos - 1, pos)) ? " " : "";
        const after = pos < doc.length && !/\s/.test(doc.sliceString(pos, pos + 1)) ? " " : "";
        cm.dispatch({ changes: { from: pos, insert: before + text.replace(/\n/g, " ") + after }, userEvent: "input" });
        this.send({ type: "consume", id });
        return;
      }
    }
    try {
      if (await insertRecognized(this.app, view, lines)) this.send({ type: "consume", id });
    } catch (e) {
      console.error("[onenote-ink] ink to text", e);
      new Notice("Ink to Text failed; the ink is still there");
    }
  }

  private onInkIndex(inkFile: string, lines: RecognizedLine[]) {
    const base = this.vaultPath();
    if (!base) return;
    const rel = path.relative(base, inkFile).split(path.sep).join("/");
    if (rel.startsWith("..")) return;
    this.handwriting.set(rel, lines.map((l) => ({ text: l.text, line: l.line })));
  }

  /** The vault path of the ink file the helper has open (it indexes that one itself after saving). */
  private loadedInkPath(): string | null {
    const target = this.activeInkView();
    return target?.view.file ? inkFileFor(target.view.file.path, target.kind) : null;
  }

  private indexStaleInk() {
    this.handwriting.prune();
    const stale = this.handwriting.stale();
    if (stale.length) this.queueIndex(stale);
  }

  private queueIndex(files: TFile[]) {
    const base = this.vaultPath();
    if (!base || !files.length) return;
    this.send({ type: "index", files: files.map((f) => ({ path: path.join(base, f.path) })) });
  }

  private onInkSaved(inkFile: string) {
    const base = this.vaultPath();
    if (!base) return;
    // Only notes sync with OneNote (a PDF's ink is `file.pdf.ink`).
    const rel = path.relative(base, inkFile).split(path.sep).join("/").replace(/\.ink$/i, ".md");
    if (this.app.vault.getFileByPath(rel)) this.markDirty(rel);
  }

  private onNoteOpened() {
    const path = this.activeNote()?.file?.path;
    if (!path || !this.autoSync()) return;
    if (this.settings.oneNoteScope === "opened" && !this.settings.oneNotePages[path]) this.pendingPush.add(path);
    // Give the note a moment to lay out before measuring it.
    if (this.pendingPush.has(path)) window.setTimeout(() => this.flushPush(path), 1500);
  }

  private syncState(notePath: string): SyncState | null {
    const s = this.settings.oneNotePages[notePath];
    return s && typeof s === "object" && Array.isArray(s.blocks) ? s : null;
  }

  /**
   * "Open in OneNote". With background sync the page is already current, so this just shows it (and
   * sends any last changes right behind). Otherwise, or for a note that has no page yet, it sends the
   * note (each block at the height it has here, the ink as real OneNote ink) and then opens it.
   * `navigate: false` is a background send. If the page has OneNote edits that weren't pulled, the
   * helper refuses unless `force`: background sends pull them first, a manual send asks.
   */
  openInOneNote(opts: { force?: boolean; navigate?: boolean } = {}) {
    const view = this.activeNote();
    const base = this.vaultPath();
    if (!view?.file || !base) return;
    if (!this.helper) {
      new Notice("OneNote Ink: the ink helper isn't running");
      return;
    }
    const notePath = view.file.path;
    const navigate = opts.navigate ?? true;
    const state = this.syncState(notePath);

    // Instant: the page is kept current in the background (states from before background sync have no
    // content fingerprint yet: send those once, which records it).
    if (navigate && !opts.force && this.autoSync() && state?.lastModified) {
      this.send({ type: "onenote-open", pageId: state.pageId });
      if (this.pushTimers.has(notePath)) {
        window.clearTimeout(this.pushTimers.get(notePath));
        this.pushTimers.delete(notePath);
      }
      this.flushPush(notePath);
      return;
    }

    const sizer = view.getMode() === "preview"
      ? view.contentEl.querySelector(".markdown-preview-sizer")
      : view.contentEl.querySelector(".cm-sizer");
    if (!(sizer instanceof HTMLElement)) return;
    const inkPath = this.inkPathFor(base, notePath);
    const layout = lineTops(view, sizer, (this.inkBottom.get(inkPath) ?? 0) > 0);
    if (!layout) {
      if (navigate) new Notice("OneNote Ink: the note isn't laid out yet; try again in a moment");
      else this.pendingPush.add(notePath);
      return;
    }
    const column = textColumn(view, sizer);
    const saved = this.settings.oneNotePages[notePath];
    this.busy.add(notePath);
    this.send({
      type: "onenote",
      export: {
        title: view.file.basename,
        noteKey: notePath,
        inkPath,
        pageId: typeof saved === "string" ? saved : saved?.pageId ?? null,
        state,
        force: !!opts.force,
        navigate,
        section: this.settings.oneNoteSection || "Obsidian",
        blocks: noteBlocks(this.app, view, layout.tops),
        contentLeft: column.left,
        contentWidth: column.width,
        originY: layout.tops[0] ?? 0,
        whiteInkToBlack: this.settings.oneNoteWhiteToBlack,
      },
    });
    if (navigate) new Notice("Sending to OneNote…");
  }

  private onSent(msg: { ok?: boolean; noteKey?: string; state?: SyncState; navigated?: boolean; navigate?: boolean;
    pageChanged?: boolean; textChanges?: number; inkChanged?: boolean; error?: string; retry?: boolean; quiet?: boolean }) {
    const noteKey = msg.noteKey;
    if (noteKey) this.busy.delete(noteKey);
    if (msg.retry && noteKey) {
      // OneNote wasn't reachable (starting, closed, updating): the change is still owed. Try again in a
      // minute; a background send says nothing, it just gets there later.
      if (this.syncsNote(noteKey)) {
        this.pendingPush.add(noteKey);
        window.setTimeout(() => this.flushPush(noteKey), 60_000);
      }
      if (msg.quiet) return;
    }
    if (msg.ok && noteKey && msg.state) {
      this.settings.oneNotePages[noteKey] = msg.state;
      this.dismissed.delete(noteKey);
      this.persist();
      if (msg.navigated) new Notice("Opened in OneNote");
      return;
    }
    if (msg.pageChanged && noteKey) {
      if (msg.navigate === false && this.autoSync()) {
        // Background send, but OneNote has edits: bring them in first, then send.
        this.pendingPush.add(noteKey);
        this.afterPull.set(noteKey, () => this.flushPush(noteKey));
        this.pullFromOneNote(noteKey, "auto");
        return;
      }
      new OverwriteModal(this.app, msg.textChanges ?? 0, !!msg.inkChanged, (choice) => {
        if (choice === "pull") this.pullFromOneNote(noteKey);
        else if (choice === "send") this.openInOneNote({ force: true });
      }).open();
      return;
    }
    // Background sync keeps trying; don't flood the screen with the same error.
    if (Date.now() - this.lastSyncError > 60_000) {
      this.lastSyncError = Date.now();
      new Notice("OneNote: " + (msg.error ?? "something went wrong"), 8000);
    }
  }

  /** Bring edits made in OneNote back: ink straight away; text applied (background) or shown first (manual). */
  pullFromOneNote(notePath?: string, origin: "manual" | "auto" = "manual") {
    const base = this.vaultPath();
    const path = notePath ?? this.activeNote()?.file?.path;
    if (!path || !base) return;
    const state = this.syncState(path);
    if (!state) {
      if (origin === "manual") new Notice("Send this note to OneNote first (Open in OneNote); then its OneNote edits can be pulled back.");
      return;
    }
    if (!this.helper || this.busy.has(path)) return;
    this.busy.add(path);
    this.pullOrigin.set(path, origin);
    this.send({ type: "onenote-pull", pull: { noteKey: path, inkPath: this.inkPathFor(base, path), state } });
    if (origin === "manual") new Notice("Pulling from OneNote…");
  }

  private onPulled(noteKey: string, changes: TextChange[], inkChanged: boolean, strokes: number, inkWords: number, after?: SyncState) {
    this.busy.delete(noteKey);
    const origin = this.pullOrigin.get(noteKey) ?? "manual";
    this.pullOrigin.delete(noteKey);
    const file = this.app.vault.getFileByPath(noteKey);
    const state = this.syncState(noteKey);
    if (!file || !state) return;
    if (inkChanged) this.quietUntil.set(noteKey, Date.now() + 3000);

    const finish = (applied: boolean, skipped: number) => {
      // The saved state becomes the page as it is now (its changes are in the note).
      if (after) this.settings.oneNotePages[noteKey] = after;
      this.dismissed.delete(noteKey);
      this.persist();
      // Blocks edited on both sides keep Obsidian's text: send it so OneNote matches.
      if (skipped > 0) this.pendingPush.add(noteKey);
      const next = this.afterPull.get(noteKey);
      this.afterPull.delete(noteKey);
      if (next) next();
      else if (skipped > 0 || (applied && this.activeNote()?.file?.path === noteKey)) window.setTimeout(() => this.flushPush(noteKey), 1500);
    };
    const inkNote = inkChanged ? `ink (${strokes} strokes)` : "";
    const wordsNote = inkWords ? ` ${inkWords} handwritten word(s) from inside OneNote text boxes were placed on their lines.` : "";

    if (changes.length === 0) {
      if (origin === "manual") new Notice((inkChanged ? `Updated ${inkNote} from OneNote.` : "Already up to date with OneNote.") + wordsNote);
      else if (inkChanged) new Notice(`OneNote → ${file.basename}: ${inkNote} updated.${wordsNote}`);
      finish(false, 0);
      return;
    }

    // A background pull never deletes text from the note: a block missing on the OneNote side may only mean
    // the page was half-written or the saved state is stale (that's how an image line was lost). The next
    // send puts it back in OneNote. Deleting stays a manual pull's decision, shown in the dialog first.
    if (origin === "auto") changes = changes.filter((c) => c.kind !== "deleted");
    if (changes.length === 0) { finish(false, 0); return; }

    const apply = async () => {
      let skipped = 0;
      this.quietUntil.set(noteKey, Date.now() + 3000);
      await this.app.vault.process(file, (text) => {
        const result = applyTextChanges(text, withoutEchoes(text, changes), state);
        skipped = result.skipped;
        return result.text;
      });
      const what = [`${changes.length - skipped} text change${changes.length - skipped === 1 ? "" : "s"}`, inkNote].filter(Boolean).join(" and ");
      new Notice(`OneNote → ${file.basename}: ${what}.${skipped ? ` ${skipped} block(s) also edited here kept Obsidian's version.` : ""}${wordsNote}`, 8000);
      finish(true, skipped);
    };

    if (origin === "auto" && !this.settings.oneNoteConfirmText) {
      void apply();
      return;
    }
    new PullModal(this.app, file.basename, changes, (choice) => {
      if (choice === "apply") void apply();
      else if (choice === "keep") {
        // Keep Obsidian's text: the page gets it on the next send.
        if (after) this.settings.oneNotePages[noteKey] = after;
        this.pendingPush.add(noteKey);
        this.persist();
        this.afterPull.delete(noteKey);
        window.setTimeout(() => this.flushPush(noteKey), 500);
      } else {
        // Cancelled: don't ask again until the page changes again.
        this.afterPull.delete(noteKey);
        if (after) this.dismissed.set(noteKey, after.lastModified);
      }
    }).open();
  }

  // ---------- layout sync ----------

  /** The note we ink on: the most recent main-area leaf, if it's a visible Markdown view. */
  private activeNote(): MarkdownView | null {
    const leaf = this.app.workspace.getMostRecentLeaf();
    const view = leaf?.view;
    if (!(view instanceof MarkdownView) || !view.file) return null;
    if (view.containerEl.getBoundingClientRect().width === 0) return null;
    return view;
  }

  /** What the ink layer sits on: the most recent main-area leaf, if it's a note, PDF or image, and visible. */
  private activeInkView(): { view: FileView; kind: InkKind } | null {
    const view = this.app.workspace.getMostRecentLeaf()?.view;
    const kind = inkKindOf(view);
    if (!kind || !(view instanceof FileView) || !view.file) return null;
    if (view.containerEl.getBoundingClientRect().width === 0) return null;
    return { view, kind };
  }

  /** The kind of thing the toolbar is on (notes get rule lines and OneNote; PDFs and images don't). */
  inkKind(): InkKind | null {
    return this.activeInkView()?.kind ?? null;
  }

  private mountToolbar(view: FileView | null) {
    const el = this.toolbar.el;
    const kind = view ? inkKindOf(view) : null;
    if (kind !== this.toolbarKind) {
      this.toolbarKind = kind;
      this.toolbar.render();
      // Some tools are for notes only (see sendTool).
      this.sendTool();
    }
    if (view && this.settings.showToolbar) {
      if (el.parentElement !== view.containerEl || el.nextElementSibling !== view.contentEl) {
        view.containerEl.insertBefore(el, view.contentEl);
      }
    } else if (el.parentElement) {
      this.toolbar.closePopover();
      el.detach();
    }
  }

  private sync() {
    const target = this.activeInkView();
    this.mountToolbar(target?.view ?? null);

    // A dialog covers the whole note: the ink steps aside. Menus, popovers and floating toolbars only
    // cover part of it: those get a see-through hole in the ink layer instead (see uiHoles).
    const covered = document.body.querySelector(".modal-container") !== null;
    const base = this.vaultPath();
    if (target && target.kind !== "markdown") {
      this.syncContent(target.view, target.kind, base, covered);
      return;
    }
    const view = target?.view instanceof MarkdownView ? target.view : null;

    const sizer = !view ? null : view.getMode() === "preview"
      ? view.contentEl.querySelector(".markdown-preview-sizer")
      : view.contentEl.querySelector(".cm-sizer");
    if (view && sizer instanceof HTMLElement) this.rulePage(view, sizer);

    const inkPath = view && base ? this.inkPathFor(base, view.file!.path) : null;
    let msg: object = { type: "layout", visible: false };
    this.inkArea = null;
    if (this.enabled && view && !covered && inkPath) {
      const area = view.contentEl.getBoundingClientRect();
      this.inkArea = area;
      const page = (sizer ?? view.contentEl).getBoundingClientRect();
      if (sizer instanceof HTMLElement) this.growPage(sizer, page.height, area.height, inkPath);
      msg = {
        zoom: 1,
        type: "layout",
        visible: true,
        title: document.title,
        ppid: process.ppid,
        x: area.left,
        y: area.top,
        w: area.width,
        h: area.height,
        ox: page.left - area.left,
        oy: page.top - area.top,
        dpr: window.devicePixelRatio,
        dark: isDarkTheme(),
        inkPath,
        holes: uiHoles(area, view),
        domInk: true,
      };
    }
    // The ink drawn in the note (when the pen's away) lives in the text column, so it scrolls with it.
    this.domInk.mount(this.enabled && view && sizer instanceof HTMLElement ? sizer : null, inkPath);

    // Title churns as notes change; leave it out of the change check.
    const key = JSON.stringify({ ...msg, title: undefined });
    if (key !== this.lastSent) {
      this.lastSent = key;
      this.send(msg);
    }

    if (view && inkPath && sizer instanceof HTMLElement) this.syncLines(view, sizer, inkPath);
  }

  private inkPathFor(base: string, notePath: string) {
    return path.join(base, notePath).replace(/\.md$/i, ".ink");
  }

  private lastMissLog = -Infinity;

  /** Screen area the ink layer covers right now (for arming the pen); null when it's hidden. */
  private inkArea: DOMRect | null = null;
  private toolbarKind: InkKind | null = null;
  private lastContentDebug = "";

  /**
   * A PDF or image: ink in its own units (PDF points / image pixels) so it scales with zoom, a PDF's
   * pages acting as its "lines" (see targets.ts) so ink stays on its page however the pages move.
   */
  private syncContent(view: FileView, kind: InkKind, base: string | null, covered: boolean) {
    const layout = kind === "pdf" ? pdfLayout(view) : imageLayout(view);
    const inkPath = base && view.file ? path.join(base, inkFileFor(view.file.path, kind)) : null;
    let msg: object = { type: "layout", visible: false };
    this.inkArea = null;
    if (this.enabled && layout && inkPath && !covered) {
      this.inkArea = layout.area;
      msg = {
        type: "layout",
        visible: true,
        title: document.title,
        ppid: process.ppid,
        x: layout.area.left,
        y: layout.area.top,
        w: layout.area.width,
        h: layout.area.height,
        ox: layout.ox,
        oy: layout.oy,
        zoom: layout.zoom,
        dpr: window.devicePixelRatio,
        dark: layout.dark,
        inkPath,
        holes: uiHoles(layout.area, view),
        // The ink is drawn in the page while the pen is away, so it scrolls with the PDF's pages or the picture.
        domInk: !!layout.inkHost,
      };
    }
    const host = this.enabled && layout?.inkHost && inkPath && !covered ? layout.inkHost : null;
    this.domInk.mount(host?.el ?? null, inkPath, host && layout ? { left: host.left, top: host.top, scale: layout.zoom, positioned: host.positioned } : undefined);
    const key = JSON.stringify({ ...msg, title: undefined });
    if (key !== this.lastSent) {
      this.lastSent = key;
      this.send(msg);
    }
    if (!layout || !inkPath) return;

    const tops = layout.tops.map((t) => Math.round(t * 100) / 100);
    const linesKey = `page|${inkPath}|${tops.join(",")}`;
    if (linesKey !== this.lastLines) {
      this.lastLines = linesKey;
      this.send({ type: "lines", inkPath, mode: "page", tops, hashes: layout.hashes, measuredFrom: 0, measuredTo: tops.length - 1 });
    }
    const debug = `${kind}|${inkPath}`;
    if (debug !== this.lastContentDebug) {
      this.lastContentDebug = debug;
      this.send({ type: "debug", message: `${kind}: ${view.file?.path}, ${tops.length} page(s), zoom ${layout.zoom.toFixed(3)} (from ${layout.source}), ink colours ${layout.dark ? "flipped for a dark page" : "as drawn"}` });
    }
  }

  /**
   * Send where each source line sits, and a fingerprint of its text (see lines.ts), so the helper keeps
   * ink with the text it was written by: across Live Preview ⇄ Reading view, and however the note's
   * lines move (typing, other plugins, sync). Measured a few times a second (layout settles as
   * off-screen parts render), immediately after mode/note switches and edits, sent only on change.
   */
  private syncLines(view: MarkdownView, sizer: HTMLElement, inkPath: string) {
    const mode = view.getMode() === "preview" ? "preview" : "source";
    const now = performance.now();
    if (!this.linesDirty && mode === this.linesMode && inkPath === this.linesPath && now - this.linesAt < 250) return;
    this.linesAt = now;
    this.linesDirty = false;
    this.linesMode = mode;
    this.linesPath = inkPath;
    // While a note is opening or reloading, its editor is briefly empty. Never report that.
    const text = view.editor?.getValue() ?? "";
    if (!text && (view.file?.stat.size ?? 0) > 0) return;
    // ...or still shows the note it had before: Obsidian names the new file first and reads it after.
    // Those lines would be another note's, and the ink would be moved onto them.
    if (!this.showsFile(view, text)) return;
    const layout = lineTops(view, sizer, (this.inkBottom.get(inkPath) ?? 0) > 0);
    if (!layout) return;
    const hashes = lineHashes(text.split("\n"));
    if (hashes.length !== layout.tops.length) return; // layout and text out of step; next pass
    const tops = layout.tops.map((t) => Math.round(t * 2) / 2);
    const key = `${mode}|${inkPath}|${layout.measuredFrom}-${layout.measuredTo}|${tops.join(",")}|${hashes.join(",")}`;
    if (key === this.lastLines) return;
    // Record what the layout looked like a moment after each mode/note switch (once it has settled).
    const switched = !this.lastLines.startsWith(`${mode}|${inkPath}|`);
    this.lastLines = key;
    this.send({ type: "lines", inkPath, mode, tops, hashes, measuredFrom: layout.measuredFrom, measuredTo: layout.measuredTo });
    if (switched) {
      window.setTimeout(() => {
        if (this.activeNote() !== view) return;
        const area = view.contentEl.getBoundingClientRect();
        const now = lineTops(view, sizer, (this.inkBottom.get(inkPath) ?? 0) > 0);
        this.send({ type: "debug", message: describeLayout(view, sizer, area, now?.tops) });
      }, 1500);
    }
  }

  /** When each note was last typed in (its text may differ from the file on disk until it's saved). */
  private lastEdit = new Map<string, number>();

  /**
   * Is this text the view's file? Compared by size with the file on disk (allowing CRLF line ends and a
   * BOM); a note typed in during the last few seconds may not be saved yet, so it counts as its own.
   */
  private showsFile(view: MarkdownView, text: string): boolean {
    const file = view.file;
    if (!file) return false;
    if (Date.now() - (this.lastEdit.get(file.path) ?? 0) < 5000) return true;
    const bytes = new TextEncoder().encode(text).length;
    const breaks = text.split("\n").length - 1;
    return [bytes, bytes + breaks, bytes + 3, bytes + 3 + breaks].includes(file.stat.size);
  }

  /**
   * Make the note as tall as its ink, like it already is for its text: keep a writing margin of blank
   * page below the lowest stroke, so writing near the bottom always leaves room to scroll on.
   * The space is a ::after spacer on the sizer (see styles.css), so Obsidian's own DOM is untouched.
   */
  private growPage(sizer: HTMLElement, pageHeight: number, areaHeight: number, inkPath: string) {
    sizer.addClass("oni-grow");
    const current = parseFloat(sizer.style.getPropertyValue("--oni-ink-space")) || 0;
    const bottom = this.inkBottom.get(inkPath) ?? 0;
    const room = Math.max(240, areaHeight * 0.5);
    const natural = pageHeight - current;
    const needed = bottom > 0 ? Math.max(0, Math.ceil(bottom + room - natural)) : 0;
    if (Math.abs(needed - current) >= 1) sizer.style.setProperty("--oni-ink-space", `${needed}px`);
  }

  /**
   * Notebook rule lines behind the text column (and the grown ink space below it). Pitch and phase
   * come from the body text, so typed lines sit on the rules and handwriting lines up with them.
   */
  private rulePage(view: MarkdownView, sizer: HTMLElement) {
    const mode = PAGE_LINES.find((l) => l.id === this.settings.pageLines);
    sizer.toggleClass("oni-ruled", !!mode && !mode.grid);
    sizer.toggleClass("oni-grid", !!mode?.grid);
    if (!mode) return;
    const set = (name: string, value: string) => {
      if (sizer.style.getPropertyValue(name) !== value) sizer.style.setProperty(name, value);
    };
    if (mode.id !== "text") {
      // OneNote's fixed spacings, from the top of the page.
      set("--oni-rule-pitch", `${mode.pitch}px`);
      set("--oni-rule-phase", "0px");
      return;
    }
    const body = view.getMode() === "preview"
      ? sizer.querySelector(".markdown-preview-section")
      : view.contentEl.querySelector(".cm-content");
    if (!(body instanceof HTMLElement)) return;
    const cs = getComputedStyle(body);
    const pitch = parseFloat(cs.lineHeight);
    if (!pitch) return;
    const textTop = body.getBoundingClientRect().top + (parseFloat(cs.paddingTop) || 0) - sizer.getBoundingClientRect().top;
    const phase = ((textTop % pitch) + pitch) % pitch;
    set("--oni-rule-pitch", `${pitch.toFixed(2)}px`);
    set("--oni-rule-phase", `${phase.toFixed(2)}px`);
  }

  private onPointer(e: PointerEvent) {
    if (e.pointerType !== "pen" || !this.enabled || this.toolbar.popoverOpen) return;
    const r = this.inkArea;
    if (!r) return;
    if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) return;
    const now = performance.now();
    if (now - this.lastPen < 100) return;
    this.lastPen = now;
    this.send({ type: "pen" });
  }
}

/** Obsidian UI that appears over a note: menus, suggestions, hover previews, tooltips, notices, our popover. */
const OVER_NOTE = ".menu, .suggestion-container, .popover, .oni-popover, .tooltip, .notice-container, .prompt";
/** Other plugins' floating bars inside a note (editing toolbar, floating table of contents, search...). */
const FLOATING = "#editingToolbarModalBar, #editingToolbarPopoverBar, .editing-toolbar-ai-inline-prompt, .floating-toc-div, .document-search-container, .cm-panels, [class*='toolbar' i], [class*='floating' i]";

/**
 * Where the ink layer must stay see-through, in viewport CSS px: Obsidian's menus and popovers, and
 * floating toolbars from other plugins, wherever they overlap the note. The ink window is a separate
 * window above Obsidian, so without this it would draw over them (or, as before, vanish for every menu).
 */
function uiHoles(area: DOMRect, view: FileView): { x: number; y: number; w: number; h: number; what: string }[] {
  const found = new Set<Element>();
  for (const el of Array.from(document.body.children)) if (el.matches(OVER_NOTE)) found.add(el);
  for (const el of Array.from(view.containerEl.querySelectorAll(FLOATING))) {
    if (el.closest(".oni-toolbar, .oni-graph, .oni-math")) continue;
    // Only things that float (positioned over the page), not toolbars that are part of the layout.
    const pos = getComputedStyle(el).position;
    if (pos === "fixed" || pos === "absolute" || pos === "sticky") found.add(el);
  }
  for (const el of Array.from(document.querySelectorAll("#editingToolbarModalBar, #editingToolbarPopoverBar"))) found.add(el);
  const holes: { x: number; y: number; w: number; h: number; what: string }[] = [];
  for (const el of found) {
    if (!(el instanceof HTMLElement) || (el.checkVisibility && !el.checkVisibility({ opacityProperty: true, visibilityProperty: true }))) continue;
    const r = el.getBoundingClientRect();
    if (r.width < 1 || r.height < 1 || r.right <= area.left || r.left >= area.right || r.bottom <= area.top || r.top >= area.bottom) continue;
    // A menu or toolbar never covers most of the page: something that does is a container that happens to
    // match (it would leave the whole page see-through, so the pen writes on nothing).
    const covered = (Math.min(r.right, area.right) - Math.max(r.left, area.left)) * (Math.min(r.bottom, area.bottom) - Math.max(r.top, area.top));
    if (covered > 0.4 * area.width * area.height) continue;
    holes.push({ x: Math.floor(r.left), y: Math.floor(r.top), w: Math.ceil(r.width) + 1, h: Math.ceil(r.height) + 1, what: `${el.tagName.toLowerCase()}.${Array.from(el.classList).join(".")}` });
  }
  return holes;
}

/** Mirrors OneNote's Options › Advanced › Pen, for the options Windows Ink can honour. */
class InkSettingTab extends PluginSettingTab {
  constructor(app: App, private plugin: OneNoteInkPlugin) {
    super(app, plugin);
  }

  display() {
    const { containerEl } = this;
    const s = this.plugin.settings;
    containerEl.empty();

    new Setting(containerEl).setName("Pen").setHeading();

    new Setting(containerEl)
      .setName("Use pen pressure sensitivity")
      .setDesc("Line thickness follows how hard you press, for a more natural look.")
      .addToggle((t) => t.setValue(s.pressure).onChange((v) => { s.pressure = v; this.plugin.save(); }));

    new Setting(containerEl)
      .setName("Smooth ink when drawing with a pen")
      .setDesc("Fits strokes to smooth curves.")
      .addToggle((t) => t.setValue(s.smooth).onChange((v) => { s.smooth = v; this.plugin.save(); }));

    new Setting(containerEl)
      .setName("Enable the scratch-out gesture while inking")
      .setDesc(this.plugin.scratchOutAvailable
        ? "Scribble back and forth over ink to erase it."
        : "Windows' gesture recogniser isn't available on this device, so this has no effect.")
      .addToggle((t) => t.setValue(s.scratchOut).onChange((v) => { s.scratchOut = v; this.plugin.save(); }));

    new Setting(containerEl)
      .setName("Enable the draw and hold ink-to-shape gesture while inking")
      .setDesc("Draw a circle, triangle, rectangle or line and keep the pen still for a moment; lift to turn it into a clean shape.")
      .addToggle((t) => t.setValue(s.shapes).onChange((v) => { s.shapes = v; this.plugin.save(); }));

    new Setting(containerEl)
      .setName("Pen eraser end")
      .setDesc("What flipping the pen over (or the eraser button) does.")
      .addDropdown((d) => d
        .addOption("stroke", "Stroke eraser")
        .addOption("point", "Point eraser")
        .setValue(s.eraserEnd)
        .onChange((v) => { s.eraserEnd = v as "stroke" | "point"; this.plugin.save(); }));

    new Setting(containerEl)
      .setName("Point eraser size")
      .addDropdown((d) => {
        for (const size of ERASER_SIZES) d.addOption(String(size.mm), `${size.label} (${size.mm} mm)`);
        d.setValue(String(s.eraserSize)).onChange((v) => { s.eraserSize = Number(v); this.plugin.save(); });
      });

    new Setting(containerEl).setName("Page").setHeading();

    new Setting(containerEl)
      .setName("Rule and grid lines")
      .setDesc("Notebook lines behind the note: spaced to match your text, OneNote's rule spacings, or a grid. Also on the toolbar.")
      .addDropdown((d) => {
        d.addOption("none", "None");
        for (const l of PAGE_LINES) d.addOption(l.id, l.label);
        d.setValue(s.pageLines).onChange((v) => { s.pageLines = v; this.plugin.save(); });
      });

    new Setting(containerEl).setName("OneNote").setHeading();

    new Setting(containerEl)
      .setName("Section")
      .setDesc("Each note gets its own page in this section of your current notebook (created if it doesn't exist).")
      .addText((t) => t.setValue(s.oneNoteSection).onChange((v) => { s.oneNoteSection = v.trim() || "Obsidian"; this.plugin.save(false); }));

    new Setting(containerEl)
      .setName("Sync with OneNote")
      .setDesc("Automatic: a note's OneNote page updates a few seconds after you type or draw, and edits made in OneNote come back on their own, so \"Open in OneNote\" is instant. Off: only when you send or pull.")
      .addDropdown((d) => d
        .addOption("auto", "Automatic")
        .addOption("off", "Off (send and pull yourself)")
        .setValue(s.oneNoteSync)
        .onChange((v) => {
          s.oneNoteSync = v as "auto" | "off";
          this.plugin.save(false);
          this.plugin.startOneNotePolling();
          this.display();
        }));

    if (s.oneNoteSync === "auto") {
      new Setting(containerEl)
        .setName("Check OneNote for changes every")
        .addDropdown((d) => {
          for (const [secs, label] of [[15, "15 seconds"], [30, "30 seconds"], [60, "minute"], [300, "5 minutes"]] as const) d.addOption(String(secs), label);
          d.setValue(String(s.oneNotePollSeconds)).onChange((v) => {
            s.oneNotePollSeconds = Number(v);
            this.plugin.save(false);
            this.plugin.startOneNotePolling();
          });
        });

      new Setting(containerEl)
        .setName("Notes to sync")
        .setDesc("Every note you open gets a OneNote page (made in the background), so any note opens instantly in OneNote.")
        .addDropdown((d) => d
          .addOption("sent", "Notes I've sent to OneNote")
          .addOption("opened", "Every note I open")
          .setValue(s.oneNoteScope)
          .onChange((v) => { s.oneNoteScope = v as "sent" | "opened"; this.plugin.save(false); }));

      new Setting(containerEl)
        .setName("Ask before applying text from OneNote")
        .setDesc("Off: text edited in OneNote goes straight into the note, with a notice saying what changed. Ink always syncs straight away.")
        .addToggle((t) => t.setValue(s.oneNoteConfirmText).onChange((v) => { s.oneNoteConfirmText = v; this.plugin.save(false); }));
    }

    new Setting(containerEl)
      .setName("Make white ink black in OneNote")
      .setDesc("OneNote pages are white unless OneNote is in dark mode, where it shows black ink as white. Turn off to keep white ink white.")
      .addToggle((t) => t.setValue(s.oneNoteWhiteToBlack).onChange((v) => { s.oneNoteWhiteToBlack = v; this.plugin.save(false); }));

    new Setting(containerEl).setName("Performance").setHeading();

    new Setting(containerEl)
      .setName("Draw ink with the GPU")
      .setDesc("Off (default): software rendering, about 100 MB less memory. Turn on if writing feels laggy. Takes effect when the ink helper restarts.")
      .addToggle((t) => t.setValue(s.gpuRendering).onChange((v) => { s.gpuRendering = v; this.plugin.save(false); }));

    new Setting(containerEl).setName("Toolbar").setHeading();

    new Setting(containerEl)
      .setName("Show ink toolbar")
      .setDesc("Pens, highlighter, erasers, lasso, undo and redo above each note.")
      .addToggle((t) => t.setValue(s.showToolbar).onChange((v) => { s.showToolbar = v; this.plugin.save(); }));

    new Setting(containerEl)
      .setName("Reset pens")
      .setDesc("Restore the default pen gallery and clear recent colours.")
      .addButton((b) => b.setButtonText("Reset").onClick(() => {
        s.slots = DEFAULT_SLOTS.map((p) => ({ ...p }));
        s.active = 0;
        s.tool = "slot";
        s.recent = [];
        this.plugin.save();
      }));
  }
}
