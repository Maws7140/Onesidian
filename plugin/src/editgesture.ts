import type { EditorView } from "@codemirror/view";

/** An Ink Editor pen gesture from the helper, in ink coordinates (px from the text column's top-left). */
export interface EditGesture {
  kind?: string;
  inkPath?: string;
  x?: number;
  y?: number;
  w?: number;
  h?: number;
  tipX?: number;
  tipY?: number;
}

/**
 * Do what an Ink Editor gesture means to the text under it: strike-through or scratch-out deletes the
 * words it covers (whole words, one neighbouring space with them), a circle selects them, a caret moves
 * the cursor there. Returns the caret position for a caret (the next handwriting goes there), else null.
 */
export function applyEditGesture(cm: EditorView, origin: { left: number; top: number }, g: EditGesture): number | null {
  const at = (x: number, y: number) => cm.posAtCoords({ x: origin.left + x, y: origin.top + y }, false);
  const { x = 0, y = 0, w = 0, h = 0 } = g;
  const word = (pos: number, side: "from" | "to") => {
    const r = cm.state.wordAt(pos);
    return r ? r[side] : pos;
  };
  const span = (a: number, b: number) => {
    const from = Math.min(a, b), to = Math.max(a, b);
    return { from: word(from, "from"), to: word(Math.max(from, to - 1), "to") };
  };
  switch (g.kind) {
    case "strike":
    case "scratch": {
      const mid = y + h / 2;
      const { from, to } = g.kind === "strike" || h < 30
        ? span(at(x + 2, mid), at(x + w - 2, mid))
        : span(at(x + 2, y + 8), at(x + w - 2, y + h - 8));
      if (to <= from) return null;
      const doc = cm.state.doc;
      let a = from, b = to;
      if (doc.sliceString(b, b + 1) === " ") b++;
      else if (doc.sliceString(a - 1, a) === " ") a--;
      cm.dispatch({ changes: { from: a, to: b, insert: "" }, userEvent: "delete" });
      return null;
    }
    case "circle": {
      const { from, to } = span(at(x + w * 0.1, y + h * 0.3), at(x + w * 0.9, y + h * 0.7));
      cm.dispatch({ selection: { anchor: from, head: to } });
      cm.focus();
      return null;
    }
    case "caret": {
      const pos = at(g.tipX ?? x + w / 2, (g.tipY ?? y) - 6);
      cm.dispatch({ selection: { anchor: pos } });
      return pos;
    }
  }
  return null;
}
