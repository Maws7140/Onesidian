import type { ChangeSet, Text } from "@codemirror/state";

/** Lines inserted (count > 0) or removed (count < 0) at a line (0-based), as the helper's ShiftLines takes them. */
export interface LineShift {
  line: number;
  count: number;
}

/**
 * Where an edit put lines in or took them out, bottom up (so each line number is still right when applied
 * in order), or null when the whole text was replaced (a note loaded into the editor: nothing to follow).
 * Inserting at the start of a line with text pushes that line down; anywhere else (and on an empty line)
 * the new lines come after it. Removing joins lines onto the first.
 */
export function lineShifts(old: Text, changes: ChangeSet): LineShift[] | null {
  const shifts: LineShift[] = [];
  let whole = false;
  changes.iterChanges((fromA, toA, _fromB, _toB, inserted) => {
    if (fromA === 0 && toA === old.length) { whole = true; return; }
    const start = old.lineAt(fromA);
    const count = inserted.lines - 1 - (old.lineAt(toA).number - start.number);
    if (!count) return;
    const at = start.number - 1;
    shifts.push({ line: count > 0 && fromA === start.from && start.to > start.from ? at : count > 0 ? at + 1 : at, count });
  });
  return whole ? null : shifts.sort((a, b) => b.line - a.line);
}
