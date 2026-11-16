import { afterEach, describe, expect, it } from "vitest";
import { count } from "drizzle-orm";
import { events, invitations, memberships, user } from "../src/db/schema/index.js";
import { addMember, bootstrapCompany, makeTestApp, signIn, type TestApp } from "./harness.js";

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});

const invite = (app: TestApp["app"], headers: Record<string, string>, email: string, roleId: string) =>
  app.inject({ method: "POST", url: "/v1/invitations", headers, payload: { email, roleId } });
const accept = (app: TestApp["app"], token: string, extra: Record<string, unknown> = {}) =>
  app.inject({
    method: "POST",
    url: "/v1/invitations/accept",
    payload: { token, name: "New Person", password: "a long enough passphrase 1", ...extra },
  });

describe("invitations", () => {
  it("lets an administrator invite, the person accept once, and then sign in with the invited role only", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const inv = await invite(t.app, c.session.headers, "Staff@Company.example", c.roleId.Staff as string);
    expect(inv.statusCode).toBe(201);
    const { token, invitationId } = inv.json();
    expect(token.length).toBeGreaterThanOrEqual(40);
    const acc = await accept(t.app, token);
    expect(acc.statusCode).toBe(201);
    expect(acc.json().email).toBe("staff@company.example");

    const s = await signIn(t.app, "staff@company.example", "a long enough passphrase 1");
    const me = (await t.app.inject({ url: "/v1/me", headers: s.headers })).json();
    expect(me.roles).toEqual(["Staff"]);
    expect(me.permissions).toEqual(["organization.read", "projects.read"]);

    const list = await t.app.inject({ url: "/v1/invitations", headers: c.session.headers });
    const row = list.json().find((r: { id: string }) => r.id === invitationId);
    expect(row.status).toBe("accepted");
    expect(row).not.toHaveProperty("token");
    expect(row).not.toHaveProperty("tokenHash");
  });

  it("stores only a hash of the invitation token", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const { token } = (
      await invite(t.app, c.session.headers, "a@company.example", c.roleId.Staff as string)
    ).json();
    const rows = await t.db.select().from(invitations);
    expect(JSON.stringify(rows)).not.toContain(token);
    expect(rows[0]?.tokenHash).toMatch(/^[0-9a-f]{64}$/);
    expect(JSON.stringify((await t.pg.query("select * from audit.events")).rows)).not.toContain(token);
  });

  it("cannot be used twice", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const { token } = (
      await invite(t.app, c.session.headers, "a@company.example", c.roleId.Staff as string)
    ).json();
    expect((await accept(t.app, token)).statusCode).toBe(201);
    const again = await accept(t.app, token, { name: "Someone Else" });
    expect(again.statusCode).toBe(400);
    expect(again.json().error.code).toBe("INVITATION_INVALID");
    expect((await t.db.select({ n: count() }).from(user))[0]?.n).toBe(2);
  });

  it("fails the same way for a wrong, expired or revoked token", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const expired = (
      await invite(t.app, c.session.headers, "old@company.example", c.roleId.Staff as string)
    ).json();
    await t.pg.query(
      "update core.invitations set created_at = now() - interval '10 days', expires_at = now() - interval '3 days'",
    );
    const revoked = (
      await invite(t.app, c.session.headers, "gone@company.example", c.roleId.Staff as string)
    ).json();
    const rev = await t.app.inject({
      method: "POST",
      url: `/v1/invitations/${revoked.invitationId}/revoke`,
      headers: c.session.headers,
    });
    expect(rev.statusCode).toBe(204);

    for (const token of [expired.token, revoked.token, "z".repeat(43)]) {
      const r = await accept(t.app, token);
      expect(r.statusCode).toBe(400);
      expect(r.json().error).toMatchObject({
        code: "INVITATION_INVALID",
        message: "This invitation is not valid.",
      });
    }
    expect((await t.db.select({ n: count() }).from(user))[0]?.n).toBe(1);
  });

  it("keeps the invitation usable after a weak password is refused", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const { token } = (
      await invite(t.app, c.session.headers, "a@company.example", c.roleId.Staff as string)
    ).json();
    const weak = await accept(t.app, token, { password: "short" });
    expect(weak.statusCode).toBe(400);
    expect(weak.json().error.code).toBe("VALIDATION_FAILED");
    expect((await accept(t.app, token)).statusCode).toBe(201);
  });

  it("gives one live token per person: inviting again revokes the earlier one", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const first = (
      await invite(t.app, c.session.headers, "a@company.example", c.roleId.Staff as string)
    ).json();
    const second = (
      await invite(t.app, c.session.headers, "a@company.example", c.roleId.Viewer as string)
    ).json();
    expect((await accept(t.app, first.token)).statusCode).toBe(400);
    expect((await accept(t.app, second.token)).statusCode).toBe(201);
    const s = await signIn(t.app, "a@company.example", "a long enough passphrase 1");
    expect((await t.app.inject({ url: "/v1/me", headers: s.headers })).json().roles).toEqual(["Viewer"]);
  });

  it("refuses an email that already has an account and a role that does not exist", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const dup = await invite(t.app, c.session.headers, "OWNER@company.example", c.roleId.Staff as string);
    expect(dup.statusCode).toBe(409);
    expect(dup.json().error.code).toBe("ACCOUNT_EXISTS");
    const noRole = await invite(
      t.app,
      c.session.headers,
      "b@company.example",
      "00000000-0000-4000-8000-000000000000",
    );
    expect(noRole.statusCode).toBe(404);
  });

  it("is refused to anyone who is not signed in or may not manage members", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    expect(
      (
        await t.app.inject({
          method: "POST",
          url: "/v1/invitations",
          payload: { email: "a@b.example", roleId: c.roleId.Staff },
        })
      ).statusCode,
    ).toBe(401);
    const staff = await addMember(t, c.session, "staff@company.example", c.roleId.Staff as string);
    const r = await invite(t.app, staff.session.headers, "x@company.example", c.roleId.Staff as string);
    expect(r.statusCode).toBe(403);
    expect(r.json().error.code).toBe("PERMISSION_DENIED");
    expect((await t.app.inject({ url: "/v1/invitations", headers: staff.session.headers })).statusCode).toBe(
      403,
    );
    expect((await t.app.inject({ url: "/v1/members", headers: staff.session.headers })).statusCode).toBe(403);
  });

  it("prevents role escalation: an Administrator cannot invite an Owner, an Owner can", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const admin = await addMember(t, c.session, "admin@company.example", c.roleId.Administrator as string);
    const denied = await invite(
      t.app,
      admin.session.headers,
      "boss@company.example",
      c.roleId.Owner as string,
    );
    expect(denied.statusCode).toBe(403);
    const ok = await invite(t.app, c.session.headers, "boss@company.example", c.roleId.Owner as string);
    expect(ok.statusCode).toBe(201);
    // An Administrator may still invite ordinary roles.
    expect(
      (
        await invite(
          t.app,
          admin.session.headers,
          "pm@company.example",
          c.roleId["Project Manager"] as string,
        )
      ).statusCode,
    ).toBe(201);
  });

  it("lets only one of two simultaneous acceptances win", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const { token } = (
      await invite(t.app, c.session.headers, "a@company.example", c.roleId.Staff as string)
    ).json();
    const [x, y] = await Promise.all([accept(t.app, token), accept(t.app, token)]);
    expect([x.statusCode, y.statusCode].filter((s) => s === 201)).toHaveLength(1);
    expect((await t.db.select({ n: count() }).from(user))[0]?.n).toBe(2);
    expect((await t.db.select({ n: count() }).from(memberships))[0]?.n).toBe(2);
  });

  it("writes audit events with no token or password in them", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const { token } = (
      await invite(t.app, c.session.headers, "a@company.example", c.roleId.Staff as string)
    ).json();
    await accept(t.app, token);
    const rows = await t.db.select().from(events);
    const actions = rows.map((r) => r.action);
    expect(actions).toEqual(
      expect.arrayContaining(["bootstrap.completed", "invitation.created", "invitation.accepted"]),
    );
    const text = JSON.stringify(rows);
    expect(text).not.toContain(token);
    expect(text).not.toContain("a long enough passphrase 1");
  });
});
