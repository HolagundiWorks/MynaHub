import { randomBytes } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import pg from "pg";
import type { Config } from "./config.js";
import { runMigrations } from "./db/migrate.js";

export class SetupError extends Error {}

/** Exclusive creation: never rotate a deployed secret or replace company settings. */
export async function initializeEnvironment(template: string, destination: string): Promise<void> {
  const content = (await readFile(template, "utf8")).replace(
    "BETTER_AUTH_SECRET=CHANGE_ME_TO_A_LONG_RANDOM_VALUE",
    `BETTER_AUTH_SECRET=${randomBytes(48).toString("base64url")}`,
  );
  try {
    await writeFile(destination, content, { flag: "wx", mode: 0o600 });
  } catch {
    throw new SetupError(
      "Could not create .env. An existing file is never overwritten; check file permissions.",
    );
  }
}

/** Validate before opening either connection; diagnostics contain no input values. */
export function validateSetup(config: Config): { runtime: URL; owner: URL } {
  const parse = (value: string | undefined, name: string) => {
    try {
      if (!value || /change_?me/i.test(value)) throw new Error();
      const url = new URL(value);
      if (
        !["postgres:", "postgresql:"].includes(url.protocol) ||
        !url.hostname ||
        !url.username ||
        !url.password ||
        url.pathname.length < 2
      )
        throw new Error();
      const local = ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
      if (
        !(local && config.NODE_ENV !== "production") &&
        !["require", "verify-full"].includes(url.searchParams.get("sslmode") ?? "")
      )
        throw new Error();
      // No TLS override parameters: node-postgres must retain certificate validation.
      if (["ssl", "sslcert", "sslkey", "sslrootcert", "uselibpqcompat"].some((k) => url.searchParams.has(k)))
        throw new Error();
      return url;
    } catch {
      throw new SetupError(
        `${name}: provide a complete PostgreSQL URL with credentials and sslmode=require or verify-full.`,
      );
    }
  };
  const runtime = parse(config.DATABASE_URL, "DATABASE_URL");
  const owner = parse(config.DIRECT_DATABASE_URL, "DIRECT_DATABASE_URL");
  if (decodeURIComponent(runtime.username) !== "myna_runtime" || runtime.username === owner.username) {
    throw new SetupError(
      "DATABASE_URL must use myna_runtime; DIRECT_DATABASE_URL must use a separate schema owner.",
    );
  }
  if (owner.hostname.includes("-pooler"))
    throw new SetupError("DIRECT_DATABASE_URL must use the direct endpoint, without -pooler.");
  if (
    runtime.hostname.replace("-pooler.", ".") !== owner.hostname ||
    runtime.port !== owner.port ||
    runtime.pathname !== owner.pathname
  ) {
    throw new SetupError("The runtime and owner URLs must point at the same endpoint and database.");
  }
  if (!config.REQUIRE_ADMIN_MFA)
    throw new SetupError("REQUIRE_ADMIN_MFA must remain true for company setup.");
  if (
    config.TRUSTED_ORIGINS.some((origin) => {
      try {
        const url = new URL(origin);
        return url.origin !== origin || (config.NODE_ENV === "production" && url.protocol !== "https:");
      } catch {
        return true;
      }
    })
  )
    throw new SetupError("TRUSTED_ORIGINS must contain explicit origins, with HTTPS in production.");
  return { runtime, owner };
}

export interface SetupQuery {
  query(text: string, values?: unknown[]): Promise<{ rows: Record<string, unknown>[] }>;
}

/** Catalog checks also catch roles created via the Neon console with neon_superuser membership. */
export async function checkRuntimeRole(db: SetupQuery): Promise<void> {
  const { rows } = await db.query(`select current_user as name,
    r.rolsuper or r.rolcreatedb or r.rolcreaterole or r.rolreplication or r.rolbypassrls as privileged,
    exists(select 1 from pg_auth_members where member = r.oid) as member,
    exists(select 1 from pg_database where datdba = r.oid) as owns_database,
    exists(select 1 from pg_namespace where nspowner = r.oid) as owns_schema,
    exists(select 1 from pg_class where relowner = r.oid) as owns_object,
    has_database_privilege(current_database(), 'CREATE') as creates_schema,
    exists(select 1 from pg_namespace where nspname not like 'pg_%' and nspname <> 'information_schema'
      and has_schema_privilege(oid, 'CREATE')) as creates_object
    from pg_roles r where r.rolname = current_user`);
  const row = rows[0];
  if (
    !row ||
    row.name !== "myna_runtime" ||
    [
      "privileged",
      "member",
      "owns_database",
      "owns_schema",
      "owns_object",
      "creates_schema",
      "creates_object",
    ].some((key) => row[key] !== false)
  ) {
    throw new SetupError(
      "Runtime role is not isolated: use SQL-created myna_runtime with no memberships, ownership, administrative or CREATE privileges.",
    );
  }
}

