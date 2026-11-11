// Contrast of the suite's colour pairs against WCAG 2.2: 1.4.3 (text, 4.5) and 1.4.11 (graphics and component edges, 3).
//   node contrast.mjs
// The colours are those in MynaUI (Themes/SuiteColors.xaml, the light theme, pure white and black, and SuiteAccent.*.xaml). Keep them in step.
const lum = h => { const c = [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16) / 255).map(v => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4)); return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]; };
const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
const N = { bg: "#FFFFFF", side: "#FFFFFF", surface: "#FFFFFF", ink: "#000000", soft: "#4D4D4D", edgeStrong: "#767676", danger: "#C42B1C" };
const A = {
  "MynaHub (Ember)": { accent: "#B83E16", hover: "#A03212", pressed: "#8C2A10", text: "#B83E16", wash: "#F8E8E0", on: "#FFFFFF" },
  "MynaCAD (Midnight)": { accent: "#102C80", hover: "#0C2470", pressed: "#081C60", text: "#102C80", wash: "#E4E8F8", on: "#FFFFFF" },
  "MynaOps (Dusk)": { accent: "#5630A0", hover: "#4A289A", pressed: "#3E2080", text: "#5630A0", wash: "#EEE8F8", on: "#FFFFFF" },
  "Myna3D (Grove)": { accent: "#166040", hover: "#125236", pressed: "#0E422C", text: "#166040", wash: "#E4F0EA", on: "#FFFFFF" },
};
const rows = [];
const add = (what, fg, bg, need) => rows.push({ what, r: ratio(fg, bg), need });
for (const [k, [f, b]] of Object.entries({ "ink on page": [N.ink, N.bg], "ink on surface": [N.ink, N.surface], "ink on sidebar": [N.ink, N.side], "soft ink on page": [N.soft, N.bg], "soft ink on surface": [N.soft, N.surface], "soft ink on sidebar": [N.soft, N.side], "danger text on surface": [N.danger, N.surface] })) add(k, f, b, 4.5);
add("field border on surface", N.edgeStrong, N.surface, 3);
for (const [name, a] of Object.entries(A)) {
  add(`${name}: text on fill`, a.on, a.accent, 4.5);
  add(`${name}: text on fill, hover`, a.on, a.hover, 4.5);
  add(`${name}: text on fill, pressed`, a.on, a.pressed, 4.5);
  add(`${name}: accent text on surface`, a.text, N.surface, 4.5);
  add(`${name}: accent text on page`, a.text, N.bg, 4.5);
  add(`${name}: ink on selection wash`, N.ink, a.wash, 4.5);
  add(`${name}: pressed edge against surface (button edge)`, a.pressed, N.surface, 3);
  add(`${name}: ink indicator on sidebar`, N.ink, N.side, 3);
}
let failed = 0;
for (const r of rows) { const ok = r.r >= r.need; if (!ok) failed++; console.log((ok ? "PASS" : "FAIL").padEnd(5), r.r.toFixed(2).padStart(6), "needs", String(r.need).padEnd(4), r.what); }
process.exit(failed ? 1 : 0);
