import { FileView, MarkdownView, View } from "obsidian";

/** What the ink layer can sit on: a note, a PDF in Obsidian's PDF viewer, or an image in its image view. */
export type InkKind = "markdown" | "pdf" | "image";

export function inkKindOf(view: View | null | undefined): InkKind | null {
  if (!view) return null;
  if (view instanceof MarkdownView) return "markdown";
  const type = view.getViewType();
  if (type === "pdf" || type === "image") return type;
  return null;
}

/**
 * A PDF or image laid out for ink. Ink lives in the content's own units (PDF points / image pixels), so it
 * scales with zoom: `zoom` is CSS px per unit, `ox`/`oy` the content's top-left inside `area`. A PDF's
 * pages are its "lines": `tops` is where each page starts, in points, as currently laid out (the gaps
 * between pages are fixed on screen, so they change in points as you zoom).
 */
export interface ContentLayout {
  area: DOMRect;
  /** A PDF's scrolling container, which the note-style copy of the ink goes in (see DomInk.mount). */
  scroller?: HTMLElement;
  ox: number;
  oy: number;
  zoom: number;
  tops: number[];
  hashes: number[];
  source: string;
  dark: boolean;
}

const fnv = (s: string) => {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193); }
  return h | 0 || 1;
};

// Obsidian's PDF view wraps pdf.js (5.x). Internal; only used for each page's exact scale.
interface PdfJsViewer { getPageView?(index: number): { viewport?: { scale?: number } } | undefined }

export function pdfLayout(view: FileView): ContentLayout | null {
  const container = view.contentEl.querySelector(".pdf-viewer-container");
  if (!(container instanceof HTMLElement)) return null;
  const pages = Array.from(container.querySelectorAll<HTMLElement>(".page[data-page-number]"));
  if (!pages.length) return null;

  // CSS px per PDF point: from pdf.js itself, else the --scale-factor it sets on the viewer.
  const api = (view as unknown as { viewer?: { child?: { pdfViewer?: { pdfViewer?: PdfJsViewer } } } }).viewer?.child?.pdfViewer?.pdfViewer;
  let zoom = api?.getPageView?.(0)?.viewport?.scale ?? 0;
  let source = "pdf.js viewport";
  if (!(zoom > 0)) {
    zoom = parseFloat(getComputedStyle(pages[0]).getPropertyValue("--scale-factor"));
    source = "--scale-factor";
  }
  if (!(zoom > 0) || !isFinite(zoom)) return null;

  const area = container.getBoundingClientRect();
  // Page content starts inside the page's border.
  const first = pages[0].getBoundingClientRect();
  const left0 = first.left + pages[0].clientLeft;
  const top0 = first.top + pages[0].clientTop;
  const tops = pages.map((p) => (p.getBoundingClientRect().top + p.clientTop - top0) / zoom);
  const hashes = pages.map((p) => fnv("page " + p.dataset.pageNumber));

  // Ink colours follow the page, not the app theme: Obsidian can draw PDF pages dark in dark mode.
  const bg = getComputedStyle(pages[0]).backgroundColor;
  return { area, scroller: container, ox: left0 - area.left, oy: top0 - area.top, zoom, tops, hashes, source, dark: isDarkColor(bg) };
}

export function imageLayout(view: FileView): ContentLayout | null {
  const img = view.contentEl.querySelector(".image-container img");
  if (!(img instanceof HTMLImageElement) || !img.naturalWidth) return null;
  const area = view.contentEl.getBoundingClientRect();
  const r = img.getBoundingClientRect();
  if (r.width === 0) return null;
  // Ink in image pixels: it stays on the same spot of the picture at any size. Pictures aren't recoloured.
  return { area, ox: r.left - area.left, oy: r.top - area.top, zoom: r.width / img.naturalWidth, tops: [0], hashes: [1], source: "image", dark: false };
}

function isDarkColor(css: string): boolean {
  const m = /rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?/.exec(css);
  if (!m || (m[4] !== undefined && parseFloat(m[4]) === 0)) return false;
  const [r, g, b] = [m[1], m[2], m[3]].map(Number);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b < 96;
}

/** Where a file's ink is kept: `Note.ink` next to `Note.md`; `file.pdf.ink` / `image.png.ink` for others. */
export function inkFileFor(notePath: string, kind: InkKind): string {
  return kind === "markdown" ? notePath.replace(/\.md$/i, ".ink") : notePath + ".ink";
}
