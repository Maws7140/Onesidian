import { ItemView, MarkdownRenderer, Menu, Notice, WorkspaceLeaf, finishRenderMath, renderMath, setIcon } from "obsidian";
import { Action, MathSettings, Problem, Result, actionsFor, parseProblem, run } from "./engine";
import { nerdamer } from "./cas";
import { GraphView, parseGraph } from "./graph";
import { makeQuiz, quizMarkdown } from "./quiz";

export const MATH_VIEW = "onenote-ink-math";

/** Where a problem came from: handwriting (which can be fixed or converted) or typed math in a note. */
export interface MathSource {
  from: "ink" | "typed";
  /** The helper's id for the handwriting. */
  id?: number;
  notePath?: string;
  /** Note lines the problem sits on (results go below `lineEnd`). */
  line: number;
  lineEnd: number;
}

export interface MathHost {
  mathSettings(): MathSettings;
  setMathSettings(s: MathSettings): void;
  /** Open the recognizer's correction panel over the handwriting. */
  fixInk(id: number): void;
  /** Replace the handwriting with typed math. */
  convertInk(source: MathSource, latex: string): Promise<boolean>;
  /** Put Markdown into the note below the problem. */
  insertBelow(source: MathSource, markdown: string): Promise<boolean>;
}

/**
 * The Math pane, like OneNote's Math Assistant: what was recognized (and Fix it), Select an action,
 * the answer with steps, graphs, and a practice quiz. Opens in the right sidebar.
 */
export class MathView extends ItemView {
  private problem: Problem | null = null;
  private source: MathSource | null = null;
  private action: Action | null = null;
  private result: Result | null = null;
  private showSteps = false;
  private editing = false;
  private graph: GraphView | null = null;

  constructor(leaf: WorkspaceLeaf, private host: MathHost) {
    super(leaf);
  }

  getViewType() { return MATH_VIEW; }
  getDisplayText() { return "Math"; }
  getIcon() { return "sigma"; }

  async onOpen() { this.render(); }
  async onClose() { this.graph?.destroy(); }

  /** Show a problem (from handwriting, or typed). Keeps the chosen action if it still applies. */
  setProblem(linear: string, latex: string | undefined, source: MathSource | null) {
    if (!linear.trim()) {
      this.problem = null;
      this.source = source;
      this.result = null;
      this.render();
      return;
    }
    try {
      this.problem = parseProblem(linear, latex);
    } catch (e) {
      this.problem = null;
      new Notice(`Couldn't read that as math: ${e instanceof Error ? e.message : e}`);
    }
    this.source = source;
    const actions = this.problem ? actionsFor(this.problem) : [];
    this.action = actions.find((a) => a.id === this.action?.id && a.variable === this.action?.variable) ?? actions[0] ?? null;
    this.result = null;
    this.editing = false;
    this.compute();
  }

  private compute() {
    this.result = this.problem && this.action ? run(this.problem, this.action, this.host.mathSettings()) : null;
    this.render();
  }

