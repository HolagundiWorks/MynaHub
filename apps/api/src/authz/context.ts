import { sql } from "drizzle-orm";
import type { Db } from "../db/types.js";
import type { Principal } from "../principal.js";

/** Transaction-local settings are cleared by commit/rollback, including behind the Neon transaction pooler. */
export function withProjectContext<T>(db: Db, principal: Principal, run: (tx: Db) => Promise<T>): Promise<T> {
  return db.transaction(async (tx) => {
    await tx.execute(sql`select set_config('app.organization_id', ${principal.organizationId}, true),
      set_config('app.membership_id', ${principal.membershipId}, true), set_config('app.user_id', ${principal.userId}, true)`);
    return run(tx);
  });
}

/** Serializes ownership and role changes with membership suspension/reactivation. */
export async function lockOrganization(db: Db, organizationId: string): Promise<void> {
  await db.execute(sql`select pg_advisory_xact_lock(hashtext(${organizationId}))`);
}
