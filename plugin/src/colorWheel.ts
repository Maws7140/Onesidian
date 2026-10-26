/** HSV colour wheel: hue around the circle, saturation outwards, brightness on a slider below. */

export interface Hsv { h: number; s: number; v: number }

export function hsvToHex({ h, s, v }: Hsv): string {
  const f = (n: number) => {
    const k = (n + h / 60) % 6;
    return v - v * s * Math.max(0, Math.min(k, 4 - k, 1));
  };
  const to = (x: number) => Math.round(x * 255).toString(16).padStart(2, "0");
  return ("#" + to(f(5)) + to(f(3)) + to(f(1))).toUpperCase();
}

export function hexToHsv(hex: string): Hsv | null {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return null;
  const n = parseInt(m[1], 16);
  const r = ((n >> 16) & 255) / 255, g = ((n >> 8) & 255) / 255, b = (n & 255) / 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
  let h = 0;
  if (d) {
    if (max === r) h = 60 * (((g - b) / d) % 6);
    else if (max === g) h = 60 * ((b - r) / d + 2);
    else h = 60 * ((r - g) / d + 4);
  }
  return { h: (h + 360) % 360, s: max ? d / max : 0, v: max };
}

export class ColorWheel {
  readonly el: HTMLElement;
  private canvas: HTMLCanvasElement;
  private marker: HTMLElement;
  private brightness: HTMLInputElement;
  private hsv: Hsv;
  private renderedV = -1;

  constructor(parent: HTMLElement, hex: string, private onChange: (hex: string) => void, private size = 184) {
    this.hsv = hexToHsv(hex) ?? { h: 0, s: 0, v: 0 };
    this.el = parent.createDiv({ cls: "oni-wheel" });
    const ring = this.el.createDiv({ cls: "oni-wheel-disc" });
    ring.style.width = ring.style.height = `${size}px`;
    this.canvas = ring.createEl("canvas");
    this.canvas.style.width = this.canvas.style.height = `${size}px`;
    this.marker = ring.createDiv({ cls: "oni-wheel-marker" });

    this.brightness = this.el.createEl("input", { cls: "oni-brightness", type: "range" });
    this.brightness.min = "0";
    this.brightness.max = "1000";
    this.brightness.setAttribute("aria-label", "Brightness");
    this.brightness.addEventListener("input", () => {
      this.hsv = { ...this.hsv, v: Number(this.brightness.value) / 1000 };
      this.render();
      this.emit();
    });

    // Drag anywhere on the disc; pointer capture keeps the drag alive outside it.
    ring.addEventListener("pointerdown", (e) => {
      ring.setPointerCapture(e.pointerId);
      this.pick(e);
    });
    ring.addEventListener("pointermove", (e) => {
      if (ring.hasPointerCapture(e.pointerId)) this.pick(e);
    });

    this.render();
  }

  set(hex: string) {
    const next = hexToHsv(hex);
    if (!next) return;
    // Greys have no hue; keep the current hue so the marker doesn't jump to red.
    this.hsv = next.s === 0 ? { ...next, h: this.hsv.h } : next;
    this.render();
  }

  private pick(e: PointerEvent) {
    const r = this.canvas.getBoundingClientRect();
    const dx = e.clientX - (r.left + r.width / 2);
    const dy = e.clientY - (r.top + r.height / 2);
    const radius = r.width / 2;
    const s = Math.min(1, Math.hypot(dx, dy) / radius);
    const h = ((Math.atan2(dy, dx) * 180) / Math.PI + 360) % 360;
    // Picking on the wheel at zero brightness would do nothing visible; lift it.
    const v = this.hsv.v < 0.05 ? 1 : this.hsv.v;
    this.hsv = { h, s, v };
    this.render();
    this.emit();
  }

  private emit() {
    this.onChange(hsvToHex(this.hsv));
  }

  private render() {
    const { h, s, v } = this.hsv;
    // The disc always shows full-brightness hues so dark pens (black!) don't turn it into a black hole;
    // the marker and brightness slider carry the actual colour.
    if (this.renderedV !== 1) this.drawDisc(1);

    const rad = (h * Math.PI) / 180;
    const half = this.size / 2;
    this.marker.style.left = `${half + Math.cos(rad) * s * half}px`;
    this.marker.style.top = `${half + Math.sin(rad) * s * half}px`;
    this.marker.style.background = hsvToHex(this.hsv);

    this.brightness.value = String(Math.round(v * 1000));
    this.brightness.style.setProperty("--oni-bright-top", hsvToHex({ h, s, v: 1 }));
  }

  private drawDisc(v: number) {
    this.renderedV = v;
    const dpr = window.devicePixelRatio || 1;
    const px = Math.round(this.size * dpr);
    this.canvas.width = this.canvas.height = px;
    const ctx = this.canvas.getContext("2d");
    if (!ctx) return;
    const img = ctx.createImageData(px, px);
    const c = px / 2;
    for (let y = 0; y < px; y++) {
      for (let x = 0; x < px; x++) {
        const dx = x + 0.5 - c, dy = y + 0.5 - c;
        const dist = Math.hypot(dx, dy);
        if (dist > c) continue;
        const hue = ((Math.atan2(dy, dx) * 180) / Math.PI + 360) % 360;
        const sat = dist / c;
        const f = (n: number) => {
          const k = (n + hue / 60) % 6;
          return v - v * sat * Math.max(0, Math.min(k, 4 - k, 1));
        };
        const i = (y * px + x) * 4;
        img.data[i] = f(5) * 255;
        img.data[i + 1] = f(3) * 255;
        img.data[i + 2] = f(1) * 255;
        // Soft 1px edge.
        img.data[i + 3] = Math.min(1, c - dist) * 255;
      }
    }
    ctx.putImageData(img, 0, 0);
  }
}
