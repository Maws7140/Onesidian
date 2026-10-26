import { App, Editor, MarkdownView, SuggestModal, TFile, normalizePath } from "obsidian";

/** A line of handwriting as Windows Ink read it, with the note line it was written on (-1: unknown). */
export interface RecognizedLine {
  text: string;
  line: number;
  list?: boolean;
}

/**
 * The note with recognized handwriting typed in where it was written, like OneNote's Ink to Text: on a
 * blank line the text takes its place (as a list item if it was written as one); next to typed text it
 * joins that line. Writing below the end of the note goes the same distance down, on new lines. Never into
 * frontmatter: writing up there goes on a new line just under it.
 */
export function placeRecognized(doc: string[], recognized: RecognizedLine[]): string[] {
  const out = doc.slice();
  let fmEnd = -1;
  if (out[0] === "---") {
    const close = out.indexOf("---", 1);
    if (close > 0) fmEnd = close;
  }

  // Lines written on the same note line (a word beside another) join up, in reading order.
  const byLine = new Map<number, { texts: string[]; list: boolean }>();
  for (const r of recognized) {
    const text = r.text.trim();
    if (!text) continue;
    const line = r.line < 0 ? out.length : r.line;
    const group = byLine.get(line) ?? { texts: [], list: false };
    group.texts.push(text);
    group.list ||= !!r.list;
    byLine.set(line, group);
  }

  // Bottom up, so inserting under the frontmatter doesn't shift lines still to place.
  const lines = [...byLine.keys()].sort((a, b) => b - a);
  const intoFrontmatter: string[] = [];
  for (const line of lines) {
    const { texts, list } = byLine.get(line)!;
    const text = texts.join(" ");
    if (line <= fmEnd) {
      intoFrontmatter.unshift((list ? "- " : "") + text);
      continue;
    }
    while (out.length <= line) out.push("");
    const current = out[line];
    out[line] = current.trim() === "" ? (list ? "- " : "") + text : current.replace(/\s+$/, "") + " " + text;
  }
  if (intoFrontmatter.length) out.splice(fmEnd + 1, 0, ...intoFrontmatter);
  return out;
}

/**
 * Apply a new version of the note through the editor as one edit covering only the lines that changed,
 * so the cursor and scroll stay put and a single undo takes it back.
 */
export function replaceLines(editor: Editor, before: string[], after: string[]) {
  let start = 0;
  while (start < before.length && start < after.length && before[start] === after[start]) start++;
  let endBefore = before.length, endAfter = after.length;
  while (endBefore > start && endAfter > start && before[endBefore - 1] === after[endAfter - 1]) { endBefore--; endAfter--; }
  if (start === endBefore && start === endAfter) return;
  const inserted = after.slice(start, endAfter);
  if (start === endBefore) {
    // Only new lines: insert them before line `start` (or after the last line).
    if (start < before.length) editor.replaceRange(inserted.join("\n") + "\n", { line: start, ch: 0 });
    else editor.replaceRange("\n" + inserted.join("\n"), { line: before.length - 1, ch: before[before.length - 1].length });
    return;
  }
  const from = { line: start, ch: 0 };
  const to = { line: endBefore - 1, ch: before[endBefore - 1].length };
  if (start === endAfter) {
    // Only removed lines: take their line breaks with them.
    if (endBefore < before.length) editor.replaceRange("", from, { line: endBefore, ch: 0 });
    else if (start > 0) editor.replaceRange("", { line: start - 1, ch: before[start - 1].length }, to);
    else editor.replaceRange("", from, to);
    return;
  }
  editor.replaceRange(inserted.join("\n"), from, to);
}

/** Type recognized handwriting into a note (see placeRecognized). */
export async function insertRecognized(app: App, view: MarkdownView, recognized: RecognizedLine[]): Promise<boolean> {
  const file = view.file;
  if (!file) return false;
  if (view.getMode() === "source") {
    const before = view.editor.getValue().split("\n");
    replaceLines(view.editor, before, placeRecognized(before, recognized));
  } else {
    await app.vault.process(file, (data) => placeRecognized(data.split("\n"), recognized).join("\n"));
  }
  return true;
}

// ---------- handwriting search ----------

export interface IndexEntry {
  /** The ink file's modified time when it was read. */
  mtime: number;
  lines: { text: string; line: number }[];
}

