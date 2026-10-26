/**
 * The note's own copy of its ink, drawn in the page (SVG) so it scrolls exactly with the text. The helper
 * sends each stroke's outline (the same shape Windows Ink draws) when it's new or changed, plus the order
 * of all strokes, and says when its native layer is on screen (pen near, a selection...): then this copy
 * hides, so there's never two.
 */
export interface DomStroke {
  id: number;
  /** SVG path of the stroke's outline, in ink units (CSS px from the text column's top-left). */
  d: string;
  /** Colour #RRGGBB and opacity. */
  c: string;
  o: number;
  /** Highlighter: drawn first, under the pen ink. */
  hl: boolean;
}

export interface DomUpdate {
  path?: string;
  /** Start over (another note, or the copy was off). */
  reset?: boolean;
  /** Every stroke's id, in drawing order; ids not listed are gone. */
  order?: number[];
  /** New or changed strokes. */
  add?: DomStroke[];
}

const SVG = "http://www.w3.org/2000/svg";

export class DomInk {
  private svg: SVGSVGElement;
  private path = "";
  private nativeShown = true;
  private strokes = new Map<number, { el: SVGPathElement; hl: boolean }>();

  constructor() {
    this.svg = document.createElementNS(SVG, "svg");
    this.svg.classList.add("oni-ink-svg");
    this.svg.setAttribute("aria-hidden", "true");
  }

  update(u: DomUpdate) {
    const path = u.path ?? "";
    if (u.reset || path !== this.path) {
      this.strokes.clear();
      this.path = path;
    }
    for (const s of u.add ?? []) {
      let entry = this.strokes.get(s.id);
      if (!entry) {
        entry = { el: document.createElementNS(SVG, "path"), hl: s.hl };
        this.strokes.set(s.id, entry);
      }
      entry.hl = s.hl;
      entry.el.setAttribute("d", s.d);
      entry.el.setAttribute("fill", s.c);
      if (s.o < 1) entry.el.setAttribute("fill-opacity", String(s.o));
      else entry.el.removeAttribute("fill-opacity");
    }
    const order = u.order ?? [...this.strokes.keys()];
    const keep = new Set(order);
    for (const id of [...this.strokes.keys()]) if (!keep.has(id)) this.strokes.delete(id);
    const listed = order.map((id) => this.strokes.get(id)).filter((e): e is { el: SVGPathElement; hl: boolean } => !!e);
    // Highlighters under the pen ink, each group in drawing order.
    this.svg.replaceChildren(...listed.filter((e) => e.hl).map((e) => e.el), ...listed.filter((e) => !e.hl).map((e) => e.el));
  }

  /** The helper's own layer is on screen (true) or not: this copy shows only when it isn't. */
  setNative(inkPath: string, visible: boolean) {
    if (inkPath && inkPath !== this.path) return;
    this.nativeShown = visible;
    this.svg.style.visibility = visible ? "hidden" : "";
  }

  /**
   * Keep the copy in the note being shown (its text column, where ink coordinates start), or take it out
   * when there's no note, the ink is for another note, or the ink layer is off.
   *
   * `at`: a PDF or a picture instead. The copy goes in an element that scrolls with it (the PDF viewer's
   * scrolling container, the picture's box), at the content's top-left, scaled from ink units (PDF points,
   * image pixels) to CSS px: it then scrolls with the pages, where the ink window chasing them lagged and
   * bounced. `positioned`: the element is a containing block already, so its CSS is left alone.
   */
  mount(host: HTMLElement | null, inkPath: string | null, at?: { left: number; top: number; scale: number; positioned?: boolean }) {
    if (!host || !inkPath || inkPath !== this.path) {
      this.svg.remove();
      return;
    }
    if (this.svg.parentElement !== host) {
      if (!at?.positioned) host.addClass("oni-ink-host");
      host.appendChild(this.svg);
      this.svg.style.visibility = this.nativeShown ? "hidden" : "";
    }
    const place = at ? `${Math.round(at.left * 100) / 100}|${Math.round(at.top * 100) / 100}|${at.scale}` : "";
    if (place !== this.placed) {
      this.placed = place;
      this.svg.style.left = at ? `${at.left}px` : "";
      this.svg.style.top = at ? `${at.top}px` : "";
      this.svg.style.transformOrigin = at ? "0 0" : "";
      this.svg.style.transform = at ? `scale(${at.scale})` : "";
    }
  }

  private placed = "";

  destroy() {
    this.svg.remove();
  }
}
