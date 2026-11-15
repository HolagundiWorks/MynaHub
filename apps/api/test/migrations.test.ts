import { fileURLToPath } from "node:url";
import { PGlite } from "@electric-sql/pglite";
import { drizzle } from "drizzle-orm/pglite";
import { migrate } from "drizzle-orm/pglite/migrator";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { shippedMigrations } from "../src/health.js";

// The shipped SQL migrations applied to a real PostgreSQL engine (PGlite, PostgreSQL compiled to WebAssembly). CI also runs the
// node-postgres path against a Postgres service container (see .github/workflows/api.yml); this file checks the SQL itself.
const folder = fileURLToPath(new URL("../drizzle", import.meta.url));
const journal = fileURLToPath(new URL("../drizzle/meta/_journal.json", import.meta.url));

let pg: PGlite;
const q = async (sql: string, params: unknown[] = []) =>
  (await pg.query(sql, params)).rows as Record<string, unknown>[];
const refused = async (sql: string, params: unknown[] = []) => {
  try {
    await pg.query(sql, params);
  } catch (e) {
    return String((e as Error).message);
  }
  return null;
};

beforeAll(async () => {
  pg = new PGlite();
  await migrate(drizzle(pg), { migrationsFolder: folder });
});
afterAll(async () => {
  await pg.close();
});

describe("migrations", () => {
  it("create every table of phases 1 and 2 in its own schema", async () => {
    const rows = await q(
      `select table_schema || '.' || table_name as t from information_schema.tables
       where table_schema in ('core','access','studio','audit','auth') order by 1`,
    );
    expect(rows.map((r) => r.t)).toEqual([
      "access.membership_roles",
      "access.permissions",
      "access.role_permissions",
      "access.roles",
      "audit.events",
      "auth.account",
      "auth.session",
      "auth.two_factor",
      "auth.user",
      "auth.verification",
      "core.bootstrap_tokens",
      "core.invitations",
      "core.memberships",
      "core.organizations",
      "studio.projects",
    ]);
  });

  it("record one applied migration per shipped migration", async () => {
    const [row] = await q('select count(*)::int as n from "drizzle"."__drizzle_migrations"');
    expect(row?.n).toBe(shippedMigrations(journal));
  });

  it("can be applied again without changing or losing anything", async () => {
    const [org] = await q(
      "insert into core.organizations (name, slug) values ('Keep Me', 'keep-me') returning id",
    );
    await migrate(drizzle(pg), { migrationsFolder: folder });
    const rows = await q("select name from core.organizations where id = $1", [org?.id]);
    expect(rows).toEqual([{ name: "Keep Me" }]);
    const [row] = await q('select count(*)::int as n from "drizzle"."__drizzle_migrations"');
    expect(row?.n).toBe(shippedMigrations(journal));
  });
});

describe("integrity constraints", () => {
  const org = async (slug: string) =>
    (
      (await q("insert into core.organizations (name, slug) values ($1, $1) returning id", [slug]))[0] as {
        id: string;
      }
    ).id;

  it("keep organisation slugs unique", async () => {
    await org("acme");
    expect(await refused("insert into core.organizations (name, slug) values ('x', 'acme')")).toMatch(
      /unique|duplicate/i,
    );
  });

  it("allow only the known membership statuses and one membership per person per organisation", async () => {
    const id = await org("members-co");
    expect(
      await refused(
        "insert into core.memberships (organization_id, auth_user_id, status) values ($1, 'u1', 'root')",
        [id],
      ),
    ).toMatch(/check/i);
    await pg.query(
      "insert into core.memberships (organization_id, auth_user_id, status) values ($1, 'u1', 'active')",
      [id],
    );
    expect(
      await refused("insert into core.memberships (organization_id, auth_user_id) values ($1, 'u1')", [id]),
    ).toMatch(/unique|duplicate/i);
  });

  it("refuse a membership for an organisation that does not exist", async () => {
    expect(
      await refused(
        "insert into core.memberships (organization_id, auth_user_id) values (gen_random_uuid(), 'u1')",
      ),
    ).toMatch(/foreign key/i);
  });

  it("let several projects have no code yet but keep a code unique within one organisation only", async () => {
    const a = await org("proj-a");
    const b = await org("proj-b");
    const ins = (o: string, code: string) =>
      pg.query(
        "insert into studio.projects (organization_id, name, code, created_by) values ($1, 'p', $2, 'u')",
        [o, code],
      );
    await ins(a, "");
    await ins(a, "");
    await ins(a, "P-1");
    await ins(b, "P-1");
    expect(
      await refused(
        "insert into studio.projects (organization_id, name, code, created_by) values ($1, 'p', 'P-1', 'u')",
        [a],
      ),
    ).toMatch(/unique|duplicate/i);
  });

  it("start a project at version 1 and reject an invalid status or version", async () => {
    const o = await org("proj-c");
    const [p] = await q(
      "insert into studio.projects (organization_id, name, created_by) values ($1, 'p', 'u') returning version, status, deleted_at",
      [o],
    );
    expect(p).toEqual({ version: 1, status: "active", deleted_at: null });
    expect(
      await refused(
        "insert into studio.projects (organization_id, name, created_by, status) values ($1, 'p', 'u', 'weird')",
        [o],
      ),
    ).toMatch(/check/i);
    expect(
      await refused(
        "insert into studio.projects (organization_id, name, created_by, version) values ($1, 'p', 'u', 0)",
        [o],
      ),
    ).toMatch(/check/i);
  });

  it("remove a role's grants and memberships when the role goes, but not the permission catalogue", async () => {
    const o = await org("rbac-co");
    await pg.query("insert into access.permissions (key, resource_type) values ('tasks.update', 'task')");
    const [r] = await q(
      "insert into access.roles (organization_id, name) values ($1, 'Staff') returning id",
      [o],
    );
    await pg.query(
      "insert into access.role_permissions (role_id, permission_key) values ($1, 'tasks.update')",
      [r?.id],
    );
    await pg.query("delete from access.roles where id = $1", [r?.id]);
    expect(await q("select 1 from access.role_permissions where role_id = $1", [r?.id])).toEqual([]);
    expect((await q("select 1 from access.permissions where key = 'tasks.update'")).length).toBe(1);
  });
});

describe("audit log", () => {
  it("accepts new events", async () => {
    await pg.query(
      "insert into audit.events (action, resource_type) values ('organization.created', 'organization')",
    );
    expect((await q("select 1 from audit.events where action = 'organization.created'")).length).toBe(1);
  });

  it("refuses to change or delete an event", async () => {
    expect(await refused("update audit.events set action = 'tampered'")).toMatch(/append-only/);
    expect(await refused("delete from audit.events")).toMatch(/append-only/);
  });

  it("refuses to be truncated", async () => {
    expect(await refused("truncate audit.events")).toMatch(/append-only/);
  });
});
