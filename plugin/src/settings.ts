export type PenKind = "pen" | "pencil" | "highlighter";

/** OneNote's effect pens (a pen slot can be one of these instead of a plain colour). */
export const EFFECTS: { id: string; label: string; css: string }[] = [
  { id: "rainbow", label: "Rainbow", css: "linear-gradient(90deg,#E81123,#F7630C,#FFB900,#16C60C,#0078D7,#8E2FC8)" },
  { id: "galaxy", label: "Galaxy", css: "radial-gradient(circle at 30% 40%,#fff 0 1px,transparent 2px),linear-gradient(135deg,#1B1464,#4B2C9A,#8E44AD,#1B1464)" },
  { id: "lava", label: "Lava", css: "linear-gradient(135deg,#8B0000,#E24A14,#FF9F1C,#E24A14)" },
  { id: "ocean", label: "Ocean", css: "linear-gradient(135deg,#0B2E59,#0E6BA8,#2EC4B6,#0E6BA8)" },
  { id: "gold", label: "Gold", css: "linear-gradient(135deg,#8A6D1F,#F6E27A,#B8860B,#F6E27A)" },
  { id: "silver", label: "Silver", css: "linear-gradient(135deg,#7D7D7D,#F2F2F2,#A0A0A0,#EDEDED)" },
  { id: "rose-gold", label: "Rose gold", css: "linear-gradient(135deg,#9E5A63,#F4C2C2,#C08081,#F4C2C2)" },
  { id: "bronze", label: "Bronze", css: "linear-gradient(135deg,#6E4A1E,#D9A066,#8C5A2B,#D9A066)" },
];

/** OneNote's rule and grid line spacings (CSS px); "text" lines up with the note's own text. */
export const PAGE_LINES: { id: string; label: string; pitch: number; grid?: boolean }[] = [
  { id: "text", label: "Rule lines (match the text)", pitch: 0 },
  { id: "narrow", label: "Narrow rule", pitch: 20 },
  { id: "college", label: "College rule", pitch: 27 },
  { id: "standard", label: "Standard rule", pitch: 33 },
  { id: "wide", label: "Wide rule", pitch: 41 },
  { id: "grid-small", label: "Small grid", pitch: 12, grid: true },
  { id: "grid-medium", label: "Medium grid", pitch: 24, grid: true },
  { id: "grid-large", label: "Large grid", pitch: 48, grid: true },
  { id: "grid-xl", label: "Very large grid", pitch: 96, grid: true },
];
/** "text-pen": OneNote's Ink to Text pen, writing that turns into typed text when you pause. */
/**
 * Also OneNote's Ink Editor pen ("editor-pen"), Insert space ("space"), the laser pointer, and the
 * shape gallery ("shape:rectangle"...).
 */
export type Tool = "slot" | "eraser-stroke" | "eraser-point" | "lasso" | "text-pen" | "editor-pen" | "space" | "laser" | `shape:${string}`;

/** One pen in the toolbar's pen gallery. Sizes are in millimetres, like OneNote's thickness menu. */
export interface PenSlot {
  kind: PenKind;
  color: string;
  size: number;
  /** An effect pen (rainbow, galaxy...): drawn with that instead of `color`. */
  effect?: string;
}

export interface InkSettings {
  slots: PenSlot[];
  active: number;
  tool: Tool;
  pressure: boolean;
  smooth: boolean;
  scratchOut: boolean;
  /** Draw a shape and hold the pen still to turn it into a clean shape. */
  shapes: boolean;
  eraserEnd: "stroke" | "point";
  eraserSize: number;
  showToolbar: boolean;
  recent: string[];
  /** Eraser the eraser button picks up when tapped from another tool. */
  lastEraser: "eraser-stroke" | "eraser-point";
  /** Notebook-style rule lines behind text and ink (from before there were spacings and grids). */
  ruled: boolean;
  /** Rule or grid lines: "none", "text", or one of PAGE_LINES. */
  pageLines: string;
  /** The ruler is out. */
  ruler: boolean;
  /** The shape the Shapes button draws. */
  shape: string;
  /** OneNote section the notes go to (created in the current notebook if missing). */
  oneNoteSection: string;
  /** Near-white ink becomes black in OneNote (OneNote shows black ink white in dark mode). */
  oneNoteWhiteToBlack: boolean;
  /** "auto": keep OneNote pages in sync in the background both ways; "off": only when you send/pull. */
  oneNoteSync: "auto" | "off";
  /** How often to check OneNote for edits (seconds). */
  oneNotePollSeconds: number;
  /** Which notes sync: those sent to OneNote at least once, or every note you open. */
  oneNoteScope: "sent" | "opened";
  /** Ask before applying text edited in OneNote (otherwise apply and show a notice). */
  oneNoteConfirmText: boolean;
  /**
   * Note path -> its OneNote page as the last send left it (page ID, which outline each block became,
   * ink fingerprint), so OneNote edits can be told apart and pulled. A bare string is a page ID from
   * before two-way sync.
   */
  oneNotePages: Record<string, string | import("./onenote").SyncState>;
  /** Math Assistant: angle unit for trig, and whether to show complex solutions. */
  mathAngle: "rad" | "deg" | "grad";
  /** Draw ink with the GPU (smoother on some machines, ~100 MB more memory); off: software rendering. */
  gpuRendering: boolean;
  mathNumbers: "real" | "complex";
  /** Notes (and PDFs, pictures) in these folders, and their subfolders, are ignored (see Ignored). */
  ignoreFolders: string[];
  /** Notes with any of these tags (or a nested tag under one) are ignored. Without the #. */
  ignoreTags: string[];
  /**
   * Frontmatter property that ignores a note: true (what's hidden is `ignoreHides`), a list of what to
   * hide ([lines], [toolbar, ink]...), or false to keep everything on even in an ignored folder or tag.
   */
  ignoreProperty: string;
  /** What ignored notes go without. */
  ignoreHides: Ignored;
}

