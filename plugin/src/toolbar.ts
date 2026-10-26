import { Menu, setIcon } from "obsidian";
import { ColorWheel } from "./colorWheel";
import {
  EFFECTS, ERASER_SIZES, InkSettings, MAX_RECENT, MAX_SLOTS, PAGE_LINES, PenKind, PenSlot, SIZE_RANGE, SWATCHES, mmToPx, shownColor,
} from "./settings";

export interface ToolbarHost {
  settings: InkSettings;
  /** Persist settings, push the active tool to the ink helper and (by default) redraw the toolbar. */
  save(rerender?: boolean): void;
  /** Send a command to the ink helper (undo, redo, and the selection actions). */
  command(type: string, extra?: object): void;
  /** How many strokes the lasso has selected. */
  selectionCount(): number;
  inkToText(purpose?: "insert" | "copy"): void;
  pasteInk(): void;
  toggleLayer(): void;
  layerOn(): boolean;
  openInOneNote(): void;
  pullFromOneNote(): void;
  /** What the toolbar is on: notes get rule lines and OneNote; PDFs and images don't. */
  inkKind(): "markdown" | "pdf" | "image" | null;
}

/** Logarithmic slider mapping: fine control at small thicknesses, like a real pen range. */
const SLIDER_STEPS = 1000;
const sizeToSlider = (mm: number, [lo, hi]: [number, number]) =>
  Math.round((Math.log(mm / lo) / Math.log(hi / lo)) * SLIDER_STEPS);
const sliderToSize = (v: number, [lo, hi]: [number, number]) =>
  Math.round(lo * Math.pow(hi / lo, v / SLIDER_STEPS) * 100) / 100;

const KIND_ICON: Record<PenKind, string> = { pen: "pen-line", pencil: "pencil", highlighter: "highlighter" };
const KIND_LABEL: Record<PenKind, string> = { pen: "Pen", pencil: "Pencil", highlighter: "Highlighter" };

/** The shape gallery, as in OneNote's Insert Shapes (ids match the helper's ShapeGallery). */
const SHAPES: [string, string][] = [
  ["line", "Line"], ["arrow", "Arrow"], ["double-arrow", "Double arrow"], ["rectangle", "Rectangle"], ["oval", "Oval"],
  ["triangle", "Triangle"], ["right-triangle", "Right triangle"], ["diamond", "Diamond"], ["parallelogram", "Parallelogram"],
  ["trapezoid", "Trapezoid"], ["pentagon", "Pentagon"], ["hexagon", "Hexagon"], ["cube", "Cube"], ["cylinder", "Cylinder"],
  ["cone", "Cone"], ["axes", "Graph axes"], ["axes-quadrant", "Graph axes (first quadrant)"], ["axes-3d", "3D axes"],
];
const formatMm = (mm: number) => `${mm.toFixed(mm < 1 ? 2 : 1)} mm`;

/**
 * Ink toolbar above the note. Built from Obsidian's own pieces (clickable-icon buttons, Menu,
 * native slider and inputs) so it takes on the user's theme instead of bringing its own.
 */
export class Toolbar {
  readonly el: HTMLElement;
  private canUndo = false;
  private canRedo = false;
  private popover: HTMLElement | null = null;
  private replaying = false;
  private closePopoverHandlers: (() => void) | null = null;

  constructor(private host: ToolbarHost) {
    this.el = createDiv({ cls: "oni-toolbar" });
    this.render();
  }

  get popoverOpen() {
    return this.popover !== null;
  }

  setHistory(canUndo: boolean, canRedo: boolean) {
    if (canUndo === this.canUndo && canRedo === this.canRedo) return;
    this.canUndo = canUndo;
    this.canRedo = canRedo;
    this.render();
  }

  setReplay(playing: boolean) {
    if (playing === this.replaying) return;
    this.replaying = playing;
    this.render();
  }

  destroy() {
    this.closePopover();
    this.el.remove();
  }

