# OneNote Ink for Obsidian (spike)

Write on top of Obsidian notes using **Windows Ink** (the inking platform OneNote is built on), with strokes saved in **ISF** (Ink Serialized Format), the ink format OneNote stores, so they can later go to OneNote unchanged.

## How it works

```
Obsidian (Electron)                         InkOverlay.exe (WPF, Windows Ink)
  plugin/src/main.ts                          helper/OverlayWindow.cs
  ├─ every frame: measure note area  ──JSON──▶ ├─ borderless transparent window owned by Obsidian
  │  + text column origin (stdin)              │  placed exactly over the note (physical px)
  └─ pen hovering over note ── "pen" ────────▶ ├─ click-through until the pen is near ("armed")
                                               ├─ InkCanvas in page coords, follows scroll
                                               └─ saves strokes as ISF → Note.ink next to Note.md
```

* Pen inks; the pen's eraser end erases strokes. Touch and mouse go to Obsidian (scroll, select, type).
* Ink is anchored to the text column, so it scrolls with the note and stays put if the pane is resized.
* Each note gets a sidecar file: `Note.md` → `Note.ink`.

## Toolbar and settings

A toolbar sits above each note: ink layer on/off · lasso · stroke eraser · point eraser (tap again for size) · pen gallery · add pen · undo/redo (+ delete selection in lasso mode).

* **Pen gallery:** tap a pen to use it; tap the active pen to open its colour wheel, brightness and size slider (logarithmic, in mm) with a live stroke preview, swatches, recent colours, and a hex field. Each pen can be switched between pen and highlighter.
* **Settings** (mirrors OneNote › Options › Advanced › Pen where Windows Ink supports it): pen pressure, ink smoothing, scratch-out gesture, what the pen's eraser end does, point eraser size, show toolbar, reset pens.
* **Draw and hold ink-to-shape** (on by default): draw a shape, keep the pen still ~0.6 s to see a preview, lift to replace the stroke. Recognition is Windows Ink's own `InkAnalyzer` (circles, ellipses, triangles, quadrilaterals…), plus a straight-line check. Undo turns the shape back into your stroke. `InkOverlay.exe --selftest-shapes` checks recognition on synthetic strokes.
* Not available yet: ruler, ink-to-text/math, effect pens (galaxy, rainbow…), pencil texture.

## Build

```bash
cd helper && dotnet publish -c Release -o publish
cd ../plugin && npm install && npm run build
node install.mjs C:\Users\Lilly\Test
```

## What this spike is testing

1. Does the overlay stay locked to the text while scrolling (how much lag)?
2. Does the pen hand-off (hover → armed) feel instant, or do first strokes get lost?
3. Does writing feel like OneNote (latency, smoothness, pressure)?

## Known spike limitations

* Main window only (no popout windows). Overlay hides while a menu or modal is open.
* Reading view and Live Preview lay text out slightly differently, so ink can shift a little between modes.
* No OneNote export yet. That's the next spike.
