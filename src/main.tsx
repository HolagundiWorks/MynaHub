import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./styles.scss";

if (import.meta.env.DEV) {
  // Browser preview only: the native bridge is absent outside the real app.
  await import("./dev/mockTauri").then((m) => m.installMock());
}

ReactDOM.createRoot(document.getElementById("root") as HTMLElement).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
