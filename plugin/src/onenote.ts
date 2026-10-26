import { App, MarkdownView, Modal, Setting } from "obsidian";

export interface ExportBlock {
  kind: string;
  text: string;
  /** CSS px from the text column top, same space as the ink. */
  y: number;
  /** Source lines (0-based, inclusive). */
  start: number;
  end: number;
  /** LaTeX in the block → MathML, so OneNote gets real equations (see mathFor). */
  math?: Record<string, string>;
}

/** $$display$$ and $inline$ math; the helper matches the same patterns (OneNoteExport.MathPattern). */
const MATH = /\$\$([\s\S]+?)\$\$|(?<![\\$])\$(?!\s)([^$\n]+?)(?<!\s)\$(?!\d)/g;

/**
 * MathML for each piece of math in a block, from Obsidian's own MathJax (OneNote stores equations as
 * MathML). Nothing if MathJax isn't loaded: the math then goes over as text.
 */
export function mathFor(text: string): Record<string, string> | undefined {
  const mathjax = (window as unknown as { MathJax?: { tex2mml?: (tex: string, opts: { display: boolean }) => string } }).MathJax;
  if (!mathjax?.tex2mml) return undefined;
  const out: Record<string, string> = {};
  for (const m of text.matchAll(MATH)) {
    const tex = (m[1] ?? m[2]).trim();
    if (out[tex]) continue;
    try {
      out[tex] = mathjax.tex2mml(tex, { display: m[1] !== undefined })
        .replace(/\s(data-[\w-]+|class)="[^"]*"/g, "")
        .replace(/>\s+</g, "><")
        .trim();
    } catch {
      /* leave it as text */
    }
  }
  return Object.keys(out).length ? out : undefined;
}

/** One sent block, as the helper recorded it (see helper/OneNoteSync.cs). */
export interface SyncBlock {
  objectId: string;
  hash: string;
  text: string;
  start: number;
  end: number;
  y: number;
}

/** How the OneNote page looked right after the last send. */
export interface SyncState {
  pageId: string;
  originX: number;
  originY: number;
  inkSig: string;
  /** Fingerprint of the page's content, for the cheap "did OneNote change it?" check. */
  lastModified: string;
  blocks: SyncBlock[];
}

export interface TextChange {
  kind: "changed" | "deleted" | "new";
  block: number;
  afterBlock: number;
  oldText: string;
  markdown: string;
}

/**
 * The note as blocks for a OneNote page: Obsidian's own section cache says where each block starts
 * (heading, paragraph, list, code…); `tops` (from lines.ts) says how high that line sits right now.
 */
export function noteBlocks(app: App, view: MarkdownView, tops: number[]): ExportBlock[] {
  const file = view.file;
  if (!file) return [];
  const lines = (view.editor?.getValue() ?? "").split("\n");
  const sections = app.metadataCache.getFileCache(file)?.sections ?? [];
  const blocks: ExportBlock[] = [];
  for (const s of sections) {
    const start = s.position.start.line;
    const end = Math.min(s.position.end.line, lines.length - 1);
    if (s.type === "yaml" || start >= lines.length) continue;
    const text = lines.slice(start, end + 1).join("\n");
    blocks.push({ kind: s.type, text, y: tops[start] ?? 0, start, end, math: text.includes("$") ? mathFor(text) : undefined });
  }
  return blocks;
}

/** Where the note's text actually starts and how wide it is, relative to the text column (sizer). */
export function textColumn(view: MarkdownView, sizer: HTMLElement): { left: number; width: number } {
  const sizerRect = sizer.getBoundingClientRect();
  const candidates = view.getMode() === "preview"
    ? view.contentEl.querySelectorAll(".markdown-preview-sizer > div:not(.mod-header):not(.mod-footer):not(.markdown-preview-pusher) > *")
    : view.contentEl.querySelectorAll(".cm-content > .cm-line");
  for (const el of Array.from(candidates)) {
    const r = el.getBoundingClientRect();
    if (r.width > 0 && r.height > 0) return { left: r.left - sizerRect.left, width: r.width };
  }
  return { left: 0, width: sizerRect.width };
}

/**
 * Apply OneNote's text changes to the note. Each sent block is found again by its text (nearest to where
 * it was), so edits made elsewhere in the note since the send don't matter; a block that was also edited
 * in Obsidian can't be found and is skipped, never overwritten. Everything else is left byte-for-byte.
 */
