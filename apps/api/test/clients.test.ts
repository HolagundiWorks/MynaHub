import { randomUUID } from "node:crypto";
import { and, eq } from "drizzle-orm";
import { afterEach, describe, expect, it } from "vitest";
import { bootstrapCompany, addMember, makeTestApp, type TestApp } from "./harness.js";
import {
  clients,
  projects,
  memberships,
  projectAssignments,
  rolePermissions,
  membershipRoles,
  roles,
} from "../src/db/schema/index.js";
import { seedOrganization } from "../src/organization.js";
let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});
async function setup() {
  t = await makeTestApp();
  const c = await bootstrapCompany(t);
  const staff = await addMember(t, c.session, "client-reader@example.test", c.roleId.Staff!);
  const [member] = await t.db.select().from(memberships).where(eq(memberships.authUserId, staff.userId));
  const rows = await t.db
    .insert(clients)
    .values([
      { organizationId: c.organizationId, name: "Assigned client", email: "assigned@example.test" },
      { organizationId: c.organizationId, name: "Unassigned client" },
    ])
    .returning();
  const [project] = await t.db
    .insert(projects)
    .values({
      organizationId: c.organizationId,
      name: "Assigned project",
      createdBy: c.userId,
      clientId: rows[0]!.id,
    })
    .returning();
  await t.db
    .insert(projectAssignments)
    .values({ organizationId: c.organizationId, projectId: project!.id, membershipId: member!.id });
  return { ...c, staff, member: member!, clients: rows, project: project! };
}
const list = (headers: Record<string, string>, query = "") =>
  t!.app.inject({ url: "/v1/clients" + query, headers });
const live = (c: Awaited<ReturnType<typeof setup>>, since?: string) =>
  t!.app.inject({
    url: "/v1/mobile/projects/live?clients=true" + (since ? "&since=" + since : ""),
    headers: c.staff.session.headers,
  });
describe("scoped live client contacts", () => {
  it("requires authentication and returns only contacts linked to assigned readable projects", async () => {
    const c = await setup();
    expect((await t!.app.inject("/v1/clients")).statusCode).toBe(401);
    const result = await list(c.staff.session.headers);
    expect(result.statusCode).toBe(200);
    expect(result.json().clients.map((r: { id: string }) => r.id)).toEqual([c.clients[0]!.id]);
    expect(
      (await t!.app.inject({ url: "/v1/clients/" + c.clients[1]!.id, headers: c.staff.session.headers }))
        .statusCode,
    ).toBe(404);
    expect((await list(c.session.headers)).json().clients).toHaveLength(2);
  });
  it("keeps client scope per permission even with organization-wide project read from another role", async () => {
    const c = await setup();
    const [extra] = await t!.db
      .insert(roles)
      .values({
        organizationId: c.organizationId,
        name: "Organization project reader",
        scope: "organization",
      })
      .returning();
    await t!.db.insert(rolePermissions).values({ roleId: extra!.id, permissionKey: "projects.read" });
    await t!.db.insert(membershipRoles).values({ membershipId: c.member.id, roleId: extra!.id });
    const [other] = await t!.db
      .insert(projects)
      .values({
        organizationId: c.organizationId,
        name: "Unassigned but readable",
        createdBy: c.userId,
        clientId: c.clients[1]!.id,
      })
      .returning();
    expect(
      (await t!.app.inject({ url: "/v1/projects/" + other!.id, headers: c.staff.session.headers }))
        .statusCode,
    ).toBe(200);
    expect((await list(c.staff.session.headers)).json().clients).toHaveLength(1);
  });
  it("hides foreign clients and enforces same-company project/client links", async () => {
    const c = await setup(),
      foreign = await seedOrganization(t!.db, { name: "Foreign", slug: "foreign-clients" });
    const [row] = await t!.db
      .insert(clients)
      .values({ organizationId: foreign.organizationId, name: "Foreign contact" })
      .returning();
    expect(
      (await t!.app.inject({ url: "/v1/clients/" + row!.id, headers: c.session.headers })).statusCode,
    ).toBe(404);
    await expect(
      t!.db.insert(projects).values({
        organizationId: c.organizationId,
        name: "Invalid client link",
        createdBy: c.userId,
        clientId: row!.id,
      }),
    ).rejects.toThrow();
  });
  it("pages organization contacts at 25 rows with stable UUID boundaries", async () => {
    const c = await setup();
    await t!.db
      .insert(clients)
      .values(
        Array.from({ length: 25 }, (_, i) => ({ organizationId: c.organizationId, name: "Contact " + i })),
      );
    const first = (await list(c.session.headers)).json();
    expect(first.clients).toHaveLength(25);
    expect(first.nextCursor).not.toBeNull();
    const second = (await list(c.session.headers, "?after=" + first.nextCursor)).json();
    expect(second.clients).toHaveLength(2);
    expect(second.nextCursor).toBeNull();
    expect(
      first.clients
        .map((r: { id: string }) => r.id)
        .filter((id: string) => second.clients.some((r: { id: string }) => r.id === id)),
    ).toEqual([]);
    expect((await list(c.session.headers, "?after=invalid")).statusCode).toBe(400);
  });
  it("delivers contact changes live and removes scope after assignment loss and permission removal", async () => {
    const c = await setup(),
      first = (await live(c)).json();
    expect(first.clientPage.clients[0].name).toBe("Assigned client");
    const waiting = live(c, first.revision);
    await new Promise((resolve) => setTimeout(resolve, 50));
    await t!.db
      .update(clients)
      .set({ name: "Changed live", version: 2 })
      .where(eq(clients.id, c.clients[0]!.id));
    const changed = (await waiting).json();
    expect(changed.clientPage.clients[0]).toMatchObject({ name: "Changed live", version: 2 });
    await t!.db.delete(projectAssignments).where(eq(projectAssignments.membershipId, c.member.id));
    expect((await live(c, changed.revision)).json().clientPage.clients).toEqual([]);
    await t!.db
      .delete(rolePermissions)
      .where(
        and(eq(rolePermissions.roleId, c.roleId.Staff!), eq(rolePermissions.permissionKey, "clients.read")),
      );
    expect((await list(c.staff.session.headers)).statusCode).toBe(403);
    expect((await live(c)).json().clientPage).toBeNull();
  });
  it("removes client contacts when the linked project is deleted or the member is suspended", async () => {
    const c = await setup();
    await t!.db.update(projects).set({ deletedAt: new Date() }).where(eq(projects.id, c.project.id));
    expect((await list(c.staff.session.headers)).json().clients).toEqual([]);
    await t!.db.update(memberships).set({ status: "suspended" }).where(eq(memberships.id, c.member.id));
    expect((await live(c)).json().error.code).toBe("MEMBERSHIP_SUSPENDED");
  });
  it("rejects assigned client-read roles without project-read permission and enforces field bounds", async () => {
    const c = await setup();
    const result = await t!.app.inject({
      method: "POST",
      url: "/v1/roles",
      headers: c.session.headers,
      payload: { name: "Unsafe contact reader", scope: "assigned", permissions: ["clients.read"] },
    });
    expect(result.json().error.code).toBe("INVALID_PERMISSIONS");
    await expect(
      t!.db.insert(clients).values({ id: randomUUID(), organizationId: c.organizationId, name: " " }),
    ).rejects.toThrow();
    await expect(
      t!.db
        .insert(clients)
        .values({ organizationId: c.organizationId, name: "Valid", email: "x".repeat(255) }),
    ).rejects.toThrow();
  });
});
