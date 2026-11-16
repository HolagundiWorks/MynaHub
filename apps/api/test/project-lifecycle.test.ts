import { randomUUID } from "node:crypto";
import { eq } from "drizzle-orm";
import { afterEach, describe, expect, it } from "vitest";
import { addMember, bootstrapCompany, makeTestApp, type TestApp } from "./harness.js";
import {
  memberships,
  projects,
  projectAssignments,
  projectTasks,
  projectLifecycleReceipts,
} from "../src/db/schema/index.js";
import { seedOrganization } from "../src/organization.js";

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});
async function setup() {
  t = await makeTestApp();
  return bootstrapCompany(t);
}
const input = () => ({
  id: randomUUID(),
  operationId: randomUUID(),
  name: "New company project",
  code: "NEW-" + randomUUID().slice(0, 8),
  status: "active",
});
const create = (headers: Record<string, string>, payload: object) =>
  t!.app.inject({ method: "POST", url: "/v1/projects", headers, payload });
const remove = (headers: Record<string, string>, id: string, payload: object) =>
  t!.app.inject({ method: "DELETE", url: "/v1/projects/" + id, headers, payload });
const deletion = (version = 1) => ({
  operationId: randomUUID(),
  expectedVersion: version,
  confirmation: "DELETE",
});

