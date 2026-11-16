import { randomUUID } from "node:crypto";
import { eq } from "drizzle-orm";
import { afterEach, describe, expect, it } from "vitest";
import { bootstrapCompany, makeTestApp, addMember, type TestApp } from "./harness.js";
import {
  projects,
  siteRecords,
  siteMutationReceipts,
  memberships,
  projectAssignments,
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
    .values({ organizationId: c.organizationId, name: "Site project", createdBy: c.userId })
    .returning();
  const supervisor = await addMember(
    t,
    c.session,
    "supervisor-sites@example.test",
    c.roleId["Site Supervisor"]!,
  );
  const [member] = await t.db.select().from(memberships).where(eq(memberships.authUserId, supervisor.userId));
  await t.db
    .insert(projectAssignments)
    .values({ organizationId: c.organizationId, projectId: p!.id, membershipId: member!.id });
  return { ...c, project: p!, supervisor, member: member! };
}
const fields = () => ({
  kind: "snag",
  recordDate: "2026-11-16",
  title: "Check lintel finish",
  details: "Fictional site observation",
  location: "North entry",
  assignedTo: "Demo contractor",
  dueDate: "2026-11-20",
  status: "open",
  resolvedDate: null,
});
const create = () => ({ ...fields(), id: randomUUID(), operationId: randomUUID() });
const call = (
  c: Awaited<ReturnType<typeof setup>>,
  method: "POST" | "PATCH" | "DELETE" | "GET",
  payload?: object,
  id?: string,
  headers = c.supervisor.session.headers,
) =>
  t!.app.inject({
    method,
    url: "/v1/projects/" + c.project.id + "/site-records" + (id ? "/" + id : ""),
    headers,
    payload,
  });