/** What a note goes without: rule lines, the toolbar, the ink layer itself. */
export interface Ignored {
  lines: boolean;
  toolbar: boolean;
  ink: boolean;
}

export const NOT_IGNORED: Ignored = { lines: false, toolbar: false, ink: false };

export const DEFAULT_SLOTS: PenSlot[] = [
  { kind: "pen", color: "#000000", size: 0.5 },
  { kind: "pen", color: "#1F5FD6", size: 0.5 },
  { kind: "pen", color: "#E71224", size: 0.5 },
  { kind: "highlighter", color: "#FFF100", size: 5 },
];

export const DEFAULT_SETTINGS: InkSettings = {
  slots: DEFAULT_SLOTS.map((s) => ({ ...s })),
  active: 0,
  tool: "slot",
  pressure: true,
  smooth: true,
  scratchOut: true,
  shapes: true,
  eraserEnd: "stroke",
  eraserSize: 3,
  showToolbar: true,
  recent: [],
  lastEraser: "eraser-stroke",
  ruled: false,
  pageLines: "none",
  ruler: false,
  shape: "rectangle",
  oneNoteSection: "Obsidian",
  oneNoteWhiteToBlack: true,
  oneNoteSync: "auto",
  oneNotePollSeconds: 30,
  oneNoteScope: "sent",
  oneNoteConfirmText: false,
  oneNotePages: {},
  mathAngle: "rad",
  gpuRendering: false,
  mathNumbers: "real",
  ignoreFolders: [],
  ignoreTags: [],
  ignoreProperty: "ink-ignore",
  ignoreHides: { lines: true, toolbar: true, ink: false },
};

/**
 * What a file goes without, from the ignore rules: its folder, its tags (with `#`, frontmatter ones
 * included) and its ignore property (`undefined` when it has none). The property wins: a list names
 * exactly what to hide, true hides the usual, false keeps everything on.
 */
export function ignoredFor(path: string, tags: string[], property: unknown, s: Pick<InkSettings, "ignoreFolders" | "ignoreTags" | "ignoreHides">): Ignored {
  if (property === false || property === "false" || property === "no" || property === "off") return NOT_IGNORED;
  const words = Array.isArray(property) ? property.map(String)
    : typeof property === "string" && !["true", "yes", "on", ""].includes(property.trim().toLowerCase()) ? property.split(/[,\s]+/)
    : null;
  if (words) {
    const w = new Set(words.map((x) => x.trim().toLowerCase().replace(/^no-/, "")));
    if (w.has("all")) return { lines: true, toolbar: true, ink: true };
    return { lines: w.has("lines"), toolbar: w.has("toolbar"), ink: w.has("ink") };
  }
  if (property === true || typeof property === "string") return { ...s.ignoreHides };
  const folder = s.ignoreFolders.map((f) => f.trim().replace(/^\/+|\/+$/g, "")).filter(Boolean)
    .some((f) => path === f || path.startsWith(f + "/"));
  const wanted = s.ignoreTags.map((t) => t.trim().replace(/^#/, "").toLowerCase()).filter(Boolean);
  const tagged = tags.some((t) => {
    const tag = t.replace(/^#/, "").toLowerCase();
    return wanted.some((w) => tag === w || tag.startsWith(w + "/"));
  });
  return folder || tagged ? { ...s.ignoreHides } : NOT_IGNORED;
}

export const MAX_SLOTS = 8;
export const MAX_RECENT = 10;

/** Thickness range per pen kind, in mm. The slider is logarithmic across it. */
export const SIZE_RANGE: Record<PenKind, [number, number]> = {
  pen: [0.1, 12],
  pencil: [0.1, 12],
  highlighter: [1, 20],
};

/** OneNote-style point eraser thicknesses, in mm. */
export const ERASER_SIZES: { label: string; mm: number }[] = [
  { label: "Small", mm: 1.5 },
  { label: "Medium", mm: 3 },
  { label: "Large", mm: 6 },
  { label: "Extra large", mm: 12 },
];

/** Quick swatches shown under the wheel. */
export const SWATCHES = [
  "#000000", "#5C5C5C", "#A6A6A6", "#FFFFFF",
  "#E71224", "#F7630C", "#FFC114", "#FFF100",
  "#16C60C", "#0F7B0F", "#00B7C3", "#1F5FD6",
  "#3B2CC9", "#8E2FC8", "#E3008C", "#8B4513",
];

export const mmToPx = (mm: number) => (mm * 96) / 25.4;

export const isDarkTheme = () => document.body.classList.contains("theme-dark");

/**
 * How a pen colour looks on the page. In dark mode dark ink (black, dark greys) is shown light, like
 * OneNote; light ink stays as chosen. Must match IsDarkInk in the helper.
 */
export function shownColor(hex: string): string {
  const m = /^#([0-9a-f]{6})$/i.exec(hex);
  if (!m || !isDarkTheme()) return hex;
  const n = parseInt(m[1], 16);
  const r = (n >> 16) & 255, g = (n >> 8) & 255, b = n & 255;
  if (Math.max(r, g, b) - Math.min(r, g, b) > 12 || Math.max(r, g, b) >= 96) return hex;
  return "#" + [r, g, b].map((v) => (255 - v).toString(16).padStart(2, "0")).join("").toUpperCase();
}