/** The file an ink file belongs to: `Note.ink` → `Note.md`, `file.pdf.ink` → `file.pdf`. */
export function documentFor(inkPath: string): string {
  const inner = inkPath.replace(/\.ink$/i, "");
  return /\.[a-z0-9]+$/i.test(inner) ? inner : inner + ".md";
}

/**
 * Every ink file's handwriting as text (read by Windows Ink in the helper), so handwriting is searchable
 * like OneNote's. Kept in the plugin's folder, keyed by ink file path; re-read when an ink file changes.
 */
export class HandwritingIndex {
  entries: Record<string, IndexEntry> = {};
  private saveTimer = 0;

  constructor(private app: App, private file: string) {}

  async load() {
    try {
      if (await this.app.vault.adapter.exists(this.file)) this.entries = JSON.parse(await this.app.vault.adapter.read(this.file));
    } catch (e) {
      console.warn("[onenote-ink] handwriting index unreadable; rebuilding", e);
      this.entries = {};
    }
  }

  set(inkPath: string, lines: IndexEntry["lines"]) {
    const file = this.app.vault.getFileByPath(inkPath);
    if (!file || !lines.length) delete this.entries[inkPath];
    else this.entries[inkPath] = { mtime: file.stat.mtime, lines };
    this.saveSoon();
  }

  remove(inkPath: string) {
    if (!(inkPath in this.entries)) return;
    delete this.entries[inkPath];
    this.saveSoon();
  }

  rename(from: string, to: string) {
    const entry = this.entries[from];
    if (!entry) return;
    delete this.entries[from];
    this.entries[to] = entry;
    this.saveSoon();
  }

  /** Ink files whose handwriting hasn't been read since they last changed. */
  stale(): TFile[] {
    return this.app.vault.getFiles().filter((f) => f.extension === "ink" && this.entries[f.path]?.mtime !== f.stat.mtime);
  }

  /** Entries for ink files that are gone. */
  prune() {
    let changed = false;
    for (const key of Object.keys(this.entries)) {
      if (!this.app.vault.getFileByPath(key)) { delete this.entries[key]; changed = true; }
    }
    if (changed) this.saveSoon();
  }

  search(query: string, limit = 200): HandwritingMatch[] {
    const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
    const matches: HandwritingMatch[] = [];
    for (const [inkPath, entry] of Object.entries(this.entries)) {
      for (const l of entry.lines) {
        const text = l.text.toLowerCase();
        if (terms.length && !terms.every((t) => text.includes(t))) continue;
        matches.push({ doc: documentFor(inkPath), line: l.line, text: l.text });
        if (matches.length >= limit) return matches;
      }
    }
    return matches;
  }

  private saveSoon() {
    window.clearTimeout(this.saveTimer);
    this.saveTimer = window.setTimeout(() => {
      void this.app.vault.adapter.write(normalizePath(this.file), JSON.stringify(this.entries));
    }, 1000);
  }
}

export interface HandwritingMatch {
  doc: string;
  /** Note line (or PDF page, 0-based) the writing is on; -1 if unknown. */
  line: number;
  text: string;
}

/** "Search handwriting": find words written in ink across the vault and jump to them. */
export class HandwritingSearchModal extends SuggestModal<HandwritingMatch> {
  constructor(app: App, private index: HandwritingIndex) {
    super(app);
    this.setPlaceholder("Search handwriting…");
    this.emptyStateText = Object.keys(index.entries).length ? "No handwriting matches" : "No handwriting read yet";
  }

  getSuggestions(query: string): HandwritingMatch[] {
    return this.index.search(query);
  }

  renderSuggestion(match: HandwritingMatch, el: HTMLElement) {
    el.createDiv({ text: match.text });
    const where = match.doc.replace(/\.md$/i, "");
    const at = match.line < 0 ? "" : /\.pdf$/i.test(match.doc) ? ` · page ${match.line + 1}` : ` · line ${match.line + 1}`;
    el.createEl("small", { text: where + at, cls: "oni-search-where" });
  }

  async onChooseSuggestion(match: HandwritingMatch) {
    const file = this.app.vault.getFileByPath(match.doc);
    if (!file) return;
    if (/\.pdf$/i.test(match.doc) && match.line >= 0) {
      await this.app.workspace.openLinkText(`${match.doc}#page=${match.line + 1}`, "", false);
      return;
    }
    const leaf = this.app.workspace.getLeaf(false);
    await leaf.openFile(file, match.line >= 0 && file.extension === "md" ? { eState: { line: match.line } } : undefined);
  }
}