  render() {
    const s = this.host.settings;
    this.el.empty();
    this.el.toggleClass("is-off", !this.host.layerOn());

    // Pens
    s.slots.forEach((slot, i) => {
      const active = s.tool === "slot" && s.active === i;
      const btn = this.button(KIND_ICON[slot.kind],
        `${slot.effect ? EFFECTS.find((e) => e.id === slot.effect)?.label + " pen" : KIND_LABEL[slot.kind]}, ${formatMm(slot.size)}${active ? " — tap again to edit" : ""}`,
        () => {
          if (active) {
            this.openPenPopover(btn, i);
          } else {
            s.tool = "slot";
            s.active = i;
            this.host.save();
          }
        });
      btn.addClass("oni-pen");
      btn.toggleClass("is-active", active);
      this.colorBar(btn, slot);
    });
    if (s.slots.length < MAX_SLOTS) {
      this.button("plus", "Add pen", () => {
        const base = s.slots[s.active] ?? s.slots[0];
        s.slots.push({ kind: "pen", color: base?.color ?? "#000000", size: base?.kind === "pen" ? base.size : 0.5 });
        s.active = s.slots.length - 1;
        s.tool = "slot";
        this.host.save();
        const pens = this.el.querySelectorAll<HTMLElement>(".oni-pen");
        this.openPenPopover(pens[pens.length - 1], s.active);
      }).addClass("oni-add");
    }
    if (this.host.inkKind() === "markdown") {
      this.button("type", "Ink to Text pen: writing becomes typed text when you pause", () => {
        s.tool = "text-pen";
        this.host.save();
      }).toggleClass("is-active", s.tool === "text-pen");
      this.button("text-cursor-input", "Ink Editor pen: strike through or scratch out words to delete them, circle to select, draw ^ to insert", () => {
        s.tool = "editor-pen";
        this.host.save();
      }).toggleClass("is-active", s.tool === "editor-pen");
    }

    this.divider();

    // Eraser: one button, mode and size in a native menu (like OneNote's eraser dropdown).
    const erasing = s.tool === "eraser-stroke" || s.tool === "eraser-point";
    const eraser = this.button("eraser", erasing ? "Eraser — tap again for options" : "Eraser", () => {
      if (!erasing) {
        s.tool = s.lastEraser;
        this.host.save();
      } else {
        this.openEraserMenu(eraser);
      }
    });
    eraser.toggleClass("is-active", erasing);

    const lasso = this.button("lasso", "Lasso select", () => {
      s.tool = "lasso";
      this.host.save();
    });
    lasso.toggleClass("is-active", s.tool === "lasso");
    if (s.tool === "lasso") this.selectionActions();

    this.divider();
    this.pageTools();
    this.divider();

    this.button("undo-2", "Undo", () => this.host.command("undo")).toggleClass("is-disabled", !this.canUndo);
    this.button("redo-2", "Redo", () => this.host.command("redo")).toggleClass("is-disabled", !this.canRedo);

    // Page and layer toggles sit on the right.
    const right = this.el.createDiv({ cls: "oni-right" });
    if (this.host.inkKind() === "markdown") {
      const oneNote = this.button("book-open", "OneNote", () => {
        const menu = new Menu();
        menu.addItem((i) => i.setTitle("Open in OneNote").setIcon("book-open").onClick(() => this.host.openInOneNote()));
        menu.addItem((i) => i.setTitle("Pull changes from OneNote").setIcon("download").onClick(() => this.host.pullFromOneNote()));
        const r = oneNote.getBoundingClientRect();
        menu.showAtPosition({ x: r.left, y: r.bottom + 4 });
      }, right);
      const lines = this.button("align-justify", "Rule and grid lines", () => {
        const menu = new Menu();
        menu.addItem((i) => i.setTitle("No lines").setChecked(s.pageLines === "none").onClick(() => { s.pageLines = "none"; this.host.save(); }));
        menu.addSeparator();
        for (const l of PAGE_LINES) {
          if (l.id === "grid-small") menu.addSeparator();
          menu.addItem((i) => i.setTitle(l.label).setChecked(s.pageLines === l.id).onClick(() => { s.pageLines = l.id; this.host.save(); }));
        }
        const r = lines.getBoundingClientRect();
        menu.showAtPosition({ x: r.left, y: r.bottom + 4 });
      }, right);
      lines.toggleClass("is-active", s.pageLines !== "none");
    }
    this.button(this.host.layerOn() ? "eye" : "eye-off", this.host.layerOn() ? "Hide ink" : "Show ink", () => {
      this.host.toggleLayer();
      this.render();
    }, right);
  }

