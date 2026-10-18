// Copies the product icons (#72, spike #60: IconPark Outline) from @iconify-json/icon-park-outline into
// src/productIcons.json: only the curated icons, never the whole set. Run after changing the list:
//   node scripts/build-icons.mjs
// The list must match BE/src/CupboardTracker.Domain/Products/ProductIcons.cs (a backend test checks it).
import { readFileSync, writeFileSync } from "node:fs";

const set = JSON.parse(
    readFileSync(new URL("../node_modules/@iconify-json/icon-park-outline/icons.json", import.meta.url)),
);
const meta = JSON.parse(
    readFileSync(new URL("../node_modules/@iconify-json/icon-park-outline/metadata.json", import.meta.url)),
);

export const EXTRAS = [
    "apple",
    "bottle",
    "cup",
    "powder",
    "seedling",
    "leaves",
    "snowflake",
    "pill",
    "pig",
    "shopping",
];
export const AISLE_ICONS = [
    "vegetables",
    "bread",
    "fish",
    "milk",
    "sandwich",
    "snowflake",
    "rice",
    "canned-fruit",
    "bottle-one",
    "chili",
    "bowl",
    "candy",
    "tea-drink",
    "shopping-cart",
];

const names = [...new Set([...meta.categories.Foods, ...EXTRAS, ...AISLE_ICONS])].sort();
const icons = {};
for (const name of names) {
    const icon = set.icons[name] ?? set.icons[set.aliases?.[name]?.parent];
    if (!icon) throw new Error(`No icon ${name} in icon-park-outline`);
    icons[name] = icon.body;
}
const out = { width: set.width ?? 48, height: set.height ?? 48, icons };
writeFileSync(new URL("../src/productIcons.json", import.meta.url), JSON.stringify(out, null, 0) + "\n");
console.log(`${names.length} icons, ${JSON.stringify(out).length} bytes`);
console.log(JSON.stringify(meta.categories.Foods));