  private render() {
    this.graph?.destroy();
    this.graph = null;
    const root = this.contentEl;
    root.empty();
    root.addClass("oni-math");

    const head = root.createDiv({ cls: "oni-math-head" });
    head.createDiv({ cls: "oni-math-title", text: "Math" });
    const gear = head.createDiv({ cls: "clickable-icon", attr: { "aria-label": "Math settings", role: "button" } });
    setIcon(gear, "settings");
    gear.addEventListener("click", (e) => this.settingsMenu(e));

    if (!this.problem) {
      root.createDiv({ cls: "oni-math-empty", text: "Lasso handwritten math and choose Ink to Math, or type an equation:" });
      this.input(root, "");
      return;
    }

    // What was recognized.
    const card = root.createDiv({ cls: "oni-math-card" });
    card.createDiv({ cls: "oni-math-label", text: this.source?.from === "ink" ? "Recognized from your handwriting" : "Equation" });
    this.math(card.createDiv({ cls: "oni-math-problem" }), this.problem.latex);
    if (this.editing) this.input(card, this.problem.linear);
    const buttons = card.createDiv({ cls: "oni-math-buttons" });
    if (this.source?.from === "ink" && this.source.id !== undefined) {
      this.button(buttons, "wand", "Fix it", () => this.host.fixInk(this.source!.id!));
    }
    this.button(buttons, "pencil", "Edit", () => { this.editing = !this.editing; this.render(); });
    if (this.source?.from === "ink" && this.source.notePath) {
      this.button(buttons, "type", "Convert to typed math", async () => {
        if (await this.host.convertInk(this.source!, this.problem!.latex)) new Notice("Handwriting converted to math");
      });
    }

    // Select an action.
    const actions = actionsFor(this.problem);
    if (!actions.length) {
      root.createDiv({ cls: "oni-math-note", text: "Nothing to do with this one yet. Try Edit if it was misread." });
      return;
    }
    const select = root.createEl("select", { cls: "dropdown oni-math-action" });
    actions.forEach((a, i) => {
      const o = select.createEl("option", { text: a.label, value: String(i) });
      if (a.id === this.action?.id && a.variable === this.action?.variable) o.selected = true;
    });
    select.addEventListener("change", () => { this.action = actions[Number(select.value)]; this.showSteps = false; this.compute(); });

    const r = this.result;
    if (!r) return;
    const out = root.createDiv({ cls: "oni-math-result" });
    if (r.error) {
      out.createDiv({ cls: "oni-math-error", text: `Couldn't do that: ${r.error}` });
      return;
    }
    if (r.graph) {
      this.graph = new GraphView(out, parseGraph(r.graph.join("\n")), { height: 280 });
      const row = out.createDiv({ cls: "oni-math-buttons" });
      if (this.source?.notePath) this.button(row, "file-input", "Insert graph in note", () => this.insert(r.markdown));
    } else {
      this.math(out.createDiv({ cls: "oni-math-answer" }), r.latex);
      if (r.approx) this.math(out.createDiv({ cls: "oni-math-approx" }), r.approx);
      if (r.note) out.createDiv({ cls: "oni-math-note", text: r.note });
      const row = out.createDiv({ cls: "oni-math-buttons" });
      if (r.steps?.length) this.button(row, this.showSteps ? "chevron-up" : "list-ordered", this.showSteps ? "Hide steps" : "Show steps", () => { this.showSteps = !this.showSteps; this.render(); });
      if (this.source?.notePath && !r.unchanged) {
        this.button(row, "file-input", "Insert answer", () => this.insert(r.markdown.includes("$") ? r.markdown : `$${r.latex}$`));
        if (r.steps?.length) this.button(row, "list-plus", "Insert steps", () => this.insert(this.stepsMarkdown(r)));
      }
      if (this.showSteps && r.steps) {
        const list = out.createEl("ol", { cls: "oni-math-steps" });
        for (const s of r.steps) {
          const li = list.createEl("li");
          void MarkdownRenderer.render(this.app, s.explanation, li.createDiv({ cls: "oni-math-step-text" }), this.source?.notePath ?? "", this);
          this.math(li.createDiv({ cls: "oni-math-step-math" }), s.latex);
        }
      }
    }

    // Practice quiz, for problems that have an answer to practise.
    if (this.source?.notePath && !r.graph && !r.unchanged && !["statistics", "sort", "trace", "transpose", "determinant", "inverse", "prime-factors", "evaluate"].includes(this.action!.id)) {
      const quiz = root.createDiv({ cls: "oni-math-quiz" });
      quiz.createDiv({ cls: "oni-math-label", text: "Practice" });
      const row = quiz.createDiv({ cls: "oni-math-buttons" });
      const count = row.createEl("input", { type: "number", cls: "oni-math-count", attr: { min: "1", max: "20", "aria-label": "Number of questions" } });
      count.value = "5";
      this.button(row, "list-checks", "Generate practice quiz", () => {
        const n = Math.max(1, Math.min(20, Number(count.value) || 5));
        const items = makeQuiz(this.problem!, this.action!, n, this.host.mathSettings());
        if (!items.length) { new Notice("Couldn't make practice problems from this one"); return; }
        void this.insert(quizMarkdown(items, this.action!));
        if (items.length < n) new Notice(`Made ${items.length} problems with tidy answers`);
      });
    }
  }

  private async insert(markdown: string) {
    if (this.source && (await this.host.insertBelow(this.source, markdown))) new Notice("Added to the note");
  }

  private stepsMarkdown(r: Result): string {
    const steps = (r.steps ?? []).map((s, i) => `> ${i + 1}. ${s.explanation}: $${s.latex}$`).join("\n");
    return `> [!note]- Steps: ${this.action!.label.charAt(0).toLowerCase()}${this.action!.label.slice(1)}\n${steps}\n> \n> **Answer:** $${r.latex}$`;
  }

