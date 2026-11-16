import { randomUUID } from "node:crypto";
import { eq } from "drizzle-orm";
import { afterEach, describe, expect, it } from "vitest";
import { addMember, bootstrapCompany, makeTestApp, type TestApp } from "./harness.js";
import { memberships, projects, projectTasks, taskMutationReceipts } from "../src/db/schema/index.js";
import { seedOrganization } from "../src/organization.js";

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});
async function setup() {
  t = await makeTestApp();
  const c = await bootstrapCompany(t);
  const [project] = await t.db
    .insert(projects)
    .values({ organizationId: c.organizationId, name: "Tasks", createdBy: c.userId })
    .returning();
  const pm = await addMember(t, c.session, "manager@tasks.example", c.roleId["Project Manager"]!);
  const [member] = await t.db.select().from(memberships).where(eq(memberships.authUserId, pm.userId));
  return { ...c, project: project!, pm, member: member! };
}
const fields = () => ({
  title: "Review drawings",
  description: "Check current revision",
  dueAt: "2026-11-20T00:00:00.000Z",
  status: "open",
  priority: "high",
});
const createInput = () => ({ ...fields(), operationId: randomUUID(), id: randomUUID() });
const editInput = (expectedVersion = 1) => ({
  ...fields(),
  operationId: randomUUID(),
  expectedVersion,
  status: "in_progress",
});
const create = (project: string, headers: Record<string, string>, payload: object) =>
  t!.app.inject({ method: "POST", url: `/v1/projects/${project}/tasks`, headers, payload });
const edit = (project: string, id: string, headers: Record<string, string>, payload: object) =>
  t!.app.inject({ method: "PATCH", url: `/v1/projects/${project}/tasks/${id}`, headers, payload });
const assign = (c: Awaited<ReturnType<typeof setup>>, remove = false) =>
  t!.app.inject({
    method: remove ? "DELETE" : "PUT",
    url: `/v1/projects/${c.project.id}/assignments/${c.member.id}`,
    headers: c.session.headers,
  });
