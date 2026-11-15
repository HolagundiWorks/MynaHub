import { afterEach, describe, expect, it } from "vitest";
import {
  ADMIN,
  bootstrapCompany,
  enableTwoFactor,
  makeTestApp,
  secretFromUri,
  signIn,
  totp,
  type TestApp,
} from "./harness.js";

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});

const inviteReq = (app: TestApp["app"], headers: Record<string, string>, roleId: string) =>
  app.inject({
    method: "POST",
    url: "/v1/invitations",
    headers,
    payload: { email: "new@company.example", roleId },
  });
const cookieOf = (r: { headers: Record<string, unknown> }) =>
  ([] as string[])
    .concat((r.headers["set-cookie"] as string | string[] | undefined) ?? [])
    .map((x) => x.split(";")[0])
    .join("; ");

describe("administrator two-factor authentication", () => {
  it("blocks administrative endpoints until the administrator has turned two-factor on", async () => {
    t = await makeTestApp({ REQUIRE_ADMIN_MFA: "true" });
    const c = await bootstrapCompany(t);
    const blocked = await inviteReq(t.app, c.session.headers, c.roleId.Staff as string);
    expect(blocked.statusCode).toBe(403);
    expect(blocked.json().error.code).toBe("MFA_REQUIRED");
    // Looking at one's own account is unaffected.
    expect((await t.app.inject({ url: "/v1/me", headers: c.session.headers })).statusCode).toBe(200);
  });

  it("enables with the password, verifies a real code, then unlocks the administrative endpoints", async () => {
    t = await makeTestApp({ REQUIRE_ADMIN_MFA: "true" });
    const c = await bootstrapCompany(t);

    const wrong = await t.app.inject({
      method: "POST",
      url: "/api/auth/two-factor/enable",
      headers: c.session.headers,
      payload: { password: "not my password" },
    });
    expect(wrong.statusCode).toBeGreaterThanOrEqual(400);

    const enable = await t.app.inject({
      method: "POST",
      url: "/api/auth/two-factor/enable",
      headers: c.session.headers,
      payload: { password: ADMIN.password },
    });
    expect(enable.statusCode).toBe(200);
    const { totpURI, backupCodes } = enable.json() as { totpURI: string; backupCodes: string[] };
    expect(backupCodes.length).toBeGreaterThan(0);

    // Until a code is verified, two-factor is not on yet.
    expect((await inviteReq(t.app, c.session.headers, c.roleId.Staff as string)).json().error.code).toBe(
      "MFA_REQUIRED",
    );

    const bad = await t.app.inject({
      method: "POST",
      url: "/api/auth/two-factor/verify-totp",
      headers: c.session.headers,
      payload: { code: "000000" },
    });
    expect(bad.statusCode).toBeGreaterThanOrEqual(400);
    const good = await t.app.inject({
      method: "POST",
      url: "/api/auth/two-factor/verify-totp",
      headers: c.session.headers,
      payload: { code: totp(secretFromUri(totpURI)) },
    });
    expect(good.statusCode).toBe(200);

    // Verifying rotates the session: the old token is dead, the new one (in set-auth-token) works.
    expect((await t.app.inject({ url: "/v1/me", headers: c.session.headers })).statusCode).toBe(401);
    const next = { authorization: `Bearer ${good.headers["set-auth-token"] as string}` };
    const me = (await t.app.inject({ url: "/v1/me", headers: next })).json();
    expect(me.user.twoFactorEnabled).toBe(true);
    expect((await inviteReq(t.app, next, c.roleId.Staff as string)).statusCode).toBe(201);
  });

  it("asks for the second factor at sign-in and gives no usable session without it", async () => {
    t = await makeTestApp({ REQUIRE_ADMIN_MFA: "true" });
    const c = await bootstrapCompany(t);
    const { secret, backupCodes } = await enableTwoFactor(t.app, c.session, ADMIN.password);

    const first = await t.app.inject({
      method: "POST",
      url: "/api/auth/sign-in/email",
      payload: { email: ADMIN.email, password: ADMIN.password },
    });
    expect(first.statusCode).toBe(200);
    expect(first.json().twoFactorRedirect).toBe(true);
    // The response may carry a token header, but it is not a session: nothing accepts it.
    const pendingToken = first.headers["set-auth-token"] as string | undefined;
    if (pendingToken)
      expect(
        (await t.app.inject({ url: "/v1/me", headers: { authorization: `Bearer ${pendingToken}` } }))
          .statusCode,
      ).toBe(401);
    const pending = cookieOf(first);
    expect((await t.app.inject({ url: "/v1/me", headers: { cookie: pending } })).statusCode).toBe(401);

    const second = await t.app.inject({
      method: "POST",
      url: "/api/auth/two-factor/verify-totp",
      headers: { cookie: pending },
      payload: { code: totp(secret, Date.now() + 30_000) },
    });
    expect(second.statusCode).toBe(200);
    const token = second.headers["set-auth-token"] as string;
    expect(
      (await t.app.inject({ url: "/v1/me", headers: { authorization: `Bearer ${token}` } })).statusCode,
    ).toBe(200);

    // A backup code works once.
    const code = backupCodes[0] as string;
    const again = await t.app.inject({
      method: "POST",
      url: "/api/auth/sign-in/email",
      payload: { email: ADMIN.email, password: ADMIN.password },
    });
    const used = await t.app.inject({
      method: "POST",
      url: "/api/auth/two-factor/verify-backup-code",
      headers: { cookie: cookieOf(again) },
      payload: { code },
    });
    expect(used.statusCode).toBe(200);
    const third = await t.app.inject({
      method: "POST",
      url: "/api/auth/sign-in/email",
      payload: { email: ADMIN.email, password: ADMIN.password },
    });
    const reused = await t.app.inject({
      method: "POST",
      url: "/api/auth/two-factor/verify-backup-code",
      headers: { cookie: cookieOf(third) },
      payload: { code },
    });
    expect(reused.statusCode).toBeGreaterThanOrEqual(400);
  });

  it("does not require two-factor for ordinary members", async () => {
    t = await makeTestApp({ REQUIRE_ADMIN_MFA: "true" });
    const c = await bootstrapCompany(t);
    const { session: owner } = await enableTwoFactor(t.app, c.session, ADMIN.password);
    const inv = await inviteReq(t.app, owner.headers, c.roleId.Staff as string);
    expect(inv.statusCode).toBe(201);
    const { token } = inv.json();
    const acc = await t.app.inject({
      method: "POST",
      url: "/v1/invitations/accept",
      payload: { token, name: "Staff", password: "a long enough passphrase 1" },
    });
    expect(acc.statusCode).toBe(201);
    const s = await signIn(t.app, "new@company.example", "a long enough passphrase 1");
    expect((await t.app.inject({ url: "/v1/me", headers: s.headers })).statusCode).toBe(200);
  });
});