describe("project lifecycle", () => {
  it("creates exactly once on concurrent retry with server-owned company/creator and one audit event", async () => {
    const c = await setup(),
      body = input();
    const [first, retry] = await Promise.all([
      create(c.session.headers, body),
      create(c.session.headers, body),
    ]);
    expect(first.statusCode).toBe(200);
    expect(retry.json()).toEqual(first.json());
    expect(first.json()).toMatchObject({
      outcome: "created",
      project: { id: body.id, name: body.name, version: 1 },
    });
    const rows = await t!.db.select().from(projects);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({ organizationId: c.organizationId, createdBy: c.userId, deletedAt: null });
    expect(await t!.db.select().from(projectLifecycleReceipts)).toHaveLength(1);
    expect(
      (await t!.pg.query("select count(*)::int n from audit.events where action='project.created'")).rows,
    ).toEqual([{ n: 1 }]);
    expect((await create(c.session.headers, { ...body, name: "Changed" })).json().error.code).toBe(
      "OPERATION_ID_REUSED",
    );
    expect((await create(c.session.headers, { ...body, operationId: randomUUID() })).json().error.code).toBe(
      "PROJECT_ID_IN_USE",
    );
    expect((await create(c.session.headers, { ...input(), code: body.code })).json().error.code).toBe(
      "PROJECT_CODE_IN_USE",
    );
  });
  it("confirms the expected revision, tombstones once and preserves tasks/receipts while hiding live data", async () => {
    const c = await setup(),
      body = input();
    await create(c.session.headers, body);
    await t!.db
      .insert(projectTasks)
      .values({ organizationId: c.organizationId, projectId: body.id, title: "Retained task" });
    const edit = await t!.app.inject({
      method: "PATCH",
      url: "/v1/projects/" + body.id,
      headers: c.session.headers,
      payload: {
        operationId: randomUUID(),
        expectedVersion: 1,
        name: "Changed project",
        code: body.code,
        status: "on_hold",
      },
    });
    expect(edit.statusCode).toBe(200);
    const stale = deletion();
    const conflict = await remove(c.session.headers, body.id, stale);
    expect(conflict.statusCode).toBe(409);
    expect(conflict.json().conflict).toMatchObject({
      expectedVersion: 1,
      current: { version: 2, name: "Changed project" },
    });
    expect((await remove(c.session.headers, body.id, stale)).json().conflict).toEqual(
      conflict.json().conflict,
    );
    const valid = deletion(2),
      done = await remove(c.session.headers, body.id, valid);
    expect(done.statusCode).toBe(200);
    expect(done.json()).toMatchObject({ outcome: "deleted", id: body.id, version: 3 });
    expect((await remove(c.session.headers, body.id, valid)).json()).toEqual(done.json());
    expect((await remove(c.session.headers, body.id, deletion(3))).statusCode).toBe(404);
    expect((await create(c.session.headers, body)).statusCode).toBe(409);
    expect(
      (await t!.app.inject({ url: "/v1/projects/" + body.id, headers: c.session.headers })).statusCode,
    ).toBe(404);
    const live = (
      await t!.app.inject({
        url: "/v1/mobile/projects/live?taskProjectId=" + body.id,
        headers: c.session.headers,
      })
    ).json();
    expect(live.projects).toEqual([]);
    expect(live.taskPage).toBeNull();
    expect(await t!.db.select().from(projectTasks)).toHaveLength(1);
    expect((await t!.db.select().from(projects))[0]!.deletedAt).not.toBeNull();
    expect(
      (await t!.pg.query("select count(*)::int n from audit.events where action='project.deleted'")).rows,
    ).toEqual([{ n: 1 }]);
    await expect(
      t!.pg.query("update studio.project_lifecycle_receipts set request_hash='changed'"),
    ).rejects.toThrow(/immutable/i);
  });
  it("requires default creation/deletion permissions and supports assigned deletion without widening creation", async () => {
    const c = await setup(),
      body = input();
    await create(c.session.headers, body);
    const pm = await addMember(t!, c.session, "pm@lifecycle.example", c.roleId["Project Manager"]!);
    expect((await create(pm.session.headers, input())).statusCode).toBe(403);
    expect((await remove(pm.session.headers, body.id, deletion())).statusCode).toBe(403);
    const custom = await t!.app.inject({
      method: "POST",
      url: "/v1/roles",
      headers: c.session.headers,
      payload: {
        name: "Assigned project removal",
        scope: "assigned",
        permissions: ["organization.read", "projects.read", "projects.delete"],
      },
    });
    expect(custom.statusCode).toBe(201);
    const [member] = await t!.db.select().from(memberships).where(eq(memberships.authUserId, pm.userId));
    expect(
      (
        await t!.app.inject({
          method: "PUT",
          url: "/v1/members/" + member!.id + "/roles",
          headers: c.session.headers,
          payload: { roleIds: [custom.json().id] },
        })
      ).statusCode,
    ).toBe(204);
    expect((await remove(pm.session.headers, body.id, deletion())).statusCode).toBe(404);
    expect(
      (
        await t!.app.inject({
          method: "PUT",
          url: `/v1/projects/${body.id}/assignments/${member!.id}`,
          headers: c.session.headers,
        })
      ).statusCode,
    ).toBe(204);
    const action = deletion(),
      done = await remove(pm.session.headers, body.id, action);
    expect(done.statusCode).toBe(200);
    await t!.db.delete(projectAssignments).where(eq(projectAssignments.membershipId, member!.id));
    expect((await remove(pm.session.headers, body.id, action)).statusCode).toBe(404);
    expect((await create(pm.session.headers, input())).statusCode).toBe(403);
  });
  it("rejects cross-company deletion and stale or suspended sessions", async () => {
    const c = await setup(),
      body = input();
    await create(c.session.headers, body);
    const foreign = await seedOrganization(t!.db, { name: "Foreign", slug: "foreign-lifecycle" });
    const [row] = await t!.db
      .insert(projects)
      .values({ organizationId: foreign.organizationId, name: "Foreign", createdBy: "foreign" })
      .returning();
    expect((await remove(c.session.headers, row!.id, deletion())).statusCode).toBe(404);
    await t!.pg.query("update auth.session set created_at=now()-interval '1 day' where user_id=$1", [
      c.userId,
    ]);
    expect((await remove(c.session.headers, body.id, deletion())).json().error.code).toBe("REAUTH_REQUIRED");
    const [member] = await t!.db.select().from(memberships).where(eq(memberships.authUserId, c.userId));
    await t!.db.update(memberships).set({ status: "suspended" }).where(eq(memberships.id, member!.id));
    expect((await create(c.session.headers, input())).json().error.code).toBe("MEMBERSHIP_SUSPENDED");
  });
  it("validates all lifecycle inputs and forbids assigned project-creation roles", async () => {
    const c = await setup(),
      body = input();
    for (const extra of [
      { name: " " },
      { name: "x".repeat(241) },
      { code: "x".repeat(81) },
      { status: "invalid" },
      { organizationId: randomUUID() },
      { createdBy: "other" },
      { deletedAt: null },
    ]) {
      expect((await create(c.session.headers, { ...body, ...extra })).statusCode).toBe(400);
    }
    await create(c.session.headers, body);
    for (const extra of [
      { confirmation: "delete" },
      { confirmation: "" },
      { expectedVersion: 0 },
      { expectedVersion: 2147483647 },
      { deletedAt: "now" },
    ]) {
      expect((await remove(c.session.headers, body.id, { ...deletion(), ...extra })).statusCode).toBe(400);
    }
    expect(
      (
        await t!.app.inject({
          method: "POST",
          url: "/v1/roles",
          headers: c.session.headers,
          payload: {
            name: "Invalid creator",
            scope: "assigned",
            permissions: ["projects.read", "projects.create"],
          },
        })
      ).json().error.code,
    ).toBe("INVALID_SCOPE");
    expect(
      (
        await t!.app.inject({
          method: "POST",
          url: "/v1/roles",
          headers: c.session.headers,
          payload: { name: "Invalid remover", scope: "organization", permissions: ["projects.delete"] },
        })
      ).json().error.code,
    ).toBe("INVALID_PERMISSIONS");
  });
  it("removes deleted projects and selected tasks from a held live snapshot", async () => {
    const c = await setup(),
      body = input();
    await create(c.session.headers, body);
    const url = "/v1/mobile/projects/live?taskProjectId=" + body.id;
    const first = (await t!.app.inject({ url, headers: c.session.headers })).json();
    const waiting = t!.app.inject({ url: url + "&since=" + first.revision, headers: c.session.headers });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect((await remove(c.session.headers, body.id, deletion())).statusCode).toBe(200);
    const next = (await waiting).json();
    expect(next.projects).toEqual([]);
    expect(next.taskPage).toBeNull();
  });
});
