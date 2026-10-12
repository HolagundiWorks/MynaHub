import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import DataTable, { type Column } from "./DataTable";

type Row = { id: number; name: string; amount: number };
const rows: Row[] = [
  { id: 1, name: "Bravo", amount: 1000 },
  { id: 2, name: "alpha", amount: 25 },
  { id: 3, name: "Charlie", amount: 300 },
];
const columns: Column<Row>[] = [
  { id: "name", header: "Name", locked: true, sortValue: (r) => r.name.toLowerCase(), render: (r) => r.name },
  // Display text is deliberately unsortable as text ("₹1,000" < "₹25" alphabetically): sorting must use the raw number.
  { id: "amount", header: "Amount", align: "end", sortValue: (r) => r.amount, render: (r) => `₹${r.amount.toLocaleString("en-IN")}` },
];
const bodyNames = () => screen.getAllByRole("row").slice(1).map((r) => within(r).getAllByRole("cell")[0].textContent);

describe("DataTable (local mode)", () => {
  it("sorts by the raw value, not the formatted text", async () => {
    render(<DataTable id="t1" rows={rows} columns={columns} rowKey={(r) => r.id} searchText={(r) => r.name} />);
    await userEvent.click(screen.getByRole("button", { name: /amount/i }));
    expect(bodyNames()).toEqual(["alpha", "Charlie", "Bravo"]);
    await userEvent.click(screen.getByRole("button", { name: /amount/i }));
    expect(bodyNames()).toEqual(["Bravo", "Charlie", "alpha"]);
  });

  it("searches, reports the result count, and recovers from no results", async () => {
    render(<DataTable id="t2" rows={rows} columns={columns} rowKey={(r) => r.id} searchText={(r) => r.name} />);
    expect(screen.getByText(/3 records/)).toBeInTheDocument();
    await userEvent.type(screen.getByRole("searchbox"), "char");
    expect(bodyNames()).toEqual(["Charlie"]);
    expect(screen.getByText(/Showing 1–1 of 1 matching records/)).toBeInTheDocument();
    await userEvent.clear(screen.getByRole("searchbox"));
    await userEvent.type(screen.getByRole("searchbox"), "zzz");
    expect(screen.getByText("No results for this search")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /clear search and filters/i }));
    expect(bodyNames()).toHaveLength(3);
  });

  it("shows removable filter tags and clears them", async () => {
    const clear = vi.fn();
    render(<DataTable id="t3" rows={rows} columns={columns} rowKey={(r) => r.id} activeFilters={[{ label: "Status: draft", clear }]} />);
    await userEvent.click(screen.getByText("Status: draft").closest(".cds--tag")!.querySelector("button")!);
    expect(clear).toHaveBeenCalled();
  });

  it("keeps an error distinct from an empty register", () => {
    const retry = vi.fn();
    render(<DataTable id="t4" rows={[]} columns={columns} rowKey={(r) => r.id} error="database unavailable" onRetry={retry} emptyTitle="No records yet" />);
    expect(screen.getByText("Could not load these records")).toBeInTheDocument();
    expect(screen.queryByText("No records yet")).not.toBeInTheDocument();
  });

  it("shows the empty state for a register that has no records", () => {
    render(<DataTable id="t5" rows={[]} columns={columns} rowKey={(r) => r.id} emptyTitle="No records yet" emptyHint="Add one." />);
    expect(screen.getByText("No records yet")).toBeInTheDocument();
  });

  it("right-aligns amount columns and never shows an action label as visible text", () => {
    render(<DataTable id="t6" rows={rows} columns={columns} rowKey={(r) => r.id} rowActions={() => <button>Open</button>} />);
    expect(screen.getAllByRole("columnheader")[1]).toHaveClass("ao-num");
    expect(screen.getAllByText("Open")).toHaveLength(3);
  });
});

describe("DataTable (server mode)", () => {
  it("asks the server for the next page and for sorting instead of doing it locally", async () => {
    const onState = vi.fn();
    render(
      <DataTable
        id="s1" rows={rows} columns={columns} rowKey={(r) => r.id}
        server={{ total: 230, state: { search: "", sort: { id: "name", dir: "asc" }, page: 0, pageSize: 50 }, onState }}
        totalsNode={<strong>₹1,325.00</strong>}
      />,
    );
    expect(screen.getByText(/Showing 1–50 of 230 records/)).toBeInTheDocument();
    expect(screen.getByText("₹1,325.00")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /next page/i }));
    expect(onState).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1 }));
    await userEvent.click(screen.getByRole("button", { name: /amount/i }));
    expect(onState).toHaveBeenLastCalledWith(expect.objectContaining({ sort: { id: "amount", dir: "asc" }, page: 0 }));
    // rows are shown exactly as the server returned them (no local re-sorting)
    expect(bodyNames()).toEqual(["Bravo", "alpha", "Charlie"]);
  });
});
