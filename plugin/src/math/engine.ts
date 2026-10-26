/**
 * The Math Assistant's brain: what a recognized (or typed) problem is, what can be done with it, and
 * doing it — exactly where possible (nerdamer, a symbolic algebra library), with worked steps for the
 * kinds of problems people solve by hand, and numbers checked by substituting answers back in.
 * No Obsidian here, so it runs in tests as is.
 */
import { compile, formatNumber, nerdamer, numeric, splitTop, toTeX, writtenTeX } from "./cas";
import { Step, stepsDerivative, stepsIntegral, stepsLinear, stepsLinearSystem, stepsQuadratic } from "./steps";

export { formatNumber, nerdamer, numeric, splitTop, toTeX };

export type Kind = "equation" | "inequality" | "system" | "inequalities" | "expression" | "number" | "matrix" | "numbers";
export type Relation = "=" | "<" | ">" | "<=" | ">=";

export interface Part {
  lhs: string;
  rhs: string;
  rel: Relation | null;
}

export interface Problem {
  linear: string;
  latex: string;
  kind: Kind;
  vars: string[];
  parts: Part[];
}

export interface MathSettings {
  angle: "rad" | "deg" | "grad";
  numbers: "real" | "complex";
}

export const DEFAULT_MATH_SETTINGS: MathSettings = { angle: "rad", numbers: "real" };

export interface Action {
  id: string;
  label: string;
  variable?: string;
}

export interface Result {
  /** The answer, as LaTeX. */
  latex: string;
  /** A decimal approximation when the exact answer isn't a plain number. */
  approx?: string;
  steps?: Step[];
  /** Lines for a graph (see graph.ts). */
  graph?: string[];
  /** Answer as Markdown, for putting in the note. */
  markdown: string;
  /** The solutions, in linear form (for solving). */
  values?: string[];
  note?: string;
  error?: string;
  /** Simplify/expand/factor gave back the problem itself: nothing to insert or practise. */
  unchanged?: boolean;
}

export type { Step };

// ---------- reading a problem ----------

const CONSTANTS = new Set(["pi", "e", "i", "Infinity"]);

/** Variables in a linear expression: names that aren't constants or function calls. Most usual first. */
export function variablesOf(s: string): string[] {
  const found = new Set<string>();
  for (const m of s.matchAll(/[A-Za-z_][A-Za-z0-9_]*/g)) {
    const name = m[0];
    const next = s[(m.index ?? 0) + name.length];
    if (next === "(" || CONSTANTS.has(name)) continue;
    found.add(name);
  }
  const order = ["x", "y", "z", "t", "theta", "r", "n"];
  return [...found].sort((a, b) => {
    const ia = order.indexOf(a), ib = order.indexOf(b);
    return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a.localeCompare(b);
  });
}

/** Split at the (first) top-level relation sign. */
export function splitRelation(s: string): Part {
  let depth = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === "(" || c === "[") depth++;
    else if (c === ")" || c === "]") depth--;
    else if (depth === 0) {
      const two = s.slice(i, i + 2);
      if (two === "<=" || two === ">=") return { lhs: s.slice(0, i).trim(), rel: two, rhs: s.slice(i + 2).trim() };
      if (c === "=" || c === "<" || c === ">") return { lhs: s.slice(0, i).trim(), rel: c, rhs: s.slice(i + 1).trim() };
    }
  }
  return { lhs: s.trim(), rhs: "0", rel: null };
}

