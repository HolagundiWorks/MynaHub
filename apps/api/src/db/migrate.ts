import { drizzle } from "drizzle-orm/node-postgres";
import { migrate } from "drizzle-orm/node-postgres/migrator";
import pg from "pg";

/**
 * Applies the shipped migrations with the schema owner's credentials. Additive and repeatable: already-applied migrations are skipped, nothing is
 * ever reset or dropped (guidelines section 7.5).
 */
export async function runMigrations(directUrl: string, migrationsFolder: string): Promise<void> {
  const pool = new pg.Pool({ connectionString: directUrl, max: 1 });
  try {
    await migrate(drizzle(pool), { migrationsFolder });
  } finally {
    await pool.end();
  }
}
