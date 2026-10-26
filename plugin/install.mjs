// Copies the built plugin and the published ink helper into a vault.
// Usage: node install.mjs <vault path>
import { cpSync, mkdirSync, existsSync, readdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "fs";
import { join, dirname } from "path";
import { fileURLToPath } from "url";

const vault = process.argv[2];
if (!vault || !existsSync(join(vault, ".obsidian"))) {
  console.error("Usage: node install.mjs <vault path>  (folder must contain .obsidian)");
  process.exit(1);
}

const here = dirname(fileURLToPath(import.meta.url));
const helper = join(here, "..", "helper", "publish");
if (!existsSync(join(helper, "InkOverlay.exe"))) {
  console.error("Helper not published yet. Run: dotnet publish ../helper -c Release -o ../helper/publish");
  process.exit(1);
}

const dest = join(vault, ".obsidian", "plugins", "onenote-ink");
mkdirSync(dest, { recursive: true });
for (const f of ["main.js", "manifest.json", "styles.css"]) cpSync(join(here, f), join(dest, f));
// A running helper locks its files, but Windows lets us rename them. Move the old build aside
// (the running process keeps using it) and clean up leftovers from earlier installs when unlocked.
const helperDest = join(dest, "helper");
if (existsSync(helperDest)) {
  for (const f of readdirSync(helperDest)) {
    const p = join(helperDest, f);
    if (f.startsWith("helper.log") || f === "backups") continue; // keep the log and pull backups across updates
    try {
      if (f.endsWith(".old")) rmSync(p);
      else renameSync(p, p + ".old");
    } catch { /* still in use; next install cleans it */ }
  }
}
// Never ship a log from local runs/self-tests over the vault's own log.
cpSync(helper, helperDest, { recursive: true, filter: (src) => !/helper\.log/.test(src) });
console.log("Installed to " + dest);

// The testing checklist goes in the vault ("Ink Tests/Ink testing checklist.md") so it can be ticked off in
// Obsidian. Updating it keeps every box already ticked (matched by its text) and adds the new ones.
const checklist = join(here, "..", "TESTING.md");
if (existsSync(checklist)) {
  const target = join(vault, "Ink Tests", "Ink testing checklist.md");
  mkdirSync(dirname(target), { recursive: true });
  const box = /^(\s*- )\[[ xX]\]( .*)$/;
  const ticked = new Set();
  if (existsSync(target)) {
    for (const line of readFileSync(target, "utf8").split(/\r?\n/)) {
      const m = box.exec(line);
      if (m && /\[[xX]\]/.test(line)) ticked.add(m[2].trim());
    }
  }
  const merged = readFileSync(checklist, "utf8").split(/\r?\n/).map((line) => {
    const m = box.exec(line);
    return m && ticked.has(m[2].trim()) ? `${m[1]}[x]${m[2]}` : line;
  });
  writeFileSync(target, merged.join("\n"));
  console.log("Checklist: " + target + (ticked.size ? ` (${ticked.size} ticked kept)` : ""));
}
