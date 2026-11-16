import type { AttachmentStore } from "../src/attachment-store.js";
import { createHmac } from "node:crypto";
import { fileURLToPath } from "node:url";
import { PGlite } from "@electric-sql/pglite";
import { drizzle } from "drizzle-orm/pglite";
import { migrate } from "drizzle-orm/pglite/migrator";
import type { FastifyInstance } from "fastify";
import { buildApp } from "../src/app.js";
import { createAuth } from "../src/auth.js";
import { createBootstrapToken } from "../src/bootstrap.js";
import { loadConfig } from "../src/config.js";
import type { Db } from "../src/db/types.js";
import * as schema from "../src/db/schema/index.js";
import { roles } from "../src/db/schema/index.js";
import { shippedMigrations } from "../src/health.js";

const folder = fileURLToPath(new URL("../drizzle", import.meta.url));
const journal = fileURLToPath(new URL("../drizzle/meta/_journal.json", import.meta.url));

export const ADMIN = {
  email: "owner@company.example",
  password: "correct horse battery staple",
  name: "Asha Owner",
};
export const SECRET = "test-secret-test-secret-test-secret-0000";

export interface TestApp {
  app: FastifyInstance;
  db: Db;
  pg: PGlite;
  auth: ReturnType<typeof createAuth>;
  close(): Promise<void>;
}

/** The real API, real migrations and real Better Auth on an in-memory PostgreSQL (PGlite). Each call is a fresh, empty company. */
export async function makeTestApp(
  env: Record<string, string> = {},
  storage?: AttachmentStore,
): Promise<TestApp> {
  const pg = new PGlite();
  const db = drizzle(pg, { schema });
  await migrate(db, { migrationsFolder: folder });
  const config = loadConfig({
    NODE_ENV: "test",
    PUBLIC_API_URL: "https://api.company.example",
    DATABASE_URL: "postgresql://unused/unused",
    BETTER_AUTH_SECRET: SECRET,
    REQUIRE_ADMIN_MFA: "false",
    LOG_LEVEL: "silent",
    ...env,
  });
  const auth = createAuth(db as never, config);
  const app = await buildApp({
    config,
    storage,
    db: db as unknown as Db,
    auth,
    version: "test",
    expectedMigrations: shippedMigrations(journal),
    liveTiming: { waitMs: 250, tickMs: 20 },
    health: {
      ping: async () => void (await pg.query("select 1")),
      appliedMigrations: async () =>
        Number(
          (
            (await pg.query('select count(*)::int as n from "drizzle"."__drizzle_migrations"')).rows[0] as {
              n: number;
            }
          ).n,
        ),
    },
  });
  return {
    app,
    db: db as unknown as Db,
    pg,
    auth,
    close: async () => {
      await app.close();
      await pg.close();
    },
  };
}

export interface Session {
  token: string;
  cookie: string;
  headers: { authorization: string };
}

/** Signs in with email and password through Better Auth; returns the bearer token (and the cookie) the response set. */
export async function signIn(app: FastifyInstance, email: string, password: string): Promise<Session> {
  const r = await app.inject({
    method: "POST",
    url: "/api/auth/sign-in/email",
    payload: { email, password },
  });
  if (r.statusCode !== 200) throw new Error(`sign-in failed: ${r.statusCode} ${r.body}`);
  const token = r.headers["set-auth-token"] as string;
  const cookies = ([] as string[]).concat((r.headers["set-cookie"] as string | string[] | undefined) ?? []);
  return {
    token,
    cookie: cookies.map((c) => c.split(";")[0]).join("; "),
    headers: { authorization: `Bearer ${token}` },
  };
}