describe("online task mutations", () => {
  it("creates once on concurrent identical retries and commits exactly one receipt/audit", async () => {
    const c = await setup(),
      input = createInput();
    const [first, retry] = await Promise.all([
      create(c.project.id, c.session.headers, input),
      create(c.project.id, c.session.headers, input),
    ]);
    expect(first.statusCode).toBe(200);
    expect(retry.json()).toEqual(first.json());
    expect(first.json().task).toMatchObject({
      ...fields(),
      id: input.id,
      projectId: c.project.id,
      version: 1,
    });
    expect(await t!.db.select().from(projectTasks)).toHaveLength(1);
    expect(await t!.db.select().from(taskMutationReceipts)).toHaveLength(1);
    expect(
      (await t!.pg.query("select count(*)::int n from audit.events where action='task.created'")).rows,
    ).toEqual([{ n: 1 }]);
  });
  it("edits once, persists conflicts without writing, and explicitly rebases on a fresh operation", async () => {
    const c = await setup(),
      task = createInput();
    await create(c.project.id, c.session.headers, task);
    const input = editInput();
    const saved = await edit(c.project.id, task.id, c.session.headers, input);
    expect(saved.statusCode).toBe(200);
    expect(saved.json().task).toMatchObject({ status: "in_progress", version: 2 });
    expect((await edit(c.project.id, task.id, c.session.headers, input)).json()).toEqual(saved.json());
    const stale = { ...editInput(), title: "My draft" };
    const conflict = await edit(c.project.id, task.id, c.session.headers, stale);
    expect(conflict.statusCode).toBe(409);
    expect(conflict.json().conflict).toMatchObject({
      outcome: "conflict",
      expectedVersion: 1,
      submitted: { title: "My draft" },
      current: { version: 2, status: "in_progress" },
    });
    expect(
      (await edit(c.project.id, task.id, c.session.headers, { ...editInput(2), title: "Newer", dueAt: null }))
        .statusCode,
    ).toBe(200);
    expect((await edit(c.project.id, task.id, c.session.headers, stale)).json().conflict).toEqual(
      conflict.json().conflict,
    );
    const rebased = await edit(c.project.id, task.id, c.session.headers, {
      ...stale,
      operationId: randomUUID(),
      expectedVersion: 3,
    });
    expect(rebased.json().task).toMatchObject({ title: "My draft", version: 4 });
    expect(
      (await t!.pg.query("select count(*)::int n from audit.events where action='task.updated'")).rows,
    ).toEqual([{ n: 3 }]);
  });
  it("rejects operation reuse across changed fields, task ids and create/edit kinds without side effects", async () => {
    const c = await setup(),
      task = createInput();
    await create(c.project.id, c.session.headers, task);
    for (const body of [
      { ...task, title: "Other" },
      { ...task, id: randomUUID() },
    ]) {
      expect((await create(c.project.id, c.session.headers, body)).json().error.code).toBe(
        "OPERATION_ID_REUSED",
      );
    }
    expect(
      (
        await edit(c.project.id, task.id, c.session.headers, {
          ...editInput(),
          operationId: task.operationId,
        })
      ).json().error.code,
    ).toBe("OPERATION_ID_REUSED");
    expect(await t!.db.select().from(projectTasks)).toHaveLength(1);
    expect((await t!.db.select().from(projectTasks))[0]!.version).toBe(1);
    expect(
      (await create(c.project.id, c.session.headers, { ...task, operationId: randomUUID() })).json().error
        .code,
    ).toBe("TASK_ID_IN_USE");
  });
  it("requires assigned write scope before create, edit or receipt replay; Staff stays read-only", async () => {
    const c = await setup(),
      task = createInput();
    expect((await create(c.project.id, c.pm.session.headers, task)).statusCode).toBe(404);
    await assign(c);
    expect((await create(c.project.id, c.pm.session.headers, task)).statusCode).toBe(200);
    const change = editInput();
    expect((await edit(c.project.id, task.id, c.pm.session.headers, change)).statusCode).toBe(200);
    await assign(c, true);
    expect((await create(c.project.id, c.pm.session.headers, task)).statusCode).toBe(404);
    expect((await edit(c.project.id, task.id, c.pm.session.headers, change)).statusCode).toBe(404);
    const staff = await addMember(t!, c.session, "staff@tasks.example", c.roleId.Staff!);
    expect((await create(c.project.id, staff.session.headers, createInput())).statusCode).toBe(403);
    expect((await edit(c.project.id, task.id, staff.session.headers, editInput())).statusCode).toBe(403);
  });
  it("refuses cross-company/project mutations, deleted tasks and suspended sessions", async () => {
    const c = await setup(),
      task = createInput();
    await create(c.project.id, c.session.headers, task);
    const foreign = await seedOrganization(t!.db, { name: "Foreign", slug: "foreign-tasks" });
    const [project] = await t!.db
      .insert(projects)
      .values({ organizationId: foreign.organizationId, name: "Foreign", createdBy: "other" })
      .returning();
    expect((await create(project!.id, c.session.headers, createInput())).statusCode).toBe(404);
    const [other] = await t!.db
      .insert(projects)
      .values({ organizationId: c.organizationId, name: "Other project", createdBy: c.userId })
      .returning();
    expect((await edit(other!.id, task.id, c.session.headers, editInput())).statusCode).toBe(404);
    await t!.db.update(projectTasks).set({ deletedAt: new Date() }).where(eq(projectTasks.id, task.id));
    expect((await edit(c.project.id, task.id, c.session.headers, editInput())).statusCode).toBe(404);
    await assign(c);
    await t!.db.update(memberships).set({ status: "suspended" }).where(eq(memberships.id, c.member.id));
    expect((await create(c.project.id, c.pm.session.headers, createInput())).json().error.code).toBe(
      "MEMBERSHIP_SUSPENDED",
    );
  });
  it("validates bounds and editable fields without creating records", async () => {
    const c = await setup();
    for (const extra of [
      { title: " " },
      { title: "x".repeat(241) },
      { description: "x".repeat(10001) },
      { status: "unknown" },
      { priority: "critical" },
      { dueAt: "not-a-date" },
      { dueAt: "0000-01-01T00:00:00Z" },
      { organizationId: randomUUID() },
      { deletedAt: null },
    ]) {
      expect((await create(c.project.id, c.session.headers, { ...createInput(), ...extra })).statusCode).toBe(
        400,
      );
    }
    const task = createInput();
    await create(c.project.id, c.session.headers, task);
    for (const extra of [
      { expectedVersion: 0 },
      { expectedVersion: 2147483647 },
      { projectId: randomUUID() },
    ]) {
      expect(
        (await edit(c.project.id, task.id, c.session.headers, { ...editInput(), ...extra })).statusCode,
      ).toBe(400);
    }
    expect(await t!.db.select().from(projectTasks)).toHaveLength(1);
  });
});