export function applyTextChanges(text: string, changes: TextChange[], state: SyncState): { text: string; skipped: number } {
  // The note's own line endings (split on \n alone, a CRLF note's lines all ended in \r, matched no sent
  // block, and every OneNote edit was skipped).
  const eol = text.includes("\r\n") ? "\r\n" : "\n";
  const lines = text.split(eol);
  const locate = (b: SyncBlock): [number, number] | null => {
    const want = b.text.split("\n");
    let best = -1;
    for (let i = 0; i + want.length <= lines.length; i++) {
      if (want.every((l, k) => lines[i + k] === l) && (best < 0 || Math.abs(i - b.start) < Math.abs(best - b.start))) best = i;
    }
    return best < 0 ? null : [best, best + want.length - 1];
  };
  const bodyStart = (): number => {
    if (lines[0]?.trim() !== "---") return 0;
    const end = lines.findIndex((l, i) => i > 0 && (l.trim() === "---" || l.trim() === "..."));
    return end < 0 ? 0 : end + 1;
  };

  const ops: { at: number; del: number; insert: string[]; order: number }[] = [];
  let skipped = 0;
  changes.forEach((c, order) => {
    if (c.kind === "new") {
      const anchor = c.afterBlock >= 0 ? state.blocks[c.afterBlock] : null;
      const loc = anchor ? locate(anchor) : null;
      if (anchor && !loc) { skipped++; return; }
      const md = c.markdown.split("\n");
      if (loc) ops.push({ at: loc[1] + 1, del: 0, insert: ["", ...md], order });
      else ops.push({ at: bodyStart(), del: 0, insert: [...md, ""], order });
      return;
    }
    const block = state.blocks[c.block];
    const loc = block ? locate(block) : null;
    if (!loc) { skipped++; return; }
    let del = loc[1] - loc[0] + 1;
    // A deleted block takes its blank separator line with it, when that can't glue two blocks together
    // (it's the first block of the body, or there's already a blank line before it).
    if (c.kind === "deleted" && lines[loc[1] + 1] === "" && (loc[0] === bodyStart() || lines[loc[0] - 1] === "")) del++;
    ops.push({ at: loc[0], del, insert: c.kind === "changed" ? c.markdown.split("\n") : [], order });
  });
  // Bottom-up, so earlier positions stay valid.
  ops.sort((a, b) => b.at - a.at || b.order - a.order);
  for (const op of ops) lines.splice(op.at, op.del, ...op.insert);
  return { text: lines.join(eol), skipped };
}

/** Shows what OneNote changed in the note's text and asks before applying it. */
export class PullModal extends Modal {
  constructor(app: App, private title: string, private changes: TextChange[], private choose: (choice: "apply" | "keep" | "cancel") => void) {
    super(app);
  }

  onOpen() {
    const { contentEl } = this;
    this.setTitle(`Text changed in OneNote: ${this.title}`);
    contentEl.createEl("p", {
      text: "Ink changes have already been pulled. These text changes were made in OneNote; the rest of the note stays exactly as it is.",
      cls: "setting-item-description",
    });
    const list = contentEl.createDiv({ cls: "oni-changes" });
    for (const c of this.changes) {
      const item = list.createDiv({ cls: "oni-change" });
      item.createDiv({
        cls: "oni-change-kind",
        text: c.kind === "changed" ? "Edited" : c.kind === "deleted" ? "Deleted in OneNote" : "Added in OneNote",
      });
      if (c.kind !== "new") item.createEl("pre", { cls: "oni-change-old", text: c.oldText });
      if (c.kind !== "deleted") item.createEl("pre", { cls: "oni-change-new", text: c.markdown });
    }
    new Setting(contentEl)
      .addButton((b) => b.setButtonText("Cancel").onClick(() => this.done("cancel")))
      .addButton((b) => b.setButtonText("Keep Obsidian's text").setWarning().onClick(() => this.done("keep")))
      .addButton((b) => b.setButtonText("Apply OneNote's text").setCta().onClick(() => this.done("apply")));
  }

  private done(choice: "apply" | "keep" | "cancel") {
    this.close();
    this.choose(choice);
  }

  onClose() {
    this.contentEl.empty();
  }
}

/** Sending would overwrite edits made in OneNote: pull first, send anyway, or stop. */
export class OverwriteModal extends Modal {
  constructor(app: App, private textChanges: number, private inkChanged: boolean, private choose: (choice: "pull" | "send" | "cancel") => void) {
    super(app);
  }

  onOpen() {
    const what = [
      this.textChanges ? `${this.textChanges} text change${this.textChanges === 1 ? "" : "s"}` : "",
      this.inkChanged ? "ink changes" : "",
    ].filter(Boolean).join(" and ");
    this.setTitle("OneNote has changes you haven't pulled");
    this.contentEl.createEl("p", { text: `The OneNote page has ${what} since this note was last sent. Sending now replaces the page and loses them.` });
    new Setting(this.contentEl)
      .addButton((b) => b.setButtonText("Cancel").onClick(() => this.done("cancel")))
      .addButton((b) => b.setButtonText("Send anyway").setWarning().onClick(() => this.done("send")))
      .addButton((b) => b.setButtonText("Pull first").setCta().onClick(() => this.done("pull")));
  }

  private done(choice: "pull" | "send" | "cancel") {
    this.close();
    this.choose(choice);
  }

  onClose() {
    this.contentEl.empty();
  }
}

/** Text the way it looks after a trip through OneNote (what the export writes), for comparing. */
function asOneNoteSees(md: string): string {
  return md
    .replace(/^```[^\n]*$/gm, "```")
    .replace(/!\[\[([^\]]+)\]\]/g, "[$1]")
    .replace(/\[\[([^\]|]+)\|([^\]]+)\]\]/g, "$2")
    .replace(/\[\[([^\]]+)\]\]/g, "$1")
    .replace(/\s+/g, " ")
    .trim();
}

/**
 * "New" blocks from OneNote that are only copies of what the note already has (a page left half-written
 * when OneNote failed mid-send shows old and new outlines side by side): dropped, never inserted again.
 */
export function withoutEchoes(text: string, changes: TextChange[]): TextChange[] {
  const note = asOneNoteSees(text);
  return changes.filter((c) => {
    if (c.kind !== "new") return true;
    const block = asOneNoteSees(c.markdown);
    const echo = block.length > 0 && note.includes(block);
    if (echo) console.log("[onenote-ink] ignored a OneNote block that duplicates the note:", c.markdown.slice(0, 60));
    return !echo;
  });
}