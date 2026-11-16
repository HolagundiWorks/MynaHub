import { afterEach, describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import type { FastifyInstance } from "fastify";
import { buildApp } from "../src/app.js";
import { loadConfig } from "../src/config.js";
import type { DbHealth } from "../src/health.js";

const config = loadConfig({
  NODE_ENV: "test",
  PUBLIC_API_URL: "https://api.company.example",
  DATABASE_URL: "postgresql://u:p@db.example/x",
  BETTER_AUTH_SECRET: "x".repeat(40),
  LOG_LEVEL: "silent",
});

let app: FastifyInstance | undefined;
afterEach(async () => {
  await app?.close();
  app = undefined;
});

async function make(health: Partial<DbHealth> = {}, expectedMigrations = 2) {
  app = await buildApp({
    config,
    expectedMigrations,
    db: {} as never,
    auth: {} as never,
    version: "9.9.9",
    health: { ping: async () => {}, appliedMigrations: async () => 2, ...health },
  });
  return app;
}

describe("health", () => {
  it("advertises only implemented mobile capabilities, without database or account access", async () => {
    const a = await make({
      ping: async () => {
        throw new Error("offline");
      },
    });
    const response = await a.inject("/v1/mobile/connection");
    expect(response.statusCode).toBe(200);
    expect(response.json()).toEqual(
      JSON.parse(readFileSync(new URL("../contracts/mobile-connection-v1.json", import.meta.url), "utf8")),
    );
    expect(response.json()).toEqual({
      service: "myna-api",
      apiVersion: 1,
      minimumVersionCode: 8,
      authBasePath: "/api/auth",
      capabilities: [
        "identity",
        "invitations",
        "sessions",
        "two-factor",
        "projects.read",
        "projects.edit",
        "projects.live",
        "projects.create",
        "projects.delete",
        "tasks.live",
        "tasks.edit",
        "clients.live",
        "clients.edit",
        "tasks.actions",
        "sites.live",
        "sites.edit",
        "files.live",
        "files.upload",
      ],
    });
    const spec = (await a.inject("/openapi.json")).json();
    expect(spec.paths["/v1/mobile/connection"]).toBeDefined();
    expect(response.json().capabilities).not.toContain("sync");
  });
  it("answers liveness without touching the database", async () => {
    const a = await make({
      ping: async () => {
        throw new Error("database is down");
      },
    });
    const r = await a.inject("/health");
    expect(r.statusCode).toBe(200);
    expect(r.json()).toEqual({ status: "ok", service: "myna-api", version: "9.9.9" });
  });

  it("is ready when the database answers and every migration is applied", async () => {
    const r = await (await make()).inject("/health/ready");
    expect(r.statusCode).toBe(200);
    expect(r.json()).toEqual({ status: "ready", database: "ok", migrations: "current" });
  });

  it("is not ready while migrations are pending", async () => {
    const r = await (await make({ appliedMigrations: async () => 1 })).inject("/health/ready");
    expect(r.statusCode).toBe(503);
    expect(r.json().error.code).toBe("MIGRATIONS_PENDING");
  });

  it("is ready when the database has more migrations than this build (a newer deployment rolled back)", async () => {
    const r = await (await make({ appliedMigrations: async () => 5 })).inject("/health/ready");
    expect(r.statusCode).toBe(200);
  });

  it("reports an unreachable database without leaking the cause", async () => {
    const a = await make({
      ping: async () => {
        throw new Error("connect ECONNREFUSED postgresql://u:secret-pw@db.example:5432");
      },
    });
    const r = await a.inject("/health/ready");
    expect(r.statusCode).toBe(503);
    expect(r.json().error.code).toBe("DATABASE_UNAVAILABLE");
    expect(r.body).not.toContain("secret-pw");
    expect(r.body).not.toContain("ECONNREFUSED");
  });
});

describe("request handling", () => {
  it("gives every response its own request id, in the header and in errors", async () => {
    const a = await make();
    const ok = await a.inject("/health");
    expect(ok.headers["x-request-id"]).toMatch(/^req_[0-9a-f]{16}$/);
    const missing = await a.inject("/nope");
    expect(missing.statusCode).toBe(404);
    expect(missing.json()).toEqual({
      error: { code: "NOT_FOUND", message: "No such route.", requestId: missing.headers["x-request-id"] },
    });
  });

  it("ignores a client-supplied request id", async () => {
    const r = await (
      await make()
    ).inject({ url: "/health", headers: { "x-request-id": "forged\nlog-line" } });
    expect(r.headers["x-request-id"]).toMatch(/^req_[0-9a-f]{16}$/);
  });

  it("turns an unexpected failure into a generic 500 with no internal detail", async () => {
    const a = await make();
    a.get("/boom", async () => {
      throw new Error("stack and secret postgresql://u:hunter2@db");
    });
    const r = await a.inject("/boom");
    expect(r.statusCode).toBe(500);
    expect(r.json().error.code).toBe("INTERNAL");
    expect(r.body).not.toContain("hunter2");
    expect(r.body).not.toContain("stack");
  });

  it("never caches API responses", async () => {
    const r = await (await make()).inject("/health");
    expect(r.headers["cache-control"]).toBe("no-store");
  });

  it("publishes an OpenAPI 3.1 document that describes the routes", async () => {
    const r = await (await make()).inject("/openapi.json");
    expect(r.statusCode).toBe(200);
    const doc = r.json();
    expect(doc.openapi).toBe("3.1.0");
    expect(Object.keys(doc.paths)).toEqual(expect.arrayContaining(["/health", "/health/ready"]));
    expect(doc.servers[0].url).toBe("https://api.company.example");
  });
});
