import { createContext, useCallback, useContext, useState } from "react";

export type Density = "compact" | "comfortable" | "expanded";

function read<T>(key: string, fallback: T): T {
  try {
    const v = localStorage.getItem(key);
    return v == null ? fallback : (JSON.parse(v) as T);
  } catch {
    return fallback;
  }
}

function write(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    /* storage can be unavailable; the preference simply is not remembered */
  }
}

/** A value remembered on this computer (a per-viewer convenience, never business data). */
export function usePersisted<T>(key: string, fallback: T): [T, (v: T) => void] {
  const [value, setValue] = useState<T>(() => read(key, fallback));
  const set = useCallback((v: T) => {
    setValue(v);
    write(key, v);
  }, [key]);
  return [value, set];
}

/** Carbon's own table sizes: the choice only selects a supported size variant, never custom dimensions. */
export const TABLE_SIZE = { compact: "sm", comfortable: "md", expanded: "lg" } as const;

export const DensityContext = createContext<Density>("comfortable");

export function useTableSize() {
  return TABLE_SIZE[useContext(DensityContext)];
}
