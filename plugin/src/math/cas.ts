/** The symbolic algebra library (nerdamer) and small helpers around it, shared by the engine and the steps. */

// eslint-disable-next-line @typescript-eslint/no-var-requires
export const nerdamer: any = require("nerdamer-prime/all");

/** Normal form of an expression (also what equality checks compare). */
export const N = (s: string): string => nerdamer(s).toString();

/** nerdamer's LaTeX for a linear expression, a little tidier (no parentheses wrapping a whole fraction). */
export function toTeX(linear: string): string {
  try {
    return tidyTeX(nerdamer(linear).toTeX());
  } catch {
    return linear;
  }
}

/** LaTeX for an expression as written (terms in the order given, nothing simplified): for showing problems. */
export function writtenTeX(linear: string): string {
  try {
    return tidyTeX(nerdamer.convertToLaTeX(linear).replace(/\{([a-zA-Z0-9])\}\^/g, "$1^"))
      .replace(/(\d)\*(?=[a-zA-Z(])/g, "$1")
      .replace(/\*/g, "\\cdot ")
      .replace(/\\log\\left\(/g, "\\ln\\left(")
      .replace(/ ?([+\-=]) ?/g, "$1")
      .replace(/\\left\(\\frac\{(\d+)\}\{(\d+)\}\\right\)(?=[a-zA-Z\\])/g, "\\frac{$1}{$2}");
  } catch {
    return toTeX(linear);
  }
}

const NAMED = "sin|cos|tan|cot|sec|csc|arcsin|arccos|arctan|sinh|cosh|tanh|exp|det|min|max";

/** nerdamer's LaTeX the way people write it: \sin x not \mathrm{sin}, 2x not 2·x, (x-2)(x-3), round brackets for matrices. */
export function tidyTeX(tex: string): string {
  return tex
    .replace(/\\frac\{\\left\((.*?)\\right\)\}/g, "\\frac{$1}")
    .replace(new RegExp(`\\\\mathrm\\{(${NAMED})\\}`, "g"), "\\$1")
    .replace(/\\mathrm\{log\}/g, "\\ln")
    .replace(/\\mathrm\{log10\}/g, "\\log_{10}")
    .replace(/\\mathrm\{asin\}/g, "\\arcsin").replace(/\\mathrm\{acos\}/g, "\\arccos").replace(/\\mathrm\{atan\}/g, "\\arctan")
    .replace(/\\mathrm\{abs\}\\left\((.*?)\\right\)/g, "\\left|$1\\right|")
    .replace(/(\d) \\cdot (?=[a-zA-Z\\])(?!\\frac|\\left\(\d)/g, "$1")
    .replace(/\\right\) \\cdot \\left\(/g, "\\right)\\left(")
    .replace(/vmatrix/g, "pmatrix")
    .replace(/\\cr\b/g, "\\\\");
}

/** A fast numeric function of the given variables (NaN where it isn't real). */
export function compile(expr: string, vars: string[]): (...args: number[]) => number {
  try {
    const fn = nerdamer(expr).buildFunction(vars);
    return (...args: number[]) => {
      try {
        const y = fn(...args);
        return typeof y === "number" ? y : NaN;
      } catch {
        return NaN;
      }
    };
  } catch {
    return (...args: number[]) => {
      const scope: Record<string, number> = {};
      vars.forEach((v, i) => (scope[v] = args[i]));
      return numeric(expr, scope);
    };
  }
}

/** Numbers to show: up to 6 significant digits, no trailing zeros. */
export function formatNumber(n: number): string {
  if (!isFinite(n)) return n > 0 ? "\\infty" : n < 0 ? "-\\infty" : "\\text{undefined}";
  if (Math.abs(n) < 1e-12) return "0";
  const s = Math.abs(n) >= 1e6 || Math.abs(n) < 1e-4 ? n.toExponential(5) : n.toPrecision(6);
  return String(Number(s)).replace(/e\+?(-?\d+)/, "\\times10^{$1}");
}

/** A numeric value for an exact expression (NaN if it isn't real). */
export function numeric(expr: string, scope: Record<string, number> = {}): number {
  try {
    const subs: Record<string, string> = {};
    for (const [k, v] of Object.entries(scope)) subs[k] = String(v);
    const s = nerdamer(expr, subs).evaluate().text("decimals");
    if (/i/.test(s.replace(/Infinity/g, ""))) return NaN;
    const n = Number(s);
    return isFinite(n) ? n : NaN;
  } catch {
    return NaN;
  }
}

/** Split at top-level separators (commas by default). */
export function splitTop(s: string, sep = ","): string[] {
  const out: string[] = [];
  let depth = 0, start = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === "(" || c === "[") depth++;
    else if (c === ")" || c === "]") depth--;
    else if (depth === 0 && c === sep) { out.push(s.slice(start, i).trim()); start = i + 1; }
  }
  out.push(s.slice(start).trim());
  return out.filter((p) => p !== "");
}

/** The terms of a sum, each with its sign: "2*x^2-3*x+1" → [+2*x^2, -3*x, +1]. */
export function splitTerms(s: string): { sign: 1 | -1; term: string }[] {
  const out: { sign: 1 | -1; term: string }[] = [];
  let depth = 0, start = 0, sign: 1 | -1 = 1;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === "(" || c === "[") depth++;
    else if (c === ")" || c === "]") depth--;
    else if (depth === 0 && (c === "+" || c === "-")) {
      const before = s.slice(start, i).trim();
      const prev = s.slice(0, i).trimEnd().slice(-1);
      if (!before || "*/^(,".includes(prev) || /\d[eE]$/.test(s.slice(0, i))) {
        if (!before && c === "-") sign = (sign * -1) as 1 | -1;
        if (!before) start = i + 1;
        continue;
      }
      out.push({ sign, term: before });
      sign = c === "-" ? -1 : 1;
      start = i + 1;
    }
  }
  const last = s.slice(start).trim();
  if (last) out.push({ sign, term: last });
  return out;
}

/** The coefficients of a polynomial in v, lowest power first. */
export function coefficients(expr: string, v: string): string[] {
  const s = nerdamer.coeffs(expr, v).toString();
  return splitTop(s.replace(/^\[/, "").replace(/\]$/, ""));
}

export const isZero = (s: string) => N(s) === "0";
export const isNegative = (s: string) => numeric(s) < 0;
