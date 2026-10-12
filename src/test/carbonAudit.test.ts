import { describe, expect, it } from "vitest";
import fs from "node:fs";
import path from "node:path";

// Source-level guard for the Carbon-only rule. It cannot prove visual compliance, but it stops regressions.
const SRC = path.resolve(__dirname, "..");
const files = (dir: string): string[] =>
  fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) return e.name === "test" || e.name === "dev" ? [] : files(p);
    return /\.(tsx?|scss)$/.test(e.name) && !/\.test\./.test(e.name) ? [p] : [];
  });
const sources = files(SRC).map((p) => ({ name: path.relative(SRC, p), text: fs.readFileSync(p, "utf8") }));
const tsx = sources.filter((s) => s.name.endsWith(".tsx"));

describe("Carbon-only UI", () => {
  it("uses no other UI or icon library", () => {
    const pkg = JSON.parse(fs.readFileSync(path.resolve(SRC, "..", "package.json"), "utf8"));
    const deps = Object.keys({ ...pkg.dependencies, ...pkg.devDependencies });
    const banned = /^(lucide|@fortawesome|font-?awesome|@heroicons|@mui|@material|material-icons|bootstrap|react-bootstrap|tailwindcss|antd|@chakra|react-icons|@radix|styled-components|@emotion)/;
    expect(deps.filter((d) => banned.test(d))).toEqual([]);
  });

  it("draws no custom SVG, image or HTML injection in app screens", () => {
    for (const s of tsx) expect(s.text, s.name).not.toMatch(/<svg|<img\b|dangerouslySetInnerHTML/);
  });

  it("uses no native form controls, tables or links where Carbon has a component", () => {
    for (const s of tsx) expect(s.text, s.name).not.toMatch(/<(button|input|select|textarea|table|dialog|a)[\s>]/);
  });

  it("uses no emoji or text glyphs as icons", () => {
    for (const s of tsx) expect(s.text, s.name).not.toMatch(/[→←↑↓×✓✔✕✖▲▼●⚠]|&times;|&rarr;|\p{Extended_Pictographic}/u);
  });

  it("restyles nothing: no inline styles, !important or hard-coded colours", () => {
    for (const s of sources) {
      expect(s.text, s.name).not.toMatch(/style=\{\{/);
      expect(s.text, s.name).not.toMatch(/!important/);
      expect(s.text, s.name).not.toMatch(/#[0-9a-fA-F]{3,8}\b(?![\w-])/);
      expect(s.text, s.name).not.toMatch(/var\(--ao-/);
    }
  });

  it("gives every icon-only button an accessible name", () => {
    for (const s of tsx) {
      for (const m of s.text.matchAll(/<(IconButton|HeaderGlobalAction)\b[^>]*>/g)) {
        expect(m[0], `${s.name}: ${m[0]}`).toMatch(/label=|aria-label=/);
      }
    }
  });

  it("imports icons only from the official Carbon package, using names that exist", async () => {
    const icons = (await import("@carbon/icons-react")) as Record<string, unknown>;
    for (const s of tsx) {
      for (const m of s.text.matchAll(/import \{([^}]*)\} from "([^"]+)"/g)) {
        if (/icon/i.test(m[2])) expect(m[2], s.name).toBe("@carbon/icons-react");
        if (m[2] === "@carbon/icons-react") {
          for (const raw of m[1].split(",")) {
            const name = raw.trim().split(/\s+as\s+/)[0];
            if (name) expect(icons[name], `${s.name}: ${name}`).toBeTruthy();
          }
        }
      }
    }
  });
});