/** Sets the company up the supported way: a setup token, then POST /v1/bootstrap. Returns the Owner's session and the system role ids. */
export async function bootstrapCompany(t: TestApp, env: { admin?: typeof ADMIN } = {}) {
  const admin = env.admin ?? ADMIN;
  const { token } = await createBootstrapToken(t.db);
  const r = await t.app.inject({
    method: "POST",
    url: "/v1/bootstrap",
    payload: {
      setupToken: token,
      organizationName: "Sample Architects",
      slug: "sample-architects",
      adminName: admin.name,
      adminEmail: admin.email,
      adminPassword: admin.password,
    },
  });
  if (r.statusCode !== 201) throw new Error(`bootstrap failed: ${r.statusCode} ${r.body}`);
  const session = await signIn(t.app, admin.email, admin.password);
  const roleRows = await t.db.select({ id: roles.id, name: roles.name }).from(roles);
  const roleId = Object.fromEntries(roleRows.map((x) => [x.name, x.id])) as Record<string, string>;
  return { ...(r.json() as { organizationId: string; userId: string }), session, roleId, setupToken: token };
}

/** Invites and accepts a person with the given role, as an administrator would. Returns their session. */
export async function addMember(
  t: TestApp,
  owner: Session,
  email: string,
  roleId: string,
  password = "another long passphrase 1",
) {
  const inv = await t.app.inject({
    method: "POST",
    url: "/v1/invitations",
    headers: owner.headers,
    payload: { email, roleId },
  });
  if (inv.statusCode !== 201) throw new Error(`invite failed: ${inv.statusCode} ${inv.body}`);
  const { token } = inv.json() as { token: string };
  const acc = await t.app.inject({
    method: "POST",
    url: "/v1/invitations/accept",
    payload: { token, name: email.split("@")[0], password },
  });
  if (acc.statusCode !== 201) throw new Error(`accept failed: ${acc.statusCode} ${acc.body}`);
  const session = await signIn(t.app, email, password);
  return { session, userId: (acc.json() as { userId: string }).userId, password, inviteToken: token };
}

// ---- TOTP (RFC 6238) so a test can act as an authenticator app ------------------------------------------------
function base32Decode(input: string): Buffer {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const ch of input.replace(/=+$/, "").toUpperCase())
    bits += alphabet.indexOf(ch).toString(2).padStart(5, "0");
  const bytes: number[] = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}

export function totp(secretBase32: string, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 1000 / 30)));
  const h = createHmac("sha1", base32Decode(secretBase32)).update(counter).digest();
  const o = (h[h.length - 1] as number) & 0xf;
  const code =
    (((h[o] as number) & 0x7f) << 24) |
    (((h[o + 1] as number) & 0xff) << 16) |
    (((h[o + 2] as number) & 0xff) << 8) |
    ((h[o + 3] as number) & 0xff);
  return String(code % 1_000_000).padStart(6, "0");
}

export function secretFromUri(totpURI: string): string {
  return new URL(totpURI).searchParams.get("secret") as string;
}

/**
 * Turns two-factor on for the signed-in person and verifies a code. Verifying rotates the session: the old token stops working and the new one
 * comes back in the set-auth-token header, so clients must replace theirs. Returns the new session, the secret and the backup codes.
 */
export async function enableTwoFactor(app: FastifyInstance, session: Session, password: string) {
  const enable = await app.inject({
    method: "POST",
    url: "/api/auth/two-factor/enable",
    headers: session.headers,
    payload: { password },
  });
  if (enable.statusCode !== 200) throw new Error(`enable failed: ${enable.statusCode} ${enable.body}`);
  const { totpURI, backupCodes } = enable.json() as { totpURI: string; backupCodes: string[] };
  const secret = secretFromUri(totpURI);
  const verify = await app.inject({
    method: "POST",
    url: "/api/auth/two-factor/verify-totp",
    headers: session.headers,
    payload: { code: totp(secret) },
  });
  if (verify.statusCode !== 200) throw new Error(`verify failed: ${verify.statusCode} ${verify.body}`);
  const token = verify.headers["set-auth-token"] as string;
  const rotated: Session = { token, cookie: "", headers: { authorization: `Bearer ${token}` } };
  return { session: rotated, secret, backupCodes };
}
