/**
 * Graphs, like Math Assistant's "Graph in 2D": a live plot in the note (a ```math-graph block, one curve
 * per line) that you can pan and zoom, with checkboxes for key points (zeros, extrema, intercepts,
 * intersections) and a slider for each parameter (where Math Assistant has +/− buttons).
 *
 *   y = x^2 + a*x + b      a function of x
 *   x^2 + y^2 = 4          any equation in x and y
 *   r = 1 + cos(6*theta)   polar
 *   x + y < 5              inequalities shade where they hold
 *   a = 1                  a parameter (slider)
 *   angle: deg             trig in degrees
 */
import { setIcon } from "obsidian";
import { compile, nerdamer } from "./cas";

type Rel = "=" | "<" | ">" | "<=" | ">=";

export interface Curve {
  source: string;
  kind: "fx" | "fy" | "polar" | "implicit" | "region";
  /** fx: f(x); fy: g(y); polar: r(θ); implicit/region: lhs − rhs. */
  expr: string;
  rel?: Rel;
}

export interface GraphSpec {
  curves: Curve[];
  params: { name: string; value: number; min: number; max: number }[];
  angle: "rad" | "deg" | "grad";
  window?: [number, number, number, number];
  errors: string[];
}

const PLOT_VARS = new Set(["x", "y", "r", "theta"]);
const CONSTANTS = new Set(["pi", "e", "i", "Infinity"]);

function relation(s: string): { lhs: string; rel: Rel; rhs: string } | null {
  let depth = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === "(") depth++;
    else if (c === ")") depth--;
    else if (depth === 0) {
      const two = s.slice(i, i + 2);
      if (two === "<=" || two === ">=") return { lhs: s.slice(0, i).trim(), rel: two, rhs: s.slice(i + 2).trim() };
      if (c === "=" || c === "<" || c === ">") return { lhs: s.slice(0, i).trim(), rel: c, rhs: s.slice(i + 1).trim() };
    }
  }
  return null;
}

const mentions = (s: string, v: string) => new RegExp(`\\b${v}\\b`).test(s);

