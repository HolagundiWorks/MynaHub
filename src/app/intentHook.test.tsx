import { StrictMode, useState } from "react";
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { queueCreate, useCreateIntent } from "./intent";

function Opener() {
  const [opened, setOpened] = useState(false);
  useCreateIntent("clients", () => setOpened(true));
  return <p>{opened ? "Client form" : "Client list"}</p>;
}

describe("create intent under Strict Mode", () => {
  it("still opens the form after the development double mount", async () => {
    queueCreate("clients");
    render(<StrictMode><Opener /></StrictMode>);
    expect(await screen.findByText("Client form")).toBeInTheDocument();
  });
});