  /** OneNote's Draw-tab page tools: insert space, shapes, ruler, laser pointer, ink replay. */
  private pageTools() {
    const s = this.host.settings;
    const pick = (tool: InkSettings["tool"]) => { s.tool = tool; this.host.save(); };
    if (this.host.inkKind() === "markdown") {
      this.button("move-vertical", "Insert space: drag down to add space (and lines) to the note, up to remove it", () => pick("space"))
        .toggleClass("is-active", s.tool === "space");
    }
    const shapeOn = s.tool.startsWith("shape:");
    const shapeLabel = SHAPES.find(([id]) => id === s.shape)?.[1] ?? "Shape";
    const shapes = this.button("shapes", shapeOn ? `${shapeLabel}: drag to draw. Tap again for other shapes` : `Shapes (${shapeLabel.toLowerCase()})`, () => {
      if (!shapeOn) { pick(`shape:${s.shape}`); return; }
      const menu = new Menu();
      for (const [id, label] of SHAPES) {
        if (id === "cube" || id === "axes") menu.addSeparator();
        menu.addItem((i) => i.setTitle(label).setChecked(s.shape === id).onClick(() => { s.shape = id; pick(`shape:${id}`); }));
      }
      const r = shapes.getBoundingClientRect();
      menu.showAtPosition({ x: r.left, y: r.bottom + 4 });
    });
    shapes.toggleClass("is-active", shapeOn);
    this.button("ruler", s.ruler ? "Put the ruler away" : "Ruler: draw along its edge for a straight line; drag its middle to move it, its ends to turn it", () => {
      s.ruler = !s.ruler;
      this.host.save();
      this.host.command("ruler", { visible: s.ruler });
    }).toggleClass("is-active", s.ruler);
    this.button("pointer", "Laser pointer (fades, isn't saved)", () => pick("laser")).toggleClass("is-active", s.tool === "laser");
    this.button(this.replaying ? "square" : "play", this.replaying ? "Stop replay" : "Ink replay: watch the page being written", () =>
      this.host.command(this.replaying ? "replay-stop" : "replay")).toggleClass("is-active", this.replaying);
  }

  private button(icon: string, label: string, onClick: () => void, parent: HTMLElement = this.el): HTMLElement {
    const b = parent.createDiv({ cls: "clickable-icon oni-btn", attr: { "aria-label": label, role: "button", tabindex: "0" } });
    setIcon(b, icon);
    b.addEventListener("click", () => {
      if (!b.hasClass("is-disabled")) onClick();
    });
    return b;
  }

