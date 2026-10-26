/**
 * Worked steps, the way they're done by hand (and the way Math Assistant shows them): linear equations,
 * quadratics (factoring when it works out, otherwise the formula), linear systems by elimination, and
 * derivatives/integrals term by term with the rule used. Explanations may hold $inline math$.
 */
import { N, coefficients, isNegative, isZero, nerdamer, numeric, splitTerms, splitTop, toTeX } from "./cas";

export interface Step {
  explanation: string;
  latex: string;
}

interface Part {
  lhs: string;
  rhs: string;
}

const eq = (l: string, r: string) => `${toTeX(l)}=${toTeX(r)}`;
const neg = (s: string) => N(`-(${s})`);

/** Linear in v: collect v on the left, numbers on the right, divide. */
export function stepsLinear(part: Part, v: string): { steps: Step[]; solutions: string[] } {
  const steps: Step[] = [{ explanation: "Start with the equation", latex: eq(part.lhs, part.rhs) }];
  const L = nerdamer.expand(part.lhs).toString();
  const R = nerdamer.expand(part.rhs).toString();
  if (/[()/]/.test(part.lhs + part.rhs) && (N(part.lhs) !== L || N(part.rhs) !== R || /[()]/.test(part.lhs + part.rhs)))
    steps.push({ explanation: "Simplify both sides", latex: eq(L, R) });
  const coef = (e: string) => N(`diff(${e},${v})`);
  const constant = (e: string) => nerdamer(e, { [v]: "0" }).toString();
  let a = coef(L), b = constant(L);
  const aR = coef(R), bR = constant(R);
  let c = bR;
  if (!isZero(aR)) {
    const term = N(`${aR}*${v}`);
    a = N(`(${a})-(${aR})`);
    steps.push({
      explanation: isNegative(aR) ? `Add $${toTeX(neg(term))}$ to both sides` : `Subtract $${toTeX(term)}$ from both sides`,
      latex: eq(N(`${a}*${v}+(${b})`), c),
    });
  }
  if (!isZero(b)) {
    c = N(`(${c})-(${b})`);
    steps.push({
      explanation: isNegative(b) ? `Add $${toTeX(neg(b))}$ to both sides` : `Subtract $${toTeX(b)}$ from both sides`,
      latex: eq(N(`${a}*${v}`), c),
    });
    b = "0";
  }
  if (isZero(a)) {
    const always = isZero(c);
    steps.push({ explanation: always ? `Both sides are the same for every $${v}$` : "This can never be true", latex: always ? "0=0" : `0=${toTeX(c)}` });
    return { steps, solutions: [] };
  }
  const value = N(`(${c})/(${a})`);
  const reciprocal = N(`1/(${a})`);
  if (N(a) !== "1") steps.push({
    explanation: /^-?\d+$/.test(reciprocal) && !/^-?\d+$/.test(N(a)) ? `Multiply both sides by $${toTeX(reciprocal)}$` : `Divide both sides by $${toTeX(a)}$`,
    latex: `${v}=${toTeX(value)}`,
  });
  return { steps, solutions: [value] };
}

/** Quadratic in v: factor if the roots are rational, else the quadratic formula. */
export function stepsQuadratic(part: Part, v: string, complex: boolean): { steps: Step[]; solutions: string[]; note?: string } {
  const steps: Step[] = [{ explanation: "Start with the equation", latex: eq(part.lhs, part.rhs) }];
  const f = nerdamer.expand(`(${part.lhs})-(${part.rhs})`).toString();
  if (!isZero(part.rhs) || N(part.lhs) !== f) steps.push({ explanation: "Move every term to one side", latex: `${toTeX(f)}=0` });
  const [c, b = "0", a = "0"] = coefficients(f, v);
  const disc = N(`(${b})^2-4*(${a})*(${c})`);
  const root = N(`sqrt(${disc})`);
  const rational = (s: string) => !/[a-zA-Z]/.test(s.replace(/\bsqrt\b/g, "S"));
  const roots = [N(`(-(${b})+sqrt(${disc}))/(2*(${a}))`), N(`(-(${b})-sqrt(${disc}))/(2*(${a}))`)];
  if ([a, b, c].every(rational) && rational(root) && !/sqrt|i\b/.test(root) && numeric(disc) >= 0) {
    const factored = nerdamer.factor(f).toString();
    if (factored !== f) {
      steps.push({ explanation: "Factor", latex: `${toTeX(factored)}=0` });
      const distinct = [...new Set(roots)].sort((a, b) => numeric(a) - numeric(b));
      steps.push({
        explanation: distinct.length > 1 ? "A product is 0 when one of its factors is 0: set each factor to 0 and solve" : "The factor is repeated: one solution",
        latex: distinct.map((r) => `${v}=${toTeX(r)}`).join("\\quad\\text{or}\\quad "),
      });
      return { steps, solutions: distinct };
    }
  }
  steps.push({ explanation: "Use the quadratic formula", latex: `${v}=\\frac{-b\\pm\\sqrt{b^{2}-4ac}}{2a}` });
  const p = (s: string) => (isNegative(s) || /[+-]/.test(s.slice(1)) ? `\\left(${toTeX(s)}\\right)` : toTeX(s));
  steps.push({
    explanation: `Substitute $a=${toTeX(a)}$, $b=${toTeX(b)}$, $c=${toTeX(c)}$`,
    latex: `${v}=\\frac{-${p(b)}\\pm\\sqrt{${p(b)}^{2}-4\\cdot ${p(a)}\\cdot ${p(c)}}}{2\\cdot ${p(a)}}`,
  });
  steps.push({ explanation: "Simplify", latex: `${v}=\\frac{${toTeX(neg(b))}\\pm\\sqrt{${toTeX(disc)}}}{${toTeX(N(`2*(${a})`))}}` });
  const negative = numeric(disc) < 0;
  if (negative && !complex) {
    steps.push({ explanation: "The number under the square root is negative, so there are no real solutions", latex: `${toTeX(disc)}<0` });
    return { steps, solutions: roots, note: "There are complex solutions: turn on complex numbers in the Math settings to see them." };
  }
  steps.push({ explanation: "The two solutions", latex: roots.map((r) => `${v}=${toTeX(r)}`).join("\\quad\\text{or}\\quad ") });
  return { steps, solutions: roots };
}

