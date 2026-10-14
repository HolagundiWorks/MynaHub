import { useEffect } from "react";

export type CreateTarget = "clients" | "projects" | "documents" | "invoices" | "bank" | "acc-expenses";

let pending: CreateTarget | null = null;

/** Ask the next visit to that module to open a new record. */
export function queueCreate(page: CreateTarget) {
  pending = page;
}

/** True once, when that module has just been opened in order to create a record. */
export function consumeCreate(page: CreateTarget): boolean {
  if (pending !== page) return false;
  pending = null;
  return true;
}

/**
 * Open a new record after navigation. The check waits one turn so React
 * Strict Mode's setup/cleanup/setup cycle does not throw the request away.
 */
export function useCreateIntent(page: CreateTarget, open: () => void) {
  useEffect(() => {
    const timer = window.setTimeout(() => {
      if (consumeCreate(page)) open();
    }, 0);
    return () => window.clearTimeout(timer);
  }, [page, open]);
}
