import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { consumeCreate } from "../../app/intent";
import DashboardPage from "./DashboardPage";

vi.mock("../../api", () => ({
  errorText: (e: unknown) => (e instanceof Error ? e.message : String(e)),
  api: {
    clientList: vi.fn(async () => [{ id: 1 }, { id: 2 }]),
    projectList: vi.fn(async () => [
      { archived: false, status: "active" },
      { archived: false, status: "completed" },
    ]),
    docPage: vi.fn(async () => ({ total: 3, rows: [], sums: {} })),
    invoicePage: vi.fn(async (q: { filters?: { status?: string } }) => (
      q.filters?.status === "draft"
        ? { total: 1, rows: [], sums: {} }
        : { total: 4, rows: [], sums: { outstanding: 7440000 } }
    )),
    bankSummary: vi.fn(async () => ({ unmatched: 4, partial: 1, matched: 2, ignored: 0, duplicate: 0, rejected_rows: 2, unallocated_credits_display: "₹0.00" })),
    auditPage: vi.fn(async () => ({
      rows: [{ id: 9, at: "2026-10-13 09:00", user: "owner", event: "client_saved", detail: "Studio" }],
      total: 1,
      sums: {},
    })),
  },
}));

describe("dashboard", () => {
  it("shows figures returned by the company file and starts a new client", async () => {
    const onOpen = vi.fn(async () => true);
    render(<DashboardPage role="Owner" company="Holagundi Consulting Works" fy="2026-27" onOpen={onOpen} />);
    expect(await screen.findByText("Holagundi Consulting Works · Financial year 2026-27. Figures come from the open company file.")).toBeInTheDocument();
    expect(await screen.findByText("2")).toBeInTheDocument();
    expect(screen.getByText("₹74,400.00")).toBeInTheDocument();
    expect(screen.getByText(/client saved/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "New client" }));
    expect(onOpen).toHaveBeenCalledWith("clients");
    expect(consumeCreate("clients")).toBe(true);
  });

  it("hides write actions from an auditor and does not invent activity", async () => {
    render(<DashboardPage role="Auditor" company="Holagundi Consulting Works" fy="2026-27" onOpen={async () => true} />);
    expect(await screen.findByText("Clients")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "New client" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Record expense" })).not.toBeInTheDocument();
  });
});
