import fs from "fs";
const file = process.argv[2];
const r = JSON.parse(fs.readFileSync(file, "utf8").replace(/^\uFEFF/, ""));
const interactive = new Set(["Button", "Edit", "ComboBox", "CheckBox", "RadioButton", "MenuItem", "TabItem", "Hyperlink", "Slider", "SplitButton", "ListItem", "DataItem"]);
const issues = { unnamed: new Map(), small: new Map(), notFocusable: new Map(), images: new Map() };
const bump = (m, key, page) => { const e = m.get(key) ?? { n: 0, pages: new Set() }; e.n++; e.pages.add(page); m.set(key, e); };
let total = 0;
for (const p of r) {
  for (const i of p.items) {
    if (i.offscreen) continue;
    total++;
    const key = `${i.type} id='${i.id}' name='${i.name}' ${i.w}x${i.h}`;
    if (interactive.has(i.type) && !i.name.trim() && i.type !== "ListItem" && i.type !== "DataItem") bump(issues.unnamed, `${i.type} id='${i.id}'`, p.page);
    if (interactive.has(i.type) && i.enabled && i.w > 0 && i.h > 0 && (i.w < 24 || i.h < 24) && !["Edit"].includes(i.type)) bump(issues.small, `${i.type} '${i.name}' ${i.w}x${i.h}`, p.page);
    if (["Button", "Edit", "ComboBox", "CheckBox", "MenuItem", "TabItem", "Hyperlink"].includes(i.type) && i.enabled && !i.focusable) bump(issues.notFocusable, `${i.type} '${i.name}'`, p.page);
    if (i.type === "Image" && !i.name.trim()) bump(issues.images, `Image id='${i.id}'`, p.page);
  }
}
console.log(file, "pages:", r.length, "visible elements:", total);
for (const [k, m] of Object.entries(issues)) {
  console.log("\n== " + k + " (" + m.size + " kinds)");
  for (const [key, e] of [...m.entries()].sort((a, b) => b[1].n - a[1].n).slice(0, 25)) console.log(`  ${String(e.n).padStart(4)}  ${key}   [${[...e.pages].slice(0, 4).join(", ")}${e.pages.size > 4 ? ", +" + (e.pages.size - 4) : ""}]`);
}
