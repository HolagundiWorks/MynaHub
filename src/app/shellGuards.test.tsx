import { describe, expect, it, vi } from "vitest";
import { useState } from "react";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DraftGuard, UnsavedProvider, useLeaveGuard } from "./unsaved";
import { computerFinancialYear, headerFinancialYear } from "./financialYear";

vi.mock("../components/dialogs", () => ({
  confirmDialog: vi.fn(async () => false),
}));

import { confirmDialog } from "../components/dialogs";

function LeaveButton() {
  const leave = useLeaveGuard();
  return <button type="button" onClick={async () => { await leave(); }}>Leave</button>;
}

function Editor() {
  const [name, setName] = useState("");
  return (
    <form>
      <DraftGuard value={{ name }} />
      <label htmlFor="name">Name</label>
      <input id="name" value={name} onChange={(e) => setName(e.target.value)} />
      <LeaveButton />
    </form>
  );
}

describe("unsaved navigation", () => {
  it("leaves a clean form without asking", async () => {
    vi.mocked(confirmDialog).mockClear();
    render(<UnsavedProvider><Editor /></UnsavedProvider>);
    await userEvent.click(screen.getByRole("button", { name: "Leave" }));
    expect(confirmDialog).not.toHaveBeenCalled();
  });

  it("asks before leaving typed text and stays when the answer is no", async () => {
    vi.mocked(confirmDialog).mockClear();
    vi.mocked(confirmDialog).mockResolvedValue(false);
    render(<UnsavedProvider><Editor /></UnsavedProvider>);
    await userEvent.type(screen.getByLabelText("Name"), "Holagundi");
    await userEvent.click(screen.getByRole("button", { name: "Leave" }));
    expect(confirmDialog).toHaveBeenCalledOnce();
    expect(screen.getByLabelText("Name")).toHaveValue("Holagundi");
  });
});

describe("header financial year", () => {
  it("uses the period that contains today", () => {
    const periods = [
      { fy: "2024-25", start_date: "2024-04-01", end_date: "2025-03-31", status: "closed" },
      { fy: "2025-26", start_date: "2025-04-01", end_date: "2026-03-31", status: "open" },
    ];
    expect(headerFinancialYear(periods, "2025-10-13", "2099-00")).toBe("2025-26");
  });

  it("uses the latest open period when today is outside every range", () => {
    const periods = [
      { fy: "2023-24", start_date: "2023-04-01", end_date: "2024-03-31", status: "open" },
      { fy: "2024-25", start_date: "2024-04-01", end_date: "2025-03-31", status: "closed" },
    ];
    expect(headerFinancialYear(periods, "2026-10-13", "2026-27")).toBe("2023-24");
  });

  it("falls back to the computer year when the file has no periods", () => {
    expect(headerFinancialYear([], "2026-10-13", "2026-27")).toBe("2026-27");
    expect(computerFinancialYear(new Date(2026, 9, 13))).toBe("2026-27");
    expect(computerFinancialYear(new Date(2026, 2, 1))).toBe("2025-26");
  });
});