  /**
   * What you can do with lasso-selected ink, like OneNote's Draw tab with a selection: Ink to Text,
   * straighten, recolour, copy, delete, and the rest in a menu. Here in the toolbar rather than floating
   * over the ink, because the pen over the ink belongs to the ink layer.
   */
  private selectionActions() {
    const host = this.host;
    const note = host.inkKind() === "markdown";
    if (host.selectionCount() === 0) {
      this.button("clipboard-paste", "Paste ink", () => host.pasteInk());
      return;
    }
    this.button("type", note ? "Ink to Text" : "Copy as text", () => host.inkToText());
    this.button("sigma", "Ink to Math: solve, graph and more", () => host.command("ink-to-math"));
    this.button("move-horizontal", "Straighten handwriting", () => host.command("straighten"));
    const pen = host.settings.slots[host.settings.active];
    const restyle = this.button("paintbrush", "Change to the current pen's colour", () => host.command("restyle"));
    restyle.addClass("oni-restyle");
    if (pen) this.colorBar(restyle, pen);
    this.button("copy", "Copy ink", () => host.command("copy"));
    this.button("trash-2", "Delete selected ink", () => host.command("deleteSelection"));
    const more = this.button("more-horizontal", "More", () => {
      const menu = new Menu();
      if (note) menu.addItem((i) => i.setTitle("Copy as text").setIcon("clipboard-type").onClick(() => host.inkToText("copy")));
      menu.addItem((i) => i.setTitle("Paste ink").setIcon("clipboard-paste").onClick(() => host.pasteInk()));
      menu.addSeparator();
      menu.addItem((i) => i.setTitle("Thicker").setIcon("plus").onClick(() => host.command("pen-scale", { factor: 1.25 })));
      menu.addItem((i) => i.setTitle("Thinner").setIcon("minus").onClick(() => host.command("pen-scale", { factor: 0.8 })));
      menu.addSeparator();
      menu.addItem((i) => i.setTitle("Rotate left 90°").setIcon("rotate-ccw").onClick(() => host.command("rotate", { angle: -90 })));
      menu.addItem((i) => i.setTitle("Rotate right 90°").setIcon("rotate-cw").onClick(() => host.command("rotate", { angle: 90 })));
      const r = more.getBoundingClientRect();
      menu.showAtPosition({ x: r.left, y: r.bottom + 4 });
    });
  }

  private divider() {
    this.el.createDiv({ cls: "oni-divider" });
  }

  /** Office-style colour bar under the pen icon; thicker for thicker pens. */
  private colorBar(btn: HTMLElement, slot: PenSlot) {
    btn.querySelector(".oni-color-bar")?.remove();
    const bar = btn.createDiv({ cls: "oni-color-bar" });
    if (slot.effect) bar.style.background = EFFECTS.find((e) => e.id === slot.effect)?.css ?? "";
    else bar.style.backgroundColor = shownColor(slot.color);
    bar.style.height = `${slot.kind === "highlighter" ? 4 : Math.max(2, Math.min(4, 1.5 + slot.size))}px`;
  }

  private openEraserMenu(anchor: HTMLElement) {
    const s = this.host.settings;
    const menu = new Menu();
    menu.addItem((item) => item
      .setTitle("Stroke eraser")
      .setIcon("eraser")
      .setChecked(s.tool === "eraser-stroke")
      .onClick(() => { s.tool = s.lastEraser = "eraser-stroke"; this.host.save(); }));
    menu.addSeparator();
    for (const size of ERASER_SIZES) {
      menu.addItem((item) => item
        .setTitle(`${size.label} point eraser`)
        .setChecked(s.tool === "eraser-point" && s.eraserSize === size.mm)
        .onClick(() => {
          s.tool = s.lastEraser = "eraser-point";
          s.eraserSize = size.mm;
          this.host.save();
        }));
    }
    const r = anchor.getBoundingClientRect();
    menu.showAtPosition({ x: r.left, y: r.bottom + 4 });
  }

  // ---------- pen popover ----------

  private openPopover(anchor: HTMLElement, build: (pop: HTMLElement) => void, onClose?: () => void) {
    this.closePopover();
    const pop = document.body.createDiv({ cls: "oni-popover" });
    build(pop);

    const a = anchor.getBoundingClientRect();
    const p = pop.getBoundingClientRect();
    const left = Math.max(8, Math.min(a.left, window.innerWidth - p.width - 8));
    const top = a.bottom + 4 + p.height > window.innerHeight ? Math.max(8, a.top - p.height - 4) : a.bottom + 4;
    pop.style.left = `${left}px`;
    pop.style.top = `${top}px`;

    const outside = (e: PointerEvent) => {
      if (!pop.contains(e.target as Node) && !anchor.contains(e.target as Node)) this.closePopover();
    };
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") this.closePopover(); };
    document.addEventListener("pointerdown", outside, true);
    document.addEventListener("keydown", esc, true);
    this.popover = pop;
    this.closePopoverHandlers = () => {
      document.removeEventListener("pointerdown", outside, true);
      document.removeEventListener("keydown", esc, true);
      onClose?.();
    };
  }

