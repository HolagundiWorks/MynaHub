import { fileURLToPath } from "node:url";
import { PGlite } from "@electric-sql/pglite";
import { drizzle } from "drizzle-orm/pglite";
import { migrate } from "drizzle-orm/pglite/migrator";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

let pg: PGlite;
const orgA = "11111111-1111-4111-8111-111111111111",
  orgB = "22222222-2222-4222-8222-222222222222";
const member = "33333333-3333-4333-8333-333333333333",
  manager = "44444444-4444-4444-8444-444444444444";
const role = "55555555-5555-4555-8555-555555555555",
  admin = "66666666-6666-4666-8666-666666666666";
const projectA = "77777777-7777-4777-8777-777777777777",
  projectB = "88888888-8888-4888-8888-888888888888",
  foreign = "99999999-9999-4999-8999-999999999999";
beforeAll(async () => {
  pg = new PGlite();
  await pg.exec("CREATE ROLE myna_runtime LOGIN");
  await migrate(drizzle(pg), { migrationsFolder: fileURLToPath(new URL("../drizzle", import.meta.url)) });
  await pg.query("insert into core.organizations(id,name,slug) values ($1,'A','a'),($2,'B','b')", [
    orgA,
    orgB,
  ]);
  await pg.query(
    "insert into core.memberships(id,organization_id,auth_user_id,status) values ($1,$3,'staff','active'),($2,$3,'manager','active')",
    [member, manager, orgA],
  );
  await pg.query(
    "insert into access.roles(id,organization_id,name,scope) values ($1,$3,'Field','assigned'),($2,$3,'Admin','organization')",
    [role, admin, orgA],
  );
  await pg.query(
    "insert into access.role_permissions(role_id,permission_key) values ($1,'projects.read'),($2,'projects.read'),($2,'projects.assign')",
    [role, admin],
  );
  await pg.query("insert into access.membership_roles(membership_id,role_id) values ($1,$3),($2,$4)", [
    member,
    manager,
    role,
    admin,
  ]);
  await pg.query(
    "insert into studio.projects(id,organization_id,name,created_by) values ($1,$4,'A','manager'),($2,$4,'B','manager'),($3,$5,'Foreign','other')",
    [projectA, projectB, foreign, orgA, orgB],
  );
  await pg.query(
    "insert into studio.project_assignments(organization_id,project_id,membership_id) values($1,$2,$3)",
    [orgA, projectA, member],
  );
});
afterAll(async () => {
  await pg.close();
});
async function runtime(run: () => Promise<void>, who = member, user = "staff", org = orgA) {
  await pg.exec("SET ROLE myna_runtime; BEGIN");
  try {
    if (who)
      await pg.query(
        "select set_config('app.organization_id',$1,true),set_config('app.membership_id',$2,true),set_config('app.user_id',$3,true)",
        [org, who, user],
      );
    await run();
  } finally {
    await pg.exec("ROLLBACK; RESET ROLE");
  }
}
describe("runtime project row security", () => {
  it("scopes client contacts by actual assignments and gives runtime SELECT only", async () => {
    const clientA = "c1000000-0000-4000-8000-000000000001",
      clientB = "c1000000-0000-4000-8000-000000000002",
      clientForeign = "c1000000-0000-4000-8000-000000000003";
    await pg.query(
      "insert into studio.clients(id,organization_id,name) values($1,$4,'Visible client'),($2,$4,'Unassigned client'),($3,$5,'Foreign client')",
      [clientA, clientB, clientForeign, orgA, orgB],
    );
    await pg.query("update studio.projects set client_id=$1 where id=$2", [clientA, projectA]);
    await pg.query("update studio.projects set client_id=$1 where id=$2", [clientB, projectB]);
    await runtime(async () => expect((await pg.query("select id from studio.clients")).rows).toEqual([]));
    await pg.query(
      "insert into access.role_permissions(role_id,permission_key) values($1,'clients.read'),($2,'clients.read')",
      [role, admin],
    );
    try {
      await runtime(async () =>
        expect((await pg.query("select id from studio.clients")).rows).toEqual([{ id: clientA }]),
      );
      await runtime(
        async () =>
          expect((await pg.query("select id from studio.clients order by id")).rows).toEqual([
            { id: clientA },
            { id: clientB },
          ]),
        manager,
        "manager",
      );
      await runtime(
        async () => expect((await pg.query("select id from studio.clients")).rows).toEqual([]),
        "",
      );
      for (const query of [
        "update studio.clients set name='Changed'",
        "delete from studio.clients",
        "truncate studio.clients",
        "insert into studio.clients(organization_id,name) values('" + orgA + "','New')",
        "update studio.projects set client_id=client_id",
      ]) {
        await runtime(async () => {
          await expect(pg.exec(query)).rejects.toThrow(/permission denied/i);
        });
      }
    } finally {
      await pg.query(
        "delete from access.role_permissions where role_id in ($1,$2) and permission_key='clients.read'",
        [role, admin],
      );
    }
  });
  it("uses scoped lifecycle functions without granting raw project insert/delete or tombstone writes", async () => {
    await pg.query(
      "insert into access.role_permissions(role_id,permission_key) values($1,'projects.create'),($1,'projects.delete')",
      [admin],
    );
    try {
      await runtime(async () =>
        expect(
          (
            await pg.query(
              "select * from studio.create_company_project('a1000000-0000-4000-8000-000000000001','Denied','','active')",
            )
          ).rows,
        ).toEqual([]),
      );
      await runtime(
        async () => {
          const id = "a1000000-0000-4000-8000-000000000002";
          expect(
            (
              await pg.query("select * from studio.create_company_project($1,'Created safely','','active')", [
                id,
              ])
            ).rows,
          ).toMatchObject([{ id, version: 1 }]);
          expect(
            (await pg.query("select * from studio.delete_company_project($1,2)", [id])).rows,
          ).toMatchObject([{ outcome: "conflict", version: 1 }]);
          expect(
            (await pg.query("select * from studio.delete_company_project($1,1)", [id])).rows,
          ).toMatchObject([{ outcome: "deleted", version: 2 }]);
          expect((await pg.query("select id from studio.projects where id=$1", [id])).rows).toEqual([]);
          expect((await pg.query("select * from studio.delete_company_project($1,2)", [id])).rows).toEqual(
            [],
          );
        },
        manager,
        "manager",
      );
      await runtime(
        async () => {
          await expect(pg.exec("update studio.projects set deleted_at=now()")).rejects.toThrow(
            /permission denied/i,
          );
        },
        manager,
        "manager",
      );
    } finally {
      await pg.query(
        "delete from access.role_permissions where role_id=$1 and permission_key in ('projects.create','projects.delete')",
        [admin],
      );
    }
  });
  it("enforces task scope and denies task mutations without tasks.write", async () => {
    await pg.query(
      "insert into studio.project_tasks(organization_id,project_id,title) values($1,$2,'Visible'),($1,$3,'Unassigned'),($4,$5,'Foreign')",
      [orgA, projectA, projectB, orgB, foreign],
    );
    await expect(
      pg.query(
        "insert into studio.project_tasks(organization_id,project_id,title) values($1,$2,'Wrong company')",
        [orgB, projectA],
      ),
    ).rejects.toThrow(/foreign key/i);
    await runtime(async () =>
      expect((await pg.query("select title from studio.project_tasks")).rows).toEqual([{ title: "Visible" }]),
    );
    await runtime(
      async () => expect((await pg.query("select title from studio.project_tasks")).rows).toEqual([]),
      "",
    );
    for (const query of [
      "delete from studio.project_tasks",
      "insert into studio.project_tasks(organization_id,project_id,title) values('" +
        orgA +
        "','" +
        projectA +
        "','New')",
      "truncate studio.project_tasks",
    ]) {
      await runtime(async () => {
        await expect(pg.exec(query)).rejects.toThrow(/permission denied|row-level security/i);
      });
    }
    await runtime(async () => {
      expect((await pg.query("update studio.project_tasks set title='Changed' returning id")).rows).toEqual(
        [],
      );
    });
  });
  it("allows task writes only within assigned scope and protects task identity columns", async () => {
    await pg.query("insert into access.role_permissions(role_id,permission_key) values($1,'tasks.write')", [
      role,
    ]);
    try {
      await runtime(async () => {
        const row = await pg.query(
          "insert into studio.project_tasks(organization_id,project_id,title) values($1,$2,'New task') returning id",
          [orgA, projectA],
        );
        const id = (row.rows[0] as { id: string }).id;
        expect(
          (
            await pg.query(
              "update studio.project_tasks set status='completed',version=2 where id=$1 returning status,version",
              [id],
            )
          ).rows,
        ).toEqual([{ status: "completed", version: 2 }]);
      });
      for (const [org, project] of [
        [orgA, projectB],
        [orgB, foreign],
      ]) {
        await runtime(async () => {
          await expect(
            pg.query(
              "insert into studio.project_tasks(organization_id,project_id,title) values($1,$2,'Forbidden')",
              [org, project],
            ),
          ).rejects.toThrow(/row-level security/i);
        });
      }
      for (const column of ["id", "organization_id", "project_id", "deleted_at"]) {
        await runtime(async () => {
          await expect(pg.exec("update studio.project_tasks set " + column + "=" + column)).rejects.toThrow(
            /permission denied/i,
          );
        });
      }
      await runtime(async () => {
        await expect(
          pg.query(
            "insert into studio.project_tasks(organization_id,project_id,title,version) values($1,$2,'Wrong version',2)",
            [orgA, projectA],
          ),
        ).rejects.toThrow(/row-level security/i);
      });
    } finally {
      await pg.query(
        "delete from access.role_permissions where role_id=$1 and permission_key='tasks.write'",
        [role],
      );
    }
  });
  it("defaults to no rows when context is missing", async () => {
    await runtime(
      async () => expect((await pg.query("select id from studio.projects")).rows).toHaveLength(0),
      "",
    );
  });
  it("shows only assigned projects and hides other companies", async () => {
    await runtime(async () =>
      expect((await pg.query("select id from studio.projects")).rows).toEqual([{ id: projectA }]),
    );
    await runtime(
      async () =>
        expect((await pg.query("select id from studio.projects order by id")).rows).toEqual([
          { id: projectA },
          { id: projectB },
        ]),
      manager,
      "manager",
    );
  });
  it("does not accept mismatched actor and membership context", async () => {
    await runtime(
      async () => expect((await pg.query("select id from studio.projects")).rows).toHaveLength(0),
      member,
      "manager",
    );
    await runtime(
      async () => expect((await pg.query("select id from studio.projects")).rows).toHaveLength(0),
      member,
      "staff",
      orgB,
    );
  });
  it("clears transaction-local context instead of leaking it to the next request", async () => {
    await pg.exec("SET ROLE myna_runtime; BEGIN");
    try {
      await pg.query(
        "select set_config('app.organization_id',$1,true),set_config('app.membership_id',$2,true),set_config('app.user_id','staff',true)",
        [orgA, member],
      );
      expect((await pg.query("select id from studio.projects")).rows).toHaveLength(1);
      await pg.exec("COMMIT");
      expect((await pg.query("select id from studio.projects")).rows).toHaveLength(0);
    } finally {
      await pg.exec("RESET ROLE");
    }
  });
  it("does not allow field staff to write assignments", async () => {
    await runtime(async () => {
      await expect(
        pg.query("insert into studio.project_assignments values($1,$2,$3)", [orgA, projectA, manager]),
      ).rejects.toThrow(/row.level security|permission denied/i);
    });
  });
  it("blocks project creation and leaves project mutation unavailable to runtime", async () => {
    await runtime(async () => {
      await expect(
        pg.query("insert into studio.projects(organization_id,name,created_by) values($1,'No','staff')", [
          orgA,
        ]),
      ).rejects.toThrow(/row.level security|permission denied/i);
    });
    await runtime(
      async () =>
        expect((await pg.query("update studio.projects set name='No' returning id")).rows).toHaveLength(0),
      manager,
      "manager",
    );
    await runtime(
      async () => {
        await expect(pg.query("delete from studio.projects returning id")).rejects.toThrow(
          /permission denied/i,
        );
      },
      manager,
      "manager",
    );
  });
  it("refuses cross-organization assignments and role links even for table owners", async () => {
    await expect(
      pg.query("insert into studio.project_assignments values($1,$2,$3)", [orgB, foreign, member]),
    ).rejects.toThrow(/same organization/i);
    const other = (
      await pg.query("insert into access.roles(organization_id,name) values($1,'Foreign') returning id", [
        orgB,
      ])
    ).rows[0] as { id: string };
    await expect(
      pg.query("insert into access.membership_roles values($1,$2)", [member, other.id]),
    ).rejects.toThrow(/same organization/i);
  });
  it("does not widen project scope through an unrelated organization-wide role", async () => {
    const other = (
      await pg.query(
        "insert into access.roles(organization_id,name,scope) values($1,'Identity only','organization') returning id",
        [orgA],
      )
    ).rows[0] as { id: string };
    await pg.query("insert into access.role_permissions values($1,'organization.read')", [other.id]);
    await pg.query("insert into access.membership_roles values($1,$2)", [member, other.id]);
    await runtime(async () =>
      expect((await pg.query("select id from studio.projects")).rows).toEqual([{ id: projectA }]),
    );
  });
  it("allows assigned editors to change only editable project columns", async () => {
    await pg.query("insert into access.role_permissions values($1,'projects.write')", [role]);
    try {
      await runtime(async () => {
        expect(
          (await pg.query("update studio.projects set name='Edited',version=version+1 returning id")).rows,
        ).toEqual([{ id: projectA }]);
        expect(
          (await pg.query("select name,version from studio.projects where id=$1", [projectA])).rows,
        ).toEqual([{ name: "Edited", version: 2 }]);
      });
      await runtime(async () => {
        await expect(
          pg.query("update studio.projects set organization_id=$1 where id=$2", [orgB, projectA]),
        ).rejects.toThrow(/permission denied/i);
      });
      await runtime(async () => {
        await expect(
          pg.query("update studio.projects set created_by='other' where id=$1", [projectA]),
        ).rejects.toThrow(/permission denied/i);
      });
      await runtime(async () => {
        await expect(
          pg.query("update studio.projects set deleted_at=now() where id=$1", [projectA]),
        ).rejects.toThrow(/permission denied/i);
      });
    } finally {
      await pg.query(
        "delete from access.role_permissions where role_id=$1 and permission_key='projects.write'",
        [role],
      );
    }
  });
  it("keeps edit receipts actor-scoped, authorized and immutable", async () => {
    await pg.query("insert into access.role_permissions values($1,'projects.write'),($2,'projects.write')", [
      role,
      admin,
    ]);
    const operation = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    try {
      await runtime(async () => {
        await pg.query(
          "insert into studio.project_edit_receipts(organization_id,user_id,operation_id,project_id,request_hash,result) values($1,'staff',$2,$3,'hash','{}')",
          [orgA, operation, projectA],
        );
        expect((await pg.query("select operation_id from studio.project_edit_receipts")).rows).toHaveLength(
          1,
        );
        await pg.exec("COMMIT; BEGIN");
      });
      await runtime(
        async () =>
          expect((await pg.query("select * from studio.project_edit_receipts")).rows).toHaveLength(0),
        manager,
        "manager",
      );
      await runtime(
        async () =>
          expect((await pg.query("select * from studio.project_edit_receipts")).rows).toHaveLength(0),
        "",
      );
      await runtime(async () => {
        await expect(
          pg.query("update studio.project_edit_receipts set request_hash='changed'"),
        ).rejects.toThrow(/permission denied/i);
      });
      await runtime(async () => {
        await expect(pg.query("delete from studio.project_edit_receipts")).rejects.toThrow(
          /permission denied/i,
        );
      });
      await runtime(async () => {
        await expect(
          pg.query(
            "insert into studio.project_edit_receipts(organization_id,user_id,operation_id,project_id,request_hash,result) values($1,'manager',gen_random_uuid(),$2,'hash','{}')",
            [orgA, projectA],
          ),
        ).rejects.toThrow(/row.level security/i);
      });
      await expect(pg.exec("delete from studio.project_edit_receipts")).rejects.toThrow(/immutable/i);
    } finally {
      await pg.query(
        "delete from access.role_permissions where role_id in ($1,$2) and permission_key='projects.write'",
        [role, admin],
      );
    }
  });
  it("suspended membership loses access even with otherwise valid context", async () => {
    await pg.query("update core.memberships set status='suspended' where id=$1", [member]);
    await runtime(async () =>
      expect((await pg.query("select id from studio.projects")).rows).toHaveLength(0),
    );
  });
});