describe("live project site records", () => {
  it("creates once on retry with server-owned company project and creator", async () => {
    const c = await setup(),
      body = create();
    const [a, b] = await Promise.all([call(c, "POST", body), call(c, "POST", body)]);
    expect(a.statusCode).toBe(200);
    expect(b.json()).toEqual(a.json());
    expect(a.json().record).toMatchObject({ id: body.id, projectId: c.project.id, version: 1 });
    const rows = await t!.db.select().from(siteRecords);
    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({ organizationId: c.organizationId, createdBy: c.supervisor.userId });
    expect(await t!.db.select().from(siteMutationReceipts)).toHaveLength(1);
    expect((await call(c, "POST", { ...body, title: "Changed" })).json().error.code).toBe(
      "OPERATION_ID_REUSED",
    );
    expect((await call(c, "POST", { ...body, operationId: randomUUID() })).json().error.code).toBe(
      "SITE_RECORD_ID_IN_USE",
    );
  });
  it("edits with explicit version conflicts and live reads update while scoped to the project", async () => {
    const c = await setup(),
      body = create();
    await call(c, "POST", body);
    const live = () =>
      t!.app.inject({
        url: "/v1/mobile/projects/live?siteProjectId=" + c.project.id,
        headers: c.supervisor.session.headers,
      });
    const before = await live();
    expect(before.json().sitePage.records).toHaveLength(1);
    const update = {
      ...fields(),
      title: "Finish accepted",
      status: "resolved",
      resolvedDate: "2026-11-17",
      operationId: randomUUID(),
      expectedVersion: 1,
    };
    const edited = await call(c, "PATCH", update, body.id);
    expect(edited.statusCode).toBe(200);
    expect(edited.json().record.version).toBe(2);
    expect((await call(c, "PATCH", update, body.id)).json()).toEqual(edited.json());
    const stale = { ...update, operationId: randomUUID() };
    const conflict = await call(c, "PATCH", stale, body.id);
    expect(conflict.statusCode).toBe(409);
    expect(conflict.json().conflict.current.status).toBe("resolved");
    expect((await call(c, "PATCH", stale, body.id)).json().conflict).toEqual(conflict.json().conflict);
    const after = await live();
    expect(after.json().revision).not.toBe(before.json().revision);
    expect(after.json().sitePage.records[0].resolvedDate).toBe("2026-11-17");
  });
  it("retains tombstones with confirmed manager deletion and blocks creation replay after removal", async () => {
    const c = await setup(),
      body = create();
    await call(c, "POST", body);
    const remove = { operationId: randomUUID(), expectedVersion: 1, confirmation: "DELETE" };
    expect((await call(c, "DELETE", remove, body.id)).statusCode).toBe(403);
    const deleted = await call(c, "DELETE", remove, body.id, c.session.headers);
    expect(deleted.statusCode).toBe(200);
    expect(deleted.json().version).toBe(2);
    expect((await call(c, "DELETE", remove, body.id, c.session.headers)).json()).toEqual(deleted.json());
    expect((await call(c, "GET")).json().records).toEqual([]);
    expect((await call(c, "POST", body)).statusCode).toBe(404);
    expect((await t!.db.select().from(siteRecords))[0]?.deletedAt).not.toBeNull();
    await expect(
      t!.pg.query("update studio.site_mutation_receipts set request_hash=request_hash"),
    ).rejects.toThrow("immutable");
  });
  it("denies unassigned, suspended and wrong-project access including receipt replays", async () => {
    const c = await setup(),
      body = create();
    await call(c, "POST", body);
    const [other] = await t!.db
      .insert(projects)
      .values({ organizationId: c.organizationId, name: "Other", createdBy: c.userId })
      .returning();
    expect(
      (
        await t!.app.inject({
          method: "PATCH",
          url: "/v1/projects/" + other!.id + "/site-records/" + body.id,
          headers: c.session.headers,
          payload: { ...fields(), expectedVersion: 1, operationId: randomUUID() },
        })
      ).statusCode,
    ).toBe(404);
    await t!.db.delete(projectAssignments).where(eq(projectAssignments.membershipId, c.member.id));
    expect((await call(c, "GET")).statusCode).toBe(404);
    expect((await call(c, "POST", body)).statusCode).toBe(404);
    await t!.db.update(memberships).set({ status: "suspended" }).where(eq(memberships.id, c.member.id));
    expect((await call(c, "GET")).statusCode).toBe(403);
  });
  it("validates calendar dates resolution status field lengths and deletion confirmation", async () => {
    const c = await setup();
    for (const extra of [
      { recordDate: "2026-02-30" },
      { recordDate: "0000-01-01" },
      { title: " " },
      { kind: "daily_report" },
      { details: "x".repeat(10001) },
      { status: "resolved", resolvedDate: null },
      { resolvedDate: "2026-11-17" },
      { status: "closed", resolvedDate: "2026-11-15" },
      { organizationId: c.organizationId },
    ])
      expect((await call(c, "POST", { ...create(), ...extra })).statusCode).toBe(400);
    const body = create();
    await call(c, "POST", body);
    expect(
      (
        await call(
          c,
          "DELETE",
          { expectedVersion: 1, operationId: randomUUID(), confirmation: "delete" },
          body.id,
          c.session.headers,
        )
      ).statusCode,
    ).toBe(400);
    await t!.db.delete(rolePermissions).where(eq(rolePermissions.permissionKey, "sites.write"));
    expect((await call(c, "POST", body)).statusCode).toBe(403);
  });
  it("bounds independent live pages to 25 records and hides records with deleted projects", async () => {
    const c = await setup();
    await t!.db.insert(siteRecords).values(
      Array.from({ length: 26 }, (_, i) => ({
        organizationId: c.organizationId,
        projectId: c.project.id,
        kind: "inspection" as const,
        recordDate: "2026-11-16",
        title: "Inspection " + i,
        createdBy: c.userId,
      })),
    );
    const first = await call(c, "GET");
    expect(first.json().records).toHaveLength(25);
    const cursor = first.json().nextCursor;
    const second = await t!.app.inject({
      url: "/v1/projects/" + c.project.id + "/site-records?after=" + cursor,
      headers: c.supervisor.session.headers,
    });
    expect(second.json().records).toHaveLength(1);
    expect(second.json().nextCursor).toBeNull();
    await t!.db.update(projects).set({ deletedAt: new Date() }).where(eq(projects.id, c.project.id));
    expect((await call(c, "GET")).statusCode).toBe(404);
    const live = await t!.app.inject({
      url: "/v1/mobile/projects/live?siteProjectId=" + c.project.id,
      headers: c.supervisor.session.headers,
    });
    expect(live.json().sitePage).toBeNull();
  });
});
