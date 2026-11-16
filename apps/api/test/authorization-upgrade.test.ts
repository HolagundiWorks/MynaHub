import { mkdtemp, mkdir, readFile, copyFile, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { PGlite } from "@electric-sql/pglite";
import { drizzle } from "drizzle-orm/pglite";
import { migrate } from "drizzle-orm/pglite/migrator";
import { expect, it } from "vitest";

it("upgrades a populated phase-2 company without replacing users, roles or projects and can be repeated", async () => {
  const folder = fileURLToPath(new URL("../drizzle", import.meta.url));
  const temporary = await mkdtemp(join(tmpdir(), "myna-phase2-"));
  const pg = new PGlite();
  try {
    const journal = JSON.parse(await readFile(join(folder, "meta/_journal.json"), "utf8"));
    journal.entries = journal.entries.slice(0, 5);
    await mkdir(join(temporary, "meta"));
    await writeFile(join(temporary, "meta/_journal.json"), JSON.stringify(journal));
    for (const entry of journal.entries)
      await copyFile(join(folder, `${entry.tag}.sql`), join(temporary, `${entry.tag}.sql`));
    await migrate(drizzle(pg), { migrationsFolder: temporary });
    const org = (
      await pg.query("insert into core.organizations(name,slug) values('Existing','existing') returning id")
    ).rows[0] as { id: string };
    const role = (
      await pg.query(
        "insert into access.roles(organization_id,name,is_system_role) values($1,'Owner',true) returning id",
        [org.id],
      )
    ).rows[0] as { id: string };
    await pg.query("insert into access.role_permissions values($1,'organization.read')", [role.id]);
    const member = (
      await pg.query(
        "insert into core.memberships(organization_id,auth_user_id,status) values($1,'existing-user','active') returning id",
        [org.id],
      )
    ).rows[0] as { id: string };
    await pg.query("insert into access.membership_roles values($1,$2)", [member.id, role.id]);
    const project = (
      await pg.query(
        "insert into studio.projects(organization_id,name,code,created_by) values($1,'Keep Me','KEEP','existing-user') returning id",
        [org.id],
      )
    ).rows[0] as { id: string };
    await migrate(drizzle(pg), { migrationsFolder: folder });
    await migrate(drizzle(pg), { migrationsFolder: folder });
    expect((await pg.query("select id,name,scope from access.roles where id=$1", [role.id])).rows).toEqual([
      { id: role.id, name: "Owner", scope: "organization" },
    ]);
    expect(
      (await pg.query("select count(*)::int as n from access.roles where organization_id=$1", [org.id])).rows,
    ).toEqual([{ n: 11 }]);
    expect((await pg.query("select id,name from studio.projects")).rows).toEqual([
      { id: project.id, name: "Keep Me" },
    ]);
    expect((await pg.query("select auth_user_id from core.memberships")).rows).toEqual([
      { auth_user_id: "existing-user" },
    ]);
    expect(
      (
        await pg.query(
          "select permission_key from access.role_permissions where role_id=$1 and permission_key='roles.manage'",
          [role.id],
        )
      ).rows,
    ).toHaveLength(1);
    expect((await pg.query("select count(*)::int as n from drizzle.__drizzle_migrations")).rows).toEqual([
      { n: 9 },
    ]);
  } finally {
    await pg.close();
    await rm(temporary, { recursive: true, force: true });
  }
});
