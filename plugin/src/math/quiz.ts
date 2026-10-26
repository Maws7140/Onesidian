/**
 * Practice quiz, like Math Assistant's: more problems like this one (same shape, different numbers), with
 * an answer key. For solving, only variants with tidy answers are kept (whole numbers or simple fractions),
 * like a worksheet would have.
 */
import { Action, MathSettings, Problem, parseProblem, polynomialDegree, run } from "./engine";
import { coefficients, nerdamer, numeric, toTeX } from "./cas";

export interface QuizItem {
  problem: Problem;
  answer: string;
}

/** The problem with its numbers changed: same signs and roughly the same size; exponents kept. */
export function vary(linear: string, rand: () => number = Math.random): string {
  return linear.replace(/(\^\(?)?(?<![A-Za-z_\d.])(\d+(?:\.\d+)?)/g, (match, exponent: string | undefined, num: string) => {
    if (exponent) return match;
    const n = Number(num);
    // 0 and 1 are structure (= 0, 1·x), not numbers to change.
    if (n === 0 || n === 1) return num;
    if (!Number.isInteger(n)) return String(Math.round((n + (rand() - 0.5) * n) * 10) / 10 || n);
    const spread = Math.max(3, Math.round(n * 0.6));
    let m = n;
    for (let k = 0; k < 8 && m === n; k++) m = Math.max(2, n + Math.round((rand() * 2 - 1) * spread));
    return String(m);
  });
}

const tidy = (s: string) => {
  // Whole numbers, or fractions with small denominators.
  const m = /^-?(\d+)(?:\/(\d+))?$/.exec(s.trim());
  return !!m && (!m[2] || Number(m[2]) <= 12) && Math.abs(numeric(s)) <= 100;
};

export function makeQuiz(p: Problem, action: Action, count: number, settings: MathSettings, rand: () => number = Math.random): QuizItem[] {
  const items: QuizItem[] = [];
  const keyOf = (s: string) => s.replace(/\s/g, "");
  const seen = new Set<string>([keyOf(p.linear)]);
  // Solving a linear or quadratic equation: build each problem from a tidy answer, like a worksheet.
  const v = action.variable ?? p.vars[0];
  const degree = (action.id === "solve" || action.id === "solve-inequality") && p.vars.length === 1 ? polynomialDegree(`(${p.parts[0].lhs})-(${p.parts[0].rhs})`, v) : -1;
  const rel = p.parts[0].rel ?? "=";
  for (let attempt = 0; attempt < count * 60 && items.length < count; attempt++) {
    const linear = degree === 1 || degree === 2 ? fromAnswers(p, v, degree, rel, rand)
      : action.id === "solve-system" && isLinearSystem(p) ? systemFromAnswers(p, rand)
      : vary(p.linear, rand);
    const key = keyOf(linear);
    if (seen.has(key)) continue;
    let variant: Problem;
    try { variant = parseProblem(linear); } catch { continue; }
    if (variant.kind !== p.kind) continue;
    const result = run(variant, action, settings);
    if (result.error || !result.latex || result.unchanged || /No (real )?solution|Infinitely/.test(result.latex)) continue;
    // Keep worksheet-friendly answers only.
    if ((action.id === "solve" || action.id === "solve-system") && !(result.values?.length && result.values.every(tidy))) continue;
    if (action.id === "solve-inequality" && /approx|sqrt/.test(result.latex)) continue;
    seen.add(key);
    items.push({ problem: variant, answer: result.latex });
  }
  return items;
}

/** A linear (ax + b = c) or quadratic (ax² + bx + c = 0) equation, or inequality, with whole-number answers. */
function fromAnswers(p: Problem, v: string, degree: number, rel: string, rand: () => number): string {
  const int = (lo: number, hi: number) => lo + Math.floor(rand() * (hi - lo + 1));
  const nonzero = (lo: number, hi: number) => { let k = 0; while (k === 0) k = int(lo, hi); return k; };
  if (degree === 1) {
    const x = nonzero(-9, 9);
    let a = nonzero(-9, 9);
    if (Math.abs(a) === 1) a *= int(2, 6);
    const b = nonzero(-15, 15);
    return `${poly([a, b], v)}${rel}${a * x + b}`;
  }
  // Same leading coefficient as the original when it's a small whole number.
  const lead = Number(coefficients(nerdamer.expand(`(${p.parts[0].lhs})-(${p.parts[0].rhs})`).toString(), v)[2]);
  const a = Number.isInteger(lead) && Math.abs(lead) <= 5 && lead !== 0 ? lead : 1;
  const r1 = int(-9, 9), r2 = int(-9, 9);
  return `${poly([a, -a * (r1 + r2), a * r1 * r2], v)}${rel}0`;
}

/** Every equation of the system is linear in its variables. */
function isLinearSystem(p: Problem): boolean {
  return p.parts.every((part) => p.vars.every((v) => polynomialDegree(`(${part.lhs})-(${part.rhs})`, v) <= 1));
}

/** A linear system with a whole-number solution: random small coefficients, right-hand sides to match. */
function systemFromAnswers(p: Problem, rand: () => number): string {
  const int = (lo: number, hi: number) => lo + Math.floor(rand() * (hi - lo + 1));
  const solution = p.vars.map(() => int(-6, 6));
  return p.parts.map(() => {
    const coeffs = p.vars.map(() => { let c = 0; while (c === 0) c = int(-5, 6); return c; });
    let lhs = "";
    coeffs.forEach((c, i) => {
      lhs += (c < 0 ? "-" : lhs ? "+" : "") + (Math.abs(c) === 1 ? "" : Math.abs(c) + "*") + p.vars[i];
    });
    return `${lhs}=${coeffs.reduce((sum, c, i) => sum + c * solution[i], 0)}`;
  }).join("\n");
}

/** Whole-number coefficients (highest power first) written the usual way: x^2-7*x+6. */
function poly(coeffs: number[], v: string): string {
  let s = "";
  coeffs.forEach((c, i) => {
    const power = coeffs.length - 1 - i;
    if (c === 0) return;
    const sign = c < 0 ? "-" : s ? "+" : "";
    const size = Math.abs(c);
    const term = power === 0 ? String(size) : `${size === 1 ? "" : size + "*"}${v}${power > 1 ? "^" + power : ""}`;
    s += sign + term;
  });
  return s || "0";
}

/** The quiz as Markdown: numbered problems, answers folded in a callout. */
export function quizMarkdown(items: QuizItem[], action: Action): string {
  const title = `**Practice: ${action.label.charAt(0).toLowerCase()}${action.label.slice(1)}**`;
  const problems = items.map((it, i) => `${i + 1}. $${it.problem.latex || toTeX(it.problem.linear)}$`).join("\n");
  const answers = items.map((it, i) => `> ${i + 1}. $${it.answer}$`).join("\n");
  return `${title}\n\n${problems}\n\n> [!success]- Answers\n${answers}\n`;
}
