import esbuild from "esbuild";
import builtins from "builtin-modules";

await esbuild.build({
  entryPoints: ["src/main.ts"],
  bundle: true,
  external: ["obsidian", "electron", "@codemirror/*", "@lezer/*", ...builtins],
  format: "cjs",
  target: "es2020",
  platform: "node",
  outfile: "main.js",
  logLevel: "info",
});
