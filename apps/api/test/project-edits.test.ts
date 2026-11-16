import { randomUUID } from "node:crypto";
import { eq } from "drizzle-orm";
import { afterEach, describe, expect, it } from "vitest";
import { addMember, bootstrapCompany, makeTestApp, type TestApp } from "./harness.js";
import { memberships, projects, projectEditReceipts } from "../src/db/schema/index.js";
import { seedOrganization } from "../src/organization.js";

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});
async function setup() {
  t = await makeTestApp();
  const company = await bootstrapCompany(t);
  const [project] = await t.db
    .insert(projects)
    .values({
      organizationId: company.organizationId,
      name: "Villa",
      code: "V-01",
      createdBy: company.userId,
    })
    .returning();
  return { ...company, project: project! };
}
const edit = (id: string, headers: Record<string, string>, payload: object) =>
  t!.app.inject({ method: "PATCH", url: "/v1/projects/" + id, headers, payload });
const input = () => ({
  operationId: randomUUID(),
  expectedVersion: 1,
  name: "Updated villa",
  code: "V-01",
  status: "on_hold",
});
async function manager(c: Awaited<ReturnType<typeof setup>>, role = "Project Manager") {
  const person = await addMember(t!, c.session, randomUUID() + "@demo.example", c.roleId[role]!);
  const [membership] = await t!.db
    .select()
    .from(memberships)
    .where(eq(memberships.authUserId, person.userId));
  return { ...person, membershipId: membership!.id };
}
async function assign(c: Awaited<ReturnType<typeof setup>>, membershipId: string, remove = false) {
  expect(
    (
      await t!.app.inject({
        method: remove ? "DELETE" : "PUT",
        url: `/v1/projects/${c.project.id}/assignments/${membershipId}`,
        headers: c.session.headers,
      })
    ).statusCode,
  ).toBe(204);
}

describe("versioned project edits", () => {
  it("commits update, receipt and one audit event atomically; identical retry does not increment twice", async () => {
    const c = await setup(),
      body = input();
    const first = await edit(c.project.id, c.session.headers, body);
    expect(first.statusCode).toBe(200);
    expect(first.json().project).toMatchObject({ name: body.name, status: "on_hold", version: 2 });
    expect((await edit(c.project.id, c.session.headers, body)).json()).toEqual(first.json());
    expect(await t!.db.select().from(projectEditReceipts)).toHaveLength(1);
    expect(
      (await t!.pg.query("select count(*)::int as n from audit.events where action='project.updated'")).rows,
    ).toEqual([{ n: 1 }]);
    expect((await t!.db.select().from(projects))[0]!.version).toBe(2);
  });
  it("returns both versions on conflict and replays the original conflict without applying it", async () => {
    const c = await setup();
    expect((await edit(c.project.id, c.session.headers, input())).statusCode).toBe(200);
    const stale = { ...input(), name: "My competing edit" };
    const conflict = await edit(c.project.id, c.session.headers, stale);
    expect(conflict.statusCode).toBe(409);
    expect(conflict.json().error.code).toBe("VERSION_CONFLICT");
    expect(conflict.json().conflict).toMatchObject({
      outcome: "conflict",
      expectedVersion: 1,
      submitted: { name: stale.name },
      current: { name: "Updated villa", version: 2 },
    });
    const newer = { ...input(), expectedVersion: 2, name: "Newer change" };
    expect((await edit(c.project.id, c.session.headers, newer)).statusCode).toBe(200);
    expect((await edit(c.project.id, c.session.headers, stale)).json().conflict).toEqual(
      conflict.json().conflict,
    );
    expect((await t!.db.select().from(projects))[0]!.version).toBe(3);
  });
  it("rejects changed payloads under a used operation id", async () => {
    const c = await setup(),
      body = input();
    await edit(c.project.id, c.session.headers, body);
    const changed = await edit(c.project.id, c.session.headers, { ...body, name: "Different" });
    expect(changed.statusCode).toBe(409);
    expect(changed.json().error.code).toBe("OPERATION_ID_REUSED");
    expect((await t!.db.select().from(projects))[0]!.version).toBe(2);
  });
  it("checks current assigned write authority before editing or replaying", async () => {
    const c = await setup(),
      pm = await manager(c),
      body = input();
    expect((await edit(c.project.id, pm.session.headers, body)).statusCode).toBe(404);
    await assign(c, pm.membershipId);
    expect((await edit(c.project.id, pm.session.headers, body)).statusCode).toBe(200);
    await assign(c, pm.membershipId, true);
    expect((await edit(c.project.id, pm.session.headers, body)).statusCode).toBe(404);
    const staff = await manager(c, "Staff");
    await assign(c, staff.membershipId);
    expect((await edit(c.project.id, staff.session.headers, input())).statusCode).toBe(403);
  });
  it("does not reveal another company's project or receipt", async () => {
    const c = await setup();
    const other = await seedOrganization(t!.db, {
      name: "Foreign",
      slug: "foreign",
    });
    const [foreign] = await t!.db
      .insert(projects)
      .values({ organizationId: other.organizationId, name: "Secret", createdBy: "other" })
      .returning();
    expect((await edit(foreign!.id, c.session.headers, input())).statusCode).toBe(404);
    expect(await t!.db.select().from(projectEditReceipts)).toHaveLength(0);
  });
  it("rolls back a duplicate code and refuses client-owned identity/version fields", async () => {
    const c = await setup();
    await t!.db
      .insert(projects)
      .values({ organizationId: c.organizationId, name: "Other", code: "USED", createdBy: c.userId });
    expect(
      (await edit(c.project.id, c.session.headers, { ...input(), code: "USED" })).json().error.code,
    ).toBe("PROJECT_CODE_IN_USE");
    expect((await t!.db.select().from(projects).where(eq(projects.id, c.project.id)))[0]!.version).toBe(1);
    expect(await t!.db.select().from(projectEditReceipts)).toHaveLength(0);
    for (const extra of [
      { organizationId: randomUUID() },
      { version: 99 },
      { deletedAt: new Date().toISOString() },
    ]) {
      expect((await edit(c.project.id, c.session.headers, { ...input(), ...extra })).statusCode).toBe(400);
    }
    expect((await edit(c.project.id, c.session.headers, { ...input(), name: " " })).statusCode).toBe(400);
    expect((await edit(c.project.id, c.session.headers, { ...input(), status: "unknown" })).statusCode).toBe(
      400,
    );
  });
  it("serializes competing versions and duplicate retries", async () => {
    const c = await setup(),
      body = input();
    const results = await Promise.all([
      edit(c.project.id, c.session.headers, body),
      edit(c.project.id, c.session.headers, body),
      edit(c.project.id, c.session.headers, input()),
    ]);
    expect(results.map((r) => r.statusCode).sort()).toEqual([200, 200, 409]);
    expect((await t!.db.select().from(projects))[0]!.version).toBe(2);
  });
  it("requires project read permission when delegating edit permission", async () => {
    const c = await setup();
    const result = await t!.app.inject({
      method: "POST",
      url: "/v1/roles",
      headers: c.session.headers,
      payload: { name: "Write only", scope: "assigned", permissions: ["projects.write"] },
    });
    expect(result.statusCode).toBe(400);
  });
});
