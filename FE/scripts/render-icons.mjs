// Renders the app icons (#76) from icons/*.svg to public/icons/*.png with a local Chrome, so they're pixel exact.
// Usage: node scripts/render-icons.mjs <path to playwright-core> [chrome executable]
// (playwright-core isn't a dependency of the app: point at any copy, e.g. one installed in a scratch folder.)
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";

const [playwrightPath, chrome = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"] =
    process.argv.slice(2);
if (!playwrightPath)
    throw new Error("Usage: node scripts/render-icons.mjs <path to playwright-core> [chrome]");
const { chromium } = createRequire(import.meta.url)(playwrightPath);

const outputs = [
    ["icons/app-icon.svg", "public/icons/icon-192.png", 192],
    ["icons/app-icon.svg", "public/icons/icon-512.png", 512],
    ["icons/app-icon-full.svg", "public/icons/icon-maskable-512.png", 512],
    ["icons/app-icon-full.svg", "public/icons/apple-touch-icon.png", 180],
];

const browser = await chromium.launch({ executablePath: chrome, headless: true });
for (const [source, target, size] of outputs) {
    const page = await browser.newPage({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
    const svg = readFileSync(source, "utf8").replace(/<!--[\s\S]*?-->/g, "");
    await page.setContent(
        `<html><body style="margin:0;background:transparent">${svg.replace("<svg ", `<svg width="${size}" height="${size}" `)}</body></html>`,
    );
    await page.screenshot({ path: target, omitBackground: true });
    await page.close();
    console.log(`${target} (${size}×${size})`);
}
await browser.close();
