import { useEffect, useRef, useState } from "react";
import { Button, ContentSwitcher, IconButton, InlineNotification, Select, SelectItem, Stack, Switch, TextArea } from "@carbon/react";
import { Add, ArrowDown, ArrowUp, TrashCan } from "@carbon/icons-react";
import { api, errorText, type TemplatePreview } from "../../api";
import PdfViewer from "../../components/PdfViewer";

/**
 * A constrained design canvas. A template is a list of blocks (heading, paragraph, bullet list) that can be added, edited,
 * reordered and given fields, shown beside a live paginated preview. It writes the same wording format the Template Manager
 * stores ("# " headings, blank-line paragraphs, "- " bullets), and the preview is rendered by the real PDF engine, so what
 * is seen is what is issued. Fonts, sizes and the page layout come from the fixed O-Studio type system, not from the canvas.
 */
type Kind = "heading" | "paragraph" | "bullets";
type Block = { id: number; kind: Kind; text: string };

let nextId = 1;
const make = (kind: Kind, text = ""): Block => ({ id: nextId++, kind, text });

export function parseBlocks(body: string): Block[] {
  const out: Block[] = [];
  for (const raw of body.replace(/\r\n/g, "\n").split(/\n\s*\n/)) {
    const block = raw.trim();
    if (!block) continue;
    const lines = block.split("\n");
    if (lines.every((l) => l.trimStart().startsWith("- "))) {
      out.push(make("bullets", lines.map((l) => l.trimStart().slice(2)).join("\n")));
    } else if (lines[0].startsWith("# ")) {
      out.push(make("heading", lines[0].slice(2).trim()));
      if (lines.length > 1) out.push(make("paragraph", lines.slice(1).join("\n")));
    } else {
      out.push(make("paragraph", block));
    }
  }
  return out;
}

export function serializeBlocks(blocks: Block[]): string {
  return blocks
    .filter((b) => b.text.trim() !== "")
    .map((b) => (b.kind === "heading" ? `# ${b.text.trim()}` : b.kind === "bullets" ? b.text.split("\n").filter((l) => l.trim()).map((l) => `- ${l.trim()}`).join("\n") : b.text.trim()))
    .join("\n\n");
}

const LABEL: Record<Kind, string> = { heading: "Heading", paragraph: "Paragraph", bullets: "Bullet list" };

export default function TemplateCanvas({ value, onChange, fields, layout, kind, name }: {
  value: string; onChange: (v: string) => void; fields: string[]; layout: string; kind: string; name: string;
}) {
  const [mode, setMode] = useState<"canvas" | "text">("canvas");
  const [blocks, setBlocks] = useState<Block[]>(() => parseBlocks(value));
  const [preview, setPreview] = useState<TemplatePreview | null>(null);
  const [error, setError] = useState("");
  const latest = useRef(0);

  const update = (next: Block[]) => { setBlocks(next); onChange(serializeBlocks(next)); };
  const patch = (id: number, text: string) => update(blocks.map((b) => (b.id === id ? { ...b, text } : b)));
  const move = (i: number, d: number) => {
    const j = i + d;
    if (j < 0 || j >= blocks.length) return;
    const next = [...blocks];
    [next[i], next[j]] = [next[j], next[i]];
    update(next);
  };

  // Live preview: debounced, and a slow answer never overwrites a newer one.
  useEffect(() => {
    const ticket = ++latest.current;
    const timer = setTimeout(async () => {
      try {
        const p = await api.templatePreview(value, layout, kind, name || "Untitled template");
        if (ticket === latest.current) { setPreview(p); setError(""); }
      } catch (e) {
        if (ticket === latest.current) setError(errorText(e));
      }
    }, 600);
    return () => clearTimeout(timer);
  }, [value, layout, kind, name]);

  return (
    <div className="ao-canvas ao-span">
      <Stack gap={4}>
        <ContentSwitcher size="sm" selectedIndex={mode === "canvas" ? 0 : 1} onChange={({ index }: { index?: number }) => {
          if (index === 1) { setMode("text"); } else { setBlocks(parseBlocks(value)); setMode("canvas"); }
        }}>
          <Switch name="canvas" text="Canvas" />
          <Switch name="text" text="Text" />
        </ContentSwitcher>
        {mode === "text" ? (
          <TextArea id="canvas-text" labelText="Wording" rows={18} value={value} onChange={(e) => onChange(e.target.value)} />
        ) : (
          <Stack gap={3}>
            {blocks.map((b, i) => (
              <div key={b.id} className="ao-block-card">
                <div className="toolbar">
                  <span className="ao-muted">{LABEL[b.kind]}</span>
                  <div>
                    <Select size="sm" id={`bf-${b.id}`} labelText="Insert a field" hideLabel value="" onChange={(e) => { if (e.target.value) patch(b.id, `${b.text}{{${e.target.value}}}`); }}>
                      <SelectItem value="" text="Insert a field…" />
                      {fields.map((f) => <SelectItem key={f} value={f} text={f} />)}
                    </Select>
                    <IconButton size="sm" kind="ghost" label="Move up" onClick={() => move(i, -1)} disabled={i === 0}><ArrowUp /></IconButton>
                    <IconButton size="sm" kind="ghost" label="Move down" onClick={() => move(i, 1)} disabled={i === blocks.length - 1}><ArrowDown /></IconButton>
                    <IconButton size="sm" kind="ghost" label="Delete this block" onClick={() => update(blocks.filter((x) => x.id !== b.id))}><TrashCan /></IconButton>
                  </div>
                </div>
                <TextArea
                  id={`bt-${b.id}`} labelText={`${LABEL[b.kind]} text`} hideLabel rows={b.kind === "heading" ? 1 : 3} value={b.text}
                  helperText={b.kind === "bullets" ? "One item per line." : b.kind === "paragraph" ? "Text in [[double brackets]] is a prompt to fill in when the document is written." : undefined}
                  onChange={(e) => patch(b.id, e.target.value)}
                />
              </div>
            ))}
            {blocks.length === 0 && <p className="ao-muted">Add a first block to start the page.</p>}
            <div className="ao-actions">
              <Button size="sm" kind="tertiary" renderIcon={Add} onClick={() => update([...blocks, make("heading")])}>Heading</Button>
              <Button size="sm" kind="tertiary" renderIcon={Add} onClick={() => update([...blocks, make("paragraph")])}>Paragraph</Button>
              <Button size="sm" kind="tertiary" renderIcon={Add} onClick={() => update([...blocks, make("bullets")])}>Bullet list</Button>
            </div>
          </Stack>
        )}
      </Stack>
      <Stack gap={3}>
        <h4>Live preview</h4>
        {error && <InlineNotification kind="error" lowContrast hideCloseButton title={error} />}
        {preview?.warnings.map((w) => <InlineNotification key={w} kind="warning" lowContrast hideCloseButton title={w} />)}
        {preview?.pdf_base64 && <PdfViewer base64={preview.pdf_base64} filename="template-preview.pdf" />}
        <p className="ao-muted">Sample project data and your real company profile. Nothing is saved or numbered.</p>
      </Stack>
    </div>
  );
}