/** A linear system, by elimination (each variable cleared from the other equations in turn). */
export function stepsLinearSystem(parts: Part[], vars: string[]): { steps: Step[]; solution?: Record<string, string>; note?: string } {
  const rows = parts.map((part) => {
    const f = nerdamer.expand(`(${part.lhs})-(${part.rhs})`).toString();
    const zero: Record<string, string> = {};
    for (const v of vars) zero[v] = "0";
    return { c: vars.map((v) => N(`diff(${f},${v})`)), k: N(`-(${nerdamer(f, zero).toString()})`) };
  });
  const rowTeX = (r: { c: string[]; k: string }) => {
    const lhs = r.c.map((c, j) => `(${c})*${vars[j]}`).join("+");
    return `${toTeX(N(lhs))}=${toTeX(r.k)}`;
  };
  const steps: Step[] = [{ explanation: "Start with the system", latex: `\\begin{cases}${parts.map((p) => eq(p.lhs, p.rhs)).join("\\\\")}\\end{cases}` }];
  steps.push({ explanation: "Write each equation with the variables on the left", latex: `\\begin{cases}${rows.map(rowTeX).join("\\\\")}\\end{cases}` });

  const order = rows.map((_, i) => i); // which original equation number each row is
  for (let j = 0; j < vars.length; j++) {
    let r = -1;
    for (let i = j; i < rows.length; i++) if (!isZero(rows[i].c[j])) { r = i; break; }
    if (r < 0) continue;
    if (r !== j) { [rows[r], rows[j]] = [rows[j], rows[r]]; [order[r], order[j]] = [order[j], order[r]]; }
    for (let i = 0; i < rows.length; i++) {
      if (i === j || isZero(rows[i].c[j])) continue;
      const factor = N(`(${rows[i].c[j]})/(${rows[j].c[j]})`);
      rows[i] = { c: rows[i].c.map((c, m) => N(`(${c})-(${factor})*(${rows[j].c[m]})`)), k: N(`(${rows[i].k})-(${factor})*(${rows[j].k})`) };
      const times = N(factor) === "1" ? "" : N(factor) === "-1" ? "" : `$${toTeX(isNegative(factor) ? neg(factor) : factor)}$ times `;
      steps.push({
        explanation: `${isNegative(factor) ? "Add" : "Subtract"} ${times}equation ${order[j] + 1} ${isNegative(factor) ? "to" : "from"} equation ${order[i] + 1} to eliminate $${vars[j]}$`,
        latex: rowTeX(rows[i]),
      });
    }
  }
  const solution: Record<string, string> = {};
  for (let j = 0; j < vars.length; j++) {
    const row = rows[j];
    if (!row || isZero(row.c[j])) {
      const contradiction = rows.some((r) => r.c.every(isZero) && !isZero(r.k));
      steps.push({ explanation: contradiction ? "An equation became impossible, so there's no solution" : "An equation became 0 = 0, so there are infinitely many solutions", latex: contradiction ? "0\\ne 0" : "0=0" });
      return { steps, note: contradiction ? "none" : "infinite" };
    }
    solution[vars[j]] = N(`(${row.k})/(${row.c[j]})`);
    if (N(row.c[j]) !== "1") steps.push({ explanation: `Divide equation ${order[j] + 1} by $${toTeX(row.c[j])}$`, latex: `${vars[j]}=${toTeX(solution[vars[j]])}` });
  }
  return { steps, solution };
}

// ---------- calculus ----------

