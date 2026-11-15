import { readFileSync } from "node:fs";

/** What the readiness check needs from the database. The real one talks to Postgres; tests use their own. */
export interface DbHealth {
  /** Throws when the database cannot be reached. */
  ping(): Promise<void>;
  /** How many migrations the database has applied. */
  appliedMigrations(): Promise<number>;
}

/** How many migrations this build ships (the entries in drizzle/meta/_journal.json). */
export function shippedMigrations(journalPath: string): number {
  const journal = JSON.parse(readFileSync(journalPath, "utf8")) as { entries: unknown[] };
  return journal.entries.length;
}
