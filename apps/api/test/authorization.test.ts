import { randomUUID } from "node:crypto";
import { eq } from "drizzle-orm";
import { afterEach, describe, expect, it } from "vitest";
import { bootstrapCompany, addMember, enableTwoFactor, makeTestApp, ADMIN, type TestApp } from "./harness.js";
import { memberships, projects } from "../src/db/schema/index.js";
import { seedOrganization } from "../src/organization.js";

let t: TestApp | undefined;
afterEach(async () => {
  await t?.close();
  t = undefined;
});
async function setup(mfa = false) {
  t = await makeTestApp({ REQUIRE_ADMIN_MFA: String(mfa) });
  return bootstrapCompany(t);
}
async function member(c: Awaited<ReturnType<typeof setup>>, role = "Staff") {
  const person = await addMember(t!, c.session, `${randomUUID()}@demo.example`, c.roleId[role]!);
  const [m] = await t!.db.select().from(memberships).where(eq(memberships.authUserId, person.userId));
  return { ...person, membershipId: m!.id };
}
async function project(c: Awaited<ReturnType<typeof setup>>, name: string) {
  return (
    await t!.db
      .insert(projects)
      .values({ organizationId: c.organizationId, name, code: name, createdBy: c.userId })
      .returning()
  )[0]!;
}
const roleBody = {
  name: "Field reader",
  scope: "assigned",
  permissions: ["organization.read", "projects.read"],
};
const call = (
  method: "GET" | "POST" | "PUT" | "PATCH" | "DELETE",
  url: string,
  headers: Record<string, string>,
  payload?: unknown,
) => t!.app.inject({ method, url, headers, ...(payload ? { payload } : {}) });