export function parseGraph(source: string): GraphSpec {
  const spec: GraphSpec = { curves: [], params: [], angle: "rad", errors: [] };
  for (const raw of source.split("\n")) {
    const line = raw.replace(/#.*/, "").trim();
    if (!line) continue;
    const option = /^(angle|window)\s*:\s*(.+)$/i.exec(line);
    if (option) {
      if (option[1].toLowerCase() === "angle" && /^(rad|deg|grad)/i.test(option[2])) spec.angle = option[2].toLowerCase().slice(0, option[2].toLowerCase().startsWith("grad") ? 4 : 3) as GraphSpec["angle"];
      if (option[1].toLowerCase() === "window") {
        const w = option[2].split(/[ ,]+/).map(Number);
        if (w.length === 4 && w.every(isFinite) && w[0] < w[1] && w[2] < w[3]) spec.window = w as GraphSpec["window"];
      }
      continue;
    }
    const param = /^([A-Za-z]\w*)\s*=\s*(-?\d+(?:\.\d+)?)(?:\s*\(\s*(-?\d+(?:\.\d+)?)\s*(?:\.\.|to|,)\s*(-?\d+(?:\.\d+)?)\s*\))?$/.exec(line);
    if (param && !PLOT_VARS.has(param[1])) {
      const value = Number(param[2]);
      const span = Math.max(10, Math.abs(value) * 2);
      spec.params.push({ name: param[1], value, min: param[3] ? Number(param[3]) : -span, max: param[4] ? Number(param[4]) : span });
      continue;
    }
    const r = relation(line);
    if (!r) { spec.curves.push({ source: line, kind: "fx", expr: line }); continue; }
    const { lhs, rel, rhs } = r;
    if (rel === "=") {
      if (lhs === "y" && !mentions(rhs, "y")) spec.curves.push({ source: line, kind: "fx", expr: rhs });
      else if (lhs === "x" && !mentions(rhs, "x") && mentions(rhs, "y")) spec.curves.push({ source: line, kind: "fy", expr: rhs });
      else if (lhs === "r" && !mentions(rhs, "r")) spec.curves.push({ source: line, kind: "polar", expr: rhs });
      else spec.curves.push({ source: line, kind: "implicit", expr: `(${lhs})-(${rhs})` });
    } else {
      spec.curves.push({ source: line, kind: "region", expr: `(${lhs})-(${rhs})`, rel });
    }
  }
  // Letters other than x, y, r, θ without a value become parameters (at 1) so the curve still draws.
  const known = new Set(spec.params.map((p) => p.name));
  for (const c of spec.curves) {
    for (const m of c.expr.matchAll(/[A-Za-z_]\w*/g)) {
      const name = m[0], next = c.expr[(m.index ?? 0) + name.length];
      if (next === "(" || PLOT_VARS.has(name) || CONSTANTS.has(name) || known.has(name)) continue;
      known.add(name);
      spec.params.push({ name, value: 1, min: -10, max: 10 });
    }
  }
  return spec;
}

/** Degrees: trig takes degrees. */
function inDegrees(expr: string, angle: GraphSpec["angle"]): string {
  if (angle === "rad") return expr;
  const unit = angle === "deg" ? "(pi/180)" : "(pi/200)";
  return expr.replace(/\b(sin|cos|tan|cot|sec|csc)\(/g, `$1(${unit}*`);
}

// ---------- key points ----------

export interface KeyPoint { x: number; y: number; label: string; kind: "zero" | "extremum" | "intercept" | "intersection" }

function refine(f: (x: number) => number, a: number, b: number): number {
  let fa = f(a);
  const fb = f(b);
  // Not bracketed (the zero is on an end, give or take rounding): the end nearer zero.
  if (fa * fb > 0) return Math.abs(fa) < Math.abs(fb) ? a : b;
  for (let k = 0; k < 60; k++) {
    const m = (a + b) / 2, fm = f(m);
    if (!isFinite(fm)) return NaN;
    if (fa * fm <= 0) b = m; else { a = m; fa = fm; }
  }
  return (a + b) / 2;
}

/** Zeros, extrema, y-intercept of y = f(x) over [lo, hi]; intersections with other functions. */
export function keyPoints(fs: ((x: number) => number)[], lo: number, hi: number, show: Set<KeyPoint["kind"]>): KeyPoint[] {
  const pts: KeyPoint[] = [];
  const n = 800, step = (hi - lo) / n;
  const fmt = (v: number) => String(Number(Math.abs(v) < 1e-10 ? 0 : v.toPrecision(4)));
  const add = (x: number, y: number, kind: KeyPoint["kind"], what: string) => {
    if (!isFinite(x) || !isFinite(y) || pts.some((p) => Math.abs(p.x - x) < step && Math.abs(p.y - y) < step)) return;
    pts.push({ x, y, kind, label: `${what} (${fmt(x)}, ${fmt(y)})` });
  };
  fs.forEach((f, ci) => {
    if (show.has("intercept")) { const y0 = f(0); if (isFinite(y0) && lo <= 0 && 0 <= hi) add(0, y0, "intercept", "y-intercept"); }
    let x0 = lo, y0 = f(lo);
    let d0 = (f(lo + step / 2) - f(lo - step / 2)) / step;
    for (let i = 1; i <= n; i++) {
      const x1 = lo + i * step, y1 = f(x1);
      if (show.has("zero") && isFinite(y0) && isFinite(y1)) {
        if (y0 === 0) add(x0, 0, "zero", "Zero");
        else if (y0 * y1 < 0 && Math.abs(y1 - y0) < (hi - lo)) { const z = refine(f, x0, x1); if (Math.abs(f(z)) < 1e-6 * Math.max(1, Math.abs(y0))) add(z, 0, "zero", "Zero"); }
      }
      if (show.has("extremum")) {
        const d1 = (f(x1 + step / 2) - f(x1 - step / 2)) / step;
        if (isFinite(d0) && isFinite(d1) && d0 * d1 < 0 && isFinite(y1)) {
          const df = (x: number) => (f(x + 1e-6) - f(x - 1e-6)) / 2e-6;
          const xe = refine(df, x1 - step, x1);
          const ye = f(xe);
          if (isFinite(ye) && Math.abs(d1 - d0) < 1e6) add(xe, ye, "extremum", d0 > 0 ? "Maximum" : "Minimum");
        }
        d0 = d1;
      }
      x0 = x1; y0 = y1;
    }
    if (show.has("intersection")) {
      for (let cj = ci + 1; cj < fs.length; cj++) {
        const g = (x: number) => f(x) - fs[cj](x);
        let a = lo, ga = g(lo);
        for (let i = 1; i <= n; i++) {
          const b = lo + i * step, gb = g(b);
          if (isFinite(ga) && isFinite(gb) && ga * gb <= 0 && ga !== gb) { const z = refine(g, a, b); add(z, f(z), "intersection", "Intersection"); }
          a = b; ga = gb;
        }
      }
    }
  });
  return pts;
}

// ---------- the view ----------

const FEATURES: { kind: KeyPoint["kind"]; label: string }[] = [
  { kind: "zero", label: "Zeros" },
  { kind: "extremum", label: "Minimum/maximum" },
  { kind: "intercept", label: "y-intercept" },
  { kind: "intersection", label: "Intersections" },
];

const PALETTE = ["--color-blue", "--color-red", "--color-green", "--color-orange", "--color-purple", "--color-cyan", "--color-pink"];

export class GraphView {
  readonly el: HTMLElement;
  private canvas: HTMLCanvasElement;
  private tip: HTMLElement;
  private cx = 0;
  private cy = 0;
  /** Math units per CSS px. */
  private scale = 20 / 600;
  private width = 600;
  private height: number;
  private show = new Set<KeyPoint["kind"]>();
  private values: Record<string, number> = {};
  private fns: { curve: Curve; f: (...a: number[]) => number; color: string }[] = [];
  private points: KeyPoint[] = [];
  private observer: ResizeObserver;
  private home: { cx: number; cy: number; scale: number } | null = null;

  constructor(parent: HTMLElement, private spec: GraphSpec, opts: { height?: number } = {}) {
    this.height = opts.height ?? 320;
    this.el = parent.createDiv({ cls: "oni-graph" });
    const stage = this.el.createDiv({ cls: "oni-graph-stage" });
    this.canvas = stage.createEl("canvas");
    this.tip = stage.createDiv({ cls: "oni-graph-tip" });
    const zoom = stage.createDiv({ cls: "oni-graph-zoom" });
    for (const [icon, label, act] of [["plus", "Zoom in", () => this.zoom(1 / 1.5)], ["minus", "Zoom out", () => this.zoom(1.5)], ["rotate-ccw", "Reset", () => this.reset()]] as const) {
      const b = zoom.createDiv({ cls: "clickable-icon", attr: { "aria-label": label, role: "button" } });
      setIcon(b, icon);
      b.addEventListener("click", act);
    }

    for (const p of spec.params) this.values[p.name] = p.value;
    this.compileAll();

    const controls = this.el.createDiv({ cls: "oni-graph-controls" });
    const legend = controls.createDiv({ cls: "oni-graph-legend" });
    for (const fn of this.fns) {
      const item = legend.createDiv({ cls: "oni-graph-legend-item" });
      item.createSpan({ cls: "oni-graph-swatch" }).style.backgroundColor = fn.color;
      item.createSpan({ text: fn.curve.source });
    }
    for (const err of spec.errors) legend.createDiv({ cls: "oni-graph-error", text: err });
    if (this.fns.some((f) => f.curve.kind === "fx")) {
      const feats = controls.createDiv({ cls: "oni-graph-features" });
      for (const feat of FEATURES) {
        if (feat.kind === "intersection" && this.fns.filter((f) => f.curve.kind === "fx").length < 2) continue;
        const label = feats.createEl("label", { cls: "oni-graph-check" });
        const box = label.createEl("input", { type: "checkbox" });
        label.appendText(feat.label);
        box.addEventListener("change", () => { box.checked ? this.show.add(feat.kind) : this.show.delete(feat.kind); this.draw(); });
      }
    }
    for (const p of spec.params) {
      const row = controls.createDiv({ cls: "oni-graph-param" });
      row.createSpan({ cls: "oni-graph-param-name", text: p.name });
      const slider = row.createEl("input", { cls: "slider", type: "range" });
      slider.min = String(p.min);
      slider.max = String(p.max);
      slider.step = String((p.max - p.min) / 200);
      slider.value = String(p.value);
      const value = row.createSpan({ cls: "oni-graph-param-value", text: String(p.value) });
      slider.addEventListener("input", () => {
        this.values[p.name] = Number(slider.value);
        value.setText(String(Number(Number(slider.value).toPrecision(3))));
        this.draw();
      });
    }

    this.fitView();
    this.bindInput();
    this.observer = new ResizeObserver(() => this.resize());
    this.observer.observe(this.el);
    this.resize();
  }

  destroy() {
    this.observer.disconnect();
  }

  private compileAll() {
    const style = getComputedStyle(document.body);
    const params = this.spec.params.map((p) => p.name);
    this.spec.curves.forEach((curve, i) => {
      const expr = inDegrees(curve.expr, this.spec.angle);
      const vars = curve.kind === "fx" ? ["x"] : curve.kind === "fy" ? ["y"] : curve.kind === "polar" ? ["theta"] : ["x", "y"];
      try {
        nerdamer(expr); // parse check
        const f = compile(expr, [...vars, ...params]);
        const color = style.getPropertyValue(PALETTE[i % PALETTE.length]).trim() || ["#3b82f6", "#ef4444", "#22c55e", "#f97316", "#a855f7"][i % 5];
        this.fns.push({ curve, f, color });
      } catch (e) {
        this.spec.errors.push(`Can't graph "${curve.source}": ${e instanceof Error ? e.message : e}`);
      }
    });
  }

  private args(...a: number[]) {
    return [...a, ...this.spec.params.map((p) => this.values[p.name])];
  }

  /** Start on the window given, else ±10 around the origin, moved up/down to where a function of x is. */
  private fitView() {
    if (this.spec.window) {
      const [x0, x1, y0, y1] = this.spec.window;
      this.cx = (x0 + x1) / 2;
      this.cy = (y0 + y1) / 2;
      this.scale = (x1 - x0) / this.width;
    } else {
      this.cx = 0;
      this.cy = 0;
      this.scale = 20 / this.width;
      const fx = this.fns.filter((f) => f.curve.kind === "fx");
      if (fx.length) {
        const ys: number[] = [];
        for (const fn of fx) for (let x = -10; x <= 10; x += 0.25) { const y = fn.f(...this.args(x)); if (isFinite(y)) ys.push(y); }
        // Stay on the origin if the curve passes through that view at all; otherwise move to where it is.
        const halfH = (this.height * this.scale) / 2;
        if (ys.length && !ys.some((y) => Math.abs(y) < halfH * 0.9)) {
          ys.sort((a, b) => Math.abs(a) - Math.abs(b));
          this.cy = ys[0];
        }
      }
    }
    this.home = { cx: this.cx, cy: this.cy, scale: this.scale };
  }

  private reset() {
    if (this.home) ({ cx: this.cx, cy: this.cy, scale: this.scale } = this.home);
    this.draw();
  }

  private zoom(factor: number, atX = this.width / 2, atY = this.height / 2) {
    const [mx, my] = this.toMath(atX, atY);
    this.scale = Math.min(1e4, Math.max(1e-6, this.scale * factor));
    this.cx = mx - (atX - this.width / 2) * this.scale;
    this.cy = my + (atY - this.height / 2) * this.scale;
    this.draw();
  }

  private resize() {
    const w = Math.max(200, Math.floor(this.el.clientWidth));
    if (w === this.width && this.canvas.width) return;
    // Keep the same span of x when the width changes.
    if (this.width) this.scale = (this.scale * this.width) / w;
    this.width = w;
    if (this.home && !this.spec.window) this.home.scale = 20 / w;
    const dpr = window.devicePixelRatio || 1;
    this.canvas.width = Math.round(w * dpr);
    this.canvas.height = Math.round(this.height * dpr);
    this.canvas.style.width = `${w}px`;
    this.canvas.style.height = `${this.height}px`;
    this.draw();
  }

  private toMath(px: number, py: number): [number, number] {
    return [this.cx + (px - this.width / 2) * this.scale, this.cy - (py - this.height / 2) * this.scale];
  }

  private toPx(x: number, y: number): [number, number] {
    return [(x - this.cx) / this.scale + this.width / 2, -(y - this.cy) / this.scale + this.height / 2];
  }

  private bindInput() {
    const pointers = new Map<number, { x: number; y: number }>();
    let pinch = 0;
    const c = this.canvas;
    c.style.touchAction = "none";
    c.addEventListener("pointerdown", (e) => {
      c.setPointerCapture(e.pointerId);
      pointers.set(e.pointerId, { x: e.offsetX, y: e.offsetY });
      if (pointers.size === 2) { const [a, b] = [...pointers.values()]; pinch = Math.hypot(a.x - b.x, a.y - b.y); }
    });
    c.addEventListener("pointermove", (e) => {
      const prev = pointers.get(e.pointerId);
      if (!prev) { this.hover(e.offsetX, e.offsetY); return; }
      if (pointers.size === 1) {
        this.cx -= (e.offsetX - prev.x) * this.scale;
        this.cy += (e.offsetY - prev.y) * this.scale;
        pointers.set(e.pointerId, { x: e.offsetX, y: e.offsetY });
        this.draw();
      } else if (pointers.size === 2) {
        pointers.set(e.pointerId, { x: e.offsetX, y: e.offsetY });
        const [a, b] = [...pointers.values()];
        const d = Math.hypot(a.x - b.x, a.y - b.y);
        if (pinch > 0 && d > 0) this.zoom(pinch / d, (a.x + b.x) / 2, (a.y + b.y) / 2);
        pinch = d;
      }
    });
    const up = (e: PointerEvent) => { pointers.delete(e.pointerId); pinch = 0; };
    c.addEventListener("pointerup", up);
    c.addEventListener("pointercancel", up);
    c.addEventListener("pointerleave", () => this.tip.hide());
    c.addEventListener("wheel", (e) => {
      e.preventDefault();
      this.zoom(Math.exp(e.deltaY * 0.0015), e.offsetX, e.offsetY);
    }, { passive: false });
  }

  /** The value under the pointer on the nearest function of x (or a key point's label). */
  private hover(px: number, py: number) {
    const near = this.points.find((p) => { const [x, y] = this.toPx(p.x, p.y); return Math.hypot(x - px, y - py) < 8; });
    let text = near?.label;
    if (!text) {
      const [mx] = this.toMath(px, py);
      let best = Infinity;
      for (const fn of this.fns) {
        if (fn.curve.kind !== "fx") continue;
        const y = fn.f(...this.args(mx));
        if (!isFinite(y)) continue;
        const d = Math.abs(this.toPx(mx, y)[1] - py);
        if (d < 12 && d < best) { best = d; text = `(${Number(mx.toPrecision(4))}, ${Number(y.toPrecision(4))})`; }
      }
    }
    if (!text) { this.tip.hide(); return; }
    this.tip.setText(text);
    this.tip.show();
    this.tip.style.left = `${Math.min(px + 12, this.width - 140)}px`;
    this.tip.style.top = `${Math.max(py - 28, 4)}px`;
  }

  draw() {
    const ctx = this.canvas.getContext("2d");
    if (!ctx) return;
    const dpr = window.devicePixelRatio || 1;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    const W = this.width, H = this.height;
    const style = getComputedStyle(this.el);
    const css = (name: string, fallback: string) => style.getPropertyValue(name).trim() || fallback;
    ctx.clearRect(0, 0, W, H);
    ctx.fillStyle = css("--background-primary", "#fff");
    ctx.fillRect(0, 0, W, H);

    // Grid and axes, at a "nice" spacing (1, 2, 5 × 10^n).
    const [x0, y1] = this.toMath(0, 0);
    const [x1, y0] = this.toMath(W, H);
    const raw = this.scale * 80;
    const pow = Math.pow(10, Math.floor(Math.log10(raw)));
    const step = [1, 2, 5, 10].map((m) => m * pow).find((s) => s >= raw) ?? raw;
    ctx.lineWidth = 1;
    ctx.strokeStyle = css("--background-modifier-border", "#ddd");
    ctx.beginPath();
    for (let gx = Math.ceil(x0 / step) * step; gx <= x1; gx += step) { const [px] = this.toPx(gx, 0); ctx.moveTo(Math.round(px) + 0.5, 0); ctx.lineTo(Math.round(px) + 0.5, H); }
    for (let gy = Math.ceil(y0 / step) * step; gy <= y1; gy += step) { const [, py] = this.toPx(0, gy); ctx.moveTo(0, Math.round(py) + 0.5); ctx.lineTo(W, Math.round(py) + 0.5); }
    ctx.stroke();
    const [ax, ay] = this.toPx(0, 0);
    ctx.strokeStyle = css("--text-muted", "#666");
    ctx.beginPath();
    if (ax >= 0 && ax <= W) { ctx.moveTo(Math.round(ax) + 0.5, 0); ctx.lineTo(Math.round(ax) + 0.5, H); }
    if (ay >= 0 && ay <= H) { ctx.moveTo(0, Math.round(ay) + 0.5); ctx.lineTo(W, Math.round(ay) + 0.5); }
    ctx.stroke();
    ctx.fillStyle = css("--text-faint", "#999");
    ctx.font = `11px ${css("--font-interface", "sans-serif")}`;
    const label = (v: number) => String(Number(v.toPrecision(6)));
    const lx = Math.min(Math.max(ax, 2), W - 30), ly = Math.min(Math.max(ay, 0), H - 14);
    ctx.textBaseline = "top";
    for (let gx = Math.ceil(x0 / step) * step; gx <= x1; gx += step) if (Math.abs(gx) > step / 2) { const [px] = this.toPx(gx, 0); ctx.fillText(label(gx), px + 2, ly + 2); }
    ctx.textBaseline = "middle";
    for (let gy = Math.ceil(y0 / step) * step; gy <= y1; gy += step) if (Math.abs(gy) > step / 2) { const [, py] = this.toPx(0, gy); ctx.fillText(label(gy), lx + 4, py); }

    // Shaded regions first, curves on top.
    for (const fn of this.fns) if (fn.curve.kind === "region") this.drawRegion(ctx, fn);
    for (const fn of this.fns) {
      ctx.strokeStyle = fn.color;
      ctx.lineWidth = 2;
      ctx.setLineDash([]);
      if (fn.curve.kind === "fx") this.drawFx(ctx, (x) => fn.f(...this.args(x)), false);
      else if (fn.curve.kind === "fy") this.drawFx(ctx, (y) => fn.f(...this.args(y)), true);
      else if (fn.curve.kind === "polar") this.drawPolar(ctx, fn);
      else if (fn.curve.kind === "implicit") this.drawImplicit(ctx, (x, y) => fn.f(...this.args(x, y)));
    }

    // Key points.
    const fx = this.fns.filter((f) => f.curve.kind === "fx").map((fn) => (x: number) => fn.f(...this.args(x)));
    this.points = this.show.size && fx.length ? keyPoints(fx, x0, x1, this.show) : [];
    for (const p of this.points) {
      const [px, py] = this.toPx(p.x, p.y);
      ctx.fillStyle = css("--text-normal", "#222");
      ctx.beginPath();
      ctx.arc(px, py, 4, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = css("--text-muted", "#555");
      ctx.textBaseline = "bottom";
      ctx.fillText(`(${Number(p.x.toPrecision(3))}, ${Number(p.y.toPrecision(3))})`, px + 6, py - 4);
    }
  }

  /** A function, sampled twice per pixel; lifts the pen across gaps and asymptotes. */
  private drawFx(ctx: CanvasRenderingContext2D, f: (t: number) => number, swap: boolean) {
    const n = (swap ? this.height : this.width) * 2;
    ctx.beginPath();
    let pen = false, prev = NaN;
    for (let i = 0; i <= n; i++) {
      const p = i / 2;
      const t = swap ? this.toMath(0, p)[1] : this.toMath(p, 0)[0];
      const v = f(t);
      if (!isFinite(v)) { pen = false; prev = NaN; continue; }
      const [px, py] = swap ? this.toPx(v, t) : this.toPx(t, v);
      const q = swap ? px : py;
      const limit = (swap ? this.width : this.height) * 4;
      // A jump across most of the screen with a sign flip is an asymptote, not part of the curve.
      if (pen && Math.abs(q - prev) > (swap ? this.width : this.height) && Math.abs(q) < limit) pen = false;
      const cq = Math.max(-limit, Math.min(limit, q));
      const [dx, dy] = swap ? [cq, p] : [p, cq];
      if (pen) ctx.lineTo(dx, dy); else ctx.moveTo(dx, dy);
      pen = true;
      prev = q;
    }
    ctx.stroke();
  }

  private drawPolar(ctx: CanvasRenderingContext2D, fn: { f: (...a: number[]) => number }) {
    const full = this.spec.angle === "deg" ? 360 : this.spec.angle === "grad" ? 400 : 2 * Math.PI;
    const n = 3000;
    ctx.beginPath();
    let pen = false;
    for (let i = 0; i <= n; i++) {
      const t = (i / n) * full;
      const r = fn.f(...this.args(t));
      if (!isFinite(r)) { pen = false; continue; }
      const rad = this.spec.angle === "deg" ? (t * Math.PI) / 180 : this.spec.angle === "grad" ? (t * Math.PI) / 200 : t;
      const [px, py] = this.toPx(r * Math.cos(rad), r * Math.sin(rad));
      if (pen) ctx.lineTo(px, py); else ctx.moveTo(px, py);
      pen = true;
    }
    ctx.stroke();
  }

  /** F(x, y) = 0 by marching squares on a 3 px grid. */
  private drawImplicit(ctx: CanvasRenderingContext2D, F: (x: number, y: number) => number) {
    const cell = 3;
    const cols = Math.ceil(this.width / cell) + 1, rows = Math.ceil(this.height / cell) + 1;
    const v = new Float64Array(cols * rows);
    for (let j = 0; j < rows; j++) for (let i = 0; i < cols; i++) { const [x, y] = this.toMath(i * cell, j * cell); v[j * cols + i] = F(x, y); }
    ctx.beginPath();
    const edge = (a: number, b: number, pa: [number, number], pb: [number, number]): [number, number] => {
      const t = a / (a - b);
      return [pa[0] + (pb[0] - pa[0]) * t, pa[1] + (pb[1] - pa[1]) * t];
    };
    for (let j = 0; j < rows - 1; j++) {
      for (let i = 0; i < cols - 1; i++) {
        const a = v[j * cols + i], b = v[j * cols + i + 1], c = v[(j + 1) * cols + i + 1], d = v[(j + 1) * cols + i];
        if (![a, b, c, d].every(isFinite)) continue;
        const pa: [number, number] = [i * cell, j * cell], pb: [number, number] = [(i + 1) * cell, j * cell];
        const pc: [number, number] = [(i + 1) * cell, (j + 1) * cell], pd: [number, number] = [i * cell, (j + 1) * cell];
        const cross: [number, number][] = [];
        if ((a > 0) !== (b > 0)) cross.push(edge(a, b, pa, pb));
        if ((b > 0) !== (c > 0)) cross.push(edge(b, c, pb, pc));
        if ((c > 0) !== (d > 0)) cross.push(edge(c, d, pc, pd));
        if ((d > 0) !== (a > 0)) cross.push(edge(d, a, pd, pa));
        // Sign flips through a pole (1/x) jump hugely; a real curve passes near zero.
        const big = Math.max(Math.abs(a), Math.abs(b), Math.abs(c), Math.abs(d));
        const small = Math.min(Math.abs(a), Math.abs(b), Math.abs(c), Math.abs(d));
        if (big > 1e3 * Math.max(small, 1e-9) && big > 1e6 * this.scale) continue;
        for (let k = 0; k + 1 < cross.length; k += 2) { ctx.moveTo(...cross[k]); ctx.lineTo(...cross[k + 1]); }
      }
    }
    ctx.stroke();
  }

  /** An inequality: shade where it holds; the boundary solid if it's included, dashed if not. */
  private drawRegion(ctx: CanvasRenderingContext2D, fn: { curve: Curve; f: (...a: number[]) => number; color: string }) {
    const rel = fn.curve.rel!;
    const holds = (v: number) => (rel === "<" ? v < 0 : rel === "<=" ? v <= 0 : rel === ">" ? v > 0 : v >= 0);
    const cell = 4;
    ctx.fillStyle = fn.color;
    ctx.globalAlpha = 0.16;
    for (let py = 0; py < this.height; py += cell) {
      for (let px = 0; px < this.width; px += cell) {
        const [x, y] = this.toMath(px + cell / 2, py + cell / 2);
        if (holds(fn.f(...this.args(x, y)))) ctx.fillRect(px, py, cell, cell);
      }
    }
    ctx.globalAlpha = 1;
    ctx.strokeStyle = fn.color;
    ctx.lineWidth = 2;
    ctx.setLineDash(rel === "<" || rel === ">" ? [6, 4] : []);
    this.drawImplicit(ctx, (x, y) => fn.f(...this.args(x, y)));
    ctx.setLineDash([]);
  }
}
