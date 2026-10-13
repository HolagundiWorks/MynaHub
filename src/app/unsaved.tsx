import { createContext, useContext, useEffect, useRef, useCallback, type ReactNode } from "react";
import { confirmDialog } from "../components/dialogs";

const LEAVE_MESSAGE = "Leave this page? Anything you are typing that is not saved will be lost.";

type UnsavedApi = {
  retain: () => () => void;
  confirmLeave: () => Promise<boolean>;
  registerSave: (save: () => void) => () => void;
  runSave: () => void;
};

const UnsavedContext = createContext<UnsavedApi | null>(null);

export function UnsavedProvider({ children }: { children: ReactNode }) {
  const count = useRef(0);
  const save = useRef<(() => void) | null>(null);

  const retain = useCallback(() => {
    count.current += 1;
    return () => { count.current = Math.max(0, count.current - 1); };
  }, []);

  const confirmLeave = useCallback(async () => {
    if (count.current === 0) return true;
    return confirmDialog(LEAVE_MESSAGE);
  }, []);

  const registerSave = useCallback((fn: () => void) => {
    save.current = fn;
    return () => { if (save.current === fn) save.current = null; };
  }, []);

  const runSave = useCallback(() => { save.current?.(); }, []);

  return (
    <UnsavedContext.Provider value={{ retain, confirmLeave, registerSave, runSave }}>
      {children}
    </UnsavedContext.Provider>
  );
}

export function useLeaveGuard(): () => Promise<boolean> {
  const api = useContext(UnsavedContext);
  return api?.confirmLeave ?? (async () => true);
}

export function useRunSave(): () => void {
  const api = useContext(UnsavedContext);
  return api?.runSave ?? (() => undefined);
}

/** Registers while `dirty` is true. Clearing `dirty`, or unmounting, drops the registration. */
export function useUnsaved(dirty: boolean) {
  const api = useContext(UnsavedContext);
  useEffect(() => {
    if (!api || !dirty) return;
    return api.retain();
  }, [api, dirty]);
}

/** Ctrl+S runs this while mounted. It must save a draft and must not issue or post. */
export function useDraftSave(save: (() => void) | null) {
  const api = useContext(UnsavedContext);
  const latest = useRef(save);
  latest.current = save;
  const active = save !== null;
  useEffect(() => {
    if (!api || !active) return;
    return api.registerSave(() => { latest.current?.(); });
  }, [api, active]);
}

function stableJson(value: unknown): string {
  const sort = (v: unknown): unknown => {
    if (Array.isArray(v)) return v.map(sort);
    if (v && typeof v === "object") {
      return Object.fromEntries(Object.keys(v as object).sort().map((k) => [k, sort((v as Record<string, unknown>)[k])]));
    }
    return v;
  };
  return JSON.stringify(sort(value));
}

/**
 * Place inside a form. The first rendered value is the clean baseline.
 * Pass a new `cleanToken` after a successful save that stays on the form.
 */
export function DraftGuard({ value, cleanToken = 0, enabled = true }: { value: unknown; cleanToken?: number; enabled?: boolean }) {
  const json = stableJson(value);
  const baseline = useRef(json);
  const seen = useRef(cleanToken);
  if (cleanToken !== seen.current) {
    seen.current = cleanToken;
    baseline.current = json;
  }
  useUnsaved(enabled && json !== baseline.current);
  return null;
}

/** Ctrl+S for the form that is on screen. `save` must not issue the document. */
export function DraftSave({ save, enabled = true }: { save: () => void; enabled?: boolean }) {
  useDraftSave(enabled ? save : null);
  return null;
}