const FN_DERIVATIVE: Record<string, string> = {
  sin: "The derivative of $\\sin x$ is $\\cos x$",
  cos: "The derivative of $\\cos x$ is $-\\sin x$",
  tan: "The derivative of $\\tan x$ is $\\sec^{2}x$",
  exp: "The derivative of $e^{x}$ is $e^{x}$",
  log: "The derivative of $\\ln x$ is $\\frac{1}{x}$",
  sqrt: "Write $\\sqrt{x}$ as $x^{1/2}$ and use the power rule",
};

function derivativeRule(term: string, v: string): string {
  if (!new RegExp(`\\b${v}\\b`).test(term)) return "The derivative of a constant is 0";
  const factors = splitTop(term, "*");
  const withV = factors.filter((f) => new RegExp(`\\b${v}\\b`).test(f));
  const power = new RegExp(`^${v}(\\^\\(?-?[\\d./]+\\)?)?$`);
  if (withV.length === 1 && power.test(withV[0])) return factors.length > 1 ? "Constant multiple and power rule: $\\frac{d}{dx}x^{n}=nx^{n-1}$" : "Power rule: $\\frac{d}{dx}x^{n}=nx^{n-1}$";
  if (withV.length > 1) return "Product rule: $(fg)'=f'g+fg'$";
  if (splitTop(withV[0] ?? "", "/").length > 1) return "Quotient rule: $\\left(\\frac{f}{g}\\right)'=\\frac{f'g-fg'}{g^{2}}$";
  const call = /^([a-z]+)\((.*)\)$/.exec(withV[0] ?? "");
  if (call && FN_DERIVATIVE[call[1]]) return call[2] === v ? FN_DERIVATIVE[call[1]] : `Chain rule: ${FN_DERIVATIVE[call[1]].charAt(0).toLowerCase()}${FN_DERIVATIVE[call[1]].slice(1)}, times the derivative of $${toTeX(call[2])}$`;
  if (/\^/.test(withV[0] ?? "")) return "Chain rule with the power rule";
  return "Differentiate";
}

export function stepsDerivative(expr: string, v: string, result: string): Step[] {
  const terms = splitTerms(expr);
  const d = (t: string) => `\\frac{d}{d${v}}\\left(${toTeX(t)}\\right)`;
  if (terms.length <= 1) return [{ explanation: derivativeRule(expr, v), latex: `${d(expr)}=${toTeX(result)}` }];
  const steps: Step[] = [{ explanation: "Differentiate term by term", latex: terms.map((t, i) => `${i === 0 ? (t.sign < 0 ? "-" : "") : t.sign < 0 ? "-" : "+"}${d(t.term)}`).join("") }];
  for (const t of terms) steps.push({ explanation: derivativeRule(t.term, v), latex: `${d(t.term)}=${toTeX(nerdamer.diff(t.term, v).toString())}` });
  steps.push({ explanation: "Combine", latex: toTeX(result) });
  return steps;
}

function integralRule(term: string, v: string): string {
  if (!new RegExp(`\\b${v}\\b`).test(term)) return `The integral of a constant $k$ is $k${v}$`;
  const factors = splitTop(term, "*").filter((f) => new RegExp(`\\b${v}\\b`).test(f));
  const only = factors.length === 1 ? factors[0] : "";
  if (new RegExp(`^${v}\\^\\(?-1\\)?$`).test(only) || only === `${v}^(-1)`) return "$\\int\\frac{1}{x}\\,dx=\\ln|x|$";
  if (new RegExp(`^${v}(\\^\\(?-?[\\d./]+\\)?)?$`).test(only)) return "Power rule: $\\int x^{n}\\,dx=\\frac{x^{n+1}}{n+1}$";
  if (only === `sin(${v})`) return "$\\int\\sin x\\,dx=-\\cos x$";
  if (only === `cos(${v})`) return "$\\int\\cos x\\,dx=\\sin x$";
  if (only === `e^${v}` || only === `exp(${v})`) return "$\\int e^{x}\\,dx=e^{x}$";
  return "Integrate";
}

export function stepsIntegral(expr: string, v: string, result: string): Step[] {
  const terms = splitTerms(expr);
  const I = (t: string) => `\\int ${toTeX(t)}\\,d${v}`;
  const steps: Step[] = [];
  if (terms.length > 1) steps.push({ explanation: "Integrate term by term", latex: terms.map((t, i) => `${i === 0 ? (t.sign < 0 ? "-" : "") : t.sign < 0 ? "-" : "+"}${I(t.term)}`).join("") });
  for (const t of terms) {
    let F = "";
    try { F = nerdamer.integrate(t.term, v).toString(); } catch { F = ""; }
    steps.push({ explanation: integralRule(t.term, v), latex: F ? `${I(t.term)}=${toTeX(F)}` : I(t.term) });
  }
  steps.push({ explanation: "Add the constant of integration", latex: `${toTeX(result)}+C` });
  return steps;
}
