import { drizzleAdapter } from "better-auth/adapters/drizzle";
import { bearer, twoFactor } from "better-auth/plugins";
import type { BetterAuthOptions } from "better-auth";
import type { Config } from "./config.js";

type DrizzleDb = Parameters<typeof drizzleAdapter>[0];

const DAY = 60 * 60 * 24;

/**
 * The Better Auth configuration (guidelines section 6), kept separate from the instance so the schema generator and the tests can reuse it.
 * Better Auth 1.7.25, pinned exactly; read that version's documentation before changing anything here (section 19.4).
 */
export function buildAuthOptions(db: DrizzleDb, config: Config): BetterAuthOptions {
  return {
    appName: "Myna",
    baseURL: config.PUBLIC_API_URL,
    secret: config.BETTER_AUTH_SECRET,
    trustedOrigins: [config.PUBLIC_API_URL, ...config.TRUSTED_ORIGINS],
    database: drizzleAdapter(db, { provider: "pg", schemaName: "auth" }),
    emailAndPassword: {
      enabled: true,
      // Public registration is off from the start (section 6.2). Accounts are created only by the one-time bootstrap and by accepted invitations,
      // both server-side in src/identity.ts.
      disableSignUp: true,
      minPasswordLength: 12,
      maxPasswordLength: 128,
      revokeSessionsOnPasswordReset: true,
    },
    session: {
      expiresIn: 7 * DAY,
      updateAge: DAY,
      // A recently signed-in session is "fresh"; sensitive operations ask for one (section 6.4).
      freshAge: 5 * 60,
      // No cookie cache: a revoked or suspended session must stop working on the very next request, not minutes later.
      cookieCache: { enabled: false },
    },
    advanced: {
      database: { generateId: "uuid" },
      useSecureCookies: config.NODE_ENV === "production",
    },
    rateLimit: {
      enabled: config.NODE_ENV !== "test",
      window: 60,
      max: 100,
      storage: "memory",
      customRules: {
        "/sign-in/email": { window: 60, max: 5 },
        "/two-factor/verify-totp": { window: 60, max: 5 },
        "/two-factor/verify-backup-code": { window: 60, max: 5 },
      },
    },
    plugins: [
      // Mobile and other non-cookie clients send the session as Authorization: Bearer (the token comes back in the set-auth-token header).
      bearer(),
      twoFactor({ issuer: "Myna" }),
    ],
  };
}
