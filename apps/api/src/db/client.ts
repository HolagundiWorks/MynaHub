import { drizzle } from "drizzle-orm/node-postgres";
import pg from "pg";
import type { DbHealth } from "../health.js";
import * as schema from "./schema/index.js";

export interface Database {
  db: ReturnType<typeof drizzle<typeof schema>>;
  pool: pg.Pool;
  health: DbHealth;
  close(): Promise<void>;
}

/** A pooled connection with the runtime credentials. No schema changes are made through it. */
export function connect(url: string, max = 10): Database {
  const pool = new pg.Pool({
    connectionString: url,
    max,
    idleTimeoutMillis: 30_000,
    connectionTimeoutMillis: 15000,
  });
  const db = drizzle(pool, { schema });
  return {
    db,
    pool,
    health: {
      async ping() {
        await pool.query("select 1");
      },
      async appliedMigrations() {
        const r = await pool.query<{ n: string }>(
          'select count(*)::text as n from "drizzle"."__drizzle_migrations"',
        );
        return Number(r.rows[0]?.n ?? 0);
      },
    },
    close: () => pool.end(),
  };
}