export async function checkRuntimeAccess(db: SetupQuery): Promise<void> {
  const { rows } = await db.query(`select n.nspname as schema, c.relname as name,
    has_schema_privilege(n.oid, 'USAGE') as usage,
    has_table_privilege(c.oid, 'SELECT') as read,
    has_table_privilege(c.oid, 'INSERT') as insert,
    has_table_privilege(c.oid, 'UPDATE') as update,
    has_table_privilege(c.oid, 'DELETE') as delete,
    has_table_privilege(c.oid, 'TRUNCATE') as truncate
    from pg_class c join pg_namespace n on n.oid = c.relnamespace
    where c.relkind in ('r','p') and n.nspname in ('core','access','studio','audit','auth')`);
  const expected = [
    "core.organizations",
    "core.memberships",
    "core.bootstrap_tokens",
    "core.invitations",
    "access.roles",
    "access.permissions",
    "access.role_permissions",
    "access.membership_roles",
    "studio.projects",
    "studio.project_assignments",
    "studio.project_edit_receipts",
    "studio.project_tasks",
    "audit.events",
    "auth.user",
    "auth.account",
    "auth.session",
    "auth.two_factor",
    "auth.verification",
  ];
  if (
    expected.some((name) => !rows.some((row) => `${row.schema}.${row.name}` === name)) ||
    rows.some(
      (row) =>
        row.usage !== true ||
        row.read !== true ||
        row.insert !== (row.name !== "project_tasks") ||
        row.truncate !== false ||
        row.update !==
          (row.schema !== "audit" &&
            row.name !== "project_edit_receipts" &&
            row.name !== "projects" &&
            row.name !== "project_tasks") ||
        row.delete !==
          (row.schema !== "audit" && row.name !== "project_edit_receipts" && row.name !== "project_tasks"),
    )
  ) {
    throw new SetupError(
      "Runtime table permissions or schema are incomplete. Create the runtime role before migrations; review the shipped grants.",
    );
  }
  const columns = await db.query(`select attname as name,
    has_column_privilege('studio.projects', attname, 'UPDATE') as writable
    from pg_attribute where attrelid='studio.projects'::regclass and attnum > 0 and not attisdropped`);
  if (
    columns.rows.some(
      (row) =>
        row.writable !== ["name", "code", "status", "version", "updated_at"].includes(String(row.name)),
    )
  ) {
    throw new SetupError("Runtime project column permissions do not match the shipped grants.");
  }
  // This is the same read used by /health/ready; USAGE alone is insufficient.
  await db.query("select count(*) from drizzle.__drizzle_migrations");
  const journal = await db.query(`select
    has_table_privilege('drizzle.__drizzle_migrations', 'INSERT') or
    has_table_privilege('drizzle.__drizzle_migrations', 'UPDATE') or
    has_table_privilege('drizzle.__drizzle_migrations', 'DELETE') or
    has_table_privilege('drizzle.__drizzle_migrations', 'TRUNCATE') as writable`);
  if (journal.rows[0]?.writable !== false) {
    throw new SetupError("Runtime access to the migration journal must be read-only.");
  }
}

/** Compare the actual migration hashes, not just a count that could hide another build. */
export async function checkMigrations(db: SetupQuery, folder: string, allowPending = false): Promise<void> {
  const { readMigrationFiles } = await import("drizzle-orm/migrator");
  const expected = readMigrationFiles({ migrationsFolder: folder });
  const { rows } = await db.query("select hash from drizzle.__drizzle_migrations order by created_at, id");
  if (
    (allowPending ? rows.length > expected.length : rows.length !== expected.length) ||
    rows.some((row, i) => row.hash !== expected[i]?.hash)
  ) {
    throw new SetupError(
      "Database migrations do not match this build. Back up and review the migration history before continuing.",
    );
  }
}

export async function setupDatabase(
  config: Config,
  folder: string,
  apply: boolean,
): Promise<"bootstrap" | "configured"> {
  validateSetup(config);
  const options = { max: 1, connectionTimeoutMillis: 15_000, statement_timeout: 30_000 };
  const runtime = new pg.Pool({ ...options, connectionString: config.DATABASE_URL });
  const owner = new pg.Pool({ ...options, connectionString: config.DIRECT_DATABASE_URL });
  // Never emit errors from pg (a server error may include connection details).
  runtime.on("error", () => {});
  owner.on("error", () => {});
  try {
    // Both identities must connect successfully before any schema change.
    await checkRuntimeRole(runtime);
    const identity = await owner.query("select current_user as name");
    if (identity.rows[0]?.name === "myna_runtime")
      throw new SetupError("Migration connection must use the schema owner.");
    if (apply) {
      const history = await owner.query("select to_regclass('drizzle.__drizzle_migrations') as journal");
      if (history.rows[0]?.journal) await checkMigrations(owner, folder, true);
      await runMigrations(config.DIRECT_DATABASE_URL!, folder);
      // Readiness requires metadata access; do not grant any writes to migration history.
      await owner.query("GRANT USAGE ON SCHEMA drizzle TO myna_runtime");
      await owner.query("GRANT SELECT ON drizzle.__drizzle_migrations TO myna_runtime");
    }
    await checkMigrations(owner, folder);
    await checkRuntimeRole(runtime);
    await checkRuntimeAccess(runtime);
    const state = await runtime.query("select exists(select 1 from core.organizations) as configured");
    return state.rows[0]?.configured ? "configured" : "bootstrap";
  } catch (err) {
    if (err instanceof SetupError) throw err;
    throw new SetupError(
      "Database setup check failed. Check connectivity, credentials, migrations and grants privately; database error details are not logged.",
    );
  } finally {
    await Promise.allSettled([runtime.end(), owner.end()]);
  }
}
