import { forwardRef, useEffect, useImperativeHandle, useRef } from "react";
import { basicSetup } from "codemirror";
import { EditorState, Compartment } from "@codemirror/state";
import { EditorView, keymap } from "@codemirror/view";
import { indentWithTab } from "@codemirror/commands";
import { html } from "@codemirror/lang-html";
import { css } from "@codemirror/lang-css";
import { autocompletion, type CompletionContext } from "@codemirror/autocomplete";
import { setDiagnostics, type Diagnostic as CmDiagnostic } from "@codemirror/lint";
import type { FieldDef, StudioDiagnostic } from "../api";

export interface CodeEditorHandle {
  /** Inserts text at the cursor (replacing any selection) and keeps focus in the editor. */
  insert: (text: string) => void;
  focus: () => void;
}

const HELPERS = ["upper", "lower", "date", "default", "multiline"];

// The editor chrome uses Carbon tokens only, so it follows the active Carbon theme.
const theme = EditorView.theme({
  "&": { backgroundColor: "var(--cds-field)", color: "var(--cds-text-primary)", fontSize: "0.8125rem", border: "1px solid var(--cds-border-strong)" },
  ".cm-content": { fontFamily: "'IBM Plex Mono', monospace", caretColor: "var(--cds-text-primary)" },
  ".cm-gutters": { backgroundColor: "var(--cds-layer-accent)", color: "var(--cds-text-secondary)", border: "none" },
  ".cm-activeLine": { backgroundColor: "var(--cds-layer-hover)" },
  ".cm-activeLineGutter": { backgroundColor: "var(--cds-layer-hover)" },
  "&.cm-focused": { outline: "2px solid var(--cds-focus)" },
  ".cm-scroller": { minHeight: "24rem", maxHeight: "60vh", overflow: "auto" },
});

/** Offers approved fields after "{{" and approved helpers after "|"; unknown names are never suggested. */
function placeholderCompletions(fields: FieldDef[]) {
  return (ctx: CompletionContext) => {
    const open = ctx.matchBefore(/\{\{[#/]?\s*[\w.]*$/);
    if (open) {
      const word = /[\w.]*$/.exec(open.text)?.[0] ?? "";
      const from = ctx.pos - word.length;
      return { from, options: fields.map((f) => ({ label: f.path, detail: f.group, info: f.label, type: "variable" })), validFor: /^[\w.]*$/ };
    }
    const pipe = ctx.matchBefore(/\{\{[^}]*\|\s*\w*$/);
    if (pipe) {
      const word = /\w*$/.exec(pipe.text)?.[0] ?? "";
      return { from: ctx.pos - word.length, options: HELPERS.map((h) => ({ label: h, type: "function" })), validFor: /^\w*$/ };
    }
    return null;
  };
}

function toCm(state: EditorState, diagnostics: StudioDiagnostic[]): CmDiagnostic[] {
  const doc = state.doc;
  return diagnostics
    .filter((d) => d.line >= 1 && d.line <= doc.lines)
    .map((d) => {
      const line = doc.line(d.line);
      const from = Math.min(line.from + Math.max(0, d.col - 1), line.to);
      const word = /^[^\s<>]+/.exec(doc.sliceString(from, line.to))?.[0].length ?? 1;
      return { from, to: Math.min(from + Math.max(1, word), line.to + (from === line.to ? 0 : 0)), severity: d.severity === "error" ? "error" : "warning", message: d.message } as CmDiagnostic;
    });
}

/**
 * A lightweight local code editor (CodeMirror 6): syntax highlighting, line numbers, indentation, search, undo,
 * autocomplete of approved placeholders, and error markers from the template validator.
 */
const CodeEditor = forwardRef<CodeEditorHandle, {
  value: string; onChange: (v: string) => void; language: "html" | "css"; label: string; readOnly?: boolean;
  diagnostics?: StudioDiagnostic[]; fields?: FieldDef[];
}>(function CodeEditor({ value, onChange, language, label, readOnly, diagnostics = [], fields = [] }, ref) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;
  const editable = useRef(new Compartment());

  useEffect(() => {
    if (!host.current) return;
    const v = new EditorView({
      parent: host.current,
      state: EditorState.create({
        doc: value,
        extensions: [
          basicSetup, keymap.of([indentWithTab]), theme,
          language === "html" ? html() : css(),
          language === "html" ? autocompletion({ override: [placeholderCompletions(fields)] }) : [],
          editable.current.of([EditorView.editable.of(!readOnly), EditorState.readOnly.of(!!readOnly)]),
          EditorView.contentAttributes.of({ "aria-label": label }),
          EditorView.updateListener.of((u) => { if (u.docChanged) onChangeRef.current(u.state.doc.toString()); }),
        ],
      }),
    });
    view.current = v;
    return () => { v.destroy(); view.current = null; };
    // The editor is created once; later value, diagnostics and read-only changes are applied by the effects below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [language, label, fields.length]);

  useEffect(() => {
    const v = view.current;
    if (v && v.state.doc.toString() !== value) v.dispatch({ changes: { from: 0, to: v.state.doc.length, insert: value } });
  }, [value]);

  useEffect(() => {
    view.current?.dispatch({ effects: editable.current.reconfigure([EditorView.editable.of(!readOnly), EditorState.readOnly.of(!!readOnly)]) });
  }, [readOnly]);

  useEffect(() => {
    const v = view.current;
    if (v) v.dispatch(setDiagnostics(v.state, toCm(v.state, diagnostics)));
  }, [diagnostics, value]);

  useImperativeHandle(ref, () => ({
    insert: (text: string) => {
      const v = view.current;
      if (!v) return;
      v.dispatch(v.state.replaceSelection(text));
      v.focus();
    },
    focus: () => view.current?.focus(),
  }), []);

  return <div ref={host} />;
});

export default CodeEditor;
