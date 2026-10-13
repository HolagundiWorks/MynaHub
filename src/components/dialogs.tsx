import { useEffect, useState } from "react";
import { Modal, TextInput } from "@carbon/react";

/**
 * Promise-based Carbon modals, used instead of the browser's prompt/confirm:
 *   const reason = await askReason("Cancel INV/1? Enter the reason.");   // null if dismissed
 *   if (await confirmDialog("Issue this document?")) ...
 * Mount <DialogHost /> once in the shell.
 */
type Request =
  | { kind: "reason"; message: string; danger: boolean; resolve: (v: string | null) => void }
  | { kind: "confirm"; message: string; danger: boolean; resolve: (v: boolean) => void };

let push: ((r: Request) => void) | null = null;

export function askReason(message: string, opts?: { danger?: boolean }): Promise<string | null> {
  return new Promise((resolve) => (push ? push({ kind: "reason", message, danger: opts?.danger ?? true, resolve }) : resolve(null)));
}

export function confirmDialog(message: string, opts?: { danger?: boolean }): Promise<boolean> {
  return new Promise((resolve) => (push ? push({ kind: "confirm", message, danger: opts?.danger ?? false, resolve }) : resolve(false)));
}

export function DialogHost() {
  const [queue, setQueue] = useState<Request[]>([]);
  const [text, setText] = useState("");
  useEffect(() => {
    push = (r) => setQueue((q) => [...q, r]);
    return () => { push = null; };
  }, []);

  const current = queue[0];
  const finish = (ok: boolean) => {
    if (!current) return;
    if (current.kind === "reason") current.resolve(ok && text.trim() ? text.trim() : null);
    else current.resolve(ok);
    setText("");
    setQueue((q) => q.slice(1));
  };

  return (
    <Modal
      open={!!current}
      size="sm"
      danger={!!current?.danger}
      modalHeading={current?.kind === "reason" ? "Reason required" : "Please confirm"}
      primaryButtonText={current?.kind === "reason" ? "Continue" : "Confirm"}
      secondaryButtonText="Cancel"
      primaryButtonDisabled={current?.kind === "reason" && !text.trim()}
      onRequestClose={() => finish(false)}
      onRequestSubmit={() => finish(true)}
      selectorPrimaryFocus={current?.kind === "reason" ? "[data-modal-primary-focus]" : ".cds--btn--danger, .cds--btn--primary"}
    >
      <p className="ao-block">{current?.message}</p>
      {current?.kind === "reason" && (
        <TextInput size="sm" id="dialog-reason" labelText="Reason" data-modal-primary-focus value={text} onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter" && text.trim()) finish(true); }} />
      )}
    </Modal>
  );
}
