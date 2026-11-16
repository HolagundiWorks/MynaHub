import { afterEach, describe, expect, it } from "vitest";
import { count, eq } from "drizzle-orm";
import { createBootstrapToken } from "../src/bootstrap.js";
import { bootstrapTokens, events, memberships, organizations, user } from "../src/db/schema/index.js";
import { ADMIN, bootstrapCompany, makeTestApp, signIn, type TestApp } from "./harness.js";

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});

const payload = (setupToken: string, extra: Record<string, unknown> = {}) => ({
  setupToken,
  organizationName: "Sample Architects",
  slug: "sample-architects",
  adminName: ADMIN.name,
  adminEmail: ADMIN.email,
  adminPassword: ADMIN.password,
  ...extra,
});

describe("public registration", () => {
  it("is switched off: nobody can create an account through the auth routes", async () => {
    t = await makeTestApp();
    const r = await t.app.inject({
      method: "POST",
      url: "/api/auth/sign-up/email",
      payload: { email: "stranger@example.com", password: "a long enough passphrase", name: "Stranger" },
    });
    expect(r.statusCode).toBeGreaterThanOrEqual(400);
    expect(r.statusCode).toBeLessThan(500);
    const [n] = await t.db.select({ n: count() }).from(user);
    expect(n?.n).toBe(0);
  });
});