describe("permission and role administration", () => {
  it("does not widen assigned project rights through organization-wide role-management authority", async () => {
    const c = await setup();
    const delegate = await member(c);
    const target = await member(c);
    const manager = (
      await call("POST", "/v1/roles", c.session.headers, {
        name: "Limited role manager",
        scope: "organization",
        permissions: ["organization.read", "roles.manage", "roles.read", "organization.members.manage"],
      })
    ).json();
    const reader = (await call("POST", "/v1/roles", c.session.headers, roleBody)).json();
    expect(
      (
        await call("PUT", `/v1/members/${delegate.membershipId}/roles`, c.session.headers, {
          roleIds: [manager.id, reader.id],
        })
      ).statusCode,
    ).toBe(204);
    expect(
      (
        await call("POST", "/v1/roles", delegate.session.headers, {
          ...roleBody,
          name: "Too broad",
          scope: "organization",
        })
      ).statusCode,
    ).toBe(403);
    expect(
      (
        await call("PUT", `/v1/members/${target.membershipId}/roles`, delegate.session.headers, {
          roleIds: [c.roleId.Accounts],
        })
      ).statusCode,
    ).toBe(403);
    expect(
      (
        await call("POST", "/v1/invitations", delegate.session.headers, {
          email: "broad@demo.example",
          roleId: c.roleId.Accounts,
        })
      ).statusCode,
    ).toBe(403);
    expect(
      (
        await call("POST", "/v1/roles", delegate.session.headers, {
          ...roleBody,
          name: "Allowed assigned reader",
        })
      ).statusCode,
    ).toBe(201);
  });
  it("has an authenticated catalogue and scope-aware system templates", async () => {
    const c = await setup();
    const staff = await member(c);
    expect((await t!.app.inject("/v1/permissions")).statusCode).toBe(401);
    expect(
      (await call("GET", "/v1/permissions", staff.session.headers)).json().map((p: { key: string }) => p.key),
    ).toContain("projects.read");
    expect((await call("GET", "/v1/roles", staff.session.headers)).statusCode).toBe(403);
    const list = await call("GET", "/v1/roles", c.session.headers);
    expect(list.statusCode).toBe(200);
    expect(list.json()).toHaveLength(11);
    expect(list.json().find((r: { name: string }) => r.name === "Staff").scope).toBe("assigned");
    expect(list.json().find((r: { name: string }) => r.name === "Principal").scope).toBe("organization");
  });
  it("creates custom roles but rejects unknown permissions, unsafe scopes and duplicate names", async () => {
    const c = await setup();
    expect((await call("POST", "/v1/roles", c.session.headers, roleBody)).statusCode).toBe(201);
    expect((await call("POST", "/v1/roles", c.session.headers, roleBody)).statusCode).toBe(409);
    expect(
      (
        await call("POST", "/v1/roles", c.session.headers, {
          ...roleBody,
          name: "Unknown",
          permissions: ["private.root"],
        })
      ).json().error.code,
    ).toBe("UNKNOWN_PERMISSION");
    expect(
      (
        await call("POST", "/v1/roles", c.session.headers, {
          ...roleBody,
          name: "Unsafe",
          permissions: ["roles.manage"],
        })
      ).json().error.code,
    ).toBe("INVALID_SCOPE");
    expect(
      (
        await call("POST", "/v1/roles", c.session.headers, {
          ...roleBody,
          name: "Assign only",
          permissions: ["projects.assign"],
        })
      ).json().error.code,
    ).toBe("INVALID_PERMISSIONS");
  });
  it("system templates are immutable and role changes take effect for existing sessions", async () => {
    const c = await setup();
    const staff = await member(c);
    expect(
      (await call("PATCH", `/v1/roles/${c.roleId.Staff}`, c.session.headers, roleBody)).json().error.code,
    ).toBe("SYSTEM_ROLE");
    const custom = (await call("POST", "/v1/roles", c.session.headers, roleBody)).json();
    expect(
      (
        await call("PUT", `/v1/members/${staff.membershipId}/roles`, c.session.headers, {
          roleIds: [custom.id],
        })
      ).statusCode,
    ).toBe(204);
    expect((await call("GET", "/v1/projects", staff.session.headers)).statusCode).toBe(200);
    expect(
      (
        await call("PATCH", `/v1/roles/${custom.id}`, c.session.headers, {
          ...roleBody,
          permissions: ["organization.read"],
        })
      ).statusCode,
    ).toBe(200);
    expect((await call("GET", "/v1/projects", staff.session.headers)).statusCode).toBe(403);
  });
  it("prevents owner promotion by an Administrator and protects the final active Owner", async () => {
    const c = await setup();
    const admin = await member(c, "Administrator");
    const staff = await member(c);
    expect(
      (
        await call("PUT", `/v1/members/${staff.membershipId}/roles`, admin.session.headers, {
          roleIds: [c.roleId.Owner],
        })
      ).statusCode,
    ).toBe(403);
    const [owner] = await t!.db.select().from(memberships).where(eq(memberships.authUserId, c.userId));
    expect(
      (
        await call("PUT", `/v1/members/${owner!.id}/roles`, admin.session.headers, {
          roleIds: [c.roleId.Staff],
        })
      ).statusCode,
    ).toBe(403);
    const last = await call("PUT", `/v1/members/${owner!.id}/roles`, c.session.headers, {
      roleIds: [c.roleId.Staff],
    });
    expect(last.statusCode).toBe(409);
    expect(last.json().error.code).toBe("LAST_OWNER");
    const second = await member(c, "Owner");
    expect(
      (
        await call("PUT", `/v1/members/${owner!.id}/roles`, second.session.headers, {
          roleIds: [c.roleId.Staff],
        })
      ).statusCode,
    ).toBe(204);
    expect((await call("GET", "/v1/roles", c.session.headers)).statusCode).toBe(403);
  });
  it("invitation delegation cannot grant permissions the inviter lacks", async () => {
    const c = await setup();
    const delegated = await member(c);
    const limited = (
      await call("POST", "/v1/roles", c.session.headers, {
        name: "Invite only",
        scope: "organization",
        permissions: ["organization.read", "organization.members.manage"],
      })
    ).json();
    expect(
      (
        await call("PUT", `/v1/members/${delegated.membershipId}/roles`, c.session.headers, {
          roleIds: [limited.id],
        })
      ).statusCode,
    ).toBe(204);
    expect(
      (
        await call("POST", "/v1/invitations", delegated.session.headers, {
          email: "new@demo.example",
          roleId: c.roleId.Viewer,
        })
      ).statusCode,
    ).toBe(403);
  });
  it("rejects foreign organization roles and members without exposing them", async () => {
    const c = await setup();
    const staff = await member(c);
    const other = await seedOrganization(t!.db, { name: "Other", slug: "other" });
    const [foreign] = await t!.db
      .insert(memberships)
      .values({ organizationId: other.organizationId, authUserId: "foreign", status: "active" })
      .returning();
    expect(
      (await call("PATCH", `/v1/roles/${other.roleIds.Staff}`, c.session.headers, roleBody)).statusCode,
    ).toBe(404);
    expect(
      (
        await call("PUT", `/v1/members/${staff.membershipId}/roles`, c.session.headers, {
          roleIds: [other.roleIds.Staff],
        })
      ).statusCode,
    ).toBe(404);
    expect(
      (
        await call("PUT", `/v1/members/${foreign!.id}/roles`, c.session.headers, {
          roleIds: [c.roleId.Staff],
        })
      ).statusCode,
    ).toBe(404);
  });
  it("role management requires administrator MFA", async () => {
    const c = await setup(true);
    expect((await call("POST", "/v1/roles", c.session.headers, roleBody)).json().error.code).toBe(
      "MFA_REQUIRED",
    );
    const verified = await enableTwoFactor(t!.app, c.session, ADMIN.password);
    expect((await call("POST", "/v1/roles", verified.session.headers, roleBody)).statusCode).toBe(201);
  });
});

