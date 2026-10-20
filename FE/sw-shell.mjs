import { createHash } from "node:crypto";
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

/**
 * Fills in the service worker's offline shell (#85, spike #83): every file this build shipped (Vite's hashed output,
 * the icons, the manifest, the page) and a version from them, so each deploy caches exactly its own files.
 * Plain JavaScript: the app has no Node type definitions.
 */
export function serviceWorkerShell() {
    let files = [];
    let outDir = "dist";
    return {
        name: "service-worker-shell",
        apply: "build",
        configResolved(config) {
            outDir = config.build.outDir;
        },
        generateBundle(_options, bundle) {
            files = Object.keys(bundle).filter((f) => !f.endsWith(".map") && !f.startsWith(".vite/"));
        },
        closeBundle() {
            const icons = readdirSync(join(outDir, "icons")).map((f) => `/icons/${f}`);
            const shell = [
                ...new Set(["/index.html", "/manifest.webmanifest", ...icons, ...files.map((f) => `/${f}`)]),
            ].sort();
            const version = createHash("sha256")
                .update(shell.join("\n"))
                .update(readFileSync(join(outDir, "index.html")))
                .digest("hex")
                .slice(0, 12);
            const path = join(outDir, "sw.js");
            const sw = readFileSync(path, "utf8")
                .replace('"__SHELL_VERSION__"', JSON.stringify(version))
                .replace("/* __SHELL_FILES__ */ []", JSON.stringify(shell));
            writeFileSync(path, sw);
        },
    };
}
