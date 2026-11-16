import { randomUUID } from "node:crypto";
import { eq } from "drizzle-orm";
import { afterEach, describe, expect, it } from "vitest";
import { bootstrapCompany, makeTestApp, addMember, type TestApp } from "./harness.js";
import {
  projects,
  projectTasks,
  memberships,
  projectAssignments,
  taskAssignments,
  taskActionReceipts,
  rolePermissions,
} from "../src/db/schema/index.js";
let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});
async function setup() {
  t = await makeTestApp();
  const c = await bootstrapCompany(t);
  const [p] = await t.db
    .insert(projects)
    .values({ organizationId: c.organizationId, name: "Task actions", createdBy: c.userId })
    .returning();
  const [task] = await t.db
    .insert(projectTasks)
    .values({ organizationId: c.organizationId, projectId: p!.id, title: "Retained task" })
    .returning();
  const pm = await addMember(t, c.session, "pm-actions@example.test", c.roleId["Project Manager"]!);
  const staff = await addMember(t, c.session, "staff-actions@example.test", c.roleId.Staff!);
  const members = await t.db.select().from(memberships);
  const manager = members.find((m) => m.authUserId === pm.userId)!,
    worker = members.find((m) => m.authUserId === staff.userId)!;
  await t.db.insert(projectAssignments).values([
    { organizationId: c.organizationId, projectId: p!.id, membershipId: manager.id },
    { organizationId: c.organizationId, projectId: p!.id, membershipId: worker.id },
  ]);
  return { ...c, project: p!, task: task!, pm, staff, manager, worker };
}
const call = (
  c: Awaited<ReturnType<typeof setup>>,
  method: "PUT" | "DELETE" | "GET",
  extra: string,
  payload?: object,
  headers = c.pm.session.headers,
) =>
  t!.app.inject({
    method,
    url:
      "/v1/projects/" + c.project.id + (method === "GET" ? "/task-assignees" : "/tasks/" + c.task.id + extra),
    headers,
    payload,
  });