  closePopover() {
    const handlers = this.closePopoverHandlers;
    this.closePopoverHandlers = null;
    this.popover?.remove();
    this.popover = null;
    handlers?.();
  }

  private openPenPopover(anchor: HTMLElement, index: number) {
    const s = this.host.settings;
    const slot = s.slots[index];
    if (!slot) return;
    const startColor = slot.color;

    this.openPopover(anchor, (pop) => {
      // Header: pen / highlighter, and the current size.
      const head = pop.createDiv({ cls: "oni-pop-head" });
      const kindBtns = {} as Record<PenKind, HTMLElement>;
      for (const kind of ["pen", "pencil", "highlighter"] as PenKind[]) {
        const b = head.createDiv({
          cls: "clickable-icon",
          attr: { "aria-label": KIND_LABEL[kind], role: "button" },
        });
        setIcon(b, KIND_ICON[kind]);
        kindBtns[kind] = b;
      }
      const sizeValue = head.createDiv({ cls: "oni-size-value" });

      const wheel = new ColorWheel(pop, slot.color, (hex) => setColor(hex, false), 168);

      const slider = pop.createEl("input", { cls: "slider oni-size", type: "range", attr: { "aria-label": "Size" } });
      slider.min = "0";
      slider.max = String(SLIDER_STEPS);

      // Ink is hidden while this is open, so preview the stroke here.
      const svgNs = "http://www.w3.org/2000/svg";
      const svg = document.createElementNS(svgNs, "svg");
      svg.setAttribute("viewBox", "0 0 200 36");
      svg.classList.add("oni-preview");
      const path = document.createElementNS(svgNs, "path");
      path.setAttribute("d", "M10 24 C 40 4, 64 4, 84 18 S 124 34, 148 16 S 180 8, 190 14");
      path.setAttribute("fill", "none");
      // Effect pens preview as their gradient.
      const defs = document.createElementNS(svgNs, "defs");
      const gradient = document.createElementNS(svgNs, "linearGradient");
      gradient.setAttribute("id", "oni-effect-preview");
      defs.appendChild(gradient);
      svg.appendChild(defs);
      const effectStops = (id?: string) => {
        const colors = (EFFECTS.find((e) => e.id === id)?.css.match(/#[0-9A-Fa-f]{6}/g) ?? []).slice(-6);
        return colors.map((c, i) => {
          const stop = document.createElementNS(svgNs, "stop");
          stop.setAttribute("offset", String(colors.length > 1 ? i / (colors.length - 1) : 0));
          stop.setAttribute("stop-color", c);
          return stop;
        });
      };
      path.setAttribute("stroke-linejoin", "round");
      svg.appendChild(path);
      pop.appendChild(svg);

      const swatches = pop.createDiv({ cls: "oni-swatches" });
      for (const hex of SWATCHES) this.chip(swatches, hex, () => setColor(hex, true));
      // Effect pens (pen only), like OneNote's.
      const effects = pop.createDiv({ cls: "oni-swatches oni-effects" });
      const effectChips: HTMLElement[] = [];
      for (const fx of EFFECTS) {
        const chip = effects.createDiv({ cls: "oni-chip oni-effect", attr: { "aria-label": `${fx.label} pen`, role: "button" } });
        chip.style.background = fx.css;
        chip.addEventListener("click", () => {
          slot.kind = "pen";
          slot.effect = slot.effect === fx.id ? undefined : fx.id;
          refresh();
          commit();
        });
        effectChips.push(chip);
      }
      const recent = pop.createDiv({ cls: "oni-swatches oni-recent" });
      for (const hex of s.recent) this.chip(recent, hex, () => setColor(hex, true));

      const foot = pop.createDiv({ cls: "oni-pop-foot" });
      const hexInput = foot.createEl("input", { type: "text", attr: { maxlength: "7", spellcheck: "false", "aria-label": "Hex colour" } });
      hexInput.addEventListener("change", () => {
        const v = hexInput.value.trim();
        const hex = (v.startsWith("#") ? v : "#" + v).toUpperCase();
        if (/^#[0-9A-F]{6}$/.test(hex)) setColor(hex, true);
        else hexInput.value = slot.color;
      });
      if (s.slots.length > 1) {
        const remove = foot.createDiv({ cls: "clickable-icon", attr: { "aria-label": "Remove pen", role: "button" } });
        setIcon(remove, "trash-2");
        remove.addEventListener("click", () => {
          s.slots.splice(index, 1);
          s.active = Math.min(s.active, s.slots.length - 1);
          this.closePopover();
          this.host.save();
        });
      }

      const range = () => SIZE_RANGE[slot.kind];
      const refresh = () => {
        for (const k of Object.keys(kindBtns) as PenKind[]) kindBtns[k].toggleClass("is-active", slot.kind === k);
        effects.style.display = slot.kind === "pen" ? "" : "none";
        EFFECTS.forEach((fx, i) => effectChips[i].toggleClass("is-active", slot.effect === fx.id));
        sizeValue.setText(formatMm(slot.size));
        slider.value = String(sizeToSlider(slot.size, range()));
        path.setAttribute("stroke", slot.effect ? "url(#oni-effect-preview)" : shownColor(slot.color));
        gradient.replaceChildren(...effectStops(slot.effect));
        path.setAttribute("stroke-width", String(Math.min(24, mmToPx(slot.size))));
        path.setAttribute("stroke-linecap", slot.kind === "highlighter" ? "butt" : "round");
        path.setAttribute("stroke-opacity", slot.kind === "highlighter" ? "0.5" : "1");
        if (document.activeElement !== hexInput) hexInput.value = slot.color;
        const pen = this.el.querySelectorAll<HTMLElement>(".oni-pen")[index];
        if (pen) {
          setIcon(pen, KIND_ICON[slot.kind]);
          this.colorBar(pen, slot);
        }
      };

      // Save without re-rendering the toolbar, so the popover's anchor stays put while dragging.
      const commit = () => this.host.save(false);

      const setColor = (hex: string, moveWheel: boolean) => {
        slot.color = hex;
        slot.effect = undefined;
        if (moveWheel) wheel.set(hex);
        refresh();
        commit();
      };

      for (const k of Object.keys(kindBtns) as PenKind[]) {
        kindBtns[k].addEventListener("click", () => {
          if (slot.kind === k) return;
          slot.kind = k;
          if (k !== "pen") slot.effect = undefined;
          const [lo, hi] = range();
          slot.size = Math.max(lo, Math.min(hi, k === "highlighter" ? 5 : 0.5));
          refresh();
          commit();
        });
      }
      slider.addEventListener("input", () => {
        slot.size = sliderToSize(Number(slider.value), range());
        refresh();
        commit();
      });

      refresh();
    }, () => {
      // Remember custom colours, newest first.
      if (slot.color !== startColor && !SWATCHES.includes(slot.color)) {
        s.recent = [slot.color, ...s.recent.filter((c) => c !== slot.color)].slice(0, MAX_RECENT);
      }
      this.host.save();
    });
  }

  private chip(parent: HTMLElement, hex: string, onPick: () => void) {
    const chip = parent.createDiv({ cls: "oni-chip", attr: { "aria-label": hex, role: "button" } });
    chip.style.backgroundColor = hex;
    chip.addEventListener("click", onPick);
  }
}
