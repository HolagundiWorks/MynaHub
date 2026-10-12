import { useCallback, useEffect, useRef, useState } from "react";
import { errorText, type Page, type PageQuery } from "../../api";
import { usePersisted } from "../../design/density";
import type { ServerMode, ServerState } from "./DataTable";

type Options<R> = {
  id: string;
  fetch: (q: PageQuery) => Promise<Page<R>>;
  /** Filter values sent to the database; changing them returns to the first page. */
  filters: Record<string, string>;
  defaultSort: { id: string; dir: "asc" | "desc" };
  pageSizes?: number[];
};

/**
 * Holds the paging state of a database-backed register and fetches one page at a time.
 * Out-of-date responses are ignored, so quick typing or paging never shows stale rows.
 */
export function useServerTable<R>(o: Options<R>) {
  const [pageSize] = usePersisted<number>(`dt.${o.id}.pageSize`, (o.pageSizes ?? [50, 100, 200])[0]);
  const [state, setState] = useState<ServerState>({ search: "", sort: o.defaultSort, page: 0, pageSize });
  const [data, setData] = useState<Page<R>>({ rows: [], total: 0, sums: {} });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [tick, setTick] = useState(0);
  const seq = useRef(0);
  const fetchRef = useRef(o.fetch);
  fetchRef.current = o.fetch;
  const filtersKey = JSON.stringify(o.filters);
  const lastFilters = useRef(filtersKey);

  const query = useCallback((s: ServerState, page = s.page): PageQuery => ({
    search: s.search, sort: s.sort?.id ?? "", dir: s.sort?.dir ?? "", page, page_size: s.pageSize, filters: JSON.parse(filtersKey),
  }), [filtersKey]);

  useEffect(() => {
    // A filter change starts again at the first page.
    if (lastFilters.current !== filtersKey) {
      lastFilters.current = filtersKey;
      if (state.page !== 0) { setState({ ...state, page: 0 }); return; }
    }
    const mine = ++seq.current;
    setLoading(true);
    fetchRef.current(query(state))
      .then((p) => { if (mine === seq.current) { setData(p); setError(""); } })
      .catch((e) => { if (mine === seq.current) setError(errorText(e)); })
      .finally(() => { if (mine === seq.current) setLoading(false); });
  }, [state, filtersKey, tick, query]);

  const exportAll = useCallback(async (): Promise<R[]> => {
    const out: R[] = [];
    for (let page = 0; ; page++) {
      const p = await fetchRef.current({ ...query(state, page), page_size: 500 });
      out.push(...p.rows);
      if (p.rows.length < 500 || out.length >= p.total) break;
    }
    return out;
  }, [query, state]);

  const reload = useCallback(() => setTick((t) => t + 1), []);
  const server: ServerMode<R> = { total: data.total, state, onState: setState, exportAll };
  return { rows: data.rows, total: data.total, sums: data.sums, loading, error, reload, server };
}
