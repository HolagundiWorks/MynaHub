import { afterEach, describe, expect, it } from "vitest";
import { eq } from "drizzle-orm";
import { events, memberships } from "../src/db/schema/index.js";
import { suspendMember } from "../src/members.js";
import type { Principal } from "../src/principal.js";
import { addMember, bootstrapCompany, makeTestApp, signIn, type TestApp } from "./harness.js";

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});

const me = (app: TestApp["app"], headers: Record<string, string>) => app.inject({ url: "/v1/me", headers });

async function membershipId(t: TestApp, userId: string) {
  const [m] = await t.db
    .select({ id: memberships.id })
    .from(memberships)
    .where(eq(memberships.authUserId, userId));
  return m?.id as string;
}

describe("suspension", () => {
  it("ends the person's sessions at once and blocks them until reactivated", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const staff = await addMember(t, c.session, "staff@company.example", c.roleId.Staff as string);
    expect((await me(t.app, staff.session.headers)).statusCode).toBe(200);

    const mid = await membershipId(t, staff.userId);
    const sus = await t.app.inject({
      method: "POST",
      url: `/v1/members/${mid}/suspend`,
      headers: c.session.headers,
    });
    expect(sus.statusCode).toBe(204);

    // The old token stops working on the very next request, no waiting for a cache to expire.
    const old = await me(t.app, staff.session.headers);
    expect(old.statusCode).toBe(401);
    // A new sign-in works at the identity layer but the company refuses the member.
    const again = await signIn(t.app, "staff@company.example", staff.password);
    const blocked = await me(t.app, again.headers);
    expect(blocked.statusCode).toBe(403);
    expect(blocked.json().error.code).toBe("MEMBERSHIP_SUSPENDED");

    const re = await t.app.inject({
      method: "POST",
      url: `/v1/members/${mid}/reactivate`,
      headers: c.session.headers,
    });
    expect(re.statusCode).toBe(204);
    expect((await me(t.app, again.headers)).statusCode).toBe(200);
  });

  it("is audited", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const staff = await addMember(t, c.session, "staff@company.example", c.roleId.Staff as string);
    const mid = await membershipId(t, staff.userId);
    await t.app.inject({ method: "POST", url: `/v1/members/${mid}/suspend`, headers: c.session.headers });
    await t.app.inject({ method: "POST", url: `/v1/members/${mid}/reactivate`, headers: c.session.headers });
    const actions = (await t.db.select().from(events)).map((e) => e.action);
    expect(actions).toEqual(expect.arrayContaining(["member.suspended", "member.reactivated"]));
  });

  it("cannot be done by someone without the permission, or to oneself", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const staff = await addMember(t, c.session, "staff@company.example", c.roleId.Staff as string);
    const other = await addMember(t, c.session, "other@company.example", c.roleId.Staff as string);
    const staffMid = await membershipId(t, staff.userId);
    const denied = await t.app.inject({
      method: "POST",
      url: `/v1/members/${staffMid}/suspend`,
      headers: other.session.headers,
    });
    expect(denied.statusCode).toBe(403);
    const ownerMid = await membershipId(t, c.userId);
    const self = await t.app.inject({
      method: "POST",
      url: `/v1/members/${ownerMid}/suspend`,
      headers: c.session.headers,
    });
    expect(self.statusCode).toBe(409);
    expect(self.json().error.code).toBe("CANNOT_SUSPEND_SELF");
  });

  it("protects Owners: an Administrator cannot suspend one, and the last Owner can never be suspended", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const admin = await addMember(t, c.session, "admin@company.example", c.roleId.Administrator as string);
    const ownerMid = await membershipId(t, c.userId);
    const r = await t.app.inject({
      method: "POST",
      url: `/v1/members/${ownerMid}/suspend`,
      headers: admin.session.headers,
    });
    expect(r.statusCode).toBe(403);

    // The guard itself, with a second Owner acting while only one Owner is active.
    const fake: Principal = {
      userId: "someone-else",
      email: "x",
      name: "x",
      twoFactorEnabled: true,
      organizationId: c.organizationId,
      membershipId: ownerMid, // holds the Owner role
      permissions: new Set(),
      sessionToken: "",
      sessionCreatedAt: new Date(),
    };
    await expect(suspendMember({ db: t.db, auth: t.auth }, fake, ownerMid, "req_test")).rejects.toMatchObject(
      { code: "LAST_OWNER" },
    );
  });

  it("asks for a fresh sign-in before suspending", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const staff = await addMember(t, c.session, "staff@company.example", c.roleId.Staff as string);
    await t.pg.query("update auth.session set created_at = now() - interval '1 hour'");
    const mid = await membershipId(t, staff.userId);
    const r = await t.app.inject({
      method: "POST",
      url: `/v1/members/${mid}/suspend`,
      headers: c.session.headers,
    });
    expect(r.statusCode).toBe(403);
    expect(r.json().error.code).toBe("REAUTH_REQUIRED");
  });

  it("lists members with their roles and status for administrators", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    await addMember(t, c.session, "staff@company.example", c.roleId.Staff as string);
    const list = (await t.app.inject({ url: "/v1/members", headers: c.session.headers })).json();
    expect(list).toHaveLength(2);
    expect(list.map((m: { email: string; roles: string[] }) => [m.email, m.roles[0]]).sort()).toEqual([
      ["owner@company.example", "Owner"],
      ["staff@company.example", "Staff"],
    ]);
  });
});

