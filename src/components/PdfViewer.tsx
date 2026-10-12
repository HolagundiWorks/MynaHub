import { useEffect, useState } from "react";
import { Button, Stack } from "@carbon/react";

/** Shows a PDF (given as base64) in the page; the blob URL never leaves the window. */
export default function PdfViewer({ base64, filename = "document.pdf", onClose }: { base64: string; filename?: string; onClose: () => void }) {
  const [url, setUrl] = useState("");
  useEffect(() => {
    const bin = atob(base64);
    const bytes = Uint8Array.from(bin, (c) => c.charCodeAt(0));
    const u = URL.createObjectURL(new Blob([bytes], { type: "application/pdf" }));
    setUrl(u);
    return () => URL.revokeObjectURL(u);
  }, [base64]);
  return (
    <Stack gap={4}>
      <div className="toolbar">
        <Button kind="secondary" size="sm" onClick={onClose}>Back</Button>
        {url && <a href={url} download={filename}>Download PDF</a>}
      </div>
      {url && <iframe title="PDF" src={url} style={{ width: "100%", height: "70vh", border: "1px solid var(--cds-border-strong-01)" }} />}
    </Stack>
  );
}
