import { useEffect, useState } from "react";
import { Button, Stack } from "@carbon/react";
import { ArrowLeft, Download } from "@carbon/icons-react";

/** Shows a PDF (given as base64) in the page; the blob URL never leaves the window. */
export default function PdfViewer({ base64, filename = "document.pdf", onClose }: { base64: string; filename?: string; onClose?: () => void }) {
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
        {onClose ? <Button renderIcon={ArrowLeft} kind="secondary" size="sm" onClick={onClose}>Back</Button> : <span />}
        {url && <Button as="a" kind="tertiary" size="sm" renderIcon={Download} href={url} download={filename}>Download PDF</Button>}
      </div>
      {url && <iframe className="ao-pdf" title="PDF" src={url} />}
    </Stack>
  );
}
