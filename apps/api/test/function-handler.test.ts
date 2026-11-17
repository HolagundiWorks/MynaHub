import Fastify from "fastify";
import { afterEach, describe, expect, it } from "vitest";
import { functionHandler } from "../src/function-handler.js";

const apps: ReturnType<typeof Fastify>[] = [];
afterEach(async () => {
  await Promise.all(apps.splice(0).map((app) => app.close()));
});
function setup() {
  const app = Fastify({ bodyLimit: 7 * 1024 * 1024 });
  apps.push(app);
  return app;
}

describe("Neon Fetch transport", () => {
  it("preserves methods, query strings, bearer credentials, JSON and separate session cookies", async () => {
    const app = setup();
    app.post("/write", async (req, reply) => {
      reply.header("set-cookie", ["one=a; HttpOnly; Secure", "two=b; HttpOnly; Secure"]);
      reply.header("cache-control", "no-store");
      return {
        body: req.body,
        query: req.query,
        auth: req.headers.authorization,
        host: req.headers.host,
        forwarded: req.headers["x-forwarded-for"] ?? null,
      };
    });
    const response = await functionHandler(app)(
      new Request("https://public.example/write?after=a%2Fb", {
        method: "POST",
        headers: {
          "content-type": "application/json",
          authorization: "Bearer fictional-test-session",
          host: "evil.example",
          "x-forwarded-for": "192.0.2.1",
        },
        body: JSON.stringify({ name: "Fictional" }),
      }),
    );
    expect(response.status).toBe(200);
    const body = await response.json();
    expect(body.body).toEqual({ name: "Fictional" });
    expect(body.query).toEqual({ after: "a/b" });
    expect(body.auth).toBe("Bearer fictional-test-session");
    expect(body.host).not.toBe("evil.example");
    expect(body.forwarded).toBeNull();
    expect(response.headers.getSetCookie()).toEqual(["one=a; HttpOnly; Secure", "two=b; HttpOnly; Secure"]);
    expect(response.headers.get("cache-control")).toBe("no-store");
  });
  it("keeps binary response bytes and handles HEAD and empty response statuses", async () => {
    const app = setup();
    app.get("/binary", async (_req, reply) =>
      reply.type("application/octet-stream").send(Buffer.from([0, 255, 128])),
    );
    app.get("/empty", async (_req, reply) => reply.code(204).send());
    const handle = functionHandler(app);
    expect([
      ...new Uint8Array(await (await handle(new Request("https://public.example/binary"))).arrayBuffer()),
    ]).toEqual([0, 255, 128]);
    expect((await handle(new Request("https://public.example/binary", { method: "HEAD" }))).body).toBeNull();
    expect(await handle(new Request("https://public.example/empty"))).toMatchObject({
      status: 204,
      body: null,
    });
  });
  it("rejects declared and streamed oversized bodies before executing any route", async () => {
    const app = setup();
    let calls = 0;
    app.post("/upload", async () => {
      calls++;
      return {};
    });
    const handle = functionHandler(app);
    const declared = await handle(
      new Request("https://public.example/upload", {
        method: "POST",
        headers: { "content-length": String(7 * 1024 * 1024 + 1) },
        body: "x",
      }),
    );
    expect(declared.status).toBe(413);
    const streamed = await handle(
      new Request("https://public.example/upload", {
        method: "POST",
        body: new Uint8Array(7 * 1024 * 1024 + 1),
      }),
    );
    expect(streamed.status).toBe(413);
    expect(calls).toBe(0);
  });
  it("does not expose internal errors or accept an aborted request", async () => {
    const app = setup();
    const controller = new AbortController();
    controller.abort();
    const response = await functionHandler(app)(
      new Request("https://public.example/", { signal: controller.signal }),
    );
    expect(response.status).toBe(408);
    expect(await response.text()).toBe("");
  });
});
