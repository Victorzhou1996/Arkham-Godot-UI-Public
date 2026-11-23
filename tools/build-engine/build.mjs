// Bundles tools/build-engine/entry.ts (arkham.build's rules code from vendor/arkham.build) into one script for Jint.
// Usage: node tools/build-engine/build.mjs
import { execSync } from "node:child_process";
import path from "node:path";
import fs from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../..");
const vendor = path.join(root, "vendor", "arkham.build");
const { build } = await import(pathToFileURL(path.join(vendor, "node_modules", "esbuild", "lib", "main.js")).href);
const commit = execSync("git rev-parse --short HEAD", { cwd: vendor }).toString().trim();

await build({
  entryPoints: [path.join(here, "main.ts")],
  bundle: true,
  format: "iife",
  target: "es2020",
  platform: "neutral",
  mainFields: ["browser", "module", "main"],
  conditions: ["browser", "import", "default"],
  outfile: path.join(root, "app", "assets", "build-engine", "engine.js"),
  absWorkingDir: path.join(vendor, "frontend"),
  nodePaths: [path.join(vendor, "node_modules")],
  alias: {
    "@": path.join(vendor, "frontend", "src"),
    "@test": path.join(vendor, "test"),
  },
  define: {
    "import.meta.env": JSON.stringify({
      MODE: "test",
      DEV: false,
      PROD: true,
      VITE_CARD_IMAGE_URL: "",
      VITE_ARKHAMDB_BASE_URL: "https://arkhamdb.com",
      VITE_PDFJS_VIEWER_URL: "https://localhost/",
      VITE_API_URL: "https://localhost",
    }),
    "process.env.NODE_ENV": '"production"',
    "globalThis.__BUILD_COMMIT__": JSON.stringify(commit),
  },
  loader: { ".json": "json" },
  legalComments: "none",
  banner: { js: fs.readFileSync(path.join(here, "prelude.js"), "utf8") },
  logLevel: "info",
});
console.log("engine built from arkham.build " + commit);