const assign = (ids: string[], version = 1) => ({
  membershipIds: ids,
  expectedVersion: version,
  operationId: randomUUID(),
});
describe("task assignees and confirmed deletion", () => {
  it("lists only eligible project members and assigns multiple once with immutable retries", async () => {
    const c = await setup();
    const candidates = await call(c, "GET", "");
    expect(candidates.statusCode).toBe(200);
    expect(candidates.json().members.map((m: { membershipId: string }) => m.membershipId)).toContain(
      c.worker.id,
    );
    const input = assign([c.worker.id, c.manager.id]);
    const [a, b] = await Promise.all([
      call(c, "PUT", "/assignees", input),
      call(c, "PUT", "/assignees", { ...input, membershipIds: [...input.membershipIds].reverse() }),
    ]);
    expect(a.statusCode).toBe(200);
    expect(b.json()).toEqual(a.json());
    expect(a.json().task.version).toBe(2);
    expect(a.json().task.assignees).toHaveLength(2);
    const live = await t!.app.inject({
      url: "/v1/mobile/projects/live?taskProjectId=" + c.project.id + "&assignees=true",
      headers: c.pm.session.headers,
    });
    expect(live.statusCode).toBe(200);
    expect(live.json().assigneePage.members.map((m: { membershipId: string }) => m.membershipId)).toContain(
      c.worker.id,
    );
    expect(live.json().taskPage.tasks[0].assignees).toHaveLength(2);

    expect(await t!.db.select().from(taskAssignments)).toHaveLength(2);
    expect(await t!.db.select().from(taskActionReceipts)).toHaveLength(1);
    expect((await call(c, "PUT", "/assignees", { ...input, membershipIds: [] })).json().error.code).toBe(
      "OPERATION_ID_REUSED",
    );
    await expect(
      t!.pg.query("update studio.task_action_receipts set request_hash=request_hash"),
    ).rejects.toThrow("immutable");
  });
  it("conflicts with concurrent task edits and deletion keeps tombstones and old assignee rows hidden", async () => {
    const c = await setup();
    const assigned = await call(c, "PUT", "/assignees", assign([c.worker.id]));
    expect(assigned.statusCode).toBe(200);
    const stale = { operationId: randomUUID(), expectedVersion: 1, confirmation: "DELETE" };
    const conflict = await call(c, "DELETE", "", stale);
    expect(conflict.statusCode).toBe(409);
    expect(conflict.json().conflict.current.assignees).toHaveLength(1);
    expect((await call(c, "DELETE", "", stale)).json().conflict).toEqual(conflict.json().conflict);
    const remove = { ...stale, operationId: randomUUID(), expectedVersion: 2 };
    const deleted = await call(c, "DELETE", "", remove);
    expect(deleted.statusCode).toBe(200);
    expect(deleted.json().version).toBe(3);
    expect((await call(c, "DELETE", "", remove)).json()).toEqual(deleted.json());
    const snapshot = await t!.app.inject({
      url: "/v1/mobile/projects/live?taskProjectId=" + c.project.id,
      headers: c.pm.session.headers,
    });
    expect(snapshot.json().taskPage.tasks).toEqual([]);
    expect((await t!.db.select().from(projectTasks))[0]?.deletedAt).not.toBeNull();
    expect(await t!.db.select().from(taskAssignments)).toHaveLength(1);
    expect((await call(c, "PUT", "/assignees", assign([], 3))).statusCode).toBe(404);
  });
  it("refuses unknown suspended unassigned and foreign memberships without changing the task", async () => {
    const c = await setup();
    const other = await addMember(t!, c.session, "unassigned-actions@example.test", c.roleId.Staff!);
    const [unassigned] = await t!.db
      .select()
      .from(memberships)
      .where(eq(memberships.authUserId, other.userId));
    for (const id of [randomUUID(), unassigned!.id])
      expect((await call(c, "PUT", "/assignees", assign([id]))).json().error.code).toBe("INVALID_ASSIGNEES");
    await t!.db.update(memberships).set({ status: "suspended" }).where(eq(memberships.id, c.worker.id));
    expect((await call(c, "PUT", "/assignees", assign([c.worker.id]))).statusCode).toBe(400);
    expect((await t!.db.select().from(projectTasks))[0]?.version).toBe(1);
    expect((await call(c, "PUT", "/assignees", assign([c.manager.id, c.manager.id]))).statusCode).toBe(400);
  });
  it("revokes action and receipt access when assignment or permission is lost and hides suspended assignees", async () => {
    const c = await setup();
    const input = assign([c.worker.id]);
    await call(c, "PUT", "/assignees", input);
    await t!.db.update(memberships).set({ status: "suspended" }).where(eq(memberships.id, c.worker.id));
    const snapshot = await t!.app.inject({
      url: "/v1/mobile/projects/live?taskProjectId=" + c.project.id,
      headers: c.pm.session.headers,
    });
    expect(snapshot.json().taskPage.tasks[0].assignees).toEqual([]);
    await t!.db.delete(projectAssignments).where(eq(projectAssignments.membershipId, c.manager.id));
    expect((await call(c, "PUT", "/assignees", input)).statusCode).toBe(404);
    expect((await call(c, "GET", "")).statusCode).toBe(404);
    expect(
      (
        await call(
          c,
          "DELETE",
          "",
          { operationId: randomUUID(), expectedVersion: 2, confirmation: "DELETE" },
          c.staff.session.headers,
        )
      ).statusCode,
    ).toBe(403);
  });
  it("requires explicit confirmation and refuses task actions through a different project", async () => {
    const c = await setup();
    expect(
      (await call(c, "DELETE", "", { operationId: randomUUID(), expectedVersion: 1, confirmation: "delete" }))
        .statusCode,
    ).toBe(400);
    const [other] = await t!.db
      .insert(projects)
      .values({ organizationId: c.organizationId, name: "Other", createdBy: c.userId })
      .returning();
    expect(
      (
        await t!.app.inject({
          method: "DELETE",
          url: "/v1/projects/" + other!.id + "/tasks/" + c.task.id,
          headers: c.session.headers,
          payload: { operationId: randomUUID(), expectedVersion: 1, confirmation: "DELETE" },
        })
      ).statusCode,
    ).toBe(404);
    await t!.db.delete(rolePermissions).where(eq(rolePermissions.permissionKey, "tasks.delete"));
    expect(
      (await call(c, "DELETE", "", { operationId: randomUUID(), expectedVersion: 1, confirmation: "DELETE" }))
        .statusCode,
    ).toBe(403);
  });
});