  /** The equation as text: what Edit shows, and what you type when there's no handwriting. */
  private input(parent: HTMLElement, value: string) {
    const box = parent.createEl("textarea", { cls: "oni-math-input", attr: { rows: "2", placeholder: "2x + 3 = 7, x^2 - 5x + 6 = 0, \\frac{d}{dx} x^3 ..." } });
    box.value = value;
    const go = () => {
      const typed = readTyped(box.value);
      if (typed) this.setProblem(typed, undefined, this.source ?? null);
    };
    box.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); go(); } });
    const row = parent.createDiv({ cls: "oni-math-buttons" });
    this.button(row, "check", "Use this", go);
    window.setTimeout(() => box.focus(), 0);
  }

  private settingsMenu(e: MouseEvent) {
    const s = this.host.mathSettings();
    const menu = new Menu();
    const set = (next: Partial<MathSettings>) => { this.host.setMathSettings({ ...s, ...next }); this.compute(); };
    for (const [angle, label] of [["rad", "Radians"], ["deg", "Degrees"], ["grad", "Gradians"]] as const) {
      menu.addItem((i) => i.setTitle(label).setChecked(s.angle === angle).onClick(() => set({ angle })));
    }
    menu.addSeparator();
    menu.addItem((i) => i.setTitle("Real numbers only").setChecked(s.numbers === "real").onClick(() => set({ numbers: "real" })));
    menu.addItem((i) => i.setTitle("Complex numbers").setChecked(s.numbers === "complex").onClick(() => set({ numbers: "complex" })));
    menu.showAtMouseEvent(e);
  }

  private button(parent: HTMLElement, icon: string, label: string, onClick: () => void) {
    const b = parent.createEl("button", { cls: "oni-math-button" });
    setIcon(b.createSpan({ cls: "oni-math-button-icon" }), icon);
    b.createSpan({ text: label });
    b.addEventListener("click", onClick);
    return b;
  }

  private math(el: HTMLElement, latex: string) {
    try {
      el.appendChild(renderMath(latex, true));
      void finishRenderMath();
    } catch {
      el.setText(latex);
    }
  }
}

/**
 * Typed or selected math → the linear form the engine works with. Accepts $…$/$$…$$ LaTeX (\frac, \sqrt,
 * cases for systems) and plain text (2x+3=7, x^2-5x+6=0); several lines are a system.
 */
export function readTyped(text: string): string | null {
  let s = text.trim().replace(/^\$\$?|\$\$?$/g, "").trim();
  if (!s) return null;
  const cases = /\\begin\{(cases|aligned|array)\}(?:\{[^}]*\})?([\s\S]*?)\\end\{\1\}/.exec(s);
  const rows = cases ? cases[2].split(/\\\\/).map((r) => r.replace(/&/g, "").trim()).filter(Boolean) : s.split(/\n|;/).map((r) => r.trim()).filter(Boolean);
  const out: string[] = [];
  for (const row of rows) {
    if (/\\[a-zA-Z]/.test(row) || /[{}]/.test(row)) {
      try {
        const tex = row.replace(/\\left|\\right/g, "").replace(/\\cdot|\\times/g, "*").replace(/\\div/g, "/").replace(/\\le(q)?\b/g, "<=").replace(/\\ge(q)?\b/g, ">=").replace(/\\,|\\;|\\!/g, "");
        // Relations are kept out of the LaTeX reader, which only does expressions.
        const parts = tex.split(/(<=|>=|=|<|>)/);
        out.push(parts.map((p, i) => (i % 2 ? p : nerdamer.convertFromLaTeX(p).toString())).join(""));
        continue;
      } catch {
        /* fall through: treat as plain text */
      }
    }
    out.push(row.replace(/−/g, "-").replace(/×|·/g, "*").replace(/÷/g, "/").replace(/≤/g, "<=").replace(/≥/g, ">=").replace(/π/g, "pi")
      // Written multiplication made explicit: 2x → 2*x, 3(x+1) → 3*(x+1), (x+1)(x-1) → (x+1)*(x-1).
      .replace(/(?<![A-Za-z_\d.])(\d+(?:\.\d+)?)\s*(?=[A-Za-z(])/g, "$1*")
      .replace(/\)\s*(?=[A-Za-z0-9(])/g, ")*"));
  }
  return out.join("\n");
}
