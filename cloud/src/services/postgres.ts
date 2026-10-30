import pg from "pg";
import { SYNC_SCHEMA_SQL } from "../schema.js";

export async function verifyPostgres(databaseUrl: string): Promise<void> {
  const client = new pg.Client({ connectionString: databaseUrl, ssl: { rejectUnauthorized: true } });
  await client.connect();
  try {
    await client.query(SYNC_SCHEMA_SQL);
    const ping = await client.query<{ ok: number }>("SELECT 1 AS ok");
    if (ping.rows[0]?.ok !== 1) throw new Error("Postgres ping failed");
    const tables = await client.query<{ n: string }>(
      `SELECT COUNT(*)::text AS n FROM information_schema.tables
       WHERE table_schema = 'public' AND table_name LIKE 'osp_%'`,
    );
    const count = Number(tables.rows[0]?.n ?? 0);
    if (count < 3) throw new Error(`Expected osp_* tables, found ${count}`);
    console.log(`[postgres] ok — ${count} osp_* tables ready`);
  } finally {
    await client.end();
  }
}