export function parseProblem(linear: string, latex?: string): Problem {
  linear = linear.trim();
  const lines = linear.split(/\n|;/).map((l) => l.trim()).filter(Boolean);
  const parts = lines.map(splitRelation);
  const vars = variablesOf(linear);
  const tex = latex ?? problemTeX(parts);
  let kind: Kind;
  if (parts.length > 1) kind = parts.every((p) => p.rel === "=") ? "system" : "inequalities";
  else if (parts[0].rel === "=") kind = "equation";
  else if (parts[0].rel) kind = "inequality";
  else if (/^matrix\(/.test(linear)) kind = "matrix";
  else if (splitTop(linear).length > 1 && splitTop(linear).every((p) => isNumber(p))) kind = "numbers";
  else if (!vars.length) kind = "number";
  else kind = "expression";
  return { linear, latex: tex, kind, vars, parts };
}

const isNumber = (s: string) => /^-?\d+(\.\d+)?$/.test(s.trim());

const REL_TEX: Record<Relation, string> = { "=": "=", "<": "<", ">": ">", "<=": "\\le ", ">=": "\\ge " };

/** LaTeX for typed (not handwritten) problems: each side converted, a system as cases. */
export function problemTeX(parts: Part[]): string {
  const one = (p: Part) => (p.rel ? `${writtenTeX(p.lhs)}${REL_TEX[p.rel]}${writtenTeX(p.rhs)}` : writtenTeX(p.lhs));
  return parts.length > 1 ? `\\begin{cases}${parts.map(one).join("\\\\")}\\end{cases}` : one(parts[0]);
}

/** Degree of a polynomial in v, or -1 if it isn't one (sqrt(x), 1/x, sin x...). */
export function polynomialDegree(expr: string, v: string): number {
  try {
    const degree = Number(nerdamer.deg(nerdamer.expand(expr).toString(), v).toString());
    return nerdamer(`diff(${expr},${v},${degree + 1})`).toString() === "0" ? degree : -1;
  } catch {
    return -1;
  }
}

// ---------- what can be done ----------

const CALCULUS = /\b(diff|integrate|defint|limit|sum|product)\(/;

export function actionsFor(p: Problem): Action[] {
  const a: Action[] = [];
  const xy = p.vars.length > 0 && p.vars.every((v) => v === "x" || v === "y");
  const polar = p.vars.includes("r") && p.vars.includes("theta");
  switch (p.kind) {
    case "equation":
      if (polar) { a.push({ id: "graph", label: "Graph in 2D" }); break; }
      if (xy && p.vars.length === 2) {
        a.push({ id: "graph", label: "Graph in 2D" });
        for (const v of p.vars) a.push({ id: "solve", label: `Solve for ${v}`, variable: v });
        break;
      }
      for (const v of p.vars) a.push({ id: "solve", label: `Solve for ${v}`, variable: v });
      if (p.vars.length === 1) {
        a.push({ id: "graph", label: "Graph in 2D" });
        a.push({ id: "graph-both", label: "Graph both sides in 2D" });
      }
      break;
    case "inequality":
      if (p.vars.length === 1) {
        a.push({ id: "solve-inequality", label: `Solve for ${p.vars[0]}`, variable: p.vars[0] });
        a.push({ id: "graph", label: "Graph in 2D" });
        a.push({ id: "graph-both", label: "Graph both sides in 2D" });
      } else if (xy) a.push({ id: "graph", label: "Graph in 2D" });
      break;
    case "system":
      a.push({ id: "solve-system", label: `Solve for ${p.vars.join(", ")}` });
      if (xy) a.push({ id: "graph", label: "Graph in 2D" });
      break;
    case "inequalities":
      if (xy) a.push({ id: "graph", label: "Graph in 2D" });
      break;
    case "expression":
      if (CALCULUS.test(p.linear)) a.push({ id: "evaluate", label: "Evaluate" });
      a.push({ id: "simplify", label: "Simplify" });
      a.push({ id: "expand", label: "Expand" });
      a.push({ id: "factor", label: "Factor" });
      for (const v of p.vars.slice(0, 2)) {
        a.push({ id: "derivative", label: `Find the derivative with respect to ${v}`, variable: v });
        a.push({ id: "integral", label: `Find the integral with respect to ${v}`, variable: v });
      }
      if (p.vars.includes("x") || p.vars.length === 1) a.push({ id: "graph", label: "Graph in 2D" });
      break;
    case "number":
      a.push({ id: "evaluate", label: "Evaluate" });
      if (/^\d+$/.test(p.linear)) a.push({ id: "prime-factors", label: "Find the prime factors" });
      break;
    case "matrix":
      a.push({ id: "determinant", label: "Find the determinant" });
      a.push({ id: "inverse", label: "Find the inverse" });
      a.push({ id: "transpose", label: "Transpose" });
      a.push({ id: "trace", label: "Find the trace" });
      break;
    case "numbers":
      a.push({ id: "statistics", label: "Find the mean, median and more" });
      a.push({ id: "sort", label: "Sort" });
      break;
  }
  return a;
}

// ---------- doing it ----------

export function run(p: Problem, action: Action, settings: MathSettings = DEFAULT_MATH_SETTINGS): Result {
  try {
    switch (action.id) {
      case "solve": return solveEquation(p, action.variable ?? p.vars[0], settings);
      case "solve-inequality": return solveInequality(p, action.variable ?? p.vars[0], settings);
      case "solve-system": return solveSystem(p, settings);
      case "graph": return graph(p, false, settings);
      case "graph-both": return graph(p, true, settings);
      case "evaluate": return evaluate(p, settings);
      case "simplify": {
        // nerdamer's simplify likes to factor (4x+2 → 2(2x+1)); for a plain sum, simplifying means collecting terms.
        const simple = nerdamer.simplify(withAngles(p.linear, settings)).toString();
        return transform(p, "simplify", !/\(/.test(p.linear) && /\(/.test(simple) ? nerdamer.expand(p.linear).toString() : simple);
      }
      case "expand": return transform(p, "expand", nerdamer.expand(p.linear).toString());
      case "factor": return transform(p, "factor", nerdamer.factor(p.linear).toString());
      case "derivative": return derivative(p, action.variable ?? p.vars[0]);
      case "integral": return integral(p, action.variable ?? p.vars[0]);
      case "prime-factors": {
        // pfactor gives "(2^3)*(3^2)*(5)"; written out by hand, since parsing it back would multiply it out.
        const f = nerdamer(`pfactor(${p.linear})`).toString();
        const tex = splitTop(f, "*").map((x) => x.replace(/^\((.*)\)$/, "$1").replace(/\^(\d+)/, "^{$1}")).join("\\cdot ");
        return answer(`${p.linear}=${tex}`, `$${p.linear}=${tex}$`);
      }
      case "determinant": return matrixOp(p, "determinant", "\\det");
      case "inverse": return matrixOp(p, "invert", "", "^{-1}");
      case "transpose": return matrixOp(p, "transpose", "", "^{T}");
      case "trace": {
        const rows = matrixRows(p.linear);
        const diag = rows.map((r, i) => r[i]).filter((x) => x !== undefined);
        const t = nerdamer(diag.join("+")).toString();
        return answer(`\\operatorname{tr}${p.latex}=${toTeX(t)}`, `$\\operatorname{tr}${p.latex}=${toTeX(t)}$`);
      }
      case "statistics": return statistics(p);
      case "sort": {
        const sorted = splitTop(p.linear).map(Number).sort((a, b) => a - b).join(", ");
        return answer(sorted, sorted);
      }
    }
    return failed(`Unknown action ${action.id}`);
  } catch (e) {
    return failed(e instanceof Error ? e.message : String(e));
  }
}

function answer(latex: string, markdown: string, extra: Partial<Result> = {}): Result {
  return { latex, markdown, ...extra };
}

function failed(message: string): Result {
  return { latex: "", markdown: "", error: message };
}

const isPlainNumber = (s: string) => /^-?\d+$/.test(s.trim());

/** Degrees/gradians: trig functions take and inverse trig functions give that unit. */
export function withAngles(linear: string, settings: MathSettings): string {
  if (settings.angle === "rad") return linear;
  const unit = settings.angle === "deg" ? "(pi/180)" : "(pi/200)";
  const inverse = settings.angle === "deg" ? "(180/pi)" : "(200/pi)";
  return rewriteCalls(linear, (name, arg) => {
    if (/^(sin|cos|tan|cot|sec|csc)$/.test(name)) return `${name}(${unit}*(${arg}))`;
    if (/^(asin|acos|atan)$/.test(name)) return `(${inverse}*${name}(${arg}))`;
    return null;
  });
}

/** Rewrite function calls (innermost first); `f` returns the replacement or null to leave it. */
function rewriteCalls(s: string, f: (name: string, arg: string) => string | null): string {
  const re = /([A-Za-z_][A-Za-z0-9_]*)\(/g;
  let out = "", last = 0;
  for (let m = re.exec(s); m; m = re.exec(s)) {
    const start = m.index, open = start + m[0].length - 1;
    let depth = 0, close = open;
    for (; close < s.length; close++) {
      if (s[close] === "(") depth++;
      else if (s[close] === ")" && --depth === 0) break;
    }
    const arg = rewriteCalls(s.slice(open + 1, close), f);
    const replaced = f(m[1], arg);
    out += s.slice(last, start) + (replaced ?? `${m[1]}(${arg})`);
    last = close + 1;
    re.lastIndex = last;
  }
  return out + s.slice(last);
}

// ---------- equations ----------

function solutionsOf(eq: string, v: string): string[] {
  const res = nerdamer.solve(eq, v);
  return (res.symbol?.elements ?? []).map((e: unknown) => String(e));
}

const isReal = (s: string) => !/\bi\b/.test(s.replace(/\bpi\b/g, ""));

/** Does a value satisfy the equation (checked numerically, so extraneous roots from squaring are dropped)? */
function satisfies(part: Part, v: string, value: string, scope: Record<string, number> = {}): boolean {
  const lhs = numeric(`(${part.lhs})-(${part.rhs})`, { ...scope, [v]: numeric(value, scope) });
  if (isNaN(lhs)) return !isReal(value); // complex: can't check this way; keep
  const size = Math.max(1, Math.abs(numeric(part.lhs, { ...scope, [v]: numeric(value, scope) })));
  return Math.abs(lhs) < 1e-7 * size;
}

function solveEquation(p: Problem, v: string, settings: MathSettings): Result {
  const part = { ...p.parts[0], lhs: withAngles(p.parts[0].lhs, settings), rhs: withAngles(p.parts[0].rhs, settings) };
  const others = p.vars.filter((x) => x !== v);
  const f = `(${part.lhs})-(${part.rhs})`;
  const trig = /\b(sin|cos|tan|cot|sec|csc)\(/.test(f);

  // Polynomial in v: exact, with worked steps for the degrees people solve by hand.
  const degree = polynomialDegree(f, v);
  if (!others.length && degree === 1 && !trig) {
    const s = stepsLinear(p.parts[0], v);
    return finish(v, s.solutions, settings, s.steps);
  }
  if (!others.length && degree === 2 && !trig) {
    const s = stepsQuadratic(p.parts[0], v, settings.numbers === "complex");
    return finish(v, s.solutions, settings, s.steps, s.note);
  }

  if (trig && !others.length) return solveTrig(part, v, settings);

  let sols: string[] = [];
  try { sols = solutionsOf(`${part.lhs}=${part.rhs}`, v); } catch { /* fall back to numbers */ }
  if (!others.length) {
    sols = sols.filter((s) => satisfies(part, v, s));
    if (!sols.length) {
      const roots = realRoots(compile(f, [v]), -1000, 1000);
      if (roots.length) {
        return answer(roots.map((r) => `${v}\\approx ${formatNumber(r)}`).join(",\\ "), roots.map((r) => `$${v}\\approx ${formatNumber(r)}$`).join(", "),
          { note: "No exact form found; these are numeric solutions between −1000 and 1000." });
      }
    }
  }
  return finish(v, sols, settings);
}

/** Present solutions: exact, filtered to real ones unless complex numbers are on, with decimals if needed. */
function finish(v: string, sols: string[], settings: MathSettings, steps?: Step[], note?: string): Result {
  const unique = [...new Set(sols.map((s) => nerdamer(s).toString()))];
  const shown = settings.numbers === "real" ? unique.filter(isReal) : unique;
  if (!shown.length) {
    const none = unique.length ? "\\text{No real solutions}" : "\\text{No solution}";
    return answer(none, unique.length ? "No real solutions" : "No solution", { steps, note: note ?? (unique.length ? "There are complex solutions: turn on complex numbers in the Math settings to see them." : undefined) });
  }
  shown.sort((a, b) => (numeric(a) || 0) - (numeric(b) || 0));
  const unwieldy = shown.some((s) => /\^\(1\/3\)|\^\(-1\/3\)/.test(s) || s.length > 60);
  if (unwieldy) {
    const real = shown.filter((s) => isFinite(numeric(s)));
    const latex = real.map((s) => `${v}\\approx ${formatNumber(numeric(s))}`).join(",\\quad ");
    return answer(latex, real.map((s) => `$${v}\\approx ${formatNumber(numeric(s))}$`).join(", "), {
      approx: `\\text{Exact: }${shown.map((s) => `${v}=${toTeX(s)}`).join(",\\ ")}`, steps, note,
    });
  }
  const latex = shown.map((s) => `${v}=${toTeX(s)}`).join(",\\quad ");
  const approxNeeded = shown.some((s) => !isPlainNumber(s) && isFinite(numeric(s)));
  const approx = approxNeeded ? shown.map((s) => `${v}\\approx ${formatNumber(numeric(s))}`).join(",\\quad ") : undefined;
  return answer(latex, shown.map((s) => `$${v}=${toTeX(s)}$`).join(", "), { approx, steps, note, values: shown });
}

/**
 * Trig equations have infinitely many solutions: find those in one period numerically, name them as
 * multiples of π where they are, and give the general solution.
 */
function solveTrig(part: Part, v: string, settings: MathSettings): Result {
  const f = compile(`(${part.lhs})-(${part.rhs})`, [v]);
  const unit = settings.angle === "deg" ? 180 / Math.PI : settings.angle === "grad" ? 200 / Math.PI : 1;
  // The period: the smallest of 2π, π, π/2... (in radians, before unit conversion) that the equation repeats with.
  const samples = [0.3, 1.1, 2.3, 4.2];
  let period = 2 * Math.PI;
  for (const candidate of [Math.PI / 2, Math.PI, 2 * Math.PI]) {
    const P = candidate * unit;
    if (samples.every((s) => Math.abs(f(s * unit) - f(s * unit + P)) < 1e-9)) { period = candidate; break; }
  }
  const roots = realRoots(f, 0, period * unit, 4000).filter((r) => r < period * unit - 1e-9);
  if (!roots.length) return answer("\\text{No solution}", "No solution");
  const name = (x: number) => (settings.angle === "rad" ? piMultiple(x) : null) ?? formatNumber(x);
  const p = settings.angle === "rad" ? piMultiple(period)! : formatNumber(period * unit);
  const general = roots.map((r) => `${v}=${name(r)}+${p}\\,n`).join(",\\quad ");
  return answer(general + "\\quad (n\\in\\mathbb{Z})", roots.map((r) => `$${v}=${name(r)}+${p}\\,n$`).join(", ") + " (n any integer)", {
    note: "The solutions repeat: n is any whole number.",
  });
}

/** x as a "nice" multiple of π (π/6, 3π/4...), if it is one. */
export function piMultiple(x: number): string | null {
  for (const d of [1, 2, 3, 4, 6, 12]) {
    const n = Math.round((x / Math.PI) * d);
    if (Math.abs((n * Math.PI) / d - x) < 1e-9) {
      if (n === 0) return "0";
      const g = gcd(Math.abs(n), d);
      const [nn, dd] = [n / g, d / g];
      const num = nn === 1 ? "\\pi" : nn === -1 ? "-\\pi" : `${nn}\\pi`;
      return dd === 1 ? num : `\\frac{${num}}{${dd}}`;
    }
  }
  return null;
}

const gcd = (a: number, b: number): number => (b ? gcd(b, a % b) : a);

/** Real roots of f in [lo, hi]: sign changes refined by bisection, plus touching roots (local |f| minima at 0). */
export function realRoots(f: (x: number) => number, lo: number, hi: number, n = 4000): number[] {
  const roots: number[] = [];
  const step = (hi - lo) / n;
  let x0 = lo, y0 = f(x0);
  const near = (r: number) => roots.some((q) => Math.abs(q - r) < step / 2);
  for (let i = 1; i <= n; i++) {
    const x1 = lo + i * step, y1 = f(x1);
    if (isFinite(y0) && isFinite(y1)) {
      if (y0 === 0 && !near(x0)) roots.push(x0);
      else if (y0 * y1 < 0) {
        let a = x0, b = x1, fa = y0;
        for (let k = 0; k < 80; k++) {
          const m = (a + b) / 2, fm = f(m);
          if (!isFinite(fm)) break;
          if (fa * fm <= 0) b = m; else { a = m; fa = fm; }
        }
        const r = (a + b) / 2;
        // A jump across an asymptote isn't a root.
        if (Math.abs(f(r)) < 1e-6 * Math.max(1, Math.abs(y0), Math.abs(y1)) && !near(r)) roots.push(r);
      }
    }
    x0 = x1; y0 = y1;
  }
  return roots.map((r) => (Math.abs(r - Math.round(r)) < 1e-9 ? Math.round(r) : r));
}

// ---------- inequalities ----------

function solveInequality(p: Problem, v: string, settings: MathSettings): Result {
  const part = p.parts[0];
  const f = `(${withAngles(part.lhs, settings)})-(${withAngles(part.rhs, settings)})`;
  const g = compile(f, [v]);
  // Exact boundary points where the solver finds them; numeric ones otherwise.
  let exact: string[] = [];
  try { exact = solutionsOf(`${withAngles(part.lhs, settings)}=${withAngles(part.rhs, settings)}`, v).filter(isReal); } catch { /* numeric only */ }
  const bounds = [...realRoots(g, -1000, 1000)];
  for (const e of exact) {
    const x = numeric(e);
    if (isFinite(x) && !bounds.some((b) => Math.abs(b - x) < 1e-7)) bounds.push(x);
  }
  // Points where it's undefined (division by zero) also split the line.
  const breaks = discontinuities(g, -1000, 1000);
  const cuts = [...new Set([...bounds, ...breaks])].sort((a, b) => a - b);
  const label = (x: number) => {
    const e = exact.find((s) => Math.abs(numeric(s) - x) < 1e-7);
    return e ? toTeX(e) : formatNumber(x);
  };
  const holds = (x: number) => {
    const y = g(x);
    if (!isFinite(y)) return false;
    switch (part.rel) {
      case "<": return y < 0;
      case "<=": return y <= 1e-12;
      case ">": return y > 0;
      case ">=": return y >= -1e-12;
      default: return false;
    }
  };
  // Test each interval between cut points, then each cut point itself.
  const edges = [-Infinity, ...cuts, Infinity];
  type Piece = { from: number; to: number; fromIn: boolean; toIn: boolean };
  const pieces: Piece[] = [];
  for (let i = 0; i < edges.length - 1; i++) {
    const a = edges[i], b = edges[i + 1];
    const mid = !isFinite(a) ? b - 1 : !isFinite(b) ? a + 1 : (a + b) / 2;
    if (!isFinite(mid) && !isFinite(a) && !isFinite(b)) { if (holds(0)) pieces.push({ from: a, to: b, fromIn: false, toIn: false }); continue; }
    if (!holds(mid)) continue;
    pieces.push({ from: a, to: b, fromIn: isFinite(a) && holds(a), toIn: isFinite(b) && holds(b) });
  }
  // Join pieces that meet at an included point.
  const merged: Piece[] = [];
  for (const piece of pieces) {
    const prev = merged[merged.length - 1];
    if (prev && prev.to === piece.from && prev.toIn) { prev.to = piece.to; prev.toIn = piece.toIn; }
    else merged.push({ ...piece });
  }
  const isolated = cuts.filter((c) => holds(c) && !merged.some((m) => (m.from < c && c < m.to) || (m.from === c && m.fromIn) || (m.to === c && m.toIn)));
  if (!merged.length && !isolated.length) return answer("\\text{No solution}", "No solution");
  if (merged.length === 1 && merged[0].from === -Infinity && merged[0].to === Infinity) return answer(`${v}\\in\\mathbb{R}`, "All real numbers", { note: "True for every real number." });

  const pieceTeX = (m: Piece) => {
    if (m.from === -Infinity) return `${v}${m.toIn ? "\\le " : "<"}${label(m.to)}`;
    if (m.to === Infinity) return `${v}${m.fromIn ? "\\ge " : ">"}${label(m.from)}`;
    return `${label(m.from)}${m.fromIn ? "\\le " : "<"}${v}${m.toIn ? "\\le " : "<"}${label(m.to)}`;
  };
  const all = [...merged.map(pieceTeX), ...isolated.map((c) => `${v}=${label(c)}`)];
  const latex = all.join("\\ \\text{or}\\ ");
  const interval = [...merged.map((m) => `${m.fromIn ? "[" : "("}${m.from === -Infinity ? "-\\infty" : label(m.from)},${m.to === Infinity ? "\\infty" : label(m.to)}${m.toIn ? "]" : ")"}`),
    ...isolated.map((c) => `\\{${label(c)}\\}`)].join("\\cup ");
  return answer(latex, `$${latex}$`, { approx: `\\text{Interval notation: }${interval}`, steps: inequalitySteps(part, v, cuts.map(label), latex) });
}

function inequalitySteps(part: Part, v: string, boundaries: string[], result: string): Step[] {
  const steps: Step[] = [
    { explanation: "Find where both sides are equal: these points split the number line into intervals", latex: `${toTeX(part.lhs)}=${toTeX(part.rhs)}` },
  ];
  if (boundaries.length) steps.push({ explanation: "Boundary points", latex: boundaries.map((b) => `${v}=${b}`).join(",\\quad ") });
  steps.push({ explanation: "Test a point in each interval and keep the intervals where the inequality holds", latex: result });
  return steps;
}

function discontinuities(f: (x: number) => number, lo: number, hi: number, n = 4000): number[] {
  const out: number[] = [];
  const step = (hi - lo) / n;
  for (let i = 0; i <= n; i++) {
    const x = lo + i * step;
    const near = Math.round(x * 1e6) / 1e6;
    if (!isFinite(f(near)) && isFinite(f(near - step / 3)) && isFinite(f(near + step / 3))) out.push(near);
  }
  return out;
}

// ---------- systems ----------

function solveSystem(p: Problem, settings: MathSettings): Result {
  const linear = p.parts.every((part) => {
    const f = nerdamer.expand(`(${part.lhs})-(${part.rhs})`).toString();
    return p.vars.every((v) => { try { return Number(nerdamer.deg(f, v).toString()) <= 1; } catch { return false; } })
      && !p.vars.some((a) => p.vars.some((b) => a !== b && nerdamer.diff(nerdamer.diff(f, a), b).toString() !== "0"));
  });
  if (linear && p.parts.length === p.vars.length) {
    const s = stepsLinearSystem(p.parts, p.vars);
    if (s.solution) {
      const latex = p.vars.map((v) => `${v}=${toTeX(s.solution![v])}`).join(",\\quad ");
      const approxNeeded = p.vars.some((v) => !isPlainNumber(s.solution![v]));
      return answer(latex, `$${latex}$`, { steps: s.steps, values: p.vars.map((v) => s.solution![v]), approx: approxNeeded ? p.vars.map((v) => `${v}\\approx ${formatNumber(numeric(s.solution![v]))}`).join(",\\quad ") : undefined });
    }
    return answer(s.note === "infinite" ? "\\text{Infinitely many solutions}" : "\\text{No solution}", s.note === "infinite" ? "Infinitely many solutions" : "No solution", { steps: s.steps });
  }
  // Non-linear: the solver's numbers.
  const raw = nerdamer.solveEquations(p.parts.map((x) => `${withAngles(x.lhs, settings)}=${withAngles(x.rhs, settings)}`), p.vars);
  const pairs: [string, unknown][] = Array.isArray(raw) ? raw : [];
  if (!pairs.length) return answer("\\text{No solution found}", "No solution found");
  const latex = pairs.map(([v, value]) => `${v}=${typeof value === "number" ? formatNumber(value) : toTeX(String(value))}`).join(",\\quad ");
  return answer(latex, `$${latex}$`);
}

// ---------- expressions ----------

/** An expression's terms and their factors, each sorted: the same however they're ordered (2+4*x = 4*x+2). */
function writtenForm(expr: string): string {
  const s = expr.replace(/\s/g, "");
  const terms: string[] = [];
  let depth = 0, start = 0;
  for (let i = 0; i <= s.length; i++) {
    const c = s[i];
    if (c === "(") depth++;
    else if (c === ")") depth--;
    // A sign at the top level starts a term (not one right after ^, * or / : x^-1, 2*-x).
    if (i === s.length || (depth === 0 && i > start && (c === "+" || c === "-") && !/[\^*/]/.test(s[i - 1]))) {
      const term = s.slice(start, i);
      const sign = term.startsWith("-") ? "-" : "";
      terms.push(sign + term.replace(/^[+-]/, "").split("*").sort().join("*"));
      start = i;
    }
  }
  return terms.sort().join("+");
}

function transform(p: Problem, what: string, result: string): Result {
  const tex = toTeX(result);
  // Written the same way as the problem (2x+3 simplified is 2x+3): say so instead of repeating it.
  // Compared with the problem as written (nerdamer simplifies whatever it parses: 2x+3x reads back as 5x),
  // in any order (it writes 4x+2 as 2+4x).
  const same = writtenForm(result) === writtenForm(p.linear);
  const note = !same ? undefined
    : what === "factor" ? "This doesn't factor over the rational numbers."
    : what === "expand" ? "There's nothing to multiply out."
    : "This is already as simple as it gets.";
  // Inserted as "problem = answer", so the note shows what was done.
  return answer(tex, `$${p.latex}=${tex}$`, { note, unchanged: same });
}

function evaluate(p: Problem, settings: MathSettings): Result {
  const expr = withAngles(p.linear, settings);
  const exact = nerdamer(expr).toString();
  const value = numeric(expr);
  const tex = toTeX(exact);
  const plain = isPlainNumber(exact);
  return answer(tex, `$${p.latex}=${tex}$`, { approx: !plain && isFinite(value) ? `\\approx ${formatNumber(value)}` : undefined });
}

function derivative(p: Problem, v: string): Result {
  const d = nerdamer.diff(p.linear, v).toString();
  const simplified = simplest(d);
  const tex = toTeX(simplified);
  const lhs = `\\frac{d}{d${v}}\\left(${p.latex}\\right)`;
  return answer(`${lhs}=${tex}`, `$${lhs}=${tex}$`, { steps: stepsDerivative(p.linear, v, simplified) });
}

function integral(p: Problem, v: string): Result {
  const F = simplest(nerdamer.integrate(p.linear, v).toString());
  if (/integrate\(/.test(F)) return answer("\\text{No closed form found}", "No closed form found");
  const tex = `${toTeX(F)}+C`;
  const lhs = `\\int ${p.latex}\\,d${v}`;
  return answer(`${lhs}=${tex}`, `$${lhs}=${tex}$`, { steps: stepsIntegral(p.linear, v, F) });
}

/** The shorter of an expression and its simplified/factored forms. */
function simplest(expr: string): string {
  const options = [expr];
  try { options.push(nerdamer.simplify(expr).toString()); } catch { /* keep */ }
  try { options.push(nerdamer.expand(expr).toString()); } catch { /* keep */ }
  return options.sort((a, b) => a.length - b.length)[0];
}

function matrixRows(linear: string): string[][] {
  const inner = linear.replace(/^matrix\(/, "").replace(/\)$/, "");
  return splitTop(inner).map((row) => splitTop(row.replace(/^\[/, "").replace(/\]$/, "")));
}

function matrixOp(p: Problem, fn: string, before: string, after = ""): Result {
  const result = nerdamer(`${fn}(${p.linear})`).toString();
  const tex = toTeX(result).replace(/\\begin\{vmatrix\}/g, "\\begin{pmatrix}").replace(/\\end\{vmatrix\}/g, "\\end{pmatrix}");
  const lhs = `${before}${p.latex}${after}`;
  return answer(`${lhs}=${tex}`, `$${lhs}=${tex}$`);
}

function statistics(p: Problem): Result {
  const xs = splitTop(p.linear).map(Number);
  const list = xs.join(",");
  const n = xs.length;
  const sorted = [...xs].sort((a, b) => a - b);
  const mean = toTeX(nerdamer(`mean(${list})`).toString());
  const median = toTeX(nerdamer(`median(${list})`).toString());
  const counts = new Map<number, number>();
  for (const x of xs) counts.set(x, (counts.get(x) ?? 0) + 1);
  const top = Math.max(...counts.values());
  const mode = top > 1 ? [...counts].filter(([, c]) => c === top).map(([x]) => x).join(", ") : "\\text{none}";
  const variance = numeric(`variance(${list})`);
  const sample = n > 1 ? (variance * n) / (n - 1) : NaN;
  const rows: [string, string][] = [
    ["\\text{Count}", String(n)],
    ["\\text{Sum}", formatNumber(xs.reduce((a, b) => a + b, 0))],
    ["\\text{Mean}", mean],
    ["\\text{Median}", median],
    ["\\text{Mode}", mode],
    ["\\text{Minimum}", String(sorted[0])],
    ["\\text{Maximum}", String(sorted[n - 1])],
    ["\\text{Range}", formatNumber(sorted[n - 1] - sorted[0])],
    ["\\text{Standard deviation (population)}", formatNumber(Math.sqrt(variance))],
    ["\\text{Standard deviation (sample)}", formatNumber(Math.sqrt(sample))],
  ];
  const latex = `\\begin{aligned}${rows.map(([k, v]) => `${k}&=${v}`).join("\\\\")}\\end{aligned}`;
  return answer(latex, `$$${latex}$$`);
}

// ---------- graphs ----------

/**
 * What "Graph in 2D" plots: an equation in x and y as is; one variable as y = (its expression), or for an
 * equation, both sides' difference with its zeros marked ("Graph both sides": each side as a curve).
 */
export function graphLines(p: Problem, bothSides: boolean): string[] {
  const v = p.vars[0];
  const toX = (s: string) => (v && v !== "x" && !p.vars.includes("x") ? s.replace(new RegExp(`\\b${v}\\b`, "g"), "x") : s);
  if (p.kind === "system" || p.kind === "inequalities") return p.parts.map((part) => `${toX(part.lhs)}${part.rel}${toX(part.rhs)}`);
  const part = p.parts[0];
  const polar = p.vars.includes("r") && p.vars.includes("theta");
  if (polar || (p.vars.includes("x") && p.vars.includes("y"))) return [`${part.lhs}${part.rel ?? "="}${part.rhs}`];
  if (p.kind === "expression") return [`y=${toX(p.linear)}`];
  if (bothSides) return [`y=${toX(part.lhs)}`, `y=${toX(part.rhs)}`];
  if (p.kind === "inequality") return [`${toX(part.lhs)}${part.rel}${toX(part.rhs)}`];
  return [`y=${/^0$/.test(part.rhs.trim()) ? toX(part.lhs) : `(${toX(part.lhs)})-(${toX(part.rhs)})`}`];
}

function graph(p: Problem, bothSides: boolean, settings: MathSettings): Result {
  // x^(2) reads better as x^2 in the note.
  const lines = graphLines(p, bothSides).map((l) => l.replace(/\^\((\d+)\)/g, "^$1"));
  if (settings.angle !== "rad") lines.push(`angle: ${settings.angle}`);
  return answer("", "```math-graph\n" + lines.join("\n") + "\n```", { graph: lines });
}
