import { and, eq } from "drizzle-orm";
import { afterEach, describe, expect, it } from "vitest";
import { addMember, bootstrapCompany, makeTestApp, type TestApp } from "./harness.js";
import { memberships, projects, projectTasks, rolePermissions } from "../src/db/schema/index.js";
import { seedOrganization } from "../src/organization.js";

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});
async function setup() {
  t = await makeTestApp();
  const owner = await bootstrapCompany(t);
  const staff = await addMember(t, owner.session, "live@demo.example", owner.roleId.Staff!);
  const [membership] = await t.db.select().from(memberships).where(eq(memberships.authUserId, staff.userId));
  const [project] = await t.db
    .insert(projects)
    .values({ organizationId: owner.organizationId, name: "Assigned", createdBy: owner.userId })
    .returning();
  expect(
    (
      await t.app.inject({
        method: "PUT",
        url: `/v1/projects/${project!.id}/assignments/${membership!.id}`,
        headers: owner.session.headers,
      })
    ).statusCode,
  ).toBe(204);
  return { ...owner, staff, membership: membership!, project: project! };
}
const live = (headers: Record<string, string>, since?: string) =>
  t!.app.inject({
    method: "GET",
    url: "/v1/mobile/projects/live" + (since ? "?since=" + since : ""),
    headers,
  });
describe("live authorized project pages", () => {
  it("delivers bounded selected-project tasks, changes, and assignment removal", async () => {
    const c = await setup();
    const [other] = await t!.db
      .insert(projects)
      .values({ organizationId: c.organizationId, name: "Unassigned", createdBy: c.userId })
      .returning();
    await t!.db.insert(projectTasks).values([
      ...Array.from({ length: 26 }, (_, i) => ({
        organizationId: c.organizationId,
        projectId: c.project.id,
        title: "Task " + i,
      })),
      { organizationId: c.organizationId, projectId: other!.id, title: "Hidden task" },
      {
        organizationId: c.organizationId,
        projectId: c.project.id,
        title: "Deleted",
        deletedAt: new Date(),
      },
    ]);
    const get = (extra = "") =>
      t!.app.inject({
        url: `/v1/mobile/projects/live?taskProjectId=${c.project.id}${extra}`,
        headers: c.staff.session.headers,
      });
    const first = (await get()).json();
    expect(first.taskPage.tasks).toHaveLength(25);
    expect(first.taskPage.tasks.every((row: { projectId: string }) => row.projectId === c.project.id)).toBe(
      true,
    );
    const second = (await get("&taskAfter=" + first.taskPage.nextCursor)).json();
    expect(second.taskPage.tasks).toHaveLength(1);
    expect(second.taskPage.nextCursor).toBeNull();
    expect(first.taskPage.tasks.map((r: { id: string }) => r.id)).not.toContain(second.taskPage.tasks[0].id);
    const hidden = await t!.app.inject({
      url: `/v1/mobile/projects/live?taskProjectId=${other!.id}`,
      headers: c.staff.session.headers,
    });
    expect(hidden.json().taskPage).toBeNull();
    const waiting = get("&since=" + first.revision);
    await new Promise((resolve) => setTimeout(resolve, 50));
    await t!.db
      .update(projectTasks)
      .set({ title: "Updated live", version: 2 })
      .where(eq(projectTasks.id, first.taskPage.tasks[0].id));
    expect((await waiting).json().taskPage.tasks[0]).toMatchObject({ title: "Updated live", version: 2 });
    await t!.app.inject({
      method: "DELETE",
      url: `/v1/projects/${c.project.id}/assignments/${c.membership.id}`,
      headers: c.session.headers,
    });
    expect((await get()).json().taskPage).toBeNull();
  });
  it("requires an authenticated member and validates cursor inputs", async () => {
    t = await makeTestApp();
    expect((await t.app.inject("/v1/mobile/projects/live")).statusCode).toBe(401);
    expect((await t.app.inject("/v1/mobile/projects/live?since=bad")).statusCode).toBe(400);
  });
  it("returns only current scope and a stable server revision", async () => {
    const c = await setup();
    const other = await seedOrganization(t!.db, { name: "Foreign", slug: "foreign" });
    await t!.db
      .insert(projects)
      .values({ organizationId: other.organizationId, name: "Never shared", createdBy: "other" });
    const first = await live(c.staff.session.headers);
    expect(first.statusCode).toBe(200);
    expect(first.json().projects.map((row: { id: string }) => row.id)).toEqual([c.project.id]);
    expect(first.json().revision).toMatch(/^[a-f0-9]{64}$/);
    expect(first.json().permissions).toContain("projects.read");
    const started = Date.now();
    const heartbeat = await live(c.staff.session.headers, first.json().revision);
    expect(Date.now() - started).toBeGreaterThanOrEqual(200);
    expect(heartbeat.json()).toEqual(first.json());
  });
  it("holds a matching revision and returns the next committed project change", async () => {
    const c = await setup();
    const first = (await live(c.staff.session.headers)).json();
    const waiting = live(c.staff.session.headers, first.revision);
    await new Promise((resolve) => setTimeout(resolve, 50));
    await t!.db
      .update(projects)
      .set({ name: "Changed live", version: 2 })
      .where(eq(projects.id, c.project.id));
    const changed = await waiting;
    expect(changed.statusCode).toBe(200);
    expect(changed.json().revision).not.toBe(first.revision);
    expect(changed.json().projects[0]).toMatchObject({ name: "Changed live", version: 2 });
  });
  it("removes unassigned projects from the next live snapshot", async () => {
    const c = await setup();
    const first = (await live(c.staff.session.headers)).json();
    const waiting = live(c.staff.session.headers, first.revision);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(
      (
        await t!.app.inject({
          method: "DELETE",
          url: `/v1/projects/${c.project.id}/assignments/${c.membership.id}`,
          headers: c.session.headers,
        })
      ).statusCode,
    ).toBe(204);
    expect((await waiting).json().projects).toEqual([]);
  });
  it("reauthenticates a held request after membership suspension", async () => {
    const c = await setup();
    const first = (await live(c.staff.session.headers)).json();
    const waiting = live(c.staff.session.headers, first.revision);
    await new Promise((resolve) => setTimeout(resolve, 50));
    await t!.db.update(memberships).set({ status: "suspended" }).where(eq(memberships.id, c.membership.id));
    const denied = await waiting;
    expect(denied.statusCode).toBe(403);
    expect(denied.json().error.code).toBe("MEMBERSHIP_SUSPENDED");
    expect(denied.json()).not.toHaveProperty("projects");
  });
  it("sends empty project scope when read permission is removed and resumes after it is restored", async () => {
    const c = await setup();
    const first = (await live(c.staff.session.headers)).json();
    await t!.db
      .delete(rolePermissions)
      .where(
        and(eq(rolePermissions.roleId, c.roleId.Staff!), eq(rolePermissions.permissionKey, "projects.read")),
      );
    const denied = (await live(c.staff.session.headers, first.revision)).json();
    expect(denied.projects).toEqual([]);
    expect(denied.permissions).not.toContain("projects.read");
    await t!.db.insert(rolePermissions).values({ roleId: c.roleId.Staff!, permissionKey: "projects.read" });
    const restored = await live(c.staff.session.headers, denied.revision);
    expect(restored.json().projects.map((row: { id: string }) => row.id)).toEqual([c.project.id]);
  });
});