describe("scoped project API", () => {
  it("only returns assigned projects to field staff, and removal takes effect immediately", async () => {
    const c = await setup();
    const staff = await member(c);
    const a = await project(c, "A");
    await project(c, "B");
    expect((await call("GET", "/v1/projects", c.session.headers)).json().projects).toHaveLength(2);
    expect((await call("GET", "/v1/projects", staff.session.headers)).json().projects).toHaveLength(0);
    expect(
      (await call("PUT", `/v1/projects/${a.id}/assignments/${staff.membershipId}`, staff.session.headers))
        .statusCode,
    ).toBe(403);
    expect(
      (await call("PUT", `/v1/projects/${a.id}/assignments/${staff.membershipId}`, c.session.headers))
        .statusCode,
    ).toBe(204);
    expect(
      (await call("GET", "/v1/projects", staff.session.headers))
        .json()
        .projects.map((p: { id: string }) => p.id),
    ).toEqual([a.id]);
    expect(
      (await call("DELETE", `/v1/projects/${a.id}/assignments/${staff.membershipId}`, c.session.headers))
        .statusCode,
    ).toBe(204);
    expect((await call("GET", `/v1/projects/${a.id}`, staff.session.headers)).statusCode).toBe(404);
  });
  it("Project Managers can assign their projects but cannot reach unassigned projects", async () => {
    const c = await setup();
    const manager = await member(c, "Project Manager");
    const viewer = await member(c, "Viewer");
    const a = await project(c, "A");
    const b = await project(c, "B");
    expect(
      (await call("PUT", `/v1/projects/${a.id}/assignments/${manager.membershipId}`, c.session.headers))
        .statusCode,
    ).toBe(204);
    expect(
      (await call("PUT", `/v1/projects/${b.id}/assignments/${viewer.membershipId}`, manager.session.headers))
        .statusCode,
    ).toBe(404);
    expect(
      (await call("PUT", `/v1/projects/${a.id}/assignments/${viewer.membershipId}`, manager.session.headers))
        .statusCode,
    ).toBe(204);
    expect((await call("GET", `/v1/projects/${a.id}`, viewer.session.headers)).statusCode).toBe(200);
  });
  it("foreign project and member ids are refused and reads are bounded", async () => {
    const c = await setup();
    const a = await project(c, "A");
    await project(c, "B");
    const other = await seedOrganization(t!.db, { name: "Other", slug: "other" });
    const [foreignProject] = await t!.db
      .insert(projects)
      .values({ organizationId: other.organizationId, name: "Foreign", createdBy: "foreign" })
      .returning();
    const [foreignMember] = await t!.db
      .insert(memberships)
      .values({ organizationId: other.organizationId, authUserId: "foreign", status: "active" })
      .returning();
    expect((await call("GET", `/v1/projects/${foreignProject!.id}`, c.session.headers)).statusCode).toBe(404);
    expect(
      (await call("PUT", `/v1/projects/${a.id}/assignments/${foreignMember!.id}`, c.session.headers))
        .statusCode,
    ).toBe(404);
    const page = (await call("GET", "/v1/projects?limit=1", c.session.headers)).json();
    expect(page.projects).toHaveLength(1);
    expect(page.nextCursor).toBeTruthy();
    const next = (
      await call("GET", `/v1/projects?limit=1&after=${page.nextCursor}`, c.session.headers)
    ).json();
    expect(next.projects).toHaveLength(1);
    expect(next.projects[0].id).not.toBe(page.projects[0].id);
    expect((await call("GET", "/v1/projects?limit=101", c.session.headers)).statusCode).toBe(400);
  });
});
