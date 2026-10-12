import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import {
  ActionableNotification, Button, Checkbox, DataTable as CarbonDataTable, DataTableSkeleton, DismissibleTag, Pagination, Popover, PopoverContent, Table,
  TableBatchActions, TableBody, TableCell, TableContainer, TableHead, TableHeader, TableRow, TableSelectAll, TableSelectRow, TableToolbar, TableToolbarContent,
  TableToolbarSearch,
} from "@carbon/react";
import { usePersisted, useTableSize } from "../../design/density";
import { toCsv, downloadText } from "./csv";

/**
 * A thin, typed layer over Carbon's DataTable. Carbon provides the table, toolbar, search, selection, batch actions,
 * pagination and skeleton; this adds typed columns, raw-value sorting, a column chooser, filter tags, CSV export and
 * server-side paging. Server mode is a documented need: Carbon's own sorting and pagination work on rows already
 * in memory, but large registers must page, sort and filter in the database.
 */
export type Column<R> = {
  id: string;
  header: string;
  minWidth?: number;
  width?: number;
  align?: "start" | "end" | "center";
  /** Sorting uses this raw value, never the formatted display text. In server mode its presence marks the column sortable. */
  sortValue?: (row: R) => string | number;
  defaultVisible?: boolean;
  /** Always visible and not offered in the column chooser (identity columns). */
  locked?: boolean;
  render: (row: R) => ReactNode;
  /** Plain value for CSV export. Defaults to the sort value. */
  csv?: (row: R) => string | number;
};

export type ActiveFilter = { label: string; clear: () => void };

export type ServerState = { search: string; sort: { id: string; dir: "asc" | "desc" } | null; page: number; pageSize: number };

export type ServerMode<R> = {
  total: number;
  state: ServerState;
  onState: (s: ServerState) => void;
  /** Fetches every matching row (not just this page) for export. */
  exportAll?: () => Promise<R[]>;
};

type Props<R> = {
  /** Used to remember column choices on this computer. */
  id: string;
  rows: R[];
  columns: Column<R>[];
  rowKey: (row: R) => string | number;
  loading?: boolean;
  /** A failed load is shown as an error, never as an empty register. */
  error?: string;
  onRetry?: () => void;
  searchText?: (row: R) => string;
  searchLabel?: string;
  filters?: ReactNode;
  activeFilters?: ActiveFilter[];
  selectable?: boolean;
  selectionActions?: (selected: R[], clear: () => void) => ReactNode;
  onSelectionChange?: (selected: R[]) => void;
  /** Visible primary action(s) for a row, followed by an overflow menu for the rest. */
  rowActions?: (row: R) => ReactNode;
  /** Local mode: totals for the filtered rows. */
  totals?: (filtered: R[]) => ReactNode;
  /** Server mode: totals over all matching rows, computed by the database. */
  totalsNode?: ReactNode;
  emptyTitle?: string;
  emptyHint?: string;
  defaultSort?: { id: string; dir: "asc" | "desc" };
  pageSizes?: number[];
  exportName?: string;
  /** Extra controls placed in the toolbar. */
  toolbarEnd?: ReactNode;
  server?: ServerMode<R>;
};

function compare(a: string | number, b: string | number) {
  if (typeof a === "number" && typeof b === "number") return a - b;
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: "base" });
}

function SelectionReporter<R>({ rows, onChange }: { rows: R[]; onChange?: (rows: R[]) => void }) {
  const key = rows.map((r) => JSON.stringify(r)).join("|");
  const last = useRef("");
  useEffect(() => {
    if (last.current !== key) { last.current = key; onChange?.(rows); }
  }, [key]); // eslint-disable-line react-hooks/exhaustive-deps
  return null;
}