describe("sessions", () => {
  it("are per device: each sign-in is its own session and they can be listed", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const phone = await signIn(t.app, "owner@company.example", "correct horse battery staple");
    const r = await t.app.inject({ url: "/api/auth/list-sessions", headers: phone.headers });
    expect(r.statusCode).toBe(200);
    expect(r.json().length).toBeGreaterThanOrEqual(2);
    expect(c.session.token).not.toBe(phone.token);
  });

  it("can be revoked one at a time and take effect on the very next request", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const phone = await signIn(t.app, "owner@company.example", "correct horse battery staple");
    const sessions = (
      await t.app.inject({ url: "/api/auth/list-sessions", headers: c.session.headers })
    ).json() as { token: string }[];
    const phoneSession = sessions.find(
      (s) => phone.token.startsWith(s.token) || s.token.startsWith(phone.token.split(".")[0] as string),
    );
    expect(phoneSession).toBeDefined();
    const rev = await t.app.inject({
      method: "POST",
      url: "/api/auth/revoke-session",
      headers: c.session.headers,
      payload: { token: phoneSession?.token },
    });
    expect(rev.statusCode).toBe(200);
    expect((await me(t.app, phone.headers)).statusCode).toBe(401);
    expect((await me(t.app, c.session.headers)).statusCode).toBe(200);
  });

  it("can all be revoked except the current one, and sign-out ends the session", async () => {
    t = await makeTestApp();
    const c = await bootstrapCompany(t);
    const a = await signIn(t.app, "owner@company.example", "correct horse battery staple");
    const b = await signIn(t.app, "owner@company.example", "correct horse battery staple");
    const others = await t.app.inject({
      method: "POST",
      url: "/api/auth/revoke-other-sessions",
      headers: b.headers,
      payload: {},
    });
    expect(others.statusCode).toBe(200);
    expect((await me(t.app, a.headers)).statusCode).toBe(401);
    expect((await me(t.app, c.session.headers)).statusCode).toBe(401);
    expect((await me(t.app, b.headers)).statusCode).toBe(200);
    const out = await t.app.inject({
      method: "POST",
      url: "/api/auth/sign-out",
      headers: b.headers,
      payload: {},
    });
    expect(out.statusCode).toBe(200);
    expect((await me(t.app, b.headers)).statusCode).toBe(401);
  });

  it("are refused when forged or missing", async () => {
    t = await makeTestApp();
    await bootstrapCompany(t);
    expect((await me(t.app, { authorization: "Bearer forged.token.value" })).statusCode).toBe(401);
    expect((await me(t.app, {})).statusCode).toBe(401);
    const bad = await t.app.inject({
      method: "POST",
      url: "/api/auth/sign-in/email",
      payload: { email: "owner@company.example", password: "wrong password here" },
    });
    expect(bad.statusCode).toBeGreaterThanOrEqual(400);
    expect(bad.headers["set-auth-token"]).toBeUndefined();
  });
});