describe("bootstrap", () => {
  it("creates the company, cloud and desktop system templates and an active Owner", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    expect(Object.keys(c.roleId).sort()).toEqual(
      [
        "Administrator",
        "Architect / Engineer",
        "Principal",
        "Accounts",
        "Document editor",
        "Auditor",
        "Owner",
        "Project Manager",
        "Site Supervisor",
        "Staff",
        "Viewer",
      ].sort(),
    );
    const me = await t.app.inject({ url: "/v1/me", headers: c.session.headers });
    expect(me.statusCode).toBe(200);
    const body = me.json();
    expect(body.user.email).toBe(ADMIN.email);
    expect(body.roles).toEqual(["Owner"]);
    expect(body.permissions).toEqual([
      "audit.read",
      "organization.members.manage",
      "organization.read",
      "projects.assign",
      "projects.read",
      "projects.write",
      "roles.manage",
      "roles.read",
    ]);
    expect(body.organization.name).toBe("Sample Architects");
  });

  it("stores only a hash of the setup token and never the password", async () => {
    t = await makeTestApp();
    const { token } = await createBootstrapToken(t.db);
    const rows = await t.db.select().from(bootstrapTokens);
    expect(rows).toHaveLength(1);
    expect(JSON.stringify(rows)).not.toContain(token);
    expect(rows[0]?.tokenHash).toMatch(/^[0-9a-f]{64}$/);
    const dump = JSON.stringify((await t.pg.query("select * from audit.events")).rows);
    expect(dump).not.toContain(token);
  });

  it("refuses a wrong, missing or malformed token with one generic error", async () => {
    t = await makeTestApp();
    await createBootstrapToken(t.db);
    for (const bad of ["x".repeat(43), "not-a-token-at-all-not-a-token-at-all"]) {
      const r = await t.app.inject({ method: "POST", url: "/v1/bootstrap", payload: payload(bad) });
      expect(r.statusCode).toBe(400);
      expect(r.json().error.code).toBe("SETUP_TOKEN_INVALID");
    }
    const short = await t.app.inject({ method: "POST", url: "/v1/bootstrap", payload: payload("short") });
    expect(short.json().error.code).toBe("VALIDATION_FAILED");
    expect((await t.db.select({ n: count() }).from(user))[0]?.n).toBe(0);
  });

  it("refuses an expired token", async () => {
    t = await makeTestApp();
    const { token } = await createBootstrapToken(t.db);
    await t.pg.query(
      "update core.bootstrap_tokens set created_at = now() - interval '2 hours', expires_at = now() - interval '1 hour'",
    );
    const r = await t.app.inject({ method: "POST", url: "/v1/bootstrap", payload: payload(token) });
    expect(r.statusCode).toBe(400);
    expect(r.json().error.code).toBe("SETUP_TOKEN_INVALID");
  });

  it("can be used only once, and is then closed for good", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const again = await t.app.inject({
      method: "POST",
      url: "/v1/bootstrap",
      payload: payload(c.setupToken, { adminEmail: "second@company.example" }),
    });
    expect(again.statusCode).toBe(409);
    expect(again.json().error.code).toBe("BOOTSTRAP_COMPLETE");
    // Even a brand new valid token cannot be made once the company exists.
    await expect(createBootstrapToken(t.db)).rejects.toMatchObject({ code: "BOOTSTRAP_COMPLETE" });
    const [orgs] = await t.db.select({ n: count() }).from(organizations);
    expect(orgs?.n).toBe(1);
  });

  it("creates exactly one company when two requests race with the same token", async () => {
    t = await makeTestApp();
    const { token } = await createBootstrapToken(t.db);
    const [a, b] = await Promise.all([
      t.app.inject({
        method: "POST",
        url: "/v1/bootstrap",
        payload: payload(token, { adminEmail: "a@company.example" }),
      }),
      t.app.inject({
        method: "POST",
        url: "/v1/bootstrap",
        payload: payload(token, { adminEmail: "b@company.example" }),
      }),
    ]);
    const codes = [a.statusCode, b.statusCode];
    expect(codes.filter((c) => c === 201)).toHaveLength(1);
    const [orgs] = await t.db.select({ n: count() }).from(organizations);
    expect(orgs?.n).toBe(1);
    const [members] = await t.db.select({ n: count() }).from(memberships);
    expect(members?.n).toBe(1);
    const [users] = await t.db.select({ n: count() }).from(user);
    expect(users?.n).toBe(1); // the loser's account was removed
  });

  it("a new token replaces the older unused one", async () => {
    t = await makeTestApp();
    const first = await createBootstrapToken(t.db);
    const second = await createBootstrapToken(t.db);
    const old = await t.app.inject({ method: "POST", url: "/v1/bootstrap", payload: payload(first.token) });
    expect(old.statusCode).toBe(400);
    const fresh = await t.app.inject({
      method: "POST",
      url: "/v1/bootstrap",
      payload: payload(second.token),
    });
    expect(fresh.statusCode).toBe(201);
  });

  it("rejects a weak administrator password and creates nothing", async () => {
    t = await makeTestApp();
    const { token } = await createBootstrapToken(t.db);
    const r = await t.app.inject({
      method: "POST",
      url: "/v1/bootstrap",
      payload: payload(token, { adminPassword: "short1" }),
    });
    expect(r.statusCode).toBe(400);
    expect((await t.db.select({ n: count() }).from(organizations))[0]?.n).toBe(0);
    expect((await t.db.select({ n: count() }).from(user))[0]?.n).toBe(0);
  });

  it("records an audit event without any secret in it", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const rows = await t.db.select().from(events).where(eq(events.action, "bootstrap.completed"));
    expect(rows).toHaveLength(1);
    const text = JSON.stringify(rows);
    expect(text).not.toContain(ADMIN.password);
    expect(text).not.toContain(c.setupToken);
  });

  it("gives no default account: before bootstrap nobody can sign in and protected routes say the company is not set up", async () => {
    t = await makeTestApp();
    const r = await t.app.inject({
      method: "POST",
      url: "/api/auth/sign-in/email",
      payload: { email: ADMIN.email, password: ADMIN.password },
    });
    expect(r.statusCode).toBeGreaterThanOrEqual(400);
    const me = await t.app.inject({ url: "/v1/me" });
    expect(me.statusCode).toBe(401);
  });

  it("signs in with a cookie as well as with a bearer token", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const s = await signIn(t.app, ADMIN.email, ADMIN.password);
    const viaCookie = await t.app.inject({ url: "/v1/me", headers: { cookie: s.cookie } });
    expect(viaCookie.statusCode).toBe(200);
    expect(c.userId).toBe(viaCookie.json().user.id);
  });
});
