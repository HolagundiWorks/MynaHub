import { describe, expect, it } from "vitest";
import { consumeCreate, queueCreate } from "./intent";

describe("create intent", () => {
  it("opens a new record only on the page that was requested", () => {
    queueCreate("projects");
    expect(consumeCreate("clients")).toBe(false);
    expect(consumeCreate("projects")).toBe(true);
    expect(consumeCreate("projects")).toBe(false);
  });

  it("does not keep an intent when navigation is abandoned", () => {
    queueCreate("invoices");
    queueCreate("clients");
    expect(consumeCreate("clients")).toBe(true);
    expect(consumeCreate("invoices")).toBe(false);
  });
});