export default function DataTable<R>(p: Props<R>) {
  const size = useTableSize();
  const server = p.server;
  const [query, setQuery] = useState(server?.state.search ?? "");
  const [searchKey, setSearchKey] = useState(0);
  const [localSort, setLocalSort] = useState(p.defaultSort ?? null);
  const [localPageSize, setLocalPageSize] = usePersisted<number>(`dt.${p.id}.pageSize`, (p.pageSizes ?? [50, 100, 200])[0]);
  const [localPage, setPage] = useState(0);
  const [hidden, setHidden] = usePersisted<string[] | null>(`dt.${p.id}.hidden`, null);
  const [colsOpen, setColsOpen] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState("");

  const sort = server ? server.state.sort : localSort;
  const pageSize = server ? server.state.pageSize : localPageSize;
  const sizes = p.pageSizes ?? [50, 100, 200];

  const defaultHidden = useMemo(() => p.columns.filter((c) => c.defaultVisible === false).map((c) => c.id), [p.columns]);
  const hiddenIds = hidden ?? defaultHidden;
  const visible = p.columns.filter((c) => c.locked || !hiddenIds.includes(c.id));

  // Server mode: send the search after a short pause instead of on every keystroke.
  useEffect(() => {
    if (!server || query === server.state.search) return;
    const t = setTimeout(() => server.onState({ ...server.state, search: query, page: 0 }), 300);
    return () => clearTimeout(t);
  }, [query]); // eslint-disable-line react-hooks/exhaustive-deps

  const filtered = useMemo(() => {
    if (server) return p.rows;
    const q = query.trim().toLowerCase();
    let out = p.rows.map((r, i) => ({ r, i }));
    if (q && p.searchText) out = out.filter(({ r }) => p.searchText!(r).toLowerCase().includes(q));
    const col = localSort ? p.columns.find((c) => c.id === localSort.id) : undefined;
    if (col?.sortValue) {
      const dir = localSort!.dir === "asc" ? 1 : -1;
      out = [...out].sort((a, b) => dir * compare(col.sortValue!(a.r), col.sortValue!(b.r)) || a.i - b.i);
    }
    return out.map((x) => x.r);
  }, [server, p.rows, p.columns, p.searchText, query, localSort]);

  const totalCount = server ? server.total : filtered.length;
  const pages = Math.max(1, Math.ceil(totalCount / pageSize));
  const current = server ? Math.min(server.state.page, pages - 1) : Math.min(localPage, pages - 1);
  const slice = server ? p.rows : filtered.slice(current * pageSize, (current + 1) * pageSize);

  useEffect(() => { if (!server) setPage(0); }, [query, localSort, localPageSize, server]);

  const byId = useMemo(() => new Map(slice.map((r) => [String(p.rowKey(r)), r])), [slice]); // eslint-disable-line react-hooks/exhaustive-deps
  const filters = p.activeFilters ?? [];
  const hasConstraint = query.trim() !== "" || filters.length > 0;
  const clearAll = () => {
    setQuery(""); setSearchKey((k) => k + 1);
    if (server) server.onState({ ...server.state, search: "", page: 0 });
    filters.forEach((f) => f.clear());
  };

  const toggleSort = (c: Column<R>) => {
    if (!c.sortValue) return;
    const next = sort?.id === c.id ? { id: c.id, dir: (sort.dir === "asc" ? "desc" : "asc") as "asc" | "desc" } : { id: c.id, dir: "asc" as const };
    if (server) server.onState({ ...server.state, sort: next, page: 0 });
    else setLocalSort(next);
  };

  const writeCsv = (list: R[]) => {
    const head = visible.map((c) => c.header);
    const lines = list.map((r) => visible.map((c) => (c.csv ? c.csv(r) : c.sortValue ? c.sortValue(r) : "")));
    downloadText(`${p.exportName ?? p.id}.csv`, toCsv([head, ...lines]));
  };

  const exportCsv = async () => {
    setExportError("");
    if (!server) { writeCsv(filtered); return; }
    if (!server.exportAll) { writeCsv(p.rows); return; }
    setExporting(true);
    try { writeCsv(await server.exportAll()); } catch (e) { setExportError(typeof e === "string" ? e : "The export failed."); }
    setExporting(false);
  };

  const headers = [...visible.map((c) => ({ key: c.id, header: c.header })), ...(p.rowActions ? [{ key: "__actions", header: "Actions" }] : [])];
  const tableRows = slice.map((r) => ({
    id: String(p.rowKey(r)),
    ...Object.fromEntries(visible.map((c) => [c.id, c.render(r)])),
    ...(p.rowActions ? { __actions: p.rowActions(r) } : {}),
  }));
  const colByKey = new Map(visible.map((c) => [c.id, c]));
  const from = totalCount === 0 ? 0 : current * pageSize + 1;
  const to = Math.min(totalCount, (current + 1) * pageSize);

  const body = (() => {
    if (p.loading) return <DataTableSkeleton columnCount={Math.max(2, headers.length)} rowCount={6} size={size} showToolbar={false} showHeader={false} />;
    if (p.error) {
      return (
        <ActionableNotification
          kind="error" lowContrast hideCloseButton title="Could not load these records" subtitle={p.error}
          actionButtonLabel={p.onRetry ? "Try again" : undefined} onActionButtonClick={p.onRetry}
        />
      );
    }
    return (
      <CarbonDataTable rows={tableRows} headers={headers} size={size} sortRow={() => 0} isSortable>
        {({ rows, headers: hs, getHeaderProps, getRowProps, getTableProps, getSelectionProps, getBatchActionProps, getToolbarProps, selectedRows }) => (
          <TableContainer>
            <SelectionReporter rows={selectedRows.map((r) => byId.get(r.id)).filter((r): r is R => !!r)} onChange={p.onSelectionChange} />
            <TableToolbar {...getToolbarProps()}>
              {p.selectable && (
                <TableBatchActions {...getBatchActionProps()}>
                  {p.selectionActions?.(selectedRows.map((r) => byId.get(r.id)).filter((r): r is R => !!r), () => undefined)}
                </TableBatchActions>
              )}
              <TableToolbarContent>
                {(p.searchText || server) && (
                  <TableToolbarSearch
                    key={searchKey} persistent size={size === "sm" ? "sm" : "lg"} placeholder={p.searchLabel ?? "Search"} labelText={p.searchLabel ?? "Search"}
                    defaultValue={query} onChange={(e: unknown, v?: string) => setQuery(v ?? (e as { target?: { value?: string } })?.target?.value ?? "")}
                  />
                )}
                {p.filters}
                {p.toolbarEnd}
                <Popover open={colsOpen} onRequestClose={() => setColsOpen(false)} align="bottom-end" isTabTip>
                  <Button kind="ghost" size={size === "sm" ? "sm" : "lg"} aria-expanded={colsOpen} onClick={() => setColsOpen(!colsOpen)}>Columns</Button>
                  <PopoverContent>
                    <div style={{ padding: "var(--cds-spacing-05)" }}>
                      {p.columns.filter((c) => !c.locked).map((c) => (
                        <Checkbox
                          key={c.id} id={`${p.id}-col-${c.id}`} labelText={c.header} checked={!hiddenIds.includes(c.id)}
                          onChange={(_, { checked }) => setHidden(checked ? hiddenIds.filter((x) => x !== c.id) : [...hiddenIds, c.id])}
                        />
                      ))}
                      <Button kind="ghost" size="sm" onClick={() => setHidden(null)}>Restore defaults</Button>
                    </div>
                  </PopoverContent>
                </Popover>
                {p.exportName && (
                  <Button kind="ghost" size={size === "sm" ? "sm" : "lg"} disabled={exporting || totalCount === 0} onClick={exportCsv}>
                    {exporting ? "Exporting…" : server ? (server.exportAll ? `Export all ${server.total} matching` : "Export this page") : "Export CSV"}
                  </Button>
                )}
              </TableToolbarContent>
            </TableToolbar>
            <Table {...getTableProps()} aria-label={p.searchLabel ?? p.id}>
              <TableHead>
                <TableRow>
                  {p.selectable && <TableSelectAll {...getSelectionProps()} />}
                  {hs.map((h) => {
                    const col = colByKey.get(h.key);
                    const isActions = h.key === "__actions";
                    const sortable = !!col?.sortValue;
                    const dir = sort?.id === col?.id ? (sort?.dir === "asc" ? "ASC" : "DESC") : "NONE";
                    const { key: _k, ...hp } = getHeaderProps({ header: h });
                    void _k;
                    return (
                      <TableHeader
                        {...hp} key={h.key} isSortable={sortable} isSortHeader={dir !== "NONE"} sortDirection={dir as "ASC" | "DESC" | "NONE"}
                        onClick={sortable ? () => toggleSort(col!) : undefined}
                        className={col?.align === "end" ? "ao-num" : undefined} style={col?.minWidth ? { minInlineSize: col.minWidth } : undefined}
                      >
                        {isActions ? <span className="cds--visually-hidden">Actions</span> : h.header}
                      </TableHeader>
                    );
                  })}
                </TableRow>
              </TableHead>
              <TableBody>
                {rows.map((row) => {
                  const { key: _rk, ...rp } = getRowProps({ row });
                  void _rk;
                  return (
                    <TableRow {...rp} key={row.id}>
                      {p.selectable && <TableSelectRow {...getSelectionProps({ row })} />}
                      {row.cells.map((cell) => {
                        const key = cell.info.header;
                        const col = colByKey.get(key);
                        return (
                          <TableCell key={cell.id} className={key === "__actions" ? "ao-num" : col?.align === "end" ? "ao-num" : undefined}>{cell.value as ReactNode}</TableCell>
                        );
                      })}
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
            {rows.length === 0 && (
              <div style={{ padding: "var(--cds-spacing-07)", textAlign: "center" }}>
                {(server ? server.total === 0 && hasConstraint : p.rows.length > 0 && hasConstraint) ? (
                  <>
                    <h4>{query.trim() && filters.length === 0 ? "No results for this search" : "No rows match the filters"}</h4>
                    <Button kind="tertiary" size="sm" onClick={clearAll}>Clear search and filters</Button>
                  </>
                ) : (
                  <>
                    <h4>{p.emptyTitle ?? "Nothing here yet"}</h4>
                    {p.emptyHint && <p className="ao-muted">{p.emptyHint}</p>}
                  </>
                )}
              </div>
            )}
          </TableContainer>
        )}
      </CarbonDataTable>
    );
  })();

  return (
    <div>
      {filters.length > 0 && (
        <div className="ao-actions ao-block" aria-label="Active filters">
          {filters.map((f) => <DismissibleTag key={f.label} type="gray" text={f.label} onClose={f.clear} title="Remove filter" />)}
          {filters.length > 1 && <Button kind="ghost" size="sm" onClick={clearAll}>Clear all</Button>}
        </div>
      )}
      {exportError && <ActionableNotification kind="error" lowContrast title="Export failed" subtitle={exportError} hideCloseButton />}
      {body}
      {!p.loading && !p.error && (
        <>
          <p className="ao-footnote" aria-live="polite">
            {totalCount === 0 ? "0 records" : server || hasConstraint
              ? <>Showing {from}–{to} of {totalCount} {hasConstraint ? "matching records" : "records"}</>
              : <>{p.rows.length} records</>}
            {server
              ? p.totalsNode && totalCount > 0 && <> · {hasConstraint ? "Totals for all matching records: " : "Totals for all records: "}{p.totalsNode}</>
              : p.totals && filtered.length > 0 && <> · {hasConstraint ? "Filtered totals: " : "Totals: "}{p.totals(filtered)}</>}
          </p>
          <Pagination
            page={current + 1} pageSize={pageSize} pageSizes={sizes} totalItems={totalCount} size={size === "sm" ? "sm" : "md"}
            itemsPerPageText="Rows per page" onChange={({ page, pageSize: ps }) => {
              if (ps !== pageSize) { if (server) server.onState({ ...server.state, pageSize: ps, page: 0 }); else setLocalPageSize(ps); return; }
              if (server) server.onState({ ...server.state, page: page - 1 }); else setPage(page - 1);
            }}
          />
        </>
      )}
    </div>
  );
}
