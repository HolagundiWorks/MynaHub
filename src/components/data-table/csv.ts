/** A cell that a spreadsheet could read as a formula is prefixed so exports cannot run formulas when opened. */
function safeCell(v: string | number): string {
  const s = String(v ?? "");
  const guarded = /^[=+\-@\t\r]/.test(s) && typeof v === "string" ? `'${s}` : s;
  return /[",\n\r]/.test(guarded) ? `"${guarded.replace(/"/g, '""')}"` : guarded;
}

export function toCsv(rows: (string | number)[][]): string {
  return rows.map((r) => r.map(safeCell).join(",")).join("\r\n");
}

export function downloadText(filename: string, text: string) {
  const url = URL.createObjectURL(new Blob(["﻿", text], { type: "text/csv;charset=utf-8" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
