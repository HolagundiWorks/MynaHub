import { betterAuth } from "better-auth";
import type { Config } from "./config.js";
import { buildAuthOptions } from "./auth-options.js";

type DrizzleDb = Parameters<typeof buildAuthOptions>[0];

/** The company's own Better Auth instance, stored in the company's own database (the `auth` schema). */
export function createAuth(db: DrizzleDb, config: Config) {
  return betterAuth(buildAuthOptions(db, config));
}

export type Auth = ReturnType<typeof createAuth>;
